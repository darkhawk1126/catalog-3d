using Catalog3d.Web.Auth;
using Catalog3d.Web.Endpoints;
using Catalog3d.Web.Persistence;
using Catalog3d.Web.Storage;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddCatalogPersistence(builder.Configuration);
builder.Services.AddDiskFileStorage(builder.Configuration);
builder.Services.AddDevAuthentication(builder.Configuration);

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
