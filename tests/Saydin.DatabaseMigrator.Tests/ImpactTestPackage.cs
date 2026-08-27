using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Saydin.DatabaseMigrator.Tests;

internal sealed class ImpactTestPackage : IDisposable
{
    private readonly TemporaryDirectory migrations;
    private readonly TemporaryDirectory impacts;

    private ImpactTestPackage(
        TemporaryDirectory migrations,
        TemporaryDirectory impacts,
        MigrationManifest manifest,
        MigrationImpactConfiguration configuration)
    {
        this.migrations = migrations;
        this.impacts = impacts;
        Manifest = manifest;
        Configuration = configuration;
    }

    public string MigrationsDirectory => migrations.Path;
    public MigrationManifest Manifest { get; }
    public MigrationImpactConfiguration Configuration { get; }

    public static ImpactTestPackage Create(
        TestDatabase database,
        string sql,
        string executionMode,
        string[] classifications,
        string relation,
        Dictionary<string, object?>? onlinePlan = null,
        string postconditionKind = "relation-exists",
        string? postconditionColumn = null,
        string? postconditionIndex = null,
        Action<Dictionary<string, object?>>? mutateBudgets = null,
        Action<Dictionary<string, object?>>? mutateTarget = null,
        bool includeChunks = false,
        bool includeCompressed = false,
        string migrationVersion = "026_impact_test",
        string? postconditionRelation = null)
    {
        var migrations = TemporaryDirectory.Create();
        var impacts = TemporaryDirectory.Create();
        try
        {
            foreach (var source in Directory.EnumerateFiles(TestPaths.MigrationsDirectory))
                if (Path.GetExtension(source) is ".sql" or ".sh")
                    File.Copy(source, Path.Combine(migrations.Path, Path.GetFileName(source)));
            File.WriteAllText(Path.Combine(migrations.Path, $"{migrationVersion}.sql"), sql,
                new UTF8Encoding(false));
            var manifest = MigrationManifest.Load(migrations.Path);
            var migration = manifest.Migrations.Single(item => item.Version == migrationVersion);
            var predecessor = manifest.Migrations[26];
            var budgets = new Dictionary<string, object?>
            {
                ["declaredTablespaceCapacityBytes"] = 10_000_000_000_000L,
                ["estimatedAdditionalBytes"] = 8_000_000L,
                ["lockTimeoutMilliseconds"] = 1_000,
                ["maxBlockingTransactionAgeSeconds"] = 30,
                ["maxCompressedBytes"] = 1_000_000_000L,
                ["maxProjectedWalBytes"] = 16_000_000L,
                ["maxRelationBytes"] = 1_000_000_000L,
                ["maxReplicaLagBytes"] = 64_000_000L,
                ["maxSlotRetentionBytes"] = 64_000_000L,
                ["maxWaitingLocks"] = 0,
                ["minFreeBytesAfter"] = 64_000_000L,
                ["minHeadroomRatioBasisPoints"] = 100,
                ["minimumStreamingReplicas"] = 0,
                ["requireAllSlotsActive"] = true,
                ["statementTimeoutMilliseconds"] = 10_000,
                ["totalTimeoutSeconds"] = 60,
            };
            mutateBudgets?.Invoke(budgets);
            var target = new Dictionary<string, object?>
            {
                ["database"] = database.Name,
                ["requiredPredecessorSha256"] = predecessor.Checksum,
                ["requiredPredecessorVersion"] = predecessor.Version,
                ["requiredSchemaManifestSha256"] = manifest.ChecksumThrough(27),
                ["systemIdentifierSha256"] = database.Contract.SystemIdentifierSha256,
            };
            mutateTarget?.Invoke(target);
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
                        ["column"] = postconditionColumn,
                        ["index"] = postconditionIndex,
                        ["kind"] = postconditionKind,
                        ["relation"] = postconditionRelation ?? relation,
                    },
                },
                ["relations"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["includeChunks"] = includeChunks,
                        ["includeCompressed"] = includeCompressed,
                        ["name"] = relation,
                        ["tablespace"] = "pg_default",
                    },
                },
                ["schemaVersion"] = 1,
                ["target"] = target,
            };
            var canonical = CanonicalJson.Canonicalize(JsonSerializer.SerializeToUtf8Bytes(document));
            File.WriteAllBytes(
                Path.Combine(impacts.Path, $"{migrationVersion}.impact.json"), canonical);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicFile = Path.Combine(impacts.Path, "impact-public.pem");
            File.WriteAllText(publicFile, key.ExportSubjectPublicKeyInfoPem(), new UTF8Encoding(false));
            var publicSha = Convert.ToHexStringLower(SHA256.HashData(
                key.ExportSubjectPublicKeyInfo()));
            var signature = key.SignData(
                canonical, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            File.WriteAllText(
                Path.Combine(impacts.Path, $"{migrationVersion}.impact.sig"),
                Convert.ToBase64String(signature), new UTF8Encoding(false));
            return new ImpactTestPackage(
                migrations,
                impacts,
                manifest,
                new MigrationImpactConfiguration(impacts.Path, publicFile, publicSha));
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
        migrations.Dispose();
        impacts.Dispose();
    }
}
