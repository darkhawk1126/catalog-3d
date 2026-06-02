using Catalog3d.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog3d.Infrastructure.Rendering;

/// <summary>
/// Registers the in-process render queue, the render worker BackgroundService, and
/// the HTTP client for IThumbnailRenderer.
///
/// Call from Catalog3d.Web via the thin registration seam in RenderingServiceRegistration.
/// </summary>
public static class RenderingInfrastructureExtensions
{
    /// <summary>
    /// Registers InProcessRenderQueue (singleton, shared between uploader and worker),
    /// RenderWorker (BackgroundService), and the SidecarThumbnailRenderer HTTP client.
    /// </summary>
    /// <param name="services">Service collection to register into.</param>
    /// <param name="sidecarBaseUrl">Value of RenderSidecar:BaseUrl from app configuration.</param>
    /// <param name="sidecarTimeout">Render timeout — default 60 s; tune via RenderSidecar:TimeoutSeconds.</param>
    public static IServiceCollection AddRenderingInfrastructure(
        this IServiceCollection services,
        Uri sidecarBaseUrl,
        TimeSpan? sidecarTimeout = null)
    {
        var timeout = sidecarTimeout ?? TimeSpan.FromSeconds(60);

        // Single instance shared between the upload path (writer) and the worker (reader).
        services.AddSingleton<InProcessRenderQueue>();
        services.AddSingleton<IRenderQueue>(sp => sp.GetRequiredService<InProcessRenderQueue>());

        // BackgroundService worker.
        services.AddHostedService<RenderWorker>();

        // Named HTTP client for the render sidecar.
        services.AddHttpClient<IThumbnailRenderer, SidecarThumbnailRenderer>(client =>
        {
            client.BaseAddress = sidecarBaseUrl;
            // Render jobs invoke an external process (f3d); allow generous time for large STLs.
            client.Timeout = timeout;
        });

        return services;
    }
}
