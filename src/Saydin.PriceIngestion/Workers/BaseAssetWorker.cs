using Microsoft.Extensions.Configuration;
using Npgsql;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Extensions;
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
    protected IIngestionWindowRepository Windows => windows;

    protected abstract DateOnly BackfillStartDate { get; }
    protected abstract int ChunkDays { get; }
    protected abstract string WorkerConfigKey { get; }
    protected abstract TimeOnly DefaultDailyRunUtcTime { get; }
    protected virtual int ContractVersion => 1;
    protected virtual TimeSpan ChunkDelay => TimeSpan.Zero;
    protected virtual TimeSpan LogicalRetryDelay => TimeSpan.FromMinutes(5);
    protected virtual TimeSpan LeaseDuration => TimeSpan.FromMinutes(30);
    protected virtual TimeSpan ProviderDeadline => HttpResilienceExtensions.TotalRequestTimeout;
    protected virtual TimeSpan FailureFinalizeTimeout => TimeSpan.FromSeconds(5);
    protected virtual DateOnly TargetDate(DateTime utcNow) => DateOnly.FromDateTime(utcNow.Date);
    protected virtual DateOnly BackfillThrough(DateTimeOffset utcNow) =>
        DateOnly.FromDateTime(utcNow.UtcDateTime.Date.AddDays(-1));
    internal DateOnly ResolveTargetDate(DateTime utcNow) => TargetDate(utcNow);
    protected virtual Task<MarketCalendarTargetResolution> ResolveBackfillThroughAsync(
        DateTimeOffset utcNow,
        CancellationToken ct) =>
        Task.FromResult(new MarketCalendarTargetResolution(
            true, BackfillThrough(utcNow), null, "calendar_not_required", "calendar_target_ready"));
    internal Task<MarketCalendarTargetResolution> ResolveBackfillThroughForTestAsync(
        DateTimeOffset utcNow,
        CancellationToken ct) => ResolveBackfillThroughAsync(utcNow, ct);

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
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pass = await BackfillAsync(ct);
                await Task.Delay(GetDelayUntilNextRun(pass.NextWakeAt), timeProvider, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<WorkerPass> BackfillAsync(CancellationToken ct)
    {
        var activeAssets = (await assets.GetActiveAssetsBySourceAsync(adapter.Source, ct))
            .OrderBy(asset => asset.Symbol, StringComparer.Ordinal)
            .ThenBy(asset => asset.Id)
            .ToArray();
        var pass = WorkerPass.Empty;
        if (activeAssets.Length == 0) return pass;
        var target = await ResolveBackfillThroughAsync(timeProvider.GetUtcNow(), ct);
        if (!target.Ready || target.TargetDate is not { } completedDay)
        {
            RecordCalendarNotReady(target.OutcomeCode);
            return pass.Include(timeProvider.GetUtcNow().Add(LogicalRetryDelay));
        }
        foreach (var asset in activeAssets)
        {
            var scope = Scope(asset, IngestionJobTypes.HistoricalBackfill);
            if (!await EnsureCalendarReadyAsync(scope, BackfillStartDate, completedDay, ct))
            {
                pass = pass.Include(timeProvider.GetUtcNow().Add(LogicalRetryDelay));
                continue;
            }
            await windows.PlanWindowsAsync(scope, BackfillStartDate, completedDay,
                ChunkDays, IngestionCadence.Daily, ct);
            pass = pass.Include(await DrainAsync(asset, scope, ct));
        }
        return pass;
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
        return (await DrainAsync(asset, scope, ct)).Disposition == DrainDisposition.Complete;
    }

    private async Task<DrainResult> DrainAsync(
        Asset asset, IngestionWindowScope scope, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var result = await windows.ClaimNextAsync(scope, _leaseOwner, LeaseDuration, ct);
            switch (result.Status)
            {
                case WindowClaimStatus.Complete:
                    return DrainResult.Complete;
                case WindowClaimStatus.Busy:
                case WindowClaimStatus.NotDue:
                    return DrainResult.Deferred(result.NextAttemptAt
                        ?? timeProvider.GetUtcNow().Add(LogicalRetryDelay));
                case WindowClaimStatus.CalendarNotReady:
                    RecordCalendarNotReady(result.OutcomeCode ?? "calendar_not_ready");
                    return DrainResult.Deferred(
                        timeProvider.GetUtcNow().Add(LogicalRetryDelay));
                case WindowClaimStatus.PermanentBlocked:
                    RecordPermanentBlocked(asset, scope,
                        result.OutcomeCode ?? "permanent_failed");
                    return DrainResult.PermanentBlocked;
                case WindowClaimStatus.Claimed:
                    if (await ProcessClaimAsync(asset, result.Claim!, ct) is { } terminal)
                        return terminal;
                    if (ChunkDelay > TimeSpan.Zero)
                        await Task.Delay(ChunkDelay, timeProvider, ct);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        ct.ThrowIfCancellationRequested();
        return DrainResult.Deferred(timeProvider.GetUtcNow().Add(LogicalRetryDelay));
    }

    private async Task<DrainResult?> ProcessClaimAsync(
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
        catch (ProviderDeadlineExceededException)
        {
            return await PersistTypedFailureAsync(claim,
                AdapterOutcome<PricePoint>.RetryableFailure("provider_deadline"),
                closed.Count, ct);
        }
        catch (Exception ex)
        {
            var retryable = ProviderFailureClassifier.IsRetryable(ex);
            var retryAt = RetryAt(claim);
            var detail = ProviderExceptionSanitizer.Detail(ex);
            logger.LogError(ProviderExceptionSanitizer.ForLog(ex),
                "{Source}/{Symbol} adapter exception {ExceptionType} ({From}..{To})",
                adapter.Source, asset.Symbol, ex.GetType().Name, claim.From, claim.To);
            using var finalize = new CancellationTokenSource(FailureFinalizeTimeout);
            try
            {
                await windows.RecordFailureAsync(claim,
                    retryable ? IngestionWindowStates.RetryableFailed : IngestionWindowStates.PermanentFailed,
                    retryable ? AdapterOutcomeKind.RetryableFailure : AdapterOutcomeKind.PermanentFailure,
                    EmptyFailureCounts(claim, closed.Count),
                    retryable ? "adapter_exception_retryable" : "adapter_exception_permanent",
                    retryable ? "adapter_transient" : "adapter_unhandled", detail,
                    retryAt, finalize.Token);
            }
            catch (Exception finalizeError)
            {
                logger.LogWarning(ProviderExceptionSanitizer.ForLog(finalizeError),
                    "Failure terminalization başarısız; lease expiry reclaim edecek: {WindowId}",
                    claim.WindowId);
                return DrainResult.Deferred(timeProvider.GetUtcNow().Add(LeaseDuration));
            }
            if (retryable)
                return DrainResult.Deferred(retryAt);
            RecordPermanentBlocked(asset, claim.Scope, "adapter_exception_permanent");
            return DrainResult.PermanentBlocked;
        }

        if (outcome.IsFailure)
            return await PersistTypedFailureAsync(claim, outcome, closed.Count, ct);

        if (!TryValidateSuccess(asset, claim, closed, outcome, out var counts, out var validationCode))
        {
            var rejected = AdapterOutcome<PricePoint>.PartialRejected(
                outcome.Records, Math.Max(outcome.RawItemCount, outcome.Records.Count),
                Math.Max(1, outcome.RejectedCount), validationCode);
            return await PersistTypedFailureAsync(claim, rejected, closed.Count, ct);
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
        catch (PostgresException ex) when (
            ex.SqlState == PostgresErrorCodes.CheckViolation
            && ex.ConstraintName == "chk_price_points_authority_immutable")
        {
            return await PersistTypedFailureAsync(claim,
                AdapterOutcome<PricePoint>.PermanentFailure("provider_revision_conflict"),
                closed.Count, ct);
        }
        catch
        {
            var terminal = await windows.GetTerminalStateAsync(claim.WindowId, CancellationToken.None);
            if (terminal is not null) return null;
            throw;
        }

        logger.LogInformation("{Source}/{Symbol} tamamlandı: {From}..{To} ({Count})",
            adapter.Source, asset.Symbol, claim.From, claim.To, counts.AcceptedDistinctCount);
        return null;
    }

    private async Task<DrainResult> PersistTypedFailureAsync(
        IngestionWindowClaim claim, AdapterOutcome<PricePoint> outcome,
        int knownNoDataCount, CancellationToken ct)
    {
        var retryable = outcome.Kind == AdapterOutcomeKind.RetryableFailure;
        var state = retryable ? IngestionWindowStates.RetryableFailed : IngestionWindowStates.PermanentFailed;
        var nextAttemptAt = retryable ? RetryAt(claim) : timeProvider.GetUtcNow();
        await windows.RecordFailureAsync(claim, state, outcome.Kind,
            FailureCounts(claim, outcome, knownNoDataCount), outcome.Code, outcome.Code, outcome.Detail,
            nextAttemptAt, ct);
        if (retryable)
            return DrainResult.Deferred(nextAttemptAt);
        logger.LogCritical(
            "{Source} permanent ingestion window izole edildi: asset={AssetId} scope={Scope} range={From}..{To} code={Code}",
            adapter.Source, claim.Scope.AssetId, claim.Scope.JobType,
            claim.From, claim.To, outcome.Code);
        return DrainResult.PermanentBlocked;
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

    private DateTimeOffset RetryAt(IngestionWindowClaim claim) =>
        timeProvider.GetUtcNow().Add(IngestionRetryBackoff.Calculate(
            LogicalRetryDelay, claim.AttemptCount, claim.WindowId));

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

    private void RecordPermanentBlocked(
        Asset asset, IngestionWindowScope scope, string outcomeCode) =>
        logger.LogCritical(
            "{Source}/{Symbol} permanent ingestion scope izole edildi; sibling asset ve worker'lar devam edecek: scope={Scope} code={Code}",
            adapter.Source, asset.Symbol, scope.JobType, outcomeCode);

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

    private TimeSpan GetDelayUntilNextRun(DateTimeOffset? nextWakeAt)
    {
        var now = timeProvider.GetUtcNow();
        var utcNow = now.UtcDateTime;
        var today = utcNow.Date.Add(DailyRunUtcTime.ToTimeSpan());
        var scheduled = utcNow < today ? today - utcNow : today.AddDays(1) - utcNow;
        if (nextWakeAt is null) return scheduled;
        var due = nextWakeAt.Value - now;
        if (due <= TimeSpan.Zero) due = TimeSpan.FromMilliseconds(1);
        return due < scheduled ? due : scheduled;
    }

    private async Task<T> WithLeaseRenewalAsync<T>(
        IngestionWindowClaim claim,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var deadlineCancellation = new CancellationTokenSource();
        var operationTask = operation(linked.Token);
        var renewalTask = RenewUntilCancelledAsync(claim, linked.Token);
        var deadlineTask = Task.Delay(
            ProviderDeadline, timeProvider, deadlineCancellation.Token);
        try
        {
            var first = await Task.WhenAny(operationTask, renewalTask, deadlineTask);
            if (first == deadlineTask && !operationTask.IsCompleted)
            {
                await linked.CancelAsync();
                _ = ObserveDetachedAsync(operationTask, claim.WindowId);
                throw new ProviderDeadlineExceededException(claim.WindowId);
            }
            if (first == renewalTask)
            {
                await linked.CancelAsync();
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
            await deadlineCancellation.CancelAsync();
            await linked.CancelAsync();
            try { await renewalTask; }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
    }

    private async Task ObserveDetachedAsync(Task operationTask, Guid windowId)
    {
        try { await operationTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogDebug(ProviderExceptionSanitizer.ForLog(ex),
                "Provider deadline sonrası task gözlemlendi: {WindowId}", windowId);
        }
    }

    private async Task RenewUntilCancelledAsync(
        IngestionWindowClaim claim, CancellationToken ct)
    {
        const int maximumTransientAttempts = 3;
        var interval = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks,
            LeaseDuration.Ticks / 3));
        while (true)
        {
            await Task.Delay(interval, timeProvider, ct);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (!await windows.RenewLeaseAsync(claim, LeaseDuration, ct))
                        throw new IngestionLeaseLostException(claim.WindowId);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (IngestionLeaseLostException) { throw; }
                catch (Exception ex) when (IsTransientLeaseFailure(ex)
                    && attempt < maximumTransientAttempts)
                {
                    logger.LogWarning(ProviderExceptionSanitizer.ForLog(ex),
                        "Lease renewal geçici hata; tekrar deneniyor: {WindowId} attempt={Attempt}",
                        claim.WindowId, attempt);
                    var retryDelay = TimeSpan.FromTicks(Math.Max(1,
                        Math.Min(TimeSpan.FromSeconds(1).Ticks,
                            LeaseDuration.Ticks / 12 * attempt)));
                    await Task.Delay(retryDelay, timeProvider, ct);
                }
                catch (Exception ex) { throw new IngestionLeaseLostException(claim.WindowId, ex); }
            }
        }
    }

    private static bool IsTransientLeaseFailure(Exception exception) =>
        exception is TimeoutException or IOException or System.Net.Sockets.SocketException ||
        exception is NpgsqlException { IsTransient: true };

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

    private enum DrainDisposition { Complete, Deferred, PermanentBlocked }

    private sealed record DrainResult(
        DrainDisposition Disposition, DateTimeOffset? NextWakeAt = null)
    {
        public static DrainResult Complete { get; } = new(DrainDisposition.Complete);
        public static DrainResult PermanentBlocked { get; } = new(DrainDisposition.PermanentBlocked);
        public static DrainResult Deferred(DateTimeOffset nextWakeAt) =>
            new(DrainDisposition.Deferred, nextWakeAt);
    }

    private sealed record WorkerPass(DateTimeOffset? NextWakeAt)
    {
        public static WorkerPass Empty { get; } = new WorkerPass((DateTimeOffset?)null);

        public WorkerPass Include(DrainResult result) =>
            result.NextWakeAt is { } due ? Include(due) : this;

        public WorkerPass Include(DateTimeOffset due) =>
            NextWakeAt is null || due < NextWakeAt.Value ? new(due) : this;
    }
}
