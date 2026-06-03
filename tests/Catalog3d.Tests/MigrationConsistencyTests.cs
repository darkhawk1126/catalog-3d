using Catalog3d.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Catalog3d.Tests;

/// <summary>
/// Guards against EF model/migration drift — the failure mode where the entity model is
/// changed (e.g. an index) but no matching migration (with its .Designer snapshot) is added,
/// which compiles and passes every in-memory test yet crash-loops the real app at migrate-on-
/// startup with PendingModelChangesWarning. The rest of the suite never boots against a relational
/// provider's migrate path, so this is the one check that catches it.
/// </summary>
public sealed class MigrationConsistencyTests
{
    [Fact]
    public void Model_HasNoPendingChanges_AgainstMigrationsSnapshot()
    {
        // No connection is opened — HasPendingModelChanges compares the runtime model against the
        // migrations-assembly snapshot at design time. The connection string is never used.
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=unused;Database=unused;Username=unused;Password=unused")
            .Options;

        using var context = new CatalogDbContext(options);

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The EF model has changes not captured in a migration. Run "
                + "'dotnet ef migrations add <Name>' (which also writes the paired .Designer.cs) "
                + "instead of hand-editing migrations or the model snapshot.");
    }
}
