using Catalog3d.Domain.Enums;

namespace Catalog3d.Application.Abstractions;

/// <summary>
/// Resolves a caller's effective access to a single <c>Model</c>, folding together the
/// collection-level RBAC (RoleAssignment / site-admin) and the per-model sharing state
/// (ModelVisibility + ModelShare). This is the choke-point for model-scoped access decisions;
/// the geometry, thumbnail, viewer, and embed endpoints all resolve access through here.
/// </summary>
public interface IModelAuthorizationService
{
    /// <summary>
    /// Resolves the caller's access to the model identified by <paramref name="modelId"/>.
    /// Returns <see cref="ModelAccess.None"/> when the model does not exist or the caller is
    /// unauthenticated, so callers can treat "no access" and "not found" uniformly (404).
    /// </summary>
    Task<ModelAccess> ResolveAsync(
        Guid modelId,
        IUserContext caller,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The caller's effective access tiers for one model. The tiers are cumulative in intent —
/// <c>CanDownload</c> implies <c>CanPreview</c>, and <c>CanManage</c> implies both — but each
/// flag is set explicitly by the resolver so callers can branch without re-deriving.
/// </summary>
/// <param name="CanPreview">May fetch the static PNG thumbnail (Preview tier).</param>
/// <param name="CanDownload">May fetch geometry / use the interactive viewer (Download tier ≡ file access).</param>
/// <param name="CanManage">May change visibility and manage shares (owner, collection Admin, or site-admin).</param>
/// <param name="Visibility">The model's current visibility, surfaced for UI/diagnostics.</param>
public readonly record struct ModelAccess(
    bool CanPreview,
    bool CanDownload,
    bool CanManage,
    ModelVisibility Visibility)
{
    /// <summary>No access of any kind — also used for unknown/invisible models.</summary>
    public static ModelAccess None => new(false, false, false, ModelVisibility.Private);
}
