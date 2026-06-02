namespace Catalog3d.Infrastructure.Auth;

/// <summary>
/// Config options that govern site-wide authorization policy.
/// Bound from config section "Authorization".
///
/// Config key map:
///   Authorization:SiteAdminGroups — string array; members of any listed Authelia group
///                                   receive CollectionRole.Admin on ALL collections without
///                                   a RoleAssignment row. Default: ["admins"]
/// </summary>
public sealed class CatalogAuthorizationOptions
{
    public const string SectionName = "Authorization";

    /// <summary>
    /// Authelia group names whose members are implicitly site-admin on every collection.
    /// Coarse homelab groups only (admins, media, games) — never catalog-specific groups.
    /// Matched against IUserContext.Groups using OrdinalIgnoreCase.
    /// </summary>
    public List<string> SiteAdminGroups { get; set; } = ["admins"];
}
