namespace Catalog3d.Application.Abstractions;

/// <summary>
/// A selectable user known to the application, surfaced to the admin UI's role/share pickers.
/// <see cref="Principal"/> is the value written into a grant (matches RoleAssignment/ModelShare);
/// <see cref="DisplayName"/> / <see cref="Username"/> are the human-friendly labels.
/// </summary>
public sealed record DirectoryUser(string Principal, string Username, string DisplayName);

/// <summary>
/// Convenience directory of users (and the groups) the app has observed signing in, so the admin
/// UI can offer a friendly picker instead of requiring the raw OIDC <c>sub</c>.
///
/// This is NOT an authorization source — it never decides access. Access is governed only by
/// <c>ICollectionAuthorizationService</c> / <c>IModelAuthorizationService</c>. The directory is
/// populated lazily by <see cref="UpsertCurrentAsync"/> on each authenticated request, so a user
/// appears here only after their first sign-in to catalog-3d.
/// </summary>
public interface IUserDirectory
{
    /// <summary>
    /// Records (or refreshes) the calling principal in the directory. Idempotent and cheap: an
    /// in-memory throttle suppresses repeat writes within a short window, so this is a no-op on
    /// the hot path. No-op for unauthenticated callers.
    /// </summary>
    Task UpsertCurrentAsync(IUserContext caller, CancellationToken cancellationToken = default);

    /// <summary>All known users, ordered by display name, for the picker. Excludes group entries.</summary>
    Task<IReadOnlyList<DirectoryUser>> ListUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Distinct group principals ("group:&lt;name&gt;") seen across all known users' last sign-in,
    /// ordered alphabetically — offered as group-grant options alongside the user picker.
    /// </summary>
    Task<IReadOnlyList<string>> ListGroupsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Maps each given principal to a friendly label for display. Known users resolve to
    /// "DisplayName" (or username); "group:&lt;name&gt;" resolves to "name"; anything unknown
    /// (e.g. a hand-entered or departed principal) maps to the principal string itself.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ResolveLabelsAsync(
        IEnumerable<string> principals, CancellationToken cancellationToken = default);
}
