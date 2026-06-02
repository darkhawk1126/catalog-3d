using Catalog3d.Domain.Entities;
using Catalog3d.Domain.Enums;
using Catalog3d.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Catalog3d.Tests;

/// <summary>
/// Milestone-4 tests: role-assignment persistence.
///
/// Validates that RoleAssignment rows follow the "user:&lt;id&gt;" / "group:&lt;name&gt;"
/// principal convention and that add/remove operations reach the DB correctly.
///
/// Coverage:
///   1. Assigning a user principal persists a "user:&lt;id&gt;" row.
///   2. Assigning a group principal persists a "group:&lt;name&gt;" row.
///   3. Removing an assignment deletes the row; no row remains.
///   4. Upsert: adding a second assignment for the same principal updates
///      the role in place (composite PK constraint respected).
///   5. Multiple principals on one collection round-trip correctly.
///   6. Removing one assignment leaves others intact.
/// </summary>
public sealed class Milestone4RoleAssignmentTests
{
    private static readonly Guid CollectionId = new("cccc0000-0000-0000-0000-000000000001");

    private static CatalogDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseInMemoryDatabase($"m4-roles-{Guid.NewGuid():N}")
            .Options;
        return new CatalogDbContext(opts);
    }

    private static void SeedCollection(CatalogDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        db.Collections.Add(new Collection
        {
            Id = CollectionId,
            Slug = "test-col",
            Name = "Test",
            Description = "",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SaveChanges();
    }

    // -------------------------------------------------------------------------
    // 1. User principal convention
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AddAssignment_UserPrincipal_PersistedWithUserPrefix()
    {
        await using var db = CreateDb();
        SeedCollection(db);

        db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionId,
            Principal = "user:sub-alice",
            Role = CollectionRole.Download,
        });
        await db.SaveChangesAsync();

        var row = await db.RoleAssignments
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.CollectionId == CollectionId && r.Principal == "user:sub-alice");

        Assert.NotNull(row);
        Assert.Equal(CollectionRole.Download, row.Role);
    }

    // -------------------------------------------------------------------------
    // 2. Group principal convention
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AddAssignment_GroupPrincipal_PersistedWithGroupPrefix()
    {
        await using var db = CreateDb();
        SeedCollection(db);

        db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionId,
            Principal = "group:media",
            Role = CollectionRole.Uploader,
        });
        await db.SaveChangesAsync();

        var row = await db.RoleAssignments
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.CollectionId == CollectionId && r.Principal == "group:media");

        Assert.NotNull(row);
        Assert.Equal(CollectionRole.Uploader, row.Role);
    }

    // -------------------------------------------------------------------------
    // 3. Remove: deleting a row leaves no trace
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RemoveAssignment_DeletesRow_NoRowRemains()
    {
        await using var db = CreateDb();
        SeedCollection(db);

        var assignment = new RoleAssignment
        {
            CollectionId = CollectionId,
            Principal = "user:sub-bob",
            Role = CollectionRole.Preview,
        };
        db.RoleAssignments.Add(assignment);
        await db.SaveChangesAsync();

        // Simulate the UI delete path: remove the tracked entity.
        db.RoleAssignments.Remove(assignment);
        await db.SaveChangesAsync();

        var exists = await db.RoleAssignments
            .AnyAsync(r => r.CollectionId == CollectionId && r.Principal == "user:sub-bob");

        Assert.False(exists);
    }

    // -------------------------------------------------------------------------
    // 4. Upsert: role update respects composite PK
    //
    // The CollectionRoles page uses Update (or Remove+Add) to change a principal's
    // role. This test verifies that overwriting the Role field and SaveChanges
    // produces exactly one row with the new role.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UpdateAssignment_ChangeRole_OneRowWithNewRole()
    {
        await using var db = CreateDb();
        SeedCollection(db);

        db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionId,
            Principal = "user:sub-carol",
            Role = CollectionRole.Preview,
        });
        await db.SaveChangesAsync();

        // Simulate the admin page updating the role: remove + re-add (safest without Attach).
        var existing = await db.RoleAssignments
            .FirstAsync(r => r.CollectionId == CollectionId && r.Principal == "user:sub-carol");
        existing.Role = CollectionRole.Admin;
        await db.SaveChangesAsync();

        var rows = await db.RoleAssignments
            .AsNoTracking()
            .Where(r => r.CollectionId == CollectionId && r.Principal == "user:sub-carol")
            .ToListAsync();

        Assert.Single(rows);
        Assert.Equal(CollectionRole.Admin, rows[0].Role);
    }

    // -------------------------------------------------------------------------
    // 5. Multiple principals round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AddMultiplePrincipals_AllPersistedCorrectly()
    {
        await using var db = CreateDb();
        SeedCollection(db);

        db.RoleAssignments.AddRange(
            new RoleAssignment { CollectionId = CollectionId, Principal = "user:sub-dave", Role = CollectionRole.Preview },
            new RoleAssignment { CollectionId = CollectionId, Principal = "group:media", Role = CollectionRole.Uploader },
            new RoleAssignment { CollectionId = CollectionId, Principal = "group:games", Role = CollectionRole.Download });
        await db.SaveChangesAsync();

        var rows = await db.RoleAssignments
            .AsNoTracking()
            .Where(r => r.CollectionId == CollectionId)
            .ToListAsync();

        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.Principal == "user:sub-dave" && r.Role == CollectionRole.Preview);
        Assert.Contains(rows, r => r.Principal == "group:media" && r.Role == CollectionRole.Uploader);
        Assert.Contains(rows, r => r.Principal == "group:games" && r.Role == CollectionRole.Download);
    }

    // -------------------------------------------------------------------------
    // 6. Remove one: other assignments are untouched
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RemoveOneAssignment_OtherAssignmentsUnchanged()
    {
        await using var db = CreateDb();
        SeedCollection(db);

        db.RoleAssignments.AddRange(
            new RoleAssignment { CollectionId = CollectionId, Principal = "user:sub-eve", Role = CollectionRole.Preview },
            new RoleAssignment { CollectionId = CollectionId, Principal = "group:admins", Role = CollectionRole.Admin });
        await db.SaveChangesAsync();

        var toRemove = await db.RoleAssignments
            .FirstAsync(r => r.Principal == "user:sub-eve");
        db.RoleAssignments.Remove(toRemove);
        await db.SaveChangesAsync();

        var remaining = await db.RoleAssignments
            .AsNoTracking()
            .Where(r => r.CollectionId == CollectionId)
            .ToListAsync();

        Assert.Single(remaining);
        Assert.Equal("group:admins", remaining[0].Principal);
        Assert.Equal(CollectionRole.Admin, remaining[0].Role);
    }
}
