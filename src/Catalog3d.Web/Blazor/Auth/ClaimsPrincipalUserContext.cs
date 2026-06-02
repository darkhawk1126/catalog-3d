using Catalog3d.Application.Abstractions;
using System.Security.Claims;

namespace Catalog3d.Web.Blazor.Auth;

/// <summary>
/// Minimal IUserContext that wraps a ClaimsPrincipal snapshot.
///
/// Used internally by AdminAuthHelper to bridge between the ClaimsPrincipal from
/// AuthenticationStateProvider and ICollectionAuthorizationService (which expects IUserContext).
///
/// Claim reading mirrors OidcUserContext (OIDC scheme) and DevUserContext (Dev scheme):
///   OIDC: sub → "user:{sub}", groups claim → "group:{name}"
///   Dev:  ClaimTypes.NameIdentifier → username (no prefix), groups → bare group names
///
/// This type is internal to the Blazor.Auth namespace; do not expose it as a registered service.
/// </summary>
internal sealed class ClaimsPrincipalUserContext : IUserContext
{
    private readonly ClaimsPrincipal _user;
    private readonly bool _isOidc;

    internal ClaimsPrincipalUserContext(ClaimsPrincipal user, string authProvider)
    {
        _user = user;
        _isOidc = authProvider.Equals("Oidc", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsAuthenticated => _user.Identity?.IsAuthenticated is true;

    public string UserId
    {
        get
        {
            if (!IsAuthenticated) return string.Empty;
            if (_isOidc) return "user:" + (_user.FindFirstValue("sub") ?? string.Empty);
            return _user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        }
    }

    public string DisplayName
    {
        get
        {
            if (!IsAuthenticated) return string.Empty;
            if (_isOidc)
                return _user.FindFirstValue("name")
                    ?? _user.FindFirstValue("preferred_username")
                    ?? string.Empty;
            return _user.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
        }
    }

    public IReadOnlyList<string> Groups
    {
        get
        {
            if (!IsAuthenticated) return [];
            if (_isOidc)
                return _user.FindAll("groups")
                    .Select(c => "group:" + c.Value)
                    .ToList()
                    .AsReadOnly();
            // DevAuthHandler stores groups under the literal claim type "groups" (OIDC-compatible name).
            return _user.FindAll("groups")
                .Select(c => c.Value)
                .ToList()
                .AsReadOnly();
        }
    }
}
