using Saydin.PriceIngestion.Adapters;
using Saydin.Shared.Diagnostics;

namespace Saydin.PriceIngestion.Repositories;

public sealed record ExpectedFreshnessStream(string Source, IngestionCadence Cadence);

public interface IIngestionFreshnessTelemetry
{
    void RecordStarted(string claimSource, IngestionCadence cadence, DateTimeOffset startedAt);

    void RecordTerminal(
        string source,
        IngestionCadence cadence,
        AdapterOutcomeKind kind,
        IngestionWindowCounts counts,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        DateOnly dataThrough,
        bool authoritativeSuccess);

    void PublishState(
        IngestionFreshnessState state,
        IReadOnlyCollection<ExpectedFreshnessStream> expectedStreams);
}

public sealed class NoopIngestionFreshnessTelemetry : IIngestionFreshnessTelemetry
{
    public void RecordStarted(string claimSource, IngestionCadence cadence, DateTimeOffset startedAt) { }

    public void RecordTerminal(
        string source, IngestionCadence cadence, AdapterOutcomeKind kind,
        IngestionWindowCounts counts, DateTimeOffset startedAt, DateTimeOffset finishedAt,
        DateOnly dataThrough, bool authoritativeSuccess) { }

    public void PublishState(
        IngestionFreshnessState state,
        IReadOnlyCollection<ExpectedFreshnessStream> expectedStreams) { }
}

public sealed class IngestionFreshnessTelemetry : IIngestionFreshnessTelemetry
{
    private static readonly IReadOnlySet<string> Sources = new HashSet<string>(StringComparer.Ordinal)
    {
        "tcmb", "coingecko", "openexchangerates", "twelvedata", "evds",
    };

    public void RecordStarted(string claimSource, IngestionCadence cadence, DateTimeOffset startedAt)
    {
        if (!Sources.Contains(claimSource)) return;
        SaydinMetrics.IngestionLastAttemptTimestamp.Record(
            startedAt.ToUnixTimeSeconds(), Tags(claimSource, cadence));
    }

    public void RecordTerminal(
        string source,
        IngestionCadence cadence,
        AdapterOutcomeKind kind,
        IngestionWindowCounts counts,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        DateOnly dataThrough,
        bool authoritativeSuccess)
    {
        if (!Sources.Contains(source)) return;
        var tags = Tags(source, cadence);
        var outcome = Outcome(kind, authoritativeSuccess);
        SaydinMetrics.IngestionAttempts.Add(1,
            tags[0], tags[1], new("outcome", outcome));
        SaydinMetrics.IngestionAttemptDuration.Record(
            Math.Max(0, (finishedAt - startedAt).TotalSeconds),
            tags[0], tags[1], new("outcome", outcome));
        SaydinMetrics.IngestionRecords.Record(
            counts.AcceptedDistinctCount,
            tags[0], tags[1], new("result", "accepted"));
        SaydinMetrics.IngestionRecords.Record(
            counts.RejectedCount,
            tags[0], tags[1], new("result", "rejected"));
        SaydinMetrics.IngestionLastAttemptTimestamp.Record(
            startedAt.ToUnixTimeSeconds(), tags);

        // Cross-asset last-success/lag/failure gauges are emitted only by the durable
        // hydration query. Recording one asset's success here could temporarily mask
        // another active asset's stale scope.
    }

    public void PublishState(
        IngestionFreshnessState state,
        IReadOnlyCollection<ExpectedFreshnessStream> expectedStreams)
    {
        var expected = expectedStreams
            .Where(stream => Sources.Contains(stream.Source))
            .ToDictionary(stream => (stream.Source, stream.Cadence));
        var durable = state.Streams
            .Where(stream => expected.ContainsKey((stream.Source, stream.Cadence)))
            .ToDictionary(stream => (stream.Source, stream.Cadence));
        var streams = expected.Values.Select(stream =>
            durable.GetValueOrDefault((stream.Source, stream.Cadence))
            ?? new IngestionFreshnessSnapshot(
                stream.Source, stream.Cadence, null, null, null, 0));

        foreach (var stream in streams)
        {
            var tags = Tags(stream.Source, stream.Cadence);
            SaydinMetrics.IngestionLastAttemptTimestamp.Record(
                stream.LastAttemptAt?.ToUnixTimeSeconds() ?? 0, tags);
            SaydinMetrics.IngestionLastSuccessTimestamp.Record(
                stream.LastSuccessAt?.ToUnixTimeSeconds() ?? 0, tags);
            SaydinMetrics.IngestionLag.Record(
                stream.DataThrough is { } through
                    ? LagSeconds(state.DatabaseNow, through, stream.Cadence)
                    : state.DatabaseNow.ToUnixTimeSeconds(), tags);
            SaydinMetrics.IngestionFailureStreak.Record(stream.FailureStreak, tags);
        }

        var coverage = state.Calendars.ToDictionary(item => item.CalendarCode, StringComparer.Ordinal);
        RecordCalendarHorizon(
            "tcmb_indicative_fx", "yesterday",
            coverage.GetValueOrDefault("tcmb_indicative_fx")?.CoverageThrough,
            state.DatabaseNow, yesterday: true);
        RecordCalendarHorizon(
            "bist_pay_xist", "45_day",
            coverage.GetValueOrDefault("bist_pay_xist")?.CoverageThrough,
            state.DatabaseNow, yesterday: false);
    }

    private static void RecordCalendarHorizon(
        string calendar, string requirement, DateOnly? coverageThrough,
        DateTimeOffset now, bool yesterday)
    {
        var istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, istanbul).DateTime);
        var requiredDate = yesterday ? localDate.AddDays(-1) : localDate;
        var horizon = coverageThrough is { } through
            ? through.DayNumber - requiredDate.DayNumber
            : -1_000_000;
        SaydinMetrics.MarketCalendarCoverageHorizon.Record(horizon,
            new("calendar", calendar), new("requirement", requirement));
    }

    private static long LagSeconds(
        DateTimeOffset now, DateOnly dataThrough, IngestionCadence cadence)
    {
        var through = cadence == IngestionCadence.Monthly
            ? new DateOnly(dataThrough.Year, dataThrough.Month,
                DateTime.DaysInMonth(dataThrough.Year, dataThrough.Month))
            : dataThrough;
        var end = new DateTimeOffset(
            through.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc));
        return Math.Max(0, (long)(now - end).TotalSeconds);
    }

    private static KeyValuePair<string, object?>[] Tags(
        string source, IngestionCadence cadence) =>
        [new("source", source), new("cadence", cadence == IngestionCadence.Daily ? "daily" : "monthly")];

    private static string Outcome(AdapterOutcomeKind kind, bool authoritativeSuccess) =>
        authoritativeSuccess ? "success" : kind switch
        {
            AdapterOutcomeKind.RetryableFailure => "retryable_failure",
            AdapterOutcomeKind.PermanentFailure => "permanent_failure",
            AdapterOutcomeKind.PartialRejected => "partial_rejected",
            AdapterOutcomeKind.Cancelled => "cancelled",
            AdapterOutcomeKind.Abandoned => "abandoned",
            _ => "rejected",
        };
}
