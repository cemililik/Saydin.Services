using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Saydin.DatabaseSecurity;
using Saydin.PriceIngestion.Repositories;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.IntegrationTests;

public sealed class IngestionDatabaseFixture : IAsyncLifetime
{
    private readonly NpgsqlDataSource _managedDataSource;
    public string AdminConnectionString { get; }
    public IDbContextFactory<SaydinDbContext> ContextFactory { get; }

    public IngestionDatabaseFixture()
    {
        var connectionFile = Environment.GetEnvironmentVariable("SAYDIN_INGESTION_TEST_DATABASE_FILE")
            ?? throw new InvalidOperationException(
                "SAYDIN_INGESTION_TEST_DATABASE_FILE zorunludur; bu required real-PG suite skip etmez.");
        AdminConnectionString = SecureSecretFile.ReadConnectionString(connectionFile);
        IngestionTestTargetGuard.Validate(
            AdminConnectionString,
            Environment.GetEnvironmentVariable("SAYDIN_INGESTION_TEST_REQUIRED"),
            Environment.GetEnvironmentVariable("SAYDIN_INGESTION_TEST_RUN_ID"),
            Environment.GetEnvironmentVariable("SAYDIN_INGESTION_TEST_EXPECTED_HOST"));
        var runtime = RuntimeDatabaseOptions.FromEnvironment(
            LoginPurpose.Ingestion, RuntimeDatabasePooling.Service);
        IngestionTestTargetGuard.ValidateRuntime(
            runtime.Host,
            runtime.Database,
            Environment.GetEnvironmentVariable("SAYDIN_INGESTION_TEST_REQUIRED"),
            Environment.GetEnvironmentVariable("SAYDIN_INGESTION_TEST_RUN_ID"),
            Environment.GetEnvironmentVariable("SAYDIN_INGESTION_TEST_EXPECTED_HOST"));
        _managedDataSource = RuntimeDatabase.OpenVerifiedDataSourceAsync(
                runtime, builder => builder.MapEnum<AssetCategory>("asset_category"))
            .GetAwaiter().GetResult();
        var options = new DbContextOptionsBuilder<SaydinDbContext>()
            .UseNpgsql(_managedDataSource, npgsql =>
                npgsql.MapEnum<AssetCategory>("asset_category"))
            .UseSnakeCaseNamingConvention()
            .Options;
        ContextFactory = new TestContextFactory(options);
    }

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT to_regclass('public.ingestion_windows') IS NOT NULL
               AND to_regclass('public.market_calendar_releases') IS NOT NULL
               AND to_regprocedure('public.verify_market_calendar_release_payload(uuid)') IS NOT NULL
               AND (SELECT count(*) FROM market_calendar_days) >= 8630
               AND NOT EXISTS (SELECT 1 FROM market_calendar_releases WHERE sealed_at IS NULL)
               AND to_regprocedure('public.enforce_price_point_ingestion_fence()') IS NOT NULL
               AND to_regprocedure('public.enforce_inflation_rate_ingestion_fence()') IS NOT NULL
               AND to_regclass('public.provider_fetch_payloads') IS NOT NULL
               AND to_regclass('public.price_observation_attributions') IS NOT NULL
               AND to_regclass('public.inflation_observation_attributions') IS NOT NULL
               AND to_regprocedure('public.enforce_observation_attribution()') IS NOT NULL
               AND (SELECT count(*) FROM public.schema_migrations
                     WHERE state IN ('succeeded','skipped_optional'))=27
               AND EXISTS (SELECT 1 FROM public.schema_migrations
                            WHERE version='023_installation_lifecycle_admission' AND state='succeeded')
               AND EXISTS (SELECT 1 FROM public.schema_migrations
                            WHERE version='024_installation_credential_rehash' AND state='succeeded')
               AND EXISTS (SELECT 1 FROM pg_trigger
                            WHERE tgname='trg_price_points_ingestion_fence' AND tgenabled='O')
               AND EXISTS (SELECT 1 FROM pg_trigger
                            WHERE tgname='trg_inflation_rates_ingestion_fence' AND tgenabled='A')
            """, connection);
        if (await command.ExecuteScalarAsync() is not true)
            throw new InvalidOperationException(
                "Migration 016/017/020/022/023/024 writer fence, calendar, authority, retention veya security şeması hazır değil; integration suite fail-closed.");
    }

    public async Task DisposeAsync() => await _managedDataSource.DisposeAsync();

    public Task<NpgsqlDataSource> OpenCalendarDataSourceAsync()
    {
        var ingestion = RuntimeDatabaseOptions.FromEnvironment(
            LoginPurpose.Ingestion, RuntimeDatabasePooling.Service);
        var calendarLogin = Environment.GetEnvironmentVariable("SAYDIN_CALENDAR_IMPORTER_TEST_LOGIN")
            ?? throw new InvalidOperationException("Calendar importer managed login missing.");
        var expected = ingestion.Contract.Login(LoginPurpose.CalendarImporter, 1);
        if (!string.Equals(calendarLogin, expected.Name, StringComparison.Ordinal))
            throw new InvalidOperationException("Calendar importer login contract mismatch.");
        var calendar = new RuntimeDatabaseOptions(
            LoginPurpose.CalendarImporter, ingestion.Contract, expected,
            ingestion.Host, ingestion.Port, ingestion.Database, ingestion.SslMode,
            Environment.GetEnvironmentVariable("SAYDIN_CALENDAR_IMPORTER_DATABASE_PASSWORD_FILE")
                ?? throw new InvalidOperationException("Calendar importer password file missing."),
            RuntimeDatabasePooling.Disabled);
        return RuntimeDatabase.OpenVerifiedDataSourceAsync(calendar);
    }

    public IngestionWindowRepository Repository(IIngestionPersistenceFaultInjector? fault = null) =>
        new(ContextFactory, fault ?? new NoopIngestionPersistenceFaultInjector(), TimeProvider.System,
            new NoopIngestionFreshnessTelemetry());

    public async Task<Guid> CreateAssetAsync(string source)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync("""
            INSERT INTO assets(id, symbol, display_name, category, is_active, source, source_id, created_at)
            VALUES (@id, @symbol, 'ING ledger test', 'crypto'::asset_category, TRUE, @source, @source_id, NOW())
            """,
            new NpgsqlParameter("id", id),
            new NpgsqlParameter("symbol", $"IT{id:N}"[..20]),
            new NpgsqlParameter("source", source),
            new NpgsqlParameter("source_id", $"it-{id:N}"));
        return id;
    }

    public async Task CleanupAssetAsync(Guid assetId)
    {
        await ExecuteAsync("""
            SET session_replication_role='replica';
            DELETE FROM ingestion_jobs WHERE window_id IN (SELECT id FROM ingestion_windows WHERE asset_id=@id);
            DELETE FROM price_observation_attributions WHERE asset_id=@id;
            DELETE FROM price_points WHERE asset_id=@id;
            DELETE FROM market_holidays WHERE asset_id=@id;
            DELETE FROM ingestion_windows WHERE asset_id=@id;
            DELETE FROM provider_fetch_payloads payload
             WHERE NOT EXISTS (SELECT 1 FROM price_observation_attributions price
                                WHERE price.provider_source=payload.provider_source
                                  AND price.payload_sha256=payload.payload_sha256)
               AND NOT EXISTS (SELECT 1 FROM inflation_observation_attributions inflation
                                WHERE inflation.provider_source=payload.provider_source
                                  AND inflation.payload_sha256=payload.payload_sha256);
            SET session_replication_role='origin';
            SET session_replication_role='replica';
            DELETE FROM asset_market_calendars WHERE asset_id=@id;
            SET session_replication_role='origin';
            DELETE FROM assets WHERE id=@id;
            """, new NpgsqlParameter("id", assetId));
    }

    public async Task<IReadOnlyList<Guid>> SuspendActiveAssetsAsync(string source)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var ids = new List<Guid>();
            await using (var command = new NpgsqlCommand("""
                UPDATE assets
                   SET is_active=FALSE
                 WHERE source=@source AND is_active
                 RETURNING id
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("source", source);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
            }
            await transaction.CommitAsync();
            return ids;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task RestoreActiveAssetsAsync(IReadOnlyList<Guid> assetIds)
    {
        if (assetIds.Count == 0) return;
        await ExecuteAsync("""
            UPDATE assets SET is_active=TRUE WHERE id=ANY(@ids)
            """, new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                Value = assetIds.ToArray(),
            });
    }

    public Task BindCalendarAsync(Guid assetId, string source)
    {
        var calendar = source switch
        {
            "tcmb" => "tcmb_indicative_fx",
            "twelvedata" => "bist_pay_xist",
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        return ExecuteAsync("""
            INSERT INTO asset_market_calendars(asset_id,source,calendar_code)
            VALUES (@id,@source,@calendar)
            ON CONFLICT (asset_id) DO NOTHING
            """, new NpgsqlParameter("id", assetId),
                new NpgsqlParameter("source", source),
                new NpgsqlParameter("calendar", calendar));
    }

    public async Task CleanupGlobalAsync(string source)
    {
        await ExecuteAsync("""
            SET session_replication_role='replica';
            DELETE FROM ingestion_jobs WHERE window_id IN (SELECT id FROM ingestion_windows WHERE source=@source AND asset_id IS NULL);
            DELETE FROM inflation_observation_attributions
             WHERE ingestion_window_id IN (SELECT id FROM ingestion_windows WHERE source=@source AND asset_id IS NULL);
            DELETE FROM ingestion_windows WHERE source=@source AND asset_id IS NULL;
            DELETE FROM provider_fetch_payloads payload
             WHERE NOT EXISTS (SELECT 1 FROM price_observation_attributions price
                                WHERE price.provider_source=payload.provider_source
                                  AND price.payload_sha256=payload.payload_sha256)
               AND NOT EXISTS (SELECT 1 FROM inflation_observation_attributions inflation
                                WHERE inflation.provider_source=payload.provider_source
                                  AND inflation.payload_sha256=payload.payload_sha256);
            SET session_replication_role='origin';
            """, new NpgsqlParameter("source", source));
    }

    public async Task<int> ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<int> ExecuteAsIngestionAsync(
        string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = await _managedDataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<int> ExecuteAsIngestionWithWindowOnlyAsync(
        Guid windowId,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var connection = await _managedDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var capability = new NpgsqlCommand(
                "SELECT set_config('saydin.ingestion_window_id', @window_id::text, true)",
                connection, transaction))
            {
                capability.Parameters.AddWithValue("window_id", windowId);
                await capability.ExecuteNonQueryAsync();
            }

            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddRange(parameters);
            var affected = await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return affected;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<int> ExecuteWithFenceAsync(
        Guid windowId,
        Guid leaseToken,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var fence = new NpgsqlCommand("""
                SELECT set_config('saydin.ingestion_window_id', @window, TRUE),
                       set_config('saydin.ingestion_lease_token', @token, TRUE)
                """, connection, transaction))
            {
                fence.Parameters.AddWithValue("window", windowId.ToString("D"));
                fence.Parameters.AddWithValue("token", leaseToken.ToString("D"));
                await fence.ExecuteNonQueryAsync();
            }

            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddRange(parameters);
            var affected = await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return affected;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task SeedPricePointForLedgerPlanningTestAsync(Guid assetId, DateOnly date)
    {
        // Test-only historical fixture. TimescaleDB hypertables reject ALTER TABLE
        // DISABLE TRIGGER; a transaction-local replication role skips the regular
        // hypertable trigger and automatically resets on rollback/commit. This helper
        // exists only in the guarded integration-test assembly; production has no bypass.
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using var command = new NpgsqlCommand("""
                SET LOCAL session_replication_role='replica';
                INSERT INTO price_points(asset_id,price_date,close,ingested_at)
                VALUES (@id,@date,1,NOW());
                SET LOCAL session_replication_role='origin';
                """, connection, transaction);
            command.Parameters.AddWithValue("id", assetId);
            command.Parameters.AddWithValue("date", date);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, typeof(T));
    }

    private sealed class TestContextFactory(DbContextOptions<SaydinDbContext> options)
        : IDbContextFactory<SaydinDbContext>
    {
        public SaydinDbContext CreateDbContext() => new(options);
        public Task<SaydinDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

public static class IngestionTestTargetGuard
{
    public static void Validate(
        string connectionString, string? required, string? runId, string? expectedHost)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        ValidateRuntime(
            builder.Host ?? throw new InvalidOperationException("Test DB host zorunludur."),
            builder.Database ?? throw new InvalidOperationException("Test DB adı zorunludur."),
            required, runId, expectedHost);
    }

    public static void ValidateRuntime(
        string host,
        string database,
        string? required,
        string? runId,
        string? expectedHost)
    {
        if (required != "true")
            throw new InvalidOperationException("SAYDIN_INGESTION_TEST_REQUIRED=true zorunludur.");
        if (runId is null || runId.Length != 32
            || runId.Any(character => !(character is >= '0' and <= '9'
                or >= 'a' and <= 'f')))
            throw new InvalidOperationException("SAYDIN_INGESTION_TEST_RUN_ID 32 lowercase hex olmalıdır.");
        if (string.IsNullOrWhiteSpace(expectedHost))
            throw new InvalidOperationException("SAYDIN_INGESTION_TEST_EXPECTED_HOST zorunludur.");

        var expectedDatabase = $"saydin_ingestion_test_{runId}";
        if (!string.Equals(database, expectedDatabase, StringComparison.Ordinal))
            throw new InvalidOperationException($"Test DB adı exact olmalıdır: {expectedDatabase}");
        if (!string.Equals(host, expectedHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test DB host allowlist dışında.");

        var marker = $"{host}|{database}";
        if (marker.Contains("prod", StringComparison.OrdinalIgnoreCase)
            || marker.Contains("staging", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Production/staging target reddedildi.");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IngestionDatabaseCollection : ICollectionFixture<IngestionDatabaseFixture>
{
    public const string Name = "ingestion-database";
}
