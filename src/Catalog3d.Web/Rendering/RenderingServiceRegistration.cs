using Catalog3d.Infrastructure.Rendering;

namespace Catalog3d.Web.Rendering;

/// <summary>
/// Thin Web-layer seam: reads RenderSidecar config and delegates to Infrastructure.
/// </summary>
internal static class RenderingServiceRegistration
{
    /// <summary>
    /// Registers the in-process render queue, RenderWorker BackgroundService, and the
    /// SidecarThumbnailRenderer HTTP client. Binds RenderSidecarOptions from configuration.
    /// </summary>
    internal static IServiceCollection AddRenderingServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration
            .GetSection(RenderSidecarOptions.SectionName)
            .Get<RenderSidecarOptions>()
            ?? new RenderSidecarOptions();

        services.Configure<RenderSidecarOptions>(
            configuration.GetSection(RenderSidecarOptions.SectionName));

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException(
                $"RenderSidecar:BaseUrl '{options.BaseUrl}' is not a valid absolute URI.");

        var timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        services.AddRenderingInfrastructure(baseUri, timeout);

        return services;
    }
}
