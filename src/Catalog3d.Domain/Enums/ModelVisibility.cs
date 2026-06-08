namespace Catalog3d.Domain.Enums;

/// <summary>
/// Per-model sharing state, orthogonal to the collection-level RBAC (RoleAssignment).
/// Determines who — beyond the owner and collection role-holders — may access a model.
///
/// The cornerstone security rule still holds: an interactive viewer is download-equivalent,
/// so "access" here means the Download tier (geometry/viewer). The Preview tier (static PNG)
/// is granted more broadly for Shared/Public models so the wiki embed can show a thumbnail
/// to page viewers who are not themselves authorized to download.
/// </summary>
public enum ModelVisibility
{
    /// <summary>Owner (and collection Admin/site-admin) only. Invisible to everyone else,
    /// including via the wiki embed — no preview is served to non-owners.</summary>
    Private = 0,

    /// <summary>Owner + principals listed in ModelShare get Download. Any authenticated user
    /// reaching the wiki embed gets the Preview (PNG) tier only.</summary>
    Shared = 1,

    /// <summary>Any authenticated user gets Download (interactive viewer + file).</summary>
    Public = 2
}
