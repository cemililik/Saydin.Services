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
                $"job_type IN ('{IngestionJobTypes.HistoricalBackfill}', '{IngestionJobTypes.DailyUpdate}', '{IngestionJobTypes.InflationBackfill}', '{IngestionJobTypes.InflationDaily}')");
            t.HasCheckConstraint(
                "chk_ingestion_jobs_status",
                $"status IN ('{IngestionJobStatuses.Running}', '{IngestionJobStatuses.Success}', '{IngestionJobStatuses.Failed}')");
        });
        builder.HasKey(j => j.Id);

        builder.Property(j => j.JobType).HasMaxLength(50).IsRequired();
        builder.Property(j => j.Status).HasMaxLength(20).IsRequired();
        builder.Property(j => j.ErrorMessage).HasColumnType("text");
        // INGR-002 (migration 012): provenance kolonu (nullable).
        builder.Property(j => j.Source).HasMaxLength(30);
        builder.Property(j => j.OutcomeCode).HasMaxLength(80);

        // SHRD-019: started_at DB-side DEFAULT NOW() — EF Configuration ile sync (drift yok).
        builder.Property(j => j.StartedAt).HasDefaultValueSql("NOW()");

        // INGR-002: AssetId nullable (inflation job'larında null). Guid? FK olduğu için
        // EF ilişkiyi otomatik optional yapar; ON DELETE RESTRICT migration tarafında.
        builder.HasOne(j => j.Asset)
               .WithMany()
               .HasForeignKey(j => j.AssetId)
               .OnDelete(DeleteBehavior.Restrict)
               .HasConstraintName("fk_ingestion_jobs_asset");

        builder.HasIndex(j => new { j.AssetId, j.Status, j.StartedAt })
               .HasDatabaseName("idx_ingestion_jobs_asset_status")
               .IsDescending(false, false, true);

        builder.HasOne(j => j.Window)
               .WithMany()
               .HasForeignKey(j => j.WindowId)
               .OnDelete(DeleteBehavior.Restrict)
               .HasConstraintName("fk_ingestion_jobs_window");

        builder.HasIndex(j => new { j.WindowId, j.StartedAt })
               .HasDatabaseName("idx_ingestion_jobs_window_started")
               .HasFilter("window_id IS NOT NULL")
               .IsDescending(false, true);
    }
}
