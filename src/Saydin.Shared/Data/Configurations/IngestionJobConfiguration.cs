using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data.Configurations;

public sealed class IngestionJobConfiguration : IEntityTypeConfiguration<IngestionJob>
{
    public void Configure(EntityTypeBuilder<IngestionJob> builder)
    {
        builder.ToTable("ingestion_jobs");
        builder.HasKey(j => j.Id);

        builder.Property(j => j.JobType).HasMaxLength(50).IsRequired();
        builder.Property(j => j.Status).HasMaxLength(20).IsRequired();
        builder.Property(j => j.ErrorMessage).HasColumnType("text");

        // started_at: DB tarafında DEFAULT NOW() var. Entity'de de varsayılan veriyoruz
        // (StartedAt = UtcNow). Bilinçli: kod ve DB aynı anlamı verir, ek round-trip yok.

        builder.HasOne(j => j.Asset)
               .WithMany()
               .HasForeignKey(j => j.AssetId)
               .HasConstraintName("fk_ingestion_jobs_asset");

        builder.HasIndex(j => new { j.AssetId, j.Status, j.StartedAt })
               .HasDatabaseName("idx_ingestion_jobs_asset_status")
               .IsDescending(false, false, true);
    }
}
