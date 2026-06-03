namespace Catalog3d.Application.Abstractions;

/// <summary>
/// Enqueues render jobs dispatched to the stl-thumb sidecar. M1 stub — the sidecar
/// integration is wired in milestone 3.
/// </summary>
public interface IRenderQueue
{
    /// <summary>
    /// Enqueues a thumbnail render job for <paramref name="modelFileId"/>.
    /// Returns the job identifier assigned by the queue implementation.
    /// </summary>
    Task<Guid> EnqueueAsync(Guid modelFileId, CancellationToken cancellationToken = default);

    /// <summary>Returns the current render state for a previously enqueued job.</summary>
    Task<RenderJobState> GetStateAsync(Guid jobId, CancellationToken cancellationToken = default);
}

public enum RenderJobState
{
    Queued,
    Processing,
    Complete,
    Failed
}
