using FluentAssertions;
using Npgsql;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;
using Saydin.Shared.Constants;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.IntegrationTests;

[Collection(IngestionDatabaseCollection.Name)]
public sealed class IngestionWriteFenceIntegrationTests(IngestionDatabaseFixture database)
{
    [Fact]
    public async Task LegacyRepositories_RejectTokenlessPriceAndInflationWrites()
    {
        var assetId = await database.CreateAssetAsync("it-legacy-fence");
        var inflationDate = new DateOnly(2097, 1, 1);
        try
        {
            var legacyPrice = new PriceIngestionRepository(database.ContextFactory);
            var priceWrite = () => legacyPrice.UpsertPricePointsAsync(
                [new PricePoint { AssetId = assetId, PriceDate = new(2097, 1, 1), Close = 1 }],
                default);
            await priceWrite.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("window_bound_authority_repository_required");

            var legacyInflation = new InflationIngestionRepository(database.ContextFactory);
            var inflationWrite = () => legacyInflation.UpsertInflationRatesAsync(
                [new InflationRate
                {
                    PeriodDate = inflationDate,
                    IndexValue = 1,
                    Source = InflationSources.Tuik,
                }], default);
            await inflationWrite.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("window_bound_authority_repository_required");
        }
        finally
        {
            await database.CleanupAssetAsync(assetId);
            await database.ExecuteAsync(
                "DELETE FROM inflation_rates WHERE period_date=@date AND source='tuik'",
                new NpgsqlParameter("date", inflationDate));
        }
    }

