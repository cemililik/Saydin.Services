using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Saydin.DatabaseSecurity;

public enum LoginPurpose
{
    Migrator,
    Api,
    Ingestion,
    CalendarImporter,
    Exporter,
    Audit,
}

public enum ManagedRoleKind
{
    Owner,
    Capability,
    Login,
}

public sealed record ManagedRole(
    string Name,
    ManagedRoleKind Kind,
    string Purpose,
    int? LoginVersion,
    string Marker,
    bool Replication = false,
    int ConnectionLimit = -1,
    DateTimeOffset? ValidUntilUtc = null);

public sealed class RoleContract
{
    public const int ContractSchemaVersion = 1;
    private const string MarkerVersion = "saydin-role-bootstrap/v1";
    private static readonly Regex DeploymentPattern =
        new("^[a-z][a-z0-9-]{2,11}$", RegexOptions.CultureInvariant);
    private static readonly Regex DatabasePattern =
        new("^[a-zA-Z][a-zA-Z0-9_]{0,62}$", RegexOptions.CultureInvariant);
    private static readonly Regex PrefixPattern =
        new("^saydin_[a-z][a-z0-9_]{2}_[0-9a-f]{24}$", RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern =
        new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private RoleContract(string deploymentId, string database, string systemIdentifierSha256, string prefix)
    {
        DeploymentId = deploymentId;
        Database = database;
        SystemIdentifierSha256 = systemIdentifierSha256;
        Prefix = prefix;
        Owner = Role("owner", ManagedRoleKind.Owner);
        MigratorCapability = Role("migrator_cap", ManagedRoleKind.Capability);
        ApiCapability = Role("api_cap", ManagedRoleKind.Capability);
        IngestionCapability = Role("ingestion_cap", ManagedRoleKind.Capability);
        CalendarImporterCapability = Role("calendar_importer_cap", ManagedRoleKind.Capability);
        ExporterCapability = Role("exporter_cap", ManagedRoleKind.Capability);
        AuditCapability = Role("audit_cap", ManagedRoleKind.Capability);
        TimescaleScheduler = Role(
            "timescale_scheduler", ManagedRoleKind.Login, "timescale_scheduler",
            connectionLimit: 0);
    }

    public string DeploymentId { get; }
    public string Database { get; }
    public string SystemIdentifierSha256 { get; }
    public string Prefix { get; }
    public ManagedRole Owner { get; }
    public ManagedRole MigratorCapability { get; }
    public ManagedRole ApiCapability { get; }
    public ManagedRole IngestionCapability { get; }
    public ManagedRole CalendarImporterCapability { get; }
    public ManagedRole ExporterCapability { get; }
    public ManagedRole AuditCapability { get; }
    public ManagedRole TimescaleScheduler { get; }

    public IReadOnlyList<ManagedRole> Capabilities =>
    [
        MigratorCapability, ApiCapability, IngestionCapability,
        CalendarImporterCapability, ExporterCapability, AuditCapability,
    ];

    public IReadOnlyList<ManagedRole> StableRoles => [Owner, .. Capabilities, TimescaleScheduler];

    // The lock serializes every role graph claim for one physical database. It
    // deliberately excludes deployment, prefix, extension and contract inputs.
    public string TargetLockSha256 => Sha256Hex(
        $"role-bootstrap-target-lock/v2\0{SystemIdentifierSha256}\0{Database}");

    public string ContractSha256(string timescaleVersion, string uuidOsspVersion) =>
        Sha256Hex(ContractMaterial(timescaleVersion, uuidOsspVersion));

    public string BackupContractSha256(
        string timescaleVersion,
        string uuidOsspVersion,
        DateTimeOffset v1ValidUntilUtc) =>
        Sha256Hex(BackupContractMaterial(timescaleVersion, uuidOsspVersion, v1ValidUntilUtc));

    public string BackupContractMaterial(
        string timescaleVersion,
        string uuidOsspVersion,
        DateTimeOffset v1ValidUntilUtc)
    {
        var backup = BackupLogin(1, v1ValidUntilUtc);
        return string.Join('\n',
        [
            "backup-contract-schema=1",
            $"parent-contract={ContractSha256(timescaleVersion, uuidOsspVersion)}",
            $"role={backup.Name}:login:backup:1:{backup.Marker}",
            "attributes=login,replication,nosuperuser,nocreatedb,nocreaterole,noinherit,nobypassrls",
            $"connection-limit={backup.ConnectionLimit}",
            $"valid-until={FormatBackupValidUntil(backup.ValidUntilUtc!.Value)}",
            "role-config=none",
            "database-connect=none",
            "replication-protocol=physical",
            "membership=none",
            "schema-table-column-function-acl=none",
        ]);
    }

    public string ContractMaterial(string timescaleVersion, string uuidOsspVersion)
    {
        var lines = new List<string>
        {
            $"contract-schema={ContractSchemaVersion}",
            MarkerVersion,
            $"deployment={DeploymentId}",
            $"database={Database}",
            $"system={SystemIdentifierSha256}",
            $"prefix={Prefix}",
            $"extension=timescaledb:{timescaleVersion}",
            $"extension=uuid-ossp:{uuidOsspVersion}",
            $"database-owner={Owner.Name}",
            "public-database=none",
            "public-schema=none",
            $"database-connect={string.Join(',', Capabilities.Select(role => role.Name).Append(TimescaleScheduler.Name))}",
            $"schema-usage={string.Join(',', new[]
            {
                ApiCapability.Name, IngestionCapability.Name, CalendarImporterCapability.Name,
                AuditCapability.Name, TimescaleScheduler.Name,
            })}",
            $"pg-control={Owner.Name},{MigratorCapability.Name},{AuditCapability.Name}",
        };
        lines.AddRange(AllRolesForVersion(1).Select(role =>
            $"role={role.Name}:{role.Kind}:{role.Purpose}:{role.LoginVersion?.ToString() ?? "none"}:{role.Marker}"));
        foreach (var purpose in Enum.GetValues<LoginPurpose>())
        {
            lines.Add($"membership={Login(purpose, 1).Name}:{Capability(purpose).Name}:" +
                      "admin=false:inherit=true:set=false");
            if (purpose == LoginPurpose.Migrator)
                lines.Add($"membership={Login(purpose, 1).Name}:{Owner.Name}:" +
                          "admin=false:inherit=false:set=true");
        }
        lines.Add($"membership={Owner.Name}:{TimescaleScheduler.Name}:" +
                  "admin=false:inherit=false:set=true");
        lines.Add($"membership={ExporterCapability.Name}:pg_monitor:" +
                  "admin=false:inherit=true:set=false");
        return string.Join('\n', lines);
    }

    public static RoleContract Create(
        string deploymentId,
        string database,
        string systemIdentifierSha256,
        string suppliedPrefix)
    {
        if (!DeploymentPattern.IsMatch(deploymentId) || !DatabasePattern.IsMatch(database) ||
            !HashPattern.IsMatch(systemIdentifierSha256) || !PrefixPattern.IsMatch(suppliedPrefix))
            throw Rejected("role_contract_invalid", DatabaseSecurityFailureKind.InvalidArguments);

        var expectedPrefix = DerivePrefix(deploymentId, database, systemIdentifierSha256);
        if (!CryptographicEquals(expectedPrefix, suppliedPrefix))
            throw Rejected("role_prefix_contract_mismatch", DatabaseSecurityFailureKind.TargetRejected);

        var contract = new RoleContract(deploymentId, database, systemIdentifierSha256, expectedPrefix);
        if (contract.AllRolesForVersion(1).Any(role => Encoding.UTF8.GetByteCount(role.Name) > 63))
            throw Rejected("role_name_too_long", DatabaseSecurityFailureKind.InvalidArguments);
        return contract;
    }

    public static string DerivePrefix(string deploymentId, string database, string systemIdentifierSha256)
    {
        if (!DeploymentPattern.IsMatch(deploymentId) || !DatabasePattern.IsMatch(database) ||
            !HashPattern.IsMatch(systemIdentifierSha256))
            throw Rejected("role_contract_invalid", DatabaseSecurityFailureKind.InvalidArguments);
        var deploymentSlug = deploymentId.Replace('-', '_')[..3];
        var suffix = Sha256Hex($"{systemIdentifierSha256}\0{database}\0{deploymentId}")[..24];
        return $"saydin_{deploymentSlug}_{suffix}";
    }

    public IReadOnlyList<ManagedRole> AllRolesForVersion(int version) =>
        [.. StableRoles, .. Enum.GetValues<LoginPurpose>().Select(purpose => Login(purpose, version))];

    public ManagedRole Login(LoginPurpose purpose, int version)
    {
        if (version is < 1 or > 999)
            throw Rejected("login_version_invalid", DatabaseSecurityFailureKind.InvalidArguments);
        var purposeName = PurposeName(purpose);
        var role = Role($"{purposeName}_login_v{version}", ManagedRoleKind.Login, purposeName, version);
        if (Encoding.UTF8.GetByteCount(role.Name) > 63)
            throw Rejected("role_name_too_long", DatabaseSecurityFailureKind.InvalidArguments);
        return role;
    }

    public ManagedRole BackupLogin(int version, DateTimeOffset validUntilUtc)
    {
        if (version is < 1 or > 2)
            throw Rejected("backup_login_version_invalid", DatabaseSecurityFailureKind.InvalidArguments);
        var normalized = NormalizeBackupValidUntil(validUntilUtc);
        var name = $"{Prefix}_backup_login_v{version}";
        if (Encoding.UTF8.GetByteCount(name) > 63)
            throw Rejected("role_name_too_long", DatabaseSecurityFailureKind.InvalidArguments);
        var marker = $"{MarkerPrefix()}purpose=backup;kind=login;version={version};" +
                     $"valid-until={FormatBackupValidUntil(normalized)}";
        return new ManagedRole(
            name, ManagedRoleKind.Login, "backup", version, marker,
            Replication: true, ConnectionLimit: 2, ValidUntilUtc: normalized);
    }

    public ManagedRole Capability(LoginPurpose purpose) => purpose switch
    {
        LoginPurpose.Migrator => MigratorCapability,
        LoginPurpose.Api => ApiCapability,
        LoginPurpose.Ingestion => IngestionCapability,
        LoginPurpose.CalendarImporter => CalendarImporterCapability,
        LoginPurpose.Exporter => ExporterCapability,
        LoginPurpose.Audit => AuditCapability,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    public bool TryParseManagedMarker(string marker, out string purpose, out int? version)
    {
        if (TryResolveManagedMarker(marker, out var role))
        {
            purpose = role.Purpose;
            version = role.LoginVersion;
            return true;
        }
        purpose = string.Empty;
        version = null;
        return false;
    }

    public bool TryResolveManagedMarker(string marker, out ManagedRole role)
    {
        role = null!;
        var prefix = MarkerPrefix();
        if (!marker.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var fields = marker[prefix.Length..].Split(';', StringSplitOptions.RemoveEmptyEntries);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var pair = field.Split('=', 2);
            if (pair.Length != 2 || !values.TryAdd(pair[0], pair[1])) return false;
        }
        var isBackup = values.TryGetValue("purpose", out var candidatePurpose) &&
                       string.Equals(candidatePurpose, "backup", StringComparison.Ordinal);
        var expectedKeys = isBackup
            ? new[] { "purpose", "kind", "version", "valid-until" }
            : values.ContainsKey("version")
                ? new[] { "purpose", "kind", "version" }
                : new[] { "purpose", "kind" };
        if (values.Count != expectedKeys.Length || expectedKeys.Any(key => !values.ContainsKey(key)) ||
            !values.TryGetValue("purpose", out var parsedPurpose) ||
            !values.TryGetValue("kind", out var kind))
            return false;
        int? version = null;
        if (values.TryGetValue("version", out var rawVersion))
        {
            if (!int.TryParse(rawVersion, out var parsed) || parsed < 1) return false;
            version = parsed;
        }
        ManagedRole? expected;
        if (isBackup)
        {
            if (version is not (1 or 2) ||
                !values.TryGetValue("valid-until", out var rawValidUntil) ||
                !TryParseBackupValidUntil(rawValidUntil, out var validUntil))
                return false;
            expected = BackupLogin(version.Value, validUntil);
        }
        else
        {
            expected = version switch
            {
                null => StableRoles.SingleOrDefault(candidate => candidate.Purpose == parsedPurpose),
                >= 1 and <= 999 when Enum.GetValues<LoginPurpose>().Select(PurposeName)
                    .Contains(parsedPurpose, StringComparer.Ordinal) =>
                    Login(ParsePurpose(parsedPurpose), version.Value),
                _ => null,
            };
        }
        if (expected is null ||
            !string.Equals(kind, expected.Kind.ToString().ToLowerInvariant(), StringComparison.Ordinal) ||
            !string.Equals(marker, expected.Marker, StringComparison.Ordinal))
            return false;
        role = expected;
        return true;
    }

    public bool IsExactMarker(ManagedRole role, string marker) =>
        string.Equals(role.Marker, marker, StringComparison.Ordinal);

    private ManagedRole Role(
        string suffix,
        ManagedRoleKind kind,
        string? purpose = null,
        int? version = null,
        int connectionLimit = -1)
    {
        purpose ??= suffix;
        var marker = $"{MarkerPrefix()}purpose={purpose};kind={kind.ToString().ToLowerInvariant()}" +
                     (version is null ? string.Empty : $";version={version.Value}");
        return new ManagedRole(
            $"{Prefix}_{suffix}", kind, purpose, version, marker,
            ConnectionLimit: connectionLimit);
    }

    private string MarkerPrefix() =>
        $"{MarkerVersion};deployment={DeploymentId};database={Database};" +
        $"system={SystemIdentifierSha256};prefix={Prefix};";

    public static string PurposeName(LoginPurpose purpose) => purpose switch
    {
        LoginPurpose.Migrator => "migrator",
        LoginPurpose.Api => "api",
        LoginPurpose.Ingestion => "ingestion",
        LoginPurpose.CalendarImporter => "calendar_importer",
        LoginPurpose.Exporter => "exporter",
        LoginPurpose.Audit => "audit",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    public static string FormatBackupValidUntil(DateTimeOffset value) =>
        NormalizeBackupValidUntil(value).ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParseBackupValidUntil(string value, out DateTimeOffset parsed)
    {
        if (DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var candidate) &&
            candidate.Offset == TimeSpan.Zero &&
            string.Equals(value, FormatBackupValidUntil(candidate), StringComparison.Ordinal))
        {
            parsed = candidate;
            return true;
        }
        parsed = default;
        return false;
    }

    private static LoginPurpose ParsePurpose(string purpose) => purpose switch
    {
        "migrator" => LoginPurpose.Migrator,
        "api" => LoginPurpose.Api,
        "ingestion" => LoginPurpose.Ingestion,
        "calendar_importer" => LoginPurpose.CalendarImporter,
        "exporter" => LoginPurpose.Exporter,
        "audit" => LoginPurpose.Audit,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static DateTimeOffset NormalizeBackupValidUntil(DateTimeOffset value) =>
        new(value.ToUniversalTime().Ticks - value.ToUniversalTime().Ticks % TimeSpan.TicksPerSecond,
            TimeSpan.Zero);

    private static bool CryptographicEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static DatabaseSecurityRejectedException Rejected(
        string code,
        DatabaseSecurityFailureKind kind) => new(code, kind);
}
