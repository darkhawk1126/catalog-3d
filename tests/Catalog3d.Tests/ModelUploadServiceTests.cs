using System.Buffers.Binary;
using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Entities;
using Catalog3d.Infrastructure.Persistence;
using Catalog3d.Infrastructure.Upload;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Catalog3d.Tests;

/// <summary>
/// Unit tests for ModelUploadService.
/// Encodes correct behavior (not implementation) for:
///   - Dup-slug → SlugConflict
///   - Empty slug → InvalidSlug
///   - Non-STL content → InvalidContent
///   - Oversize upload → TooLarge
///   - Size backfilled from IFileStore.SizeAsync
///   - Render job enqueued on success
/// </summary>
public sealed class ModelUploadServiceTests : IDisposable
{
    private static readonly Guid TestCollectionId = new("cc000000-0000-0000-0000-000000000001");

    private readonly string _storageRoot;
    private readonly FakeFileStore _fileStore;
    private readonly TrackingRenderQueue _renderQueue;
    private readonly CatalogDbContext _db;

    public ModelUploadServiceTests()
    {
        _storageRoot = Path.Combine(Path.GetTempPath(), $"upload-svc-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);

        _fileStore = new FakeFileStore(_storageRoot);
        _renderQueue = new TrackingRenderQueue();

        var dbOptions = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseInMemoryDatabase($"upload-svc-{Guid.NewGuid():N}")
            .Options;
        _db = new CatalogDbContext(dbOptions);

        // Seed the collection.
        _db.Collections.Add(new Collection
        {
            Id = TestCollectionId,
            Slug = "test-collection",
            Name = "Test",
            Description = "",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_storageRoot))
        {
            try { Directory.Delete(_storageRoot, recursive: true); }
            catch (IOException) { }
        }
    }

    // -------------------------------------------------------------------------
    // Success path
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_ValidStl_ReturnsSuccess()
    {
        var svc = BuildService();
        var request = MakeRequest(BuildValidStl(triangles: 1), "widget.stl");

        var result = await svc.UploadAsync(request);

        Assert.IsType<ModelUploadResult.Success>(result);
    }

    [Fact]
    public async Task Upload_ValidStl_SizeBackfilled()
    {
        var svc = BuildService();
        var stlBytes = BuildValidStl(triangles: 2);
        var request = MakeRequest(stlBytes, "model.stl");

        var result = await svc.UploadAsync(request);

        var success = Assert.IsType<ModelUploadResult.Success>(result);
        Assert.Equal(stlBytes.Length, success.File.Size);
    }

    [Fact]
    public async Task Upload_ValidStl_RenderEnqueued()
    {
        var svc = BuildService();
        var request = MakeRequest(BuildValidStl(triangles: 1), "cube.stl");

        await svc.UploadAsync(request);

        Assert.Equal(1, _renderQueue.EnqueueCallCount);
    }

    [Fact]
    public async Task Upload_ValidStl_MimeTypeIsServerDerived()
    {
        var svc = BuildService();
        // Client claims it is "application/octet-stream" — the service must use "model/stl".
        var request = MakeRequest(BuildValidStl(triangles: 1), "part.stl");

        var result = await svc.UploadAsync(request);

        var success = Assert.IsType<ModelUploadResult.Success>(result);
        Assert.Equal("model/stl", success.File.MimeType);
    }

    // -------------------------------------------------------------------------
    // Dup-slug → SlugConflict
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_DuplicateSlug_ReturnsSlugConflict()
    {
        var svc = BuildService();

        // First upload succeeds.
        var first = MakeRequest(BuildValidStl(triangles: 1), "widget.stl");
        var r1 = await svc.UploadAsync(first);
        Assert.IsType<ModelUploadResult.Success>(r1);

        // Second upload with same filename (same slug) must conflict.
        // Different bytes so the blob key is different (forces a new blob write attempt).
        var second = MakeRequest(BuildValidStl(triangles: 2), "widget.stl");
        var r2 = await svc.UploadAsync(second);

        var conflict = Assert.IsType<ModelUploadResult.SlugConflict>(r2);
        Assert.Equal("widget", conflict.Slug);
    }

    [Fact]
    public async Task Upload_DuplicateSlug_OrphanedBlobDeleted()
    {
        var svc = BuildService();

        var first = MakeRequest(BuildValidStl(triangles: 1), "widget.stl");
        await svc.UploadAsync(first);

        int blobsBeforeConflict = _fileStore.WrittenKeys.Count;

        var second = MakeRequest(BuildValidStl(triangles: 2), "widget.stl");
        await svc.UploadAsync(second);

        // The second blob was written then deleted; net count stays at 1.
        Assert.Equal(1, _fileStore.ExistingKeyCount);
        // Delete was called for the orphan.
        Assert.True(_fileStore.DeleteCallCount >= 1);
        _ = blobsBeforeConflict; // referenced to suppress unused warning
    }

    // -------------------------------------------------------------------------
    // Empty slug → InvalidSlug
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_EmptySlug_ReturnsInvalidSlug()
    {
        var svc = BuildService();
        // A filename consisting entirely of dashes produces an empty slug after trimming.
        var request = MakeRequest(BuildValidStl(triangles: 1), "----.stl");

        var result = await svc.UploadAsync(request);

        Assert.IsType<ModelUploadResult.InvalidSlug>(result);
    }

    [Fact]
    public async Task Upload_EmptySlug_NoBlobWritten()
    {
        var svc = BuildService();
        var request = MakeRequest(BuildValidStl(triangles: 1), "----.stl");

        await svc.UploadAsync(request);

        // Empty slug is rejected before touching storage.
        Assert.Empty(_fileStore.WrittenKeys);
    }

    // -------------------------------------------------------------------------
    // Non-STL content → InvalidContent
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_NonStl_TooShort_ReturnsInvalidContent()
    {
        var svc = BuildService();
        // File shorter than 84 bytes.
        var request = MakeRequest(new byte[10], "thing.stl");

        var result = await svc.UploadAsync(request);

        Assert.IsType<ModelUploadResult.InvalidContent>(result);
    }

    [Fact]
    public async Task Upload_NonStl_InconsistentLength_ReturnsInvalidContent()
    {
        var svc = BuildService();
        // Declares 5 triangles but only has bytes for 1.
        var bytes = BuildStlWithWrongCount(declaredTriangles: 5, actualTriangles: 1);
        var request = MakeRequest(bytes, "bad.stl");

        var result = await svc.UploadAsync(request);

        Assert.IsType<ModelUploadResult.InvalidContent>(result);
    }

    [Fact]
    public async Task Upload_NonStl_OrphanBlobDeleted()
    {
        var svc = BuildService();
        var request = MakeRequest(new byte[10], "thing.stl");

        await svc.UploadAsync(request);

        Assert.Equal(0, _fileStore.ExistingKeyCount);
    }

    // -------------------------------------------------------------------------
    // Oversize → TooLarge
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_ExceedsSizeLimit_ReturnsTooLarge()
    {
        // Configure a 100-byte ceiling.
        var svc = BuildService(maxBytes: 100);
        // Payload is 84+50 = 134 bytes.
        var request = MakeRequest(BuildValidStl(triangles: 1), "big.stl");

        var result = await svc.UploadAsync(request);

        var tooLarge = Assert.IsType<ModelUploadResult.TooLarge>(result);
        Assert.Equal(100L, tooLarge.LimitBytes);
    }

    [Fact]
    public async Task Upload_ExceedsSizeLimit_NoBlobPersisted()
    {
        var svc = BuildService(maxBytes: 100);
        var request = MakeRequest(BuildValidStl(triangles: 1), "big.stl");

        await svc.UploadAsync(request);

        Assert.Equal(0, _fileStore.ExistingKeyCount);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private ModelUploadService BuildService(long maxBytes = 10 * 1024 * 1024)
    {
        var opts = Options.Create(new ModelUploadOptions { MaxSizeBytes = maxBytes });
        return new ModelUploadService(
            _fileStore,
            _renderQueue,
            _db,
            opts,
            NullLogger<ModelUploadService>.Instance);
    }

    private static ModelUploadRequest MakeRequest(
        byte[] bytes,
        string fileName,
        string? slug = null)
        => new()
        {
            CollectionId = TestCollectionId,
            OwnerId = "user:tester",
            FileStream = new MemoryStream(bytes),
            FileName = fileName,
            SlugOverride = slug,
        };

    // Builds a valid binary STL with <n> triangles (all-zero geometry).
    private static byte[] BuildValidStl(int triangles)
    {
        int size = 84 + triangles * 50;
        var buf = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(80, 4), (uint)triangles);
        return buf;
    }

    // Builds an STL that declares one count but has bytes for another.
    private static byte[] BuildStlWithWrongCount(int declaredTriangles, int actualTriangles)
    {
        int size = 84 + actualTriangles * 50;
        var buf = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(80, 4), (uint)declaredTriangles);
        return buf;
    }
}

