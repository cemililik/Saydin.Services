using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Saydin.Api.IntegrationTests.Fixtures;
using Saydin.Api.Repositories;
using Saydin.DatabaseSecurity;
using Saydin.Shared.Entities;

namespace Saydin.Api.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class PriceRepositoryAuthorityIntegrationTests(DatabaseFixture db)
{
    [SkippableFact]
    public async Task ManagedApi_BulkNearest_PreservesParityAndDuplicatesInOneCommand()
    {
        Skip.IfNot(db.Available, db.SkipReason);
        Skip.IfNot(db.PriceAuthority, "Frozen migration 020 authority fingerprint is required.");
        await using var scenario = await AuthorityObservationScenario.CreateAsync(db);
        var interceptor = new ReaderCommandCounter();
        await using var context = db.CreateContext(interceptor);
        var repository = new PriceRepository(context);

        var requested = Enumerable.Range(0, 600)
            .Select(index => (index % 4) switch
            {
                0 => AuthorityObservationScenario.FirstFinalPriceDate,
                1 => new DateOnly(2097, 8, 12),
                2 => new DateOnly(2097, 8, 9),
                _ => new DateOnly(2097, 8, 17),
            })
            .Append(new DateOnly(2097, 8, 12))
            .ToArray();

        var actual = await repository.GetNearestPricesAsync(
            scenario.Symbol, requested, 7, CancellationToken.None);

        actual.Should().HaveCount(601);
        actual[0]!.PriceDate.Should().Be(AuthorityObservationScenario.FirstFinalPriceDate);
        actual[1]!.PriceDate.Should().Be(AuthorityObservationScenario.FirstFinalPriceDate,
            "backward candidates have priority over a closer future price");
        actual[2]!.PriceDate.Should().Be(AuthorityObservationScenario.FirstFinalPriceDate,
            "the forward candidate is used only when no backward candidate exists");
        actual[3]!.PriceDate.Should().Be(AuthorityObservationScenario.LastFinalPriceDate,
            "legacy, wrong-provider, and short-hash rows cannot win");
        actual[^1]!.PriceDate.Should().Be(actual[1]!.PriceDate,
            "duplicate request positions must be preserved");
        interceptor.ReaderCommandCount.Should().Be(1);
    }

