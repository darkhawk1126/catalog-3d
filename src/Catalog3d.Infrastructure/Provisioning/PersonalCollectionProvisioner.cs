using System.Collections.Concurrent;
using System.Text;
using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Entities;
using Catalog3d.Domain.Enums;
using Catalog3d.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Catalog3d.Infrastructure.Provisioning;

/// <summary>
/// Process-wide set of principals already confirmed to have a personal collection.
/// Registered as a singleton so the per-request (scoped) provisioner can skip the DB after the
/// first sighting. A miss only costs one indexed existence check, so a cold cache is harmless.
/// </summary>
public sealed class ProvisionedPrincipalCache
{
    private readonly ConcurrentDictionary<string, byte> _seen =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsProvisioned(string principal) => _seen.ContainsKey(principal);
    public void MarkProvisioned(string principal) => _seen.TryAdd(principal, 0);
}

/// <summary>
/// Creates a personal collection for each authenticated principal on first sighting, granting
/// them Admin on it. The collection is the user's "folder"; uploads default there and the
/// per-model sharing (Visibility/ModelShare) governs who else may see each model.
/// </summary>
internal sealed class PersonalCollectionProvisioner : IPersonalCollectionProvisioner
{
    private readonly CatalogDbContext _db;
    private readonly ProvisionedPrincipalCache _cache;
    private readonly ILogger<PersonalCollectionProvisioner> _logger;

    public PersonalCollectionProvisioner(
        CatalogDbContext db,
        ProvisionedPrincipalCache cache,
        ILogger<PersonalCollectionProvisioner> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    public async Task EnsureProvisionedAsync(IUserContext caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAuthenticated || string.IsNullOrEmpty(caller.UserId))
            return;

        var principal = caller.UserId;
        if (_cache.IsProvisioned(principal))
            return;

        // Already has at least one collection they administer? Then they're provisioned.
        var alreadyOwns = await _db.RoleAssignments
            .AnyAsync(r => r.Principal == principal && r.Role == CollectionRole.Admin, cancellationToken)
            .ConfigureAwait(false);

        if (alreadyOwns)
        {
            _cache.MarkProvisioned(principal);
            return;
        }

        var baseSlug = DerivePersonalSlug(principal);
        var slug = baseSlug;

        // Disambiguate the rare case where two principals sanitize to the same slug: a stable
        // short hash of the full principal keeps it deterministic across restarts.
        if (await _db.Collections.AnyAsync(c => c.Slug == slug, cancellationToken).ConfigureAwait(false))
            slug = $"{baseSlug}-{ShortHash(principal)}";

        var now = DateTimeOffset.UtcNow;
        var collectionId = Guid.NewGuid();
        var displayName = string.IsNullOrWhiteSpace(caller.DisplayName)
            ? StripPrefix(principal)
            : caller.DisplayName;

        _db.Collections.Add(new Collection
        {
            Id = collectionId,
            Slug = slug,
            Name = $"{displayName}'s models",
            Description = $"Personal model folder for {displayName}.",
            CreatedAt = now,
            UpdatedAt = now,
        });

        _db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = collectionId,
            Principal = principal,
            Role = CollectionRole.Admin,
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Provisioned personal collection '{Slug}' for principal {Principal}.", slug, principal);
        }
        catch (DbUpdateException)
        {
            // A concurrent request for the same principal won the race — that's fine, the
            // collection now exists. Detach our duplicates so this scoped context stays usable.
            _db.ChangeTracker.Clear();
        }

        _cache.MarkProvisioned(principal);
    }

    // "user:ab-12" → "u-ab-12";  dev "Alice" → "u-alice". ASCII-lower, non-alphanumerics → '-'.
    private static string DerivePersonalSlug(string principal)
    {
        var id = StripPrefix(principal).ToLowerInvariant();

        var sb = new StringBuilder(id.Length);
        foreach (var ch in id)
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');

        var body = sb.ToString();
        while (body.Contains("--", StringComparison.Ordinal))
            body = body.Replace("--", "-", StringComparison.Ordinal);
        body = body.Trim('-');

        if (body.Length == 0)
            body = ShortHash(principal);

        var slug = "u-" + body;
        return slug.Length > 200 ? slug[..200] : slug;
    }

    private static string StripPrefix(string principal) =>
        principal.StartsWith("user:", StringComparison.OrdinalIgnoreCase)
            ? principal["user:".Length..]
            : principal;

    // Deterministic 8-hex-char FNV-1a hash; stable across restarts (no RNG/time).
    private static string ShortHash(string value)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= prime;
        }
        return hash.ToString("x8");
    }
}
