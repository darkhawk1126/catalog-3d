namespace Catalog3d.Application.Abstractions;

/// <summary>
/// Ensures every authenticated principal has a personal collection (their "folder") with
/// themselves as Admin, created on first sighting. Idempotent and cheap after the first call
/// (an in-memory cache suppresses repeat DB round-trips for already-provisioned principals).
/// </summary>
public interface IPersonalCollectionProvisioner
{
    /// <summary>
    /// Creates the caller's personal collection if it does not yet exist. No-op for
    /// unauthenticated callers and for principals already provisioned this process lifetime.
    /// </summary>
    Task EnsureProvisionedAsync(IUserContext caller, CancellationToken cancellationToken = default);
}