    [Fact]
    public async Task AtomicRepositoryPath_SucceedsForPriceAndInflation_AndTokenlessUpdateFails()
    {
        var assetId = await database.CreateAssetAsync(ProviderSources.CoinGecko);
        var priceDate = new DateOnly(2096, 1, 1);
        var inflationDate = new DateOnly(2096, 2, 1);
        const string inflationScopeSource = "evds";
        try
        {
            var repository = database.Repository();
            var priceScope = PriceScope(ProviderSources.CoinGecko, assetId, priceDate);
            await repository.EnsureWindowsAsync(priceScope.Scope, [priceScope.Range], default);
            var priceClaim = (await repository.ClaimNextAsync(
                priceScope.Scope, "valid-price", TimeSpan.FromMinutes(1), default)).Claim!;
            await repository.CompletePriceAsync(priceClaim,
                PriceOutcome(assetId, priceDate), Counts(1), default);

            var inflationScope = new IngestionWindowScope(
                inflationScopeSource, null, IngestionJobTypes.InflationBackfill, 1);
            await repository.EnsureWindowsAsync(inflationScope,
                [new IngestionWindowRange(inflationDate, inflationDate)], default);
            var inflationClaim = (await repository.ClaimNextAsync(
                inflationScope, "valid-inflation", TimeSpan.FromMinutes(1), default)).Claim!;
            await repository.CompleteInflationAsync(inflationClaim,
                AdapterOutcome<InflationRate>.Data([AuthorityTestData.Evds(inflationDate)], 1),
                Counts(1), default);

            (await database.ScalarAsync<string>(
                "SELECT state FROM ingestion_windows WHERE id=@id",
                new NpgsqlParameter("id", priceClaim.WindowId)))
                .Should().Be(IngestionWindowStates.Succeeded);
            (await database.ScalarAsync<string>(
                "SELECT state FROM ingestion_windows WHERE id=@id",
                new NpgsqlParameter("id", inflationClaim.WindowId)))
                .Should().Be(IngestionWindowStates.Succeeded);

            var tokenlessUpdate = () => database.ExecuteAsync("""
                UPDATE price_points SET close=999 WHERE asset_id=@id AND price_date=@date
                """, new NpgsqlParameter("id", assetId), new NpgsqlParameter("date", priceDate));
            (await tokenlessUpdate.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
            (await database.ScalarAsync<decimal>(
                "SELECT close FROM price_points WHERE asset_id=@id AND price_date=@date",
                new NpgsqlParameter("id", assetId), new NpgsqlParameter("date", priceDate)))
                .Should().Be(42);
        }
        finally
        {
            await database.CleanupAssetAsync(assetId);
            await database.CleanupGlobalAsync(inflationScopeSource);
            await database.ExecuteAsync(
                "DELETE FROM inflation_rates WHERE period_date=@date AND source='tuik'",
                new NpgsqlParameter("date", inflationDate));
        }
    }

    [Fact]
    public async Task PriceFence_RejectsForgedTokenWrongAssetWrongDateWrongSourceAndWrongJob()
    {
        var assetId = await database.CreateAssetAsync(ProviderSources.CoinGecko);
        var otherAssetId = await database.CreateAssetAsync(ProviderSources.CoinGecko);
        var sourceMismatchAssetId = await database.CreateAssetAsync(ProviderSources.CoinGecko);
        var date = new DateOnly(2095, 1, 1);
        try
        {
            var repository = database.Repository();
            var valid = PriceScope(ProviderSources.CoinGecko, assetId, date);
            await repository.EnsureWindowsAsync(valid.Scope, [valid.Range], default);
            var claim = (await repository.ClaimNextAsync(
                valid.Scope, "scope-owner", TimeSpan.FromMinutes(2), default)).Claim!;

            await AssertPriceRejectedAsync(claim.WindowId, Guid.CreateVersion7(), assetId, date);
            await AssertPriceRejectedAsync(claim.WindowId, claim.LeaseToken, otherAssetId, date);
            await AssertPriceRejectedAsync(claim.WindowId, claim.LeaseToken, assetId, date.AddDays(1));

            var wrongSource = PriceScope("it-forged-source", sourceMismatchAssetId, date);
            await repository.EnsureWindowsAsync(wrongSource.Scope, [wrongSource.Range], default);
            var wrongSourceClaim = (await repository.ClaimNextAsync(
                wrongSource.Scope, "source-owner", TimeSpan.FromMinutes(2), default)).Claim!;
            await AssertPriceRejectedAsync(wrongSourceClaim.WindowId,
                wrongSourceClaim.LeaseToken, sourceMismatchAssetId, date);

            var wrongJobScope = new IngestionWindowScope(
                ProviderSources.CoinGecko, sourceMismatchAssetId, IngestionJobTypes.InflationBackfill, 1);
            await repository.EnsureWindowsAsync(wrongJobScope,
                [new IngestionWindowRange(date, date)], default);
            var wrongJobClaim = (await repository.ClaimNextAsync(
                wrongJobScope, "job-owner", TimeSpan.FromMinutes(2), default)).Claim!;
            await AssertPriceRejectedAsync(wrongJobClaim.WindowId,
                wrongJobClaim.LeaseToken, sourceMismatchAssetId, date);
        }
        finally
        {
            await database.CleanupAssetAsync(assetId);
            await database.CleanupAssetAsync(otherAssetId);
            await database.CleanupAssetAsync(sourceMismatchAssetId);
        }
    }

    [Fact]
    public async Task ExpiredAndReclaimedLease_RejectsExpiredAndStaleTokensAtTrigger()
    {
        var assetId = await database.CreateAssetAsync(ProviderSources.CoinGecko);
        var date = new DateOnly(2094, 1, 1);
        try
        {
            var repository = database.Repository();
            var scoped = PriceScope(ProviderSources.CoinGecko, assetId, date);
            await repository.EnsureWindowsAsync(scoped.Scope, [scoped.Range], default);
            var stale = (await repository.ClaimNextAsync(
                scoped.Scope, "stale-owner", TimeSpan.FromMinutes(1), default)).Claim!;
            await database.ExecuteAsync("""
                UPDATE ingestion_windows SET lease_until=clock_timestamp()-interval '1 second'
                 WHERE id=@id
                """, new NpgsqlParameter("id", stale.WindowId));

            await AssertPriceRejectedAsync(stale.WindowId, stale.LeaseToken, assetId, date);
            var current = (await repository.ClaimNextAsync(
                scoped.Scope, "current-owner", TimeSpan.FromMinutes(1), default)).Claim!;
            await AssertPriceRejectedAsync(stale.WindowId, stale.LeaseToken, assetId, date);

            await repository.CompletePriceAsync(
                current, PriceOutcome(assetId, date), Counts(1), default);
        }
        finally { await database.CleanupAssetAsync(assetId); }
    }

    [Fact]
    public async Task InflationFence_RejectsWrongDataSourceAndOutOfRangeMonth()
    {
        const string source = "evds";
        var date = new DateOnly(2093, 1, 1);
        try
        {
            var repository = database.Repository();
            var scope = new IngestionWindowScope(
                source, null, IngestionJobTypes.InflationBackfill, 1);
            await repository.EnsureWindowsAsync(scope,
                [new IngestionWindowRange(date, date)], default);
            var claim = (await repository.ClaimNextAsync(
                scope, "inflation-scope", TimeSpan.FromMinutes(1), default)).Claim!;

            await AssertInflationRejectedAsync(
                claim, date, InflationSources.SeedApproximation);
            await AssertInflationRejectedAsync(
                claim, date.AddMonths(1), InflationSources.Tuik);
        }
        finally
        {
            await database.ExecuteAsync(
                "DELETE FROM inflation_rates WHERE period_date BETWEEN '2093-01-01' AND '2093-02-01'");
            await database.CleanupGlobalAsync(source);
        }
    }

    [Fact]
    public async Task SuppressedBatch_RollsBackDataWindowAndJob_InsteadOfFalseSuccess()
    {
        var assetId = await database.CreateAssetAsync(ProviderSources.CoinGecko);
        var date = new DateOnly(2092, 1, 1);
        try
        {
            var repository = database.Repository();
            var scoped = PriceScope(ProviderSources.CoinGecko, assetId, date);
            await repository.EnsureWindowsAsync(scoped.Scope, [scoped.Range], default);
            var claim = (await repository.ClaimNextAsync(
                scoped.Scope, "suppressed-owner", TimeSpan.FromMinutes(1), default)).Claim!;

            await database.ExecuteAsync("""
                CREATE OR REPLACE FUNCTION it_suppress_price_write()
                RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NULL; END $$;
                DROP TRIGGER IF EXISTS aaa_it_suppress_price_write ON price_points;
                CREATE TRIGGER aaa_it_suppress_price_write
                BEFORE INSERT OR UPDATE ON price_points
                FOR EACH ROW EXECUTE FUNCTION it_suppress_price_write();
                """);
            try
            {
                var complete = () => repository.CompletePriceAsync(
                    claim, PriceOutcome(assetId, date), Counts(1), default);
                await complete.Should().ThrowAsync<InvalidOperationException>()
                    .WithMessage("*expected=1, affected=0*");
            }
            finally
            {
                await database.ExecuteAsync("""
                    DROP TRIGGER IF EXISTS aaa_it_suppress_price_write ON price_points;
                    DROP FUNCTION IF EXISTS it_suppress_price_write();
                    """);
            }

            (await database.ScalarAsync<long>(
                "SELECT count(*) FROM price_points WHERE asset_id=@id",
                new NpgsqlParameter("id", assetId))).Should().Be(0);
            (await database.ScalarAsync<string>(
                "SELECT state FROM ingestion_windows WHERE id=@id",
                new NpgsqlParameter("id", claim.WindowId))).Should().Be(IngestionWindowStates.Running);
            (await database.ScalarAsync<string>(
                "SELECT status FROM ingestion_jobs WHERE id=@id",
                new NpgsqlParameter("id", claim.JobId))).Should().Be(IngestionJobStatuses.Running);
        }
        finally { await database.CleanupAssetAsync(assetId); }
    }

    [Fact]
    public async Task Schema_HasWriterFenceTriggers_InTimescaleSupportedModes()
    {
        (await database.ScalarAsync<long>("""
            SELECT count(*) FROM pg_trigger
             WHERE tgrelid IN ('public.price_points'::regclass, 'public.inflation_rates'::regclass)
               AND ((tgname='trg_price_points_ingestion_fence' AND tgenabled='O')
                 OR (tgname='trg_inflation_rates_ingestion_fence' AND tgenabled='A'))
            """)).Should().Be(2);
        (await database.ScalarAsync<long>("""
            SELECT count(*) FROM pg_proc
             WHERE proname IN ('enforce_price_point_ingestion_fence',
                               'enforce_inflation_rate_ingestion_fence')
            """)).Should().Be(2);
    }

    private async Task AssertPriceRejectedAsync(
        Guid windowId, Guid token, Guid assetId, DateOnly date)
    {
        var write = () => database.ExecuteWithFenceAsync(windowId, token, """
            WITH asset AS (SELECT source_id FROM assets WHERE id=@asset), evidence AS (
              SELECT jsonb_build_object(
                'as_of_at',to_char(@date::date,'YYYY-MM-DD')||'T00:00:00Z',
                'close',1,'date',to_char(@date::date,'YYYY-MM-DD'),
                'observation_id','coingecko:'||source_id||':try:'||
                  (extract(epoch FROM @date::date::timestamp AT TIME ZONE 'UTC')*1000)::bigint,
                'provider_source','coingecko','quote_currency','TRY',
                'source_timestamp_ms',
                  (extract(epoch FROM @date::date::timestamp AT TIME ZONE 'UTC')*1000)::bigint,
                'symbol',source_id) raw FROM asset)
            INSERT INTO price_points(
              asset_id,price_date,close,provider_source,source_observation_id,as_of_at,
              price_kind,is_final,observation_sha256,authority_contract_version,source_raw)
            SELECT @asset,@date,1,'coingecko',raw->>'observation_id',
                   @date::date::timestamp AT TIME ZONE 'UTC','daily_utc_reference',true,
                   sha256(convert_to(saydin_canonical_observation(raw)::text,'UTF8')),1,raw
              FROM evidence
            """, new NpgsqlParameter("asset", assetId), new NpgsqlParameter("date", date));
        (await write.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    private async Task AssertInflationRejectedAsync(
        IngestionWindowClaim claim, DateOnly date, string source)
    {
        var write = () => database.ExecuteWithFenceAsync(
            claim.WindowId, claim.LeaseToken, """
            WITH evidence AS (SELECT jsonb_build_object(
              'as_of_at',to_char(@date::date,'YYYY-MM-DD')||'T00:00:00Z',
              'date',to_char(@date::date,'YYYY-MM-DD'),'index_value',1,
              'observation_id','evds:TP_FG_J0:'||to_char(@date::date,'YYYY-MM'),
              'provider_source','evds','series','TP.FG.J0') raw)
            INSERT INTO inflation_rates(
              period_date,index_value,source,provider_source,source_observation_id,as_of_at,
              price_kind,is_final,observation_sha256,authority_contract_version,source_raw)
            SELECT @date,1,@source,'evds',raw->>'observation_id',
                   @date::date::timestamp AT TIME ZONE 'UTC','cpi_index',true,
                   sha256(convert_to(saydin_canonical_observation(raw)::text,'UTF8')),1,raw
              FROM evidence
            """, new NpgsqlParameter("date", date), new NpgsqlParameter("source", source));
        (await write.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    private static (IngestionWindowScope Scope, IngestionWindowRange Range) PriceScope(
        string source, Guid assetId, DateOnly date) =>
        (new IngestionWindowScope(source, assetId, IngestionJobTypes.HistoricalBackfill, 1),
         new IngestionWindowRange(date, date));

    private static AdapterOutcome<PricePoint> PriceOutcome(Guid assetId, DateOnly date) =>
        AdapterOutcome<PricePoint>.Data(
            [AuthorityTestData.CoinGecko(assetId, $"it-{assetId:N}", date)], 1);

    private static IngestionWindowCounts Counts(int count) =>
        new(count, count, count, count, 0, 0);
}
