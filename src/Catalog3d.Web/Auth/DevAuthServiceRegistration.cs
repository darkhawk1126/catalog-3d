using Catalog3d.Infrastructure.Auth;
using Catalog3d.Web.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Catalog3d.Web.Auth;

internal static class DevAuthServiceRegistration
{
    /// <summary>Cookie scheme name for browser-based dev sign-in via /login/dev.</summary>
    internal const string DevCookieScheme = "DevCookie";

    // Policy scheme name that forwards to the appropriate leaf scheme.
    internal const string DevAutoScheme = "DevAuto";

    internal static IServiceCollection AddDevAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // DevAuto is the default scheme: it inspects the request and forwards to either
        // DevScheme (X-Dev-User header, used by API clients and tests) or DevCookie
        // (browser cookie, used by Blazor pages). This lets both API and Blazor flows
        // authenticate with the same registered schemes.
        services.AddAuthentication(options =>
            {
                options.DefaultScheme = DevAutoScheme;
                options.DefaultChallengeScheme = DevCookieScheme;
            })
            .AddPolicyScheme(DevAutoScheme, "Dev header or cookie", options =>
            {
                // Forward to the header-based scheme when the X-Dev-User header is present,
                // otherwise fall back to the cookie scheme (Blazor browser sessions).
                options.ForwardDefaultSelector = ctx =>
                    ctx.Request.Headers.ContainsKey("X-Dev-User") ? "DevScheme" : DevCookieScheme;
            })
            .AddCookie(DevCookieScheme, options =>
            {
                options.LoginPath = "/login/dev";
                options.AccessDeniedPath = "/login/dev";
                // Short session lifetime for dev — ephemeral data-protection keys mean
                // cookies don't survive restart anyway; 8-hour sliding window is enough.
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);

                // API paths (/api/...) expect 401, not a browser redirect.
                // Return 401 directly so programmatic clients get a machine-readable response.
                options.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = ctx =>
                    {
                        if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                        {
                            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return Task.CompletedTask;
                        }
                        ctx.Response.Redirect(ctx.RedirectUri);
                        return Task.CompletedTask;
                    },
                    OnRedirectToAccessDenied = ctx =>
                    {
                        if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                        {
                            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return Task.CompletedTask;
                        }
                        ctx.Response.Redirect(ctx.RedirectUri);
                        return Task.CompletedTask;
                    },
                };
            })
            .AddScheme<DevAuthSchemeOptions, DevAuthHandler>(
                "DevScheme",
                options => configuration.GetSection(DevAuthSchemeOptions.SectionName).Bind(options));

        services.AddAuthorization();

        // Bind DevUsers config to the default (unnamed) IOptions<DevAuthSchemeOptions> so that
        // DevLoginEndpoints can inject IOptions<DevAuthSchemeOptions> and read Users.
        // AddScheme only populates the named "DevScheme" options; this covers the unnamed slot.
        services.Configure<DevAuthSchemeOptions>(
            configuration.GetSection(DevAuthSchemeOptions.SectionName));

        // Bind so that Authorization:SiteAdminGroups applies in dev mode too.
        // CollectionAuthorizationService depends on IOptions<CatalogAuthorizationOptions>
        // regardless of auth scheme; binding here keeps dev and OIDC behaviour consistent.
        services.Configure<CatalogAuthorizationOptions>(
            configuration.GetSection(CatalogAuthorizationOptions.SectionName));

        services.AddDevUserContext();
        services.AddCollectionAuthorization();
        services.AddCollectionResourceAuthorization();

        return services;
    }
}
