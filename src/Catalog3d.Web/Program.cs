using Catalog3d.Infrastructure.Rendering;
using Catalog3d.Web.Auth;
using Catalog3d.Web.Blazor;
using Catalog3d.Web.Endpoints;
using Catalog3d.Web.Persistence;
using Catalog3d.Web.Rendering;
using Catalog3d.Web.Storage;
using Microsoft.FluentUI.AspNetCore.Components;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddCatalogPersistence(builder.Configuration);
builder.Services.AddDiskFileStorage(builder.Configuration);
builder.Services.AddRenderingServices(builder.Configuration);
builder.Services.Configure<ViewerOptions>(
    builder.Configuration.GetSection(ViewerOptions.SectionName));

// Auth:Provider selects the active authentication scheme.
// Both registrations are always compiled in; only one is wired at runtime.
//   Dev  — X-Dev-User header, config-hardcoded users. Default for local development.
//   Oidc — OpenIdConnect against Authelia. Required for production.
var authProvider = builder.Configuration["Auth:Provider"] ?? "Dev";
if (authProvider.Equals("Oidc", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddOidcAuthentication(builder.Configuration, builder.Environment);
}
else
{
    builder.Services.AddDevAuthentication(builder.Configuration);
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

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

// REST API endpoints (external contract — stable versioned URLs).
app.MapCatalogEndpoints();

// Blazor Server admin UI. Routes declared in Admin/Pages/*.razor.
// DO NOT add routes here — add @page directives to page components.
app.MapBlazorAdmin();

app.Run();

// Expose for WebApplicationFactory in integration tests.
public partial class Program { }
