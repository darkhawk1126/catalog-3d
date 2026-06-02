using Catalog3d.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog3d.Infrastructure.Persistence;

internal sealed class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        builder.ToTable("role_assignments");

        // Composite PK: one principal holds at most one role per collection.
        builder.HasKey(r => new { r.CollectionId, r.Principal });

        builder.Property(r => r.CollectionId)
            .HasColumnName("collection_id")
            .IsRequired();

        builder.Property(r => r.Principal)
            .HasColumnName("principal")
            .HasMaxLength(500)
            .IsRequired();

        // Stored as int; ordinal >= comparisons are semantically valid per domain design.
        builder.Property(r => r.Role)
            .HasColumnName("role")
            .HasConversion<int>()
            .IsRequired();

        // Support efficient "which collections does principal X have role >= Y on?" queries.
        builder.HasIndex(r => new { r.Principal, r.Role })
            .HasDatabaseName("ix_role_assignments_principal_role");
    }
}
