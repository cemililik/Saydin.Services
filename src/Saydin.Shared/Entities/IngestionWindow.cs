namespace Saydin.Shared.Entities;

/// <summary>Durable logical ingestion range and its lease/retry/completeness state.</summary>
public sealed class IngestionWindow
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public string Source { get; init; } = default!;
    public Guid? AssetId { get; init; }
    public string JobType { get; init; } = default!;
    public DateOnly RangeStart { get; init; }
    public DateOnly RangeEnd { get; init; }
    public int ContractVersion { get; init; }
    public Guid? CalendarReleaseId { get; set; }
    public string State { get; set; } = IngestionWindowStates.Pending;
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public int RequestedCalendarCount { get; set; }
    public int ExpectedObservationCount { get; set; }
    public int RawItemCount { get; set; }
    public int AcceptedDistinctCount { get; set; }
    public int RejectedCount { get; set; }
    public int ExpectedNoDataCount { get; set; }
    public string? OutcomeCode { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public Asset? Asset { get; init; }
    public MarketCalendarRelease? CalendarRelease { get; init; }
}

public static class IngestionWindowStates
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string ExpectedNoData = "expected_no_data";
    public const string RetryableFailed = "retryable_failed";
    public const string PermanentFailed = "permanent_failed";
    public const string Cancelled = "cancelled";
    public const string Abandoned = "abandoned";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Pending, Running, Succeeded, ExpectedNoData, RetryableFailed,
        PermanentFailed, Cancelled, Abandoned,
    };
}
