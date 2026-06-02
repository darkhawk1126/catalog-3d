using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Enums;
using Catalog3d.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Catalog3d.Infrastructure.Auth;

/// <summary>
/// EF Core-backed ACL resolution. A principal matches a RoleAssignment either by
/// UserId (direct assignment) or by any group membership in IUserContext.Groups
/// (group assignment). The effective role is the maximum across all matching rows,
/// which lets a user get Uploader from a direct assignment and still benefit from
/// an Admin group assignment without requiring two queries.
///
/// Site-admin short-circuit: if any of the caller's Groups (with "group:" prefix)
/// matches a name in CatalogAuthorizationOptions.SiteAdminGroups (matched without
/// prefix, OrdinalIgnoreCase), the caller is returned Admin on every collection
/// without a DB round-trip. This is evaluated before the database query.
/// </summary>
internal sealed class CollectionAuthorizationService : ICollectionAuthorizationService
{
    private readonly CatalogDbContext _db;
    private readonly CatalogAuthorizationOptions _authzOptions;

    public CollectionAuthorizationService(
        CatalogDbContext db,
        IOptions<CatalogAuthorizationOptions> authzOptions)
    {
        _db = db;
        _authzOptions = authzOptions.Value;
    }

    public async Task<CollectionRole?> GetEffectiveRoleAsync(
        Guid collectionId,
        IUserContext caller,
        CancellationToken cancellationToken = default)
    {
        if (!caller.IsAuthenticated)
            return null;

        // Site-admin groups bypass the DB: they are Admin on every collection.
        if (IsSiteAdmin(caller))
            return CollectionRole.Admin;

        var principals = BuildPrincipalSet(caller);

        // Single query: filter to this collection and the caller's principals,
        // then take the max role (highest numeric value = most permissive).
        var maxRole = await _db.RoleAssignments
            .Where(r => r.CollectionId == collectionId && principals.Contains(r.Principal))
            .Select(r => (CollectionRole?)r.Role)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

        return maxRole;
    }

    public async Task<bool> AuthorizeAsync(
        Guid collectionId,
        IUserContext caller,
        CollectionRole minimumRole,
        CancellationToken cancellationToken = default)
    {
        var effective = await GetEffectiveRoleAsync(collectionId, caller, cancellationToken)
            .ConfigureAwait(false);

        return effective.HasValue && effective.Value >= minimumRole;
    }

    public async Task<IReadOnlyList<Guid>> GetAuthorizedCollectionIdsAsync(
        IUserContext caller,
        CollectionRole minimumRole,
        CancellationToken cancellationToken = default)
    {
        if (!caller.IsAuthenticated)
            return Array.Empty<Guid>();

        // Site admins see every collection; return all IDs in one query without JOIN to RoleAssignments.
        if (IsSiteAdmin(caller))
        {
            return await _db.Collections
                .Select(c => c.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var principals = BuildPrincipalSet(caller);

        // Group by collection; return only those where the max role meets the bar.
        // The GroupBy→Max pattern is a single SQL GROUP BY with HAVING.
        var ids = await _db.RoleAssignments
            .Where(r => principals.Contains(r.Principal))
            .GroupBy(r => r.CollectionId)
            .Where(g => g.Max(r => (int)r.Role) >= (int)minimumRole)
            .Select(g => g.Key)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return ids;
    }

    // Returns true if the caller belongs to any Authelia group listed in SiteAdminGroups.
    // caller.Groups uses the "group:<name>" convention; SiteAdminGroups stores bare names.
    private bool IsSiteAdmin(IUserContext caller)
    {
        var adminGroups = _authzOptions.SiteAdminGroups;
        if (adminGroups is null or { Count: 0 })
            return false;

        foreach (var group in caller.Groups)
        {
            // Strip the "group:" prefix before comparing against the bare config values.
            var name = group.StartsWith("group:", StringComparison.OrdinalIgnoreCase)
                ? group[6..]
                : group;

            foreach (var adminGroup in adminGroups)
            {
                if (string.Equals(name, adminGroup, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    // Build the full set of principals (userId + all groups) for IN-clause matching.
    private static HashSet<string> BuildPrincipalSet(IUserContext caller)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { caller.UserId };
        foreach (var group in caller.Groups)
            set.Add(group);
        return set;
    }
}
