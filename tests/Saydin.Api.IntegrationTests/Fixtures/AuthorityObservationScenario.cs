using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Saydin.Api.IntegrationTests.Fixtures;

/// <summary>
/// Disposable migration-020 read-boundary fixture. Final rows traverse the real
/// live-window/GUC/canonical-hash triggers. Legacy and forged rows model pre-020
/// expand-phase data under the owner-only fixture identity; the original NOT VALID
/// constraint is restored atomically before the managed API SUT can observe them.
/// </summary>
internal sealed class AuthorityObservationScenario : IAsyncDisposable
{
    internal static readonly DateOnly FirstFinalPriceDate = new(2097, 8, 11);
    internal static readonly DateOnly SecondFinalPriceDate = new(2097, 8, 13);
    internal static readonly DateOnly LastFinalPriceDate = new(2097, 8, 15);
    internal static readonly DateOnly LegacyExactPriceDate = new(2097, 8, 10);
    internal static readonly DateOnly LegacyLatestPriceDate = new(2097, 8, 18);
    internal static readonly DateOnly WrongSourcePriceDate = new(2097, 8, 14);
    internal static readonly DateOnly ShortHashPriceDate = new(2097, 8, 16);
    internal static readonly DateOnly FirstFinalCpiMonth = new(2024, 2, 1);
    internal static readonly DateOnly LastFinalCpiMonth = new(2024, 4, 1);

    private readonly DatabaseFixture _database;

    private AuthorityObservationScenario(DatabaseFixture database)
    {
        _database = database;
        AssetId = Guid.CreateVersion7();
        PriceWindowId = Guid.CreateVersion7();
        InflationWindowId = Guid.CreateVersion7();
        PriceLeaseToken = Guid.CreateVersion7();
        InflationLeaseToken = Guid.CreateVersion7();
        Symbol = $"P{AssetId:N}"[..20].ToUpperInvariant();
        SourceId = $"prv-{AssetId:N}";
    }

    internal Guid AssetId { get; }
    internal Guid PriceWindowId { get; }
    internal Guid InflationWindowId { get; }
    internal Guid PriceLeaseToken { get; }
    internal Guid InflationLeaseToken { get; }
    internal string Symbol { get; }
    internal string SourceId { get; }

    internal static async Task<AuthorityObservationScenario> CreateAsync(DatabaseFixture database)
    {
        if (!database.PriceAuthority)
            throw new InvalidOperationException("frozen_migration_020_not_ready");

        var scenario = new AuthorityObservationScenario(database);
        try
        {
            await scenario.SeedFinalRowsAsync();
            await scenario.SeedLegacyAndForgedPriceRowsAsync();
            return scenario;
        }
        catch
        {
            await scenario.DisposeAsync();
            throw;
        }
    }

