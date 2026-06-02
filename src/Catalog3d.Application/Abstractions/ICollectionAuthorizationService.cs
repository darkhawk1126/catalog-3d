using Catalog3d.Domain.Enums;

namespace Catalog3d.Application.Abstractions;

/// <summary>
/// Resolves the effective CollectionRole for a caller against a specific collection.
/// This is the single choke-point for ACL decisions; all endpoint handlers go through here.
/// Returns null when the principal has no assignment (collection is invisible to them).
/// </summary>
public interface ICollectionAuthorizationService
{
    /// <summary>
    /// Returns the effective role the caller holds on <paramref name="collectionId"/>,
    /// or null if no role is assigned (collection must not be listed or accessed).
    /// </summary>
    Task<CollectionRole?> GetEffectiveRoleAsync(
        Guid collectionId,
        IUserContext caller,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true when the caller's effective role is at least <paramref name="minimumRole"/>.
    /// </summary>
    Task<bool> AuthorizeAsync(
        Guid collectionId,
        IUserContext caller,
        CollectionRole minimumRole,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all collection IDs for which the caller holds at least <paramref name="minimumRole"/>.
    /// Used to filter list-collections results.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetAuthorizedCollectionIdsAsync(
        IUserContext caller,
        CollectionRole minimumRole,
        CancellationToken cancellationToken = default);
}
