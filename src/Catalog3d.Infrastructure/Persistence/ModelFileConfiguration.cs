using Catalog3d.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog3d.Infrastructure.Persistence;

internal sealed class ModelFileConfiguration : IEntityTypeConfiguration<ModelFile>
{
    public void Configure(EntityTypeBuilder<ModelFile> builder)
    {
        builder.ToTable("model_files");

        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).HasColumnName("id");

        builder.Property(f => f.ModelId)
            .HasColumnName("model_id")
            .IsRequired();

        builder.Property(f => f.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        // Content-addressed key: SHA-256 hex (64 chars). Fixed length avoids over-allocation.
        builder.Property(f => f.BlobKey)
            .HasColumnName("blob_key")
            .HasMaxLength(64)
            .IsFixedLength()
            .IsRequired();

        builder.Property(f => f.Size)
            .HasColumnName("size")
            .IsRequired();

        builder.Property(f => f.MimeType)
            .HasColumnName("mime_type")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(f => f.Sha256)
            .HasColumnName("sha256")
            .HasMaxLength(64)
            .IsFixedLength()
            .IsRequired();

        builder.Property(f => f.TriCount)
            .HasColumnName("tri_count");

        builder.Property(f => f.BoundingBox)
            .HasColumnName("bounding_box");

        builder.Property(f => f.RenderStatus)
            .HasColumnName("render_status")
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(f => f.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(f => f.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // BlobKey uniqueness enforces content-addressed dedup at the database level.
        // A single blob (same SHA-256) can appear only once per model to prevent redundant storage.
        builder.HasIndex(f => new { f.ModelId, f.BlobKey })
            .IsUnique()
            .HasDatabaseName("ix_model_files_model_blobkey");
    }
}
