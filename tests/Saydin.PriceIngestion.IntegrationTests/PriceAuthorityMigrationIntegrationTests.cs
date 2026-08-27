using FluentAssertions;
using Npgsql;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;
using Saydin.Shared.Constants;
using Saydin.Shared.Entities;
using System.Globalization;
using System.Text.Json;

namespace Saydin.PriceIngestion.IntegrationTests;

[Collection(IngestionDatabaseCollection.Name)]
public sealed class PriceAuthorityMigrationIntegrationTests(IngestionDatabaseFixture database)
{
    [Fact]
    public async Task ManagedRepository_CompletesPriceAndInflation_PreservesReplayAndRollsBackPayloadConflict()
    {
        var assetId = await database.CreateAssetAsync(ProviderSources.CoinGecko);
        var sourceId = $"it-{assetId:N}";
        var priceDate = new DateOnly(2098, 1, 1);
        var conflictDate = priceDate.AddDays(1);
        var inflationDate = new DateOnly(2098, 2, 1);
        var firstPayload = Enumerable.Repeat((byte)0xA1, 32).ToArray();
        var secondPayload = Enumerable.Repeat((byte)0xB2, 32).ToArray();
        try
        {
            var repository = database.Repository();
            var historical = new IngestionWindowScope(ProviderSources.CoinGecko, assetId,
                IngestionJobTypes.HistoricalBackfill, 1);
            await repository.EnsureWindowsAsync(historical, [new(priceDate, priceDate)], default);
            var firstClaim = (await repository.ClaimNextAsync(
                historical, "authority-historical", TimeSpan.FromMinutes(2), default)).Claim!;
            await repository.CompletePriceAsync(firstClaim,
                AdapterOutcome<PricePoint>.Data(
                    [Price(assetId, sourceId, priceDate, firstPayload, 100)], 1),
                Counts(1), default);
            var firstTimestamp = await database.ScalarAsync<DateTime>("""
                SELECT ingested_at FROM price_points WHERE asset_id=@asset AND price_date=@date
                """, new NpgsqlParameter("asset", assetId), new NpgsqlParameter("date", priceDate));

            var daily = new IngestionWindowScope(ProviderSources.CoinGecko, assetId,
                IngestionJobTypes.DailyUpdate, 1);
            await repository.EnsureWindowsAsync(daily, [new(priceDate, priceDate)], default);
            var replayClaim = (await repository.ClaimNextAsync(
                daily, "authority-daily", TimeSpan.FromMinutes(2), default)).Claim!;
            await repository.CompletePriceAsync(replayClaim,
                AdapterOutcome<PricePoint>.Data(
                    [Price(assetId, sourceId, priceDate, secondPayload, 120)], 1),
                Counts(1), default);

            (await database.ScalarAsync<long>("""
                SELECT count(*) FROM price_observation_attributions
                 WHERE asset_id=@asset AND price_date=@date
                """, new NpgsqlParameter("asset", assetId), new NpgsqlParameter("date", priceDate)))
                .Should().Be(2);
            (await database.ScalarAsync<DateTime>("""
                SELECT ingested_at FROM price_points WHERE asset_id=@asset AND price_date=@date
                """, new NpgsqlParameter("asset", assetId), new NpgsqlParameter("date", priceDate)))
                .Should().Be(firstTimestamp, "idempotent normalized replay must retain first ingestion time");

            var inflationScope = new IngestionWindowScope(ProviderSources.Evds, null,
                IngestionJobTypes.InflationBackfill, 1);
            await repository.EnsureWindowsAsync(inflationScope,
                [new(inflationDate, inflationDate)], default);
            var inflationResult = await repository.ClaimNextAsync(
                inflationScope, "authority-inflation", TimeSpan.FromMinutes(2), default);
            inflationResult.Status.Should().Be(WindowClaimStatus.Claimed);
            var inflationClaim = inflationResult.Claim!;
            await repository.CompleteInflationAsync(inflationClaim,
                AdapterOutcome<InflationRate>.Data(
                    [Inflation(inflationDate, Enumerable.Repeat((byte)0xC3, 32).ToArray(), 90)], 1),
                Counts(1), default);
            (await database.ScalarAsync<long>("""
                SELECT count(*) FROM inflation_observation_attributions
                 WHERE period_date=@date AND source='tuik'
                """, new NpgsqlParameter("date", inflationDate))).Should().Be(1);

            await repository.EnsureWindowsAsync(historical, [new(conflictDate, conflictDate)], default);
            var conflictClaim = (await repository.ClaimNextAsync(
                historical, "authority-conflict", TimeSpan.FromMinutes(2), default)).Claim!;
            var conflict = () => repository.CompletePriceAsync(conflictClaim,
                AdapterOutcome<PricePoint>.Data(
                    [Price(assetId, sourceId, conflictDate, firstPayload, 101)], 1),
                Counts(1), default);
            await conflict.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*payload hash/length ledger conflict*");
            (await database.ScalarAsync<long>("""
                SELECT count(*) FROM price_points WHERE asset_id=@asset AND price_date=@date
                """, new NpgsqlParameter("asset", assetId), new NpgsqlParameter("date", conflictDate)))
                .Should().Be(0, "root data must roll back with attribution failure");
            (await database.ScalarAsync<string>(
                "SELECT state FROM ingestion_windows WHERE id=@id",
                new NpgsqlParameter("id", conflictClaim.WindowId))).Should().Be(IngestionWindowStates.Running);
            (await database.ScalarAsync<string>(
                "SELECT status FROM ingestion_jobs WHERE id=@id",
                new NpgsqlParameter("id", conflictClaim.JobId))).Should().Be(IngestionJobStatuses.Running);
        }
        finally
        {
            await database.CleanupAssetAsync(assetId);
            await database.ExecuteAsync("""
                SET session_replication_role='replica';
                DELETE FROM inflation_observation_attributions
                 WHERE period_date=@date AND source='tuik';
                DELETE FROM inflation_rates WHERE period_date=@date AND source='tuik';
                SET session_replication_role='origin';
                """, new NpgsqlParameter("date", inflationDate));
            await database.CleanupGlobalAsync(ProviderSources.Evds);
        }
    }

