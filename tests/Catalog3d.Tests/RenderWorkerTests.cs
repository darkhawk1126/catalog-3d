using System.Buffers.Binary;
using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Entities;
using Catalog3d.Infrastructure.Persistence;
using Catalog3d.Infrastructure.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Catalog3d.Tests;

/// <summary>
/// Unit tests for RenderWorker — the real implementation, not a reimplementation.
///
/// Scope: the worker dequeues a job, resolves the ModelFile.BlobKey, calls
/// IThumbnailRenderer.RenderAsync, writes a Thumbnail ModelFile (with backfilled Size),
/// and updates RenderStatus + Model.Status. Uses an in-memory DB, a mock
/// IThumbnailRenderer, a mock IFileStore (returns a minimal valid binary STL for
/// ParseStlMetadataAsync and a fixed PNG size for H7 SizeAsync), and a real
/// InProcessRenderQueue so the Channel mechanics are exercised.
/// </summary>
public sealed class RenderWorkerTests : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly InProcessRenderQueue _queue;
    private readonly MockThumbnailRenderer _mockRenderer;
    private readonly MockFileStore _mockFileStore;
    private readonly IServiceScope _seedScope;
    private readonly CatalogDbContext _db;

    // -------------------------------------------------------------------------
    // Stable seed IDs
    // -------------------------------------------------------------------------

    private static readonly Guid CollectionId = new("eeeeeeee-0000-0000-0000-000000000001");
    private static readonly Guid ModelId      = new("eeeeeeee-0000-0000-0000-000000000002");
    private static readonly Guid StlFileId    = new("eeeeeeee-0000-0000-0000-000000000003");
    private const string StlBlobKey = "aabbccdd0011223344556677889900aabbccdd0011223344556677889900aabb";

    // Fake PNG size returned by MockFileStore.SizeAsync.
    private const long FakePngSize = 89L;

    public RenderWorkerTests()
    {
        var dbOpts = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseInMemoryDatabase($"rw-{Guid.NewGuid():N}")
            .Options;

        _mockRenderer  = new MockThumbnailRenderer();
        _mockFileStore = new MockFileStore();

        var services = new ServiceCollection();

        services.AddSingleton(dbOpts);
        services.AddScoped<CatalogDbContext>();
        services.AddSingleton<InProcessRenderQueue>();
        services.AddSingleton<IThumbnailRenderer>(_mockRenderer);
        services.AddSingleton<IFileStore>(_mockFileStore);
        services.AddLogging();

        _provider  = services.BuildServiceProvider();
        _queue     = _provider.GetRequiredService<InProcessRenderQueue>();
        _seedScope = _provider.CreateScope();
        _db        = _seedScope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        SeedDb();
    }

    public async ValueTask DisposeAsync()
    {
        _seedScope.Dispose();
        await _provider.DisposeAsync();
    }

    // -------------------------------------------------------------------------
    // Happy path: job dequeued → thumbnail written → statuses updated → Size set
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Execute_HappyPath_WritesThumbFileAndUpdatesStatuses()
    {
        _mockRenderer.Result = RenderResult.Ok();

        var cts    = new CancellationTokenSource();
        var worker = new RenderWorker(
            _queue,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RenderWorker>.Instance);

        var jobId      = await _queue.EnqueueAsync(StlFileId, CancellationToken.None);
        var workerTask = worker.StartAsync(cts.Token);

        // Poll until the job reaches a terminal state (Complete or Failed), up to 10 s.
        await WaitForTerminalStateAsync(jobId, TimeSpan.FromSeconds(10));

        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        // Renderer was called with the correct blob key.
        Assert.Equal(StlBlobKey, _mockRenderer.LastBlobKey);

        // Verify DB state via a fresh scope (worker uses its own scopes internally).
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var thumbFile = await db.ModelFiles
            .FirstOrDefaultAsync(f => f.ModelId == ModelId && f.Kind == ModelFileKind.Thumbnail);

        Assert.NotNull(thumbFile);
        Assert.Equal(RenderStatus.Complete, thumbFile.RenderStatus);
        Assert.Equal(StlBlobKey, thumbFile.BlobKey);

        // H7: Size must be backfilled from IFileStore.SizeAsync (not remain 0).
        Assert.Equal(FakePngSize, thumbFile.Size);

        var stlFile = await db.ModelFiles.FindAsync(StlFileId);
        Assert.NotNull(stlFile);
        Assert.Equal(RenderStatus.Complete, stlFile.RenderStatus);

        var model = await db.Models.FindAsync(ModelId);
        Assert.NotNull(model);
        Assert.Equal(ModelStatus.Ready, model.Status);
    }

    // -------------------------------------------------------------------------
    // Failure path: renderer error → STL and Model both marked Failed
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Execute_RendererFailure_MarksBothStlAndModelFailed()
    {
        _mockRenderer.Result = RenderResult.Failure("stl-thumb process exited 1");

        var cts    = new CancellationTokenSource();
        var worker = new RenderWorker(
            _queue,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RenderWorker>.Instance);

        var jobId      = await _queue.EnqueueAsync(StlFileId, CancellationToken.None);
        var workerTask = worker.StartAsync(cts.Token);

        await WaitForTerminalStateAsync(jobId, TimeSpan.FromSeconds(10));

        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var stlFile = await db.ModelFiles.FindAsync(StlFileId);
        Assert.NotNull(stlFile);
        Assert.Equal(RenderStatus.Failed, stlFile.RenderStatus);

        var model = await db.Models.FindAsync(ModelId);
        Assert.NotNull(model);
        Assert.Equal(ModelStatus.Failed, model.Status);
    }

    // -------------------------------------------------------------------------
    // M3: already-Complete file → idempotent no-op (no duplicate thumbnail row)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Execute_AlreadyCompleteFile_SkipsWithoutRerendering()
    {
        // Flip the STL file to Complete before the worker picks it up.
        var stlFile = await _db.ModelFiles.FindAsync(StlFileId);
        stlFile!.RenderStatus = RenderStatus.Complete;
        await _db.SaveChangesAsync();

        _mockRenderer.Result = RenderResult.Ok();

        var cts    = new CancellationTokenSource();
        var worker = new RenderWorker(
            _queue,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RenderWorker>.Instance);

        var jobId      = await _queue.EnqueueAsync(StlFileId, CancellationToken.None);
        var workerTask = worker.StartAsync(cts.Token);

        await WaitForTerminalStateAsync(jobId, TimeSpan.FromSeconds(10));

        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        // Renderer must NOT have been called — the early-exit fired first.
        Assert.Null(_mockRenderer.LastBlobKey);

        // No thumbnail row should have been created.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var thumbCount = await db.ModelFiles.CountAsync(f => f.Kind == ModelFileKind.Thumbnail);
        Assert.Equal(0, thumbCount);

        // Regression: skipping an already-Complete file must still reconcile the parent
        // model to Ready. The seed model is Processing (as a re-render request leaves it);
        // the skip path previously returned early, leaving it stuck in Processing forever.
        var model = await db.Models.FirstAsync();
        Assert.Equal(ModelStatus.Ready, model.Status);

        var jobState = await _queue.GetStateAsync(jobId);
        Assert.Equal(RenderJobState.Complete, jobState);
    }

    /// <summary>
    /// Spins until the job reaches Complete or Failed (both terminal), or the deadline expires.
    /// </summary>
    private async Task WaitForTerminalStateAsync(Guid jobId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await _queue.GetStateAsync(jobId);
            if (state is RenderJobState.Complete or RenderJobState.Failed)
                return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Job {jobId} did not reach a terminal state within {timeout}.");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void SeedDb()
    {
        var now = DateTimeOffset.UtcNow;

        _db.Collections.Add(new Collection
        {
            Id          = CollectionId,
            Slug        = "test-col",
            Name        = "Test",
            Description = "",
            CreatedAt   = now,
            UpdatedAt   = now,
        });

        _db.Models.Add(new Model
        {
            Id           = ModelId,
            CollectionId = CollectionId,
            Slug         = "test-model",
            Name         = "Test Model",
            Description  = "",
            Owner        = "tester",
            Status       = ModelStatus.Processing,
            CreatedAt    = now,
            UpdatedAt    = now,
        });

        _db.ModelFiles.Add(new ModelFile
        {
            Id          = StlFileId,
            ModelId     = ModelId,
            Kind        = ModelFileKind.Stl,
            BlobKey     = StlBlobKey,
            Size        = 134,   // 84-byte header + 1 triangle * 50 bytes
            MimeType    = "model/stl",
            Sha256      = StlBlobKey,
            RenderStatus = RenderStatus.Pending,
            CreatedAt   = now,
            UpdatedAt   = now,
        });

        _db.SaveChanges();
    }
}

