using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Saydin.CalendarData;

public static class CalendarReleaseImporter
{
    private const int ConnectionTimeoutSeconds = 15;
    private const int CommandTimeoutSeconds = 300;
    private const int LockTimeoutSeconds = 30;

    public static async Task<string> ImportAsync(
        CalendarReleaseCommand options,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default,
        Action? verifiedBundleBarrier = null)
    {
        if (options.Command != CalendarReleaseCommandName.Import)
            throw new CalendarDataException("import_command_required");
        var bundle = CalendarDataGenerator.LoadVerified(options.DataRoot);
        verifiedBundleBarrier?.Invoke();
        bundle.EnsureInputsUnchanged();
        var normalized = bundle.Calendars.Single(item => item.CalendarCode == options.CalendarCode);
        var manifest = bundle.Manifest;
        var calendar = manifest.Calendars.Single(item => item.Code == options.CalendarCode);
        var sources = manifest.Sources.Where(item => item.CalendarCode == options.CalendarCode)
            .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var days = ParseDays(normalized.Content, options.CalendarCode);
        var coverageFrom = ParseDate(calendar.CoverageFrom);
        var coverageThrough = ParseDate(calendar.CoverageThrough);
        var releasedAt = LatestRetrievedAt(sources);
        if (days.Count != normalized.RowCount || days[0].Date != coverageFrom
            || days[^1].Date != coverageThrough)
            throw new CalendarDataException("normalized_coverage_mismatch", options.CalendarCode);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ConfigureTransactionTimeoutsAsync(connection, transaction, cancellationToken);
        await LockAsync(connection, transaction, options.CalendarCode, cancellationToken);

        var existing = await ReadReleaseAsync(
            connection, transaction, options.ReleaseId, cancellationToken);
        if (existing is not null)
        {
            if (existing.CalendarCode != options.CalendarCode
                || existing.SnapshotSetId != manifest.SnapshotSetId
                || existing.ReleaseVersion != options.ReleaseVersion
                || existing.NormalizedSha256 != normalized.NormalizedSha256
                || existing.SourceBundleSha256 != normalized.SourceBundleSha256
                || existing.RowCount != normalized.RowCount
                || existing.CoverageFrom != coverageFrom
                || existing.CoverageThrough != coverageThrough
                || existing.ReleasedAt != releasedAt
                || existing.SealedAt is null)
                throw new CalendarDataException("release_id_payload_conflict", options.ReleaseId.ToString("D"));
            var persistedSources = await ReadSourcesAsync(
                connection, transaction, options.ReleaseId, cancellationToken);
            var expectedSources = sources.Select(source => new SourceRow(
                source.Id, source.Kind, source.Role, source.Uri, source.MediaType,
                ParseRetrievedAt(source.RetrievedAt), source.RawSha256, source.SnapshotPath,
                source.Year, source.Month)).ToArray();
            if (!persistedSources.SequenceEqual(expectedSources))
                throw new CalendarDataException(
                    "release_id_source_provenance_conflict", options.ReleaseId.ToString("D"));
            await ActivatePointerAsync(connection, transaction, options, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result("import_idempotent", options, normalized);
        }

        await InsertReleaseAsync(connection, transaction, options, manifest.SnapshotSetId,
            normalized, coverageFrom, coverageThrough, sources, cancellationToken);
        await CopySourcesAsync(connection, options.ReleaseId, sources, cancellationToken);
        await CopyDaysAsync(connection, options.ReleaseId, days, cancellationToken);
        await using (var seal = new NpgsqlCommand(
            "SELECT public.seal_market_calendar_release($1)", connection, transaction))
        {
            seal.Parameters.AddWithValue(options.ReleaseId);
            await seal.ExecuteNonQueryAsync(cancellationToken);
        }
        await ActivatePointerAsync(connection, transaction, options, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result("imported", options, normalized);
    }

    public static async Task<string> ActivateAsync(
        CalendarReleaseCommand options,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        if (options.Command != CalendarReleaseCommandName.Activate)
            throw new CalendarDataException("activate_command_required");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ConfigureTransactionTimeoutsAsync(connection, transaction, cancellationToken);
        await LockAsync(connection, transaction, options.CalendarCode, cancellationToken);
        var release = await ReadReleaseAsync(connection, transaction, options.ReleaseId, cancellationToken);
        if (release is null || release.CalendarCode != options.CalendarCode || release.SealedAt is null)
            throw new CalendarDataException("rollback_release_not_sealed");
        await ActivatePointerAsync(connection, transaction, options, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return $"activated: calendar={options.CalendarCode}; release={options.ReleaseId:D}; normalized_sha256={release.NormalizedSha256}";
    }

    private static async Task InsertReleaseAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        CalendarReleaseCommand options, string snapshotSetId,
        NormalizedCalendar normalized, DateOnly coverageFrom, DateOnly coverageThrough,
        IReadOnlyCollection<SourceDefinition> sources, CancellationToken ct)
    {
        var releasedAt = LatestRetrievedAt(sources);
        await using var command = new NpgsqlCommand("""
            INSERT INTO market_calendar_releases(
                id,calendar_code,snapshot_set_id,release_version,coverage_from,coverage_through,
                row_count,normalized_sha256,source_bundle_sha256,released_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
            """, connection, transaction);
        command.Parameters.AddWithValue(options.ReleaseId);
        command.Parameters.AddWithValue(options.CalendarCode);
        command.Parameters.AddWithValue(snapshotSetId);
        command.Parameters.AddWithValue(options.ReleaseVersion);
        command.Parameters.AddWithValue(coverageFrom);
        command.Parameters.AddWithValue(coverageThrough);
        command.Parameters.AddWithValue(normalized.RowCount);
        command.Parameters.AddWithValue(normalized.NormalizedSha256);
        command.Parameters.AddWithValue(normalized.SourceBundleSha256);
        command.Parameters.AddWithValue(releasedAt);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task CopySourcesAsync(
        NpgsqlConnection connection, Guid releaseId,
        IReadOnlyCollection<SourceDefinition> sources, CancellationToken ct)
    {
        await using var writer = await connection.BeginBinaryImportAsync("""
            COPY market_calendar_release_sources(
                release_id,source_id,source_kind,source_role,source_uri,media_type,
                retrieved_at,raw_sha256,snapshot_path,source_year,source_month) FROM STDIN (FORMAT BINARY)
            """, ct);
        foreach (var source in sources)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(releaseId, NpgsqlDbType.Uuid, ct);
            await writer.WriteAsync(source.Id, NpgsqlDbType.Varchar, ct);
            await writer.WriteAsync(source.Kind, NpgsqlDbType.Varchar, ct);
            await writer.WriteAsync(source.Role, NpgsqlDbType.Varchar, ct);
            await writer.WriteAsync(source.Uri, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(source.MediaType, NpgsqlDbType.Varchar, ct);
            await writer.WriteAsync(ParseRetrievedAt(source.RetrievedAt), NpgsqlDbType.TimestampTz, ct);
            await writer.WriteAsync(source.RawSha256, NpgsqlDbType.Char, ct);
            await writer.WriteAsync(source.SnapshotPath, NpgsqlDbType.Text, ct);
            if (source.Year is { } year) await writer.WriteAsync(year, NpgsqlDbType.Integer, ct);
            else await writer.WriteNullAsync(ct);
            if (source.Month is { } month) await writer.WriteAsync(month, NpgsqlDbType.Integer, ct);
            else await writer.WriteNullAsync(ct);
        }
        await writer.CompleteAsync(ct);
    }

    private static async Task CopyDaysAsync(
        NpgsqlConnection connection, Guid releaseId,
        IReadOnlyCollection<CalendarDayRow> days, CancellationToken ct)
    {
        await using var writer = await connection.BeginBinaryImportAsync("""
            COPY market_calendar_days(
                release_id,calendar_date,observation_expected,market_state,reason_code,evidence_raw_sha256)
            FROM STDIN (FORMAT BINARY)
            """, ct);
        foreach (var day in days)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(releaseId, NpgsqlDbType.Uuid, ct);
            await writer.WriteAsync(day.Date, NpgsqlDbType.Date, ct);
            await writer.WriteAsync(day.ObservationExpected, NpgsqlDbType.Boolean, ct);
            await writer.WriteAsync(day.MarketState, NpgsqlDbType.Varchar, ct);
            await writer.WriteAsync(day.ReasonCode, NpgsqlDbType.Varchar, ct);
            await writer.WriteAsync(day.EvidenceRawSha256, NpgsqlDbType.Char, ct);
        }
        await writer.CompleteAsync(ct);
    }

    private static async Task ActivatePointerAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        CalendarReleaseCommand options, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT public.activate_market_calendar_release($1,$2,$3)",
            connection, transaction);
        command.Parameters.AddWithValue(options.CalendarCode);
        command.Parameters.AddWithValue(options.ReleaseId);
        command.Parameters.AddWithValue(options.ExpectedCurrentReleaseId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task LockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string calendarCode, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('saydin.calendar.' || $1,0))",
            connection, transaction);
        command.Parameters.AddWithValue(calendarCode);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ConfigureTransactionTimeoutsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"""
            SET LOCAL lock_timeout='{LockTimeoutSeconds}s';
            SET LOCAL statement_timeout='{CommandTimeoutSeconds}s';
            SET LOCAL idle_in_transaction_session_timeout='{CommandTimeoutSeconds}s';
            """, connection, transaction)
        {
            CommandTimeout = ConnectionTimeoutSeconds,
        };
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<ReleaseRow?> ReadReleaseAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid releaseId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT calendar_code,snapshot_set_id,release_version,coverage_from,coverage_through,row_count,
                   normalized_sha256,source_bundle_sha256,released_at,sealed_at
              FROM market_calendar_releases WHERE id=$1
            """, connection, transaction);
        command.Parameters.AddWithValue(releaseId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
            reader.GetFieldValue<DateOnly>(3), reader.GetFieldValue<DateOnly>(4),
            reader.GetInt32(5), reader.GetString(6), reader.GetString(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9));
    }

    private static async Task<IReadOnlyList<SourceRow>> ReadSourcesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid releaseId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT source_id,source_kind,source_role,source_uri,media_type,retrieved_at,
                   raw_sha256,snapshot_path,source_year,source_month
              FROM market_calendar_release_sources
             WHERE release_id=$1
             ORDER BY source_id COLLATE "C"
            """, connection, transaction);
        command.Parameters.AddWithValue(releaseId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<SourceRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9)));
        return rows;
    }

    private static IReadOnlyList<CalendarDayRow> ParseDays(byte[] content, string calendarCode)
    {
        var lines = System.Text.Encoding.UTF8.GetString(content).Split('\n');
        if (lines.Length < 3 || lines[0] != "calendar_code,date,observation_expected,market_state,reason_code,evidence_raw_sha256"
            || lines[^1].Length != 0)
            throw new CalendarDataException("normalized_csv_invalid");
        var rows = new List<CalendarDayRow>(lines.Length - 2);
        foreach (var line in lines.Skip(1).SkipLast(1))
        {
            var fields = line.Split(',');
            if (fields.Length != 6 || fields[0] != calendarCode
                || !DateOnly.TryParseExact(fields[1], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date)
                || fields[2] is not ("true" or "false"))
                throw new CalendarDataException("normalized_csv_invalid", line);
            rows.Add(new(date, fields[2] == "true", fields[3], fields[4], fields[5]));
        }
        return rows;
    }

    private static DateOnly ParseDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date : throw new CalendarDataException("coverage_date_invalid", value);

    private static DateTimeOffset ParseRetrievedAt(string value) =>
        DateTimeOffset.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static DateTimeOffset LatestRetrievedAt(IEnumerable<SourceDefinition> sources) =>
        sources.Select(source => ParseRetrievedAt(source.RetrievedAt)).Max();

    private static string Result(
        string outcome, CalendarReleaseCommand options, NormalizedCalendar normalized) =>
        $"{outcome}: calendar={options.CalendarCode}; release={options.ReleaseId:D}; rows={normalized.RowCount}; normalized_sha256={normalized.NormalizedSha256}; source_bundle_sha256={normalized.SourceBundleSha256}";

    private sealed record CalendarDayRow(
        DateOnly Date, bool ObservationExpected, string MarketState,
        string ReasonCode, string EvidenceRawSha256);
    private sealed record ReleaseRow(
        string CalendarCode, string SnapshotSetId, int ReleaseVersion, DateOnly CoverageFrom,
        DateOnly CoverageThrough, int RowCount, string NormalizedSha256,
        string SourceBundleSha256, DateTimeOffset ReleasedAt, DateTimeOffset? SealedAt);
    private sealed record SourceRow(
        string SourceId, string SourceKind, string SourceRole, string SourceUri,
        string MediaType, DateTimeOffset RetrievedAt, string RawSha256, string SnapshotPath,
        int? SourceYear, int? SourceMonth);
}
