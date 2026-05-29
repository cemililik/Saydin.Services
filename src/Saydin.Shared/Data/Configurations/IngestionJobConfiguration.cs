using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data.Configurations;

public sealed class IngestionJobConfiguration : IEntityTypeConfiguration<IngestionJob>
{
    public void Configure(EntityTypeBuilder<IngestionJob> builder)
    {
        // SHRD-001: 001 migration'da tanımlı CHECK constraint'ler EF tarafında modelleniyor;
        // aksi halde Add-Migration "drop check constraint" üretebilirdi. Migration 011
        // bu CHECK'leri `inflation_backfill | inflation_daily` ekleyerek genişletti.
        builder.ToTable("ingestion_jobs", t =>
        {
            t.HasCheckConstraint(
                "chk_ingestion_jobs_type",
                $"job_type IN ('{IngestionJobTypes.HistoricalBackfill}', '{IngestionJobTypes.DailyUpdate}', 'inflation_backfill', 'inflation_daily')");
            t.HasCheckConstraint(
                "chk_ingestion_jobs_status",
                $"status IN ('{IngestionJobStatuses.Running}', '{IngestionJobStatuses.Success}', '{IngestionJobStatuses.Failed}')");
        });
        builder.HasKey(j => j.Id);

        builder.Property(j => j.JobType).HasMaxLength(50).IsRequired();
        builder.Property(j => j.Status).HasMaxLength(20).IsRequired();
        builder.Property(j => j.ErrorMessage).HasColumnType("text");

        // SHRD-019: started_at DB-side DEFAULT NOW() — EF Configuration ile sync (drift yok).
        builder.Property(j => j.StartedAt).HasDefaultValueSql("NOW()");

        builder.HasOne(j => j.Asset)
               .WithMany()
               .HasForeignKey(j => j.AssetId)
               .HasConstraintName("fk_ingestion_jobs_asset");

        builder.HasIndex(j => new { j.AssetId, j.Status, j.StartedAt })
               .HasDatabaseName("idx_ingestion_jobs_asset_status")
               .IsDescending(false, false, true);
    }
}
