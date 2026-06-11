namespace Catalog3d.Application.Abstractions;

/// <summary>
/// Provides caller identity to application services without coupling them to
/// ASP.NET Core ClaimsPrincipal or any specific auth scheme.
/// </summary>
public interface IUserContext
{
    /// <summary>Stable identifier for the authenticated principal (sub claim or dev user id).</summary>
    string UserId { get; }

    /// <summary>Display name; used for owner attribution on uploaded models.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Friendly login/account name (OIDC <c>preferred_username</c>, or the dev user id).
    /// Used only to derive a readable personal-collection slug — identity and RBAC still key
    /// off <see cref="UserId"/>. May be empty when the IdP omits it; callers must fall back.
    /// Defaulted here so existing implementers (and test fakes) need no change.
    /// </summary>
    string Username => string.Empty;

    /// <summary>All group/role identifiers the principal belongs to (OIDC groups or dev config groups).</summary>
    IReadOnlyList<string> Groups { get; }

    /// <summary>True when the request is authenticated.</summary>
    bool IsAuthenticated { get; }
}
