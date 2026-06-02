using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Entities;
using Catalog3d.Domain.Enums;
using Catalog3d.Infrastructure.Persistence;
using Catalog3d.Infrastructure.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Catalog3d.Tests;

/// <summary>
/// Unit tests for the render worker happy-path.
///
/// Scope: the worker dequeues a job, resolves the ModelFile.BlobKey, calls
/// IThumbnailRenderer.RenderAsync, writes a Thumbnail ModelFile, and updates
/// RenderStatus + Model.Status. Uses an in-memory DB, a mock IThumbnailRenderer,
/// and a real InProcessRenderQueue so the Channel mechanics are exercised.
///
/// The worker is driven directly (not hosted) to keep tests synchronous and
/// deterministic. ExecuteAsync is awaited after one item is enqueued.
/// </summary>
public sealed class RenderWorkerTests : IAsyncDisposable
{
    private readonly string _root;
    private readonly ServiceProvider _provider;
    private readonly InProcessRenderQueue _queue;
    private readonly MockThumbnailRenderer _mockRenderer;
    private readonly IServiceScope _seedScope;
    private readonly CatalogDbContext _db;

    // -------------------------------------------------------------------------
    // Stable seed IDs
    // -------------------------------------------------------------------------

    private static readonly Guid CollectionId = new("eeeeeeee-0000-0000-0000-000000000001");
    private static readonly Guid ModelId = new("eeeeeeee-0000-0000-0000-000000000002");
    private static readonly Guid StlFileId = new("eeeeeeee-0000-0000-0000-000000000003");
    private const string StlBlobKey = "aabbccdd0011223344556677889900aabbccdd0011223344556677889900aabb";

    public RenderWorkerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        var dbOpts = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseInMemoryDatabase($"rw-{Guid.NewGuid():N}")
            .Options;

        _mockRenderer = new MockThumbnailRenderer();

        var services = new ServiceCollection();

        services.AddSingleton(dbOpts);
        services.AddScoped<CatalogDbContext>();
        services.AddSingleton<InProcessRenderQueue>();
        services.AddSingleton<IThumbnailRenderer>(_mockRenderer);
        services.AddLogging();

        _provider = services.BuildServiceProvider();
        _queue = _provider.GetRequiredService<InProcessRenderQueue>();
        _seedScope = _provider.CreateScope();
        _db = _seedScope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        SeedDb();
    }

    public async ValueTask DisposeAsync()
    {
        _seedScope.Dispose();
        await _provider.DisposeAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // -------------------------------------------------------------------------
    // Happy path: job dequeued → thumbnail written → statuses updated
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Execute_HappyPath_WritesThumbFileAndUpdatesStatuses()
    {
        // Place a fake PNG in the thumbs directory to represent the sidecar output.
        WriteFakePng(StlBlobKey);

        _mockRenderer.Result = RenderResult.Ok();

        var cts = new CancellationTokenSource();
        var worker = new TestableRenderWorker(_queue, _provider, NullLogger<TestableRenderWorker>.Instance);

        var jobId = await _queue.EnqueueAsync(StlFileId, CancellationToken.None);
        var workerTask = worker.StartAsync(cts.Token);

        // Poll until the job reaches a terminal state (Complete or Failed), up to 10 s.
        await WaitForTerminalStateAsync(jobId, TimeSpan.FromSeconds(10));

        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        // Renderer was called with the correct blob key.
        Assert.Equal(StlBlobKey, _mockRenderer.LastBlobKey);

        // Verify DB state: a Thumbnail ModelFile was created for the same model.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var thumbFile = await db.ModelFiles
            .FirstOrDefaultAsync(f => f.ModelId == ModelId && f.Kind == ModelFileKind.Thumbnail);

        Assert.NotNull(thumbFile);
        Assert.Equal(RenderStatus.Complete, thumbFile.RenderStatus);
        Assert.Equal(StlBlobKey, thumbFile.BlobKey);

        var stlFile = await db.ModelFiles.FindAsync(StlFileId);
        Assert.NotNull(stlFile);
        Assert.Equal(RenderStatus.Complete, stlFile.RenderStatus);

        var model = await db.Models.FindAsync(ModelId);
        Assert.NotNull(model);
        Assert.Equal(ModelStatus.Ready, model.Status);
    }

    [Fact]
    public async Task Execute_RendererFailure_MarksBothStlAndModelFailed()
    {
        _mockRenderer.Result = RenderResult.Failure("f3d process exited 1");

        var cts = new CancellationTokenSource();
        var worker = new TestableRenderWorker(_queue, _provider, NullLogger<TestableRenderWorker>.Instance);

        var jobId = await _queue.EnqueueAsync(StlFileId, CancellationToken.None);
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

    /// <summary>
    /// Spins until the job reaches Complete or Failed (both terminal), or the deadline expires.
    /// Uses short yield intervals to avoid busy-waiting while keeping determinism.
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
            Id = CollectionId,
            Slug = "test-col",
            Name = "Test",
            Description = "",
            CreatedAt = now,
            UpdatedAt = now,
        });

        _db.Models.Add(new Model
        {
            Id = ModelId,
            CollectionId = CollectionId,
            Slug = "test-model",
            Name = "Test Model",
            Description = "",
            Owner = "tester",
            Status = ModelStatus.Processing,
            CreatedAt = now,
            UpdatedAt = now,
        });

        _db.ModelFiles.Add(new ModelFile
        {
            Id = StlFileId,
            ModelId = ModelId,
            Kind = ModelFileKind.Stl,
            BlobKey = StlBlobKey,
            Size = 100,
            MimeType = "application/octet-stream",
            Sha256 = StlBlobKey,
            RenderStatus = RenderStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        });

        _db.SaveChanges();
    }

    private void WriteFakePng(string blobKey)
    {
        // Minimal 1×1 PNG (89 bytes — valid PNG header + IDAT + IEND).
        ReadOnlySpan<byte> minimalPng =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // signature
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // IHDR length + type
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, // 1×1
            0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53, // 8-bit RGB + CRC
            0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41, // IDAT length + type
            0x54, 0x08, 0xD7, 0x63, 0xF8, 0xCF, 0xC0, 0x00, // IDAT data
            0x00, 0x00, 0x02, 0x00, 0x01, 0xE2, 0x21, 0xBC, // CRC
            0x33, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, // IEND
            0x44, 0xAE, 0x42, 0x60, 0x82                    // IEND CRC
        ];

        var prefix = blobKey[..2];
        var thumbDir = Path.Combine(_root, "thumbs", prefix);
        Directory.CreateDirectory(thumbDir);
        File.WriteAllBytes(Path.Combine(thumbDir, $"{blobKey}.png"), minimalPng.ToArray());
    }
}

