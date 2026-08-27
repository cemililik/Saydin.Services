using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data.Configurations;

public sealed class IngestionWindowConfiguration : IEntityTypeConfiguration<IngestionWindow>
{
    public void Configure(EntityTypeBuilder<IngestionWindow> builder)
    {
        builder.ToTable("ingestion_windows", table =>
        {
            table.HasCheckConstraint("chk_ingestion_windows_range", "range_start <= range_end");
            table.HasCheckConstraint("chk_ingestion_windows_contract", "contract_version > 0");
            table.HasCheckConstraint("chk_ingestion_windows_attempt", "attempt_count >= 0");
            table.HasCheckConstraint("chk_ingestion_windows_counts",
                "requested_calendar_count >= 0 AND expected_observation_count >= 0 AND raw_item_count >= 0 AND accepted_distinct_count >= 0 AND rejected_count >= 0 AND expected_no_data_count >= 0 AND expected_observation_count <= requested_calendar_count AND accepted_distinct_count <= raw_item_count");
            table.HasCheckConstraint("chk_ingestion_windows_terminal_completeness",
                "(state = 'succeeded' AND accepted_distinct_count > 0 AND rejected_count = 0 AND accepted_distinct_count = expected_observation_count AND expected_no_data_count = requested_calendar_count - expected_observation_count) OR (state = 'expected_no_data' AND requested_calendar_count > 0 AND expected_observation_count = 0 AND accepted_distinct_count = 0 AND rejected_count = 0 AND expected_no_data_count = requested_calendar_count) OR state NOT IN ('succeeded','expected_no_data')");
            table.HasCheckConstraint("chk_ingestion_windows_state",
                "state IN ('pending','running','succeeded','expected_no_data','retryable_failed','permanent_failed','cancelled','abandoned')");
            table.HasCheckConstraint("chk_ingestion_windows_lease",
                "(state = 'running' AND lease_owner IS NOT NULL AND lease_token IS NOT NULL AND lease_until IS NOT NULL) OR (state <> 'running' AND lease_owner IS NULL AND lease_token IS NULL AND lease_until IS NULL)");
            table.HasCheckConstraint("chk_ingestion_windows_completed",
                "(state IN ('succeeded','expected_no_data','permanent_failed') AND completed_at IS NOT NULL) OR (state NOT IN ('succeeded','expected_no_data','permanent_failed') AND completed_at IS NULL)");
            table.HasCheckConstraint("chk_ingestion_windows_outcome_codes",
                "(state IN ('pending','running') AND outcome_code IS NULL) OR (state IN ('succeeded','expected_no_data','retryable_failed','permanent_failed','cancelled','abandoned') AND outcome_code IS NOT NULL)");
            table.HasCheckConstraint("chk_ingestion_windows_error_codes",
                "(state IN ('retryable_failed','permanent_failed') AND error_code IS NOT NULL) OR (state NOT IN ('retryable_failed','permanent_failed') AND error_code IS NULL)");
        });
        builder.HasKey(window => window.Id);
        builder.Property(window => window.Source).HasMaxLength(30).IsRequired();
        builder.Property(window => window.JobType).HasMaxLength(50).IsRequired();
        builder.Property(window => window.State).HasMaxLength(30).IsRequired();
        builder.Property(window => window.LeaseOwner).HasMaxLength(120);
        builder.Property(window => window.OutcomeCode).HasMaxLength(80);
        builder.Property(window => window.ErrorCode).HasMaxLength(80);
        builder.Property(window => window.CreatedAt).HasDefaultValueSql("NOW()");
        builder.Property(window => window.UpdatedAt).HasDefaultValueSql("NOW()");
        builder.Property(window => window.NextAttemptAt).HasDefaultValueSql("NOW()");
        builder.HasOne(window => window.Asset)
            .WithMany()
            .HasForeignKey(window => window.AssetId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_ingestion_windows_asset");
        builder.HasOne(window => window.CalendarRelease)
            .WithMany()
            .HasForeignKey(window => window.CalendarReleaseId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_ingestion_windows_calendar_release");
        builder.HasIndex(window => new
            {
                window.Source, window.AssetId, window.JobType,
                window.RangeStart, window.RangeEnd, window.ContractVersion,
            })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("uq_ingestion_windows_logical");
        builder.HasIndex(window => new
            {
                window.Source, window.AssetId, window.JobType,
                window.ContractVersion, window.RangeStart, window.RangeEnd,
            })
            .HasDatabaseName("idx_ingestion_windows_claim")
            .HasFilter("state NOT IN ('succeeded', 'expected_no_data')");
        builder.HasIndex(window => window.LeaseUntil)
            .HasDatabaseName("idx_ingestion_windows_lease_expiry")
            .HasFilter("state = 'running'");
        builder.HasIndex(window => window.CalendarReleaseId)
            .HasDatabaseName("idx_ingestion_windows_calendar_release")
            .HasFilter("calendar_release_id IS NOT NULL");
    }
}
