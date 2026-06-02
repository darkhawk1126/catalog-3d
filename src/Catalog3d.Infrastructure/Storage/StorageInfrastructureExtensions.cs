using Catalog3d.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog3d.Infrastructure.Storage;

public static class StorageInfrastructureExtensions
{
    /// <summary>Registers DiskFileStore as the IFileStore singleton.</summary>
    public static IServiceCollection AddDiskFileStore(this IServiceCollection services)
    {
        services.AddSingleton<IFileStore, DiskFileStore>();
        return services;
    }
}
