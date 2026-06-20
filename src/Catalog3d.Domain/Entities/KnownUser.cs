namespace Catalog3d.Domain.Entities;

/// <summary>
/// A user the application has seen sign in at least once, captured so the admin UI can offer a
/// friendly picker instead of forcing operators to type the opaque OIDC <c>sub</c> by hand.
///
/// catalog-3d has no user database of its own — identity arrives per-request from the IdP — so
/// this table is populated lazily: every authenticated request upserts the caller's identity
/// (see the directory capture in the request pipeline). It is a convenience directory, NOT an
/// authorization source: access is still governed solely by <see cref="RoleAssignment"/> and
/// <see cref="ModelShare"/>.
///
/// <see cref="Principal"/> is the stable identity key and matches
/// <see cref="RoleAssignment.Principal"/> / <see cref="ModelShare.Principal"/> exactly
/// (e.g. "user:&lt;oidc-sub&gt;", or the bare dev username under the Dev scheme), so a grant can be
/// written straight from a picked entry and existing rows can be resolved back to a friendly name.
/// </summary>
public sealed class KnownUser
{
    /// <summary>Stable identity key — equals <c>IUserContext.UserId</c> (e.g. "user:&lt;sub&gt;").</summary>
    public string Principal { get; set; } = string.Empty;

    /// <summary>Friendly login name (OIDC <c>preferred_username</c>); may be empty if the IdP omits it.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Human display name (OIDC <c>name</c>), used as the primary label in the picker.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated list of the principal-form group identifiers seen on the caller's last
    /// sign-in (e.g. "group:admins,group:media"). Lets the role/share picker offer group grants
    /// without a separate IdP query. Empty when the caller belongs to no groups.
    /// </summary>
    public string GroupsCsv { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; init; }

    public DateTimeOffset LastSeenAt { get; set; }
}
