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

    // -------------------------------------------------------------------------
    // SizeAsync — H7
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SizeAsync_ReturnsCorrectByteLength()
    {
        var payload = new byte[1337];
        Random.Shared.NextBytes(payload);

        using var stream = new MemoryStream(payload);
        var key = await _store.WriteAsync(stream, "blobs");

        var size = await _store.SizeAsync(key, "blobs");

        Assert.Equal(1337L, size);
    }

    [Fact]
    public async Task SizeAsync_ThrowsFileNotFound_WhenBlobAbsent()
    {
        // A valid-format key that was never written.
        var key = new string('a', 64);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _store.SizeAsync(key, "blobs"));
    }

    // -------------------------------------------------------------------------
    // BlobPath key validation — M13
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("../etc/passwd")]
    [InlineData("not-a-hash")]
    [InlineData("ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890")]  // uppercase
    [InlineData("abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890.exe")]
    public async Task ReadAsync_InvalidKey_ThrowsArgumentException(string badKey)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.ReadAsync(badKey, "blobs"));
    }

    [Theory]
    [InlineData("abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890")]        // 64-char hex
    [InlineData("abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890.png")]    // 64-char hex + .png
    public async Task BlobPath_ValidKeys_DoNotThrow(string validKey)
    {
        // ExistsAsync exercises BlobPath; a missing file is fine — we just need no ArgumentException.
        var ex = await Record.ExceptionAsync(() => _store.ExistsAsync(validKey, "blobs"));
        Assert.Null(ex);
    }

    // -------------------------------------------------------------------------
    // Concurrent identical-content writes — M9
    // -------------------------------------------------------------------------

    [Fact]
    public async Task WriteAsync_ConcurrentIdenticalContent_AllReturnSameKeyWithNoException()
    {
        const int concurrency = 20;
        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        var expectedKey = ToHexSha256(payload);

        var tasks = Enumerable.Range(0, concurrency).Select(_ =>
            _store.WriteAsync(new MemoryStream(payload), "blobs"));

        var keys = await Task.WhenAll(tasks);

        Assert.All(keys, k => Assert.Equal(expectedKey, k));
        // Exactly one file on disk (dedup).
        var blobPath = Path.Combine(_root, "blobs", expectedKey[..2], expectedKey);
        Assert.True(File.Exists(blobPath));
    }

    private static string ToHexSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
