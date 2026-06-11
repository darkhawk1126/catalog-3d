using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Enums;
using Catalog3d.Infrastructure.Persistence;
using Catalog3d.Infrastructure.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Catalog3d.Tests;

/// <summary>
/// Personal-collection slug derivation: the auto-provisioned collection should use the
/// caller's friendly login name (OIDC preferred_username), falling back to the opaque
/// principal/sub only when no username is available. Ownership always keys off the sub.
///
/// EF Core InMemory provider — no real Postgres required.
/// </summary>
public sealed class PersonalCollectionSlugTests
{
    private static CatalogDbContext CreateDb(string? name = null) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseInMemoryDatabase(name ?? $"slug-{Guid.NewGuid():N}")
            .Options);

    private static PersonalCollectionProvisioner Build(CatalogDbContext db) =>
        new(db, new ProvisionedPrincipalCache(), NullLogger<PersonalCollectionProvisioner>.Instance);

    private sealed class FakeUser(string userId, string username) : IUserContext
    {
        public bool IsAuthenticated => true;
        public string UserId { get; } = userId;
        public string Username { get; } = username;
        public string DisplayName => "Test User";
        public IReadOnlyList<string> Groups { get; } = [];
    }

    [Fact]
    public async Task Provisions_slug_from_friendly_username_not_sub()
    {
        await using var db = CreateDb();
        // sub is an opaque UUID; username is the friendly login name.
        var caller = new FakeUser("user:325fe1d9-2c4b-4d0e-9a11-abc123def456", "Deathlok1126");

        await Build(db).EnsureProvisionedAsync(caller);

        var collection = await db.Collections.SingleAsync();
        Assert.Equal("u-deathlok1126", collection.Slug);

        // Ownership is keyed on the sub-based principal, never the username.
        var admin = await db.RoleAssignments.SingleAsync();
        Assert.Equal("user:325fe1d9-2c4b-4d0e-9a11-abc123def456", admin.Principal);
        Assert.Equal(CollectionRole.Admin, admin.Role);
    }

    [Fact]
    public async Task Sanitizes_username_into_a_safe_slug()
    {
        await using var db = CreateDb();
        var caller = new FakeUser("user:abc", "Cool User!");

        await Build(db).EnsureProvisionedAsync(caller);

        var collection = await db.Collections.SingleAsync();
        Assert.Equal("u-cool-user", collection.Slug);
    }

    [Fact]
    public async Task Falls_back_to_principal_when_username_is_absent()
    {
        await using var db = CreateDb();
        // No preferred_username from the IdP: slug derives from the sub-based principal.
        var caller = new FakeUser("user:fallback-sub", "");

        await Build(db).EnsureProvisionedAsync(caller);

        var collection = await db.Collections.SingleAsync();
        Assert.Equal("u-fallback-sub", collection.Slug);
    }
}
