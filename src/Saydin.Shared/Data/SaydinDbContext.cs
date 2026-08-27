using Microsoft.EntityFrameworkCore;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data;

public sealed class SaydinDbContext(DbContextOptions<SaydinDbContext> options) : DbContext(options)
{
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<PricePoint> PricePoints => Set<PricePoint>();
    public DbSet<User> Users => Set<User>();
    // These two types remain in the compiled model for relationships and schema
    // verification, but the managed API role has no table privileges on them.
    // Omitting public DbSet properties prevents accidental API query usage.
    public DbSet<SavedScenario> SavedScenarios => Set<SavedScenario>();
    public DbSet<InflationRate> InflationRates => Set<InflationRate>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();
    public DbSet<IngestionJob> IngestionJobs => Set<IngestionJob>();
    public DbSet<IngestionWindow> IngestionWindows => Set<IngestionWindow>();
    public DbSet<MarketCalendar> MarketCalendars => Set<MarketCalendar>();
    public DbSet<MarketCalendarRelease> MarketCalendarReleases => Set<MarketCalendarRelease>();
    public DbSet<MarketCalendarReleaseSource> MarketCalendarReleaseSources => Set<MarketCalendarReleaseSource>();
    public DbSet<MarketCalendarDay> MarketCalendarDays => Set<MarketCalendarDay>();
    public DbSet<MarketCalendarActiveRelease> MarketCalendarActiveReleases => Set<MarketCalendarActiveRelease>();
    public DbSet<AssetMarketCalendar> AssetMarketCalendars => Set<AssetMarketCalendar>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // PostgreSQL asset_category enum ↔ C# AssetCategory eşlemesi.
        // Npgsql bu eşlemeyi provider seviyesinde yapar; TypeHandler veya CASE WHEN gerekmiyor.
        modelBuilder.HasPostgresEnum<AssetCategory>("public", "asset_category");

        // Tüm IEntityTypeConfiguration implementasyonlarını bu assembly'den uygula
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SaydinDbContext).Assembly);
    }
}