    [Fact]
    public async Task Migration020_ManagedSchema_PreservesMultiWindowPayloadProvenanceAndRejectsDrift()
    {
        await using var connection = new NpgsqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        (await ScalarAsync<bool>(connection, transaction, """
            SELECT NOT EXISTS (
                       SELECT 1 FROM public.schema_migrations
                        WHERE state NOT IN ('succeeded','skipped_optional'))
               AND EXISTS (
                       SELECT 1 FROM public.schema_migrations
                        WHERE version='020_price_authority_expand'
                          AND state='succeeded'
                          AND checksum='8cb3f07bffef6013f42d196a20f0c08ed3e02547028d5694d6fba5f9749c52a8')
            """)).Should().BeTrue();

        foreach (var canary in new[]
                 {
                     "Authorization value", "Bearer value", "api_key=value",
                     "app_id=value", "credential=value",
                 })
        {
            (await ScalarAsync<bool>(connection, transaction, """
                SELECT public.saydin_source_raw_allowed(
                    jsonb_build_object('symbol',@canary))
                """, new NpgsqlParameter("canary", canary))).Should().BeFalse();
        }
        foreach (var helper in new[]
                 {
                     "public.saydin_source_raw_allowed(jsonb)",
                     "public.saydin_canonical_observation(jsonb)",
                 })
        {
            (await ScalarAsync<bool>(connection, transaction, """
                SELECT NOT EXISTS (
                           SELECT 1 FROM pg_proc p, LATERAL aclexplode(
                             COALESCE(p.proacl,acldefault('f',p.proowner))) acl
                            WHERE p.oid=@helper::regprocedure
                              AND acl.grantee=0 AND acl.privilege_type='EXECUTE')
                   AND has_function_privilege(
                           (SELECT ingestion_capability_role FROM saydin_role_contract WHERE singleton=1),
                           @helper,'EXECUTE')
                   AND NOT has_function_privilege(
                           (SELECT audit_capability_role FROM saydin_role_contract WHERE singleton=1),
                           @helper,'EXECUTE')
                """, new NpgsqlParameter("helper", helper))).Should().BeTrue();
        }

        const string asset = "a0200000-0000-7000-8000-000000000001";
        const string historical = "b0200000-0000-7000-8000-000000000001";
        const string daily = "b0200000-0000-7000-8000-000000000002";
        const string outOfScope = "b0200000-0000-7000-8000-000000000003";
        const string historicalToken = "c0200000-0000-7000-8000-000000000001";
        const string dailyToken = "c0200000-0000-7000-8000-000000000002";
        const string outOfScopeToken = "c0200000-0000-7000-8000-000000000003";
        await ExecuteAsync(connection, transaction, """
            INSERT INTO assets(id,symbol,display_name,category,is_active,source,source_id)
            VALUES(@asset,'PRV020IT','PRV020IT','crypto',true,'coingecko','prv020-it');
            INSERT INTO ingestion_windows(
                id,source,asset_id,job_type,range_start,range_end,contract_version,state,
                lease_owner,lease_token,lease_until,attempt_count,next_attempt_at)
            VALUES
              (@historical,'coingecko',@asset,'historical_backfill','2093-01-01','2093-01-03',1,
               'running','test',@historicalToken,clock_timestamp()+interval '1 hour',1,clock_timestamp()),
              (@daily,'coingecko',@asset,'daily_update','2093-01-01','2093-01-03',1,
               'running','test',@dailyToken,clock_timestamp()+interval '1 hour',1,clock_timestamp()),
              (@outOfScope,'coingecko',@asset,'daily_update','2094-01-01','2094-01-03',1,
               'running','test',@outOfScopeToken,clock_timestamp()+interval '1 hour',1,clock_timestamp());
            """,
            new NpgsqlParameter("asset", Guid.Parse(asset)),
            new NpgsqlParameter("historical", Guid.Parse(historical)),
            new NpgsqlParameter("daily", Guid.Parse(daily)),
            new NpgsqlParameter("outOfScope", Guid.Parse(outOfScope)),
            new NpgsqlParameter("historicalToken", Guid.Parse(historicalToken)),
            new NpgsqlParameter("dailyToken", Guid.Parse(dailyToken)),
            new NpgsqlParameter("outOfScopeToken", Guid.Parse(outOfScopeToken)));

        await PresentAsync(connection, transaction, historical, historicalToken);
        await ExecuteAsync(connection, transaction, InsertNormalizedSql,
            new NpgsqlParameter("asset", Guid.Parse(asset)));
        var firstIngestedAt = await ScalarAsync<DateTime>(connection, transaction, """
            SELECT ingested_at FROM price_points
             WHERE asset_id=@asset AND price_date='2093-01-01'
            """, new NpgsqlParameter("asset", Guid.Parse(asset)));
        await ExecuteAsync(connection, transaction,
            InsertNormalizedSql.Replace("\"close\": 42,", "\"close\": 42.00,"),
            new NpgsqlParameter("asset", Guid.Parse(asset)));
        (await ScalarAsync<DateTime>(connection, transaction, """
            SELECT ingested_at FROM price_points
             WHERE asset_id=@asset AND price_date='2093-01-01'
            """, new NpgsqlParameter("asset", Guid.Parse(asset)))).Should().Be(firstIngestedAt);
        await ExecuteAsync(connection, transaction, AttributionSql,
            new NpgsqlParameter("asset", Guid.Parse(asset)),
            new NpgsqlParameter("window", Guid.Parse(historical)),
            new NpgsqlParameter("payload", Enumerable.Repeat((byte)0xAA, 32).ToArray()),
            new NpgsqlParameter("length", 100));

        await PresentAsync(connection, transaction, daily, dailyToken);
        await ExecuteAsync(connection, transaction, InsertNormalizedSql,
            new NpgsqlParameter("asset", Guid.Parse(asset)));
        await ExecuteAsync(connection, transaction, AttributionSql,
            new NpgsqlParameter("asset", Guid.Parse(asset)),
            new NpgsqlParameter("window", Guid.Parse(daily)),
            new NpgsqlParameter("payload", Enumerable.Repeat((byte)0xBB, 32).ToArray()),
            new NpgsqlParameter("length", 120));

        (await ScalarAsync<long>(connection, transaction, """
            SELECT count(*) FROM price_observation_attributions WHERE asset_id=@asset
            """, new NpgsqlParameter("asset", Guid.Parse(asset)))).Should().Be(2);
        (await ScalarAsync<long>(connection, transaction, """
            SELECT count(*) FROM provider_fetch_payloads
             WHERE provider_source='coingecko'
               AND payload_sha256 IN (decode(repeat('aa',32),'hex'),decode(repeat('bb',32),'hex'))
            """)).Should().Be(2);

        await AssertLeaseGatesAsync(connection, transaction, asset, historical, historicalToken,
            daily, dailyToken);
        await AssertTimestampColumnsDeniedAsync(connection, transaction, asset, daily, dailyToken);

        await AssertSqlStateAsync(connection, transaction, "hash_drift", "23514", """
            UPDATE price_points SET observation_sha256=decode(repeat('00',32),'hex')
             WHERE asset_id=@asset AND price_date='2093-01-01'
            """, new NpgsqlParameter("asset", Guid.Parse(asset)));
        await AssertSqlStateAsync(connection, transaction, "nan", "23514", """
            UPDATE price_points SET close='NaN'
             WHERE asset_id=@asset AND price_date='2093-01-01'
            """, new NpgsqlParameter("asset", Guid.Parse(asset)));
        await AssertSqlStateAsync(connection, transaction, "zero_payload", "23514", """
            INSERT INTO provider_fetch_payloads(provider_source,payload_sha256,payload_byte_length)
            VALUES('coingecko',decode(repeat('00',32),'hex'),1)
            """);
        await AssertSqlStateAsync(connection, transaction, "forged_window", "42501", """
            INSERT INTO price_observation_attributions(
              asset_id,price_date,ingestion_window_id,provider_source,payload_sha256,
              source_observation_id,observation_sha256,authority_contract_version)
            SELECT asset_id,price_date,@historical,provider_source,@payload,
                   source_observation_id,observation_sha256,authority_contract_version
              FROM price_points WHERE asset_id=@asset AND price_date='2093-01-01'
            """, new NpgsqlParameter("asset", Guid.Parse(asset)),
            new NpgsqlParameter("historical", Guid.Parse(historical)),
            new NpgsqlParameter("payload", Enumerable.Repeat((byte)0xBB, 32).ToArray()));
        await PresentAsync(connection, transaction, outOfScope, outOfScopeToken);
        await AssertSqlStateAsync(connection, transaction, "out_of_scope_window", "23503", """
            INSERT INTO price_observation_attributions(
              asset_id,price_date,ingestion_window_id,provider_source,payload_sha256,
              source_observation_id,observation_sha256,authority_contract_version)
            SELECT asset_id,price_date,@window,provider_source,@payload,
                   source_observation_id,observation_sha256,authority_contract_version
              FROM price_points WHERE asset_id=@asset AND price_date='2093-01-01'
            """, new NpgsqlParameter("asset", Guid.Parse(asset)),
            new NpgsqlParameter("window", Guid.Parse(outOfScope)),
            new NpgsqlParameter("payload", Enumerable.Repeat((byte)0xAA, 32).ToArray()));

        (await ScalarAsync<long>(connection, transaction, """
            SELECT count(*) FROM timescaledb_information.chunks
             WHERE hypertable_schema='public' AND hypertable_name='price_points'
            """)).Should().BeGreaterThan(0);
        await transaction.RollbackAsync();
    }

