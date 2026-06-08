namespace Catalog3d.Domain.Entities;

/// <summary>
/// A per-model access grant to a single principal, used when a Model's Visibility is Shared.
/// Grants the Download tier (interactive viewer + file) to the named principal — sharing a
/// model is download-equivalent by the cornerstone rule, so there is no finer share role.
///
/// Principal convention matches RoleAssignment.Principal and IUserContext:
///   direct user grant : "user:&lt;oidc-sub&gt;"  (OIDC)  or the bare dev username (Dev)
///   group grant       : "group:&lt;name&gt;"
/// </summary>
public sealed class ModelShare
{
    public Guid ModelId { get; set; }

    /// <summary>The principal granted Download on this model (user or group).</summary>
    public string Principal { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public Model Model { get; set; } = null!;
}
