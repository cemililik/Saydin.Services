using System.Globalization;

namespace Saydin.CalendarData;

internal static class CalendarPlanMaterializer
{
    private static readonly string[] MonthCodes =
        ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    public static void MaterializeTcmb(
        string baseDataRoot,
        string outputPath,
        DateTimeOffset utcNow)
    {
        var baseBundle = CalendarDataGenerator.LoadVerified(baseDataRoot);
        var cutoff = TcmbProviderCutoff(utcNow);
        var calendars = baseBundle.Manifest.Calendars.Select(calendar =>
            calendar.Code == CalendarDataGenerator.TcmbCode
                ? new CalendarDefinition
                {
                    Code = calendar.Code,
                    CoverageFrom = calendar.CoverageFrom,
                    CoverageThrough = cutoff.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    OutputPath = calendar.OutputPath,
                }
                : calendar).ToArray();
        var annualId = $"tcmb-annual-{cutoff.Year}";
        var monthlyId = $"tcmb-month-{cutoff:yyyyMM}";
        var prior = baseBundle.Manifest.Sources.ToDictionary(source => source.Id, StringComparer.Ordinal);
        var sources = new[]
        {
            Request(prior, annualId, "tcmbAnnualIndex", "discovery",
                $"https://www.tcmb.gov.tr/kurlar/kur{cutoff.Year}_tr.html", cutoff.Year, null),
            Request(prior, monthlyId, "tcmbMonthlyArchive", "authority",
                $"https://www.tcmb.gov.tr/kurlar/{cutoff:yyyyMM}/{MonthCodes[cutoff.Month - 1]}_tr.html",
                cutoff.Year, cutoff.Month),
        };
        var plan = new CalendarAcquisitionPlan
        {
            SchemaVersion = 1,
            SnapshotSetId = $"cal-tcmb-{cutoff:yyyy-MM-dd}",
            Calendars = calendars,
            Sources = sources,
        };
        // The schedule writes to a stable path. Same-cutoff reruns are no-ops;
        // a later cutoff atomically advances the plan instead of conflicting
        // with yesterday's materialized input.
        SecureBundleStorage.WritePrivateFileAtomicallyReplacing(
            outputPath, ManifestJson.Write(plan));
    }

    internal static DateOnly TcmbProviderCutoff(DateTimeOffset utcNow)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
        var local = TimeZoneInfo.ConvertTime(utcNow, zone);
        var today = DateOnly.FromDateTime(local.DateTime);
        return TimeOnly.FromDateTime(local.DateTime) >= new TimeOnly(16, 30)
            ? today
            : today.AddDays(-1);
    }

    private static CalendarAcquisitionSource Request(
        IReadOnlyDictionary<string, SourceDefinition> prior,
        string id,
        string kind,
        string role,
        string uri,
        int year,
        int? month)
    {
        if (prior.TryGetValue(id, out var source))
            return new CalendarAcquisitionSource
            {
                Id = source.Id,
                CalendarCode = source.CalendarCode,
                Kind = source.Kind,
                Role = source.Role,
                Uri = source.Uri,
                MediaType = source.MediaType,
                Year = source.Year,
                Month = source.Month,
            };
        return new CalendarAcquisitionSource
        {
            Id = id,
            CalendarCode = CalendarDataGenerator.TcmbCode,
            Kind = kind,
            Role = role,
            Uri = uri,
            MediaType = "text/html",
            Year = year,
            Month = month,
        };
    }
}
