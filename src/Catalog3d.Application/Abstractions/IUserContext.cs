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

    /// <summary>All group/role identifiers the principal belongs to (OIDC groups or dev config groups).</summary>
    IReadOnlyList<string> Groups { get; }

    /// <summary>True when the request is authenticated.</summary>
    bool IsAuthenticated { get; }
}
