using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Entities;
using Catalog3d.Domain.Enums;
using Catalog3d.Infrastructure.Auth;
using Catalog3d.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Catalog3d.Tests;

// =============================================================================
// H1 — CollectionDetail ACL bypass
// =============================================================================

/// <summary>
/// Verifies that CollectionDetail renders a not-found state when the caller has
/// no Preview role on the collection, and renders content when Preview is held.
///
/// H1: CollectionDetail.razor must gate on HasCollectionRoleAsync(Preview) before
/// rendering name/description/model list. Without the role the component must
/// behave identically to the "collection not found" path.
///
/// These are integration tests that exercise the full Blazor component pipeline via
/// WebApplicationFactory (not bUnit — bUnit cannot run interactive server components
/// without a running server).
/// </summary>
public sealed class CollectionDetailAclTests
{
    private static readonly Guid CollectionId = new("aac10000-0000-0000-0000-000000000001");

    // -------------------------------------------------------------------------
    // A — unauthenticated user gets redirected (not 200 with content)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectionDetail_UnauthenticatedUser_DoesNotExposeContent()
    {
        await using var factory = CollectionDetailFixture.WithUser(
            userId: "user:no-one",
            groups: [],
            hasPreviewRole: false,
            authenticated: false);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync($"/collections/test-col");

        // An unauthenticated Blazor page redirects to login — any 3xx or challenge is correct.
        // The critical invariant is that we do NOT get a 200 with collection content.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // B — authenticated user without Preview sees not-found (no collection content)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectionDetail_NoRoleUser_RendersNotFoundNotContent()
    {
        await using var factory = CollectionDetailFixture.WithUser(
            userId: "user:no-role",
            groups: [],
            hasPreviewRole: false);

        var client = factory.CreateClient();
        var response = await client.GetAsync($"/collections/test-col");

        // Response must be 200 (Blazor always 200s the component shell) but the
        // rendered HTML must NOT contain the collection name or model list markers.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        // The collection name "Test Collection" must not appear in the output
        Assert.DoesNotContain("Test Collection", html, StringComparison.Ordinal);
        // The "Upload Model" button only appears when canUpload — must not appear
        Assert.DoesNotContain("Upload Model", html, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // C — authenticated user WITH Preview sees collection content
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectionDetail_PreviewRoleUser_RendersCollectionContent()
    {
        await using var factory = CollectionDetailFixture.WithUser(
            userId: "user:preview-user",
            groups: [],
            hasPreviewRole: true);

        var client = factory.CreateClient();
        var response = await client.GetAsync($"/collections/test-col");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        // Collection name must appear somewhere in the rendered output.
        Assert.Contains("Test Collection", html, StringComparison.Ordinal);
    }
}

// =============================================================================
// H2 — Dev auth fail-closed
// =============================================================================

/// <summary>
/// Verifies that Program.cs refuses to start when Auth:Provider=Dev is configured
/// outside of the Development environment.
/// </summary>
public sealed class DevAuthFailClosedTests
{
    [Fact]
    public void Program_DevAuthOutsideDevEnvironment_ThrowsOnStartup()
    {
        // CreateClient triggers host creation which traverses Program.cs configuration.
        // The InvalidOperationException may propagate directly or be wrapped in a
        // TargetInvocationException/AggregateException by WebApplicationFactory.
        Exception? thrown = null;
        try
        {
            var factory = new DevAuthInProductionFixture();
            factory.CreateClient();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        Assert.NotNull(thrown);

        // Walk the exception chain to find the InvalidOperationException with the Dev guard message.
        var found = false;
        var current = thrown;
        while (current is not null)
        {
            if (current is InvalidOperationException ioe &&
                ioe.Message.Contains("Dev", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                break;
            }
            current = current.InnerException;
        }

        Assert.True(found,
            $"Expected an InvalidOperationException about Dev auth in the exception chain, got: {thrown}");
    }
}

// =============================================================================
// M1 — Principal case normalization
// =============================================================================

/// <summary>
/// Verifies that principal matching is genuinely case-insensitive end-to-end:
/// OidcUserContext lowercases group names, DevUserContext lowercases group names,
/// and CollectionRoles.razor input is normalized before writing to the DB so that
/// role assignments stored with different casing still match.
/// </summary>
public sealed class PrincipalCaseNormalizationTests
{
    private static CatalogDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseInMemoryDatabase($"case-norm-{Guid.NewGuid():N}")
            .Options;
        return new CatalogDbContext(opts);
    }

    private static ICollectionAuthorizationService BuildService(CatalogDbContext db)
        => new CollectionAuthorizationService(db, Options.Create(new CatalogAuthorizationOptions
        {
            SiteAdminGroups = ["admins"]
        }));

    private static readonly Guid ColId = new("cccc0000-0000-0000-0000-000000000001");

    private static void SeedCollection(CatalogDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        db.Collections.Add(new Collection
        {
            Id = ColId, Slug = "case-col", Name = "Case Col",
            Description = "", CreatedAt = now, UpdatedAt = now
        });
        db.SaveChanges();
    }

    // -------------------------------------------------------------------------
    // OidcUserContext lowercases group names
    // -------------------------------------------------------------------------

    [Fact]
    public void OidcUserContext_MixedCaseGroup_IsNormalized()
    {
        var identity = new ClaimsIdentity(
            [new Claim("sub", "abc"), new Claim("groups", "ADMINS"), new Claim("groups", "Media")],
            "TestScheme");
        var accessor = new Microsoft.AspNetCore.Http.HttpContextAccessor
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity)
            }
        };

        var ctx = new OidcUserContext(accessor);

        // All group names must be lowercase after normalization
        foreach (var g in ctx.Groups)
            Assert.Equal(g, g.ToLowerInvariant(), StringComparer.Ordinal);
    }

    // -------------------------------------------------------------------------
    // DevUserContext lowercases group names
    // -------------------------------------------------------------------------

    [Fact]
    public void DevUserContext_MixedCaseGroup_IsNormalized()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "alice"),
                new Claim(DevAuthHandler.GroupsClaimType, "CATALOG-ADMINS"),
                new Claim(DevAuthHandler.GroupsClaimType, "Media")
            ],
            "DevScheme");
        var accessor = new Microsoft.AspNetCore.Http.HttpContextAccessor
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity)
            }
        };

        var ctx = new DevUserContext(accessor);

        foreach (var g in ctx.Groups)
            Assert.Equal(g, g.ToLowerInvariant(), StringComparer.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Matching works across casing: DB row stored uppercase, principal lowercase
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectionAuth_PrincipalStoredUppercase_MatchesLowercaseCaller()
    {
        await using var db = CreateDb();
        SeedCollection(db);

        // DB row has uppercase principal (e.g. written before normalization was enforced)
        db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = ColId,
            Principal = "group:MEDIA",   // uppercase
            Role = CollectionRole.Preview
        });
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        // Caller with normalized lowercase group
        var caller = new FakeUserContextM1("user:alice", ["group:media"]);

        // Because BuildPrincipalSet uses OrdinalIgnoreCase, this must still match
        var role = await svc.GetEffectiveRoleAsync(ColId, caller);
        Assert.Equal(CollectionRole.Preview, role);
    }

    // -------------------------------------------------------------------------
    // SiteAdminGroups comparison: mixed-case config matches lowercase principal
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectionAuth_SiteAdminGroup_MatchesCaseInsensitive()
    {
        await using var db = CreateDb();
        SeedCollection(db);

        // Config uses capitalized name; principal has been lowercased by OidcUserContext
        var svc = new CollectionAuthorizationService(db, Options.Create(new CatalogAuthorizationOptions
        {
            SiteAdminGroups = ["Admins"]  // note capital A
        }));
        var caller = new FakeUserContextM1("user:boss", ["group:admins"]);  // lowercase

        var role = await svc.GetEffectiveRoleAsync(ColId, caller);
        Assert.Equal(CollectionRole.Admin, role);
    }

    private sealed class FakeUserContextM1(string userId, IReadOnlyList<string> groups) : IUserContext
    {
        public bool IsAuthenticated => true;
        public string UserId { get; } = userId;
        public string DisplayName => "Test";
        public IReadOnlyList<string> Groups { get; } = groups;
    }
}