// =============================================================================
// FakeFileStore — real filesystem writes for integration fidelity, with tracking
// =============================================================================

internal sealed class FakeFileStore : IFileStore
{
    private readonly string _root;
    private readonly HashSet<string> _existingKeys = [];
    private int _deleteCallCount;

    public FakeFileStore(string root) => _root = root;

    public IReadOnlySet<string> WrittenKeys => _existingKeys;
    public int ExistingKeyCount => _existingKeys.Count;
    public int DeleteCallCount => _deleteCallCount;

    public async Task<string> WriteAsync(Stream content, string sub, CancellationToken ct = default)
    {
        // Compute SHA-256 while buffering to a temp file.
        var dir = Path.Combine(_root, sub);
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".tmp-{Guid.NewGuid():N}");

        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);

        byte[] buf = new byte[4096];
        await using (var tmp_fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
        {
            int n;
            while ((n = await content.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                sha.AppendData(buf, 0, n);
                await tmp_fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }

        var key = Convert.ToHexString(sha.GetCurrentHash()).ToLowerInvariant();
        var final = Path.Combine(dir, key);
        File.Move(tmp, final, overwrite: true);
        _existingKeys.Add(key);
        return key;
    }

    public Task<Stream> ReadAsync(string key, string sub, CancellationToken ct = default)
    {
        var path = Path.Combine(_root, sub, key);
        Stream s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(s);
    }

    public Task<bool> ExistsAsync(string key, string sub, CancellationToken ct = default)
        => Task.FromResult(File.Exists(Path.Combine(_root, sub, key)));

    public Task DeleteAsync(string key, string sub, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _deleteCallCount);
        var path = Path.Combine(_root, sub, key);
        _existingKeys.Remove(key);
        try { File.Delete(path); } catch (IOException) { }
        return Task.CompletedTask;
    }

    public Task<long> SizeAsync(string key, string sub, CancellationToken ct = default)
    {
        var info = new FileInfo(Path.Combine(_root, sub, key));
        if (!info.Exists) throw new FileNotFoundException(key);
        return Task.FromResult(info.Length);
    }
}
