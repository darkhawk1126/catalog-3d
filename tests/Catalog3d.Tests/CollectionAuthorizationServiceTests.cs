using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Entities;
using Catalog3d.Domain.Enums;
using Catalog3d.Infrastructure.Auth;
using Catalog3d.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Catalog3d.Tests;

/// <summary>
/// Unit tests for CollectionAuthorizationService using the OIDC principal
/// convention ("user:&lt;sub&gt;" and "group:&lt;name&gt;" RoleAssignment rows).
///
/// Uses EF Core InMemory provider — no real Postgres required.
/// Each test gets its own named database to guarantee isolation.
///
/// Coverage:
///   1. User matched by direct "user:&lt;sub&gt;" assignment.
///   2. User matched by "group:&lt;name&gt;" assignment.
///   3. User with both user and group assignment gets the higher role.
///   4. Unauthenticated caller gets null everywhere / empty list.
///   5. No assignment → null / invisible.
///   6. SiteAdminGroups bootstrap: caller in admins group → Admin on every
///      collection without a RoleAssignment row.  (Tests #6 are marked with
///      a trait so the integration phase can isolate them; they will FAIL until
///      the downstream authorization agent implements the short-circuit.)
///   7. GetAuthorizedCollectionIdsAsync returns only collections meeting
///      the minimum role bar, filtered by OIDC-convention principals.
/// </summary>
public sealed class CollectionAuthorizationServiceTests
{
    // -------------------------------------------------------------------------
    // Infrastructure helpers
    // -------------------------------------------------------------------------

    private static CatalogDbContext CreateDb(string? name = null)
    {
        var opts = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseInMemoryDatabase(name ?? $"authz-{Guid.NewGuid():N}")
            .Options;
        return new CatalogDbContext(opts);
    }

    /// <summary>
    /// Builds a CollectionAuthorizationService wired to the supplied db.
    /// Supplies default CatalogAuthorizationOptions (SiteAdminGroups = ["admins"])
    /// matching the production default so site-admin short-circuit tests exercise
    /// the live implementation path.
    /// </summary>
    private static ICollectionAuthorizationService BuildService(CatalogDbContext db)
    {
        var opts = Options.Create(new CatalogAuthorizationOptions
        {
            SiteAdminGroups = ["admins"],
        });
        return new CollectionAuthorizationService(db, opts);
    }

    private sealed class FakeUser(string userId, IReadOnlyList<string> groups) : IUserContext
    {
        public bool IsAuthenticated => true;
        public string UserId { get; } = userId;
        public string DisplayName => "Test User";
        public IReadOnlyList<string> Groups { get; } = groups;
    }

    private sealed class UnauthenticatedUser : IUserContext
    {
        public bool IsAuthenticated => false;
        public string UserId => string.Empty;
        public string DisplayName => string.Empty;
        public IReadOnlyList<string> Groups => Array.Empty<string>();
    }

    private static readonly Guid CollectionA = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid CollectionB = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid CollectionC = new("bbbbbbbb-0000-0000-0000-000000000003");

    private static void SeedCollections(CatalogDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        db.Collections.AddRange(
            new Collection { Id = CollectionA, Slug = "col-a", Name = "A", Description = "", CreatedAt = now, UpdatedAt = now },
            new Collection { Id = CollectionB, Slug = "col-b", Name = "B", Description = "", CreatedAt = now, UpdatedAt = now },
            new Collection { Id = CollectionC, Slug = "col-c", Name = "C", Description = "", CreatedAt = now, UpdatedAt = now });
        db.SaveChanges();
    }

