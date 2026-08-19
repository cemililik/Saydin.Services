using Npgsql;
using Saydin.CalendarData;
using Saydin.DatabaseSecurity;

try
{
    if (args is ["acquire", "--base-data-root", var baseRoot, "--plan", var planPath,
        "--staging-root", var stagingRoot, "--output-name", var outputName])
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false,
        };
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var acquisition = new CalendarAcquisition(httpClient, TimeProvider.System);
        var output = await acquisition.RunAsync(new CalendarAcquisitionOptions(
            baseRoot, planPath, stagingRoot, outputName, TimeSpan.FromSeconds(30)), CancellationToken.None);
        Console.WriteLine($"calendar acquisition quarantined: {output}");
        return;
    }

    if (args is [var command, "--data-root", var dataRoot]
        && command is "generate" or "verify")
    {
        IReadOnlyList<NormalizedCalendar> generated;
        if (command == "verify")
        {
            var bundle = CalendarDataGenerator.LoadVerified(dataRoot);
            bundle.EnsureInputsUnchanged();
            generated = bundle.Calendars;
        }
        else generated = CalendarDataGenerator.Generate(dataRoot);
        if (command == "generate") CalendarDataGenerator.Write(dataRoot, generated);
        foreach (var calendar in generated)
            Console.WriteLine($"{calendar.CalendarCode}: rows={calendar.RowCount}, normalized_sha256={calendar.NormalizedSha256}, source_bundle_sha256={calendar.SourceBundleSha256}");
        return;
    }

    var options = CalendarReleaseCommand.Parse(args, Environment.GetEnvironmentVariable);
    await using var dataSource = await RuntimeDatabase.OpenVerifiedDataSourceAsync(options.Database);
    var result = options.Command switch
    {
        CalendarReleaseCommandName.Import => await CalendarReleaseImporter.ImportAsync(options, dataSource),
        CalendarReleaseCommandName.Activate => await CalendarReleaseImporter.ActivateAsync(options, dataSource),
        _ => throw new CalendarDataException("arguments_invalid"),
    };
    Console.WriteLine(result);
}
catch (CalendarDataException ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 2;
}
catch (DatabaseSecurityRejectedException ex)
{
    Console.Error.WriteLine($"database_security_rejected:{ex.Code}");
    Environment.ExitCode = 78;
}
catch (NpgsqlException ex)
{
    Console.Error.WriteLine($"database_operation_failed:{ex.SqlState ?? "unknown"}");
    Environment.ExitCode = 3;
}
