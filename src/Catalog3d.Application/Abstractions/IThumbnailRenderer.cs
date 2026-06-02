namespace Catalog3d.Application.Abstractions;

/// <summary>
/// Asks the render sidecar to produce a thumbnail PNG for an STL blob.
///
/// Protocol (shared-volume + blob-key):
///   The app and sidecar share the blob volume at a common root. The app passes only
///   the blob key (SHA-256 hex); the sidecar resolves the STL path from the key using
///   the same layout convention as DiskFileStore:
///     read  : {volumeRoot}/blobs/{key[0..2]}/{key}
///     write : {volumeRoot}/thumbs/{key[0..2]}/{key}.png
///   The sidecar overwrites an existing thumbnail (idempotent). No STL bytes travel over HTTP.
///
/// Failure semantics:
///   Returns RenderResult.Failure on sidecar error (non-2xx HTTP, timeout, bad response).
///   The caller (RenderWorker) is responsible for updating ModelFile.RenderStatus and retrying.
/// </summary>
public interface IThumbnailRenderer
{
    /// <summary>
    /// Requests a thumbnail for the blob identified by <paramref name="blobKey"/>.
    /// The sidecar reads the STL from the shared volume and writes the PNG in-place.
    /// </summary>
    /// <param name="blobKey">SHA-256 hex key of the source STL blob.</param>
    /// <param name="cancellationToken">Propagates caller cancellation to the HTTP call.</param>
    Task<RenderResult> RenderAsync(string blobKey, CancellationToken cancellationToken = default);
}

/// <summary>Result of a thumbnail render request.</summary>
/// <param name="Success">True when the sidecar wrote a valid PNG to the thumbs path.</param>
/// <param name="ErrorMessage">Non-null on failure; suitable for logging and DB persistence.</param>
public readonly record struct RenderResult(bool Success, string? ErrorMessage)
{
    public static RenderResult Ok() => new(true, null);
    public static RenderResult Failure(string message) => new(false, message);
}