    // -------------------------------------------------------------------------
    // 1. Direct user assignment ("user:<sub>")
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetEffectiveRole_UserDirectAssignment_ReturnsAssignedRole()
    {
        await using var db = CreateDb();
        SeedCollections(db);
        db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionA,
            Principal = "user:sub-alice",
            Role = CollectionRole.Download,
        });
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var caller = new FakeUser("user:sub-alice", []);

        var role = await svc.GetEffectiveRoleAsync(CollectionA, caller);

        Assert.Equal(CollectionRole.Download, role);
    }

    [Fact]
    public async Task AuthorizeAsync_UserDirectAssignment_TrueForEqualRole()
    {
        await using var db = CreateDb();
        SeedCollections(db);
        db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionA,
            Principal = "user:sub-bob",
            Role = CollectionRole.Preview,
        });
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var caller = new FakeUser("user:sub-bob", []);

        Assert.True(await svc.AuthorizeAsync(CollectionA, caller, CollectionRole.Preview));
        Assert.False(await svc.AuthorizeAsync(CollectionA, caller, CollectionRole.Download));
    }

    // -------------------------------------------------------------------------
    // 2. Group assignment ("group:<name>")
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetEffectiveRole_GroupAssignment_ReturnsRoleForMember()
    {
        await using var db = CreateDb();
        SeedCollections(db);
        db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionB,
            Principal = "group:media",
            Role = CollectionRole.Uploader,
        });
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        // caller.Groups uses the "group:" prefix convention
        var caller = new FakeUser("user:sub-carol", ["group:media"]);

        var role = await svc.GetEffectiveRoleAsync(CollectionB, caller);

        Assert.Equal(CollectionRole.Uploader, role);
    }

    [Fact]
    public async Task GetEffectiveRole_GroupAssignment_NonMemberGetsNull()
    {
        await using var db = CreateDb();
        SeedCollections(db);
        db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionB,
            Principal = "group:media",
            Role = CollectionRole.Uploader,
        });
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var caller = new FakeUser("user:sub-dave", ["group:games"]); // not in media

        var role = await svc.GetEffectiveRoleAsync(CollectionB, caller);

        Assert.Null(role);
    }

    // -------------------------------------------------------------------------
    // 3. User and group assignments — effective role is the maximum
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetEffectiveRole_BothUserAndGroup_ReturnsHigherRole()
    {
        await using var db = CreateDb();
        SeedCollections(db);
        db.RoleAssignments.AddRange(
            new RoleAssignment { CollectionId = CollectionA, Principal = "user:sub-eve", Role = CollectionRole.Preview },
            new RoleAssignment { CollectionId = CollectionA, Principal = "group:admins", Role = CollectionRole.Admin });
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var caller = new FakeUser("user:sub-eve", ["group:admins"]);

        var role = await svc.GetEffectiveRoleAsync(CollectionA, caller);

        Assert.Equal(CollectionRole.Admin, role);
    }

    // -------------------------------------------------------------------------
    // 4. Unauthenticated caller
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetEffectiveRole_Unauthenticated_ReturnsNull()
    {
        await using var db = CreateDb();
        SeedCollections(db);
        db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionA,
            Principal = "user:sub-frank",
            Role = CollectionRole.Preview,
        });
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var role = await svc.GetEffectiveRoleAsync(CollectionA, new UnauthenticatedUser());

        Assert.Null(role);
    }

    [Fact]
    public async Task GetAuthorizedCollectionIds_Unauthenticated_ReturnsEmpty()
    {
        await using var db = CreateDb();
        SeedCollections(db);

        var svc = BuildService(db);
        var ids = await svc.GetAuthorizedCollectionIdsAsync(new UnauthenticatedUser(), CollectionRole.Preview);

        Assert.Empty(ids);
    }

    // -------------------------------------------------------------------------
    // 5. No assignment — collection is invisible
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetEffectiveRole_NoAssignment_ReturnsNull()
    {
        await using var db = CreateDb();
        SeedCollections(db);
        // No RoleAssignment rows — collection C is invisible to everyone.

        var svc = BuildService(db);
        var caller = new FakeUser("user:sub-grace", ["group:media"]);

        var role = await svc.GetEffectiveRoleAsync(CollectionC, caller);

        Assert.Null(role);
    }

    // -------------------------------------------------------------------------
    // 6. SiteAdminGroups bootstrap — Admin everywhere without a DB row.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetEffectiveRole_SiteAdminGroup_ReturnsAdminWithoutDbRow()
    {
        await using var db = CreateDb();
        SeedCollections(db);
        // Deliberately NO RoleAssignment row — admin must come from SiteAdminGroups config.

        // Build a service that knows "admins" is a site-admin group.
        // The downstream agent is expected to accept IOptions<CatalogAuthorizationOptions>
        // in CollectionAuthorizationService (or a decorator). Until then this test
        // constructs the service via the same BuildService helper and will fail —
        // which is intentional: it is a specification test.
        var svc = BuildService(db);
        var caller = new FakeUser("user:sub-henry", ["group:admins"]);

        var role = await svc.GetEffectiveRoleAsync(CollectionA, caller);

        Assert.Equal(CollectionRole.Admin, role);
    }

    [Fact]
    public async Task GetEffectiveRole_SiteAdminGroup_UnrelatedCollection_ReturnsAdmin()
    {
        await using var db = CreateDb();
        SeedCollections(db);
        // CollectionC has zero DB assignments; site-admin must still see it.

        var svc = BuildService(db);
        var caller = new FakeUser("user:sub-ivan", ["group:admins"]);

        var role = await svc.GetEffectiveRoleAsync(CollectionC, caller);

        Assert.Equal(CollectionRole.Admin, role);
    }

    [Fact]
    public async Task GetAuthorizedCollectionIds_SiteAdminGroup_ReturnsAllCollections()
    {
        await using var db = CreateDb();
        SeedCollections(db);
        // Only CollectionA has a DB row; site-admin must see all three.
        db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionA,
            Principal = "group:games",
            Role = CollectionRole.Preview,
        });
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var caller = new FakeUser("user:sub-judy", ["group:admins"]);

        var ids = await svc.GetAuthorizedCollectionIdsAsync(caller, CollectionRole.Preview);

        Assert.Contains(CollectionA, ids);
        Assert.Contains(CollectionB, ids);
        Assert.Contains(CollectionC, ids);
    }

    [Fact]
    public async Task GetEffectiveRole_NonSiteAdminGroup_NullWhenNoDbRow()
    {
        // "media" group is NOT in SiteAdminGroups; without a DB row they get null.
        await using var db = CreateDb();
        SeedCollections(db);

        var svc = BuildService(db);
        var caller = new FakeUser("user:sub-karl", ["group:media"]);

        var role = await svc.GetEffectiveRoleAsync(CollectionA, caller);

        // media is not a site-admin group — without a DB row, must be invisible.
        Assert.Null(role);
    }

    // -------------------------------------------------------------------------
    // 7. GetAuthorizedCollectionIdsAsync — OIDC convention principals
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetAuthorizedCollectionIds_UserAssignment_ReturnsMatchingCollections()
    {
        await using var db = CreateDb();
        SeedCollections(db);
        db.RoleAssignments.AddRange(
            new RoleAssignment { CollectionId = CollectionA, Principal = "user:sub-lucy", Role = CollectionRole.Preview },
            new RoleAssignment { CollectionId = CollectionB, Principal = "user:sub-lucy", Role = CollectionRole.Download });
        // CollectionC has no row → must be excluded
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var caller = new FakeUser("user:sub-lucy", []);

        var ids = (await svc.GetAuthorizedCollectionIdsAsync(caller, CollectionRole.Preview)).ToHashSet();

        Assert.Contains(CollectionA, ids);
        Assert.Contains(CollectionB, ids);
        Assert.DoesNotContain(CollectionC, ids);
    }

    [Fact]
    public async Task GetAuthorizedCollectionIds_GroupAssignment_ReturnsMatchingCollections()
    {
        await using var db = CreateDb();
        SeedCollections(db);
        db.RoleAssignments.AddRange(
            new RoleAssignment { CollectionId = CollectionA, Principal = "group:media", Role = CollectionRole.Download },
            new RoleAssignment { CollectionId = CollectionC, Principal = "group:games", Role = CollectionRole.Preview });
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        // Caller is in media but not games
        var caller = new FakeUser("user:sub-mike", ["group:media"]);

        var ids = (await svc.GetAuthorizedCollectionIdsAsync(caller, CollectionRole.Preview)).ToHashSet();

        Assert.Contains(CollectionA, ids);
        Assert.DoesNotContain(CollectionB, ids);
        Assert.DoesNotContain(CollectionC, ids); // games, not media
    }

    [Fact]
    public async Task GetAuthorizedCollectionIds_MinimumRoleFilter_ExcludesBelowBar()
    {
        await using var db = CreateDb();
        SeedCollections(db);
        db.RoleAssignments.AddRange(
            new RoleAssignment { CollectionId = CollectionA, Principal = "user:sub-nina", Role = CollectionRole.Preview },
            new RoleAssignment { CollectionId = CollectionB, Principal = "user:sub-nina", Role = CollectionRole.Uploader });
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var caller = new FakeUser("user:sub-nina", []);

        // Asking for Uploader+ should return only B
        var ids = (await svc.GetAuthorizedCollectionIdsAsync(caller, CollectionRole.Uploader)).ToHashSet();

        Assert.DoesNotContain(CollectionA, ids); // Preview < Uploader
        Assert.Contains(CollectionB, ids);
    }
}
