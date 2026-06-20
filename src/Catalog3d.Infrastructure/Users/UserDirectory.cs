using System.Collections.Concurrent;
using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Entities;
using Catalog3d.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Catalog3d.Infrastructure.Users;

/// <summary>
/// <see cref="IUserDirectory"/> backed by the <c>known_users</c> table.
///
/// Registered as a singleton and uses <see cref="IDbContextFactory{TContext}"/> to create a
/// short-lived context per operation, so it is safe to consume from both the request pipeline
/// (capture middleware) and Blazor Server circuits without the scoped-context pitfalls EF warns
/// about. A process-wide throttle suppresses repeat writes for a recently-seen principal, keeping
/// <see cref="UpsertCurrentAsync"/> a no-op on the hot path (mirrors the personal-collection
/// provisioner's singleton cache).
/// </summary>
internal sealed class UserDirectory : IUserDirectory
{
    // Re-record a principal at most once per window even if it signs in repeatedly. LastSeenAt is
    // therefore accurate to ~this granularity, which is plenty for an admin picker.
    private static readonly TimeSpan WriteThrottle = TimeSpan.FromMinutes(15);

    private readonly IDbContextFactory<CatalogDbContext> _dbFactory;
    private readonly ILogger<UserDirectory> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastWritten =
        new(StringComparer.OrdinalIgnoreCase);

    public UserDirectory(
        IDbContextFactory<CatalogDbContext> dbFactory,
        ILogger<UserDirectory> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task UpsertCurrentAsync(IUserContext caller, CancellationToken cancellationToken = default)
    {
        if (caller is null || !caller.IsAuthenticated || string.IsNullOrEmpty(caller.UserId))
            return;

        var principal = caller.UserId;
        var now = DateTimeOffset.UtcNow;

        // Throttle: skip if we recorded this principal within the window.
        if (_lastWritten.TryGetValue(principal, out var last) && now - last < WriteThrottle)
            return;

        var username = caller.Username ?? string.Empty;
        var displayName = string.IsNullOrWhiteSpace(caller.DisplayName) ? username : caller.DisplayName;
        var groupsCsv = string.Join(',', caller.Groups);

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            var existing = await db.KnownUsers
                .FirstOrDefaultAsync(u => u.Principal == principal, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                db.KnownUsers.Add(new KnownUser
                {
                    Principal = principal,
                    Username = username,
                    DisplayName = displayName,
                    GroupsCsv = groupsCsv,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                });
            }
            else
            {
                existing.Username = username;
                existing.DisplayName = displayName;
                existing.GroupsCsv = groupsCsv;
                existing.LastSeenAt = now;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _lastWritten[principal] = now;
        }
        catch (DbUpdateException ex)
        {
            // A concurrent first-sighting of the same principal won the race; the row now exists,
            // so this is benign. Mark it seen so we don't thrash on retries.
            _lastWritten[principal] = now;
            _logger.LogDebug(ex, "Directory upsert race for principal {Principal}; ignoring.", principal);
        }
    }

    public async Task<IReadOnlyList<DirectoryUser>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var rows = await db.KnownUsers
            .AsNoTracking()
            .OrderBy(u => u.DisplayName)
            .ThenBy(u => u.Username)
            .Select(u => new DirectoryUser(u.Principal, u.Username, u.DisplayName))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows;
    }

    public async Task<IReadOnlyList<string>> ListGroupsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var csvs = await db.KnownUsers
            .AsNoTracking()
            .Select(u => u.GroupsCsv)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Group principals are kept in their grant-ready "group:<name>" form; split, dedupe, sort.
        var groups = csvs
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .SelectMany(c => c.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(g => g.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return groups;
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveLabelsAsync(
        IEnumerable<string> principals, CancellationToken cancellationToken = default)
    {
        var wanted = principals
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
            return result;

        // Only user principals need a DB lookup; groups and unknowns resolve from the string.
        var userPrincipals = wanted
            .Where(p => !p.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var known = new Dictionary<string, KnownUser>(StringComparer.OrdinalIgnoreCase);
        if (userPrincipals.Count > 0)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var rows = await db.KnownUsers
                .AsNoTracking()
                .Where(u => userPrincipals.Contains(u.Principal))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var row in rows)
                known[row.Principal] = row;
        }

        foreach (var p in wanted)
        {
            if (p.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
                result[p] = p["group:".Length..];
            else if (known.TryGetValue(p, out var u))
                result[p] = FormatUserLabel(u);
            else
                result[p] = p; // hand-entered, departed, or not-yet-seen principal
        }

        return result;
    }

    // "Bryan B. (deathlok1126)" when both are known and differ; otherwise whichever is present.
    private static string FormatUserLabel(KnownUser u)
    {
        var display = string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName;
        if (string.IsNullOrWhiteSpace(display))
            return u.Principal;

        return !string.IsNullOrWhiteSpace(u.Username)
            && !string.Equals(u.Username, display, StringComparison.OrdinalIgnoreCase)
                ? $"{display} ({u.Username})"
                : display;
    }
}
