using Catalog3d.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace Catalog3d.Web.Auth;

/// <summary>
/// Minimal HTTP endpoints for dev-mode sign-in and sign-out.
/// These are form-POST endpoints consumed by the Blazor DevLogin page.
/// Active only when Auth:Provider = Dev.
///
/// POST /auth/dev-login  — validates the submitted username against DevUsers config,
///                         signs the user into DevCookieScheme, redirects to returnUrl.
/// POST /auth/dev-logout — clears the DevCookie and redirects to /login/dev.
/// </summary>
internal static class DevLoginEndpoints
{
    internal static WebApplication MapDevLoginEndpoints(this WebApplication app)
    {
        // Both endpoints bypass antiforgery because the DevLogin page uses a plain HTML form
        // (not a Blazor EditForm with antiforgery token). This is acceptable for a dev-only
        // flow that is not active in production.
        app.MapPost("/auth/dev-login", HandleLoginAsync)
            .DisableAntiforgery()
            .AllowAnonymous();

        app.MapPost("/auth/dev-logout", (Delegate)HandleLogoutAsync)
            .DisableAntiforgery()
            .AllowAnonymous();

        return app;
    }

    private static async Task<IResult> HandleLoginAsync(
        HttpContext httpContext,
        IOptions<DevAuthSchemeOptions> devOptions,
        CancellationToken cancellationToken)
    {
        var form = await httpContext.Request.ReadFormAsync(cancellationToken);
        var username = form["username"].FirstOrDefault()?.Trim();
        var returnUrl = form["returnUrl"].FirstOrDefault();

        if (string.IsNullOrEmpty(username) || !devOptions.Value.Users.TryGetValue(username, out var entry))
        {
            // Unknown user — redirect back to login with an error indicator.
            var errorRedirect = string.IsNullOrEmpty(returnUrl)
                ? "/login/dev?error=invalid"
                : $"/login/dev?error=invalid&returnUrl={Uri.EscapeDataString(returnUrl)}";
            return Results.Redirect(errorRedirect);
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, username),
            new(ClaimTypes.Name, entry.DisplayName),
        };

        foreach (var group in entry.Groups)
            claims.Add(new Claim(DevAuthHandler.GroupsClaimType, group));

        var identity = new ClaimsIdentity(claims, DevAuthServiceRegistration.DevCookieScheme);
        var principal = new ClaimsPrincipal(identity);

        await httpContext.SignInAsync(
            DevAuthServiceRegistration.DevCookieScheme,
            principal,
            new AuthenticationProperties { IsPersistent = false });

        // Validate returnUrl to prevent open redirect: only allow relative paths.
        var redirectTo = IsLocalUrl(returnUrl) ? returnUrl! : "/";
        return Results.Redirect(redirectTo);
    }

    private static async Task<IResult> HandleLogoutAsync(HttpContext httpContext)
    {
        await httpContext.SignOutAsync(DevAuthServiceRegistration.DevCookieScheme);
        return Results.Redirect("/login/dev");
    }

    private static bool IsLocalUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        // Reject absolute URLs and protocol-relative URLs.
        return url.StartsWith('/') && !url.StartsWith("//");
    }
}
