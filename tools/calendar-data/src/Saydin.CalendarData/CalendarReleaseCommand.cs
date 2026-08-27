using Saydin.DatabaseSecurity;

namespace Saydin.CalendarData;

public enum CalendarReleaseCommandName { Import, Activate }

public sealed record CalendarReleaseCommand(
    CalendarReleaseCommandName Command,
    string DataRoot,
    string CalendarCode,
    Guid ReleaseId,
    int ReleaseVersion,
    Guid ExpectedCurrentReleaseId,
    RuntimeDatabaseOptions Database)
{
    public override string ToString() =>
        $"CalendarReleaseCommand {{ Command = {Command}, DataRoot = {DataRoot}, " +
        $"CalendarCode = {CalendarCode}, ReleaseId = {ReleaseId:D}, " +
        $"ReleaseVersion = {ReleaseVersion}, ExpectedCurrentReleaseId = {ExpectedCurrentReleaseId:D}, " +
        "Database = [REDACTED] }";

    public static CalendarReleaseCommand Parse(
        IReadOnlyList<string> args,
        Func<string, string?> environment)
    {
        if (args.Count == 0)
            throw new CalendarDataException("arguments_invalid");
        var command = args[0] switch
        {
            "import" => CalendarReleaseCommandName.Import,
            "activate" => CalendarReleaseCommandName.Activate,
            _ => throw new CalendarDataException("arguments_invalid"),
        };
        if ((args.Count - 1) % 2 != 0)
            throw new CalendarDataException("arguments_invalid");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index += 2)
            if (!values.TryAdd(args[index], args[index + 1]))
                throw new CalendarDataException("argument_duplicate", args[index]);

        var allowed = command == CalendarReleaseCommandName.Import
            ? new[] { "--data-root", "--calendar", "--release-id", "--release-version", "--expected-current-release" }
            : new[] { "--calendar", "--release-id", "--expected-current-release" };
        if (values.Keys.Except(allowed, StringComparer.Ordinal).Any()
            || allowed.Any(key => !values.ContainsKey(key)))
            throw new CalendarDataException("arguments_invalid");
        var calendar = values["--calendar"];
        if (calendar is not (CalendarDataGenerator.TcmbCode or CalendarDataGenerator.BistCode))
            throw new CalendarDataException("calendar_code_unsupported", calendar);
        if (!Guid.TryParseExact(values["--release-id"], "D", out var releaseId)
            || releaseId == Guid.Empty)
            throw new CalendarDataException("release_id_invalid");
        if (!Guid.TryParseExact(values["--expected-current-release"], "D", out var expected)
            || expected == Guid.Empty)
            throw new CalendarDataException("expected_current_release_invalid");
        var version = command == CalendarReleaseCommandName.Import
            && int.TryParse(values["--release-version"], out var parsed) && parsed > 0
                ? parsed : command == CalendarReleaseCommandName.Activate ? 0
                : throw new CalendarDataException("release_version_invalid");
        var database = RuntimeDatabaseOptions.FromEnvironment(
            LoginPurpose.CalendarImporter, RuntimeDatabasePooling.Disabled, environment);
        var root = command == CalendarReleaseCommandName.Import ? values["--data-root"] : string.Empty;
        return new(command, root, calendar, releaseId, version, expected, database);
    }
}
