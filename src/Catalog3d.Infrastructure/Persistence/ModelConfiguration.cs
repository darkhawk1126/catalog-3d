using Catalog3d.Domain.Entities;
using Catalog3d.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog3d.Infrastructure.Persistence;

internal sealed class ModelConfiguration : IEntityTypeConfiguration<Model>
{
    public void Configure(EntityTypeBuilder<Model> builder)
    {
        builder.ToTable("models");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnName("id");

        builder.Property(m => m.CollectionId)
            .HasColumnName("collection_id")
            .IsRequired();

        builder.Property(m => m.Slug)
            .HasColumnName("slug")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(m => m.Name)
            .HasColumnName("name")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(m => m.Description)
            .HasColumnName("description")
            .IsRequired();

        builder.Property(m => m.Owner)
            .HasColumnName("owner")
            .HasMaxLength(500)
            .IsRequired();

        // Stored as string so schema reads without enum knowledge; ordinal sort on string is not needed.
        builder.Property(m => m.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        // Stored as string (like Status) for human-readable schema reads. Defaults to Private so
        // pre-existing rows and new uploads are owner-only until explicitly shared/published.
        builder.Property(m => m.Visibility)
            .HasColumnName("visibility")
            .HasConversion<string>()
            .HasMaxLength(50)
            .HasDefaultValue(ModelVisibility.Private)
            .IsRequired();

        builder.Property(m => m.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(m => m.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Slug is a stable, globally unique public identifier (e.g. /models/{slug} for wiki links).
        // The per-(collection,slug) index is replaced by a global unique index on slug alone.
        builder.HasIndex(m => m.Slug)
            .IsUnique()
            .HasDatabaseName("ix_models_slug");

        builder.HasMany(m => m.Files)
            .WithOne(f => f.Model)
            .HasForeignKey(f => f.ModelId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(m => m.Shares)
            .WithOne(s => s.Model)
            .HasForeignKey(s => s.ModelId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
