using System.Security.Cryptography;
using System.Text;
using Catalog3d.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Catalog3d.Tests;

/// <summary>
/// Verifies the content-addressed round-trip contract for DiskFileStore:
/// WriteAsync returns the SHA-256 hex key, ExistsAsync finds it, ReadAsync
/// returns identical bytes, and DeleteAsync removes it.
/// </summary>
public sealed class DiskFileStoreTests : IDisposable
{
    private readonly string _root;
    private readonly DiskFileStore _store;

    public DiskFileStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"diskfilestore-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        var opts = Options.Create(new DiskFileStoreOptions { Root = _root });
        _store = new DiskFileStore(opts);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task WriteAsync_ReturnsCorrectSha256Key()
    {
        var payload = "hello catalog-3d"u8.ToArray();
        var expectedKey = ToHexSha256(payload);

        using var stream = new MemoryStream(payload);
        var key = await _store.WriteAsync(stream, "blobs");

        Assert.Equal(expectedKey, key);
    }

    [Fact]
    public async Task RoundTrip_WriteThenRead_ReturnsSameBytes()
    {
        var payload = Encoding.UTF8.GetBytes("STL content placeholder");

        using var writeStream = new MemoryStream(payload);
        var key = await _store.WriteAsync(writeStream, "blobs");

        await using var readStream = await _store.ReadAsync(key, "blobs");
        using var ms = new MemoryStream();
        await readStream.CopyToAsync(ms);

        Assert.Equal(payload, ms.ToArray());
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrueAfterWrite_FalseAfterDelete()
    {
        var payload = "exists-check"u8.ToArray();
        using var stream = new MemoryStream(payload);
        var key = await _store.WriteAsync(stream, "blobs");

        Assert.True(await _store.ExistsAsync(key, "blobs"));

        await _store.DeleteAsync(key, "blobs");

        Assert.False(await _store.ExistsAsync(key, "blobs"));
    }

    [Fact]
    public async Task WriteAsync_IsIdempotent_DuplicateContentDeduplicates()
    {
        var payload = "deduplicated content"u8.ToArray();

        using var s1 = new MemoryStream(payload);
        var key1 = await _store.WriteAsync(s1, "blobs");

        using var s2 = new MemoryStream(payload);
        var key2 = await _store.WriteAsync(s2, "blobs");

        // Same content must yield the same key and must not throw on the second write.
        Assert.Equal(key1, key2);
        Assert.True(await _store.ExistsAsync(key1, "blobs"));
    }

    [Fact]
    public async Task WriteAsync_UsesSubdirectorySharding_TwoCharPrefix()
    {
        var payload = "sharding-check"u8.ToArray();
        using var stream = new MemoryStream(payload);
        var key = await _store.WriteAsync(stream, "blobs");

        // The file must live at {root}/blobs/{key[..2]}/{key}
        var expectedPath = Path.Combine(_root, "blobs", key[..2], key);
        Assert.True(File.Exists(expectedPath), $"Expected blob at {expectedPath}");
    }

    [Fact]
    public async Task WriteAsync_DifferentSubDirectories_AreIndependent()
    {
        var payload = "same-bytes-different-subdir"u8.ToArray();

        using var s1 = new MemoryStream(payload);
        var blobKey = await _store.WriteAsync(s1, "blobs");

        using var s2 = new MemoryStream(payload);
        var thumbKey = await _store.WriteAsync(s2, "thumbs");

        // Same key, but independently stored in each subdirectory.
        Assert.Equal(blobKey, thumbKey);
        Assert.True(await _store.ExistsAsync(blobKey, "blobs"));
        Assert.True(await _store.ExistsAsync(thumbKey, "thumbs"));
    }

    private static string ToHexSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
