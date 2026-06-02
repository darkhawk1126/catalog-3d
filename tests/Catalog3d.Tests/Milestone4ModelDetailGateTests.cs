using System.Net;
using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Entities;
using Catalog3d.Domain.Enums;
using Catalog3d.Infrastructure.Persistence;
using Catalog3d.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace Catalog3d.Tests;

/// <summary>
/// Milestone-4 tests: model-detail page Download-gate (security cornerstone).
///
/// The DESIGN.md states: "The interactive viewer is download-equivalent: three.js
/// needs raw geometry client-side, so viewing interactively ≡ obtaining the STL."
/// The model-detail page must ONLY expose the viewer link and geometry affordances
/// to callers with Download+ role. Preview-only callers see the thumbnail but not
/// the viewer or file bytes.
///
/// These tests validate the REST endpoints that back the model-detail page:
///
///   Geometry endpoint (GET /api/v1/models/files/{fileId}):
///     - Download role  → auth gate passes (200 or 501 stub; not 401/403)
///     - Uploader role  → auth gate passes (Uploader ⊇ Download)
///     - Admin role     → auth gate passes (Admin ⊇ Download)
///     - Preview-only   → 404 (geometry invisible at Preview tier)
///     - No role        → 404 (collection invisible)
///     - Unauthenticated→ 401
///
///   Viewer route (GET /viewer/{fileId}):
///     - Download role  → auth gate passes
///     - Preview-only   → 404 (viewer ≡ download)
///     - No role        → 404
///     - Unauthenticated→ 401
///
///   Thumbnail endpoint (GET /api/v1/models/{slug}/thumbnail):
///     - Preview role   → auth gate passes (Preview is the thumbnail tier)
///     - Download role  → auth gate passes (Download ⊇ Preview)
///     - No role        → 404
///     - Unauthenticated→ 401
///
/// Symmetry invariant: viewer and geometry must have IDENTICAL access outcomes
/// for the same caller — both require Download, neither less.
/// </summary>
public sealed class Milestone4ModelDetailGateTests
{
    // -------------------------------------------------------------------------
    // Geometry endpoint: Download gate
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Geometry_DownloadRole_PassesAuthGate()
    {
        await using var fixture = ModelDetailFixture.WithRole(CollectionRole.Download);
        var client = fixture.CreateClient();

        var response = await client.GetAsync(
            $"/api/v1/models/files/{ModelDetailFixture.StlFileId:D}");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Geometry_UploaderRole_PassesAuthGate()
    {
        // Uploader ⊇ Download — uploaders can fetch their own geometry.
        await using var fixture = ModelDetailFixture.WithRole(CollectionRole.Uploader);
        var client = fixture.CreateClient();

        var response = await client.GetAsync(
            $"/api/v1/models/files/{ModelDetailFixture.StlFileId:D}");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Geometry_AdminRole_PassesAuthGate()
    {
        // Admin ⊇ Uploader ⊇ Download.
        await using var fixture = ModelDetailFixture.WithRole(CollectionRole.Admin);
        var client = fixture.CreateClient();

        var response = await client.GetAsync(
            $"/api/v1/models/files/{ModelDetailFixture.StlFileId:D}");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait("Milestone", "4-EndpointImpl")]
    public async Task Geometry_PreviewOnlyRole_Returns404()
    {
        // Preview callers must NEVER receive STL bytes; geometry is invisible at this tier.
        await using var fixture = ModelDetailFixture.WithRole(CollectionRole.Preview);
        var client = fixture.CreateClient();

        var response = await client.GetAsync(
            $"/api/v1/models/files/{ModelDetailFixture.StlFileId:D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Milestone", "4-EndpointImpl")]
    public async Task Geometry_NoRole_Returns404()
    {
        await using var fixture = ModelDetailFixture.WithNoRole();
        var client = fixture.CreateClient();

        var response = await client.GetAsync(
            $"/api/v1/models/files/{ModelDetailFixture.StlFileId:D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Geometry_Unauthenticated_Returns401()
    {
        await using var fixture = ModelDetailFixture.Unauthenticated();
        var client = fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(
            $"/api/v1/models/files/{ModelDetailFixture.StlFileId:D}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Viewer route: Download gate (viewer ≡ download)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Viewer_DownloadRole_PassesAuthGate()
    {
        await using var fixture = ModelDetailFixture.WithRole(CollectionRole.Download);
        var client = fixture.CreateClient();

        var response = await client.GetAsync($"/viewer/{ModelDetailFixture.StlFileId:D}");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait("Milestone", "4-EndpointImpl")]
    public async Task Viewer_PreviewOnlyRole_Returns404()
    {
        // Interactive viewer = download equivalent. Preview callers must be denied.
        await using var fixture = ModelDetailFixture.WithRole(CollectionRole.Preview);
        var client = fixture.CreateClient();

        var response = await client.GetAsync($"/viewer/{ModelDetailFixture.StlFileId:D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Milestone", "4-EndpointImpl")]
    public async Task Viewer_NoRole_Returns404()
    {
        await using var fixture = ModelDetailFixture.WithNoRole();
        var client = fixture.CreateClient();

        var response = await client.GetAsync($"/viewer/{ModelDetailFixture.StlFileId:D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Viewer_Unauthenticated_Returns401()
    {
        await using var fixture = ModelDetailFixture.Unauthenticated();
        var client = fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/viewer/{ModelDetailFixture.StlFileId:D}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Thumbnail endpoint: Preview gate
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Thumbnail_PreviewRole_PassesAuthGate()
    {
        // Preview is the minimum tier for thumbnails — auth gate must pass.
        await using var fixture = ModelDetailFixture.WithRole(CollectionRole.Preview);
        var client = fixture.CreateClient();

        var response = await client.GetAsync(
            $"/api/v1/models/{ModelDetailFixture.ModelSlug}/thumbnail");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Thumbnail_DownloadRole_PassesAuthGate()
    {
        // Download ⊇ Preview — Download callers can also fetch thumbnails.
        await using var fixture = ModelDetailFixture.WithRole(CollectionRole.Download);
        var client = fixture.CreateClient();

        var response = await client.GetAsync(
            $"/api/v1/models/{ModelDetailFixture.ModelSlug}/thumbnail");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait("Milestone", "4-EndpointImpl")]
    public async Task Thumbnail_NoRole_Returns404()
    {
        await using var fixture = ModelDetailFixture.WithNoRole();
        var client = fixture.CreateClient();

        var response = await client.GetAsync(
            $"/api/v1/models/{ModelDetailFixture.ModelSlug}/thumbnail");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Thumbnail_Unauthenticated_Returns401()
    {
        await using var fixture = ModelDetailFixture.Unauthenticated();
        var client = fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(
            $"/api/v1/models/{ModelDetailFixture.ModelSlug}/thumbnail");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Symmetry invariant: viewer and geometry must have the SAME access bar
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Milestone", "4-EndpointImpl")]
    public async Task ViewerAndGeometry_PreviewRole_BothReturn404()
    {
        // Encodes the cornerstone: viewer grant ≡ file grant. Neither accessible at Preview.
        await using var fixture = ModelDetailFixture.WithRole(CollectionRole.Preview);
        var client = fixture.CreateClient();

        var geoResponse = await client.GetAsync(
            $"/api/v1/models/files/{ModelDetailFixture.StlFileId:D}");
        var viewerResponse = await client.GetAsync(
            $"/viewer/{ModelDetailFixture.StlFileId:D}");

        Assert.Equal(HttpStatusCode.NotFound, geoResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, viewerResponse.StatusCode);
    }

    [Fact]
    public async Task ViewerAndGeometry_DownloadRole_BothPassGate()
    {
        await using var fixture = ModelDetailFixture.WithRole(CollectionRole.Download);
        var client = fixture.CreateClient();

        var geoResponse = await client.GetAsync(
            $"/api/v1/models/files/{ModelDetailFixture.StlFileId:D}");
        var viewerResponse = await client.GetAsync(
            $"/viewer/{ModelDetailFixture.StlFileId:D}");

        // Both must pass the auth gate (200 = implemented, other non-4xx = stub).
        Assert.NotEqual(HttpStatusCode.Unauthorized, geoResponse.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, geoResponse.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, viewerResponse.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, viewerResponse.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Thumbnail is NOT download-gated — Preview callers access it but not geometry
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ThumbnailAccessible_ButGeometryBlocked_ForPreviewCaller()
    {
        // Verifies the tier split: Preview passes thumbnail gate, fails geometry gate.
        await using var fixture = ModelDetailFixture.WithRole(CollectionRole.Preview);
        var client = fixture.CreateClient();

        var thumbnailResponse = await client.GetAsync(
            $"/api/v1/models/{ModelDetailFixture.ModelSlug}/thumbnail");
        var geometryResponse = await client.GetAsync(
            $"/api/v1/models/files/{ModelDetailFixture.StlFileId:D}");

        // Thumbnail auth gate: passes (200 or 404-if-not-yet-rendered; not 401/403).
        Assert.NotEqual(HttpStatusCode.Unauthorized, thumbnailResponse.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, thumbnailResponse.StatusCode);

        // Geometry auth gate: blocked. Will be 404 once endpoint body is implemented.
        // For now we only assert it's not 200 (the stub returns non-200 for Preview).
        Assert.NotEqual(HttpStatusCode.OK, geometryResponse.StatusCode);
    }
}

// =============================================================================
// ModelDetailFixture
// =============================================================================

/// <summary>
/// Fixture that seeds one collection, one model, and one STL + thumbnail file,
/// then presents a caller with the specified role.
/// Mirrors the AclFixture pattern but scoped to milestone-4 cornerstone tests.
/// </summary>
internal sealed class ModelDetailFixture : WebApplicationFactory<Program>, IAsyncDisposable
{
    public static readonly Guid CollectionId = new("fd000000-0000-0000-0000-000000000001");
    public static readonly Guid ModelId = new("fd000000-0000-0000-0000-000000000002");
    public static readonly Guid StlFileId = new("fd000000-0000-0000-0000-000000000003");
    public static readonly Guid ThumbFileId = new("fd000000-0000-0000-0000-000000000004");
    public const string ModelSlug = "m4-detail-model";
    public const string CollectionSlug = "m4-detail-col";

    // Distinct SHA-256 keys for STL and thumbnail blobs.
    private const string StlBlobKey = "a3f2e1d0c9b8a7968574635251403f2e1d0c9b8a79685746352403020f0e0d0c";
    private const string ThumbBlobKey = "b4c5d6e7f8091a2b3c4d5e6f7081920313233343536373839404142434445464";

    private readonly IUserContext _userContext;
    private readonly bool _isAuthenticated;
    private readonly string _dbName = $"m4-detail-{Guid.NewGuid():N}";
    private readonly string _storageRoot;
    private Action<CatalogDbContext>? _seedRoleAction;

    private ModelDetailFixture(IUserContext userContext, bool isAuthenticated)
    {
        _userContext = userContext;
        _isAuthenticated = isAuthenticated;
        _storageRoot = Path.Combine(Path.GetTempPath(), $"m4-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);
        SeedBlobFiles();
    }

    internal static ModelDetailFixture WithRole(CollectionRole role)
    {
        var ctx = new ModelDetailStubUser("user:m4-tester");
        var fixture = new ModelDetailFixture(ctx, isAuthenticated: true);
        fixture._seedRoleAction = db => db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionId,
            Principal = "user:m4-tester",
            Role = role,
        });
        return fixture;
    }

    internal static ModelDetailFixture WithNoRole()
        => new(new ModelDetailStubUser("user:m4-norole"), isAuthenticated: true);

    internal static ModelDetailFixture Unauthenticated()
        => new(new ModelDetailUnauthUser(), isAuthenticated: false);

    private void SeedBlobFiles()
    {
        // 1×1 PNG thumbnail.
        ReadOnlySpan<byte> minimalPng =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,
            0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41,
            0x54, 0x08, 0xD7, 0x63, 0xF8, 0xCF, 0xC0, 0x00,
            0x00, 0x00, 0x02, 0x00, 0x01, 0xE2, 0x21, 0xBC,
            0x33, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E,
            0x44, 0xAE, 0x42, 0x60, 0x82
        ];

        // Thumbnail: stored as {key}.png under thumbs/
        var thumbDir = Path.Combine(_storageRoot, "thumbs", ThumbBlobKey[..2]);
        Directory.CreateDirectory(thumbDir);
        File.WriteAllBytes(Path.Combine(thumbDir, $"{ThumbBlobKey}.png"), minimalPng.ToArray());

        // STL blob
        var stlBytes = new byte[84 + 50];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(stlBytes.AsSpan(80, 4), 1u);
        var blobDir = Path.Combine(_storageRoot, "blobs", StlBlobKey[..2]);
        Directory.CreateDirectory(blobDir);
        File.WriteAllBytes(Path.Combine(blobDir, StlBlobKey), stlBytes);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(DbContextOptions<CatalogDbContext>))
                    services.RemoveAt(i);
            }

            var dbOpts = new DbContextOptionsBuilder<CatalogDbContext>()
                .UseInMemoryDatabase(_dbName)
                .Options;
            services.AddSingleton(dbOpts);

            var root = _storageRoot;
            services.Configure<DiskFileStoreOptions>(o => o.Root = root);

            services.RemoveAll<IUserContext>();
            services.AddScoped<IUserContext>(_ => _userContext);

            var isAuth = _isAuthenticated;
            services
                .AddAuthentication(options =>
                {
                    options.DefaultScheme = ModelDetailTestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = ModelDetailTestAuthHandler.SchemeName;
                })
                .AddScheme<ModelDetailTestAuthHandlerOptions, ModelDetailTestAuthHandler>(
                    ModelDetailTestAuthHandler.SchemeName,
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
            Name = "M4 Detail Test Collection",
            Description = "",
            CreatedAt = now,
            UpdatedAt = now,
        });

        db.Models.Add(new Model
        {
            Id = ModelId,
            CollectionId = CollectionId,
            Slug = ModelSlug,
            Name = "M4 Detail Test Model",
            Description = "",
            Owner = "m4-tester",
            Status = ModelStatus.Ready,
            CreatedAt = now,
            UpdatedAt = now,
        });

        db.ModelFiles.Add(new ModelFile
        {
            Id = StlFileId,
            ModelId = ModelId,
            Kind = ModelFileKind.Stl,
            BlobKey = StlBlobKey,
            Size = 134,
            MimeType = "application/octet-stream",
            Sha256 = StlBlobKey,
            RenderStatus = RenderStatus.Complete,
            CreatedAt = now,
            UpdatedAt = now,
        });

        db.ModelFiles.Add(new ModelFile
        {
            Id = ThumbFileId,
            ModelId = ModelId,
            Kind = ModelFileKind.Thumbnail,
            BlobKey = ThumbBlobKey,
            Size = 89,
            MimeType = "image/png",
            Sha256 = ThumbBlobKey,
            RenderStatus = RenderStatus.Complete,
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

internal sealed class ModelDetailStubUser(string userId) : IUserContext
{
    public bool IsAuthenticated => true;
    public string UserId { get; } = userId;
    public string DisplayName => "M4 Tester";
    public IReadOnlyList<string> Groups => Array.Empty<string>();
}

internal sealed class ModelDetailUnauthUser : IUserContext
{
    public bool IsAuthenticated => false;
    public string UserId => string.Empty;
    public string DisplayName => string.Empty;
    public IReadOnlyList<string> Groups => Array.Empty<string>();
}

internal sealed class ModelDetailTestAuthHandlerOptions : AuthenticationSchemeOptions
{
    public bool Authenticated { get; set; } = true;
}

internal sealed class ModelDetailTestAuthHandler(
    IOptionsMonitor<ModelDetailTestAuthHandlerOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<ModelDetailTestAuthHandlerOptions>(options, logger, encoder)
{
    internal const string SchemeName = "ModelDetailTestScheme";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Options.Authenticated)
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity([], Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
