using Microsoft.EntityFrameworkCore;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data;

public sealed class SaydinDbContext(DbContextOptions<SaydinDbContext> options) : DbContext(options)
{
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<PricePoint> PricePoints => Set<PricePoint>();
    public DbSet<User> Users => Set<User>();
    public DbSet<SavedScenario> SavedScenarios => Set<SavedScenario>();
    public DbSet<InflationRate> InflationRates => Set<InflationRate>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // PostgreSQL asset_category enum ↔ C# AssetCategory eşlemesi.
        // Npgsql bu eşlemeyi provider seviyesinde yapar; TypeHandler veya CASE WHEN gerekmiyor.
        modelBuilder.HasPostgresEnum<AssetCategory>("public", "asset_category");

        // Tüm IEntityTypeConfiguration implementasyonlarını bu assembly'den uygula
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SaydinDbContext).Assembly);
    }
}
