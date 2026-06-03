namespace Catalog3d.Application.Abstractions;

/// <summary>
/// Content-addressed blob storage. Implementations handle disk layout; callers
/// address blobs exclusively by the SHA-256 hex key returned from Write.
/// </summary>
public interface IFileStore
{
    /// <summary>
    /// Write a stream to the store. Returns the content-addressed key (SHA-256 hex).
    /// The key is stable across identical content — deduplication is implicit.
    /// </summary>
    Task<string> WriteAsync(Stream content, string blobSubDirectory, CancellationToken cancellationToken = default);

    /// <summary>Opens a read stream for the blob identified by <paramref name="key"/>.</summary>
    Task<Stream> ReadAsync(string key, string blobSubDirectory, CancellationToken cancellationToken = default);

    /// <summary>Returns true if a blob with <paramref name="key"/> exists in <paramref name="blobSubDirectory"/>.</summary>
    Task<bool> ExistsAsync(string key, string blobSubDirectory, CancellationToken cancellationToken = default);

    /// <summary>Permanently removes a blob. No-op if the key does not exist.</summary>
    Task DeleteAsync(string key, string blobSubDirectory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the byte length of the stored blob identified by <paramref name="key"/>.
    /// Throws <see cref="FileNotFoundException"/> when the blob does not exist.
    /// </summary>
    Task<long> SizeAsync(string key, string blobSubDirectory, CancellationToken ct = default);
}
