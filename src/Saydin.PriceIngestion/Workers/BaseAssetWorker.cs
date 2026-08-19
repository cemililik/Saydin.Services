using Microsoft.Extensions.Configuration;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;
using Saydin.Shared.Diagnostics;

namespace Saydin.PriceIngestion.Workers;

/// <summary>Durable ingestion-window ledger tabanlı ortak price worker.</summary>
public abstract class BaseAssetWorker(
    IExternalPriceAdapter adapter,
    IPriceIngestionRepository assets,
    IIngestionWindowRepository windows,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger logger)
{
    private readonly string _leaseOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.CreateVersion7():N}";
    protected IConfiguration Configuration => configuration;

    protected abstract DateOnly BackfillStartDate { get; }
    protected abstract int ChunkDays { get; }
    protected abstract string WorkerConfigKey { get; }
    protected abstract TimeOnly DefaultDailyRunUtcTime { get; }
    protected virtual int ContractVersion => 1;
    protected virtual TimeSpan ChunkDelay => TimeSpan.Zero;
    protected virtual TimeSpan LogicalRetryDelay => TimeSpan.FromMinutes(5);
    protected virtual TimeSpan LeaseDuration => TimeSpan.FromMinutes(30);
    protected virtual TimeSpan FailureFinalizeTimeout => TimeSpan.FromSeconds(5);
    protected virtual DateOnly TargetDate(DateTime utcNow) => DateOnly.FromDateTime(utcNow.Date);
    protected virtual DateOnly BackfillThrough(DateTimeOffset utcNow) =>
        DateOnly.FromDateTime(utcNow.UtcDateTime.Date.AddDays(-1));
    internal DateOnly ResolveTargetDate(DateTime utcNow) => TargetDate(utcNow);

    private TimeOnly DailyRunUtcTime
    {
        get
        {
            var section = configuration.GetSection($"IngestionWorkers:{WorkerConfigKey}");
            return new TimeOnly(
                section.GetValue<int?>("DailyRunUtcHour") ?? DefaultDailyRunUtcTime.Hour,
                section.GetValue<int?>("DailyRunUtcMinute") ?? DefaultDailyRunUtcTime.Minute);
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !await BackfillAsync(ct))
            await DelayUntilRetryAsync(ct);

        if (!ct.IsCancellationRequested && IsScheduledTimePassedToday())
            await FetchDailyAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(GetDelayUntilNextRun(), timeProvider, ct);
                if (await BackfillAsync(ct))
                    await FetchDailyAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<bool> BackfillAsync(CancellationToken ct)
    {
        var activeAssets = await assets.GetActiveAssetsBySourceAsync(adapter.Source, ct);
        var completedDay = BackfillThrough(timeProvider.GetUtcNow());
        foreach (var asset in activeAssets)
        {
            var scope = Scope(asset, IngestionJobTypes.HistoricalBackfill);
            if (!await EnsureCalendarReadyAsync(scope, BackfillStartDate, completedDay, ct))
                return false;
            await windows.PlanWindowsAsync(scope, BackfillStartDate, completedDay,
                ChunkDays, IngestionCadence.Daily, ct);
            if (!await DrainAsync(asset, scope, ct))
                return false;
        }
        return true;
    }

    internal async Task<bool> BackfillChunkedAsync(
        Asset asset, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var ranges = new List<IngestionWindowRange>();
        for (var start = from; start <= to; start = start.AddDays(ChunkDays))
        {
            var end = start.AddDays(ChunkDays - 1);
            if (end > to) end = to;
            ranges.Add(new IngestionWindowRange(start, end));
        }
        var scope = Scope(asset, IngestionJobTypes.HistoricalBackfill);
        if (!await EnsureCalendarReadyAsync(scope, from, to, ct))
            return false;
        await windows.EnsureWindowsAsync(scope, ranges, ct);
        return await DrainAsync(asset, scope, ct);
    }

    private async Task FetchDailyAsync(CancellationToken ct)
    {
        var target = TargetDate(timeProvider.GetUtcNow().UtcDateTime);
        var activeAssets = await assets.GetActiveAssetsBySourceAsync(adapter.Source, ct);
        foreach (var asset in activeAssets)
        {
            var scope = Scope(asset, IngestionJobTypes.DailyUpdate);
            if (!await EnsureCalendarReadyAsync(scope, target, target, ct))
                return;
            await windows.EnsureWindowsAsync(scope, [new IngestionWindowRange(target, target)], ct);
            if (!await DrainAsync(asset, scope, ct)) return;
        }
    }

    private async Task<bool> DrainAsync(Asset asset, IngestionWindowScope scope, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var result = await windows.ClaimNextAsync(scope, _leaseOwner, LeaseDuration, ct);
            switch (result.Status)
            {
                case WindowClaimStatus.Complete:
                    return true;
                case WindowClaimStatus.Busy:
                case WindowClaimStatus.NotDue:
                    return false;
                case WindowClaimStatus.CalendarNotReady:
                    RecordCalendarNotReady(result.OutcomeCode ?? "calendar_not_ready");
                    return false;
                case WindowClaimStatus.PermanentBlocked:
                    throw new PermanentIngestionWindowException(
                        adapter.Source, asset.Id, default, default,
                        result.OutcomeCode ?? "permanent_failed");
                case WindowClaimStatus.Claimed:
                    if (!await ProcessClaimAsync(asset, result.Claim!, ct)) return false;
                    if (ChunkDelay > TimeSpan.Zero)
                        await Task.Delay(ChunkDelay, timeProvider, ct);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        ct.ThrowIfCancellationRequested();
        return false;
    }

    private async Task<bool> ProcessClaimAsync(
        Asset asset, IngestionWindowClaim claim, CancellationToken ct)
    {
        var closed = await GetCalendarClosedDatesAsync(asset, claim, ct);
        AdapterOutcome<PricePoint> outcome;
        try
        {
            outcome = await WithLeaseRenewalAsync(claim, token =>
                adapter.FetchRangeAsync(new PriceFetchRequest(
                    asset.Id, asset.Symbol, asset.SourceId ?? string.Empty,
                    claim.From, claim.To, closed), token), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await MarkCancelledBoundedAsync(claim, closed.Count);
            throw;
        }
        catch (IngestionLeaseLostException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var retryable = ProviderFailureClassifier.IsRetryable(ex);
            logger.LogError("{Source}/{Symbol} adapter exception {ExceptionType} ({From}..{To})",
                adapter.Source, asset.Symbol, ex.GetType().Name, claim.From, claim.To);
            using var finalize = new CancellationTokenSource(FailureFinalizeTimeout);
            await windows.RecordFailureAsync(claim,
                retryable ? IngestionWindowStates.RetryableFailed : IngestionWindowStates.PermanentFailed,
                retryable ? AdapterOutcomeKind.RetryableFailure : AdapterOutcomeKind.PermanentFailure,
                EmptyFailureCounts(claim, closed.Count),
                retryable ? "adapter_exception_retryable" : "adapter_exception_permanent",
                retryable ? "adapter_transient" : "adapter_unhandled", ex.GetType().Name,
                timeProvider.GetUtcNow().Add(LogicalRetryDelay), finalize.Token);
            if (retryable) return false;
            throw;
        }

        if (outcome.IsFailure)
            return await PersistTypedFailureAsync(claim, outcome, closed.Count, ct);

        if (!TryValidateSuccess(asset, claim, closed, outcome, out var counts, out var validationCode))
        {
            var rejected = AdapterOutcome<PricePoint>.PartialRejected(
                outcome.Records, Math.Max(outcome.RawItemCount, outcome.Records.Count),
                Math.Max(1, outcome.RejectedCount), validationCode);
            await PersistTypedFailureAsync(claim, rejected, closed.Count, ct);
            throw new PermanentIngestionWindowException(
                adapter.Source, asset.Id, claim.From, claim.To, validationCode);
        }

        try
        {
            await windows.CompletePriceAsync(claim, outcome, counts, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await MarkCancelledBoundedAsync(claim, closed.Count);
            throw;
        }
        catch
        {
            var terminal = await windows.GetTerminalStateAsync(claim.WindowId, CancellationToken.None);
            if (terminal is not null) return true;
            throw;
        }

        logger.LogInformation("{Source}/{Symbol} tamamlandı: {From}..{To} ({Count})",
            adapter.Source, asset.Symbol, claim.From, claim.To, counts.AcceptedDistinctCount);
        return true;
    }

    private async Task<bool> PersistTypedFailureAsync(
        IngestionWindowClaim claim, AdapterOutcome<PricePoint> outcome,
        int knownNoDataCount, CancellationToken ct)
    {
        var retryable = outcome.Kind == AdapterOutcomeKind.RetryableFailure;
        var state = retryable ? IngestionWindowStates.RetryableFailed : IngestionWindowStates.PermanentFailed;
        await windows.RecordFailureAsync(claim, state, outcome.Kind,
            FailureCounts(claim, outcome, knownNoDataCount), outcome.Code, outcome.Code, outcome.Detail,
            timeProvider.GetUtcNow().Add(retryable ? LogicalRetryDelay : TimeSpan.Zero), ct);
        if (retryable) return false;
        throw new PermanentIngestionWindowException(
            adapter.Source, claim.Scope.AssetId, claim.From, claim.To, outcome.Code);
    }

    private async Task MarkCancelledBoundedAsync(IngestionWindowClaim claim, int expectedNoDataCount)
    {
        using var finalize = new CancellationTokenSource(FailureFinalizeTimeout);
        try
        {
            await windows.RecordFailureAsync(claim, IngestionWindowStates.Cancelled,
                AdapterOutcomeKind.Cancelled, EmptyFailureCounts(claim, expectedNoDataCount),
                "cancelled", "cancelled", null, timeProvider.GetUtcNow(), finalize.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Cancellation terminalization başarısız; lease expiry reclaim edecek: {WindowId}",
                claim.WindowId);
        }
    }

    private async Task<IReadOnlySet<DateOnly>> GetCalendarClosedDatesAsync(
        Asset asset, IngestionWindowClaim claim, CancellationToken ct)
    {
        if (adapter.Source is not ("tcmb" or "twelvedata"))
            return new HashSet<DateOnly>();

        if (claim.Scope.ContractVersion >= 2)
        {
            if (claim.CalendarReleaseId is not { } releaseId)
                throw new CalendarNotReadyException("calendar_release_unbound");
            return await windows.GetExpectedNoDataDatesAsync(
                releaseId, claim.From, claim.To, ct);
        }

        var closed = (await assets.GetMarketHolidaysAsync(
            asset.Id, claim.From, claim.To, ct)).ToHashSet();
        foreach (var date in AdapterCompleteness.Dates(claim.From, claim.To))
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) closed.Add(date);
        return closed;
    }

    private async Task<bool> EnsureCalendarReadyAsync(
        IngestionWindowScope scope, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var readiness = await windows.CheckCalendarReadinessAsync(scope, from, to, ct);
        if (readiness.Ready) return true;
        RecordCalendarNotReady(readiness.OutcomeCode);
        return false;
    }

    private void RecordCalendarNotReady(string reason)
    {
        SaydinMetrics.MarketCalendarNotReady.Add(1,
            new KeyValuePair<string, object?>("source", adapter.Source),
            new KeyValuePair<string, object?>("reason", reason));
        logger.LogWarning(
            "{Source} authoritative calendar hazır değil; provider çağrısı yapılmadı: {Reason}",
            adapter.Source, reason);
    }

    private static bool TryValidateSuccess(
        Asset asset, IngestionWindowClaim claim, IReadOnlySet<DateOnly> closed,
        AdapterOutcome<PricePoint> outcome, out IngestionWindowCounts counts, out string code)
    {
        var requested = AdapterCompleteness.Dates(claim.From, claim.To).ToHashSet();
        var expected = requested.Except(closed).ToHashSet();
        var dates = outcome.Records.Select(point => point.PriceDate).ToArray();
        var distinct = dates.ToHashSet();
        var valid = outcome.ExpectedNoDataDates.SetEquals(closed)
            && outcome.RejectedCount == 0
            && outcome.Records.All(point => point.AssetId == asset.Id)
            && dates.Length == distinct.Count
            && distinct.SetEquals(expected)
            && outcome.RawItemCount >= distinct.Count
            && ((distinct.Count == 0 && outcome.Kind == AdapterOutcomeKind.ExpectedNoData)
                || (distinct.Count > 0 && outcome.Kind == AdapterOutcomeKind.Data));
        code = valid ? outcome.Code : "worker_completeness_rejected";
        counts = new IngestionWindowCounts(
            requested.Count, expected.Count, Math.Max(outcome.RawItemCount, distinct.Count),
            distinct.Count, valid ? 0 : Math.Max(1, outcome.RejectedCount), closed.Count);
        return valid;
    }

    private static IngestionWindowCounts FailureCounts(
        IngestionWindowClaim claim, AdapterOutcome<PricePoint> outcome, int noDataCount)
    {
        var requested = claim.To.DayNumber - claim.From.DayNumber + 1;
        var distinct = outcome.Records.Select(point => point.PriceDate).Distinct().Count();
        return new IngestionWindowCounts(requested, Math.Max(0, requested - noDataCount),
            Math.Max(outcome.RawItemCount, distinct), distinct,
            Math.Max(outcome.RejectedCount, outcome.Kind == AdapterOutcomeKind.PartialRejected ? 1 : 0),
            noDataCount);
    }

    private static IngestionWindowCounts EmptyFailureCounts(
        IngestionWindowClaim claim, int noDataCount)
    {
        var requested = claim.To.DayNumber - claim.From.DayNumber + 1;
        return new IngestionWindowCounts(
            requested, Math.Max(0, requested - noDataCount), 0, 0, 0, noDataCount);
    }

    private IngestionWindowScope Scope(Asset asset, string jobType) =>
        new(adapter.Source, asset.Id, jobType, ContractVersion);

    private bool IsScheduledTimePassedToday()
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return now >= now.Date.Add(DailyRunUtcTime.ToTimeSpan());
    }

    private TimeSpan GetDelayUntilNextRun()
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var today = now.Date.Add(DailyRunUtcTime.ToTimeSpan());
        return now < today ? today - now : today.AddDays(1) - now;
    }

    private Task DelayUntilRetryAsync(CancellationToken ct) => Task.Delay(
        LogicalRetryDelay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : LogicalRetryDelay,
        timeProvider, ct);

    private async Task<T> WithLeaseRenewalAsync<T>(
        IngestionWindowClaim claim,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var operationTask = operation(linked.Token);
        var renewalTask = RenewUntilCancelledAsync(claim, linked.Token);
        try
        {
            var first = await Task.WhenAny(operationTask, renewalTask);
            if (first == renewalTask)
            {
                linked.Cancel();
                try { await renewalTask; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    try { await operationTask; } catch (OperationCanceledException) { }
                    catch (Exception operationError)
                    {
                        logger.LogDebug(operationError,
                            "Lease kaybı sonrası provider task gözlemlendi: {WindowId}", claim.WindowId);
                    }
                    throw ex is IngestionLeaseLostException
                        ? ex : new IngestionLeaseLostException(claim.WindowId, ex);
                }
            }
            return await operationTask;
        }
        finally
        {
            linked.Cancel();
            try { await renewalTask; }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
    }

    private async Task RenewUntilCancelledAsync(
        IngestionWindowClaim claim, CancellationToken ct)
    {
        var interval = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks,
            LeaseDuration.Ticks / 3));
        while (true)
        {
            await Task.Delay(interval, timeProvider, ct);
            try
            {
                if (!await windows.RenewLeaseAsync(claim, LeaseDuration, ct))
                    throw new IngestionLeaseLostException(claim.WindowId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (IngestionLeaseLostException) { throw; }
            catch (Exception ex) { throw new IngestionLeaseLostException(claim.WindowId, ex); }
        }
    }

    internal static List<(DateOnly From, DateOnly To)> ComputeMissingRanges(
        DateOnly from, DateOnly to, IReadOnlySet<DateOnly> existing)
    {
        var result = new List<(DateOnly, DateOnly)>();
        DateOnly? start = null;
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (existing.Contains(date))
            {
                if (start is { } value)
                {
                    result.Add((value, date.AddDays(-1)));
                    start = null;
                }
            }
            else start ??= date;
        }
        if (start is { } last) result.Add((last, to));
        return result;
    }
}
