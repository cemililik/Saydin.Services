using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Saydin.CalendarData;

public sealed class CalendarAcquisitionPlan
{
    public required int SchemaVersion { get; init; }
    public required string SnapshotSetId { get; init; }
    public required IReadOnlyList<CalendarDefinition> Calendars { get; init; }
    public required IReadOnlyList<CalendarAcquisitionSource> Sources { get; init; }
}

public sealed class CalendarAcquisitionSource
{
    public required string Id { get; init; }
    public required string CalendarCode { get; init; }
    public required string Kind { get; init; }
    public required string Role { get; init; }
    public required string Uri { get; init; }
    public required string MediaType { get; init; }
    public int? Year { get; init; }
    public int? Month { get; init; }
}

public sealed record CalendarAcquisitionOptions(
    string BaseDataRoot,
    string PlanPath,
    string StagingRoot,
    string OutputName,
    TimeSpan RequestTimeout,
    int MaximumAttempts = 3,
    int MaximumRedirects = 3);

public sealed class CalendarReviewEnvelope
{
    public required int SchemaVersion { get; init; }
    public required string SnapshotSetId { get; init; }
    public required string SourceManifestSha256 { get; init; }
    public required string ExpectedOutputSha256 { get; init; }
}

public sealed class CalendarAcquisition(
    HttpClient httpClient,
    TimeProvider timeProvider,
    Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
{
    private static readonly Regex OutputNamePattern = new(
        "^[a-z0-9][a-z0-9._-]{0,79}$", RegexOptions.CultureInvariant);
    private readonly Func<TimeSpan, CancellationToken, Task> _retryDelay =
        retryDelay ?? ((delay, token) => Task.Delay(delay, timeProvider, token));

    public async Task<string> RunAsync(CalendarAcquisitionOptions options, CancellationToken ct)
    {
        ValidateOptions(options);
        var baseRoot = Path.GetFullPath(options.BaseDataRoot);
        var planPath = Path.GetFullPath(options.PlanPath);
        SecureBundleStorage.EnsureRegularFileNoFollow(
            Path.GetDirectoryName(planPath)!, planPath, "acquisition_plan_unsafe");
        var plan = ManifestJson.Read<CalendarAcquisitionPlan>(planPath);
        ValidatePlan(plan);

        var baseBundle = CalendarDataGenerator.LoadVerified(baseRoot);
        if (string.Equals(baseBundle.Manifest.SnapshotSetId, plan.SnapshotSetId, StringComparison.Ordinal))
            throw new CalendarDataException("snapshot_set_id_not_advanced", plan.SnapshotSetId);
        ValidateAgainstBase(plan, baseBundle.Manifest);

        var stagingRoot = SecureBundleStorage.EnsurePrivateDirectory(options.StagingRoot);
        using var acquisitionLock = SecureBundleStorage.OpenExclusiveLock(stagingRoot);
        var finalPath = Path.Combine(stagingRoot, options.OutputName);
        if (Directory.Exists(finalPath) || File.Exists(finalPath))
            throw new CalendarDataException("acquisition_output_exists", options.OutputName);

        var pendingName = $".pending-{options.OutputName}-{Guid.CreateVersion7():N}";
        var pendingRoot = SecureBundleStorage.EnsurePrivateDirectory(Path.Combine(stagingRoot, pendingName));
        try
        {
        var sources = baseBundle.Manifest.Sources.ToDictionary(source => source.Id, StringComparer.Ordinal);
        var refreshedSourceIds = plan.Sources
            .Select(source => source.Id)
            .ToHashSet(StringComparer.Ordinal);
        var writtenSnapshots = new HashSet<string>(StringComparer.Ordinal);
        var baseStore = new SourceSnapshotStore(baseRoot, baseBundle.Manifest);

        foreach (var source in baseBundle.Manifest.Sources)
        {
            // A refreshed source can produce different content-addressed bytes. Do not copy its
            // previous snapshot into the candidate: it is no longer referenced by the final
            // manifest and the reviewed exact-inventory gate must reject orphan raw material.
            if (refreshedSourceIds.Contains(source.Id)) continue;
            var raw = baseStore.Read(source);
            WriteSnapshot(pendingRoot, source.SnapshotPath, raw, writtenSnapshots);
        }
        baseBundle.EnsureInputsUnchanged();

        var retrievedAt = timeProvider.GetUtcNow().ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        foreach (var request in plan.Sources.OrderBy(source => source.Id, StringComparer.Ordinal))
        {
            var template = PendingDefinition(request, retrievedAt);
            var raw = await DownloadWithRetryAsync(template, options, ct);
            var hash = Convert.ToHexStringLower(SHA256.HashData(raw));
            var extension = request.MediaType == "application/pdf" ? ".pdf" : ".html";
            var snapshotPath = $"snapshots/sha256/{hash}{extension}";
            WriteSnapshot(pendingRoot, snapshotPath, raw, writtenSnapshots);
            sources[request.Id] = new SourceDefinition
            {
                Id = request.Id,
                CalendarCode = request.CalendarCode,
                Kind = request.Kind,
                Role = request.Role,
                Uri = request.Uri,
                MediaType = request.MediaType,
                RetrievedAt = retrievedAt,
                RawSha256 = hash,
                SnapshotPath = snapshotPath,
                Year = request.Year,
                Month = request.Month,
            };
        }

        var requestedManifest = new SourceManifest
        {
            SchemaVersion = 1,
            SnapshotSetId = plan.SnapshotSetId,
            Calendars = plan.Calendars,
            Sources = sources.Values.OrderBy(source => source.Id, StringComparer.Ordinal).ToArray(),
        };
        var requestedTcmb = plan.Calendars.Single(calendar =>
            calendar.Code == CalendarDataGenerator.TcmbCode);
        var requestedThrough = ParseDate(requestedTcmb.CoverageThrough);
        var previousTcmb = baseBundle.Manifest.Calendars.Single(calendar =>
            calendar.Code == CalendarDataGenerator.TcmbCode);
        var previousThrough = ParseDate(previousTcmb.CoverageThrough);
        var evidencedThrough = CalendarDataGenerator.ResolveTcmbCoverageThrough(
            pendingRoot, requestedManifest, requestedThrough);
        if (evidencedThrough < previousThrough)
            throw new CalendarDataException("tcmb_publication_evidence_regressed");
        var materializedCalendars = plan.Calendars.Select(calendar =>
            calendar.Code == CalendarDataGenerator.TcmbCode
                ? new CalendarDefinition
                {
                    Code = calendar.Code,
                    CoverageFrom = calendar.CoverageFrom,
                    CoverageThrough = evidencedThrough.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    OutputPath = calendar.OutputPath,
                }
                : calendar).ToArray();
        var manifest = new SourceManifest
        {
            SchemaVersion = requestedManifest.SchemaVersion,
            SnapshotSetId = requestedManifest.SnapshotSetId,
            Calendars = materializedCalendars,
            Sources = requestedManifest.Sources,
        };
        var manifestBytes = ManifestJson.Write(manifest);
        SecureBundleStorage.WriteNewPrivateFile(
            pendingRoot, "source-manifest.json", manifestBytes);

        var generated = CalendarDataGenerator.Generate(pendingRoot);
        foreach (var calendar in generated)
            SecureBundleStorage.WriteNewPrivateFile(pendingRoot, calendar.OutputPath, calendar.Content);
        var expected = new ExpectedOutputSet
        {
            SchemaVersion = 1,
            SnapshotSetId = manifest.SnapshotSetId,
            Outputs = generated.Select(calendar => new ExpectedOutput
            {
                CalendarCode = calendar.CalendarCode,
                OutputPath = calendar.OutputPath,
                RowCount = calendar.RowCount,
                NormalizedSha256 = calendar.NormalizedSha256,
                SourceBundleSha256 = calendar.SourceBundleSha256,
            }).OrderBy(output => output.CalendarCode, StringComparer.Ordinal).ToArray(),
        };
        var expectedBytes = ManifestJson.Write(expected);
        SecureBundleStorage.WriteNewPrivateFile(
            pendingRoot, "expected-output.json", expectedBytes);
        SecureBundleStorage.WriteNewPrivateFile(
            pendingRoot, "review-envelope.json", ManifestJson.Write(new CalendarReviewEnvelope
            {
                SchemaVersion = 1,
                SnapshotSetId = manifest.SnapshotSetId,
                SourceManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
                ExpectedOutputSha256 = Convert.ToHexStringLower(SHA256.HashData(expectedBytes)),
            }));

        var verified = CalendarDataGenerator.LoadVerified(pendingRoot);
        verified.EnsureInputsUnchanged();
        Directory.Move(pendingRoot, finalPath);
        return finalPath;
        }
        catch
        {
            SecureBundleStorage.DeletePrivateTree(pendingRoot, stagingRoot);
            throw;
        }
    }

    private static DateOnly ParseDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var result)
            ? result
            : throw new CalendarDataException("coverage_through_invalid", value);

    private async Task<byte[]> DownloadWithRetryAsync(
        SourceDefinition source,
        CalendarAcquisitionOptions options,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await DownloadAsync(source, options, ct);
            }
            catch (RetryableAcquisitionException) when (attempt < options.MaximumAttempts)
            {
                await _retryDelay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
            catch (RetryableAcquisitionException)
            {
                throw new CalendarDataException("source_http_retry_exhausted", source.Id);
            }
            catch (HttpRequestException) when (attempt < options.MaximumAttempts)
            {
                await _retryDelay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < options.MaximumAttempts)
            {
                await _retryDelay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
            catch (HttpRequestException ex)
            {
                throw new CalendarDataException("source_network_failed", $"{source.Id}: {ex.GetType().Name}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new CalendarDataException("source_timeout", source.Id);
            }
        }
    }

    internal Task<byte[]> DownloadForTestAsync(
        SourceDefinition source, CalendarAcquisitionOptions options, CancellationToken ct) =>
        DownloadWithRetryAsync(source, options, ct);

    private async Task<byte[]> DownloadAsync(
        SourceDefinition source,
        CalendarAcquisitionOptions options,
        CancellationToken ct)
    {
        var current = new Uri(source.Uri, UriKind.Absolute);
        for (var redirect = 0; ; redirect++)
        {
            SourceSnapshotStore.ValidateOfficialUri(source, current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("Saydin-Calendar-Acquisition/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(source.MediaType));
            request.Headers.AcceptEncoding.ParseAdd("identity");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.RequestTimeout);
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            if (IsRedirect(response.StatusCode))
            {
                if (redirect >= options.MaximumRedirects || response.Headers.Location is null)
                    throw new CalendarDataException("source_redirect_invalid", source.Id);
                current = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                SourceSnapshotStore.ValidateOfficialUri(source, current);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                throw new RetryableAcquisitionException();
            if (response.StatusCode != HttpStatusCode.OK)
                throw new CalendarDataException(
                    "source_http_status_invalid", $"{source.Id}: {(int)response.StatusCode}");
            if (response.Content.Headers.ContentEncoding.Count != 0)
                throw new CalendarDataException("source_content_encoding_invalid", source.Id);
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType,
                    source.MediaType, StringComparison.OrdinalIgnoreCase))
                throw new CalendarDataException("source_media_type_mismatch", source.Id);

            var maximum = OfficialSourcePolicy.MaximumBytes(source.MediaType);
            if (response.Content.Headers.ContentLength is <= 0
                || response.Content.Headers.ContentLength > maximum)
                throw new CalendarDataException("source_size_invalid", source.Id);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var raw = await ReadBoundedAsync(stream, maximum, timeout.Token);
            if (source.MediaType == "application/pdf" && !raw.AsSpan().StartsWith("%PDF-"u8))
                throw new CalendarDataException("source_media_mismatch", source.Id);
            return raw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, long maximum, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (output.Length + count > maximum)
                throw new CalendarDataException("source_size_invalid");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (output.Length == 0) throw new CalendarDataException("source_size_invalid");
        return output.ToArray();
    }

    private static SourceDefinition PendingDefinition(CalendarAcquisitionSource request, string retrievedAt)
    {
        var extension = request.MediaType == "application/pdf" ? ".pdf" : ".html";
        return new SourceDefinition
        {
            Id = request.Id,
            CalendarCode = request.CalendarCode,
            Kind = request.Kind,
            Role = request.Role,
            Uri = request.Uri,
            MediaType = request.MediaType,
            RetrievedAt = retrievedAt,
            RawSha256 = new string('0', 64),
            SnapshotPath = $"snapshots/sha256/{new string('0', 64)}{extension}",
            Year = request.Year,
            Month = request.Month,
        };
    }

    private static void WriteSnapshot(
        string root, string path, byte[] raw, ISet<string> writtenSnapshots)
    {
        if (writtenSnapshots.Add(path)) SecureBundleStorage.WriteNewPrivateFile(root, path, raw);
    }

    private static void ValidatePlan(CalendarAcquisitionPlan plan)
    {
        if (plan.SchemaVersion != 1)
            throw new CalendarDataException("acquisition_plan_schema_unsupported");
        if (!OutputNamePattern.IsMatch(plan.SnapshotSetId) || plan.Calendars.Count != 2 || plan.Sources.Count == 0)
            throw new CalendarDataException("acquisition_plan_invalid");
        if (plan.Sources.Select(source => source.Id).Distinct(StringComparer.Ordinal).Count() != plan.Sources.Count)
            throw new CalendarDataException("acquisition_source_duplicate");
        var calendars = plan.Calendars.Select(calendar => calendar.Code).ToHashSet(StringComparer.Ordinal);
        foreach (var request in plan.Sources)
            SourceSnapshotStore.ValidateSource(PendingDefinition(request, "2000-01-01T00:00:00Z"), calendars);
    }

    private static void ValidateAgainstBase(CalendarAcquisitionPlan plan, SourceManifest baseManifest)
    {
        var baseCalendars = baseManifest.Calendars.ToDictionary(calendar => calendar.Code, StringComparer.Ordinal);
        foreach (var calendar in plan.Calendars)
        {
            if (!baseCalendars.TryGetValue(calendar.Code, out var previous)
                || calendar.CoverageFrom != previous.CoverageFrom
                || calendar.OutputPath != previous.OutputPath
                || !DateOnly.TryParseExact(calendar.CoverageThrough, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var through)
                || !DateOnly.TryParseExact(previous.CoverageThrough, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var previousThrough)
                || through < previousThrough)
                throw new CalendarDataException("calendar_coverage_regression", calendar.Code);
        }

        var priorSources = baseManifest.Sources.ToDictionary(source => source.Id, StringComparer.Ordinal);
        foreach (var request in plan.Sources)
        {
            if (!priorSources.TryGetValue(request.Id, out var previous)) continue;
            if (request.CalendarCode != previous.CalendarCode
                || request.Kind != previous.Kind
                || request.Role != previous.Role
                || request.Uri != previous.Uri
                || request.MediaType != previous.MediaType
                || request.Year != previous.Year
                || request.Month != previous.Month)
                throw new CalendarDataException("source_identity_changed", request.Id);
        }
    }

    private static void ValidateOptions(CalendarAcquisitionOptions options)
    {
        if (!Path.IsPathFullyQualified(options.BaseDataRoot)
            || !Path.IsPathFullyQualified(options.PlanPath)
            || !Path.IsPathFullyQualified(options.StagingRoot)
            || !OutputNamePattern.IsMatch(options.OutputName)
            || options.RequestTimeout < TimeSpan.FromSeconds(1)
            || options.RequestTimeout > TimeSpan.FromMinutes(2)
            || options.MaximumAttempts is < 1 or > 5
            || options.MaximumRedirects is < 0 or > 5)
            throw new CalendarDataException("acquisition_arguments_invalid");
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private sealed class RetryableAcquisitionException : Exception;
}
