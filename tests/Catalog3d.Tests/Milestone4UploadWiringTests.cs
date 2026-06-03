using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Entities;
using Catalog3d.Domain.Enums;
using Catalog3d.Infrastructure.Persistence;
using Catalog3d.Infrastructure.Storage;
using Catalog3d.Infrastructure.Upload;
using Catalog3d.Web.Endpoints.Dto;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Catalog3d.Tests;

/// <summary>
/// Milestone-4 tests: upload page wires upload to storage + render enqueue.
///
/// The Blazor CollectionUpload page calls POST /api/v1/collections/{slug}/models
/// (same-origin REST endpoint) rather than bypassing the REST layer. These tests
/// exercise that endpoint to verify the full upload wiring:
///
///   1. Uploader: file lands in IFileStore (blob exists after upload).
///   2. Uploader: a ModelFile row is persisted with RenderStatus.Pending.
///   3. Uploader: a render job is enqueued (IRenderQueue.EnqueueAsync called).
///   4. Preview-only caller: upload rejected with 403 (Forbid).
///   5. No-role caller: upload rejected (collection is 404).
///   6. Unauthenticated: upload rejected with 401.
///   7. Upload with explicit name/description fields: Model.Name + Description persisted.
///   8. Content-addressing: uploading the same bytes twice returns the same blob key.
///   9. 201 Created Location header uses the model slug, not the GUID (M2).
///  10. Response body includes Slug field (M2).
///  11. Duplicate slug → 409 Conflict (H5, IModelUploadService delegation).
///  12. Invalid STL content → 400 Bad Request (M11, IModelUploadService delegation).
///  13. Upload exceeds configured ceiling → 400 with TooLarge reason (M10).
///  14. Empty-derived slug → 400 Bad Request (H5, before touching storage).
/// </summary>
public sealed class Milestone4UploadWiringTests
{
    // -------------------------------------------------------------------------
    // 1. Uploader — file lands in IFileStore
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_UploaderRole_BlobExistsInStore()
    {
        await using var fixture = UploadFixture.WithRole(CollectionRole.Uploader);
        var client = fixture.CreateClient();

        using var content = MinimalStlContent("cube.stl");
        var response = await client.PostAsync(
            $"/api/v1/collections/{UploadFixture.CollectionSlug}/models", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<UploadModelResponse>();
        Assert.NotNull(result);

        // Verify the blob was actually written to the temp storage root.
        var blobPath = fixture.BlobPath(result.BlobKey);
        Assert.True(File.Exists(blobPath), $"Blob file not found at {blobPath}");
    }

    // -------------------------------------------------------------------------
    // 2. Uploader — ModelFile row persisted with RenderStatus.Pending
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_UploaderRole_ModelFileRowPersistedPending()
    {
        await using var fixture = UploadFixture.WithRole(CollectionRole.Uploader);
        var client = fixture.CreateClient();

        using var content = MinimalStlContent("cube2.stl");
        var response = await client.PostAsync(
            $"/api/v1/collections/{UploadFixture.CollectionSlug}/models", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<UploadModelResponse>();
        Assert.NotNull(result);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var modelFile = await db.ModelFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == result.FileId);

        Assert.NotNull(modelFile);
        Assert.Equal(ModelFileKind.Stl, modelFile.Kind);
        Assert.Equal(RenderStatus.Pending, modelFile.RenderStatus);
    }

    // -------------------------------------------------------------------------
    // 3. Uploader — render job enqueued
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_UploaderRole_RenderJobEnqueued()
    {
        var trackingQueue = new TrackingRenderQueue();
        await using var fixture = UploadFixture.WithRoleAndQueue(CollectionRole.Uploader, trackingQueue);
        var client = fixture.CreateClient();

        using var content = MinimalStlContent("cube3.stl");
        var response = await client.PostAsync(
            $"/api/v1/collections/{UploadFixture.CollectionSlug}/models", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, trackingQueue.EnqueueCallCount);
    }

    // -------------------------------------------------------------------------
    // 4. Preview-only caller: upload rejected with 403
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_PreviewRole_ReturnsForbid()
    {
        await using var fixture = UploadFixture.WithRole(CollectionRole.Preview);
        var client = fixture.CreateClient();

        using var content = MinimalStlContent("cube4.stl");
        var response = await client.PostAsync(
            $"/api/v1/collections/{UploadFixture.CollectionSlug}/models", content);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // 5. No-role caller: collection is 404 (invisible)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_NoRole_ReturnsNotFound()
    {
        await using var fixture = UploadFixture.WithNoRole();
        var client = fixture.CreateClient();

        using var content = MinimalStlContent("cube5.stl");
        var response = await client.PostAsync(
            $"/api/v1/collections/{UploadFixture.CollectionSlug}/models", content);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // 6. Unauthenticated: 401
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_Unauthenticated_Returns401()
    {
        await using var fixture = UploadFixture.Unauthenticated();
        var client = fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var content = MinimalStlContent("cube6.stl");
        var response = await client.PostAsync(
            $"/api/v1/collections/{UploadFixture.CollectionSlug}/models", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // 7. Name + description fields persisted on the Model row
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_WithNameAndDescription_ModelRowHasCorrectFields()
    {
        await using var fixture = UploadFixture.WithRole(CollectionRole.Uploader);
        var client = fixture.CreateClient();

        using var content = BuildNamedUploadContent(
            fileName: "widget.stl",
            modelName: "My Widget",
            description: "A test widget");

        var response = await client.PostAsync(
            $"/api/v1/collections/{UploadFixture.CollectionSlug}/models", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<UploadModelResponse>();
        Assert.NotNull(result);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var model = await db.Models
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == result.ModelId);

        Assert.NotNull(model);
        Assert.Equal("My Widget", model.Name);
        Assert.Equal("A test widget", model.Description);
    }

    // -------------------------------------------------------------------------
    // 8. Content-addressing: same bytes → same blob key
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_SameBytesTowice_ReturnsSameBlobKey()
    {
        await using var fixture = UploadFixture.WithRole(CollectionRole.Uploader);
        var client = fixture.CreateClient();

        // First upload with "a.stl".
        using var content1 = MinimalStlContent("a.stl");
        var r1 = await client.PostAsync(
            $"/api/v1/collections/{UploadFixture.CollectionSlug}/models", content1);
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        var result1 = await r1.Content.ReadFromJsonAsync<UploadModelResponse>();

        // Second upload with the identical bytes but different file name.
        using var content2 = MinimalStlContent("b.stl");
        var r2 = await client.PostAsync(
            $"/api/v1/collections/{UploadFixture.CollectionSlug}/models", content2);
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);
        var result2 = await r2.Content.ReadFromJsonAsync<UploadModelResponse>();

        Assert.NotNull(result1);
        Assert.NotNull(result2);
        // Same bytes → same SHA-256 hash → same blob key.
        Assert.Equal(result1.BlobKey, result2.BlobKey);
    }

    // -------------------------------------------------------------------------
    // 9. 201 Created Location header uses slug (M2)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_Created_LocationHeaderUsesSlugNotGuid()
    {
        await using var fixture = UploadFixture.WithRole(CollectionRole.Uploader);
        var client = fixture.CreateClient();

        using var content = MinimalStlContent("my-widget.stl");
        var response = await client.PostAsync(
            $"/api/v1/collections/{UploadFixture.CollectionSlug}/models", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var location = response.Headers.Location?.ToString();
        Assert.NotNull(location);

        // Location must contain the slug, not a GUID pattern.
        Assert.Contains("my-widget", location);
        Assert.DoesNotMatch(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", location);
    }

    // -------------------------------------------------------------------------
    // 10. Response body includes Slug field (M2)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_Created_ResponseBodyContainsSlug()
    {
        await using var fixture = UploadFixture.WithRole(CollectionRole.Uploader);
        var client = fixture.CreateClient();

        using var content = MinimalStlContent("my-part.stl");
        var response = await client.PostAsync(
            $"/api/v1/collections/{UploadFixture.CollectionSlug}/models", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<UploadModelResponse>();
        Assert.NotNull(result);
        Assert.Equal("my-part", result.Slug);
    }

    // -------------------------------------------------------------------------
    // 11. Duplicate slug → 409 Conflict (H5)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_DuplicateSlug_Returns409Conflict()
    {
        await using var fixture = UploadFixture.WithRole(CollectionRole.Uploader);
        var client = fixture.CreateClient();

        // First upload succeeds.
        using var first = MinimalStlContent("dup-model.stl");
        var r1 = await client.PostAsync(
            $"/api/v1/collections/{UploadFixture.CollectionSlug}/models", first);
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);

        // Second upload with same slug (different bytes to force a new blob).
        using var second = DifferentStlContent("dup-model.stl");
        var r2 = await client.PostAsync(
            $"/api/v1/collections/{UploadFixture.CollectionSlug}/models", second);

        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
    }

    // -------------------------------------------------------------------------
    // 12. Invalid STL content → 400 Bad Request (M11)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_InvalidStlContent_Returns400()
    {
        await using var fixture = UploadFixture.WithRole(CollectionRole.Uploader);
        var client = fixture.CreateClient();

        // 10 bytes — too short to be a valid binary STL (needs ≥ 84).
        using var content = RawBytesContent(new byte[10], "garbage.stl");
        var response = await client.PostAsync(
            $"/api/v1/collections/{UploadFixture.CollectionSlug}/models", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // 13. Upload exceeds ceiling → 400 (M10)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_ExceedsSizeCeiling_Returns400()
    {
        // Configure a 50-byte ceiling; the minimal STL (84+50=134 bytes) exceeds it.
        await using var fixture = UploadFixture.WithRoleAndMaxBytes(CollectionRole.Uploader, maxBytes: 50);
        var client = fixture.CreateClient();

        using var content = MinimalStlContent("big.stl");
        var response = await client.PostAsync(
            $"/api/v1/collections/{UploadFixture.CollectionSlug}/models", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // 14. Empty slug → 400 Bad Request (H5)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_EmptyDerivedSlug_Returns400()
    {
        await using var fixture = UploadFixture.WithRole(CollectionRole.Uploader);
        var client = fixture.CreateClient();

        // Filename "----.stl" produces an empty slug after trimming dashes.
        using var content = MinimalStlContent("----.stl");
        var response = await client.PostAsync(
            $"/api/v1/collections/{UploadFixture.CollectionSlug}/models", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    // Minimal valid binary STL (1 triangle, 84+50 bytes).
    private static byte[] BuildMinimalStlBytes()
    {
        var buf = new byte[84 + 50];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(80, 4), 1u);
        return buf;
    }

    private MultipartFormDataContent MinimalStlContent(string fileName)
    {
        var bytes = BuildMinimalStlBytes();
        var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(fileContent, "file", fileName);
        return multipart;
    }

    private static MultipartFormDataContent BuildNamedUploadContent(
        string fileName,
        string modelName,
        string description)
    {
        var bytes = new byte[84 + 50];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), 1u);
        // Use different triangle data to avoid slug collisions in the same DB.
        bytes[84] = 0x01;

        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(modelName), "name");
        multipart.Add(new StringContent(description), "description");
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(fileContent, "file", fileName);
        return multipart;
    }

    // Different valid STL bytes (2 triangles) so blob key differs from MinimalStlContent.
    private MultipartFormDataContent DifferentStlContent(string fileName)
    {
        var buf = new byte[84 + 100];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(80, 4), 2u);
        var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(buf);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(fileContent, "file", fileName);
        return multipart;
    }

    private static MultipartFormDataContent RawBytesContent(byte[] bytes, string fileName)
    {
        var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(fileContent, "file", fileName);
        return multipart;
    }
}

// =============================================================================
// TrackingRenderQueue — counts EnqueueAsync calls without side effects
// =============================================================================

internal sealed class TrackingRenderQueue : IRenderQueue
{
    private int _enqueueCount;
    public int EnqueueCallCount => _enqueueCount;

    public Task<Guid> EnqueueAsync(Guid modelFileId, CancellationToken cancellationToken = default)
    {
        System.Threading.Interlocked.Increment(ref _enqueueCount);
        return Task.FromResult(Guid.NewGuid());
    }

    public Task<RenderJobState> GetStateAsync(Guid jobId, CancellationToken cancellationToken = default)
        => Task.FromResult(RenderJobState.Queued);
}

// =============================================================================
// UploadFixture — one-shot WebApplicationFactory for upload wiring tests
// =============================================================================

internal sealed class UploadFixture : WebApplicationFactory<Program>, IAsyncDisposable
{
    public static readonly Guid CollectionId = new("ee000000-0000-0000-0000-000000000001");
    public const string CollectionSlug = "upload-test-col";

    private readonly IUserContext _userContext;
    private readonly bool _isAuthenticated;
    private readonly string _dbName = $"upload-{Guid.NewGuid():N}";
    private readonly string _storageRoot;
    private readonly IRenderQueue? _renderQueue;
    private readonly long? _maxBytesOverride;
    private Action<CatalogDbContext>? _seedRoleAction;

    private UploadFixture(
        IUserContext userContext,
        bool isAuthenticated,
        IRenderQueue? renderQueue = null,
        long? maxBytesOverride = null)
    {
        _userContext = userContext;
        _isAuthenticated = isAuthenticated;
        _renderQueue = renderQueue;
        _maxBytesOverride = maxBytesOverride;
        _storageRoot = Path.Combine(Path.GetTempPath(), $"upload-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);
    }

    internal static UploadFixture WithRole(CollectionRole role)
    {
        var ctx = new UploadStubUserContext("user:uploader-tester");
        var fixture = new UploadFixture(ctx, isAuthenticated: true);
        fixture._seedRoleAction = db => db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionId,
            Principal = "user:uploader-tester",
            Role = role,
        });
        return fixture;
    }

    internal static UploadFixture WithRoleAndQueue(CollectionRole role, IRenderQueue queue)
    {
        var ctx = new UploadStubUserContext("user:uploader-tester");
        var fixture = new UploadFixture(ctx, isAuthenticated: true, renderQueue: queue);
        fixture._seedRoleAction = db => db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionId,
            Principal = "user:uploader-tester",
            Role = role,
        });
        return fixture;
    }

    internal static UploadFixture WithRoleAndMaxBytes(CollectionRole role, long maxBytes)
    {
        var ctx = new UploadStubUserContext("user:uploader-tester");
        var fixture = new UploadFixture(ctx, isAuthenticated: true, maxBytesOverride: maxBytes);
        fixture._seedRoleAction = db => db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionId,
            Principal = "user:uploader-tester",
            Role = role,
        });
        return fixture;
    }

    internal static UploadFixture WithNoRole()
        => new(new UploadStubUserContext("user:no-role"), isAuthenticated: true);

    internal static UploadFixture Unauthenticated()
        => new(new UnauthUploadStubUserContext(), isAuthenticated: false);

    /// <summary>
    /// Returns the path where DiskFileStore would write a blob with the given key.
    /// Used to assert the file was actually created on disk.
    /// </summary>
    public string BlobPath(string blobKey)
        => Path.Combine(_storageRoot, "blobs", blobKey[..2], blobKey);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Swap Postgres for in-memory.
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(DbContextOptions<CatalogDbContext>))
                    services.RemoveAt(i);
            }
            var dbOpts = new DbContextOptionsBuilder<CatalogDbContext>()
                .UseInMemoryDatabase(_dbName)
                .Options;
            services.AddSingleton(dbOpts);

