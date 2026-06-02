using Catalog3d.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Catalog3d.Web.Persistence;

/// <summary>
/// Applies pending EF Core migrations on startup in the Development environment.
///
/// This is intentionally Development-only. Production uses an init container that
/// runs `dotnet ef database update` before the app pod starts (see DESIGN.md).
/// Registered only when IHostEnvironment.IsDevelopment() is true — see
/// PersistenceServiceRegistration.AddCatalogPersistence.
///
/// A hosted service is used rather than app.MigrateAsync() in Program.cs so
/// Program.cs (sealed) does not need to be touched.
/// </summary>
internal sealed class DevelopmentMigrateHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DevelopmentMigrateHostedService> _logger;

    public DevelopmentMigrateHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<DevelopmentMigrateHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Development: checking for pending EF Core migrations...");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        // In-memory or non-relational providers (e.g. tests) do not support migration APIs.
        if (!db.Database.IsRelational())
        {
            _logger.LogInformation("Non-relational provider detected; skipping migration check.");
            return;
        }

        var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
        var pendingList = pending.ToList();

        if (pendingList.Count == 0)
        {
            _logger.LogInformation("No pending migrations.");
            return;
        }

        _logger.LogInformation("Applying {Count} migration(s): {Migrations}",
            pendingList.Count, string.Join(", ", pendingList));

        await db.Database.MigrateAsync(cancellationToken);

        _logger.LogInformation("Migrations applied successfully.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
