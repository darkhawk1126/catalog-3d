using System.Collections.Concurrent;
using System.Threading.Channels;
using Catalog3d.Application.Abstractions;

namespace Catalog3d.Infrastructure.Rendering;

/// <summary>
/// In-process IRenderQueue backed by a bounded Channel. Suitable for a single-instance
/// homelab deployment where durability across restarts is not required.
///
/// The Channel decouples enqueue (upload path, synchronous) from processing (RenderWorker,
/// BackgroundService). Items that survive a restart are re-queued by the startup reconciler
/// (downstream agent responsibility — scan ModelFiles with RenderStatus.Pending on startup).
///
/// BoundedChannelFullMode.Wait: back-pressures the upload handler if the queue fills,
/// which is preferable to dropping jobs silently.
/// </summary>
internal sealed class InProcessRenderQueue : IRenderQueue
{
    // 256 slots — enough to absorb a burst of bulk uploads without materializing a large queue.
    private const int QueueCapacity = 256;

    private readonly Channel<RenderQueueItem> _channel = Channel.CreateBounded<RenderQueueItem>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,  // RenderWorker is the sole reader.
            SingleWriter = false  // Multiple upload handlers may enqueue concurrently.
        });

    // Lightweight in-memory state map. Jobs are ephemeral; this is not a durable store.
    private readonly ConcurrentDictionary<Guid, RenderJobState> _states = new();

    /// <summary>Exposes the read side to RenderWorker (internal to Infrastructure).</summary>
    internal ChannelReader<RenderQueueItem> Reader => _channel.Reader;

    /// <inheritdoc/>
    public async Task<Guid> EnqueueAsync(
        Guid modelFileId,
        CancellationToken cancellationToken = default)
    {
        var jobId = Guid.NewGuid();
        var item = new RenderQueueItem(jobId, modelFileId);

        _states[jobId] = RenderJobState.Queued;

        // WriteAsync honours BoundedChannelFullMode.Wait — back-pressures if full.
        await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);

        return jobId;
    }

    /// <inheritdoc/>
    public Task<RenderJobState> GetStateAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _states.TryGetValue(jobId, out var s) ? s : RenderJobState.Failed;
        return Task.FromResult(state);
    }

    /// <summary>
    /// Called by RenderWorker to record state transitions (Queued → Processing → Complete/Failed).
    /// Internal to Infrastructure — not part of the IRenderQueue contract.
    /// </summary>
    internal void SetState(Guid jobId, RenderJobState state) => _states[jobId] = state;
}

/// <summary>A single render job item flowing through the in-process channel.</summary>
internal readonly record struct RenderQueueItem(Guid JobId, Guid ModelFileId);