            // Point DiskFileStore at our temp directory.
            var root = _storageRoot;
            services.Configure<DiskFileStoreOptions>(o => o.Root = root);

            // Register the shared upload service so the endpoint can delegate to it.
            // Program.cs registers this in production; fixtures must wire it explicitly.
            services.AddScoped<IModelUploadService, Catalog3d.Infrastructure.Upload.ModelUploadService>();

            // Apply per-test size ceiling before the service reads options.
            if (_maxBytesOverride.HasValue)
            {
                var ceiling = _maxBytesOverride.Value;
                services.Configure<ModelUploadOptions>(o => o.MaxSizeBytes = ceiling);
            }

            // Stub user context.
            services.RemoveAll<IUserContext>();
            services.AddScoped<IUserContext>(_ => _userContext);

            // Remove the RenderWorker background service so it does not race
            // with test assertions by transitioning RenderStatus out of Pending.
            services.RemoveAll<IHostedService>();

            // If a tracking render queue was provided, swap the real one out.
            if (_renderQueue is not null)
            {
                var queue = _renderQueue;
                services.RemoveAll<IRenderQueue>();
                services.AddSingleton<IRenderQueue>(_ => queue);
            }

            // Test auth scheme.
            var isAuth = _isAuthenticated;
            services
                .AddAuthentication(options =>
                {
                    options.DefaultScheme = UploadTestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = UploadTestAuthHandler.SchemeName;
                })
                .AddScheme<UploadTestAuthHandlerOptions, UploadTestAuthHandler>(
                    UploadTestAuthHandler.SchemeName,
                    opts => opts.Authenticated = isAuth);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        SeedDb(db);

