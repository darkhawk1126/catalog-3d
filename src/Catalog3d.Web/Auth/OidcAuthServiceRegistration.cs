using Catalog3d.Infrastructure.Auth;
using Catalog3d.Web.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Catalog3d.Web.Auth;

/// <summary>
/// Registers OIDC authentication for Auth:Provider = Oidc.
///
/// Scheme names are stable constants so downstream agents can reference them
/// without magic strings:
///   OidcAuthServiceRegistration.CookieScheme  = "OidcCookie"
///   OidcAuthServiceRegistration.OidcScheme    = OpenIdConnectDefaults.AuthenticationScheme
///
/// Authorization config keys (see CatalogAuthorizationOptions):
///   Authorization:SiteAdminGroups   (default ["admins"])
/// </summary>
internal static class OidcAuthServiceRegistration
{
    internal const string CookieScheme = "OidcCookie";
    internal const string OidcScheme = OpenIdConnectDefaults.AuthenticationScheme; // "OpenIdConnect"

    internal static IServiceCollection AddOidcAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var oidcOptions = configuration.GetSection(OidcOptions.SectionName).Get<OidcOptions>()
            ?? new OidcOptions();

        // In Development, default RequireHttpsMetadata to false when not explicitly set.
        // This lets local Authelia run over plain http without requiring the dev to set the key.
        if (environment.IsDevelopment() && !configuration.GetSection(OidcOptions.SectionName)
                .GetChildren().Any(c => c.Key.Equals(nameof(OidcOptions.RequireHttpsMetadata),
                    StringComparison.OrdinalIgnoreCase)))
        {
            oidcOptions.RequireHttpsMetadata = false;
        }

        services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieScheme;
                options.DefaultChallengeScheme = OidcScheme;
            })
            .AddCookie(CookieScheme)
            .AddOpenIdConnect(OidcScheme, options =>
            {
                options.Authority = oidcOptions.Authority;
                options.ClientId = oidcOptions.ClientId;
                options.ClientSecret = oidcOptions.ClientSecret;
                options.CallbackPath = oidcOptions.CallbackPath;
                options.RequireHttpsMetadata = oidcOptions.RequireHttpsMetadata;

                options.ResponseType = OpenIdConnectResponseType.Code;

                // PKCE S256 — UsePkce defaults to true; explicit for clarity.
                options.UsePkce = true;

                // The ASP.NET Core OIDC handler sends client_id and client_secret in the POST
                // body by default (client_secret_post), which is what Authelia's immich-style
                // client expects. No additional configuration needed for this.

                // Groups are NOT in the ID token (Authelia prod has no claims_policy configured).
                // They arrive via the UserInfo endpoint; "groups" scope must be requested.
                options.GetClaimsFromUserInfoEndpoint = true;

                // Disable inbound claim type mapping so claim names arrive with their OIDC names
                // (e.g. "sub", "name", "groups") instead of long WS-Federation URN equivalents.
                // OidcUserContext reads "groups" by its literal name.
                options.MapInboundClaims = false;

                // "openid" is included implicitly; add the rest from config (profile, email, groups).
                options.Scope.Clear();
                options.Scope.Add("openid");
                foreach (var scope in oidcOptions.Scopes)
                    options.Scope.Add(scope);

                // SaveTokens keeps the refresh token in the cookie so the sliding session
                // (refresh_token grant) can be exercised without re-prompting the user.
                options.SaveTokens = true;

                options.SignInScheme = CookieScheme;

                // Authelia 4.39.13 does not expose end_session_endpoint in its discovery document.
                // Suppress RP-initiated logout: sign-out is local (cookie removal only).
                options.SignedOutCallbackPath = "/signout-callback-oidc";
            });

        services.AddAuthorization();

        // Bind strongly-typed options for use by the authorization layer.
        services.Configure<CatalogAuthorizationOptions>(
            configuration.GetSection(CatalogAuthorizationOptions.SectionName));

        services.AddOidcUserContext();
        services.AddCollectionAuthorization();
        services.AddCollectionResourceAuthorization();

        return services;
    }
}
