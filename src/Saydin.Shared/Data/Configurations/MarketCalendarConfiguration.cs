using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data.Configurations;

public sealed class MarketCalendarConfiguration : IEntityTypeConfiguration<MarketCalendar>
{
    public void Configure(EntityTypeBuilder<MarketCalendar> builder)
    {
        builder.ToTable("market_calendars");
        builder.HasKey(calendar => calendar.Code);
        builder.Property(calendar => calendar.Code).HasMaxLength(60);
        builder.Property(calendar => calendar.Authority).HasMaxLength(120).IsRequired();
        builder.Property(calendar => calendar.TimeZone).HasMaxLength(60).IsRequired();
    }
}

public sealed class MarketCalendarReleaseConfiguration : IEntityTypeConfiguration<MarketCalendarRelease>
{
    public void Configure(EntityTypeBuilder<MarketCalendarRelease> builder)
    {
        builder.ToTable("market_calendar_releases");
        builder.HasKey(release => release.Id);
        builder.Property(release => release.CalendarCode).HasMaxLength(60).IsRequired();
        builder.Property(release => release.SnapshotSetId).HasMaxLength(100).IsRequired();
        builder.Property(release => release.NormalizedSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(release => release.SourceBundleSha256).HasMaxLength(64).IsFixedLength().IsRequired();
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
        builder.ToTable("market_calendar_release_sources");
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
        builder.ToTable("market_calendar_days");
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
        builder.ToTable("asset_market_calendars");
        builder.HasKey(binding => binding.AssetId);
        builder.Property(binding => binding.Source).HasMaxLength(30).IsRequired();
        builder.Property(binding => binding.CalendarCode).HasMaxLength(60).IsRequired();
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
