using System.Globalization;
using System.Text.RegularExpressions;
using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseRoleBootstrap;

internal enum BootstrapCommand
{
    Ensure,
    Verify,
    Rotate,
    ResetPassword,
    Retire,
}

internal sealed record BootstrapTimeouts(
    TimeSpan Connect,
    TimeSpan Lock,
    TimeSpan Statement,
    TimeSpan Total);

internal sealed record BootstrapOptions(
    BootstrapCommand Command,
    string AdminConnectionFile,
    RoleContract Contract,
    string TimescaleVersion,
    string UuidOsspVersion,
    BootstrapTimeouts Timeouts,
    IReadOnlyDictionary<LoginPurpose, string> PasswordFiles,
    string? BackupPasswordFile,
    DateTimeOffset BackupV1ValidUntilUtc,
    LoginPurpose? RotatePurpose,
    bool RotateBackup,
    int? RotateVersion,
    string? RotatePasswordFile,
    DateTimeOffset? RotateBackupValidUntilUtc,
    int? ReplacementVersion = null,
    TimeSpan? DrainTimeout = null)
{
    private static readonly Regex ExtensionVersionPattern =
        new("^[0-9]+(?:\\.[0-9]+){1,3}$", RegexOptions.CultureInvariant);

    public string ContractSha256 => Contract.ContractSha256(TimescaleVersion, UuidOsspVersion);

    public static BootstrapOptions Parse(string[] args)
    {
        if (args.Length == 0)
            throw Invalid("command_missing");
        var command = args[0] switch
        {
            "ensure" => BootstrapCommand.Ensure,
            "verify" => BootstrapCommand.Verify,
            "rotate" => BootstrapCommand.Rotate,
            "reset-password" => BootstrapCommand.ResetPassword,
            "retire" => BootstrapCommand.Retire,
            _ => throw Invalid("command_unknown"),
        };

        var values = ParsePairs(args[1..]);
        var adminFile = Required(values, "--admin-connection-file");
        var deployment = Required(values, "--deployment-id");
        var database = Required(values, "--target-database");
        var systemHash = Required(values, "--system-identifier-sha256");
        var prefix = Required(values, "--role-prefix");
        var timescaleVersion = Required(values, "--timescaledb-version");
        var uuidVersion = Required(values, "--uuid-ossp-version");
        var backupV1ValidUntil = ParseTimestamp(
            Required(values, "--backup-v1-valid-until"), "backup_valid_until_invalid");
        if (!ExtensionVersionPattern.IsMatch(timescaleVersion) ||
            !ExtensionVersionPattern.IsMatch(uuidVersion))
            throw Invalid("extension_version_invalid");

        var timeouts = new BootstrapTimeouts(
            ParseDuration(values, "--connect-timeout-seconds", 10, 1, 30),
            ParseDuration(values, "--lock-timeout-seconds", 30, 1, 120),
            ParseDuration(values, "--statement-timeout-seconds", 30, 1, 120),
            ParseDuration(values, "--total-timeout-seconds", 120, 5, 300));
        if (timeouts.Total <= timeouts.Connect || timeouts.Total <= timeouts.Lock)
            throw Invalid("timeout_contract_invalid");

        var passwordFiles = new Dictionary<LoginPurpose, string>();
        string? backupPasswordFile = null;
        LoginPurpose? rotatePurpose = null;
        var rotateBackup = false;
        int? rotateVersion = null;
        string? rotatePasswordFile = null;
        DateTimeOffset? rotateBackupValidUntil = null;
        int? replacementVersion = null;
        TimeSpan? drainTimeout = null;

        if (command == BootstrapCommand.Ensure)
        {
            foreach (var purpose in Enum.GetValues<LoginPurpose>())
                passwordFiles.Add(purpose, Required(values,
                    $"--{RoleContract.PurposeName(purpose).Replace('_', '-')}-password-file"));
            backupPasswordFile = Required(values, "--backup-password-file");
        }
        else if (command is BootstrapCommand.Rotate or BootstrapCommand.ResetPassword or
                 BootstrapCommand.Retire)
        {
            var login = Required(values, "--login");
            rotateBackup = string.Equals(login, "backup", StringComparison.Ordinal);
            if (!rotateBackup)
                rotatePurpose = ParsePurpose(login);
            if (!int.TryParse(Required(values, "--login-version"), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var version))
                throw Invalid("login_version_invalid");
            if (rotateBackup && (command != BootstrapCommand.Rotate || version != 2))
                throw Invalid("backup_rotate_version_must_be_v2");
            if (!rotateBackup && !RoleContract.IsAllowedLoginVersion(version))
                throw Invalid("login_version_invalid");
            rotateVersion = version;
            if (command is BootstrapCommand.Rotate or BootstrapCommand.ResetPassword)
                rotatePasswordFile = Required(values, "--password-file");
            if (rotateBackup)
                rotateBackupValidUntil = ParseTimestamp(
                    Required(values, "--valid-until"), "backup_valid_until_invalid");
            if (command == BootstrapCommand.Retire)
            {
                if (!int.TryParse(Required(values, "--replacement-version"), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var replacement) ||
                    !RoleContract.IsAllowedLoginVersion(replacement))
                    throw Invalid("replacement_version_invalid");
                replacementVersion = replacement;
                drainTimeout = ParseDuration(
                    values, "--drain-timeout-seconds", 30, 1, 120);
            }
        }

        var allowed = CommonKeys().ToHashSet(StringComparer.Ordinal);
        if (command == BootstrapCommand.Ensure)
        {
            foreach (var purpose in Enum.GetValues<LoginPurpose>())
                allowed.Add($"--{RoleContract.PurposeName(purpose).Replace('_', '-')}-password-file");
            allowed.Add("--backup-password-file");
        }
        if (command is BootstrapCommand.Rotate or BootstrapCommand.ResetPassword or
            BootstrapCommand.Retire)
        {
            allowed.Add("--login");
            allowed.Add("--login-version");
            if (command is BootstrapCommand.Rotate or BootstrapCommand.ResetPassword)
                allowed.Add("--password-file");
            if (rotateBackup)
                allowed.Add("--valid-until");
            if (command == BootstrapCommand.Retire)
            {
                allowed.Add("--replacement-version");
                allowed.Add("--drain-timeout-seconds");
            }
        }
        if (values.Keys.Any(key => !allowed.Contains(key)))
            throw Invalid("argument_unsupported");

        return new BootstrapOptions(
            command, adminFile, RoleContract.Create(deployment, database, systemHash, prefix),
            timescaleVersion, uuidVersion, timeouts, passwordFiles,
            backupPasswordFile, backupV1ValidUntil, rotatePurpose, rotateBackup,
            rotateVersion, rotatePasswordFile, rotateBackupValidUntil,
            replacementVersion, drainTimeout);
    }

    private static Dictionary<string, string> ParsePairs(string[] args)
    {
        if (args.Length == 0 || args.Length % 2 != 0)
            throw Invalid("argument_pair_invalid");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(args[index + 1]) ||
                !values.TryAdd(args[index], args[index + 1]))
                throw Invalid("argument_invalid");
        }
        return values;
    }

    private static IEnumerable<string> CommonKeys()
    {
        yield return "--admin-connection-file";
        yield return "--deployment-id";
        yield return "--target-database";
        yield return "--system-identifier-sha256";
        yield return "--role-prefix";
        yield return "--timescaledb-version";
        yield return "--uuid-ossp-version";
        yield return "--backup-v1-valid-until";
        yield return "--connect-timeout-seconds";
        yield return "--lock-timeout-seconds";
        yield return "--statement-timeout-seconds";
        yield return "--total-timeout-seconds";
    }

    private static DateTimeOffset ParseTimestamp(string value, string code)
    {
        if (!RoleContract.TryParseBackupValidUntil(value, out var parsed))
            throw Invalid(code);
        return parsed;
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : throw Invalid("argument_required");

    private static TimeSpan ParseDuration(
        IReadOnlyDictionary<string, string> values,
        string key,
        int defaultSeconds,
        int minimum,
        int maximum)
    {
        if (!values.TryGetValue(key, out var value)) return TimeSpan.FromSeconds(defaultSeconds);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds < minimum || seconds > maximum)
            throw Invalid("timeout_invalid");
        return TimeSpan.FromSeconds(seconds);
    }

    private static LoginPurpose ParsePurpose(string value) => value switch
    {
        "migrator" => LoginPurpose.Migrator,
        "api" => LoginPurpose.Api,
        "ingestion" => LoginPurpose.Ingestion,
        "calendar-importer" => LoginPurpose.CalendarImporter,
        "exporter" => LoginPurpose.Exporter,
        "audit" => LoginPurpose.Audit,
        _ => throw Invalid("login_purpose_invalid"),
    };

    private static BootstrapRejectedException Invalid(string code) =>
        new(code, BootstrapExitCodes.InvalidArguments);
}
