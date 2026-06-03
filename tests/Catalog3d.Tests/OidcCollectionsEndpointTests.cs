using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Entities;
using Catalog3d.Domain.Enums;
using Catalog3d.Infrastructure.Auth;
using Catalog3d.Infrastructure.Persistence;
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
/// Integration tests for GET /api/v1/collections under the OIDC principal
/// convention. Uses WebApplicationFactory with an in-memory database and a
/// stub IUserContext + test auth handler so no real OIDC server is needed.
///
/// The auth pipeline is replaced with a no-op handler that always marks the
/// request as authenticated (stamping a minimal ClaimsPrincipal so ASP.NET Core
/// middleware is satisfied). The actual identity comes from the IUserContext stub,
/// which is what the endpoint handlers consume.
///
/// Scenarios:
///   A. User matched by "user:&lt;sub&gt;" direct assignment → sees assigned collections.
///   B. User matched by "group:&lt;name&gt;" assignment → sees group-assigned collections.
///   C. SiteAdminGroups bootstrap: "group:admins" → sees ALL collections.
///   D. User with no assignment → empty array (200).
///   E. Unauthenticated user → 401.
/// </summary>
public sealed class OidcCollectionsEndpointTests
{
    // -------------------------------------------------------------------------
    // Shared seed data GUIDs
    // -------------------------------------------------------------------------

