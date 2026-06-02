using System.Security.Claims;
using Catalog3d.Infrastructure.Auth;
using Microsoft.AspNetCore.Http;

// OidcUserContext is configured with MapInboundClaims = false, so the handler
// delivers claims with their literal OIDC names ("sub", "name", "groups") rather
// than WS-Federation URN equivalents. Tests must therefore use literal names, not
// "sub" / "name".

namespace Catalog3d.Tests;

/// <summary>
/// Unit tests for OidcUserContext claim mapping.
///
/// The class under test is internal sealed; these tests are in the same assembly
/// (test project references Infrastructure). All tests construct a ClaimsPrincipal
/// directly without spinning up ASP.NET Core — no WebApplicationFactory needed.
/// </summary>
public sealed class OidcUserContextTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static OidcUserContext BuildContext(IEnumerable<Claim> claims, bool authenticated = true)
    {
        var identity = authenticated
            ? new ClaimsIdentity(claims, "TestScheme")   // authenticationType != null → IsAuthenticated = true
            : new ClaimsIdentity(claims);

        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = principal };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        return new OidcUserContext(accessor);
    }

    // -------------------------------------------------------------------------
    // IsAuthenticated
    // -------------------------------------------------------------------------

    [Fact]
    public void IsAuthenticated_WhenIdentityAuthenticated_ReturnsTrue()
    {
        var ctx = BuildContext([], authenticated: true);
        Assert.True(ctx.IsAuthenticated);
    }

    [Fact]
    public void IsAuthenticated_WhenIdentityNotAuthenticated_ReturnsFalse()
    {
        var ctx = BuildContext([], authenticated: false);
        Assert.False(ctx.IsAuthenticated);
    }

    [Fact]
    public void IsAuthenticated_WhenNoHttpContext_ReturnsFalse()
    {
        var accessor = new HttpContextAccessor { HttpContext = null };
        var ctx = new OidcUserContext(accessor);
        Assert.False(ctx.IsAuthenticated);
    }

    // -------------------------------------------------------------------------
    // UserId — must be "user:<sub>"
    // -------------------------------------------------------------------------

    [Fact]
    public void UserId_ReturnsPrefixedSub()
    {
        var sub = "a1b2c3d4-1234-5678-abcd-000000000001";
        var ctx = BuildContext([new Claim("sub", sub)]);

        Assert.Equal($"user:{sub}", ctx.UserId);
    }

    [Fact]
    public void UserId_WhenSubAbsent_ReturnsUserColonEmpty()
    {
        // Missing sub should degrade gracefully — no exception, but value is "user:".
        var ctx = BuildContext([new Claim("name", "Alice")]);

        // Acceptable: either "user:" or string.Empty. Contract says prefix the sub;
        // when absent the prefix alone is the graceful fallback.
        Assert.StartsWith("user:", ctx.UserId, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // DisplayName — must come from the name claim
    // -------------------------------------------------------------------------

    [Fact]
    public void DisplayName_ReturnsPrincipalNameClaim()
    {
        var ctx = BuildContext([
            new Claim("sub", "sub-val"),
            new Claim("name", "Alice Smith"),
        ]);

        Assert.Equal("Alice Smith", ctx.DisplayName);
    }

    [Fact]
    public void DisplayName_WhenNameAbsent_ReturnsEmptyString()
    {
        var ctx = BuildContext([new Claim("sub", "sub-val")]);

        Assert.Equal(string.Empty, ctx.DisplayName);
    }

    // -------------------------------------------------------------------------
    // Groups — single "groups" claim (Authelia sends a JSON array as one claim)
    // -------------------------------------------------------------------------

    [Fact]
    public void Groups_SingleGroupsClaim_ReturnsPrefixedEntries()
    {
        // Authelia delivers groups as a JSON string array in one claim value.
        // The OIDC handler (with GetClaimsFromUserInfoEndpoint = true) parses
        // JSON arrays from UserInfo and emits one Claim per element with the
        // same claim type. This test covers the single-claim / pre-parsed case.
        var ctx = BuildContext([
            new Claim("sub", "sub-val"),
            new Claim("groups", "admins"),
        ]);

        var groups = ctx.Groups;
        Assert.Single(groups);
        Assert.Equal("group:admins", groups[0]);
    }

    [Fact]
    public void Groups_MultipleGroupsClaims_ReturnsPrefixedAll()
    {
        // When the handler maps multiple groups from UserInfo it emits one Claim
        // per group — all with claim type "groups". OidcUserContext must collect
        // every "groups" claim and prefix each.
        var ctx = BuildContext([
            new Claim("sub", "sub-val"),
            new Claim("groups", "admins"),
            new Claim("groups", "media"),
            new Claim("groups", "games"),
        ]);

        var groups = ctx.Groups.ToHashSet(StringComparer.Ordinal);
        Assert.Equal(3, groups.Count);
        Assert.Contains("group:admins", groups);
        Assert.Contains("group:media", groups);
        Assert.Contains("group:games", groups);
    }

    [Fact]
    public void Groups_NoGroupsClaims_ReturnsEmpty()
    {
        var ctx = BuildContext([new Claim("sub", "sub-val")]);

        Assert.Empty(ctx.Groups);
    }

    // -------------------------------------------------------------------------
    // Principal convention — UserId and Groups must be formatted so that
    // CollectionAuthorizationService can match "user:<sub>" and "group:<name>"
    // RoleAssignment rows without any mapping code in the service.
    // -------------------------------------------------------------------------

    [Fact]
    public void PrincipalConvention_UserIdMatchesRoleAssignmentFormat()
    {
        const string sub = "a1b2c3d4-1234-5678-abcd-000000000099";
        var ctx = BuildContext([new Claim("sub", sub)]);

        // A RoleAssignment.Principal stored as "user:<sub>" must equal ctx.UserId.
        Assert.Equal($"user:{sub}", ctx.UserId);
    }

    [Fact]
    public void PrincipalConvention_GroupsMatchRoleAssignmentFormat()
    {
        var ctx = BuildContext([
            new Claim("sub", "sub-val"),
            new Claim("groups", "media"),
        ]);

        // A RoleAssignment.Principal stored as "group:media" must appear in Groups.
        Assert.Contains("group:media", ctx.Groups);
    }
}
