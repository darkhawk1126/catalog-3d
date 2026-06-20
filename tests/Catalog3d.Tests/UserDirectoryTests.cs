using Catalog3d.Application.Abstractions;
using Catalog3d.Infrastructure.Persistence;
using Catalog3d.Infrastructure.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Catalog3d.Tests;

/// <summary>
/// Tests for the capture-on-login user directory: the convenience source behind the admin
/// role/share pickers. Validates upsert behaviour, listing/ordering, group extraction, and the
/// friendly-label resolution used to render existing grants (user / group / unknown principal).
/// </summary>
public sealed class UserDirectoryTests
{
    // IDbContextFactory over a single named in-memory store so contexts created across calls
    // (and across separate UserDirectory instances) share the same data.
    private sealed class TestDbFactory : IDbContextFactory<CatalogDbContext>
    {
        private readonly DbContextOptions<CatalogDbContext> _options;

        public TestDbFactory(string dbName)
        {
            _options = new DbContextOptionsBuilder<CatalogDbContext>()
                .UseInMemoryDatabase(dbName)
                .Options;
        }

        public CatalogDbContext CreateDbContext() => new(_options);
    }

    private sealed class FakeUser : IUserContext
    {
        public required string UserId { get; init; }
        public string DisplayName { get; init; } = string.Empty;
        public string Username { get; init; } = string.Empty;
        public IReadOnlyList<string> Groups { get; init; } = [];
        public bool IsAuthenticated { get; init; } = true;
    }

    private static (UserDirectory dir, TestDbFactory factory) NewDirectory()
    {
        var factory = new TestDbFactory($"userdir-{Guid.NewGuid():N}");
        return (new UserDirectory(factory, NullLogger<UserDirectory>.Instance), factory);
    }

    // A fresh instance shares the store but has an empty write-throttle, simulating a later
    // sign-in beyond the throttle window.
    private static UserDirectory FreshInstance(TestDbFactory factory) =>
        new(factory, NullLogger<UserDirectory>.Instance);

    [Fact]
    public async Task UpsertCurrentAsync_NewUser_PersistsRow()
    {
        var (dir, factory) = NewDirectory();

        await dir.UpsertCurrentAsync(new FakeUser
        {
            UserId = "user:sub-alice",
            Username = "alice",
            DisplayName = "Alice Example",
            Groups = ["group:media", "group:friends"],
        });

        await using var db = factory.CreateDbContext();
        var row = await db.KnownUsers.SingleAsync();

        Assert.Equal("user:sub-alice", row.Principal);
        Assert.Equal("alice", row.Username);
        Assert.Equal("Alice Example", row.DisplayName);
        Assert.Equal("group:media,group:friends", row.GroupsCsv);
        Assert.Equal(row.FirstSeenAt, row.LastSeenAt);
    }

    [Fact]
    public async Task UpsertCurrentAsync_Unauthenticated_IsNoOp()
    {
        var (dir, factory) = NewDirectory();

        await dir.UpsertCurrentAsync(new FakeUser
        {
            UserId = "user:sub-x",
            IsAuthenticated = false,
        });

        await using var db = factory.CreateDbContext();
        Assert.Equal(0, await db.KnownUsers.CountAsync());
    }

    [Fact]
    public async Task UpsertCurrentAsync_ExistingUser_UpdatesDisplayNameAndLastSeen()
    {
        var (dir, factory) = NewDirectory();

        await dir.UpsertCurrentAsync(new FakeUser
        {
            UserId = "user:sub-bob",
            Username = "bob",
            DisplayName = "Bob",
            Groups = [],
        });

        DateTimeOffset firstSeen;
        await using (var db = factory.CreateDbContext())
            firstSeen = (await db.KnownUsers.SingleAsync()).FirstSeenAt;

        // A later sign-in (fresh instance => throttle does not suppress) with a changed name.
        await FreshInstance(factory).UpsertCurrentAsync(new FakeUser
        {
            UserId = "user:sub-bob",
            Username = "bob",
            DisplayName = "Bob Renamed",
            Groups = ["group:admins"],
        });

        await using var db2 = factory.CreateDbContext();
        var row = await db2.KnownUsers.SingleAsync();   // still exactly one row
        Assert.Equal("Bob Renamed", row.DisplayName);
        Assert.Equal("group:admins", row.GroupsCsv);
        Assert.Equal(firstSeen, row.FirstSeenAt);       // FirstSeenAt is immutable
        Assert.True(row.LastSeenAt >= firstSeen);
    }

    [Fact]
    public async Task UpsertCurrentAsync_SameInstanceTwice_ThrottlesSecondWrite()
    {
        var (dir, factory) = NewDirectory();

        await dir.UpsertCurrentAsync(new FakeUser { UserId = "user:sub-c", DisplayName = "First" });
        // Second call on the same instance is within the throttle window → suppressed.
        await dir.UpsertCurrentAsync(new FakeUser { UserId = "user:sub-c", DisplayName = "Second" });

        await using var db = factory.CreateDbContext();
        var row = await db.KnownUsers.SingleAsync();
        Assert.Equal("First", row.DisplayName);
    }

    [Fact]
    public async Task ListUsersAsync_OrdersByDisplayName()
    {
        var (dir, factory) = NewDirectory();
        await dir.UpsertCurrentAsync(new FakeUser { UserId = "user:sub-z", DisplayName = "Zoe", Username = "zoe" });
        await FreshInstance(factory).UpsertCurrentAsync(new FakeUser { UserId = "user:sub-a", DisplayName = "Aaron", Username = "aaron" });

        var users = await dir.ListUsersAsync();

        Assert.Equal(2, users.Count);
        Assert.Equal("Aaron", users[0].DisplayName);
        Assert.Equal("Zoe", users[1].DisplayName);
    }

    [Fact]
    public async Task ListGroupsAsync_ReturnsDistinctGroupPrincipals()
    {
        var (dir, factory) = NewDirectory();
        await dir.UpsertCurrentAsync(new FakeUser { UserId = "user:sub-1", Groups = ["group:media", "group:admins"] });
        await FreshInstance(factory).UpsertCurrentAsync(new FakeUser { UserId = "user:sub-2", Groups = ["group:media"] });

        var groups = await dir.ListGroupsAsync();

        Assert.Equal(["group:admins", "group:media"], groups);
    }

    [Fact]
    public async Task ResolveLabelsAsync_ResolvesUserGroupAndUnknown()
    {
        var (dir, factory) = NewDirectory();
        await dir.UpsertCurrentAsync(new FakeUser
        {
            UserId = "user:sub-known",
            Username = "known",
            DisplayName = "Known User",
        });

        var labels = await dir.ResolveLabelsAsync(
            ["user:sub-known", "group:media", "user:sub-departed"]);

        Assert.Equal("Known User (known)", labels["user:sub-known"]);
        Assert.Equal("media", labels["group:media"]);
        Assert.Equal("user:sub-departed", labels["user:sub-departed"]); // unknown → principal verbatim
    }
}
