using System.Security.Cryptography;
using System.Text;
using FluentAssertions;

namespace Saydin.DatabaseMigrator.Tests;

[Trait("Category", "Unit")]
public sealed class SqlScriptNormalizerTests
{
    [Fact]
    public void Normalize_OuterTransactionAndDollarQuotedBody_RemovesOnlyOuterCommands()
    {
        const string sql = """
            -- leading comment
            BEGIN;
            DO $body$
            BEGIN
                RAISE NOTICE 'BEGIN; COMMIT; \\echo';
            END
            $body$;
            COMMIT;
            """;
        var migration = Definition(sql);

        var normalized = SqlScriptNormalizer.Normalize(migration);

        normalized.Should().Contain("DO $body$");
        normalized.Should().Contain(@"RAISE NOTICE 'BEGIN; COMMIT; \\echo'");
        normalized.Should().NotContain("\nBEGIN;\nDO");
        normalized.Should().NotEndWith("COMMIT;");
        normalized.Count(character => character == '\n').Should().Be(sql.Count(character => character == '\n'));
    }

    [Fact]
    public void Normalize_PsqlMetaCommandOutsideQuotedBody_RejectsWithoutRewriting()
    {
        var migration = Definition("SELECT 1;\n\\echo unsafe\n");

        var act = () => SqlScriptNormalizer.Normalize(migration);

        act.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("psql_metacommand_unsupported");
    }

    [Fact]
    public void Normalize_UnpairedTransactionControl_Rejects()
    {
        var migration = Definition("BEGIN; SELECT 1;");

        var act = () => SqlScriptNormalizer.Normalize(migration);

        act.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("transaction_control_unsupported");
    }

    [Theory]
    [InlineData("VACUUM assets;")]
    [InlineData("CREATE INDEX CONCURRENTLY idx_assets_source ON assets(source);")]
    [InlineData("CREATE UNIQUE INDEX CONCURRENTLY uq_assets_source ON assets(source);")]
    [InlineData("CREATE DATABASE unsafe;")]
    public void Normalize_TransactionIncompatibleStatement_RejectsBeforeExecution(string sql)
    {
        var migration = Definition(sql);

        var act = () => SqlScriptNormalizer.Normalize(migration);

        act.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("nontransactional_statement_unsupported");
    }

    [Fact]
    public void Normalize_AllHistoricalSql_PreservesLineCountAndProducesNonEmptyBody()
    {
        var migrations = MigrationManifest.Load(TestPaths.MigrationsDirectory);

        foreach (var migration in migrations.Migrations.Where(item => item.Kind == MigrationKind.Sql))
        {
            var normalized = SqlScriptNormalizer.Normalize(migration);
            var raw = migration.ReadSql();
            normalized.Should().NotBeNullOrWhiteSpace(migration.FileName);
            normalized.Count(character => character == '\n')
                .Should().Be(raw.Count(character => character == '\n'), migration.FileName);
            normalized.Length.Should().Be(raw.Length,
                $"normalization must preserve raw byte positions for {migration.FileName}");
            var removed = new string(raw.Where((character, index) =>
                    character != normalized[index]).ToArray());
            removed.Replace(" ", string.Empty, StringComparison.Ordinal)
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant().Should().BeOneOf([string.Empty, "begin;commit;"],
                    $"only the outer transaction wrapper may differ for {migration.FileName}");
            for (var index = 0; index < raw.Length; index++)
                if (raw[index] != normalized[index])
                    normalized[index].Should().Be(' ',
                        $"normalization may only blank non-newline bytes in {migration.FileName}");
        }
    }

    [Theory]
    [InlineData("008_add_activity_logs", "selectadd_compression_policy('activity_logs',interval'7 days')")]
    [InlineData("013_enable_activity_log_compression", "selectadd_compression_policy('activity_logs',interval'7 days',if_not_exists=>true)")]
    public void DeferPinnedActivityCompressionPolicy_ExactHistoricalStatement_IsBlanked(
        string version,
        string expectedCanonical)
    {
        var migration = MigrationManifest.Load(TestPaths.MigrationsDirectory).Migrations
            .Single(item => item.Version == version);
        var normalized = SqlScriptNormalizer.Normalize(migration);

        var deferred = SqlScriptNormalizer.DeferPinnedActivityCompressionPolicy(
            normalized, migration.FileName, expectedCanonical);

        deferred.Should().NotContain("add_compression_policy('activity_logs'");
        deferred.Count(character => character == '\n').Should().Be(normalized.Count(character => character == '\n'));
    }

    [Fact]
    public void DeferPinnedActivityCompressionPolicy_DuplicateOrMutatedStatement_IsRejected()
    {
        const string canonical = "selectadd_compression_policy('activity_logs',interval'7 days')";
        var duplicate = "SELECT add_compression_policy('activity_logs', INTERVAL '7 days');\n" +
                        "SELECT add_compression_policy('activity_logs', INTERVAL '7 days');";
        var mutated = "SELECT add_compression_policy('activity_logs', INTERVAL '8 days');";

        Action duplicateAction = () => SqlScriptNormalizer.DeferPinnedActivityCompressionPolicy(
            duplicate, "duplicate.sql", canonical);
        Action mutatedAction = () => SqlScriptNormalizer.DeferPinnedActivityCompressionPolicy(
            mutated, "mutated.sql", canonical);

        duplicateAction.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("compression_policy_defer_contract_mismatch");
        mutatedAction.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("compression_policy_defer_contract_mismatch");
    }

    private static MigrationDefinition Definition(string sql)
    {
        var bytes = Encoding.UTF8.GetBytes(sql);
        return new MigrationDefinition(
            "999_test", "999_test.sql", "999_test.sql", bytes,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), MigrationKind.Sql);
    }
}
