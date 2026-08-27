using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Saydin.DatabaseMigrator.Tests;

[Trait("Category", "Unit")]
public sealed class MigrationImpactManifestTests
{
    private const string SystemHash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("ALTER TABLE public.users ALTER COLUMN device_id TYPE text;", "table-rewrite")]
    [InlineData("ALTER TABLE public.users VALIDATE CONSTRAINT chk_users;", "validate-constraint")]
    [InlineData("CREATE INDEX ix_users_device ON public.users(device_id);", "create-index-nonconcurrent")]
    [InlineData("CREATE INDEX CONCURRENTLY ix_users_device ON public.users(device_id);", "create-index-concurrent")]
    [InlineData("UPDATE public.users SET device_id='x' WHERE device_id IS NULL;", "large-dml")]
    [InlineData("DELETE FROM public.users WHERE device_id IS NULL;", "large-dml")]
    [InlineData("SELECT compress_chunk('public.chunk');", "timescale-compression")]
    [InlineData("SELECT drop_chunks('public.users');", "timescale-chunk-operation")]
    [InlineData("VACUUM public.users;", "opaque-or-unknown")]
    public void Analyze_ClassifiesRiskWithoutReadingLiterals(string sql, string expected)
    {
        var result = SqlImpactAnalyzer.Analyze(sql);

        result.Classifications.Should().Contain(expected);
        if (expected is not ("timescale-compression" or "timescale-chunk-operation" or
            "opaque-or-unknown"))
            result.Relations.Should().Contain("public.users");
    }

    [Fact]
    public void Analyze_TimescaleOperation_RequiresExactPublicRelationLiteral()
    {
        var bound = SqlImpactAnalyzer.Analyze("SELECT compress_chunk('public.activity_logs');");
        var unbound = SqlImpactAnalyzer.Analyze("SELECT compress_chunk('activity_logs');");
        var spoofed = SqlImpactAnalyzer.Analyze(
            "SELECT compress_chunk('activity_logs'),'public.activity_logs';");

        bound.Classifications.Should().Equal("timescale-compression");
        bound.Relations.Should().Equal("public.activity_logs");
        unbound.Classifications.Should().Contain("opaque-or-unknown");
        unbound.Relations.Should().BeEmpty();
        spoofed.Classifications.Should().Contain("opaque-or-unknown");
        spoofed.Relations.Should().BeEmpty();
    }

    [Fact]
    public void Canonicalize_DuplicateProperty_Rejects()
    {
        var action = () => CanonicalJson.Canonicalize("{\"a\":1,\"a\":2}"u8.ToArray());

        action.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("migration_impact_manifest_invalid");
    }

    [Fact]
    public void LoadAndVerify_SignedTransactionalTail_BindsSqlPredecessorAndTarget()
    {
        using var fixture = SignedImpactFixture.Create(
            "ALTER TABLE public.users ALTER COLUMN device_id TYPE text;",
            executionMode: "transactional",
            classifications: ["table-rewrite"],
            onlinePlan: null);

        var impacts = MigrationImpactSet.LoadAndVerify(
            fixture.Manifest, 27, fixture.Configuration);

        var impact = impacts.For("026_impact_test");
        impact.Mode.Should().Be(MigrationExecutionMode.Transactional);
        impact.Document.Target.RequiredPredecessorVersion.Should()
            .Be("025_ingestion_calendar_rebind");
        impact.SqlAnalysis.Relations.Should().Equal("public.users");
    }

    [Fact]
    public void LoadAndVerify_WrongSignature_RejectsBeforeDatabaseAccess()
    {
        using var fixture = SignedImpactFixture.Create(
            "ALTER TABLE public.users ALTER COLUMN device_id TYPE text;",
            "transactional", ["table-rewrite"], null);
        File.WriteAllText(fixture.SignatureFile, Convert.ToBase64String(new byte[64]));

        var action = () => MigrationImpactSet.LoadAndVerify(
            fixture.Manifest, 27, fixture.Configuration);

        action.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("migration_impact_signature_invalid");
    }

    [Fact]
    public void LoadAndVerify_MissingConfiguration_RejectsExactCode()
    {
        using var fixture = SignedImpactFixture.Create(
            "ALTER TABLE public.users ALTER COLUMN device_id TYPE text;",
            "transactional", ["table-rewrite"], null);

        var action = () => MigrationImpactSet.LoadAndVerify(
            fixture.Manifest, 27, configuration: null);

        action.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("migration_impact_configuration_required");
    }

    [Fact]
    public void LoadAndVerify_MalformedConfiguration_RejectsExactCode()
    {
        using var fixture = SignedImpactFixture.Create(
            "ALTER TABLE public.users ALTER COLUMN device_id TYPE text;",
            "transactional", ["table-rewrite"], null);
        var configuration = fixture.Configuration with { PublicKeySha256 = "not-a-sha256" };

        var action = () => MigrationImpactSet.LoadAndVerify(
            fixture.Manifest, 27, configuration);

        action.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("migration_impact_configuration_invalid");
    }

    [Fact]
    public void LoadAndVerify_PublicKeyPinMismatch_RejectsExactCode()
    {
        using var fixture = SignedImpactFixture.Create(
            "ALTER TABLE public.users ALTER COLUMN device_id TYPE text;",
            "transactional", ["table-rewrite"], null);
        var pinnedSha256 = fixture.Configuration.PublicKeySha256;
        var configuration = fixture.Configuration with
        {
            PublicKeySha256 = (pinnedSha256[0] == '0' ? '1' : '0') + pinnedSha256[1..],
        };

        var action = () => MigrationImpactSet.LoadAndVerify(
            fixture.Manifest, 27, configuration);

        action.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("migration_impact_public_key_mismatch");
    }

    [Fact]
    public void LoadAndVerify_InvalidPublicKey_RejectsExactCode()
    {
        using var fixture = SignedImpactFixture.Create(
            "ALTER TABLE public.users ALTER COLUMN device_id TYPE text;",
            "transactional", ["table-rewrite"], null);
        File.WriteAllText(fixture.Configuration.PublicKeyFile, "not a public key");

        var action = () => MigrationImpactSet.LoadAndVerify(
            fixture.Manifest, 27, fixture.Configuration);

        action.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("migration_impact_public_key_invalid");
    }

    [Theory]
    [InlineData("migrationSha256", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("migrationVersion", "023_wrong_identity")]
    public void LoadAndVerify_MigrationIdentityMismatch_RejectsExactCode(
        string property,
        string value)
    {
        using var fixture = SignedImpactFixture.Create(
            "ALTER TABLE public.users ALTER COLUMN device_id TYPE text;",
            "transactional", ["table-rewrite"], null,
            mutateDocument: document => document[property] = value);

        var action = () => MigrationImpactSet.LoadAndVerify(
            fixture.Manifest, 27, fixture.Configuration);

        action.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("migration_impact_identity_mismatch");
    }

    [Theory]
    [InlineData("requiredPredecessorSha256", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("requiredPredecessorVersion", "021_api_trust_expand")]
    [InlineData("requiredSchemaManifestSha256", "0000000000000000000000000000000000000000000000000000000000000000")]
    public void LoadAndVerify_PredecessorBindingMismatch_RejectsExactCode(
        string property,
        string value)
    {
        using var fixture = SignedImpactFixture.Create(
            "ALTER TABLE public.users ALTER COLUMN device_id TYPE text;",
            "transactional", ["table-rewrite"], null,
            mutateDocument: document =>
                ((Dictionary<string, object?>)document["target"]!)[property] = value);

        var action = () => MigrationImpactSet.LoadAndVerify(
            fixture.Manifest, 27, fixture.Configuration);

        action.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("migration_impact_predecessor_invalid");
    }

    [Fact]
    public void LoadAndVerify_UnexpectedImpactFile_RejectsExactCode()
    {
        using var fixture = SignedImpactFixture.Create(
            "ALTER TABLE public.users ALTER COLUMN device_id TYPE text;",
            "transactional", ["table-rewrite"], null);
        File.WriteAllText(Path.Combine(fixture.Impacts.Path, "999_unexpected.impact.json"), "{}");

        var action = () => MigrationImpactSet.LoadAndVerify(
            fixture.Manifest, 27, fixture.Configuration);

        action.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("migration_impact_file_set_mismatch");
    }

    [Fact]
    public void For_MissingImpactDefinition_RejectsExactCode()
    {
        var action = () => MigrationImpactSet.Empty.For("023_missing");

        action.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("migration_impact_manifest_missing");
    }

    [Fact]
    public void LoadAndVerify_InvalidRelation_RejectsExactCode()
    {
        using var fixture = SignedImpactFixture.Create(
            "ALTER TABLE public.users ALTER COLUMN device_id TYPE text;",
            "transactional", ["table-rewrite"], null,
            mutateDocument: document =>
                ((Dictionary<string, object?>[])document["relations"]!)[0]["name"] =
                    "private.users");

        var action = () => MigrationImpactSet.LoadAndVerify(
            fixture.Manifest, 27, fixture.Configuration);

        action.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("migration_impact_relation_invalid");
    }

    [Fact]
    public void LoadAndVerify_InvalidPostcondition_RejectsExactCode()
    {
        using var fixture = SignedImpactFixture.Create(
            "ALTER TABLE public.users ALTER COLUMN device_id TYPE text;",
            "transactional", ["table-rewrite"], null,
            mutateDocument: document =>
                ((Dictionary<string, object?>[])document["postconditions"]!)[0]["kind"] =
                    "unsupported");

        var action = () => MigrationImpactSet.LoadAndVerify(
            fixture.Manifest, 27, fixture.Configuration);

        action.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("migration_impact_postcondition_invalid");
    }

    [Fact]
    public void LoadAndVerify_InvalidOnlinePlan_RejectsExactCode()
    {
        var onlinePlan = OnlinePlan();
        onlinePlan["batchSize"] = 0;
        using var fixture = SignedImpactFixture.Create(
            "-- execution is generated by the bounded online plan\n",
            "resumable-online", ["resumable-online"], onlinePlan);

        var action = () => MigrationImpactSet.LoadAndVerify(
            fixture.Manifest, 27, fixture.Configuration);

        action.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("migration_online_plan_contract_invalid");
    }

    [Fact]
    public void LoadAndVerify_LexicallyInvalidSql_RejectsExactCode()
    {
        using var fixture = SignedImpactFixture.Create(
            "UPDATE public.users SET device_id = 'unterminated;",
            "transactional", ["large-dml"], null);

        var action = () => MigrationImpactSet.LoadAndVerify(
            fixture.Manifest, 27, fixture.Configuration);

        action.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("migration_sql_lexically_invalid");
    }

    [Fact]
    public void LoadAndVerify_UnknownSqlCommand_RejectsEvenWhenSigned()
    {
        using var fixture = SignedImpactFixture.Create(
            "VACUUM public.users;", "transactional", ["opaque-or-unknown"], null);

        var action = () => MigrationImpactSet.LoadAndVerify(
            fixture.Manifest, 27, fixture.Configuration);

        action.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("migration_impact_static_classification_mismatch");
    }

    [Fact]
    public void LoadAndVerify_OnlinePlan_RequiresNoRawExecutableSql()
    {
        using var fixture = SignedImpactFixture.Create(
            "-- execution is generated by the bounded online plan\n",
            "resumable-online", ["resumable-online"], OnlinePlan());

        var impacts = MigrationImpactSet.LoadAndVerify(
            fixture.Manifest, 27, fixture.Configuration);

        impacts.For("026_impact_test").Mode.Should().Be(MigrationExecutionMode.ResumableOnline);
    }

    [Fact]
    public void LoadAndVerify_SignedBudgetCannotViolateWalBound()
    {
        using var fixture = SignedImpactFixture.Create(
            "ALTER TABLE public.users ALTER COLUMN device_id TYPE text;",
            "transactional", ["table-rewrite"], null,
            budgets => budgets["estimatedAdditionalBytes"] = 2_000_001L);

        var action = () => MigrationImpactSet.LoadAndVerify(
            fixture.Manifest, 27, fixture.Configuration);

        action.Should().Throw<MigratorRejectedException>()
            .Which.Code.Should().Be("migration_impact_budget_invalid");
    }

    private static Dictionary<string, object?> OnlinePlan() => new()
    {
        ["batchSize"] = 100,
        ["keyColumn"] = "id",
        ["maxBatchMilliseconds"] = 1_000,
        ["pauseCompressionPolicy"] = false,
        ["planKind"] = "uuid-keyset-set-constant-where-null",
        ["relation"] = "public.users",
        ["targetColumn"] = "device_id",
        ["targetType"] = "text",
        ["targetValue"] = "redacted",
    };

    private sealed class SignedImpactFixture : IDisposable
    {
        private SignedImpactFixture(
            TemporaryDirectory migrations,
            TemporaryDirectory impacts,
            MigrationManifest manifest,
            MigrationImpactConfiguration configuration,
            string signatureFile)
        {
            Migrations = migrations;
            Impacts = impacts;
            Manifest = manifest;
            Configuration = configuration;
            SignatureFile = signatureFile;
        }

        public TemporaryDirectory Migrations { get; }
        public TemporaryDirectory Impacts { get; }
        public MigrationManifest Manifest { get; }
        public MigrationImpactConfiguration Configuration { get; }
        public string SignatureFile { get; }

        public static SignedImpactFixture Create(
            string sql,
            string executionMode,
            string[] classifications,
            Dictionary<string, object?>? onlinePlan,
            Action<Dictionary<string, object?>>? mutateBudgets = null,
            Action<Dictionary<string, object?>>? mutateDocument = null)
        {
            var migrations = TemporaryDirectory.Create();
            var impacts = TemporaryDirectory.Create();
            try
            {
                foreach (var source in Directory.EnumerateFiles(TestPaths.MigrationsDirectory))
                    if (Path.GetExtension(source) is ".sql" or ".sh")
                        File.Copy(source, Path.Combine(migrations.Path, Path.GetFileName(source)));
                File.WriteAllText(Path.Combine(migrations.Path, "026_impact_test.sql"), sql,
                    new UTF8Encoding(false));
                var manifest = MigrationManifest.Load(migrations.Path);
                var migration = manifest.Migrations.Single(item => item.Version == "026_impact_test");
                var predecessor = manifest.Migrations[26];

                var budgets = new Dictionary<string, object?>
                {
                    ["declaredTablespaceCapacityBytes"] = 10_000_000_000L,
                    ["estimatedAdditionalBytes"] = 1_000_000L,
                    ["lockTimeoutMilliseconds"] = 1_000,
                    ["maxBlockingTransactionAgeSeconds"] = 30,
                    ["maxCompressedBytes"] = 1_000_000_000L,
                    ["maxProjectedWalBytes"] = 2_000_000L,
                    ["maxRelationBytes"] = 1_000_000_000L,
                    ["maxReplicaLagBytes"] = 1_000_000L,
                    ["maxSlotRetentionBytes"] = 1_000_000L,
                    ["maxWaitingLocks"] = 0,
                    ["minFreeBytesAfter"] = 1_000_000L,
                    ["minHeadroomRatioBasisPoints"] = 100,
                    ["minimumStreamingReplicas"] = 0,
                    ["requireAllSlotsActive"] = true,
                    ["statementTimeoutMilliseconds"] = 10_000,
                    ["totalTimeoutSeconds"] = 60,
                };
                mutateBudgets?.Invoke(budgets);
                var document = new Dictionary<string, object?>
                {
                    ["budgets"] = budgets,
                    ["classifications"] = classifications,
                    ["executionMode"] = executionMode,
                    ["migrationSha256"] = migration.Checksum,
                    ["migrationVersion"] = migration.Version,
                    ["onlinePlan"] = onlinePlan,
                    ["postconditions"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["column"] = null,
                            ["index"] = null,
                            ["kind"] = "relation-exists",
                            ["relation"] = "public.users",
                        },
                    },
                    ["relations"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["includeChunks"] = false,
                            ["includeCompressed"] = false,
                            ["name"] = "public.users",
                            ["tablespace"] = "pg_default",
                        },
                    },
                    ["schemaVersion"] = 1,
                    ["target"] = new Dictionary<string, object?>
                    {
                        ["database"] = "saydin",
                        ["requiredPredecessorSha256"] = predecessor.Checksum,
                        ["requiredPredecessorVersion"] = predecessor.Version,
                        ["requiredSchemaManifestSha256"] = manifest.ChecksumThrough(27),
                        ["systemIdentifierSha256"] = SystemHash,
                    },
                };
                mutateDocument?.Invoke(document);
                var canonical = CanonicalJson.Canonicalize(
                    JsonSerializer.SerializeToUtf8Bytes(document));
                var manifestFile = Path.Combine(impacts.Path, "026_impact_test.impact.json");
                File.WriteAllBytes(manifestFile, canonical);
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var publicFile = Path.Combine(impacts.Path, "impact-public.pem");
                File.WriteAllText(publicFile, key.ExportSubjectPublicKeyInfoPem(), new UTF8Encoding(false));
                var publicSha = Convert.ToHexStringLower(SHA256.HashData(
                    key.ExportSubjectPublicKeyInfo()));
                var signature = key.SignData(
                    canonical, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
                var signatureFile = Path.Combine(impacts.Path, "026_impact_test.impact.sig");
                File.WriteAllText(signatureFile, Convert.ToBase64String(signature),
                    new UTF8Encoding(false));
                return new SignedImpactFixture(
                    migrations, impacts, manifest,
                    new MigrationImpactConfiguration(impacts.Path, publicFile, publicSha),
                    signatureFile);
            }
            catch
            {
                migrations.Dispose();
                impacts.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Migrations.Dispose();
            Impacts.Dispose();
        }
    }
}
