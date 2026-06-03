using Catalog3d.Application.Abstractions;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace Catalog3d.Infrastructure.Auth;

/// <summary>
/// IUserContext backed by the OIDC ClaimsPrincipal produced by the
/// Microsoft.AspNetCore.Authentication.OpenIdConnect handler.
///
/// Claim name note: the handler is configured with MapInboundClaims = false, so OIDC
/// claim names arrive verbatim ("sub", "name", "groups") rather than as WS-Federation URNs
/// (ClaimTypes.NameIdentifier, ClaimTypes.Name). All FindFirstValue calls below use the
/// literal OIDC claim names.
///
/// Principal convention (used by CollectionAuthorizationService + RoleAssignment.Principal):
///   Direct user assignment : "user:<oidc-sub>"   e.g. "user:a1b2c3d4-..."
///   Group assignment       : "group:<name>"       e.g. "group:admins"
///
/// The BuildPrincipalSet helper in CollectionAuthorizationService adds UserId and each
/// Groups entry verbatim into an OrdinalIgnoreCase HashSet. RoleAssignment rows stored as
/// "user:<sub>" and "group:<name>" match via principals.Contains without any change to
/// CollectionAuthorizationService.
/// </summary>
internal sealed class OidcUserContext : IUserContext
{
    private readonly ClaimsPrincipal _user;

    public OidcUserContext(IHttpContextAccessor httpContextAccessor)
    {
        _user = httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal();
    }

    public bool IsAuthenticated =>
        _user.Identity?.IsAuthenticated is true;

    // "user:" prefix is the principal convention: RoleAssignment.Principal rows for direct
    // user grants are stored as "user:<sub>", matched OrdinalIgnoreCase by BuildPrincipalSet.
    // MapInboundClaims = false means the sub claim retains its literal OIDC name "sub".
    public string UserId =>
        "user:" + (_user.FindFirstValue("sub") ?? string.Empty);

    // "name" is populated by the "profile" scope. Fall back to "preferred_username" when the
    // IdP omits the formatted name claim. MapInboundClaims = false; no ClaimTypes URN used.
    public string DisplayName =>
        _user.FindFirstValue("name")
            ?? _user.FindFirstValue("preferred_username")
            ?? string.Empty;

    // Authelia delivers groups as a JSON string array via the UserInfo endpoint.
    // GetClaimsFromUserInfoEndpoint = true causes the handler to expand the JSON array into
    // individual claims, all sharing the claim type "groups". "group:" prefix matches the
    // RoleAssignment.Principal convention for group grants.
    // M1: normalize to lower-case so matching is genuinely case-insensitive end-to-end.
    // BuildPrincipalSet uses OrdinalIgnoreCase, but the EF Core IN-clause translates to SQL
    // where string comparisons depend on collation; storing lower-case values makes the
    // invariant explicit and independent of DB collation.
    public IReadOnlyList<string> Groups =>
        _user.FindAll("groups")
             .Select(c => "group:" + c.Value.ToLowerInvariant())
             .ToList()
             .AsReadOnly();
}
