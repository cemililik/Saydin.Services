using Saydin.PriceIngestion.Adapters;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Repositories;

public enum IngestionCadence { Daily, Monthly }

public sealed record IngestionWindowScope(
    string Source,
    Guid? AssetId,
    string JobType,
    int ContractVersion);

public sealed record IngestionWindowRange(DateOnly From, DateOnly To);

public enum WindowClaimStatus
{
    Claimed,
    Complete,
    Busy,
    NotDue,
    PermanentBlocked,
    CalendarNotReady,
}

public sealed record IngestionWindowClaim(
    Guid WindowId,
    Guid JobId,
    IngestionWindowScope Scope,
    DateOnly From,
    DateOnly To,
    string LeaseOwner,
    Guid LeaseToken,
    int AttemptCount,
    Guid? CalendarReleaseId = null);

public sealed record WindowClaimResult(
    WindowClaimStatus Status,
    IngestionWindowClaim? Claim = null,
    DateTimeOffset? NextAttemptAt = null,
    string? OutcomeCode = null);

public sealed record IngestionWindowCounts(
    int RequestedCalendarCount,
    int ExpectedObservationCount,
    int RawItemCount,
    int AcceptedDistinctCount,
    int RejectedCount,
    int ExpectedNoDataCount);

public sealed record WindowTerminalState(string State, string? OutcomeCode);

public sealed record IngestionFreshnessSnapshot(
    string Source,
    IngestionCadence Cadence,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    DateOnly? DataThrough,
    long FailureStreak);

public sealed record MarketCalendarCoverageSnapshot(
    string CalendarCode,
    DateOnly CoverageThrough);

public sealed record IngestionFreshnessState(
    DateTimeOffset DatabaseNow,
    IReadOnlyList<IngestionFreshnessSnapshot> Streams,
    IReadOnlyList<MarketCalendarCoverageSnapshot> Calendars);

public sealed record MarketCalendarReadiness(
    bool Required,
    bool Ready,
    Guid? ReleaseId,
    string? CalendarCode,
    string OutcomeCode)
{
    public static MarketCalendarReadiness NotRequired { get; } =
        new(false, true, null, null, "calendar_not_required");
}

public sealed class CalendarNotReadyException(string outcomeCode)
    : InvalidOperationException($"Authoritative market calendar ready değil: {outcomeCode}")
{
    public string OutcomeCode { get; } = outcomeCode;
}

public interface IIngestionWindowRepository
{
    Task<IngestionFreshnessState> ReadFreshnessStateAsync(CancellationToken ct) =>
        Task.FromResult(new IngestionFreshnessState(
            DateTimeOffset.UtcNow, [], []));

    Task<MarketCalendarReadiness> CheckCalendarReadinessAsync(
        IngestionWindowScope scope,
        DateOnly from,
        DateOnly to,
        CancellationToken ct) => Task.FromResult(MarketCalendarReadiness.NotRequired);

    Task<IReadOnlySet<DateOnly>> GetExpectedNoDataDatesAsync(
        Guid releaseId,
        DateOnly from,
        DateOnly to,
        CancellationToken ct) => Task.FromResult<IReadOnlySet<DateOnly>>(new HashSet<DateOnly>());

    Task PlanWindowsAsync(
        IngestionWindowScope scope,
        DateOnly start,
        DateOnly end,
        int chunkSize,
        IngestionCadence cadence,
        CancellationToken ct);

    Task EnsureWindowsAsync(
        IngestionWindowScope scope,
        IReadOnlyList<IngestionWindowRange> ranges,
        CancellationToken ct);

    Task<WindowClaimResult> ClaimNextAsync(
        IngestionWindowScope scope,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken ct);

    Task<bool> RenewLeaseAsync(
        IngestionWindowClaim claim,
        TimeSpan leaseDuration,
        CancellationToken ct);

    Task CompletePriceAsync(
        IngestionWindowClaim claim,
        AdapterOutcome<PricePoint> outcome,
        IngestionWindowCounts counts,
        CancellationToken ct);

    Task CompleteInflationAsync(
        IngestionWindowClaim claim,
        AdapterOutcome<InflationRate> outcome,
        IngestionWindowCounts counts,
        CancellationToken ct);

    Task RecordFailureAsync(
        IngestionWindowClaim claim,
        string state,
        AdapterOutcomeKind kind,
        IngestionWindowCounts counts,
        string outcomeCode,
        string errorCode,
        string? detail,
        DateTimeOffset nextAttemptAt,
        CancellationToken ct);

    Task<WindowTerminalState?> GetTerminalStateAsync(Guid windowId, CancellationToken ct);

    Task RequeuePermanentAsync(Guid windowId, DateTimeOffset nextAttemptAt, CancellationToken ct);
}

public interface IIngestionPersistenceFaultInjector
{
    Task BeforeCommitAsync(Guid windowId, CancellationToken ct);
    Task AfterCommitAsync(Guid windowId, CancellationToken ct);
}

public sealed class NoopIngestionPersistenceFaultInjector : IIngestionPersistenceFaultInjector
{
    public Task BeforeCommitAsync(Guid windowId, CancellationToken ct) => Task.CompletedTask;
    public Task AfterCommitAsync(Guid windowId, CancellationToken ct) => Task.CompletedTask;
}

public sealed class IngestionLeaseLostException(Guid windowId, Exception? innerException = null)
    : InvalidOperationException($"Ingestion lease kaybedildi: {windowId}", innerException);
