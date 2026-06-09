using Catalog3d.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Catalog3d.Web.Persistence;

internal static class PersistenceServiceRegistration
{
    internal static IServiceCollection AddCatalogPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("CatalogDb");

        // AddDbContextFactory registers IDbContextFactory<CatalogDbContext> as Singleton.
        // This is used directly by Blazor Server components which create short-lived contexts
        // per-operation (mandatory pattern — Blazor circuits cannot share one scoped context).
        //
        // PATTERN for Blazor components (mandatory):
        //
        //   [Inject] IDbContextFactory<CatalogDbContext> DbFactory { get; set; } = null!;
        //
        //   protected override async Task OnInitializedAsync()
        //   {
        //       await using var db = await DbFactory.CreateDbContextAsync();
        //       // ... query db ...
        //   }
        //
        // Each call to CreateDbContextAsync() returns an independent CatalogDbContext instance
        // that the component owns and is responsible for disposing. Never store or share the
        // created instance across multiple async operations or component lifetimes.
        services.AddDbContextFactory<CatalogDbContext>(options =>
            options.UseNpgsql(connectionString));

        // Provide CatalogDbContext as a Scoped service backed by the factory.
        // API endpoint handlers and CollectionAuthorizationService inject CatalogDbContext
        // directly; the factory-backed scoped registration supplies one per HTTP request.
        // The caller owns disposal — ASP.NET Core disposes the scope at end of request.
        services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>().CreateDbContext());

        // Auto-apply migrations on startup when either the environment is Development (always,
        // for a frictionless local loop) or Database:MigrateOnStartup=true is set explicitly
        // (production opt-in). The production runtime image is the ASP.NET runtime (no SDK /
        // dotnet-ef), so an app-side MigrateAsync on boot is simpler than an EF-tools init
        // container; the chart sets Database:MigrateOnStartup=true and a single-replica Recreate
        // rollout keeps the boot-time migration safe.
        var aspnetEnv = configuration["ASPNETCORE_ENVIRONMENT"]
            ?? configuration["environment"]
            ?? Environments.Production;

        var isDevelopment = aspnetEnv.Equals(Environments.Development, StringComparison.OrdinalIgnoreCase);
        var migrateOnStartup = string.Equals(
            configuration["Database:MigrateOnStartup"], "true", StringComparison.OrdinalIgnoreCase);

        if (isDevelopment || migrateOnStartup)
            services.AddHostedService<DevelopmentMigrateHostedService>();

        return services;
    }
}