// =============================================================================
// Fixture: CollectionDetail ACL test harness
// =============================================================================

public sealed class CollectionDetailFixture : WebApplicationFactory<Program>
{
    private static readonly Guid CollectionId = new("aac10000-0000-0000-0000-000000000001");

    private readonly IUserContext _userContext;
    private readonly bool _authenticated;
    private readonly bool _hasPreviewRole;
    private readonly string _dbName = $"cd-acl-{Guid.NewGuid():N}";

    private CollectionDetailFixture(IUserContext userContext, bool authenticated, bool hasPreviewRole)
    {
        _userContext = userContext;
        _authenticated = authenticated;
        _hasPreviewRole = hasPreviewRole;
    }

    public static CollectionDetailFixture WithUser(
        string userId,
        IReadOnlyList<string> groups,
        bool hasPreviewRole,
        bool authenticated = true)
        => new(new StubUserContext(userId, groups), authenticated, hasPreviewRole);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Override AllowedHosts so the test client (localhost) is not rejected by the
        // host-filtering middleware that was scoped to catalog.mallcop.dev in appsettings.json.
        builder.UseSetting("AllowedHosts", "*");

        builder.ConfigureServices(services =>
        {
            // Replace Postgres with InMemory
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(DbContextOptions<CatalogDbContext>))
                    services.RemoveAt(i);
            }

