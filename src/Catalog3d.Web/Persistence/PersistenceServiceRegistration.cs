using Catalog3d.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Catalog3d.Web.Persistence;

internal static class PersistenceServiceRegistration
{
    internal static IServiceCollection AddCatalogPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<CatalogDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("CatalogDb")));

        return services;
    }
}
