using System.Security.Cryptography;
using System.Text;
using Saydin.Migrations;

namespace Saydin.DataRepair;

internal static class EmbeddedRepairMigrationTrust
{
    public static IReadOnlyList<RepairMigrationEntry> Entries { get; } =
        MigrationTrustRoot.Versions.Select(version =>
            new RepairMigrationEntry(version, MigrationTrustRoot.Checksums[version])).ToArray();

    public static string ManifestSha256 { get; } = ComputeManifestSha256(Entries);

    public static string ComputeManifestSha256(IReadOnlyList<RepairMigrationEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var migration in entries)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(migration.Version));
            hash.AppendData([0]);
            hash.AppendData(Encoding.ASCII.GetBytes(migration.Sha256));
            hash.AppendData([0]);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
