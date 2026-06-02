using Catalog3d.Web.Blazor.Admin.Shared;

namespace Catalog3d.Web.Blazor;

/// <summary>
/// Registers all Blazor Server services for the admin/upload UI.
///
/// Called once from Program.cs: builder.Services.AddBlazorAdmin()
///
/// What this registers:
///   - AddRazorComponents() + AddInteractiveServerComponents() — core Blazor Server
///   - AddCascadingAuthenticationState() — makes AuthenticationState available to
///     all descendant components via CascadingParameter without re-declaring it
///   - AdminAuthHelper (scoped per-circuit) — page-level admin guard helper;
///     reads user from AuthenticationStateProvider (not IHttpContextAccessor, which
///     is null during Blazor Server SignalR rendering)
///
/// NOTE on IUserContext: Blazor components do NOT inject IUserContext directly.
/// Use AdminAuthHelper for all authorization decisions in admin pages. IUserContext
/// registered by the auth setup (DevUserContext / OidcUserContext) remains for the
/// API endpoint pipeline only; it is not overridden here.
///
/// NOTE on IDbContextFactory: registered in AddCatalogPersistence, available here.
/// Components must use the factory pattern — see PersistenceServiceRegistration docs.
/// </summary>
internal static class BlazorAdminServiceRegistration
{
    internal static IServiceCollection AddBlazorAdmin(this IServiceCollection services)
    {
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // Makes AuthenticationState available as a CascadingParameter to all
        // components without explicitly cascading it in every layout.
        services.AddCascadingAuthenticationState();

        // AdminAuthHelper provides admin guard logic for all Blazor admin pages.
        // Scoped: one per circuit; reads from AuthenticationStateProvider + ICollectionAuthorizationService.
        services.AddScoped<AdminAuthHelper>();

        return services;
    }

    internal static WebApplication MapBlazorAdmin(this WebApplication app)
    {
        app.MapRazorComponents<Admin.App>()
            .AddInteractiveServerRenderMode();

        return app;
    }
}