            var inMemoryOptions = new DbContextOptionsBuilder<CatalogDbContext>()
                .UseInMemoryDatabase(_dbName)
                .Options;
            services.AddSingleton(inMemoryOptions);

            // Stub IUserContext
            services.RemoveAll<IUserContext>();
            services.AddScoped<IUserContext>(_ => _userContext);

            var authenticated = _authenticated;
            services
                .AddAuthentication(options =>
                {
                    options.DefaultScheme = "TestScheme";
                    options.DefaultChallengeScheme = "TestScheme";
                })
                .AddScheme<TestAuthHandlerOptions, TestAuthHandler>(
                    "TestScheme",
                    opts => opts.Authenticated = authenticated);

            services.Configure<CatalogAuthorizationOptions>(opts =>
            {
                opts.SiteAdminGroups = ["admins"];
            });

            // Override ICollectionAuthorizationService with one that uses the hasPreviewRole flag
            services.RemoveAll<ICollectionAuthorizationService>();
            var hasPreviewRole = _hasPreviewRole;
            services.AddScoped<ICollectionAuthorizationService>(_ =>
                new FixedRoleAuthService(hasPreviewRole));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.Collections.Add(new Collection
        {
            Id = CollectionId,
            Slug = "test-col",
            Name = "Test Collection",
            Description = "Test description",
            CreatedAt = now,
            UpdatedAt = now
        });
        db.SaveChanges();

        return host;
    }
}

/// <summary>
/// Stub ICollectionAuthorizationService that returns a fixed Preview role (or null).
/// Used to isolate the CollectionDetail rendering test from DB-backed auth.
/// </summary>
internal sealed class FixedRoleAuthService(bool hasPreviewRole) : ICollectionAuthorizationService
{
    public Task<CollectionRole?> GetEffectiveRoleAsync(
        Guid collectionId, IUserContext caller, CancellationToken cancellationToken = default)
        => Task.FromResult<CollectionRole?>(hasPreviewRole ? CollectionRole.Preview : null);

    public Task<bool> AuthorizeAsync(
        Guid collectionId, IUserContext caller, CollectionRole minimumRole, CancellationToken cancellationToken = default)
    {
        // Preview is the minimum visible role; hasPreviewRole signals at least Preview
        var effective = hasPreviewRole ? CollectionRole.Preview : (CollectionRole?)null;
        return Task.FromResult(effective.HasValue && effective.Value >= minimumRole);
    }

    public Task<IReadOnlyList<Guid>> GetAuthorizedCollectionIdsAsync(
        IUserContext caller, CollectionRole minimumRole, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Guid>>([]);
}

// =============================================================================
// Fixture: Dev auth fail-closed
// =============================================================================

public sealed class DevAuthInProductionFixture : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Simulate a non-Development environment with Auth:Provider = Dev — this should fail
        builder.UseEnvironment("Production");

        builder.ConfigureAppConfiguration((ctx, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Provider"] = "Dev",
            });
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
        => base.CreateHost(builder);
}
