using Saydin.Migrations;

namespace Saydin.DatabaseMigrator;

// The deploy migrator and DQA consume the same canonical immutable trust root.
internal static class MigratorMigrationTrustRoot
{
    internal static IReadOnlyList<string> Versions => MigrationTrustRoot.Versions;

    internal static IReadOnlyDictionary<string, string> Checksums => MigrationTrustRoot.Checksums;
}
