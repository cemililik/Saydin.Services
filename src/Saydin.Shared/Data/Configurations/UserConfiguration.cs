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
        // SHRD-010 follow-up: DB CHECK case-sensitive ('free' | 'premium'); `UserTiers.All`
        // C# tarafında `OrdinalIgnoreCase`'tir ama DB'ye yazma aşamasında daima
        // `UserTiers.Free` / `UserTiers.Premium` literal sabitleri kullanılır
        // (Repository.CreateAsync ve user create path'leri). HasDefaultValue da
        // lowercase sabittir; mixed-case sızıntısı engellenir.
        builder.ToTable("users", t => t.HasCheckConstraint(
            "chk_users_tier",
            $"tier IN ({string.Join(", ", UserTiers.All.Select(v => $"'{v}'"))})"));
        builder.HasKey(u => u.Id);

        builder.Property(u => u.DeviceId).HasMaxLength(200);
        builder.Property(u => u.Email).HasMaxLength(200);
        builder.Property(u => u.Tier).HasMaxLength(20).IsRequired().HasDefaultValue(UserTiers.Free);
        builder.Property(u => u.PrincipalStatus)
            .HasMaxLength(32)
            .IsRequired()
            .HasDefaultValue("legacy_quarantined");
        builder.Property(u => u.PrincipalContractVersion).IsRequired().HasDefaultValue(1);
        builder.Property(u => u.PrincipalQuarantinedAt)
            .HasDefaultValueSql("statement_timestamp()");

        builder.ToTable("users", table =>
        {
            table.HasCheckConstraint(
                "chk_users_principal_status",
                "principal_status IN ('legacy_quarantined', 'active', 'revoked')");
            table.HasCheckConstraint(
                "chk_users_principal_contract_version",
                "principal_contract_version > 0");
            table.HasCheckConstraint(
                "chk_users_principal_lifecycle",
                "(principal_status = 'legacy_quarantined' AND principal_quarantined_at IS NOT NULL AND principal_revoked_at IS NULL) OR " +
                "(principal_status = 'active' AND device_id IS NULL AND principal_quarantined_at IS NULL AND principal_revoked_at IS NULL) OR " +
                "(principal_status = 'revoked' AND principal_revoked_at IS NOT NULL)");
            table.HasCheckConstraint(
                "chk_users_principal_expiry",
                "principal_expires_at IS NULL OR principal_expires_at > created_at");
        });

        // F2.5-5 / F2.7-2 ([C-E-14], [C-G-001-5]): Email ve DeviceId UNIQUE constraint'leri
        // partial olmalı — aksi halde PostgreSQL aynı NULL'u iki kez kabul etmez ve
        // anonim kullanıcı oluşturulamaz. Kod tarafı HasFilter ile partial unique
        // index modelliyor.
        // SHRD-018 follow-up: karşılık gelen migration dosyası `011_phase2_schema_hardening.sql`.
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
