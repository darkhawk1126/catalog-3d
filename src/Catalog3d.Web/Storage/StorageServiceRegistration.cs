using Catalog3d.Infrastructure.Storage;

namespace Catalog3d.Web.Storage;

internal static class StorageServiceRegistration
{
    internal static IServiceCollection AddDiskFileStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<DiskFileStoreOptions>(
            configuration.GetSection(DiskFileStoreOptions.SectionName));

        services.AddDiskFileStore();

        return services;
    }
}
