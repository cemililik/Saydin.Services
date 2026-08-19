using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data.Configurations;

public sealed class InstallationCredentialConfiguration : IEntityTypeConfiguration<InstallationCredential>
{
    public void Configure(EntityTypeBuilder<InstallationCredential> builder)
    {
        builder.ToTable("installation_credentials", table =>
        {
            table.HasCheckConstraint("chk_installation_credentials_generation", "generation > 0");
            table.HasCheckConstraint("chk_installation_credentials_hash_key_version", "hash_key_version > 0");
            table.HasCheckConstraint(
                "chk_installation_credentials_secret_hash",
                "octet_length(secret_hash) = 32 AND secret_hash <> decode(repeat('00', 32), 'hex')");
            table.HasCheckConstraint(
                "chk_installation_credentials_state",
                "state IN ('pending', 'active', 'revoked')");
            table.HasCheckConstraint(
                "chk_installation_credentials_lifecycle",
                "(state = 'pending' AND activated_at IS NULL AND revoked_at IS NULL AND pending_expires_at IS NOT NULL AND pending_expires_at > issued_at) OR " +
                "(state = 'active' AND activated_at IS NOT NULL AND revoked_at IS NULL AND pending_expires_at IS NULL) OR " +
                "(state = 'revoked' AND revoked_at IS NOT NULL)");
            table.HasCheckConstraint(
                "chk_installation_credentials_expiry",
                "expires_at IS NULL OR expires_at > issued_at");
            table.HasCheckConstraint(
                "chk_installation_credentials_rotation",
                "(rotation_id IS NULL AND rotation_parent_id IS NULL) OR (rotation_id IS NOT NULL AND rotation_parent_id IS NOT NULL)");
        });

        builder.HasKey(credential => credential.Id);
        builder.Property(credential => credential.SecretHash).HasColumnType("bytea").IsRequired();
        builder.Property(credential => credential.State).HasMaxLength(16).IsRequired();

        builder.HasIndex(credential => new { credential.HashKeyVersion, credential.SecretHash })
            .IsUnique()
            .HasDatabaseName("uq_installation_credentials_verifier");
        builder.HasIndex(credential => new { credential.PrincipalId, credential.Generation })
            .IsUnique()
            .HasDatabaseName("uq_installation_credentials_generation");
        builder.HasIndex(credential => credential.PrincipalId)
            .IsUnique()
            .HasDatabaseName("uq_installation_credentials_active_principal")
            .HasFilter("state = 'active'");
        // EF Core coalesces two indexes over the same property list even when their
        // PostgreSQL predicates differ. Migration 021 remains authoritative for the
        // second exact partial index; keep an explicit model annotation so schema
        // audits cannot silently forget that database-only boundary.
        builder.Metadata.SetAnnotation(
            "Saydin:DatabaseIndex:uq_installation_credentials_pending_principal",
            "UNIQUE (principal_id) WHERE state = 'pending'");
        builder.HasIndex(credential => credential.RotationId)
            .IsUnique()
            .HasDatabaseName("uq_installation_credentials_rotation_id")
            .HasFilter("rotation_id IS NOT NULL");

        builder.HasOne(credential => credential.Principal)
            .WithMany(principal => principal.InstallationCredentials)
            .HasForeignKey(credential => credential.PrincipalId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(credential => credential.RotationParent)
            .WithMany()
            .HasForeignKey(credential => credential.RotationParentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