    private const string InsertNormalizedSql = """
        WITH evidence(raw) AS (VALUES(
          '{"as_of_at": "2093-01-01T00:00:00.0000000Z", "close": 42, "date": "2093-01-01", "observation_id": "coingecko:prv020-it:try:3881606400000", "provider_source": "coingecko", "quote_currency": "TRY", "source_timestamp_ms": 3881606400000, "symbol": "prv020-it"}'::jsonb))
        INSERT INTO price_points(
          asset_id,price_date,close,provider_source,source_observation_id,as_of_at,price_kind,
          is_final,observation_sha256,authority_contract_version,source_raw)
        SELECT @asset,'2093-01-01',42,'coingecko','coingecko:prv020-it:try:3881606400000',
               '2093-01-01Z','daily_utc_reference',true,
               sha256(convert_to(saydin_canonical_observation(raw)::text,'UTF8')),1,raw FROM evidence
        ON CONFLICT(asset_id,price_date) DO UPDATE SET
          close=excluded.close,provider_source=excluded.provider_source,
          source_observation_id=excluded.source_observation_id,as_of_at=excluded.as_of_at,
          price_kind=excluded.price_kind,is_final=excluded.is_final,
          observation_sha256=excluded.observation_sha256,
          authority_contract_version=excluded.authority_contract_version,source_raw=excluded.source_raw
        """;

