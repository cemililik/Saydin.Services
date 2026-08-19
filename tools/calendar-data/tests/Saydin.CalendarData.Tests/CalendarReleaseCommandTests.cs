using Saydin.CalendarData;
using Saydin.DatabaseSecurity;

namespace Saydin.CalendarData.Tests;

public sealed class CalendarReleaseCommandTests
{
    [Fact]
    public void Import_RequiresExplicitAllowlistedCalendarAndCasPointer()
    {
        var command = CalendarReleaseCommand.Parse(
            ["import", "--data-root", "/bundle", "--calendar", "tcmb_indicative_fx",
             "--release-id", "ca100000-0000-7000-8000-000000000010",
             "--release-version", "10", "--expected-current-release",
             "ca100000-0000-7000-8000-000000000001"],
            Environment());

        Assert.Equal(CalendarReleaseCommandName.Import, command.Command);
        Assert.Equal(10, command.ReleaseVersion);
        Assert.Equal("tcmb_indicative_fx", command.CalendarCode);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("tcmb")]
    [InlineData("bist")]
    public void Import_RejectsNonAllowlistedCalendar(string calendar)
    {
        var act = () => CalendarReleaseCommand.Parse(
            ["import", "--data-root", "/bundle", "--calendar", calendar,
             "--release-id", "ca100000-0000-7000-8000-000000000010",
             "--release-version", "10", "--expected-current-release",
             "ca100000-0000-7000-8000-000000000001"],
            Environment());

        var exception = Assert.Throws<CalendarDataException>(act);
        Assert.StartsWith("calendar_code_unsupported", exception.Message);
    }

    [Fact]
    public void Activate_RequiresDatabaseTopologyWithoutEchoingSecrets()
    {
        var act = () => CalendarReleaseCommand.Parse(
            ["activate", "--calendar", "bist_pay_xist",
             "--release-id", "ca100000-0000-7000-8000-000000000002",
             "--expected-current-release", "ca100000-0000-7000-8000-000000000010"],
            _ => null);

        var exception = Assert.Throws<DatabaseSecurityRejectedException>(act);
        Assert.Equal("runtime_database_environment_invalid", exception.Code);
    }

    [Fact]
    public void CommandString_RedactsDatabaseSecret()
    {
        const string sentinel = "CALENDAR_PASSWORD_SENTINEL_7f3a";
        var command = CalendarReleaseCommand.Parse(
            ["activate", "--calendar", "bist_pay_xist",
             "--release-id", "ca100000-0000-7000-8000-000000000002",
             "--expected-current-release", "ca100000-0000-7000-8000-000000000010"],
            Environment($"/run/secrets/{sentinel}"));

        var rendered = command.ToString();

        Assert.Contains("Database = [REDACTED]", rendered);
        Assert.DoesNotContain(sentinel, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyRawDatabaseUrl_IsRejected()
    {
        var environment = Environment();
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in EnvironmentKeys)
            values[key] = environment(key);
        values["SAYDIN_CALENDAR_DATABASE_URL"] = "Password=must-not-appear";

        var exception = Assert.Throws<DatabaseSecurityRejectedException>(() =>
            CalendarReleaseCommand.Parse(
                ["activate", "--calendar", "bist_pay_xist",
                 "--release-id", "ca100000-0000-7000-8000-000000000002",
                 "--expected-current-release", "ca100000-0000-7000-8000-000000000010"],
                key => values.GetValueOrDefault(key)));

        Assert.Equal("runtime_database_raw_secret_environment_rejected", exception.Code);
        Assert.DoesNotContain("must-not-appear", exception.Message, StringComparison.Ordinal);
    }

    private static readonly string[] EnvironmentKeys =
    [
        "PGHOST", "PGPORT", "PGDATABASE", "PGUSER", "PGSSLMODE",
        "SAYDIN_DEPLOYMENT_ID", "SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256",
        "SAYDIN_DATABASE_ROLE_PREFIX", "SAYDIN_DATABASE_LOGIN_VERSION",
        "SAYDIN_CALENDAR_IMPORTER_DATABASE_PASSWORD_FILE",
    ];

    private static Func<string, string?> Environment(string passwordFile = "/run/secrets/calendar")
    {
        const string systemHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var prefix = RoleContract.DerivePrefix("test-a", "test", systemHash);
        var login = RoleContract.Create("test-a", "test", systemHash, prefix)
            .Login(LoginPurpose.CalendarImporter, 1).Name;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PGHOST"] = "db",
            ["PGPORT"] = "5432",
            ["PGDATABASE"] = "test",
            ["PGUSER"] = login,
            ["PGSSLMODE"] = "disable",
            ["SAYDIN_DEPLOYMENT_ID"] = "test-a",
            ["SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256"] = systemHash,
            ["SAYDIN_DATABASE_ROLE_PREFIX"] = prefix,
            ["SAYDIN_DATABASE_LOGIN_VERSION"] = "1",
            ["SAYDIN_CALENDAR_IMPORTER_DATABASE_PASSWORD_FILE"] = passwordFile,
        };
        return key => values.GetValueOrDefault(key);
    }
}
