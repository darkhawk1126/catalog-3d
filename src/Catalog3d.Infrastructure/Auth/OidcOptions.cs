namespace Catalog3d.Infrastructure.Auth;

/// <summary>
/// Strongly-typed options for the OIDC authentication scheme.
/// Bound from config section "Oidc" (Auth:Provider = Oidc activates this scheme).
///
/// Config key map:
///   Oidc:Authority               — issuer base URL; discovery appends /.well-known/openid-configuration
///   Oidc:ClientId                — OIDC client_id registered in Authelia
///   Oidc:ClientSecret            — plaintext secret (k8s SealedSecret in prod; env var or user-secrets in dev)
///   Oidc:CallbackPath            — default /signin-oidc; override only if host-path routing requires it
///   Oidc:RequireHttpsMetadata    — set false in Development for local Authelia over http
///   Oidc:Scopes                  — extra scopes beyond "openid"; must include "groups" for Authelia groups claim
/// </summary>
public sealed class OidcOptions
{
    public const string SectionName = "Oidc";

    /// <summary>
    /// Authelia issuer URL. Prod: https://login.mallcop.dev
    /// Dev: http://localhost:9091 (docker-compose local Authelia)
    /// </summary>
    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>ASP.NET Core callback path. Default: /signin-oidc</summary>
    public string CallbackPath { get; set; } = "/signin-oidc";

    /// <summary>
    /// Set false in Development when local Authelia runs over plain HTTP.
    /// The OIDC registration reads this from the options, not from IHostEnvironment,
    /// so the calling registration code is responsible for defaulting it based on
    /// IHostEnvironment.IsDevelopment() when absent from config.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Additional scopes appended to "openid". Must contain "groups" so that the
    /// UserInfo endpoint returns the Authelia groups claim.
    /// Default: ["profile", "email", "groups"]
    /// </summary>
    public List<string> Scopes { get; set; } = ["profile", "email", "groups"];
}
