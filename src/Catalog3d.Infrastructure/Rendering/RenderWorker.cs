using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Entities;
using Catalog3d.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Catalog3d.Infrastructure.Rendering;

/// <summary>
/// Drains the IRenderQueue channel, calls the f3d sidecar for each job, and
/// updates the DB with Complete / Failed status. On success it also creates the
/// Thumbnail ModelFile record so the thumbnail endpoint can find it.
///
/// Startup reconciliation: jobs that were Pending when the process last died are
/// re-enqueued on start so they are not permanently lost.
/// </summary>
internal sealed class RenderWorker : BackgroundService
{
    private readonly InProcessRenderQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RenderWorker> _logger;

    public RenderWorker(
        InProcessRenderQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<RenderWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RenderWorker started; awaiting render jobs.");

        // Reconcile any Pending jobs that survived a prior restart before draining live work.
        await ReconcilePendingAsync(stoppingToken).ConfigureAwait(false);

        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            _queue.SetState(item.JobId, RenderJobState.Processing);
            await ProcessJobAsync(item, stoppingToken).ConfigureAwait(false);
        }

        _logger.LogInformation("RenderWorker stopping.");
    }

    // -------------------------------------------------------------------------
    // Re-enqueue any ModelFile rows left in Pending state from a prior run.
    // -------------------------------------------------------------------------
    private async Task ReconcilePendingAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

            // Re-enqueue both Pending (never started) and Processing (started but crashed).
            // On restart, files stuck in Processing indicate the prior process died mid-job.
            var pendingIds = await db.ModelFiles
                .AsNoTracking()
                .Where(f => f.Kind == ModelFileKind.Stl
                    && (f.RenderStatus == RenderStatus.Pending || f.RenderStatus == RenderStatus.Processing))
                .Select(f => f.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var id in pendingIds)
            {
                await _queue.EnqueueAsync(id, ct).ConfigureAwait(false);
                _logger.LogInformation("Reconciled pending render job for ModelFile {ModelFileId}.", id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-fatal: the app still starts; affected models remain Pending until
            // the next restart or a manual re-trigger.
            _logger.LogError(ex, "Startup reconciliation failed — some Pending jobs may not be re-queued.");
        }
    }

    // -------------------------------------------------------------------------
    // Execute a single render job end-to-end.
    // -------------------------------------------------------------------------
    private async Task ProcessJobAsync(RenderQueueItem item, CancellationToken ct)
    {
        _logger.LogInformation(
            "Processing render job {JobId} for ModelFile {ModelFileId}.",
            item.JobId, item.ModelFileId);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var renderer = scope.ServiceProvider.GetRequiredService<IThumbnailRenderer>();
            var fileStore = scope.ServiceProvider.GetRequiredService<IFileStore>();

            // Load the STL file record to get the blob key.
            var stlFile = await db.ModelFiles
                .Include(f => f.Model)
                .FirstOrDefaultAsync(f => f.Id == item.ModelFileId, ct)
                .ConfigureAwait(false);

            if (stlFile is null)
            {
                _logger.LogWarning(
                    "ModelFile {ModelFileId} not found; dropping job {JobId}.",
                    item.ModelFileId, item.JobId);
                _queue.SetState(item.JobId, RenderJobState.Failed);
                return;
            }

            // Mark as processing in DB so a restart knows this job was picked up.
            stlFile.RenderStatus = RenderStatus.Processing;
            stlFile.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // Parse STL metadata (triangle count + bounding box) in-process.
            // This is independent of the sidecar and runs first so metadata is available
            // regardless of whether the render succeeds or fails.
            var metadata = await ParseStlMetadataAsync(fileStore, stlFile.BlobKey, item, ct)
                .ConfigureAwait(false);

            var result = await renderer.RenderAsync(stlFile.BlobKey, ct).ConfigureAwait(false);

            if (result.Success)
            {
                await RecordSuccessAsync(db, stlFile, metadata, ct).ConfigureAwait(false);
                _queue.SetState(item.JobId, RenderJobState.Complete);
                _logger.LogInformation(
                    "Render complete for ModelFile {ModelFileId} (job {JobId}).",
                    item.ModelFileId, item.JobId);
            }
            else
            {
                await RecordFailureAsync(db, stlFile, metadata, ct).ConfigureAwait(false);
                _queue.SetState(item.JobId, RenderJobState.Failed);
                _logger.LogWarning(
                    "Render failed for ModelFile {ModelFileId} (job {JobId}): {Error}.",
                    item.ModelFileId, item.JobId, result.ErrorMessage);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Unhandled error in render job {JobId} for ModelFile {ModelFileId}.",
                item.JobId, item.ModelFileId);
            _queue.SetState(item.JobId, RenderJobState.Failed);

            // Best-effort: mark the STL file as Failed so the UI can surface the error.
            try
            {
                await using var fallback = _scopeFactory.CreateAsyncScope();
                var db = fallback.ServiceProvider.GetRequiredService<CatalogDbContext>();
                var f = await db.ModelFiles.FindAsync(new object[] { item.ModelFileId }, ct).ConfigureAwait(false);
                if (f is not null)
                {
                    f.RenderStatus = RenderStatus.Failed;
                    f.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception inner) when (inner is not OperationCanceledException)
            {
                _logger.LogError(inner,
                    "Could not persist Failed status for ModelFile {ModelFileId}.",
                    item.ModelFileId);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Extract binary STL metadata (triangle count + bounding box) without GPU.
    // Non-fatal: returns null on parse failure so the render job continues.
    // -------------------------------------------------------------------------
    private async Task<StlMetadata?> ParseStlMetadataAsync(
        IFileStore fileStore,
        string blobKey,
        RenderQueueItem item,
        CancellationToken ct)
    {
        try
        {
            await using var stream = await fileStore.ReadAsync(blobKey, "blobs", ct).ConfigureAwait(false);
            var metadata = await BinaryStlParser.ParseAsync(stream, ct).ConfigureAwait(false);

            if (metadata is null)
            {
                _logger.LogWarning(
                    "STL metadata parse returned null for blob {BlobKey} (job {JobId}); " +
                    "file may be ASCII STL or corrupt — metadata fields will be null.",
                    blobKey, item.JobId);
            }

            return metadata;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Failed to parse STL metadata for blob {BlobKey} (job {JobId}); continuing without metadata.",
                blobKey, item.JobId);
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Persist success: update STL metadata + RenderStatus, create Thumbnail ModelFile,
    // flip Model.Status to Ready.
    // -------------------------------------------------------------------------
    private static async Task RecordSuccessAsync(
        CatalogDbContext db,
        ModelFile stlFile,
        StlMetadata? metadata,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        stlFile.RenderStatus = RenderStatus.Complete;
        stlFile.UpdatedAt = now;

        if (metadata is not null)
        {
            stlFile.TriCount = metadata.Value.TriCount;
            stlFile.BoundingBox = metadata.Value.BoundingBoxJson;
        }

        // The thumbnail PNG is keyed by the STL blob key (1:1). An existing record
        // means a prior render completed (idempotent sidecar) — skip duplicate insert.
        var existingThumb = await db.ModelFiles
            .AnyAsync(f => f.ModelId == stlFile.ModelId && f.Kind == ModelFileKind.Thumbnail, ct)
            .ConfigureAwait(false);

        if (!existingThumb)
        {
            var thumb = new ModelFile
            {
                Id = Guid.NewGuid(),
                ModelId = stlFile.ModelId,
                Kind = ModelFileKind.Thumbnail,
                // BlobKey equals the STL blob key — the sidecar writes the PNG at
                // thumbs/{key[0..2]}/{key}.png. The unique index is on (ModelId, BlobKey, Kind)
                // so this does not conflict with the STL row. The thumbnail endpoint calls
                // fileStore.ReadAsync(blobKey + ".png", "thumbs") to reach that path.
                BlobKey = stlFile.BlobKey,
                Size = 0,
                MimeType = "image/png",
                Sha256 = stlFile.BlobKey,
                RenderStatus = RenderStatus.Complete,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.ModelFiles.Add(thumb);
        }

        // Flip the parent model to Ready.
        stlFile.Model.Status = ModelStatus.Ready;
        stlFile.Model.UpdatedAt = now;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Persist failure: write any available metadata, mark STL as Failed, flip Model.
    // -------------------------------------------------------------------------
    private static async Task RecordFailureAsync(
        CatalogDbContext db,
        ModelFile stlFile,
        StlMetadata? metadata,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        stlFile.RenderStatus = RenderStatus.Failed;
        stlFile.UpdatedAt = now;

        // Preserve metadata even when render fails — it's independently useful.
        if (metadata is not null)
        {
            stlFile.TriCount = metadata.Value.TriCount;
            stlFile.BoundingBox = metadata.Value.BoundingBoxJson;
        }

        stlFile.Model.Status = ModelStatus.Failed;
        stlFile.Model.UpdatedAt = now;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
