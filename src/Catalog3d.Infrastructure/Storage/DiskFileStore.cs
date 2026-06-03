using System.Buffers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Catalog3d.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Catalog3d.Infrastructure.Storage;

/// <summary>
/// Content-addressed on-disk IFileStore. Files are stored at:
///   {root}/{blobSubDirectory}/{key[..2]}/{key}
/// where key is the lowercase SHA-256 hex of the content.
/// Writes are atomic: data lands in a temp file first, then is moved into place.
/// Identical content deduplicates automatically — WriteAsync is idempotent.
/// </summary>
internal sealed class DiskFileStore : IFileStore
{
    // 80 KiB — large enough to keep copy loops tight without pressuring LOH (85 KB threshold).
    private const int CopyBufferSize = 80 * 1024;

    // Accepts 64-char hex keys (STL blobs) or 64-char hex + ".png" (thumbnail blobs).
    private static readonly Regex ValidKeyPattern =
        new(@"^[0-9a-f]{64}(\.png)?$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private readonly DiskFileStoreOptions _options;

    public DiskFileStore(IOptions<DiskFileStoreOptions> options)
    {
        _options = options.Value;
    }

    /// <inheritdoc/>
    public async Task<string> WriteAsync(
        Stream content,
        string blobSubDirectory,
        CancellationToken cancellationToken = default)
    {
        string dir = SubDir(blobSubDirectory);
        string tmpPath = Path.Combine(dir, $".tmp-{Guid.NewGuid():N}");

        // Ensure subdirectory exists before writing.
        Directory.CreateDirectory(dir);

        string sha256Hex;
        try
        {
            sha256Hex = await HashAndCopyToTempAsync(content, tmpPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            TryDeleteFile(tmpPath);
            throw;
        }

        string finalPath = BlobPath(blobSubDirectory, sha256Hex);
        string finalDir = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(finalDir);

        // Content-addressed: overwrite is safe because any two files with the same SHA-256
        // key are byte-identical. Using overwrite:true closes the TOCTOU window where a
        // concurrent write of the same content could leave the temp file on disk if the
        // exists-check races with another writer's Move.
        File.Move(tmpPath, finalPath, overwrite: true);

        return sha256Hex;
    }

    /// <inheritdoc/>
    public Task<Stream> ReadAsync(
        string key,
        string blobSubDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = BlobPath(blobSubDirectory, key);

        if (!File.Exists(path))
            throw new FileNotFoundException($"Blob not found: {key}", path);

        // FileOptions.Asynchronous enables true async I/O on the kernel level.
        Stream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return Task.FromResult(stream);
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(
        string key,
        string blobSubDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(BlobPath(blobSubDirectory, key)));
    }

    /// <inheritdoc/>
    public Task DeleteAsync(
        string key,
        string blobSubDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryDeleteFile(BlobPath(blobSubDirectory, key));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<long> SizeAsync(
        string key,
        string blobSubDirectory,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string path = BlobPath(blobSubDirectory, key);
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException($"Blob not found: {key}", path);
        return Task.FromResult(info.Length);
    }

    // --- helpers ---

    private string SubDir(string blobSubDirectory) =>
        Path.Combine(_options.Root, blobSubDirectory);

    private string BlobPath(string blobSubDirectory, string key)
    {
        // Defense-in-depth: keys are always server-derived SHA-256 hashes, but validate
        // anyway so a bug or future code path cannot escape the storage root.
        if (!ValidKeyPattern.IsMatch(key))
            throw new ArgumentException(
                $"Invalid blob key '{key}': must match ^[0-9a-f]{{64}}(\\.png)?$.", nameof(key));

        // Two-char prefix shard mirrors git's object store: limits directory entry counts.
        string prefix = key[..2];
        return Path.Combine(_options.Root, blobSubDirectory, prefix, key);
    }

    private static async Task<string> HashAndCopyToTempAsync(
        Stream source,
        string tmpPath,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using FileStream tmp = new(
                tmpPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous);

            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
                await tmp.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await tmp.FlushAsync(cancellationToken).ConfigureAwait(false);

            byte[] hashBytes = hasher.GetCurrentHash();
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
