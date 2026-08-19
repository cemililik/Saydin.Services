using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Saydin.Migrations;

namespace Saydin.DataQualityAudit;

internal static partial class EmbeddedMigrations
{
    internal const string ScenarioIntegrityVersion = "018_scenario_integrity";
    internal const string ScenarioIntegrityChecksum =
        "8f6f76c12862c5f3696f9241c9e6566e75d048875552656b32b7eca84f65a056";
    internal const string ApiTrustVersion = "021_api_trust_expand";
    internal const string ApiTrustChecksum =
        "1f44aa1413d611cb8b078541e0100985c33614274e2fd700a8f8b94303045c1e";
    internal const string PrincipalRetentionVersion = "022_principal_retention";
    internal const string PrincipalRetentionChecksum =
        "568017c27eb6038a06b48ee00f2f0820bba6cf7b577dd5f283291ac9995e8afd";

    internal static IReadOnlyDictionary<string, string> PinnedChecksums =>
        MigrationTrustRoot.Checksums;

    [GeneratedRegex("^[0-9]{3}[a-z]?_[a-z0-9_]+\\.(sql|sh)$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationFileNamePattern();

    public static EmbeddedMigrationManifest Load(Assembly? assembly = null)
    {
        assembly ??= typeof(EmbeddedMigrations).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("Migrations/", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (resourceNames.Length == 0)
            throw new AuditRejectedException("embedded_migration_manifest_empty", AuditExitCodes.PreflightRejected);

        var migrations = new List<EmbeddedMigration>(resourceNames.Length);
        var versions = new HashSet<string>(StringComparer.Ordinal);
        using var manifestHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var resourceName in resourceNames)
        {
            var fileName = resourceName["Migrations/".Length..];
            if (!MigrationFileNamePattern().IsMatch(fileName))
                throw new AuditRejectedException("embedded_migration_filename_invalid", AuditExitCodes.PreflightRejected);
            var version = Path.GetFileNameWithoutExtension(fileName);
            if (!versions.Add(version))
                throw new AuditRejectedException("embedded_migration_version_duplicate", AuditExitCodes.PreflightRejected);

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new AuditRejectedException("embedded_migration_unreadable", AuditExitCodes.PreflightRejected);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var checksum = AuditCryptography.Sha256Hex(memory.ToArray());
            ValidatePinnedChecksum(version, checksum);
            migrations.Add(new EmbeddedMigration(version, fileName, checksum));
            manifestHash.AppendData(Encoding.UTF8.GetBytes(version));
            manifestHash.AppendData([0]);
            manifestHash.AppendData(Encoding.ASCII.GetBytes(checksum));
            manifestHash.AppendData([0]);
        }

        if (!migrations.Select(migration => migration.Version)
                .SequenceEqual(MigrationTrustRoot.Versions, StringComparer.Ordinal))
            throw new AuditRejectedException(
                "embedded_migration_manifest_mismatch", AuditExitCodes.PreflightRejected);

        return new EmbeddedMigrationManifest(
            migrations,
            Convert.ToHexStringLower(manifestHash.GetHashAndReset()));
    }

    internal static void ValidatePinnedChecksum(string version, string checksum)
    {
        if (!PinnedChecksums.TryGetValue(version, out var expected) ||
            !string.Equals(checksum, expected, StringComparison.Ordinal))
            throw new AuditRejectedException(
                "embedded_migration_checksum_mismatch", AuditExitCodes.PreflightRejected);
    }

}
