using Catalog3d.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog3d.Infrastructure.Upload;

public static class UploadInfrastructureExtensions
{
    /// <summary>
    /// Registers <see cref="IModelUploadService"/> as a scoped service (requires a DB context
    /// per request) and binds <see cref="ModelUploadOptions"/> from configuration section
    /// <c>Upload</c>.
    /// </summary>
    public static IServiceCollection AddModelUploadService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ModelUploadOptions>(
            configuration.GetSection(ModelUploadOptions.SectionName));

        services.AddScoped<IModelUploadService, ModelUploadService>();

        return services;
    }
}