        return host;
    }

    private void SeedDb(CatalogDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        db.Collections.Add(new Collection
        {
            Id = CollectionId,
            Slug = CollectionSlug,
            Name = "Upload Test Collection",
            Description = "",
            CreatedAt = now,
            UpdatedAt = now,
        });

        _seedRoleAction?.Invoke(db);
        db.SaveChanges();
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        Dispose();
        if (Directory.Exists(_storageRoot))
        {
            try { Directory.Delete(_storageRoot, recursive: true); }
            catch (IOException) { }
        }
        await ValueTask.CompletedTask;
    }
}

// -------------------------------------------------------------------------
// Stub user contexts for upload tests
// -------------------------------------------------------------------------

internal sealed class UploadStubUserContext(string userId) : IUserContext
{
    public bool IsAuthenticated => true;
    public string UserId { get; } = userId;
    public string DisplayName => "Upload Tester";
    public IReadOnlyList<string> Groups => Array.Empty<string>();
}

internal sealed class UnauthUploadStubUserContext : IUserContext
{
    public bool IsAuthenticated => false;
    public string UserId => string.Empty;
    public string DisplayName => string.Empty;
    public IReadOnlyList<string> Groups => Array.Empty<string>();
}

// -------------------------------------------------------------------------
// Test auth handler for upload fixture
// -------------------------------------------------------------------------

internal sealed class UploadTestAuthHandlerOptions : AuthenticationSchemeOptions
{
    public bool Authenticated { get; set; } = true;
}

internal sealed class UploadTestAuthHandler(
    IOptionsMonitor<UploadTestAuthHandlerOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<UploadTestAuthHandlerOptions>(options, logger, encoder)
{
    internal const string SchemeName = "UploadTestScheme";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Options.Authenticated)
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new System.Security.Claims.ClaimsIdentity([], Scheme.Name);
        var ticket = new AuthenticationTicket(
            new System.Security.Claims.ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
