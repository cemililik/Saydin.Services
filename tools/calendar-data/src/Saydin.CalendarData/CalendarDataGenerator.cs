using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Saydin.CalendarData;

public sealed record NormalizedCalendar(
    string CalendarCode,
    string OutputPath,
    byte[] Content,
    int RowCount,
    string NormalizedSha256,
    string SourceBundleSha256);

public sealed class VerifiedCalendarBundle(
    string dataRoot,
    byte[] manifestBytes,
    byte[] expectedBytes,
    SourceManifest manifest,
    IReadOnlyList<NormalizedCalendar> calendars)
{
    public SourceManifest Manifest { get; } = manifest;
    public IReadOnlyList<NormalizedCalendar> Calendars { get; } = calendars;

    public void EnsureInputsUnchanged()
    {
        var manifestPath = Path.Combine(dataRoot, "source-manifest.json");
        SecureBundleStorage.EnsureRegularFileNoFollow(dataRoot, manifestPath, "verified_input_unsafe");
        var current = File.ReadAllBytes(manifestPath);
        if (!current.AsSpan().SequenceEqual(manifestBytes))
            throw new CalendarDataException("verified_input_changed", "source-manifest.json");
        var expectedPath = Path.Combine(dataRoot, "expected-output.json");
        SecureBundleStorage.EnsureRegularFileNoFollow(dataRoot, expectedPath, "verified_input_unsafe");
        current = File.ReadAllBytes(expectedPath);
        if (!current.AsSpan().SequenceEqual(expectedBytes))
            throw new CalendarDataException("verified_input_changed", "expected-output.json");
    }
}

public static class CalendarDataGenerator
{
    public const string TcmbCode = "tcmb_indicative_fx";
    public const string BistCode = "bist_pay_xist";

    public static IReadOnlyList<NormalizedCalendar> Generate(string dataRoot)
    {
        var manifest = ManifestJson.Read<SourceManifest>(Path.Combine(dataRoot, "source-manifest.json"));
        return Generate(dataRoot, manifest);
    }

    public static VerifiedCalendarBundle LoadVerified(string dataRoot)
    {
        var manifestPath = Path.Combine(dataRoot, "source-manifest.json");
        SecureBundleStorage.EnsureRegularFileNoFollow(dataRoot, manifestPath, "verified_input_unsafe");
        var expectedPath = Path.Combine(dataRoot, "expected-output.json");
        SecureBundleStorage.EnsureRegularFileNoFollow(dataRoot, expectedPath, "verified_input_unsafe");
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var expectedBytes = File.ReadAllBytes(expectedPath);
        var manifest = ManifestJson.Read<SourceManifest>(manifestBytes, manifestPath);
        var expected = ManifestJson.Read<ExpectedOutputSet>(expectedBytes, "expected-output.json");
        var generated = Generate(dataRoot, manifest);
        Verify(dataRoot, generated, manifest, expected);
        return new(dataRoot, manifestBytes, expectedBytes, manifest, generated);
    }

    private static IReadOnlyList<NormalizedCalendar> Generate(
        string dataRoot, SourceManifest manifest)
    {
        var store = new SourceSnapshotStore(dataRoot, manifest);
        ValidateManifestShape(manifest);
        foreach (var source in manifest.Sources)
            store.Read(source);
        return manifest.Calendars.OrderBy(item => item.Code, StringComparer.Ordinal).Select(calendar =>
            calendar.Code switch
            {
                TcmbCode => GenerateTcmb(calendar, manifest, store),
                BistCode => GenerateBist(calendar, manifest, store),
                _ => throw new CalendarDataException("calendar_code_unsupported", calendar.Code),
            }).ToArray();
    }

    public static void Write(string outputRoot, IEnumerable<NormalizedCalendar> calendars)
    {
        var root = Path.GetFullPath(outputRoot);
        foreach (var calendar in calendars)
        {
            var target = Path.GetFullPath(Path.Combine(root, calendar.OutputPath));
            var relative = Path.GetRelativePath(root, target);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new CalendarDataException("output_path_escape", calendar.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, calendar.Content);
        }
    }