    [SkippableFact]
    public async Task Frozen020Fingerprint_FunctionBodyDriftFailsClosedAndRollbackRestoresReadiness()
    {
        Skip.IfNot(db.Available, db.SkipReason);
        Skip.IfNot(db.PriceAuthority, "Frozen migration 020 authority fingerprint is required.");
        var adminConnectionString = SecureSecretFile.ReadConnectionString(
            Environment.GetEnvironmentVariable("SAYDIN_TEST_ADMIN_CONNECTION_FILE")
            ?? throw new InvalidOperationException("Admin setup connection file missing."));
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE OR REPLACE FUNCTION public.saydin_canonical_observation(payload jsonb)
                RETURNS jsonb LANGUAGE sql IMMUTABLE STRICT
                SET search_path=pg_catalog,pg_temp
                AS $body$ SELECT payload $body$
                """;
            await command.ExecuteNonQueryAsync();

            DatabaseFixture.VerifyPriceAuthorityFingerprint(connection, transaction)
                .Should().BeFalse("function-body drift must make required fixture readiness fail closed");
            await transaction.RollbackAsync();
        }

        DatabaseFixture.VerifyPriceAuthorityFingerprint(connection).Should().BeTrue();
    }

    [SkippableTheory]
    [InlineData("constraint", "ALTER TABLE public.provider_fetch_payloads DROP CONSTRAINT chk_provider_fetch_payloads_length")]
    [InlineData("default", "ALTER TABLE public.provider_fetch_payloads ALTER COLUMN first_observed_at SET DEFAULT pg_catalog.now()")]
    [InlineData("primary-index", "ALTER INDEX public.pk_provider_fetch_payloads RENAME TO drift_provider_fetch_payloads")]
    [InlineData("table-acl", "GRANT UPDATE ON TABLE public.provider_fetch_payloads TO PUBLIC")]
    [InlineData("column-acl", "GRANT INSERT(first_observed_at) ON TABLE public.provider_fetch_payloads TO PUBLIC")]
    public async Task Frozen020Fingerprint_StructureOrAclDriftFailsClosedAndRollbackRestoresReadiness(
        string driftKind,
        string mutationSql)
    {
        Skip.IfNot(db.Available, db.SkipReason);
        Skip.IfNot(db.PriceAuthority, "Frozen migration 020 authority fingerprint is required.");
        var adminConnectionString = SecureSecretFile.ReadConnectionString(
            Environment.GetEnvironmentVariable("SAYDIN_TEST_ADMIN_CONNECTION_FILE")
            ?? throw new InvalidOperationException("Admin setup connection file missing."));
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();

        DatabaseFixture.VerifyPriceAuthorityFingerprint(connection).Should().BeTrue();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = mutationSql;
            await command.ExecuteNonQueryAsync();

            DatabaseFixture.VerifyPriceAuthorityFingerprint(connection, transaction)
                .Should().BeFalse($"{driftKind} drift must make required fixture readiness fail closed");
            await transaction.RollbackAsync();
        }

        DatabaseFixture.VerifyPriceAuthorityFingerprint(connection).Should().BeTrue();
    }

    [SkippableTheory]
    [InlineData("chunk-column-acl")]
    [InlineData("chunk-extra-trigger")]
    [InlineData("chunk-disabled-authority-trigger")]
    public async Task Frozen020Fingerprint_ChunkDriftFailsClosedAndRollbackRestoresReadiness(
        string driftKind)
    {
        Skip.IfNot(db.Available, db.SkipReason);
        Skip.IfNot(db.PriceAuthority, "Frozen migration 020 authority fingerprint is required.");
        await using var scenario = await AuthorityObservationScenario.CreateAsync(db);
        var adminConnectionString = SecureSecretFile.ReadConnectionString(
            Environment.GetEnvironmentVariable("SAYDIN_TEST_ADMIN_CONNECTION_FILE")
            ?? throw new InvalidOperationException("Admin setup connection file missing."));
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();

        string chunkSchema;
        string chunkName;
        string apiRole;
        await using (var resolve = connection.CreateCommand())
        {
            resolve.CommandText = """
                SELECT chunks.chunk_schema,chunks.chunk_name,contract.api_capability_role
                  FROM timescaledb_information.chunks chunks
                  CROSS JOIN public.saydin_role_contract contract
                 WHERE chunks.hypertable_schema='public'
                   AND chunks.hypertable_name='price_points'
                   AND contract.singleton=1
                 ORDER BY chunks.range_end DESC NULLS LAST
                 LIMIT 1
                """;
            await using var reader = await resolve.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue("a current price Timescale chunk is required");
            chunkSchema = reader.GetString(0);
            chunkName = reader.GetString(1);
            apiRole = reader.GetString(2);
        }

        var qualifiedChunk = $"{Quote(chunkSchema)}.{Quote(chunkName)}";
        DatabaseFixture.VerifyPriceAuthorityFingerprint(connection).Should().BeTrue();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = driftKind switch
            {
                "chunk-column-acl" =>
                    $"GRANT SELECT(close) ON TABLE {qualifiedChunk} TO {Quote(apiRole)}",
                "chunk-extra-trigger" => $"""
                    CREATE FUNCTION public.api_fixture_unexpected_chunk_trigger()
                    RETURNS trigger LANGUAGE plpgsql
                    SET search_path=pg_catalog,pg_temp
                    AS $body$ BEGIN RETURN NEW; END $body$;
                    CREATE TRIGGER api_fixture_unexpected_chunk_trigger
                    BEFORE INSERT ON {qualifiedChunk}
                    FOR EACH ROW EXECUTE FUNCTION public.api_fixture_unexpected_chunk_trigger()
                    """,
                "chunk-disabled-authority-trigger" =>
                    $"""
                    UPDATE pg_catalog.pg_trigger
                       SET tgenabled='D'
                     WHERE tgrelid='{qualifiedChunk}'::regclass
                       AND tgname='trg_price_points_authority'
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(driftKind)),
            };
            await command.ExecuteNonQueryAsync();

            DatabaseFixture.VerifyPriceAuthorityFingerprint(connection, transaction)
                .Should().BeFalse($"{driftKind} must make API fixture readiness fail closed");
            await transaction.RollbackAsync();
        }

        DatabaseFixture.VerifyPriceAuthorityFingerprint(connection).Should().BeTrue();

        static string Quote(string identifier) =>
            new NpgsqlCommandBuilder().QuoteIdentifier(identifier);
    }

    [SkippableFact]
    public async Task ManagedApi_AllPriceReadShapes_ExposeOnlyCompleteFinalAuthority()
    {
        Skip.IfNot(db.Available, db.SkipReason);
        Skip.IfNot(db.PriceAuthority, "Frozen migration 020 authority fingerprint is required.");
        await using var scenario = await AuthorityObservationScenario.CreateAsync(db);

        var adminConnectionString = SecureSecretFile.ReadConnectionString(
            Environment.GetEnvironmentVariable("SAYDIN_TEST_ADMIN_CONNECTION_FILE")
            ?? throw new InvalidOperationException("Admin setup connection file missing."));
        await using (var fingerprintConnection = new NpgsqlConnection(adminConnectionString))
        {
            await fingerprintConnection.OpenAsync();
            DatabaseFixture.VerifyPriceAuthorityFingerprint(fingerprintConnection).Should().BeTrue(
                "chunks created after migration 020 must retain exact owner/ACL/trigger authority");
        }

        await using (var admin = db.CreateAdminContext())
        {
            var drift = await admin.PricePoints
                .Where(point => point.AssetId == scenario.AssetId)
                .Select(point => new
                {
                    point.PriceDate,
                    HashLength = point.ObservationSha256 == null
                        ? (int?)null
                        : point.ObservationSha256.Length,
                })
                .ToListAsync();
            drift.Should().Contain(row =>
                row.PriceDate == AuthorityObservationScenario.WrongSourcePriceDate
                && row.HashLength == 32);
            drift.Should().Contain(row =>
                row.PriceDate == AuthorityObservationScenario.ShortHashPriceDate
                && row.HashLength == 31);
        }

        await using var context = db.CreateContext();
        var sessionUser = await context.Database.SqlQueryRaw<string>(
                "SELECT session_user::text AS \"Value\"")
            .SingleAsync();
        sessionUser.Should().EndWith("_api_login_v1", "repository SUT must use the managed API login");
        var repository = new PriceRepository(context);

        (await repository.GetPriceAsync(
            scenario.Symbol, AuthorityObservationScenario.LegacyExactPriceDate,
            CancellationToken.None)).Should().BeNull();
        (await repository.GetPriceAsync(
            scenario.Symbol, AuthorityObservationScenario.FirstFinalPriceDate,
            CancellationToken.None)).Should().Match<PricePoint>(point =>
                point.Close == 11m && point.ObservationSha256!.Length == 32);
        (await repository.GetPriceAsync(
            scenario.Symbol, AuthorityObservationScenario.WrongSourcePriceDate,
            CancellationToken.None)).Should().BeNull("provider_source must equal the asset source");
        (await repository.GetPriceAsync(
            scenario.Symbol, AuthorityObservationScenario.ShortHashPriceDate,
            CancellationToken.None)).Should().BeNull("a SHA-256 authority hash must be exactly 32 bytes");

        var nearest = await repository.GetNearestPriceAsync(
            scenario.Symbol, new DateOnly(2097, 8, 12), 7, CancellationToken.None);
        nearest!.PriceDate.Should().Be(AuthorityObservationScenario.FirstFinalPriceDate);

        (await repository.GetLatestPriceDateAsync(scenario.Symbol, CancellationToken.None))
            .Should().Be(AuthorityObservationScenario.LastFinalPriceDate,
                "newer legacy and malformed rows must not affect latest");

        var range = await repository.GetPriceRangeAsync(
            scenario.Symbol,
            AuthorityObservationScenario.LegacyExactPriceDate,
            AuthorityObservationScenario.LegacyLatestPriceDate,
            CancellationToken.None);
        range.Select(point => point.PriceDate).Should().Equal(
            AuthorityObservationScenario.FirstFinalPriceDate,
            AuthorityObservationScenario.SecondFinalPriceDate,
            AuthorityObservationScenario.LastFinalPriceDate);

        var dateRange = await repository.GetAllActiveAssetsWithDateRangesAsync(CancellationToken.None);
        var assetRange = dateRange.Single(row => row.Asset.Id == scenario.AssetId);
        assetRange.FirstDate.Should().Be(AuthorityObservationScenario.FirstFinalPriceDate);
        assetRange.LastDate.Should().Be(AuthorityObservationScenario.LastFinalPriceDate);
    }

    private sealed class ReaderCommandCounter : DbCommandInterceptor
    {
        internal int ReaderCommandCount { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommandCount++;
            return ValueTask.FromResult(result);
        }
    }
}