    private static readonly Guid ColPublic = new("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid ColRestricted = new("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid ColHidden = new("cccccccc-0000-0000-0000-000000000003");

    // Simulate Authelia-style UUID subs
    private const string SubDirect = "d1000000-0000-0000-0000-000000000001";
    private const string SubGroupOnly = "d2000000-0000-0000-0000-000000000002";
    private const string SubSiteAdmin = "d3000000-0000-0000-0000-000000000003";
    private const string SubNoAccess = "d4000000-0000-0000-0000-000000000004";

    // -------------------------------------------------------------------------
    // A — direct user assignment
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetCollections_DirectUserAssignment_ReturnsOnlyAssignedCollections()
    {
        await using var factory = OidcCollectionsFixture.Authenticated(
            userId: $"user:{SubDirect}",
            groups: ["group:games"]);

        factory.SeedAction = db =>
        {
            db.RoleAssignments.AddRange(
                new RoleAssignment { CollectionId = ColPublic, Principal = $"user:{SubDirect}", Role = CollectionRole.Preview },
                new RoleAssignment { CollectionId = ColRestricted, Principal = "group:games", Role = CollectionRole.Download });
        };

        var slugs = await GetSlugSetAsync(factory);
        Assert.Contains("col-public", slugs);
        Assert.Contains("col-restricted", slugs);
        Assert.DoesNotContain("col-hidden", slugs);
    }

    [Fact]
    public async Task GetCollections_DirectUserAssignment_ExcludesGroupCollectionWhenNotMember()
    {
        await using var factory = OidcCollectionsFixture.Authenticated(
            userId: $"user:{SubDirect}",
            groups: []); // not in games

        factory.SeedAction = db =>
        {
            db.RoleAssignments.AddRange(
                new RoleAssignment { CollectionId = ColPublic, Principal = $"user:{SubDirect}", Role = CollectionRole.Preview },
                new RoleAssignment { CollectionId = ColRestricted, Principal = "group:games", Role = CollectionRole.Download });
        };

        var slugs = await GetSlugSetAsync(factory);
        Assert.Contains("col-public", slugs);
        Assert.DoesNotContain("col-restricted", slugs);
    }

    // -------------------------------------------------------------------------
    // B — group assignment only
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetCollections_GroupAssignment_ReturnsMemberCollections()
    {
        await using var factory = OidcCollectionsFixture.Authenticated(
            userId: $"user:{SubGroupOnly}",
            groups: ["group:media"]);

        factory.SeedAction = db =>
        {
            db.RoleAssignments.AddRange(
                new RoleAssignment { CollectionId = ColPublic, Principal = "group:media", Role = CollectionRole.Preview },
                new RoleAssignment { CollectionId = ColRestricted, Principal = "group:games", Role = CollectionRole.Download });
        };

        var slugs = await GetSlugSetAsync(factory);
        Assert.Contains("col-public", slugs);
        Assert.DoesNotContain("col-restricted", slugs);
        Assert.DoesNotContain("col-hidden", slugs);
    }

    // -------------------------------------------------------------------------
    // C — SiteAdminGroups bootstrap
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetCollections_SiteAdminGroup_ReturnsAllCollections()
    {
        await using var factory = OidcCollectionsFixture.Authenticated(
            userId: $"user:{SubSiteAdmin}",
            groups: ["group:admins"]);

        // No DB rows — admin bootstrap must surface all three collections.
        factory.SeedAction = _ => { };

        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/collections");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dtos = await response.Content.ReadFromJsonAsync<CollectionDto[]>();
        Assert.NotNull(dtos);

        var slugs = dtos.Select(d => d.Slug).ToHashSet();
        Assert.Contains("col-public", slugs);
        Assert.Contains("col-restricted", slugs);
        Assert.Contains("col-hidden", slugs);
    }

    // -------------------------------------------------------------------------
    // D — no assignment
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetCollections_NoAssignment_ReturnsEmptyArray()
    {
        await using var factory = OidcCollectionsFixture.Authenticated(
            userId: $"user:{SubNoAccess}",
            groups: []);

        factory.SeedAction = db =>
        {
            db.RoleAssignments.Add(
                new RoleAssignment { CollectionId = ColPublic, Principal = "user:someone-else", Role = CollectionRole.Preview });
        };

        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/collections");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dtos = await response.Content.ReadFromJsonAsync<CollectionDto[]>();
        Assert.NotNull(dtos);
        Assert.Empty(dtos);
    }

    // -------------------------------------------------------------------------
    // E — unauthenticated
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetCollections_Unauthenticated_Returns401()
    {
        await using var factory = OidcCollectionsFixture.Unauthenticated();

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var response = await client.GetAsync("/api/v1/collections");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Helper
    // -------------------------------------------------------------------------

    private static async Task<HashSet<string>> GetSlugSetAsync(OidcCollectionsFixture factory)
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/collections");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dtos = await response.Content.ReadFromJsonAsync<CollectionDto[]>();
        Assert.NotNull(dtos);
        return dtos.Select(d => d.Slug).ToHashSet();
    }
}

// =============================================================================
// Fixture
// =============================================================================

/// <summary>
/// One-shot WebApplicationFactory for OIDC principal scenarios.
///
/// Authentication is handled by a lightweight TestAuthHandler that always
/// succeeds (for authenticated scenarios) or always returns NoResult
/// (for unauthenticated). The real identity data is injected via IUserContext.
///
/// This decouples the auth pipeline (which requires cookie/OIDC middleware) from
/// the identity data the endpoint handlers actually consume.
/// </summary>
public sealed class OidcCollectionsFixture : WebApplicationFactory<Program>, IAsyncDisposable
{
    internal const string TestScheme = "TestScheme";

    private readonly IUserContext _userContext;
    private readonly bool _authenticated;
    private readonly string _dbName = $"oidc-col-{Guid.NewGuid():N}";

    public Action<CatalogDbContext>? SeedAction { get; set; }

    private OidcCollectionsFixture(IUserContext userContext, bool authenticated)
    {
        _userContext = userContext;
        _authenticated = authenticated;
    }

    public static OidcCollectionsFixture Authenticated(string userId, IReadOnlyList<string> groups)
        => new(new StubUserContext(userId, groups), authenticated: true);

    public static OidcCollectionsFixture Unauthenticated()
        => new(new UnauthenticatedStubUserContext(), authenticated: false);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // AllowedHosts is scoped to catalog.mallcop.dev in appsettings.json; override
        // for test clients which send requests to localhost.
        builder.UseSetting("AllowedHosts", "*");

        builder.ConfigureServices(services =>
        {
            // Swap Postgres for InMemory.
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(DbContextOptions<CatalogDbContext>))
                    services.RemoveAt(i);
            }

