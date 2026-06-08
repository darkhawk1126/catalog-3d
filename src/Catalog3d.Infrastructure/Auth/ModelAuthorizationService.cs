using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Enums;
using Catalog3d.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Catalog3d.Infrastructure.Auth;

/// <summary>
/// Folds collection-level RBAC and per-model sharing into a single <see cref="ModelAccess"/>.
///
/// Decision order (most to least privileged):
///   1. Owner / collection Admin / site-admin  → manage + download + preview.
///   2. Collection Download role                → download + preview.
///   3. Visibility == Public                    → download + preview (any authenticated user).
///   4. Visibility == Shared and principal is in ModelShare → download + preview.
///   5. Collection Preview role                 → preview only.
///   6. Visibility in {Shared, Public}          → preview only (the wiki-embed grant: any
///      authenticated page viewer may see the PNG of a published model, but not its geometry).
///   7. Otherwise (Private to a non-owner)      → no access.
///
/// The cornerstone rule is preserved: CanDownload is the only flag that gates geometry, and an
/// interactive viewer is treated as download-equivalent by every caller of this service.
/// </summary>
internal sealed class ModelAuthorizationService : IModelAuthorizationService
{
    private readonly CatalogDbContext _db;
    private readonly ICollectionAuthorizationService _collectionAuth;

    public ModelAuthorizationService(
        CatalogDbContext db,
        ICollectionAuthorizationService collectionAuth)
    {
        _db = db;
        _collectionAuth = collectionAuth;
    }

    public async Task<ModelAccess> ResolveAsync(
        Guid modelId,
        IUserContext caller,
        CancellationToken cancellationToken = default)
    {
        if (!caller.IsAuthenticated)
            return ModelAccess.None;

        var model = await _db.Models
            .AsNoTracking()
            .Select(m => new { m.Id, m.CollectionId, m.Owner, m.Visibility })
            .FirstOrDefaultAsync(m => m.Id == modelId, cancellationToken)
            .ConfigureAwait(false);

        if (model is null)
            return ModelAccess.None;

        var collRole = await _collectionAuth
            .GetEffectiveRoleAsync(model.CollectionId, caller, cancellationToken)
            .ConfigureAwait(false);

        var isOwner = !string.IsNullOrEmpty(caller.UserId)
            && string.Equals(model.Owner, caller.UserId, StringComparison.OrdinalIgnoreCase);

        var canManage = isOwner || RoleAtLeast(collRole, CollectionRole.Admin);

        var canDownload = canManage
            || RoleAtLeast(collRole, CollectionRole.Download)
            || model.Visibility == ModelVisibility.Public
            || (model.Visibility == ModelVisibility.Shared
                && await IsSharedWithAsync(model.Id, caller, cancellationToken).ConfigureAwait(false));

        var canPreview = canDownload
            || RoleAtLeast(collRole, CollectionRole.Preview)
            // Wiki-embed grant: a published (Shared/Public) model's thumbnail is visible to any
            // authenticated page viewer, even one who cannot download it.
            || model.Visibility is ModelVisibility.Shared or ModelVisibility.Public;

        return new ModelAccess(canPreview, canDownload, canManage, model.Visibility);
    }

    private async Task<bool> IsSharedWithAsync(
        Guid modelId,
        IUserContext caller,
        CancellationToken cancellationToken)
    {
        var principals = BuildPrincipalSet(caller);

        return await _db.ModelShares
            .Where(s => s.ModelId == modelId && principals.Contains(s.Principal))
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool RoleAtLeast(CollectionRole? role, CollectionRole minimum) =>
        role.HasValue && role.Value >= minimum;

    // Mirrors CollectionAuthorizationService.BuildPrincipalSet: the caller's own id plus every
    // group, matched OrdinalIgnoreCase. RoleAssignment/ModelShare rows store "user:<sub>" and
    // "group:<name>" (OIDC) or bare dev identifiers; both match via principals.Contains.
    private static HashSet<string> BuildPrincipalSet(IUserContext caller)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { caller.UserId };
        foreach (var group in caller.Groups)
            set.Add(group);
        return set;
    }
}
