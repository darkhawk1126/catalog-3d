using Catalog3d.Application.Abstractions;
using Catalog3d.Infrastructure.Rendering;
using Catalog3d.Infrastructure.Upload;
using Catalog3d.Web.Auth;
using Catalog3d.Web.Blazor;
using Catalog3d.Web.Endpoints;
using Catalog3d.Web.Persistence;
using Catalog3d.Web.Rendering;
using Catalog3d.Web.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.FluentUI.AspNetCore.Components;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddCatalogPersistence(builder.Configuration);
builder.Services.AddDiskFileStorage(builder.Configuration);
builder.Services.AddRenderingServices(builder.Configuration);
builder.Services.AddModelUploadService(builder.Configuration);
builder.Services.Configure<ViewerOptions>(
    builder.Configuration.GetSection(ViewerOptions.SectionName));

// Auth:Provider selects the active authentication scheme.
// Both registrations are always compiled in; only one is wired at runtime.
//   Dev  — X-Dev-User header, config-hardcoded users. Development only (H2: fail closed).
//   Oidc — OpenIdConnect against Authelia. Required for production.
var authProvider = builder.Configuration["Auth:Provider"] ?? "Dev";
if (authProvider.Equals("Oidc", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddOidcAuthentication(builder.Configuration, builder.Environment);
}
else if (authProvider.Equals("Dev", StringComparison.OrdinalIgnoreCase))
{
    // H2: Dev auth is a security-sensitive shortcut; refuse to use it outside Development
    // to prevent hardcoded credentials from being activated in staging or production.
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "Auth:Provider='Dev' is only permitted in the Development environment. " +
            "Set Auth:Provider=Oidc in non-Development environments.");
    }

    builder.Services.AddDevAuthentication(builder.Configuration);
}
else
{
    throw new InvalidOperationException(
        $"Unknown Auth:Provider value '{authProvider}'. Valid values: Dev, Oidc.");
}

// Blazor Server admin UI + FluentUI component library.
// AddBlazorAdmin registers RazorComponents, InteractiveServer render mode,
// CascadingAuthenticationState, and AdminAuthHelper.
builder.Services.AddBlazorAdmin();

// FluentUI Blazor component library services (IToastService, IDialogService, etc.).
// Scoped lifetime is the default and correct choice for Blazor Server circuits.
builder.Services.AddFluentUIComponents();

// Antiforgery is required by Blazor Server interactive render mode.
builder.Services.AddAntiforgery();

// DataProtection key ring persistence. In Development the default in-memory/ephemeral key
// ring is fine (auth cookies + anti-forgery tokens are expected not to survive a restart).
// In production the key ring MUST be persisted to durable storage, or every pod restart /
// replica invalidates all existing auth cookies. Set DataProtection:KeyRingPath to a mounted
// volume (PVC) to enable filesystem persistence; SetApplicationName keeps the ring stable
// across replicas sharing that path.
var keyRingPath = builder.Configuration["DataProtection:KeyRingPath"];
if (!string.IsNullOrWhiteSpace(keyRingPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
        .SetApplicationName("catalog-3d");
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// L8: security headers — applied to every response before any content is written.
// The set of origins permitted to iframe the viewer is config-driven (Viewer:FrameAncestors)
// so the deployed wiki host can be set per environment without a code change.
var viewerFrameAncestors = app.Services
    .GetRequiredService<Microsoft.Extensions.Options.IOptions<ViewerOptions>>()
    .Value.FrameAncestors;

app.Use(async (ctx, next) =>
{
    // Prevent MIME sniffing that could turn a benign blob download into an executable.
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";

    // L8: HSTS — only meaningful over HTTPS; browser enforces HTTPS for subsequent requests.
    // max-age=31536000 (1 year) is the HSTS Preload minimum.
    if (!ctx.Request.IsHttps && !app.Environment.IsDevelopment())
    {
        // Skip HSTS header on plain-HTTP in dev to avoid breaking the local docker-compose setup.
    }
    else if (ctx.Request.IsHttps)
    {
        ctx.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    }

    // L8: frame-ancestors — explicit decision for wiki embedding.
    // The viewer routes (/viewer and /viewer/embed/*) must be embeddable from the wiki;
    // all other routes disallow framing to prevent clickjacking. The permitted origins come
    // from Viewer:FrameAncestors (defaults to the homelab wiki hosts).
    if (ctx.Request.Path.StartsWithSegments("/viewer", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.Headers["Content-Security-Policy"] = $"frame-ancestors {viewerFrameAncestors}";
        ctx.Response.Headers["X-Frame-Options"] = "ALLOWALL"; // superseded by CSP; kept for older clients
    }
    else
    {
        ctx.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
    }

    await next();
});

app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

// Auto-provision each authenticated principal's personal collection on first sighting.
// Runs after authentication so ctx.User is populated; the provisioner self-suppresses for
// already-provisioned principals (singleton cache), so this is a no-op on the hot path.
app.Use(async (ctx, next) =>
{
    if (ctx.User.Identity?.IsAuthenticated is true)
    {
        var userContext = ctx.RequestServices.GetRequiredService<IUserContext>();
        var provisioner = ctx.RequestServices.GetRequiredService<IPersonalCollectionProvisioner>();
        await provisioner.EnsureProvisionedAsync(userContext, ctx.RequestAborted);
    }

    await next();
});

// L9: /challenge triggers an ASP.NET Core challenge against the default challenge scheme
// (OidcScheme when Auth:Provider=Oidc). RedirectToLogin and DevLogin.razor both reference
// this route for OIDC sign-in. The challenge handler issues the browser redirect to Authelia.
app.MapGet("/challenge", async (HttpContext ctx) =>
{
    var returnUrl = ctx.Request.Query["returnUrl"].FirstOrDefault() ?? "/";
    await ctx.ChallengeAsync(new AuthenticationProperties { RedirectUri = returnUrl });
}).AllowAnonymous();

// REST API endpoints (external contract — stable versioned URLs).
// Dev-mode login endpoints are mapped inside MapCatalogEndpoints when Auth:Provider=Dev.
app.MapCatalogEndpoints();

// Blazor Server admin UI. Routes declared in Admin/Pages/*.razor.
// DO NOT add routes here — add @page directives to page components.
app.MapBlazorAdmin();

app.Run();

// Expose for WebApplicationFactory in integration tests.
public partial class Program { }
