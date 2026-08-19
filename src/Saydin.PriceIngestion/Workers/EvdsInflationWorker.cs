using Microsoft.Extensions.Configuration;
using Saydin.PriceIngestion.Adapters;
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
    private TimeSpan FailureFinalizeTimeout => TimeSpan.FromMilliseconds(
        configuration.GetValue<int?>($"{ConfigKey}:FailureFinalizeTimeoutMs") ?? 5_000);

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !await BackfillAsync(ct))
            await Task.Delay(LogicalRetryDelay, timeProvider, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(GetDelayUntilNextRun(), timeProvider, ct);
                if (await BackfillAsync(ct))
                    await FetchLatestAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<bool> BackfillAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var target = new DateOnly(now.Year, now.Month, 1).AddMonths(-1);
        var scope = Scope(IngestionJobTypes.InflationBackfill);
        await windows.PlanWindowsAsync(scope, BackfillStartDate, target,
            BackfillChunkMonths, IngestionCadence.Monthly, ct);
        return await DrainAsync(scope, ct);
    }

    internal async Task<bool> RunBackfillChunksAsync(
        IReadOnlyList<(DateOnly From, DateOnly To)> chunks,
        CancellationToken ct)
    {
        var scope = Scope(IngestionJobTypes.InflationBackfill);
        await windows.EnsureWindowsAsync(scope,
            chunks.Select(chunk => new IngestionWindowRange(chunk.From, chunk.To)).ToArray(), ct);
        return await DrainAsync(scope, ct);
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

    private async Task FetchLatestAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var target = new DateOnly(now.Year, now.Month, 1).AddMonths(-1);
        var scope = Scope(IngestionJobTypes.InflationDaily);
        await windows.EnsureWindowsAsync(scope, [new IngestionWindowRange(target, target)], ct);
        await DrainAsync(scope, ct);
    }

    private async Task<bool> DrainAsync(IngestionWindowScope scope, CancellationToken ct)
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
                case WindowClaimStatus.PermanentBlocked:
                    throw new PermanentIngestionWindowException(
                        adapter.Source, null, default, default,
                        result.OutcomeCode ?? "permanent_failed");
                case WindowClaimStatus.Claimed:
                    if (!await ProcessClaimAsync(result.Claim!, ct)) return false;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        ct.ThrowIfCancellationRequested();
        return false;
    }

    private async Task<bool> ProcessClaimAsync(IngestionWindowClaim claim, CancellationToken ct)
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
        catch (Exception ex)
        {
            var retryable = ProviderFailureClassifier.IsRetryable(ex);
            using var finalize = new CancellationTokenSource(FailureFinalizeTimeout);
            await windows.RecordFailureAsync(claim,
                retryable ? IngestionWindowStates.RetryableFailed : IngestionWindowStates.PermanentFailed,
                retryable ? AdapterOutcomeKind.RetryableFailure : AdapterOutcomeKind.PermanentFailure,
                EmptyCounts(claim),
                retryable ? "adapter_exception_retryable" : "adapter_exception_permanent",
                retryable ? "adapter_transient" : "adapter_unhandled", ex.GetType().Name,
                timeProvider.GetUtcNow().Add(LogicalRetryDelay), finalize.Token);
            if (retryable) return false;
            throw;
        }

        if (outcome.IsFailure)
            return await PersistFailureAsync(claim, outcome, ct);

        if (!TryValidateSuccess(claim, outcome, out var counts))
        {
            var rejected = AdapterOutcome<InflationRate>.PartialRejected(
                outcome.Records, Math.Max(outcome.RawItemCount, outcome.Records.Count),
                Math.Max(1, outcome.RejectedCount), "worker_month_completeness_rejected");
            await PersistFailureAsync(claim, rejected, ct);
            throw new PermanentIngestionWindowException(
                adapter.Source, null, claim.From, claim.To, rejected.Code);
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
        catch
        {
            if (await windows.GetTerminalStateAsync(claim.WindowId, CancellationToken.None) is not null)
                return true;
            throw;
        }
        return true;
    }

    private async Task<bool> PersistFailureAsync(
        IngestionWindowClaim claim, AdapterOutcome<InflationRate> outcome, CancellationToken ct)
    {
        var retryable = outcome.Kind == AdapterOutcomeKind.RetryableFailure;
        await windows.RecordFailureAsync(claim,
            retryable ? IngestionWindowStates.RetryableFailed : IngestionWindowStates.PermanentFailed,
            outcome.Kind, FailureCounts(claim, outcome), outcome.Code, outcome.Code, outcome.Detail,
            timeProvider.GetUtcNow().Add(retryable ? LogicalRetryDelay : TimeSpan.Zero), ct);
        if (retryable) return false;
        throw new PermanentIngestionWindowException(
            adapter.Source, null, claim.From, claim.To, outcome.Code);
    }

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
            linked.Cancel();
            try { await renewalTask; }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
    }

    private async Task RenewUntilCancelledAsync(IngestionWindowClaim claim, CancellationToken ct)
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

    private TimeSpan GetDelayUntilNextRun()
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var day = Math.Min(MonthlyRunDay, DateTime.DaysInMonth(now.Year, now.Month));
        var run = new DateTime(now.Year, now.Month, day,
            MonthlyRunUtcTime.Hour, MonthlyRunUtcTime.Minute, 0, DateTimeKind.Utc);
        if (now < run) return run - now;
        var next = now.AddMonths(1);
        day = Math.Min(MonthlyRunDay, DateTime.DaysInMonth(next.Year, next.Month));
        run = new DateTime(next.Year, next.Month, day,
            MonthlyRunUtcTime.Hour, MonthlyRunUtcTime.Minute, 0, DateTimeKind.Utc);
        return run - now;
    }
}
