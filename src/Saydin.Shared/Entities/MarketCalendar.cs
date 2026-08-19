namespace Saydin.Shared.Entities;

/// <summary>An authoritative market/publication calendar identity.</summary>
public sealed class MarketCalendar
{
    public string Code { get; init; } = default!;
    public string Authority { get; init; } = default!;
    public string TimeZone { get; init; } = default!;
    public DateTimeOffset CreatedAt { get; init; }
    public ICollection<MarketCalendarRelease> Releases { get; init; } = [];
}

/// <summary>Immutable, content-verified calendar release.</summary>
public sealed class MarketCalendarRelease
{
    public Guid Id { get; init; }
    public string CalendarCode { get; init; } = default!;
    public string SnapshotSetId { get; init; } = default!;
    public int ReleaseVersion { get; init; }
    public DateOnly CoverageFrom { get; init; }
    public DateOnly CoverageThrough { get; init; }
    public int RowCount { get; init; }
    public string NormalizedSha256 { get; init; } = default!;
    public string SourceBundleSha256 { get; init; } = default!;
    public DateTimeOffset ReleasedAt { get; init; }
    public DateTimeOffset? SealedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public MarketCalendar Calendar { get; init; } = default!;
    public ICollection<MarketCalendarReleaseSource> Sources { get; init; } = [];
    public ICollection<MarketCalendarDay> Days { get; init; } = [];
}

/// <summary>Content-addressed authority/discovery evidence for one release.</summary>
public sealed class MarketCalendarReleaseSource
{
    public Guid ReleaseId { get; init; }
    public string SourceId { get; init; } = default!;
    public string SourceKind { get; init; } = default!;
    public string SourceRole { get; init; } = default!;
    public string SourceUri { get; init; } = default!;
    public string MediaType { get; init; } = default!;
    public DateTimeOffset RetrievedAt { get; init; }
    public string RawSha256 { get; init; } = default!;
    public string SnapshotPath { get; init; } = default!;
    public int? SourceYear { get; init; }
    public int? SourceMonth { get; init; }
    public MarketCalendarRelease Release { get; init; } = default!;
    public ICollection<MarketCalendarDay> EvidencedDays { get; init; } = [];
}

/// <summary>One immutable daily observation/session expectation in a release.</summary>
public sealed class MarketCalendarDay
{
    public Guid ReleaseId { get; init; }
    public DateOnly CalendarDate { get; init; }
    public bool ObservationExpected { get; init; }
    public string MarketState { get; init; } = default!;
    public string ReasonCode { get; init; } = default!;
    public string EvidenceRawSha256 { get; init; } = default!;
    public MarketCalendarRelease Release { get; init; } = default!;
    public MarketCalendarReleaseSource EvidenceSource { get; init; } = default!;
}

/// <summary>Current asset/source to authoritative calendar binding.</summary>
public sealed class AssetMarketCalendar
{
    public Guid AssetId { get; init; }
    public string Source { get; init; } = default!;
    public string CalendarCode { get; init; } = default!;
    public DateTimeOffset BoundAt { get; init; }
    public Asset Asset { get; init; } = default!;
    public MarketCalendar Calendar { get; init; } = default!;
}

/// <summary>The deliberately mutable pointer selecting the current release.</summary>
public sealed class MarketCalendarActiveRelease
{
    public string CalendarCode { get; init; } = default!;
    public Guid ReleaseId { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    public MarketCalendarRelease Release { get; init; } = default!;
}
