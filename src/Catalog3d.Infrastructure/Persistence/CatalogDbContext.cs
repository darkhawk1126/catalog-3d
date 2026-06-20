using Catalog3d.Domain.Entities;
using Catalog3d.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Catalog3d.Infrastructure.Persistence;

public sealed class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options) { }

    public DbSet<Collection> Collections { get; init; } = null!;
    public DbSet<Model> Models { get; init; } = null!;
    public DbSet<ModelFile> ModelFiles { get; init; } = null!;
    public DbSet<ModelShare> ModelShares { get; init; } = null!;
    public DbSet<RoleAssignment> RoleAssignments { get; init; } = null!;
    public DbSet<KnownUser> KnownUsers { get; init; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new CollectionConfiguration());
        modelBuilder.ApplyConfiguration(new ModelConfiguration());
        modelBuilder.ApplyConfiguration(new ModelFileConfiguration());
        modelBuilder.ApplyConfiguration(new ModelShareConfiguration());
        modelBuilder.ApplyConfiguration(new RoleAssignmentConfiguration());
        modelBuilder.ApplyConfiguration(new KnownUserConfiguration());
    }
}
