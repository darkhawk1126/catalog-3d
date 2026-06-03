using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Entities;
using Catalog3d.Domain.Enums;
using Catalog3d.Infrastructure.Auth;
using Catalog3d.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Catalog3d.Tests;

/// <summary>
/// Admin gating tests for collection create/manage operations.
///
/// These tests validate the authorization rules enforced by ICollectionAuthorizationService
/// that back the Blazor page guards:
///
///   - CollectionCreate and CollectionList "New" button: site-admin only.
///   - CollectionEdit, CollectionRoles: collection-Admin or site-admin.
///   - Non-admin callers navigated away (guard blocks access).
///
/// L5: IsSiteAdmin is now routed through the real ICollectionAuthorizationService so the
/// test covers the actual production code path rather than a local reimplementation.
/// L6: Stale Milestone-2/WILL-FAIL annotations removed; these tests are all green.
/// </summary>
public sealed class Milestone4AdminGatingTests
{
    private static readonly Guid CollectionId = new("dddd0000-0000-0000-0000-000000000001");

    private static CatalogDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseInMemoryDatabase($"m4-admin-{Guid.NewGuid():N}")
            .Options;
        return new CatalogDbContext(opts);
    }

    private static void SeedCollection(CatalogDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        db.Collections.Add(new Collection
        {
            Id = CollectionId,
            Slug = "gating-col",
            Name = "Gating Test",
            Description = "",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SaveChanges();
    }

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

    private sealed class UnauthUser : IUserContext
    {
        public bool IsAuthenticated => false;
        public string UserId => string.Empty;
        public string DisplayName => string.Empty;
        public IReadOnlyList<string> Groups => Array.Empty<string>();
    }

    // -------------------------------------------------------------------------
    // L5: IsSiteAdmin routed through the real service (GetEffectiveRoleAsync with
    // a sentinel collection ID that will never have a DB row). The site-admin
    // short-circuit in CollectionAuthorizationService returns Admin without a DB
    // lookup when any of the caller's groups matches SiteAdminGroups config.
    // -------------------------------------------------------------------------

    private static async Task<bool> IsSiteAdminViaService(IUserContext caller)
    {
        await using var db = new CatalogDbContext(
            new DbContextOptionsBuilder<CatalogDbContext>()
                .UseInMemoryDatabase($"is-sa-{Guid.NewGuid():N}")
                .Options);

        var svc = BuildService(db);
        // No collection seeded → any return of Admin must come from the site-admin short-circuit
        var sentinelId = Guid.NewGuid();
        return await svc.AuthorizeAsync(sentinelId, caller, CollectionRole.Admin);
    }

    // -------------------------------------------------------------------------
    // 1. Site-admin passes the CollectionCreate guard
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SiteAdmin_IsSiteAdmin_ReturnsTrue()
    {
        var caller = new FakeUser("user:sub-admin", ["group:admins"]);
        Assert.True(await IsSiteAdminViaService(caller));
    }

    // -------------------------------------------------------------------------
    // 2. Non-site-admin is blocked from CollectionCreate
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NonSiteAdmin_IsSiteAdmin_ReturnsFalse()
    {
        var caller = new FakeUser("user:sub-media", ["group:media"]);
        Assert.False(await IsSiteAdminViaService(caller));
    }

    [Fact]
    public async Task UserWithNoGroups_IsSiteAdmin_ReturnsFalse()
    {
        var caller = new FakeUser("user:sub-solo", []);
        Assert.False(await IsSiteAdminViaService(caller));
    }

    // -------------------------------------------------------------------------
    // 3. Collection-Admin passes the CollectionEdit / CollectionRoles guard
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectionAdmin_IsCollectionAdmin_ReturnsTrue()
    {
        await using var db = CreateDb();
        SeedCollection(db);

        db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionId,
            Principal = "user:sub-alice",
            Role = CollectionRole.Admin,
        });
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var caller = new FakeUser("user:sub-alice", []);

        var isAdmin = await svc.AuthorizeAsync(CollectionId, caller, CollectionRole.Admin);
        Assert.True(isAdmin);
    }

    // -------------------------------------------------------------------------
    // 4. Preview-only caller is blocked from CollectionEdit / CollectionRoles
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PreviewOnlyCaller_IsNotCollectionAdmin()
    {
        await using var db = CreateDb();
        SeedCollection(db);

        db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionId,
            Principal = "user:sub-viewer",
            Role = CollectionRole.Preview,
        });
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var caller = new FakeUser("user:sub-viewer", []);

        var isAdmin = await svc.AuthorizeAsync(CollectionId, caller, CollectionRole.Admin);
        Assert.False(isAdmin);
    }

    // -------------------------------------------------------------------------
    // 5. Site-admin is implicitly collection-Admin without a RoleAssignment row
    //
    // The page guard calls AdminAuthHelper.IsCollectionAdminAsync which delegates
    // to ICollectionAuthorizationService.AuthorizeAsync with CollectionRole.Admin.
    // The service's site-admin short-circuit must fire before the DB lookup.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SiteAdmin_IsCollectionAdmin_TrueWithoutDbRow()
    {
        await using var db = CreateDb();
        SeedCollection(db);
        // Deliberately no RoleAssignment row.

        var svc = BuildService(db);
        var caller = new FakeUser("user:sub-boss", ["group:admins"]);

        var isAdmin = await svc.AuthorizeAsync(CollectionId, caller, CollectionRole.Admin);
        Assert.True(isAdmin);
    }

    // -------------------------------------------------------------------------
    // 6. Uploader is NOT collection-Admin
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UploaderRole_IsNotCollectionAdmin()
    {
        await using var db = CreateDb();
        SeedCollection(db);

        db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionId,
            Principal = "user:sub-uploader",
            Role = CollectionRole.Uploader,
        });
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var caller = new FakeUser("user:sub-uploader", []);

        var isAdmin = await svc.AuthorizeAsync(CollectionId, caller, CollectionRole.Admin);
        Assert.False(isAdmin);
    }

    // -------------------------------------------------------------------------
    // 7. Unauthenticated caller is blocked from everything
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Unauthenticated_IsSiteAdmin_ReturnsFalse()
    {
        Assert.False(await IsSiteAdminViaService(new UnauthUser()));
    }

    [Fact]
    public async Task Unauthenticated_IsNotCollectionAdmin()
    {
        await using var db = CreateDb();
        SeedCollection(db);

        db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionId,
            Principal = "user:sub-alice",
            Role = CollectionRole.Admin,
        });
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var isAdmin = await svc.AuthorizeAsync(CollectionId, new UnauthUser(), CollectionRole.Admin);
        Assert.False(isAdmin);
    }

    // -------------------------------------------------------------------------
    // 8. Uploader does have access at the Uploader level
    //    (Uploader >= Uploader = true; Uploader < Admin = false)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UploaderRole_AuthorizedForUploader_NotForAdmin()
    {
        await using var db = CreateDb();
        SeedCollection(db);

        db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionId,
            Principal = "user:sub-uploader2",
            Role = CollectionRole.Uploader,
        });
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var caller = new FakeUser("user:sub-uploader2", []);

        Assert.True(await svc.AuthorizeAsync(CollectionId, caller, CollectionRole.Uploader));
        Assert.False(await svc.AuthorizeAsync(CollectionId, caller, CollectionRole.Admin));
    }
}