            var inMemoryOptions = new DbContextOptionsBuilder<CatalogDbContext>()
                .UseInMemoryDatabase(_dbName)
                .Options;
            services.AddSingleton(inMemoryOptions);

            // Replace IUserContext with the per-scenario stub.
            services.RemoveAll<IUserContext>();
            services.AddScoped<IUserContext>(_ => _userContext);

            // Replace the authentication pipeline with a test handler so
            // ASP.NET Core's RequireAuthorization() sees an authenticated (or not)
            // ClaimsPrincipal without needing cookie sessions or OIDC discovery.
            //
            // We must remove ALL authentication services first, then re-register
            // only the test scheme. The Dev/OIDC registrations happen before this
            // ConfigureServices callback, so removal is safe here.
            // Rather than surgically removing Dev/OIDC scheme registrations, add the test
            // scheme on top and set it as the default — it overrides scheme selection.
            // AddAuthentication reconfigures AuthenticationOptions defaults; existing
            // scheme handler registrations remain but are bypassed when the default changes.

            var authenticated = _authenticated;
            services
                .AddAuthentication(options =>
                {
                    options.DefaultScheme = TestScheme;
                    options.DefaultChallengeScheme = TestScheme;
                })
                .AddScheme<TestAuthHandlerOptions, TestAuthHandler>(
                    TestScheme,
                    opts => opts.Authenticated = authenticated);

            // Ensure SiteAdminGroups uses the default ("admins") so the downstream
            // authorization agent can read it from IOptions<CatalogAuthorizationOptions>.
            services.Configure<CatalogAuthorizationOptions>(opts =>
            {
                opts.SiteAdminGroups = ["admins"];
            });
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        SeedBaseCollections(db);
        SeedAction?.Invoke(db);
        db.SaveChanges();

        return host;
    }

    private static void SeedBaseCollections(CatalogDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        db.Collections.AddRange(
            new Collection
            {
                Id = new Guid("cccccccc-0000-0000-0000-000000000001"),
                Slug = "col-public",
                Name = "Public",
                Description = "",
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Collection
            {
                Id = new Guid("cccccccc-0000-0000-0000-000000000002"),
                Slug = "col-restricted",
                Name = "Restricted",
                Description = "",
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Collection
            {
                Id = new Guid("cccccccc-0000-0000-0000-000000000003"),
                Slug = "col-hidden",
                Name = "Hidden",
                Description = "",
                CreatedAt = now,
                UpdatedAt = now,
            });
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        Dispose();
        await ValueTask.CompletedTask;
    }
}

// =============================================================================
// Test authentication handler — stamps HttpContext.User as authenticated or not
// =============================================================================

public sealed class TestAuthHandlerOptions : AuthenticationSchemeOptions
{
    public bool Authenticated { get; set; } = true;
}

public sealed class TestAuthHandler(
    IOptionsMonitor<TestAuthHandlerOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<TestAuthHandlerOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Options.Authenticated)
            return Task.FromResult(AuthenticateResult.NoResult());

        // Minimal identity — the endpoint handlers read from IUserContext, not claims.
        var identity = new ClaimsIdentity([], Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

// =============================================================================
// Stub IUserContext implementations
// =============================================================================

/// <summary>
/// Authenticated stub carrying OIDC-convention principals.
/// UserId is "user:&lt;sub&gt;" and Groups entries are "group:&lt;name&gt;".
/// </summary>
internal sealed class StubUserContext(string userId, IReadOnlyList<string> groups) : IUserContext
{
    public bool IsAuthenticated => true;
    public string UserId { get; } = userId;
    public string DisplayName => "Test User";
    public IReadOnlyList<string> Groups { get; } = groups;
}

/// <summary>
/// Unauthenticated stub. CollectionAuthorizationService short-circuits to null/empty
/// when IsAuthenticated = false, consistent with the service's own early-return logic.
/// </summary>
internal sealed class UnauthenticatedStubUserContext : IUserContext
{
    public bool IsAuthenticated => false;
    public string UserId => string.Empty;
    public string DisplayName => string.Empty;
    public IReadOnlyList<string> Groups => Array.Empty<string>();
}
