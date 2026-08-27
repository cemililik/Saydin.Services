using Microsoft.Extensions.Configuration;
using Npgsql;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Extensions;
using Saydin.PriceIngestion.Repositories;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.PriceIngestion.Workers;

public sealed class EvdsInflationWorker(
    IInflationAdapter adapter,
    IIngestionWindowRepository windows,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<EvdsInflationWorker> logger)
{
    private static readonly DateOnly BackfillStartDate = new(2003, 1, 1);
    private const int BackfillChunkMonths = 60;
    private const int ContractVersion = 1;
    private const string ConfigKey = "IngestionWorkers:EvdsInflation";
    private readonly string _leaseOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.CreateVersion7():N}";

    private int MonthlyRunDay => configuration.GetValue<int?>($"{ConfigKey}:MonthlyRunDay") ?? 3;
    private TimeOnly MonthlyRunUtcTime => new(
        configuration.GetValue<int?>($"{ConfigKey}:DailyRunUtcHour") ?? 10,
        configuration.GetValue<int?>($"{ConfigKey}:DailyRunUtcMinute") ?? 0);
    private TimeSpan LogicalRetryDelay => TimeSpan.FromMinutes(30);
    private TimeSpan LeaseDuration => TimeSpan.FromMinutes(30);
    private TimeSpan ProviderDeadline => HttpResilienceExtensions.TotalRequestTimeout;
    private TimeSpan FailureFinalizeTimeout => TimeSpan.FromMilliseconds(
        configuration.GetValue<int?>($"{ConfigKey}:FailureFinalizeTimeoutMs") ?? 5_000);

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
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var target = new DateOnly(now.Year, now.Month, 1).AddMonths(-1);
        var scope = Scope(IngestionJobTypes.InflationBackfill);
        await windows.PlanWindowsAsync(scope, BackfillStartDate, target,
            BackfillChunkMonths, IngestionCadence.Monthly, ct);
        var result = await DrainAsync(scope, ct);
        return result.NextWakeAt is { } due ? new WorkerPass(due) : WorkerPass.Empty;
    }

    internal async Task<bool> RunBackfillChunksAsync(
        IReadOnlyList<(DateOnly From, DateOnly To)> chunks,
        CancellationToken ct)
    {
        var scope = Scope(IngestionJobTypes.InflationBackfill);
        await windows.EnsureWindowsAsync(scope,
            chunks.Select(chunk => new IngestionWindowRange(chunk.From, chunk.To)).ToArray(), ct);
        return (await DrainAsync(scope, ct)).Disposition == DrainDisposition.Complete;
    }

    internal static IReadOnlyList<(DateOnly From, DateOnly To)> ComputeBackfillChunks(
        DateOnly from, DateOnly to, int chunkMonths)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkMonths, 1);
        var chunks = new List<(DateOnly, DateOnly)>();
        for (var start = from; start <= to; start = start.AddMonths(chunkMonths))
        {
            var end = start.AddMonths(chunkMonths - 1);
            if (end > to) end = to;
            chunks.Add((start, end));
        }
        return chunks;
    }

    private async Task<DrainResult> DrainAsync(IngestionWindowScope scope, CancellationToken ct)
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
                case WindowClaimStatus.PermanentBlocked:
                    RecordPermanentBlocked(result.OutcomeCode ?? "permanent_failed");
                    return DrainResult.PermanentBlocked;
                case WindowClaimStatus.Claimed:
                    if (await ProcessClaimAsync(result.Claim!, ct) is { } terminal)
                        return terminal;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        ct.ThrowIfCancellationRequested();
        return DrainResult.Deferred(timeProvider.GetUtcNow().Add(LogicalRetryDelay));
    }

    private async Task<DrainResult?> ProcessClaimAsync(
        IngestionWindowClaim claim, CancellationToken ct)
    {
        AdapterOutcome<InflationRate> outcome;
        try
        {
            outcome = await WithLeaseRenewalAsync(claim,
                token => adapter.FetchRangeAsync(claim.From, claim.To, token), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await MarkCancelledBoundedAsync(claim);
            throw;
        }
        catch (IngestionLeaseLostException)
        {
            throw;
        }
        catch (ProviderDeadlineExceededException)
        {
            return await PersistFailureAsync(claim,
                AdapterOutcome<InflationRate>.RetryableFailure("provider_deadline"), ct);
        }
        catch (Exception ex)
        {
            var retryable = ProviderFailureClassifier.IsRetryable(ex);
            var retryAt = RetryAt(claim);
            var detail = ProviderExceptionSanitizer.Detail(ex);
            logger.LogError(ProviderExceptionSanitizer.ForLog(ex),
                "EVDS adapter exception {ExceptionType} ({From}..{To})",
                ex.GetType().Name, claim.From, claim.To);
            using var finalize = new CancellationTokenSource(FailureFinalizeTimeout);
            try
            {
                await windows.RecordFailureAsync(claim,
                    retryable ? IngestionWindowStates.RetryableFailed : IngestionWindowStates.PermanentFailed,
                    retryable ? AdapterOutcomeKind.RetryableFailure : AdapterOutcomeKind.PermanentFailure,
                    EmptyCounts(claim),
                    retryable ? "adapter_exception_retryable" : "adapter_exception_permanent",
                    retryable ? "adapter_transient" : "adapter_unhandled", detail,
                    retryAt, finalize.Token);
            }
            catch (Exception finalizeError)
            {
                logger.LogWarning(ProviderExceptionSanitizer.ForLog(finalizeError),
                    "EVDS failure terminalization başarısız; lease expiry reclaim edecek: {WindowId}",
                    claim.WindowId);
                return DrainResult.Deferred(timeProvider.GetUtcNow().Add(LeaseDuration));
            }
            if (retryable)
                return DrainResult.Deferred(retryAt);
            RecordPermanentBlocked("adapter_exception_permanent");
            return DrainResult.PermanentBlocked;
        }

        if (outcome.IsFailure)
            return await PersistFailureAsync(claim, outcome, ct);

        if (!TryValidateSuccess(claim, outcome, out var counts))
        {
            var rejected = AdapterOutcome<InflationRate>.PartialRejected(
                outcome.Records, Math.Max(outcome.RawItemCount, outcome.Records.Count),
                Math.Max(1, outcome.RejectedCount), "worker_month_completeness_rejected");
            return await PersistFailureAsync(claim, rejected, ct);
        }

        try
        {
            await windows.CompleteInflationAsync(claim, outcome, counts, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await MarkCancelledBoundedAsync(claim);
            throw;
        }
        catch (PostgresException ex) when (
            ex.SqlState == PostgresErrorCodes.CheckViolation
            && ex.ConstraintName == "chk_inflation_rates_authority_immutable")
        {
            return await PersistFailureAsync(claim,
                AdapterOutcome<InflationRate>.PermanentFailure("provider_revision_conflict"), ct);
        }
        catch
        {
            if (await windows.GetTerminalStateAsync(claim.WindowId, CancellationToken.None) is not null)
                return null;
            throw;
        }
        return null;
    }

    private async Task<DrainResult> PersistFailureAsync(
        IngestionWindowClaim claim, AdapterOutcome<InflationRate> outcome, CancellationToken ct)
    {
        var retryable = outcome.Kind == AdapterOutcomeKind.RetryableFailure;
        var nextAttemptAt = retryable ? RetryAt(claim) : timeProvider.GetUtcNow();
        await windows.RecordFailureAsync(claim,
            retryable ? IngestionWindowStates.RetryableFailed : IngestionWindowStates.PermanentFailed,
            outcome.Kind, FailureCounts(claim, outcome), outcome.Code, outcome.Code, outcome.Detail,
            nextAttemptAt, ct);
        if (retryable)
            return DrainResult.Deferred(nextAttemptAt);
        RecordPermanentBlocked(outcome.Code);
        return DrainResult.PermanentBlocked;
    }

    private void RecordPermanentBlocked(string outcomeCode) =>
        logger.LogCritical(
            "EVDS permanent ingestion scope izole edildi; sibling worker'lar devam edecek: code={Code}",
            outcomeCode);

    private DateTimeOffset RetryAt(IngestionWindowClaim claim) =>
        timeProvider.GetUtcNow().Add(IngestionRetryBackoff.Calculate(
            LogicalRetryDelay, claim.AttemptCount, claim.WindowId));

    private async Task MarkCancelledBoundedAsync(IngestionWindowClaim claim)
    {
        using var finalize = new CancellationTokenSource(FailureFinalizeTimeout);
        try
        {
            await windows.RecordFailureAsync(claim, IngestionWindowStates.Cancelled,
                AdapterOutcomeKind.Cancelled, EmptyCounts(claim), "cancelled", "cancelled", null,
                timeProvider.GetUtcNow(), finalize.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "EVDS cancellation terminalization başarısız; lease expiry reclaim edecek: {WindowId}",
                claim.WindowId);
        }
    }

    private static bool TryValidateSuccess(
        IngestionWindowClaim claim,
        AdapterOutcome<InflationRate> outcome,
        out IngestionWindowCounts counts)
    {
        var expected = Months(claim.From, claim.To).ToHashSet();
        var dates = outcome.Records.Select(rate => rate.PeriodDate).ToArray();
        var distinct = dates.ToHashSet();
        var valid = outcome.Kind == AdapterOutcomeKind.Data
            && outcome.ExpectedNoDataDates.Count == 0
            && outcome.RejectedCount == 0
            && outcome.Records.All(rate => rate.Source == Saydin.Shared.Constants.InflationSources.Tuik)
            && dates.Length == distinct.Count
            && distinct.SetEquals(expected)
            && outcome.RawItemCount >= distinct.Count;
        counts = new IngestionWindowCounts(expected.Count, expected.Count,
            Math.Max(outcome.RawItemCount, distinct.Count), distinct.Count,
            valid ? 0 : Math.Max(1, outcome.RejectedCount), 0);
        return valid;
    }

    private static IngestionWindowCounts FailureCounts(
        IngestionWindowClaim claim, AdapterOutcome<InflationRate> outcome)
    {
        var requested = Months(claim.From, claim.To).Count;
        var distinct = outcome.Records.Select(rate => rate.PeriodDate).Distinct().Count();
        return new IngestionWindowCounts(requested, requested,
            Math.Max(outcome.RawItemCount, distinct), distinct,
            Math.Max(outcome.RejectedCount, outcome.Kind == AdapterOutcomeKind.PartialRejected ? 1 : 0), 0);
    }

    private static IngestionWindowCounts EmptyCounts(IngestionWindowClaim claim)
    {
        var count = Months(claim.From, claim.To).Count;
        return new IngestionWindowCounts(count, count, 0, 0, 0, 0);
    }

    private static IReadOnlyList<DateOnly> Months(DateOnly from, DateOnly to)
    {
        var months = new List<DateOnly>();
        for (var month = new DateOnly(from.Year, from.Month, 1);
             month <= new DateOnly(to.Year, to.Month, 1);
             month = month.AddMonths(1)) months.Add(month);
        return months;
    }

    private IngestionWindowScope Scope(string jobType) =>
        new(adapter.Source, null, jobType, ContractVersion);

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
                linked.Cancel();
                _ = ObserveDetachedAsync(operationTask, claim.WindowId);
                throw new ProviderDeadlineExceededException(claim.WindowId);
            }
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
                            "EVDS lease kaybı sonrası provider task gözlemlendi: {WindowId}", claim.WindowId);
                    }
                    throw ex is IngestionLeaseLostException
                        ? ex : new IngestionLeaseLostException(claim.WindowId, ex);
                }
            }
            return await operationTask;
        }
        finally
        {
            deadlineCancellation.Cancel();
            linked.Cancel();
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
                "EVDS provider deadline sonrası task gözlemlendi: {WindowId}", windowId);
        }
    }

    private async Task RenewUntilCancelledAsync(IngestionWindowClaim claim, CancellationToken ct)
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
                        "EVDS lease renewal geçici hata; tekrar deneniyor: {WindowId} attempt={Attempt}",
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

    private TimeSpan GetDelayUntilNextRun(DateTimeOffset? nextWakeAt)
    {
        var now = timeProvider.GetUtcNow();
        var utcNow = now.UtcDateTime;
        var day = Math.Min(MonthlyRunDay, DateTime.DaysInMonth(utcNow.Year, utcNow.Month));
        var run = new DateTime(utcNow.Year, utcNow.Month, day,
            MonthlyRunUtcTime.Hour, MonthlyRunUtcTime.Minute, 0, DateTimeKind.Utc);
        TimeSpan scheduled;
        if (utcNow < run) scheduled = run - utcNow;
        else
        {
            var next = utcNow.AddMonths(1);
            day = Math.Min(MonthlyRunDay, DateTime.DaysInMonth(next.Year, next.Month));
            run = new DateTime(next.Year, next.Month, day,
                MonthlyRunUtcTime.Hour, MonthlyRunUtcTime.Minute, 0, DateTimeKind.Utc);
            scheduled = run - utcNow;
        }
        if (nextWakeAt is null) return scheduled;
        var due = nextWakeAt.Value - now;
        if (due <= TimeSpan.Zero) due = TimeSpan.FromMilliseconds(1);
        return due < scheduled ? due : scheduled;
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
    }
}