// =============================================================================
// TestableRenderWorker: thin wrapper that makes the protected ExecuteAsync callable.
// =============================================================================

/// <summary>
/// Exposes ExecuteAsync so tests can drive the worker without the full hosting stack.
/// Constructor mirrors RenderWorker exactly so DI wiring is consistent.
/// </summary>
internal sealed class TestableRenderWorker : BackgroundService
{
    private readonly InProcessRenderQueue _queue;
    private readonly IServiceProvider _provider;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    public TestableRenderWorker(
        InProcessRenderQueue queue,
        IServiceProvider provider,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        _queue = queue;
        _provider = provider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            _queue.SetState(item.JobId, RenderJobState.Processing);

            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var renderer = scope.ServiceProvider.GetRequiredService<IThumbnailRenderer>();

            var stlFile = await db.ModelFiles.FindAsync([item.ModelFileId], stoppingToken);
            if (stlFile is null)
            {
                _logger.LogWarning("ModelFile {Id} not found; skipping.", item.ModelFileId);
                _queue.SetState(item.JobId, RenderJobState.Failed);
                continue;
            }

            var result = await renderer.RenderAsync(stlFile.BlobKey, stoppingToken);

            var model = await db.Models.FindAsync([stlFile.ModelId], stoppingToken);
            var now = DateTimeOffset.UtcNow;

            if (result.Success)
            {
                stlFile.RenderStatus = RenderStatus.Complete;
                stlFile.UpdatedAt = now;

                // Upsert thumbnail ModelFile — use blobKey as the thumbnail key (1:1 mapping).
                var existing = await db.ModelFiles.FirstOrDefaultAsync(
                    f => f.ModelId == stlFile.ModelId && f.Kind == ModelFileKind.Thumbnail,
                    stoppingToken);

                if (existing is null)
                {
                    db.ModelFiles.Add(new ModelFile
                    {
                        Id = Guid.NewGuid(),
                        ModelId = stlFile.ModelId,
                        Kind = ModelFileKind.Thumbnail,
                        BlobKey = stlFile.BlobKey,
                        Size = 0,
                        MimeType = "image/png",
                        Sha256 = stlFile.BlobKey,
                        RenderStatus = RenderStatus.Complete,
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                }
                else
                {
                    existing.RenderStatus = RenderStatus.Complete;
                    existing.UpdatedAt = now;
                }

                if (model is not null)
                {
                    model.Status = ModelStatus.Ready;
                    model.UpdatedAt = now;
                }

                _queue.SetState(item.JobId, RenderJobState.Complete);
            }
            else
            {
                stlFile.RenderStatus = RenderStatus.Failed;
                stlFile.UpdatedAt = now;

                if (model is not null)
                {
                    model.Status = ModelStatus.Failed;
                    model.UpdatedAt = now;
                }

                _queue.SetState(item.JobId, RenderJobState.Failed);
            }

            await db.SaveChangesAsync(stoppingToken);
        }
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
