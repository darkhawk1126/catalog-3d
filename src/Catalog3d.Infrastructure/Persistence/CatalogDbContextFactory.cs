using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Catalog3d.Infrastructure.Persistence;

/// <summary>
/// Provides a CatalogDbContext for EF Core tooling (migrations, scaffolding).
/// Reads appsettings.json from the Web project, which is the authoritative config source.
/// A CONNECTIONSTRINGS__CATALOGDB environment variable overrides the file-based value,
/// enabling CI and container-based migration runs without the sibling directory assumption.
/// </summary>
public sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        // The EF tooling sets the working directory to the project root when running dotnet-ef.
        // Resolve the Web project's appsettings relative to the Infrastructure project root.
        var infraRoot = AppContext.BaseDirectory;
        var webSettingsPath = Path.GetFullPath(
            Path.Combine(infraRoot, "..", "..", "..", "..", "Catalog3d.Web"));

        var configuration = new ConfigurationBuilder()
            .SetBasePath(webSettingsPath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("CatalogDb")
            ?? throw new InvalidOperationException(
                "Connection string 'CatalogDb' not found. " +
                "Set ConnectionStrings__CatalogDb or ensure appsettings.json is present in Catalog3d.Web.");

        var optionsBuilder = new DbContextOptionsBuilder<CatalogDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        return new CatalogDbContext(optionsBuilder.Options);
    }
}