    private static void Verify(
        string dataRoot,
        IReadOnlyList<NormalizedCalendar> generated,
        SourceManifest manifest,
        ExpectedOutputSet expected)
    {
        if (expected.SchemaVersion != 1 || expected.SnapshotSetId != manifest.SnapshotSetId)
            throw new CalendarDataException("expected_output_set_mismatch");
        if (expected.Outputs.Count != generated.Count)
            throw new CalendarDataException("expected_output_count_mismatch");

        foreach (var item in generated)
        {
            var contract = expected.Outputs.SingleOrDefault(candidate => candidate.CalendarCode == item.CalendarCode)
                           ?? throw new CalendarDataException("expected_output_missing", item.CalendarCode);
            if (contract.OutputPath != item.OutputPath
                || contract.RowCount != item.RowCount
                || contract.NormalizedSha256 != item.NormalizedSha256
                || contract.SourceBundleSha256 != item.SourceBundleSha256)
                throw new CalendarDataException("expected_output_mismatch", item.CalendarCode);

            var committedPath = Path.Combine(dataRoot, item.OutputPath);
            if (!File.Exists(committedPath))
                throw new CalendarDataException("normalized_output_drift", item.OutputPath);
            SecureBundleStorage.EnsureRegularFileNoFollow(
                dataRoot, committedPath, "normalized_output_unsafe");
            if (!File.ReadAllBytes(committedPath).AsSpan().SequenceEqual(item.Content))
                throw new CalendarDataException("normalized_output_drift", item.OutputPath);
        }
    }

