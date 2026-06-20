using Catalog3d.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog3d.Infrastructure.Persistence;

internal sealed class KnownUserConfiguration : IEntityTypeConfiguration<KnownUser>
{
    public void Configure(EntityTypeBuilder<KnownUser> builder)
    {
        builder.ToTable("known_users");

        // Principal is the natural key — one row per identity, matching RoleAssignment.Principal
        // and ModelShare.Principal so picked entries write a grant with no translation.
        builder.HasKey(u => u.Principal);

        builder.Property(u => u.Principal)
            .HasColumnName("principal")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(u => u.Username)
            .HasColumnName("username")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(u => u.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(u => u.GroupsCsv)
            .HasColumnName("groups_csv")
            .IsRequired();

        builder.Property(u => u.FirstSeenAt)
            .HasColumnName("first_seen_at")
            .IsRequired();

        builder.Property(u => u.LastSeenAt)
            .HasColumnName("last_seen_at")
            .IsRequired();
    }
}
