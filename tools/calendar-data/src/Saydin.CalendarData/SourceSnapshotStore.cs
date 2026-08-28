using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Saydin.CalendarData;

public sealed partial class SourceSnapshotStore
{
    private static readonly string[] MonthCodes =
        ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    private readonly string _dataRoot;
    private readonly IReadOnlyDictionary<string, SourceDefinition> _sources;
    private readonly Dictionary<string, byte[]> _verifiedContent = new(StringComparer.Ordinal);

    public SourceSnapshotStore(string dataRoot, SourceManifest manifest)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        if (manifest.SchemaVersion != 1)
            throw new CalendarDataException("manifest_schema_unsupported", manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        if (string.IsNullOrWhiteSpace(manifest.SnapshotSetId))
            throw new CalendarDataException("snapshot_set_id_missing");
        if (manifest.Calendars.Count == 0 || manifest.Sources.Count == 0)
            throw new CalendarDataException("manifest_empty");

        var calendarCodes = manifest.Calendars.Select(item => item.Code).ToHashSet(StringComparer.Ordinal);
        if (calendarCodes.Count != manifest.Calendars.Count)
            throw new CalendarDataException("calendar_code_duplicate");

        var byId = new Dictionary<string, SourceDefinition>(StringComparer.Ordinal);
        var snapshotPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in manifest.Sources)
        {
            ValidateSource(source, calendarCodes);
            if (!byId.TryAdd(source.Id, source))
                throw new CalendarDataException("source_id_duplicate", source.Id);
            if (snapshotPaths.TryGetValue(source.SnapshotPath, out var existingHash)
                && !string.Equals(existingHash, source.RawSha256, StringComparison.Ordinal))
                throw new CalendarDataException("snapshot_path_conflict", source.SnapshotPath);
            snapshotPaths[source.SnapshotPath] = source.RawSha256;
        }
        _sources = byId;
    }

    public byte[] Read(SourceDefinition source)
    {
        if (!_sources.TryGetValue(source.Id, out var known) || !ReferenceEquals(source, known))
            throw new CalendarDataException("source_not_in_manifest", source.Id);

        if (_verifiedContent.TryGetValue(source.Id, out var verified))
            return verified;

        var fullPath = Path.GetFullPath(Path.Combine(_dataRoot, source.SnapshotPath));
        var relative = Path.GetRelativePath(_dataRoot, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new CalendarDataException("snapshot_path_escape", source.SnapshotPath);
        if (!File.Exists(fullPath))
            throw new CalendarDataException("snapshot_missing", source.SnapshotPath);

        SecureBundleStorage.EnsureRegularFileNoFollow(_dataRoot, fullPath, "snapshot_path_unsafe");
        var length = new FileInfo(fullPath).Length;
        var maximum = OfficialSourcePolicy.MaximumBytes(source.MediaType);
        if (length is <= 0 || length > maximum)
            throw new CalendarDataException("snapshot_size_invalid", source.Id);

        var raw = File.ReadAllBytes(fullPath);
        var actual = Convert.ToHexStringLower(SHA256.HashData(raw));
        if (!string.Equals(actual, source.RawSha256, StringComparison.Ordinal))
            throw new CalendarDataException("snapshot_hash_mismatch", $"{source.Id}: expected={source.RawSha256}, actual={actual}");
        if (source.MediaType == "application/pdf" && !raw.AsSpan().StartsWith("%PDF-"u8))
            throw new CalendarDataException("snapshot_media_mismatch", source.Id);
        _verifiedContent.Add(source.Id, raw);
        return raw;
    }

    internal static void ValidateSource(SourceDefinition source, IReadOnlySet<string> calendarCodes)
    {
        if (!SourceIdRegex().IsMatch(source.Id))
            throw new CalendarDataException("source_id_invalid", source.Id);
        if (!calendarCodes.Contains(source.CalendarCode))
            throw new CalendarDataException("source_calendar_unknown", source.Id);
        if (source.Role is not ("authority" or "discovery" or "policy"))
            throw new CalendarDataException("source_role_invalid", source.Id);
        if (!Sha256Regex().IsMatch(source.RawSha256))
            throw new CalendarDataException("source_hash_invalid", source.Id);
        if (!DateTimeOffset.TryParseExact(source.RetrievedAt, "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _))
            throw new CalendarDataException("source_retrieved_at_invalid", source.Id);

        if (!Uri.TryCreate(source.Uri, UriKind.Absolute, out var uri))
            throw new CalendarDataException("source_uri_invalid", source.Id);

        ValidateOfficialUri(source, uri);

        var expectedExtension = source.MediaType switch
        {
            "text/html" => ".html",
            "application/pdf" => ".pdf",
            _ => throw new CalendarDataException("source_media_type_invalid", source.Id),
        };
        if (!source.SnapshotPath.StartsWith("snapshots/sha256/", StringComparison.Ordinal)
            || Path.GetExtension(source.SnapshotPath) != expectedExtension
            || Path.GetFileNameWithoutExtension(source.SnapshotPath) != source.RawSha256)
            throw new CalendarDataException("snapshot_path_not_content_addressed", source.Id);

    }

    internal static void ValidateOfficialUri(SourceDefinition source, Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new CalendarDataException("source_uri_invalid", source.Id);

        switch (source.Kind)
        {
            case "tcmbAnnualIndex":
                RequireYearOnly(source);
                RequireUri(uri, "www.tcmb.gov.tr", $"/kurlar/kur{source.Year}_tr.html", source.Id);
                RequireMediaType(source, "text/html");
                break;
            case "tcmbMonthlyArchive":
                RequireYearMonth(source);
                RequireUri(uri, "www.tcmb.gov.tr",
                    $"/kurlar/{source.Year}{source.Month:00}/{MonthCodes[source.Month!.Value - 1]}_tr.html", source.Id);
                RequireMediaType(source, "text/html");
                break;
            case "tcmbPolicyFaq":
                if (source.Year is not null || source.Month is not null)
                    throw new CalendarDataException("source_date_unexpected", source.Id);
                RequireUri(uri, "www.tcmb.gov.tr",
                    "/wps/wcm/connect/TR/TCMB+TR/Main+Menu/Banka+Hakkinda/Sikca+Sorulan+Sorular", source.Id);
                RequireMediaType(source, "text/html");
                break;
            case "bistPayHolidayPdf":
                RequireYearOnly(source);
                RequireUri(uri, "www.borsaistanbul.com",
                    $"/files/pay-piyasasi-{source.Year}-yili-tatil-tablosu.pdf", source.Id);
                RequireMediaType(source, "application/pdf");
                break;
            case "bistHolidayIndex":
                if (source.Year is not null || source.Month is not null)
                    throw new CalendarDataException("source_date_unexpected", source.Id);
                RequireUri(uri, "www.borsaistanbul.com", "/resmi-tatil-gunleri", source.Id);
                RequireMediaType(source, "text/html");
                break;
            default:
                throw new CalendarDataException("source_kind_invalid", source.Id);
        }
    }

    private static void RequireYearOnly(SourceDefinition source)
    {
        if (source.Year is < 2000 or > 2100 || source.Month is not null)
            throw new CalendarDataException("source_year_invalid", source.Id);
    }

    private static void RequireYearMonth(SourceDefinition source)
    {
        if (source.Year is < 2000 or > 2100 || source.Month is < 1 or > 12)
            throw new CalendarDataException("source_year_month_invalid", source.Id);
    }

    private static void RequireMediaType(SourceDefinition source, string expected)
    {
        if (source.MediaType != expected)
            throw new CalendarDataException("source_media_type_mismatch", source.Id);
    }

    private static void RequireUri(Uri uri, string host, string path, string id)
    {
        var escapedPath = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        if (!escapedPath.StartsWith("/", StringComparison.Ordinal)) escapedPath = "/" + escapedPath;
        if (escapedPath.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || escapedPath.Contains("%5c", StringComparison.OrdinalIgnoreCase))
            throw new CalendarDataException("source_uri_not_allowlisted", id);
        var unescapedPath = Uri.UnescapeDataString(escapedPath);
        if (!string.Equals(uri.IdnHost, host, StringComparison.Ordinal)
            || !string.Equals(unescapedPath, path, StringComparison.Ordinal))
            throw new CalendarDataException("source_uri_not_allowlisted", id);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SourceIdRegex();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
