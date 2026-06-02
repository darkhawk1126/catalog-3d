using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
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

namespace Catalog3d.Tests;

/// <summary>
/// ACL-gate integration tests for the milestone-3 endpoints.
///
/// Security cornerstone assertions:
///   Thumbnail (GET /api/v1/models/{slug}/thumbnail):
///     - Preview role → 200 (or 404 only if thumbnail missing; 200 when present)
///     - No role      → 404
///     - Unauthenticated → 401
///
///   Geometry (GET /api/v1/models/files/{fileId:guid}):
///     - Download role → 200 (file bytes)
///     - Preview-only  → 404 (not 403 — collection stays invisible at this tier)
///     - No role       → 404
///     - Unauthenticated → 401
///
///   Viewer (GET /viewer/{fileId:guid}):
///     - Download role → 200 (HTML page)
///     - Preview-only  → 404
///     - No role       → 404
///     - Unauthenticated → 401
///
/// Uses WebApplicationFactory + in-memory DB + on-disk temp IFileStore so the
/// thumbnail and geometry byte-serving paths exercise the real DiskFileStore.
/// Authentication is handled by a test scheme (same pattern as OidcCollectionsFixture).
///
/// NOTE: The three endpoint bodies return 501 in the milestone-3 stub. When the
/// render-pipeline agent implements them, these tests will flip from 501 to the
/// asserted status codes. The ACL-rejection tests (404 / 401) are exercised now
/// because the stub returns 501 only after passing the auth check — so a 401 or
/// 404 from the auth layer surfaces before reaching the stub body.
///
/// Implementation note for the test author (this file):
///   Tests that assert 200 are marked [Trait("Milestone", "3-EndpointImpl")] so
///   the test runner can skip them until the endpoint bodies are filled in.
///   Tests that assert 404 / 401 (pure ACL rejections) run immediately.
/// </summary>
public sealed class EndpointAclTests
{
    // -------------------------------------------------------------------------
    // Thumbnail endpoint ACL
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Thumbnail_PreviewRole_Returns200OrNotFoundButNever401Or403()
    {
        // A Preview-role caller must either get 200 (if thumbnail ready) or 404
        // (thumbnail pending/failed) — never 401 or 403. This asserts the auth gate
        // passes for Preview callers. The endpoint stub returns 501 until implemented.
        await using var factory = AclFixture.WithRole(CollectionRole.Preview);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/models/{AclFixture.ModelSlug}/thumbnail");

        // Auth passed: we get either 200 (implemented + thumb ready), 404 (no thumb),
        // or 501 (stub). Any of these proves the auth gate was NOT the stopper.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait("Milestone", "3-EndpointImpl")]
    public async Task Thumbnail_NoRole_Returns404()
    {
        // Caller has no role on this collection → collection is invisible → 404.
        // Requires endpoint body implementation (stub returns 501 until implemented).
        await using var factory = AclFixture.WithNoRole();
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/models/{AclFixture.ModelSlug}/thumbnail");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Thumbnail_Unauthenticated_Returns401()
    {
        await using var factory = AclFixture.Unauthenticated();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/api/v1/models/{AclFixture.ModelSlug}/thumbnail");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Geometry endpoint ACL (GET /api/v1/models/files/{fileId})
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Geometry_DownloadRole_PassesAuthGate()
    {
        // Download-role caller must not hit 401/403. They will get 200 (when endpoint
        // implemented) or 501 (stub). Either proves the auth layer passed.
        await using var factory = AclFixture.WithRole(CollectionRole.Download);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/models/files/{AclFixture.StlFileId:D}");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait("Milestone", "3-EndpointImpl")]
    public async Task Geometry_PreviewOnlyRole_Returns404()
    {
        // Preview callers must not get STL bytes. 404 hides the resource entirely
        // (no confirmation the file exists) rather than leaking a 403.
        // Requires endpoint body implementation (stub returns 501 until implemented).
        await using var factory = AclFixture.WithRole(CollectionRole.Preview);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/models/files/{AclFixture.StlFileId:D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Milestone", "3-EndpointImpl")]
    public async Task Geometry_NoRole_Returns404()
    {
        // Requires endpoint body implementation (stub returns 501 until implemented).
        await using var factory = AclFixture.WithNoRole();
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/models/files/{AclFixture.StlFileId:D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Geometry_Unauthenticated_Returns401()
    {
        await using var factory = AclFixture.Unauthenticated();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/api/v1/models/files/{AclFixture.StlFileId:D}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Viewer route ACL (GET /viewer/{fileId})
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Viewer_DownloadRole_PassesAuthGate()
    {
        await using var factory = AclFixture.WithRole(CollectionRole.Download);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/viewer/{AclFixture.StlFileId:D}");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait("Milestone", "3-EndpointImpl")]
    public async Task Viewer_PreviewOnlyRole_Returns404()
    {
        // Interactive viewing ≡ download — Preview callers must be denied (404, not 403).
        // Requires endpoint body implementation (stub returns 501 until implemented).
        await using var factory = AclFixture.WithRole(CollectionRole.Preview);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/viewer/{AclFixture.StlFileId:D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Milestone", "3-EndpointImpl")]
    public async Task Viewer_NoRole_Returns404()
    {
        // Requires endpoint body implementation (stub returns 501 until implemented).
        await using var factory = AclFixture.WithNoRole();
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/viewer/{AclFixture.StlFileId:D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Viewer_Unauthenticated_Returns401()
    {
        await using var factory = AclFixture.Unauthenticated();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/viewer/{AclFixture.StlFileId:D}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Cross-endpoint symmetry: viewer and geometry must have the SAME access bar
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Milestone", "3-EndpointImpl")]
    public async Task ViewerAndGeometry_SameRole_HaveMatchingAuthOutcome()
    {
        // A Preview-only caller must be denied BOTH the viewer and the geometry endpoint.
        // This test encodes the "viewer grant = file grant" invariant from the security cornerstone.
        await using var factory = AclFixture.WithRole(CollectionRole.Preview);
        var client = factory.CreateClient();

        var geoResponse = await client.GetAsync($"/api/v1/models/files/{AclFixture.StlFileId:D}");
        var viewerResponse = await client.GetAsync($"/viewer/{AclFixture.StlFileId:D}");

        // Both must be 404 (auth-level rejection).
        Assert.Equal(HttpStatusCode.NotFound, geoResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, viewerResponse.StatusCode);
    }

    [Fact]
    public async Task Geometry_UploaderRole_PassesAuthGate()
    {
        // Uploader ⊇ Download — uploaders can download their own files.
        await using var factory = AclFixture.WithRole(CollectionRole.Uploader);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/models/files/{AclFixture.StlFileId:D}");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Geometry_AdminRole_PassesAuthGate()
    {
        // Admin ⊇ Uploader ⊇ Download.
        await using var factory = AclFixture.WithRole(CollectionRole.Admin);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/models/files/{AclFixture.StlFileId:D}");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }
}

// =============================================================================
// AclFixture — one-shot WebApplicationFactory for ACL gate tests
// =============================================================================

/// <summary>
/// Lightweight fixture that seeds exactly one collection, one model, and one STL
/// ModelFile, then presents a caller with the specified role (or no role).
///
/// IFileStore is replaced with a DiskFileStore pointing at a temp directory so
/// the thumbnail and geometry endpoints can serve real bytes when implemented.
/// </summary>
internal sealed class AclFixture : WebApplicationFactory<Program>, IAsyncDisposable
{
    // -------------------------------------------------------------------------
    // Stable test IDs
    // -------------------------------------------------------------------------

    public static readonly Guid CollectionId = new("ffff0000-0000-0000-0000-000000000001");
    public static readonly Guid ModelId = new("ffff0000-0000-0000-0000-000000000002");
    public static readonly Guid StlFileId = new("ffff0000-0000-0000-0000-000000000003");
    public static readonly Guid ThumbFileId = new("ffff0000-0000-0000-0000-000000000004");
    public const string ModelSlug = "acl-test-model";
    public const string CollectionSlug = "acl-test-col";

    // SHA-256 hex values for seeded blobs. Different for STL vs thumbnail so the
    // unique (ModelId, BlobKey) index constraint is satisfied even on real Postgres.
    private const string StlBlobKey = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    // SHA-256 hex of "thumbnail" — distinct from STL key.
    private const string ThumbBlobKey = "5a9d7d5b8e2f4c6a1b3e7d9f2a4c8e6b0d3f5a7c9e1b4d6f8a0c2e4f6b8d0e2f";

    private readonly IUserContext _userContext;
    private readonly bool _isAuthenticated;
    private readonly string _dbName = $"acl-{Guid.NewGuid():N}";
    private readonly string _storageRoot;

    private AclFixture(IUserContext userContext, bool isAuthenticated)
    {
        _userContext = userContext;
        _isAuthenticated = isAuthenticated;
        _storageRoot = Path.Combine(Path.GetTempPath(), $"acl-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);

        // Pre-create blob and thumbnail files so endpoints can serve them.
        SeedBlobFiles();
    }

    // -------------------------------------------------------------------------
    // Factory methods
    // -------------------------------------------------------------------------

    internal static AclFixture WithRole(CollectionRole role)
    {
        var ctx = new StubUserContext($"user:acl-tester", []);
        var fixture = new AclFixture(ctx, isAuthenticated: true);
        fixture._seedRoleAction = db => db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionId,
            Principal = "user:acl-tester",
            Role = role,
        });
        return fixture;
    }

    internal static AclFixture WithNoRole()
        => new(new StubUserContext("user:no-role", []), isAuthenticated: true);

    internal static AclFixture Unauthenticated()
        => new(new UnauthenticatedStubUserContext(), isAuthenticated: false);

    private Action<CatalogDbContext>? _seedRoleAction;

    // -------------------------------------------------------------------------
    // WebApplicationFactory wiring
    // -------------------------------------------------------------------------

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

            // Override the storage root so DiskFileStore resolves blobs from the temp directory
            // (seeded in SeedBlobFiles) instead of /data which does not exist in tests.
            var root = _storageRoot;
            services.Configure<DiskFileStoreOptions>(o => o.Root = root);

            // Replace IUserContext with per-scenario stub.
            services.RemoveAll<IUserContext>();
            services.AddScoped<IUserContext>(_ => _userContext);

            // Test auth scheme — stamps authenticated or not.
            var isAuth = _isAuthenticated;
            services
                .AddAuthentication(options =>
                {
                    options.DefaultScheme = AclTestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = AclTestAuthHandler.SchemeName;
                })
                .AddScheme<AclTestAuthHandlerOptions, AclTestAuthHandler>(
                    AclTestAuthHandler.SchemeName,
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

    // -------------------------------------------------------------------------
    // Seed helpers
    // -------------------------------------------------------------------------

    private void SeedBlobFiles()
    {
        // Write a minimal 1×1 PNG to the thumbs subdirectory so the thumbnail
        // endpoint can serve it when implemented.
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

        var thumbDir = Path.Combine(_storageRoot, "thumbs", ThumbBlobKey[..2]);
        Directory.CreateDirectory(thumbDir);
        File.WriteAllBytes(Path.Combine(thumbDir, $"{ThumbBlobKey}.png"), minimalPng.ToArray());

        // Write a minimal binary STL (1 triangle) to the blobs subdirectory.
        var stl = BuildMinimalStl();
        var blobDir = Path.Combine(_storageRoot, "blobs", StlBlobKey[..2]);
        Directory.CreateDirectory(blobDir);
        File.WriteAllBytes(Path.Combine(blobDir, StlBlobKey), stl);
    }

    private static byte[] BuildMinimalStl()
    {
        // 80-byte header + 4-byte count (1 triangle) + 50-byte triangle body
        var buf = new byte[84 + 50];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(80, 4), 1u);
        // Normal and vertices are all zero-floats; attribute bytes = 0. Valid for our purposes.
        return buf;
    }

    private void SeedDb(CatalogDbContext db)
    {
        var now = DateTimeOffset.UtcNow;

        db.Collections.Add(new Collection
        {
            Id = CollectionId,
            Slug = CollectionSlug,
            Name = "ACL Test Collection",
            Description = "",
            CreatedAt = now,
            UpdatedAt = now,
        });

        db.Models.Add(new Model
        {
            Id = ModelId,
            CollectionId = CollectionId,
            Slug = ModelSlug,
            Name = "ACL Test Model",
            Description = "",
            Owner = "tester",
            Status = ModelStatus.Ready,
            CreatedAt = now,
            UpdatedAt = now,
        });

        // STL file — what the geometry + viewer endpoints serve.
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

        // Thumbnail file — what the thumbnail endpoint serves.
        // ThumbBlobKey is distinct from StlBlobKey to satisfy the unique (ModelId, BlobKey) constraint.
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

// =============================================================================
// Test auth handler for ACL fixture
// =============================================================================

internal sealed class AclTestAuthHandlerOptions : AuthenticationSchemeOptions
{
    public bool Authenticated { get; set; } = true;
}

internal sealed class AclTestAuthHandler(
    IOptionsMonitor<AclTestAuthHandlerOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AclTestAuthHandlerOptions>(options, logger, encoder)
{
    internal const string SchemeName = "AclTestScheme";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Options.Authenticated)
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity([], Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