// =============================================================================
// Mock IThumbnailRenderer
// =============================================================================

internal sealed class MockThumbnailRenderer : IThumbnailRenderer
{
    public RenderResult Result { get; set; } = RenderResult.Ok();
    public string? LastBlobKey { get; private set; }

    public Task<RenderResult> RenderAsync(string blobKey, CancellationToken cancellationToken = default)
    {
        LastBlobKey = blobKey;
        return Task.FromResult(Result);
    }
}

// =============================================================================
// Mock IFileStore — returns a minimal valid binary STL for ParseStlMetadataAsync
// and a fixed PNG byte count for H7 SizeAsync backfill.
// =============================================================================

internal sealed class MockFileStore : IFileStore
{
    // Minimal valid binary STL: 80-byte header + 4-byte count (1 triangle) + 50 bytes = 134 bytes.
    // One triangle at the origin so bounding-box parsing succeeds without error.
    private static readonly byte[] MinimalStl = BuildMinimalStl();

    // Matches the 89-byte minimal PNG used in the old WriteFakePng helper.
    public long PngSize { get; set; } = 89L;

    public Task<Stream> ReadAsync(string key, string blobSubDirectory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Return a fresh copy so each reader gets an independent stream position.
        Stream s = new MemoryStream(MinimalStl, writable: false);
        return Task.FromResult(s);
    }

    public Task<long> SizeAsync(string key, string blobSubDirectory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(PngSize);
    }

    public Task<string> WriteAsync(Stream content, string blobSubDirectory, CancellationToken ct = default)
        => throw new NotSupportedException("MockFileStore does not support WriteAsync.");

    public Task<bool> ExistsAsync(string key, string blobSubDirectory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }

    public Task DeleteAsync(string key, string blobSubDirectory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static byte[] BuildMinimalStl()
    {
        // 80-byte header + 4-byte count (1) + 50-byte triangle = 134 bytes.
        var buf = new byte[134];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(80, 4), 1u); // triCount = 1
        // Triangle bytes are all zero — normal (0,0,0), three vertices at origin, attr=0.
        // The bounding box will be [0,0,0]→[0,0,0], which is valid (not null).
        return buf;
    }
}
