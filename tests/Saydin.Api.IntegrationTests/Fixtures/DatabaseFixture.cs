using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;

namespace Saydin.Api.IntegrationTests.Fixtures;

/// <summary>
/// F2.6-21: docker-compose ağındaki gerçek PostgreSQL'e bağlanır. Bağlantı dizesi
/// <c>ConnectionStrings__Postgres</c> env'inden okunur (compose `tests` profili sağlar).
/// Erişilemezse <see cref="Available"/>=false olur ve testler SkippableFact ile atlanır.
/// </summary>
public sealed class DatabaseFixture : IDisposable
{
    private readonly NpgsqlDataSource? _dataSource;
    private readonly DbContextOptions<SaydinDbContext>? _options;

    public bool Available { get; }
    public string SkipReason { get; } = string.Empty;

    /// <summary>
    /// Migration 012 (F2.7-5) uygulanmış mı — inflation_rates PK composite (period_date, source) mı?
    /// Eski şemalı (henüz migrate edilmemiş) bir DB'de inflation testi spurious fail etmesin diye
    /// test bu bayrakla skip eder.
    /// </summary>
    public bool CompositeInflationPk { get; }

    public DatabaseFixture()
    {
        var connStr = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            SkipReason = "ConnectionStrings__Postgres env yok (entegrasyon DB'si erişilemez).";
            return;
        }

        try
        {
            _dataSource = new NpgsqlDataSourceBuilder(connStr)
                .MapEnum<AssetCategory>("asset_category")
                .Build();

            // Hızlı erişilebilirlik kontrolü + migration 012 şema probu.
            using (var conn = _dataSource.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'pk_inflation_rates'";
                var pkDef = cmd.ExecuteScalar() as string ?? string.Empty;
                CompositeInflationPk = pkDef.Contains("source", StringComparison.OrdinalIgnoreCase);
            }

            _options = new DbContextOptionsBuilder<SaydinDbContext>()
                .UseNpgsql(_dataSource, npgsql => npgsql.MapEnum<AssetCategory>("asset_category"))
                .UseSnakeCaseNamingConvention()
                .Options;

            Available = true;
        }
        catch (Exception ex)
        {
            SkipReason = $"PostgreSQL erişilemez: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public SaydinDbContext CreateContext() =>
        new(_options ?? throw new InvalidOperationException(SkipReason));

    public void Dispose() => _dataSource?.Dispose();
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}