    private static NormalizedCalendar GenerateTcmb(
        CalendarDefinition calendar,
        SourceManifest manifest,
        SourceSnapshotStore store)
    {
        var from = ParseDate(calendar.CoverageFrom, "coverage_from_invalid");
        var through = ParseDate(calendar.CoverageThrough, "coverage_through_invalid");
        var sources = manifest.Sources.Where(source => source.CalendarCode == TcmbCode).ToArray();
        var policy = sources.SingleOrDefault(source => source.Kind == "tcmbPolicyFaq")
            ?? throw new CalendarDataException("tcmb_policy_source_missing");
        ValidateTcmbPolicy(store.Read(policy));
        var annual = UniqueDictionary(sources.Where(source => source.Kind == "tcmbAnnualIndex"), source => source.Year!.Value,
            "tcmb_annual_source_duplicate");
        var months = UniqueDictionary(sources.Where(source => source.Kind == "tcmbMonthlyArchive"),
            source => (source.Year!.Value, source.Month!.Value), "tcmb_month_source_duplicate");

        for (var year = from.Year; year <= through.Year; year++)
        {
            if (!annual.TryGetValue(year, out var annualSource))
                throw new CalendarDataException("tcmb_annual_source_missing", year.ToString(CultureInfo.InvariantCulture));
            var linked = TcmbArchiveParser.ParseAnnualMonthNumbers(store.Read(annualSource), year);
            var firstMonth = year == from.Year ? from.Month : 1;
            var lastMonth = year == through.Year ? through.Month : 12;
            var expectedMonths = Enumerable.Range(firstMonth, lastMonth - firstMonth + 1).ToHashSet();
            if (!expectedMonths.IsSubsetOf(linked))
                throw new CalendarDataException("tcmb_annual_required_month_missing", year.ToString(CultureInfo.InvariantCulture));
            if (year < through.Year && linked.Count != 12)
                throw new CalendarDataException("tcmb_historical_annual_incomplete", year.ToString(CultureInfo.InvariantCulture));
        }

        var output = Header();
        var rowCount = 0;
        DateOnly? latestPublication = null;
        for (var cursor = new DateOnly(from.Year, from.Month, 1); cursor <= through; cursor = cursor.AddMonths(1))
        {
            if (!months.TryGetValue((cursor.Year, cursor.Month), out var source))
                throw new CalendarDataException("tcmb_month_source_missing", cursor.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            var published = TcmbArchiveParser.ParsePublicationDates(store.Read(source), cursor.Year, cursor.Month);
            var latestInMonth = published.Where(date => date <= through).DefaultIfEmpty().Max();
            if (latestInMonth != default && (latestPublication is null || latestInMonth > latestPublication))
                latestPublication = latestInMonth;
            var monthEnd = new DateOnly(cursor.Year, cursor.Month, DateTime.DaysInMonth(cursor.Year, cursor.Month));
            var start = cursor < from ? from : cursor;
            var end = monthEnd > through ? through : monthEnd;
            foreach (var date in Dates(start, end))
            {
                var expected = published.Contains(date);
                AppendRow(output, TcmbCode, date, expected,
                    expected ? "publication" : "no_publication",
                    expected ? "official_archive_link" : "official_archive_absence",
                    source.RawSha256);
                rowCount++;
            }
            if (cursor.Year == through.Year && cursor.Month == through.Month)
            {
                var weekend = through.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                if (!weekend && !published.Contains(through)
                    || weekend && (latestPublication is null
                        || latestPublication < through.AddDays(-3)))
                    throw new CalendarDataException(
                        "tcmb_coverage_beyond_last_publication",
                        through.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
        }
        return Build(calendar, sources, output, rowCount);
    }

    private static NormalizedCalendar GenerateBist(
        CalendarDefinition calendar,
        SourceManifest manifest,
        SourceSnapshotStore store)
    {
        var from = ParseDate(calendar.CoverageFrom, "coverage_from_invalid");
        var through = ParseDate(calendar.CoverageThrough, "coverage_through_invalid");
        var sources = manifest.Sources.Where(source => source.CalendarCode == BistCode).ToArray();
        var index = sources.SingleOrDefault(source => source.Kind == "bistHolidayIndex")
            ?? throw new CalendarDataException("bist_index_source_missing");
        var indexBytes = store.Read(index);
        var pdfs = UniqueDictionary(sources.Where(source => source.Kind == "bistPayHolidayPdf"), source => source.Year!.Value,
            "bist_pdf_source_duplicate");
        var sessionsByYear = new Dictionary<int, IReadOnlyDictionary<DateOnly, BistHolidaySession>>();
        for (var year = from.Year; year <= through.Year; year++)
        {
            if (!pdfs.TryGetValue(year, out var source))
                throw new CalendarDataException("bist_pdf_source_missing", year.ToString(CultureInfo.InvariantCulture));
            ValidateBistIndexLink(indexBytes, source);
            sessionsByYear[year] = BistPayCalendarParser.Parse(store.Read(source), year);
        }

        var output = Header();
        var rowCount = 0;
        foreach (var date in Dates(from, through))
        {
            var source = pdfs[date.Year];
            if (sessionsByYear[date.Year].TryGetValue(date, out var state))
            {
                AppendRow(output, BistCode, date, state == BistHolidaySession.Partial,
                    state == BistHolidaySession.Partial ? "partial_session" : "closed",
                    state == BistHolidaySession.Partial ? "official_partial_session" : "official_no_session",
                    source.RawSha256);
            }
            else if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                AppendRow(output, BistCode, date, false, "closed", "weekend", source.RawSha256);
            }
            else
            {
                // This is explicitly an inference from the complement of the official closure
                // schedule. The reason code must not claim that the PDF directly enumerated an
                // open session; the evidence hash identifies the exact authority schedule used.
                AppendRow(output, BistCode, date, true, "full_session",
                    "inferred_open_from_official_closure_schedule", source.RawSha256);
            }
            rowCount++;
        }
        return Build(calendar, sources, output, rowCount);
    }

    internal static DateOnly ResolveLatestTcmbPublication(
        string dataRoot,
        SourceManifest manifest,
        DateOnly notAfter)
    {
        var store = new SourceSnapshotStore(dataRoot, manifest);
        foreach (var source in manifest.Sources.Where(source =>
                     source.CalendarCode == TcmbCode
                     && source.Kind == "tcmbMonthlyArchive"
                     && new DateOnly(source.Year!.Value, source.Month!.Value, 1) <= notAfter)
                 .OrderByDescending(source => source.Year)
                 .ThenByDescending(source => source.Month))
        {
            var latest = TcmbArchiveParser.ParsePublicationDates(
                    store.Read(source), source.Year!.Value, source.Month!.Value)
                .Where(date => date <= notAfter)
                .OrderByDescending(date => date)
                .FirstOrDefault();
            if (latest != default) return latest;
        }
        throw new CalendarDataException("tcmb_publication_evidence_missing",
            notAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    internal static DateOnly ResolveTcmbCoverageThrough(
        string dataRoot,
        SourceManifest manifest,
        DateOnly requestedThrough)
    {
        if (requestedThrough.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            // The pinned TCMB policy explicitly states that indicative rates are
            // not determined on weekends. Archive evidence must still be recent;
            // an arbitrarily old publication cannot justify advancing coverage.
            var latest = ResolveLatestTcmbPublication(dataRoot, manifest, requestedThrough);
            if (latest < requestedThrough.AddDays(-3))
                throw new CalendarDataException(
                    "tcmb_coverage_beyond_last_publication",
                    requestedThrough.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            return requestedThrough;
        }
        return ResolveLatestTcmbPublication(dataRoot, manifest, requestedThrough);
    }

    private static void ValidateTcmbPolicy(byte[] raw)
    {
        var text = Encoding.UTF8.GetString(raw);
        if (!text.Contains("15.30", StringComparison.Ordinal)
            || !text.Contains("16.00-16.30", StringComparison.Ordinal)
            || !text.Contains("resmi tatiller, hafta sonları ve yarım gün", StringComparison.OrdinalIgnoreCase))
            throw new CalendarDataException("tcmb_policy_semantics_missing");
    }

    private static void ValidateBistIndexLink(byte[] indexRaw, SourceDefinition pdf)
    {
        var text = Encoding.UTF8.GetString(indexRaw);
        var expected = $"pay-piyasasi-{pdf.Year}-yili-tatil-tablosu.pdf";
        if (!text.Contains(expected, StringComparison.OrdinalIgnoreCase))
            throw new CalendarDataException("bist_index_pdf_link_missing", expected);
    }

    private static NormalizedCalendar Build(
        CalendarDefinition calendar,
        IReadOnlyCollection<SourceDefinition> sources,
        StringBuilder output,
        int rowCount)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(output.ToString());
        return new NormalizedCalendar(calendar.Code, calendar.OutputPath, bytes, rowCount,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), SourceBundleHash(sources));
    }

    private static string SourceBundleHash(IEnumerable<SourceDefinition> sources)
    {
        var canonical = new StringBuilder();
        foreach (var source in sources.OrderBy(item => item.Id, StringComparer.Ordinal))
            canonical.Append(source.Id).Append('\t')
                .Append(source.Kind).Append('\t')
                .Append(source.Role).Append('\t')
                .Append(source.Uri).Append('\t')
                .Append(source.MediaType).Append('\t')
                .Append(source.RetrievedAt).Append('\t')
                .Append(source.RawSha256).Append('\t')
                .Append(source.SnapshotPath).Append('\t')
                .Append(source.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('\t')
                .Append(source.Month?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void ValidateManifestShape(SourceManifest manifest)
    {
        var expectedCodes = new[] { TcmbCode, BistCode };
        if (!manifest.Calendars.Select(item => item.Code).Order(StringComparer.Ordinal)
                .SequenceEqual(expectedCodes.Order(StringComparer.Ordinal)))
            throw new CalendarDataException("calendar_set_invalid");
        foreach (var calendar in manifest.Calendars)
        {
            var from = ParseDate(calendar.CoverageFrom, "coverage_from_invalid");
            var through = ParseDate(calendar.CoverageThrough, "coverage_through_invalid");
            if (from > through)
                throw new CalendarDataException("coverage_range_invalid", calendar.Code);
            if (!calendar.OutputPath.StartsWith("normalized/", StringComparison.Ordinal)
                || Path.GetExtension(calendar.OutputPath) != ".csv")
                throw new CalendarDataException("calendar_output_path_invalid", calendar.Code);
        }
    }

    private static Dictionary<TKey, SourceDefinition> UniqueDictionary<TKey>(
        IEnumerable<SourceDefinition> sources,
        Func<SourceDefinition, TKey> keySelector,
        string errorCode) where TKey : notnull
    {
        var result = new Dictionary<TKey, SourceDefinition>();
        foreach (var source in sources)
            if (!result.TryAdd(keySelector(source), source))
                throw new CalendarDataException(errorCode, source.Id);
        return result;
    }

    private static DateOnly ParseDate(string text, string code)
    {
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var result))
            throw new CalendarDataException(code, text);
        return result;
    }

    private static IEnumerable<DateOnly> Dates(DateOnly from, DateOnly through)
    {
        for (var date = from; date <= through; date = date.AddDays(1))
            yield return date;
    }

    private static StringBuilder Header() =>
        new("calendar_code,date,observation_expected,market_state,reason_code,evidence_raw_sha256\n");

    private static void AppendRow(
        StringBuilder output,
        string code,
        DateOnly date,
        bool expected,
        string state,
        string reason,
        string evidenceHash)
    {
        output.Append(code).Append(',')
            .Append(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
            .Append(expected ? "true" : "false").Append(',')
            .Append(state).Append(',').Append(reason).Append(',').Append(evidenceHash).Append('\n');
    }
}
