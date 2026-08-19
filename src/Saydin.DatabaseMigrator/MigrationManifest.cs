using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Saydin.DatabaseMigrator;

internal enum MigrationKind
{
    Sql,
    OptionalExporterRole,
}

internal sealed record MigrationDefinition(
    string Version,
    string FileName,
    string Path,
    byte[] RawBytes,
    string Checksum,
    MigrationKind Kind)
{
    public string ReadSql() => Encoding.UTF8.GetString(RawBytes);
}

internal sealed class MigrationManifest
{
    private static readonly Regex FileNamePattern = new(
        "^[0-9]{3}[a-z]?_[a-z0-9_]+\\.(sql|sh)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private MigrationManifest(IReadOnlyList<MigrationDefinition> migrations, string checksum)
    {
        Migrations = migrations;
        Checksum = checksum;
    }

    public IReadOnlyList<MigrationDefinition> Migrations { get; }
    public string Checksum { get; }

    public string ChecksumThrough(int exclusiveCount)
    {
        if (exclusiveCount is < 1 || exclusiveCount > Migrations.Count)
            throw new MigratorRejectedException("migration_manifest_prefix_invalid");
        using var manifestHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var migration in Migrations.Take(exclusiveCount))
        {
            manifestHash.AppendData(Encoding.UTF8.GetBytes(migration.Version));
            manifestHash.AppendData([0]);
            manifestHash.AppendData(Encoding.ASCII.GetBytes(migration.Checksum));
            manifestHash.AppendData([0]);
        }
        return Convert.ToHexStringLower(manifestHash.GetHashAndReset());
    }

    public static MigrationManifest Load(string directory)
    {
        if (!Directory.Exists(directory))
            throw new MigratorRejectedException("migration_directory_missing");

        var files = Directory.EnumerateFiles(directory)
            .Where(path => Path.GetExtension(path) is ".sql" or ".sh")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        if (files.Length == 0)
            throw new MigratorRejectedException("migration_manifest_empty");

        var migrations = new List<MigrationDefinition>(files.Length);
        var versions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in files)
        {
            var fileName = Path.GetFileName(path);
            if (!FileNamePattern.IsMatch(fileName))
                throw new MigratorRejectedException("migration_filename_invalid", fileName);

            var extension = Path.GetExtension(fileName);
            var version = Path.GetFileNameWithoutExtension(fileName);
            if (!versions.Add(version))
                throw new MigratorRejectedException("migration_version_duplicate", version);

            var kind = extension switch
            {
                ".sql" => MigrationKind.Sql,
                ".sh" when version == "012b_create_exporter_role" => MigrationKind.OptionalExporterRole,
                _ => throw new MigratorRejectedException("shell_migration_unsupported", version),
            };
            var rawBytes = File.ReadAllBytes(path);
            migrations.Add(new MigrationDefinition(
                version,
                fileName,
                path,
                rawBytes,
                Convert.ToHexStringLower(SHA256.HashData(rawBytes)),
                kind));
        }

        using var manifestHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var migration in migrations)
        {
            manifestHash.AppendData(Encoding.UTF8.GetBytes(migration.Version));
            manifestHash.AppendData([0]);
            manifestHash.AppendData(Encoding.ASCII.GetBytes(migration.Checksum));
            manifestHash.AppendData([0]);
        }

        return new MigrationManifest(
            migrations,
            Convert.ToHexStringLower(manifestHash.GetHashAndReset()));
    }
}
