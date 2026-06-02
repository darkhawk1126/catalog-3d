using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace Catalog3d.Infrastructure.Auth;

/// <summary>
/// Dev-mode authentication handler. Resolves the caller from the X-Dev-User
/// request header and matches against configured DevUsers entries.
///
/// OIDC replacement: swap this handler for one that validates a bearer token;
/// the claim shape (sub, name, groups) is intentionally kept OIDC-compatible
/// so DevUserContext works against either scheme without modification.
/// </summary>
public sealed class DevAuthHandler : AuthenticationHandler<DevAuthSchemeOptions>
{
    // Claim type for group membership — matches the standard OIDC "groups" claim.
    internal const string GroupsClaimType = "groups";

    public DevAuthHandler(
        IOptionsMonitor<DevAuthSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers["X-Dev-User"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header))
            return Task.FromResult(AuthenticateResult.NoResult());

        var username = header.Trim();
        if (!Options.Users.TryGetValue(username, out var entry))
        {
            // Known header present but unknown user — fail explicitly so it is
            // clear in logs that a misconfigured client sent a bad username,
            // rather than silently falling through to anonymous.
            Logger.LogWarning("DevAuth: unknown dev user '{Username}' in X-Dev-User header", username);
            return Task.FromResult(AuthenticateResult.Fail($"Unknown dev user: {username}"));
        }

        var claims = new List<Claim>
        {
            // sub is the stable identity; use username since there are no GUIDs in dev config.
            new(ClaimTypes.NameIdentifier, username),
            new(ClaimTypes.Name, entry.DisplayName),
        };

        foreach (var group in entry.Groups)
            claims.Add(new Claim(GroupsClaimType, group));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
