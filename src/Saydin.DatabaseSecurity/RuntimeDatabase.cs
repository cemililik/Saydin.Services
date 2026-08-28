using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;

namespace Saydin.DatabaseSecurity;

public enum RuntimeDatabasePooling
{
    Disabled,
    Service,
}

public sealed record RuntimeDatabaseOptions(
    LoginPurpose Purpose,
    RoleContract Contract,
    ManagedRole Login,
    string Host,
    int Port,
    string Database,
    SslMode SslMode,
    string PasswordFile,
    RuntimeDatabasePooling Pooling)
{
    private const int CurrentLoginVersion = 1;
    private static readonly Regex HostPattern =
        new("^[a-zA-Z0-9.:[\\]-]{1,253}$", RegexOptions.CultureInvariant, RegexTimeouts.Default);
    private static readonly string[] RawSecretEnvironmentNames =
    [
        "DATABASE_URL",
        "ConnectionStrings__Postgres",
        "PGPASSWORD",
        "PGPASSFILE",
        "POSTGRES_PASSWORD",
        "POSTGRES_EXPORTER_PASSWORD",
        "DATA_SOURCE_NAME",
        "SAYDIN_API_DATABASE_URL",
        "SAYDIN_INGESTION_DATABASE_URL",
        "SAYDIN_CALENDAR_DATABASE_URL",
        "SAYDIN_CALENDAR_IMPORTER_DATABASE_URL",
        "SAYDIN_EXPORTER_DATABASE_URL",
        "SAYDIN_AUDIT_DATABASE_URL",
        "SAYDIN_DATA_QUALITY_AUDIT_DATABASE_URL",
        "SAYDIN_MIGRATOR_DATABASE_URL",
    ];

    public static RuntimeDatabaseOptions FromEnvironment(
        LoginPurpose purpose,
        RuntimeDatabasePooling pooling,
        Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        if (RawSecretEnvironmentNames.Any(name => environment(name) is not null))
            throw Rejected("runtime_database_raw_secret_environment_rejected");

        var host = Required(environment, "PGHOST", 253);
        if (!HostPattern.IsMatch(host))
            throw Rejected("runtime_database_environment_invalid");
        var database = Required(environment, "PGDATABASE", 63);
        var username = Required(environment, "PGUSER", 63);
        var deployment = Required(environment, "SAYDIN_DEPLOYMENT_ID", 12);
        var systemHash = Required(environment, "SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256", 64);
        var prefix = Required(environment, "SAYDIN_DATABASE_ROLE_PREFIX", 63);
        var passwordFile = Required(environment, PasswordFileEnvironment(purpose), 1024);
        var port = ParsePort(environment("PGPORT") ?? "5432");
        var sslMode = ParseSslMode(environment("PGSSLMODE") ?? "require");
        var version = ParseLoginVersion(environment("SAYDIN_DATABASE_LOGIN_VERSION") ?? "1");
        var contract = RoleContract.Create(deployment, database, systemHash, prefix);
        var login = contract.Login(purpose, version);
        if (!CryptographicEquals(username, login.Name))
            throw Rejected("runtime_database_login_contract_mismatch");

        return new RuntimeDatabaseOptions(
            purpose, contract, login, host, port, database, sslMode, passwordFile, pooling);
    }

    public static string PasswordFileEnvironment(LoginPurpose purpose) => purpose switch
    {
        LoginPurpose.Api => "SAYDIN_API_DATABASE_PASSWORD_FILE",
        LoginPurpose.Ingestion => "SAYDIN_INGESTION_DATABASE_PASSWORD_FILE",
        LoginPurpose.CalendarImporter => "SAYDIN_CALENDAR_IMPORTER_DATABASE_PASSWORD_FILE",
        LoginPurpose.Exporter => "SAYDIN_EXPORTER_DATABASE_PASSWORD_FILE",
        LoginPurpose.Audit => "SAYDIN_AUDIT_DATABASE_PASSWORD_FILE",
        LoginPurpose.Migrator => "SAYDIN_MIGRATOR_DATABASE_PASSWORD_FILE",
        _ => throw Rejected("runtime_database_purpose_invalid"),
    };

    private static string Required(Func<string, string?> environment, string name, int maximumLength)
    {
        var value = environment(name);
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw Rejected("runtime_database_environment_invalid");
        return value;
    }

    private static int ParsePort(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
        port is >= 1 and <= 65535
            ? port
            : throw Rejected("runtime_database_environment_invalid");

    private static int ParseLoginVersion(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) &&
        version is >= CurrentLoginVersion and <= 2
            ? version
            : throw Rejected("runtime_database_login_version_invalid");

    private static SslMode ParseSslMode(string value) => value.ToLowerInvariant() switch
    {
        "disable" => SslMode.Disable,
        "require" => SslMode.Require,
        "verify-ca" or "verifyca" => SslMode.VerifyCA,
        "verify-full" or "verifyfull" => SslMode.VerifyFull,
        _ => throw Rejected("runtime_database_ssl_mode_invalid"),
    };

    private static bool CryptographicEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static DatabaseSecurityRejectedException Rejected(string code) =>
        new(code, DatabaseSecurityFailureKind.InvalidArguments);
}

public static class RuntimeDatabase
{
    public static async Task<NpgsqlDataSource> OpenVerifiedDataSourceAsync(
        RuntimeDatabaseOptions options,
        Action<NpgsqlDataSourceBuilder>? configure = null,
        CancellationToken cancellationToken = default)
    {
        NpgsqlDataSource? dataSource = null;
        try
        {
            var password = SecureSecretFile.ReadPassword(options.PasswordFile);
            var connection = new NpgsqlConnectionStringBuilder
            {
                Host = options.Host,
                Port = options.Port,
                Database = options.Database,
                Username = options.Login.Name,
                Password = password,
                SslMode = options.SslMode,
                CheckCertificateRevocation = options.SslMode != SslMode.Disable,
                Pooling = options.Pooling == RuntimeDatabasePooling.Service,
                MinPoolSize = 0,
                MaxPoolSize = options.Pooling == RuntimeDatabasePooling.Service ? 50 : 1,
                ConnectionIdleLifetime = 300,
                ConnectionPruningInterval = 10,
                Timeout = 10,
                CommandTimeout = 30,
                CancellationTimeout = 2_000,
                KeepAlive = 30,
                IncludeErrorDetail = false,
                LogParameters = false,
                PersistSecurityInfo = false,
                Enlist = false,
                NoResetOnClose = false,
                SearchPath = "pg_catalog,public,pg_temp",
                ApplicationName = $"saydin-{RoleContract.PurposeName(options.Purpose)}",
            };
            connection.Passfile = null;
            connection.Options = null;

            var builder = new NpgsqlDataSourceBuilder(connection.ConnectionString);
            builder.EnableParameterLogging(false);
            configure?.Invoke(builder);
            dataSource = builder.Build();
            await VerifyIdentityAsync(dataSource, options, cancellationToken);
            return dataSource;
        }
        catch (DatabaseSecurityRejectedException)
        {
            if (dataSource is not null) await dataSource.DisposeAsync();
            throw;
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            if (dataSource is not null) await dataSource.DisposeAsync();
            throw new DatabaseSecurityRejectedException(
                "runtime_database_connection_rejected", DatabaseSecurityFailureKind.TargetRejected);
        }
    }

    private static async Task VerifyIdentityAsync(
        NpgsqlDataSource dataSource,
        RuntimeDatabaseOptions options,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT current_database(), session_user::text, current_user::text,
                   login.rolsuper, login.rolinherit, login.rolcreaterole,
                   login.rolcreatedb, login.rolcanlogin, login.rolreplication,
                   login.rolbypassrls, login.rolconnlimit,
                   login.rolvaliduntil IS NULL, login.rolconfig IS NULL,
                   shobj_description(login.oid, 'pg_authid')
              FROM pg_catalog.pg_roles AS login
             WHERE login.rolname = session_user;

            SELECT granted.rolname, membership.admin_option,
                   membership.inherit_option, membership.set_option,
                   membership.grantor
              FROM pg_catalog.pg_auth_members AS membership
              JOIN pg_catalog.pg_roles AS granted ON granted.oid = membership.roleid
              JOIN pg_catalog.pg_roles AS member_role ON member_role.oid = membership.member
             WHERE member_role.rolname = session_user
             ORDER BY granted.rolname;

            SELECT capability.rolsuper, capability.rolinherit, capability.rolcreaterole,
                   capability.rolcreatedb, capability.rolcanlogin, capability.rolreplication,
                   capability.rolbypassrls, capability.rolconnlimit,
                   capability.rolvaliduntil IS NULL, capability.rolconfig IS NULL,
                   shobj_description(capability.oid, 'pg_authid'),
                   pg_has_role(session_user, @capability, 'SET')
             FROM pg_catalog.pg_roles AS capability
             WHERE capability.rolname = @capability;

            SELECT granted.rolname, membership.admin_option,
                   membership.inherit_option, membership.set_option,
                   membership.grantor
              FROM pg_catalog.pg_auth_members AS membership
              JOIN pg_catalog.pg_roles AS granted ON granted.oid = membership.roleid
              JOIN pg_catalog.pg_roles AS member_role ON member_role.oid = membership.member
             WHERE member_role.rolname = @capability
             ORDER BY granted.rolname;

            SELECT candidate.role_name,
                   pg_has_role(session_user, managed.oid, 'USAGE'),
                   pg_has_role(session_user, managed.oid, 'SET')
              FROM unnest(@managed_roles::text[]) AS candidate(role_name)
              JOIN pg_catalog.pg_roles AS managed ON managed.rolname=candidate.role_name
             ORDER BY candidate.role_name COLLATE "C";
            """;
        var capability = options.Contract.Capability(options.Purpose);
        command.Parameters.AddWithValue("capability", capability.Name);
        var managedRoles = options.Contract.AllRolesForVersion(1)
            .Concat(options.Contract.AllRolesForVersion(2))
            .Select(role => role.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        command.Parameters.AddWithValue("managed_roles", managedRoles);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            !string.Equals(reader.GetString(0), options.Database, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), options.Login.Name, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(2), options.Login.Name, StringComparison.Ordinal) ||
            reader.GetBoolean(3) || reader.GetBoolean(4) || reader.GetBoolean(5) ||
            reader.GetBoolean(6) || !reader.GetBoolean(7) || reader.GetBoolean(8) ||
            reader.GetBoolean(9) || reader.GetInt32(10) != -1 || !reader.GetBoolean(11) ||
            !reader.GetBoolean(12) || reader.IsDBNull(13) ||
            !options.Contract.IsExactMarker(options.Login, reader.GetString(13)) ||
            await reader.ReadAsync(cancellationToken))
            throw TargetRejected();

        if (!await reader.NextResultAsync(cancellationToken)) throw TargetRejected();
        if (!await reader.ReadAsync(cancellationToken) ||
            !string.Equals(reader.GetString(0), capability.Name, StringComparison.Ordinal) ||
            reader.GetBoolean(1) || !reader.GetBoolean(2) || reader.GetBoolean(3) ||
            reader.GetFieldValue<uint>(4) != 10u ||
            await reader.ReadAsync(cancellationToken))
            throw TargetRejected();

        if (!await reader.NextResultAsync(cancellationToken) ||
            !await reader.ReadAsync(cancellationToken) ||
            reader.GetBoolean(0) || reader.GetBoolean(1) || reader.GetBoolean(2) ||
            reader.GetBoolean(3) || reader.GetBoolean(4) || reader.GetBoolean(5) ||
            reader.GetBoolean(6) || reader.GetInt32(7) != -1 || !reader.GetBoolean(8) ||
            !reader.GetBoolean(9) || reader.IsDBNull(10) ||
            !options.Contract.IsExactMarker(capability, reader.GetString(10)) ||
            reader.GetBoolean(11) || await reader.ReadAsync(cancellationToken))
            throw TargetRejected();

        if (!await reader.NextResultAsync(cancellationToken)) throw TargetRejected();
        if (options.Purpose == LoginPurpose.Exporter)
        {
            if (!await reader.ReadAsync(cancellationToken) ||
                !string.Equals(reader.GetString(0), "pg_monitor", StringComparison.Ordinal) ||
                reader.GetBoolean(1) || !reader.GetBoolean(2) || reader.GetBoolean(3) ||
                reader.GetFieldValue<uint>(4) != 10u ||
                await reader.ReadAsync(cancellationToken))
                throw TargetRejected();
        }
        else if (await reader.ReadAsync(cancellationToken))
        {
            throw TargetRejected();
        }

        if (!await reader.NextResultAsync(cancellationToken)) throw TargetRejected();
        var observed = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var role = reader.GetString(0);
            if (!observed.Add(role)) throw TargetRejected();
            if (string.Equals(role, options.Login.Name, StringComparison.Ordinal)) continue;
            var expectedUsage = string.Equals(role, capability.Name, StringComparison.Ordinal);
            if (reader.GetBoolean(1) != expectedUsage || reader.GetBoolean(2)) throw TargetRejected();
        }
        if (!observed.Contains(options.Login.Name) || !observed.Contains(capability.Name))
            throw TargetRejected();
    }

    private static DatabaseSecurityRejectedException TargetRejected() =>
        new("runtime_database_identity_rejected", DatabaseSecurityFailureKind.TargetRejected);
}
