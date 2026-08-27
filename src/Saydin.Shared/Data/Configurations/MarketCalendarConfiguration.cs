using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data.Configurations;

public sealed class MarketCalendarConfiguration : IEntityTypeConfiguration<MarketCalendar>
{
    public void Configure(EntityTypeBuilder<MarketCalendar> builder)
    {
        builder.ToTable("market_calendars", table =>
        {
            table.HasCheckConstraint("chk_market_calendars_code", "code ~ '^[a-z0-9_]+$'");
            table.HasCheckConstraint(
                "chk_market_calendars_time_zone", "time_zone = 'Europe/Istanbul'");
        });
        builder.HasKey(calendar => calendar.Code);
        builder.Property(calendar => calendar.Code).HasMaxLength(60);
        builder.Property(calendar => calendar.Authority).HasMaxLength(120).IsRequired();
        builder.Property(calendar => calendar.TimeZone).HasMaxLength(60).IsRequired();
        builder.Property(calendar => calendar.CreatedAt).HasDefaultValueSql("NOW()");
    }
}

public sealed class MarketCalendarReleaseConfiguration : IEntityTypeConfiguration<MarketCalendarRelease>
{
    public void Configure(EntityTypeBuilder<MarketCalendarRelease> builder)
    {
        builder.ToTable("market_calendar_releases", table =>
        {
            table.HasCheckConstraint("chk_market_calendar_releases_version", "release_version > 0");
            table.HasCheckConstraint(
                "chk_market_calendar_releases_coverage", "coverage_from <= coverage_through");
            table.HasCheckConstraint(
                "chk_market_calendar_releases_row_count",
                "row_count = coverage_through - coverage_from + 1");
            table.HasCheckConstraint(
                "chk_market_calendar_releases_hashes",
                "normalized_sha256 ~ '^[0-9a-f]{64}$' AND source_bundle_sha256 ~ '^[0-9a-f]{64}$'");
        });
        builder.HasKey(release => release.Id);
        builder.Property(release => release.CalendarCode).HasMaxLength(60).IsRequired();
        builder.Property(release => release.SnapshotSetId).HasMaxLength(100).IsRequired();
        builder.Property(release => release.NormalizedSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(release => release.SourceBundleSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(release => release.CreatedAt).HasDefaultValueSql("NOW()");
        builder.HasOne(release => release.Calendar)
            .WithMany(calendar => calendar.Releases)
            .HasForeignKey(release => release.CalendarCode)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_market_calendar_releases_calendar");
        builder.HasIndex(release => new { release.CalendarCode, release.ReleaseVersion })
            .IsUnique().HasDatabaseName("uq_market_calendar_releases_calendar_version");
        builder.HasAlternateKey(release => new { release.CalendarCode, release.Id })
            .HasName("uq_market_calendar_releases_calendar_id");
    }
}

public sealed class MarketCalendarReleaseSourceConfiguration
    : IEntityTypeConfiguration<MarketCalendarReleaseSource>
{
    public void Configure(EntityTypeBuilder<MarketCalendarReleaseSource> builder)
    {
        builder.ToTable("market_calendar_release_sources", table =>
        {
            table.HasCheckConstraint(
                "chk_market_calendar_release_sources_role",
                "source_role IN ('authority','discovery','policy')");
            table.HasCheckConstraint(
                "chk_market_calendar_release_sources_hash", "raw_sha256 ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint(
                "chk_market_calendar_release_sources_month",
                "source_month IS NULL OR source_month BETWEEN 1 AND 12");
        });
        builder.HasKey(source => new { source.ReleaseId, source.SourceId });
        builder.Property(source => source.SourceId).HasMaxLength(100);
        builder.Property(source => source.SourceKind).HasMaxLength(50).IsRequired();
        builder.Property(source => source.SourceRole).HasMaxLength(30).IsRequired();
        builder.Property(source => source.SourceUri).IsRequired();
        builder.Property(source => source.MediaType).HasMaxLength(100).IsRequired();
        builder.Property(source => source.RawSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(source => source.SnapshotPath).IsRequired();
        builder.HasAlternateKey(source => new { source.ReleaseId, source.RawSha256 })
            .HasName("uq_market_calendar_release_sources_hash");
        builder.HasOne(source => source.Release)
            .WithMany(release => release.Sources)
            .HasForeignKey(source => source.ReleaseId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_market_calendar_release_sources_release");
    }
}

public sealed class MarketCalendarDayConfiguration : IEntityTypeConfiguration<MarketCalendarDay>
{
    public void Configure(EntityTypeBuilder<MarketCalendarDay> builder)
    {
        builder.ToTable("market_calendar_days", table =>
        {
            table.HasCheckConstraint(
                "chk_market_calendar_days_state",
                "market_state IN ('publication','no_publication','full_session','partial_session','closed')");
            table.HasCheckConstraint(
                "chk_market_calendar_days_semantics",
                "(observation_expected AND market_state IN ('publication','full_session','partial_session')) OR (NOT observation_expected AND market_state IN ('no_publication','closed'))");
        });
        builder.HasKey(day => new { day.ReleaseId, day.CalendarDate });
        builder.Property(day => day.MarketState).HasMaxLength(30).IsRequired();
        builder.Property(day => day.ReasonCode).HasMaxLength(60).IsRequired();
        builder.Property(day => day.EvidenceRawSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.HasOne(day => day.Release)
            .WithMany(release => release.Days)
            .HasForeignKey(day => day.ReleaseId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_market_calendar_days_release");
        builder.HasOne(day => day.EvidenceSource)
            .WithMany(source => source.EvidencedDays)
            .HasForeignKey(day => new { day.ReleaseId, day.EvidenceRawSha256 })
            .HasPrincipalKey(source => new { source.ReleaseId, source.RawSha256 })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_market_calendar_days_evidence");
    }
}

public sealed class AssetMarketCalendarConfiguration : IEntityTypeConfiguration<AssetMarketCalendar>
{
    public void Configure(EntityTypeBuilder<AssetMarketCalendar> builder)
    {
        builder.ToTable("asset_market_calendars", table => table.HasCheckConstraint(
            "chk_asset_market_calendars_source",
            "(source = 'tcmb' AND calendar_code = 'tcmb_indicative_fx') OR (source = 'twelvedata' AND calendar_code = 'bist_pay_xist')"));
        builder.HasKey(binding => binding.AssetId);
        builder.Property(binding => binding.Source).HasMaxLength(30).IsRequired();
        builder.Property(binding => binding.CalendarCode).HasMaxLength(60).IsRequired();
        builder.Property(binding => binding.BoundAt).HasDefaultValueSql("NOW()");
        builder.HasOne(binding => binding.Asset).WithOne()
            .HasForeignKey<AssetMarketCalendar>(binding => binding.AssetId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_asset_market_calendars_asset");
        builder.HasOne(binding => binding.Calendar).WithMany()
            .HasForeignKey(binding => binding.CalendarCode)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_asset_market_calendars_calendar");
    }
}

public sealed class MarketCalendarActiveReleaseConfiguration
    : IEntityTypeConfiguration<MarketCalendarActiveRelease>
{
    public void Configure(EntityTypeBuilder<MarketCalendarActiveRelease> builder)
    {
        builder.ToTable("market_calendar_active_releases");
        builder.HasKey(active => active.CalendarCode);
        builder.Property(active => active.CalendarCode).HasMaxLength(60);
        builder.Property(active => active.ActivatedAt).HasDefaultValueSql("NOW()");
        builder.HasIndex(active => active.ReleaseId).IsUnique();
        builder.HasOne(active => active.Release).WithOne()
            .HasForeignKey<MarketCalendarActiveRelease>(
                active => new { active.CalendarCode, active.ReleaseId })
            .HasPrincipalKey<MarketCalendarRelease>(
                release => new { release.CalendarCode, release.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_market_calendar_active_release");
    }
}
