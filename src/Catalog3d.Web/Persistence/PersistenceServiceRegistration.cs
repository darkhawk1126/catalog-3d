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

        // Auto-apply migrations in Development only.
        // Read the environment from config; ASPNETCORE_ENVIRONMENT is available in configuration
        // even before the host is built. Production uses an init container (see DESIGN.md).
        var aspnetEnv = configuration["ASPNETCORE_ENVIRONMENT"]
            ?? configuration["environment"]
            ?? Environments.Production;

        if (aspnetEnv.Equals(Environments.Development, StringComparison.OrdinalIgnoreCase))
            services.AddHostedService<DevelopmentMigrateHostedService>();

        return services;
    }
}