    private async Task SeedFinalRowsAsync()
    {
        await using var setup = _database.CreateAdminContext();
        await using var transaction = await setup.Database.BeginTransactionAsync();

        await setup.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.assets(id,symbol,display_name,category,is_active,source,source_id)
            VALUES ({AssetId},{Symbol},'PRV integration asset','crypto',TRUE,'coingecko',{SourceId});
            INSERT INTO public.ingestion_windows(
                id,source,asset_id,job_type,range_start,range_end,contract_version,
                state,lease_owner,lease_token,lease_until,attempt_count)
            VALUES ({PriceWindowId},'coingecko',{AssetId},'historical_backfill',
                    {LegacyExactPriceDate},{LegacyLatestPriceDate},1,
                    'running','api-prv-integration',{PriceLeaseToken},
                    pg_catalog.clock_timestamp()+interval '10 minutes',1);
            INSERT INTO public.ingestion_windows(
                id,source,asset_id,job_type,range_start,range_end,contract_version,
                state,lease_owner,lease_token,lease_until,attempt_count)
            VALUES ({InflationWindowId},'evds',NULL,'inflation_backfill',
                    {FirstFinalCpiMonth},{LastFinalCpiMonth},1,
                    'running','api-prv-integration',{InflationLeaseToken},
                    pg_catalog.clock_timestamp()+interval '10 minutes',1);
            SELECT pg_catalog.set_config('saydin.ingestion_window_id',{PriceWindowId.ToString("D")},TRUE),
                   pg_catalog.set_config('saydin.ingestion_lease_token',{PriceLeaseToken.ToString("D")},TRUE);
            """);

        foreach (var (date, close) in new[]
                 {
                     (FirstFinalPriceDate, 11m),
                     (SecondFinalPriceDate, 13m),
                     (LastFinalPriceDate, 15m),
                 })
        {
            var asOf = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var epochMilliseconds = asOf.ToUnixTimeMilliseconds();
            var observationId = $"coingecko:{SourceId}:try:{epochMilliseconds}";
            var sourceRaw = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["as_of_at"] = asOf.ToString("O"),
                ["close"] = close,
                ["date"] = date.ToString("yyyy-MM-dd"),
                ["observation_id"] = observationId,
                ["provider_source"] = "coingecko",
                ["quote_currency"] = "TRY",
                ["source_timestamp_ms"] = epochMilliseconds,
                ["symbol"] = SourceId,
            });
            await setup.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO public.price_points(
                    asset_id,price_date,close,provider_source,source_observation_id,
                    as_of_at,price_kind,is_final,observation_sha256,
                    authority_contract_version,source_raw)
                VALUES ({AssetId},{date},{close},'coingecko',{observationId},{asOf},
                        'daily_utc_reference',TRUE,
                        pg_catalog.sha256(pg_catalog.convert_to(
                            public.saydin_canonical_observation({sourceRaw}::jsonb)::text,'UTF8')),
                        1,{sourceRaw}::jsonb);
                """);
        }

        await setup.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_catalog.set_config('saydin.ingestion_window_id',{InflationWindowId.ToString("D")},TRUE),
                   pg_catalog.set_config('saydin.ingestion_lease_token',{InflationLeaseToken.ToString("D")},TRUE);
            """);

        foreach (var (period, indexValue) in new[]
                 {
                     (FirstFinalCpiMonth, 201m),
                     (LastFinalCpiMonth, 204m),
                 })
        {
            var asOf = new DateTimeOffset(period.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var observationId = $"evds:TP_FG_J0:{period:yyyy-MM}";
            var sourceRaw = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["as_of_at"] = asOf.ToString("O"),
                ["date"] = period.ToString("yyyy-MM-dd"),
                ["index_value"] = indexValue,
                ["observation_id"] = observationId,
                ["provider_source"] = "evds",
                ["series"] = "TP.FG.J0",
            });
            await setup.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO public.inflation_rates(
                    period_date,index_value,source,provider_source,source_observation_id,
                    as_of_at,price_kind,is_final,observation_sha256,
                    authority_contract_version,source_raw)
                VALUES ({period},{indexValue},'tuik','evds',{observationId},{asOf},
                        'cpi_index',TRUE,
                        pg_catalog.sha256(pg_catalog.convert_to(
                            public.saydin_canonical_observation({sourceRaw}::jsonb)::text,'UTF8')),
                        1,{sourceRaw}::jsonb);
                """);
        }

        await transaction.CommitAsync();
    }

    private async Task SeedLegacyAndForgedPriceRowsAsync()
    {
        await using var setup = _database.CreateAdminContext();
        var definition = await setup.Database.SqlQueryRaw<string>("""
                SELECT pg_catalog.pg_get_constraintdef(oid) AS "Value"
                  FROM pg_catalog.pg_constraint
                 WHERE conrelid='public.price_points'::regclass
                   AND conname='chk_price_points_authority_tuple'
                """)
            .SingleAsync();
        if (!definition.EndsWith("NOT VALID", StringComparison.Ordinal))
            throw new InvalidOperationException("price_authority_constraint_not_not_valid");

        await using var transaction = await setup.Database.BeginTransactionAsync();
        await setup.Database.ExecuteSqlRawAsync(
            "ALTER TABLE public.price_points DROP CONSTRAINT chk_price_points_authority_tuple");
        await setup.Database.ExecuteSqlRawAsync(
            "SET LOCAL session_replication_role='replica'");

        await setup.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.price_points(asset_id,price_date,close)
            VALUES ({AssetId},{LegacyExactPriceDate},10),
                   ({AssetId},{LegacyLatestPriceDate},18);
            """);

        var wrongAsOf = new DateTimeOffset(WrongSourcePriceDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var wrongObservationId = $"tcmb:USD:{WrongSourcePriceDate:yyyy-MM-dd}:forex_buying";
        var wrongRaw = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["as_of_at"] = wrongAsOf.ToString("O"),
            ["close"] = 14m,
            ["currency"] = "USD",
            ["date"] = WrongSourcePriceDate.ToString("yyyy-MM-dd"),
            ["observation_id"] = wrongObservationId,
            ["provider_source"] = "tcmb",
            ["unit"] = 1,
        });
        await setup.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.price_points(
                asset_id,price_date,close,provider_source,source_observation_id,
                as_of_at,price_kind,is_final,observation_sha256,
                authority_contract_version,source_raw)
            VALUES ({AssetId},{WrongSourcePriceDate},14,'tcmb',{wrongObservationId},{wrongAsOf},
                    'official_reference',TRUE,
                    pg_catalog.sha256(pg_catalog.convert_to(
                        public.saydin_canonical_observation({wrongRaw}::jsonb)::text,'UTF8')),
                    1,{wrongRaw}::jsonb);
            """);

        var shortAsOf = new DateTimeOffset(ShortHashPriceDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var shortObservationId = $"coingecko:{SourceId}:try:{shortAsOf.ToUnixTimeMilliseconds()}";
        var shortRaw = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["as_of_at"] = shortAsOf.ToString("O"),
            ["close"] = 16m,
            ["date"] = ShortHashPriceDate.ToString("yyyy-MM-dd"),
            ["observation_id"] = shortObservationId,
            ["provider_source"] = "coingecko",
            ["quote_currency"] = "TRY",
            ["source_timestamp_ms"] = shortAsOf.ToUnixTimeMilliseconds(),
            ["symbol"] = SourceId,
        });
        await setup.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.price_points(
                asset_id,price_date,close,provider_source,source_observation_id,
                as_of_at,price_kind,is_final,observation_sha256,
                authority_contract_version,source_raw)
            VALUES ({AssetId},{ShortHashPriceDate},16,'coingecko',{shortObservationId},{shortAsOf},
                    'daily_utc_reference',TRUE,
                    pg_catalog.substr(pg_catalog.sha256(pg_catalog.convert_to(
                        public.saydin_canonical_observation({shortRaw}::jsonb)::text,'UTF8')),1,31),
                    1,{shortRaw}::jsonb);
            """);

        await using (var restoreConstraint = setup.Database.GetDbConnection().CreateCommand())
        {
            restoreConstraint.Transaction = transaction.GetDbTransaction();
            restoreConstraint.CommandText =
                $"ALTER TABLE public.price_points ADD CONSTRAINT chk_price_points_authority_tuple {definition}";
            await restoreConstraint.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await using var cleanup = _database.CreateAdminContext();
        await using var transaction = await cleanup.Database.BeginTransactionAsync();
        await cleanup.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role='replica'");
        await cleanup.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM public.inflation_rates
             WHERE source='tuik' AND period_date IN ({FirstFinalCpiMonth},{LastFinalCpiMonth});
            DELETE FROM public.price_points WHERE asset_id={AssetId};
            DELETE FROM public.ingestion_windows WHERE id IN ({PriceWindowId},{InflationWindowId});
            DELETE FROM public.assets WHERE id={AssetId};
            """);
        await transaction.CommitAsync();
    }
}
