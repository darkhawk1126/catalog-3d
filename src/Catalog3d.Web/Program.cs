using Catalog3d.Web.Auth;
using Catalog3d.Web.Endpoints;
using Catalog3d.Web.Persistence;
using Catalog3d.Web.Storage;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddCatalogPersistence(builder.Configuration);
builder.Services.AddDiskFileStorage(builder.Configuration);

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

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapCatalogEndpoints();

app.Run();

// Expose for WebApplicationFactory in integration tests.
public partial class Program { }
