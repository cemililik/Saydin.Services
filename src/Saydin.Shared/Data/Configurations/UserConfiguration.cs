using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saydin.Shared.Constants;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        // F2.5-4 ([C-E-13]): users.tier CHECK constraint kod tarafında modellenir.
        // UserTiers.All ile birebir aynı kalmalı.
        builder.ToTable("users", t => t.HasCheckConstraint(
            "chk_users_tier",
            $"tier IN ({string.Join(", ", UserTiers.All.Select(v => $"'{v}'"))})"));
        builder.HasKey(u => u.Id);

        builder.Property(u => u.DeviceId).HasMaxLength(200);
        builder.Property(u => u.Email).HasMaxLength(200);
        builder.Property(u => u.Tier).HasMaxLength(20).IsRequired().HasDefaultValue(UserTiers.Free);

        // F2.5-5 / F2.7-2 ([C-E-14], [C-G-001-5]): Email ve DeviceId UNIQUE constraint'leri
        // partial olmalı — aksi halde PostgreSQL aynı NULL'u iki kez kabul etmez ve
        // anonim kullanıcı oluşturulamaz. Kod tarafı HasFilter ile partial unique
        // index modelliyor; karşılık gelen migration 011_partial_unique_users.sql.
        builder.HasIndex(u => u.DeviceId)
            .IsUnique()
            .HasDatabaseName("uq_users_device_id")
            .HasFilter("device_id IS NOT NULL");
        builder.HasIndex(u => u.Email)
            .IsUnique()
            .HasDatabaseName("uq_users_email")
            .HasFilter("email IS NOT NULL");
    }
}