    private static PricePoint Price(
        Guid assetId, string sourceId, DateOnly date, byte[] payloadSha256, int payloadLength)
    {
        var instant = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var timestamp = instant.ToUnixTimeMilliseconds();
        var observationId = $"coingecko:{sourceId}:try:{timestamp.ToString(CultureInfo.InvariantCulture)}";
        return new PricePoint
        {
            AssetId = assetId,
            PriceDate = date,
            Close = 42,
            ProviderSource = ProviderSources.CoinGecko,
            SourceObservationId = observationId,
            AsOfAt = instant,
            PriceKind = ObservationPriceKinds.DailyUtcReference,
            IsFinal = true,
            PayloadSha256 = payloadSha256,
            PayloadByteLength = payloadLength,
            SourceRaw = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["as_of_at"] = instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["close"] = 42m,
                ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["observation_id"] = observationId,
                ["provider_source"] = ProviderSources.CoinGecko,
                ["quote_currency"] = "TRY",
                ["source_timestamp_ms"] = timestamp,
                ["symbol"] = sourceId,
            }),
        };
    }

    private static InflationRate Inflation(DateOnly date, byte[] payloadSha256, int payloadLength)
    {
        var instant = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var observationId = $"evds:TP_FG_J0:{date.ToString("yyyy-MM", CultureInfo.InvariantCulture)}";
        return new InflationRate
        {
            PeriodDate = date,
            IndexValue = 158.37m,
            Source = InflationSources.Tuik,
            ProviderSource = ProviderSources.Evds,
            SourceObservationId = observationId,
            AsOfAt = instant,
            PriceKind = ObservationPriceKinds.CpiIndex,
            IsFinal = true,
            PayloadSha256 = payloadSha256,
            PayloadByteLength = payloadLength,
            SourceRaw = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["as_of_at"] = instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["index_value"] = 158.37m,
                ["observation_id"] = observationId,
                ["provider_source"] = ProviderSources.Evds,
                ["series"] = "TP.FG.J0",
            }),
        };
    }

    private static IngestionWindowCounts Counts(int count) =>
        new(count, count, count, count, 0, 0);

    private static async Task AssertLeaseGatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string asset,
        string historical,
        string historicalToken,
        string daily,
        string dailyToken)
    {
        var payload = Enumerable.Repeat((byte)0xCC, 32).ToArray();
        await ExecuteAsync(connection, transaction, """
            SELECT set_config('saydin.ingestion_window_id',@window,true),
                   set_config('saydin.ingestion_lease_token','',true)
            """, new NpgsqlParameter("window", historical));
        await AssertSqlStateAsync(connection, transaction, "payload_missing_token", "42501", """
            INSERT INTO provider_fetch_payloads(provider_source,payload_sha256,payload_byte_length)
            VALUES('coingecko',@payload,12)
            """, new NpgsqlParameter("payload", payload));
        await AssertSqlStateAsync(connection, transaction, "attribution_missing_token", "42501", """
            INSERT INTO price_observation_attributions(
              asset_id,price_date,ingestion_window_id,provider_source,payload_sha256,
              source_observation_id,observation_sha256,authority_contract_version)
            SELECT asset_id,price_date,@window,provider_source,@payload,
                   source_observation_id,observation_sha256,authority_contract_version
              FROM price_points WHERE asset_id=@asset AND price_date='2093-01-01'
            """, new NpgsqlParameter("asset", Guid.Parse(asset)),
            new NpgsqlParameter("window", Guid.Parse(historical)),
            new NpgsqlParameter("payload", Enumerable.Repeat((byte)0xBB, 32).ToArray()));

        await PresentAsync(connection, transaction, historical,
            "d0200000-0000-7000-8000-000000000001");
        await AssertSqlStateAsync(connection, transaction, "payload_stale_token", "42501", """
            INSERT INTO provider_fetch_payloads(provider_source,payload_sha256,payload_byte_length)
            VALUES('coingecko',@payload,12)
            """, new NpgsqlParameter("payload", payload));
        await AssertSqlStateAsync(connection, transaction, "attribution_stale_token", "42501",
            AttributionForExistingObservationSql,
            new NpgsqlParameter("asset", Guid.Parse(asset)),
            new NpgsqlParameter("window", Guid.Parse(historical)),
            new NpgsqlParameter("payload", Enumerable.Repeat((byte)0xBB, 32).ToArray()));

        await PresentAsync(connection, transaction, historical, historicalToken);
        await ExecuteAsync(connection, transaction,
            "UPDATE ingestion_windows SET lease_until=clock_timestamp()-interval '1 second' WHERE id=@window",
            new NpgsqlParameter("window", Guid.Parse(historical)));
        await AssertSqlStateAsync(connection, transaction, "payload_expired", "42501", """
            INSERT INTO provider_fetch_payloads(provider_source,payload_sha256,payload_byte_length)
            VALUES('coingecko',@payload,12)
            """, new NpgsqlParameter("payload", payload));
        await AssertSqlStateAsync(connection, transaction, "attribution_expired", "42501",
            AttributionForExistingObservationSql,
            new NpgsqlParameter("asset", Guid.Parse(asset)),
            new NpgsqlParameter("window", Guid.Parse(historical)),
            new NpgsqlParameter("payload", Enumerable.Repeat((byte)0xBB, 32).ToArray()));
        await ExecuteAsync(connection, transaction, """
            UPDATE ingestion_windows SET lease_until=clock_timestamp()+interval '1 hour' WHERE id=@window
            """, new NpgsqlParameter("window", Guid.Parse(historical)));

        await PresentAsync(connection, transaction, daily, dailyToken);
        await ExecuteAsync(connection, transaction,
            "UPDATE ingestion_windows SET state='cancelled',outcome_code='lease_test_terminal',lease_owner=NULL,lease_token=NULL,lease_until=NULL WHERE id=@window",
            new NpgsqlParameter("window", Guid.Parse(daily)));
        await AssertSqlStateAsync(connection, transaction, "payload_terminal", "42501", """
            INSERT INTO provider_fetch_payloads(provider_source,payload_sha256,payload_byte_length)
            VALUES('coingecko',@payload,12)
            """, new NpgsqlParameter("payload", payload));
        await AssertSqlStateAsync(connection, transaction, "attribution_terminal", "42501",
            AttributionForExistingObservationSql,
            new NpgsqlParameter("asset", Guid.Parse(asset)),
            new NpgsqlParameter("window", Guid.Parse(daily)),
            new NpgsqlParameter("payload", Enumerable.Repeat((byte)0xAA, 32).ToArray()));
        await ExecuteAsync(connection, transaction, """
            UPDATE ingestion_windows SET state='running',lease_owner='test',lease_token=@token,
                   lease_until=clock_timestamp()+interval '1 hour',outcome_code=NULL WHERE id=@window
            """, new NpgsqlParameter("window", Guid.Parse(daily)),
            new NpgsqlParameter("token", Guid.Parse(dailyToken)));

        (await ScalarAsync<long>(connection, transaction, """
            SELECT count(*) FROM provider_fetch_payloads
             WHERE provider_source='coingecko' AND payload_sha256=@payload
            """, new NpgsqlParameter("payload", payload))).Should().Be(0);
        (await ScalarAsync<long>(connection, transaction, """
            SELECT count(*) FROM price_observation_attributions WHERE asset_id=@asset
            """, new NpgsqlParameter("asset", Guid.Parse(asset)))).Should().Be(2);
    }

    private const string AttributionForExistingObservationSql = """
        INSERT INTO price_observation_attributions(
          asset_id,price_date,ingestion_window_id,provider_source,payload_sha256,
          source_observation_id,observation_sha256,authority_contract_version)
        SELECT asset_id,price_date,@window,provider_source,@payload,
               source_observation_id,observation_sha256,authority_contract_version
          FROM price_points WHERE asset_id=@asset AND price_date='2093-01-01'
        """;

    private static async Task AssertTimestampColumnsDeniedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string asset,
        string daily,
        string dailyToken)
    {
        var ingestionRole = await ScalarAsync<string>(connection, transaction, """
            SELECT ingestion_capability_role FROM saydin_role_contract WHERE singleton=1
            """);
        var setIngestionRole = await ScalarAsync<string>(connection, transaction,
            "SELECT pg_catalog.format('SET LOCAL ROLE %I',@role)",
            new NpgsqlParameter("role", ingestionRole));
        await transaction.SaveAsync("timestamp_acl");
        await ExecuteAsync(connection, transaction, "RESET ROLE; " + setIngestionRole);
        await PresentAsync(connection, transaction, daily, dailyToken);
        foreach (var (savepoint, sql) in new[]
                 {
                     ("price_timestamp", "INSERT INTO price_points(asset_id,price_date,ingested_at) VALUES(@asset,'2093-01-02',clock_timestamp())"),
                     ("inflation_timestamp", "INSERT INTO inflation_rates(period_date,index_value,source,created_at) VALUES('2093-01-01',1,'tuik',clock_timestamp())"),
                     ("payload_timestamp", "INSERT INTO provider_fetch_payloads(provider_source,payload_sha256,payload_byte_length,first_observed_at) VALUES('coingecko',decode(repeat('dd',32),'hex'),1,clock_timestamp())"),
                     ("attribution_timestamp", "INSERT INTO price_observation_attributions(asset_id,price_date,ingestion_window_id,provider_source,payload_sha256,source_observation_id,observation_sha256,authority_contract_version,attributed_at) SELECT asset_id,price_date,@window,provider_source,decode(repeat('bb',32),'hex'),source_observation_id,observation_sha256,authority_contract_version,clock_timestamp() FROM price_points WHERE asset_id=@asset AND price_date='2093-01-01'"),
                 })
        {
            await AssertSqlStateAsync(connection, transaction, savepoint, "42501", sql,
                new NpgsqlParameter("asset", Guid.Parse(asset)),
                new NpgsqlParameter("window", Guid.Parse(daily)));
        }
        await transaction.RollbackAsync("timestamp_acl");
    }

    private const string AttributionSql = """
        INSERT INTO provider_fetch_payloads(provider_source,payload_sha256,payload_byte_length)
        VALUES('coingecko',@payload,@length) ON CONFLICT DO NOTHING;
        INSERT INTO price_observation_attributions(
          asset_id,price_date,ingestion_window_id,provider_source,payload_sha256,
          source_observation_id,observation_sha256,authority_contract_version)
        SELECT asset_id,price_date,@window,provider_source,@payload,
               source_observation_id,observation_sha256,authority_contract_version
          FROM price_points WHERE asset_id=@asset AND price_date='2093-01-01'
        """;

    private static async Task PresentAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string window, string token) =>
        await ExecuteAsync(connection, transaction, """
            SELECT set_config('saydin.ingestion_window_id',@window,true),
                   set_config('saydin.ingestion_lease_token',@token,true)
            """, new NpgsqlParameter("window", window),
            new NpgsqlParameter("token", token));

    private static async Task AssertSqlStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string savepoint, string sqlState, string sql, params NpgsqlParameter[] parameters)
    {
        await transaction.SaveAsync(savepoint);
        var act = () => ExecuteAsync(connection, transaction, sql, parameters);
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(sqlState);
        await transaction.RollbackAsync(savepoint);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync())!;
    }

}
