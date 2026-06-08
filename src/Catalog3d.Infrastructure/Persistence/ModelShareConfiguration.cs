using Catalog3d.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog3d.Infrastructure.Persistence;

internal sealed class ModelShareConfiguration : IEntityTypeConfiguration<ModelShare>
{
    public void Configure(EntityTypeBuilder<ModelShare> builder)
    {
        builder.ToTable("model_shares");

        // Composite PK: a principal is shared into a model at most once.
        builder.HasKey(s => new { s.ModelId, s.Principal });

        builder.Property(s => s.ModelId)
            .HasColumnName("model_id")
            .IsRequired();

        builder.Property(s => s.Principal)
            .HasColumnName("principal")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(s => s.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        // Supports the "is principal X shared into model M?" lookup in ModelAuthorizationService,
        // which filters by ModelId and tests Principal IN (caller principals).
        builder.HasIndex(s => s.Principal)
            .HasDatabaseName("ix_model_shares_principal");
    }
}
