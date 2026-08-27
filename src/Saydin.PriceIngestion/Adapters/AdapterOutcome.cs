using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Adapters;

public enum AdapterOutcomeKind
{
    Data,
    ExpectedNoData,
    RetryableFailure,
    PermanentFailure,
    PartialRejected,
    Cancelled,
    Abandoned,
}

/// <summary>
/// Provider sonucunu "boş liste = başarı" belirsizliğinden kurtaran typed contract.
/// Completeness'in nihai kararı worker tarafından requested/expected date setleriyle
/// yapılır; raw item sayısı accepted distinct sayısından bağımsızdır.
/// </summary>
public sealed record AdapterOutcome<T>(
    AdapterOutcomeKind Kind,
    IReadOnlyList<T> Records,
    int RawItemCount,
    int RejectedCount,
    IReadOnlySet<DateOnly> ExpectedNoDataDates,
    string Code,
    string? Detail = null)
{
    public bool IsFailure => Kind is AdapterOutcomeKind.RetryableFailure
        or AdapterOutcomeKind.PermanentFailure
        or AdapterOutcomeKind.PartialRejected
        or AdapterOutcomeKind.Cancelled
        or AdapterOutcomeKind.Abandoned;

    public static AdapterOutcome<T> Data(
        IReadOnlyList<T> records,
        int rawItemCount,
        IReadOnlySet<DateOnly>? expectedNoDataDates = null,
        string code = "data_complete") =>
        new(AdapterOutcomeKind.Data, records, rawItemCount, 0,
            expectedNoDataDates ?? EmptyDates, code);

    public static AdapterOutcome<T> ExpectedNoData(
        IReadOnlySet<DateOnly> dates,
        string code,
        int rawItemCount = 0) =>
        new(AdapterOutcomeKind.ExpectedNoData, [], rawItemCount, 0, dates, code);

    public static AdapterOutcome<T> RetryableFailure(
        string code,
        string? detail = null,
        int rawItemCount = 0,
        int rejectedCount = 0) =>
        new(AdapterOutcomeKind.RetryableFailure, [], rawItemCount, rejectedCount,
            EmptyDates, code, detail);

    public static AdapterOutcome<T> PermanentFailure(
        string code,
        string? detail = null,
        int rawItemCount = 0,
        int rejectedCount = 0) =>
        new(AdapterOutcomeKind.PermanentFailure, [], rawItemCount, rejectedCount,
            EmptyDates, code, detail);

    public static AdapterOutcome<T> PartialRejected(
        IReadOnlyList<T> records,
        int rawItemCount,
        int rejectedCount,
        string code,
        string? detail = null,
        IReadOnlySet<DateOnly>? expectedNoDataDates = null) =>
        new(AdapterOutcomeKind.PartialRejected, records, rawItemCount, rejectedCount,
            expectedNoDataDates ?? EmptyDates, code, detail);

    private static readonly IReadOnlySet<DateOnly> EmptyDates = new HashSet<DateOnly>();
}

public sealed record PriceFetchRequest(
    Guid AssetId,
    string AssetSymbol,
    string SourceId,
    DateOnly From,
    DateOnly To,
    IReadOnlySet<DateOnly> CalendarClosedDates);
