using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Text.RegularExpressions;
using Saydin.PriceIngestion.Adapters;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Repositories;

/// <summary>
/// Durable window control-plane. Claim/finalization row locks and fencing tokens prevent
/// stale owners from committing. Data UPSERT, window terminal state and job terminal
/// state share one DbContext, connection and transaction.
/// </summary>
public sealed class IngestionWindowRepository(
    IDbContextFactory<SaydinDbContext> contextFactory,
    IIngestionPersistenceFaultInjector faultInjector,
    TimeProvider timeProvider,
    IIngestionFreshnessTelemetry freshnessTelemetry) : IIngestionWindowRepository
{
    private const int MaxErrorLength = 2000;

    public async Task<IngestionFreshnessState> ReadFreshnessStateAsync(CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var now = await context.Database.SqlQuery<DateTimeOffset>(
            $"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var rows = await context.Database.SqlQuery<FreshnessRow>($"""
            WITH expected_scope AS (
                -- A provider is healthy only when every active asset it is responsible
                -- for has durable history. Seeding from jobs alone lets a newly enabled
                -- asset disappear behind a healthy sibling until its first plan/claim.
                SELECT a.source, a.id AS asset_id, 'daily'::text AS cadence
                  FROM assets a
                 WHERE a.is_active
                   AND a.source IN ('tcmb','coingecko','openexchangerates','twelvedata')
                UNION ALL
                -- Inflation is a required global, asset-less monthly stream and must
                -- exist in the durable universe before its first job is created.
                SELECT 'evds'::text, NULL::uuid, 'monthly'::text
            ), scope_attempt AS (
                SELECT j.source, j.asset_id,
                       CASE WHEN j.job_type IN ('inflation_backfill','inflation_daily')
                            THEN 'monthly' ELSE 'daily' END AS cadence,
                       max(j.started_at) AS last_attempt_at
                  FROM ingestion_jobs j
                 WHERE j.source IS NOT NULL
                   AND j.window_id IS NOT NULL
                   AND (j.asset_id IS NULL OR EXISTS (
                        SELECT 1 FROM assets a WHERE a.id=j.asset_id AND a.is_active))
                 GROUP BY j.source, j.asset_id, cadence
            ), scope_success AS (
                SELECT j.source, j.asset_id,
                       CASE WHEN j.job_type IN ('inflation_backfill','inflation_daily')
                            THEN 'monthly' ELSE 'daily' END AS cadence,
                       max(j.finished_at) AS last_success_at,
                       max(w.range_end) AS data_through
                  FROM ingestion_jobs j
                  JOIN ingestion_windows w ON w.id=j.window_id
                 WHERE j.source IS NOT NULL
                   AND j.status='success'
                   AND w.state IN ('succeeded','expected_no_data')
                   AND (j.asset_id IS NULL OR EXISTS (
                        SELECT 1 FROM assets a WHERE a.id=j.asset_id AND a.is_active))
                 GROUP BY j.source, j.asset_id, cadence
            ), scope_state AS (
                SELECT e.source, e.asset_id, e.cadence, a.last_attempt_at,
                       s.last_success_at, s.data_through,
                       (SELECT count(*)::bigint
                          FROM ingestion_jobs failed
                         WHERE failed.source=e.source
                           AND failed.asset_id IS NOT DISTINCT FROM e.asset_id
                           AND (CASE WHEN failed.job_type IN ('inflation_backfill','inflation_daily')
                                     THEN 'monthly' ELSE 'daily' END)=e.cadence
                           AND failed.status='failed'
                           AND (s.last_success_at IS NULL OR failed.started_at > s.last_success_at)
                       ) AS failure_streak
                  FROM expected_scope e
                  LEFT JOIN scope_attempt a
                    ON a.source=e.source AND a.asset_id IS NOT DISTINCT FROM e.asset_id
                   AND a.cadence=e.cadence
                  LEFT JOIN scope_success s
                    ON s.source=e.source AND s.asset_id IS NOT DISTINCT FROM e.asset_id
                   AND s.cadence=e.cadence
            ), grouped AS (
                SELECT source, cadence,
                       max(last_attempt_at) AS last_attempt_at,
                       CASE WHEN count(*) FILTER (WHERE last_success_at IS NULL) > 0
                            THEN NULL ELSE min(last_success_at) END AS last_success_at,
                       CASE WHEN count(*) FILTER (WHERE data_through IS NULL) > 0
                            THEN NULL ELSE min(data_through) END AS data_through,
                       sum(failure_streak)::bigint AS failure_streak
                  FROM scope_state
                 GROUP BY source, cadence
            )
            SELECT g.source AS source, g.cadence AS cadence,
                   g.last_attempt_at AS last_attempt_at,
                   g.last_success_at AS last_success_at,
                   g.data_through AS data_through,
                   g.failure_streak AS failure_streak
              FROM grouped g
            """).ToListAsync(ct);
        var calendars = await context.MarketCalendarActiveReleases.AsNoTracking()
            .Select(active => new MarketCalendarCoverageSnapshot(
                active.CalendarCode, active.Release.CoverageThrough))
            .ToListAsync(ct);
        return new IngestionFreshnessState(now,
            rows.Select(row => new IngestionFreshnessSnapshot(
                row.Source,
                row.Cadence == "monthly" ? IngestionCadence.Monthly : IngestionCadence.Daily,
                row.LastAttemptAt,
                row.LastSuccessAt,
                row.DataThrough,
                row.FailureStreak)).ToArray(),
            calendars);
    }

    public async Task<MarketCalendarReadiness> CheckCalendarReadinessAsync(
        IngestionWindowScope scope,
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        if (!RequiresAuthoritativeCalendar(scope))
            return MarketCalendarReadiness.NotRequired;
        if (scope.AssetId is null || from > to)
            return new(true, false, null, null, "calendar_binding_missing");

        await using var context = await contextFactory.CreateDbContextAsync(ct);
        return await ResolveCalendarReadinessAsync(context, scope, from, to, null, ct);
    }

    public async Task<IReadOnlySet<DateOnly>> GetExpectedNoDataDatesAsync(
        Guid releaseId,
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        if (from > to)
            throw new ArgumentException("Calendar başlangıcı bitişten sonra olamaz.");
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var days = await context.MarketCalendarDays.AsNoTracking()
            .Where(day => day.ReleaseId == releaseId
                && day.CalendarDate >= from && day.CalendarDate <= to)
            .Select(day => new { day.CalendarDate, day.ObservationExpected })
            .ToListAsync(ct);
        var requested = to.DayNumber - from.DayNumber + 1;
        if (days.Count != requested)
            throw new CalendarNotReadyException("calendar_coverage_missing");
        return days.Where(day => !day.ObservationExpected)
            .Select(day => day.CalendarDate).ToHashSet();
    }

    public async Task PlanWindowsAsync(
        IngestionWindowScope scope,
        DateOnly start,
        DateOnly end,
        int chunkSize,
        IngestionCadence cadence,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        if (start > end) return;

        var readiness = await CheckCalendarReadinessAsync(scope, start, end, ct);
        if (!readiness.Ready)
            throw new CalendarNotReadyException(readiness.OutcomeCode);

        await using var context = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(ct);
        await AcquireScopeLockAsync(context, tx, scope, tryOnly: false, ct);

        var existing = await context.IngestionWindows
            .Where(window => window.Source == scope.Source
                && window.AssetId == scope.AssetId
                && window.JobType == scope.JobType
                && window.ContractVersion == scope.ContractVersion
                && window.RangeEnd >= start
                && window.RangeStart <= end)
            .OrderBy(window => window.RangeStart)
            .Select(window => new { window.RangeStart, window.RangeEnd })
            .ToListAsync(ct);

        var current = start;
        while (current <= end)
        {
            var containing = existing.FirstOrDefault(item =>
                item.RangeStart <= current && item.RangeEnd >= current);
            if (containing is not null)
            {
                current = Add(containing.RangeEnd, cadence, 1);
                continue;
            }

            var nextExisting = existing.FirstOrDefault(item => item.RangeStart > current);
            var chunkEnd = Add(current, cadence, chunkSize - 1);
            if (chunkEnd > end) chunkEnd = end;
            if (nextExisting is not null)
            {
                var beforeExisting = Add(nextExisting.RangeStart, cadence, -1);
                if (chunkEnd > beforeExisting) chunkEnd = beforeExisting;
            }
            context.IngestionWindows.Add(NewWindow(scope, current, chunkEnd));
            existing.Add(new { RangeStart = current, RangeEnd = chunkEnd });
            existing = existing.OrderBy(item => item.RangeStart).ToList();
            current = Add(chunkEnd, cadence, 1);
        }

        await context.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task EnsureWindowsAsync(
        IngestionWindowScope scope,
        IReadOnlyList<IngestionWindowRange> ranges,
        CancellationToken ct)
    {
        if (ranges.Count == 0) return;
        if (ranges.Any(range => range.From > range.To))
            throw new ArgumentException("Window başlangıcı bitişten sonra olamaz.", nameof(ranges));

        foreach (var range in ranges)
        {
            var readiness = await CheckCalendarReadinessAsync(scope, range.From, range.To, ct);
            if (!readiness.Ready)
                throw new CalendarNotReadyException(readiness.OutcomeCode);
        }

        await using var context = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(ct);
        await AcquireScopeLockAsync(context, tx, scope, tryOnly: false, ct);

        var existing = await context.IngestionWindows
            .Where(window => window.Source == scope.Source
                && window.AssetId == scope.AssetId
                && window.JobType == scope.JobType
                && window.ContractVersion == scope.ContractVersion)
            .Select(window => new { window.RangeStart, window.RangeEnd })
            .ToListAsync(ct);

        foreach (var range in ranges.OrderBy(range => range.From))
        {
            if (existing.Any(item => item.RangeStart == range.From && item.RangeEnd == range.To))
                continue;
            if (existing.Any(item => item.RangeStart <= range.To && item.RangeEnd >= range.From))
                throw new InvalidOperationException(
                    $"Overlapping ingestion window reddedildi: {range.From:yyyy-MM-dd}..{range.To:yyyy-MM-dd}");
            context.IngestionWindows.Add(NewWindow(scope, range.From, range.To));
            existing.Add(new { RangeStart = range.From, RangeEnd = range.To });
        }

        await context.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<WindowClaimResult> ClaimNextAsync(
        IngestionWindowScope scope,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        await using var context = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(ct);
        if (!await AcquireScopeLockAsync(context, tx, scope, tryOnly: true, ct))
        {
            await tx.CommitAsync(ct);
            return new WindowClaimResult(WindowClaimStatus.Busy);
        }

        var rows = await context.IngestionWindows
            .FromSqlInterpolated($"""
                SELECT * FROM ingestion_windows
                WHERE source = {scope.Source}
                  AND asset_id IS NOT DISTINCT FROM {scope.AssetId}
                  AND job_type = {scope.JobType}
                  AND contract_version = {scope.ContractVersion}
                  AND state NOT IN ('succeeded', 'expected_no_data')
                ORDER BY range_start, range_end
                LIMIT 1
                FOR UPDATE
                """)
            .AsTracking()
            .ToListAsync(ct);
        var window = rows.SingleOrDefault();
        if (window is null)
        {
            await tx.CommitAsync(ct);
            return new WindowClaimResult(WindowClaimStatus.Complete);
        }

        var now = await GetDatabaseNowAsync(context, tx, ct);
        if (window.State == IngestionWindowStates.PermanentFailed)
        {
            await tx.CommitAsync(ct);
            return new WindowClaimResult(
                WindowClaimStatus.PermanentBlocked, OutcomeCode: window.OutcomeCode);
        }
        if (window.State == IngestionWindowStates.Running && window.LeaseUntil > now)
        {
            await tx.CommitAsync(ct);
            return new WindowClaimResult(WindowClaimStatus.Busy, NextAttemptAt: window.LeaseUntil);
        }
        if (window.State == IngestionWindowStates.RetryableFailed && window.NextAttemptAt > now)
        {
            await tx.CommitAsync(ct);
            return new WindowClaimResult(
                WindowClaimStatus.NotDue, NextAttemptAt: window.NextAttemptAt,
                OutcomeCode: window.OutcomeCode);
        }

        if (RequiresAuthoritativeCalendar(scope))
        {
            var readiness = await ResolveCalendarReadinessAsync(
                context, scope, window.RangeStart, window.RangeEnd,
                window.CalendarReleaseId, ct);
            if (!readiness.Ready)
            {
                await tx.CommitAsync(ct);
                return new WindowClaimResult(
                    WindowClaimStatus.CalendarNotReady,
                    OutcomeCode: readiness.OutcomeCode);
            }
            window.CalendarReleaseId ??= readiness.ReleaseId;
        }

        if (window.State == IngestionWindowStates.Running)
        {
            var abandonedJobs = await context.IngestionJobs
                .Where(job => job.WindowId == window.Id && job.Status == IngestionJobStatuses.Running)
                .ToListAsync(ct);
            foreach (var abandoned in abandonedJobs)
            {
                abandoned.Status = IngestionJobStatuses.Failed;
                abandoned.FinishedAt = now;
                abandoned.OutcomeCode = "lease_expired";
                abandoned.ErrorMessage = "Lease expired before terminal acknowledgement.";
            }
        }

        var token = Guid.CreateVersion7();
        window.State = IngestionWindowStates.Running;
        window.LeaseOwner = leaseOwner;
        window.LeaseToken = token;
        window.LeaseUntil = now.Add(leaseDuration);
        window.AttemptCount++;
        window.NextAttemptAt = now;
        window.RequestedCalendarCount = 0;
        window.ExpectedObservationCount = 0;
        window.RawItemCount = 0;
        window.AcceptedDistinctCount = 0;
        window.RejectedCount = 0;
        window.ExpectedNoDataCount = 0;
        window.OutcomeCode = null;
        window.ErrorCode = null;
        window.CompletedAt = null;
        window.UpdatedAt = now;

        var job = new IngestionJob
        {
            AssetId = window.AssetId,
            JobType = window.JobType,
            Source = window.Source,
            StartedAt = now,
            Status = IngestionJobStatuses.Running,
            DateRangeStart = window.RangeStart,
            DateRangeEnd = window.RangeEnd,
            WindowId = window.Id,
        };
        context.IngestionJobs.Add(job);
        await context.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        freshnessTelemetry.RecordStarted(claimSource: scope.Source,
            Cadence(scope.JobType), now);

        return new WindowClaimResult(WindowClaimStatus.Claimed,
            new IngestionWindowClaim(window.Id, job.Id, scope, window.RangeStart,
                window.RangeEnd, leaseOwner, token, window.AttemptCount, window.CalendarReleaseId));
    }

    public async Task<bool> RenewLeaseAsync(
        IngestionWindowClaim claim,
        TimeSpan leaseDuration,
        CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var affected = await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ingestion_windows
               SET lease_until = clock_timestamp() + {leaseDuration}, updated_at = clock_timestamp()
             WHERE id = {claim.WindowId}
               AND state = 'running'
               AND lease_owner = {claim.LeaseOwner}
               AND lease_token = {claim.LeaseToken}
               AND lease_until > clock_timestamp()
            """, ct);
        return affected == 1;
    }

    public Task CompletePriceAsync(
        IngestionWindowClaim claim,
        AdapterOutcome<PricePoint> outcome,
        IngestionWindowCounts counts,
        CancellationToken ct) =>
        CompleteValidatedPriceAsync(claim, outcome, counts, ct);

    private Task CompleteValidatedPriceAsync(
        IngestionWindowClaim claim,
        AdapterOutcome<PricePoint> outcome,
        IngestionWindowCounts counts,
        CancellationToken ct)
    {
        return CompleteAsync(claim, outcome.Kind, outcome.Code, counts,
            (context, token) => ValidatePriceCompletionAsync(context, claim, outcome, counts, token),
            (context, token) => UpsertPricesAsync(context, claim, outcome.Records, token), ct);
    }

    public Task CompleteInflationAsync(
        IngestionWindowClaim claim,
        AdapterOutcome<InflationRate> outcome,
        IngestionWindowCounts counts,
        CancellationToken ct) =>
        CompleteValidatedInflationAsync(claim, outcome, counts, ct);

    private Task CompleteValidatedInflationAsync(
        IngestionWindowClaim claim,
        AdapterOutcome<InflationRate> outcome,
        IngestionWindowCounts counts,
        CancellationToken ct)
    {
        ValidateInflationCompletion(claim, outcome, counts);
        return CompleteAsync(claim, outcome.Kind, outcome.Code, counts,
            (_, _) => Task.CompletedTask,
            (context, token) => UpsertInflationAsync(context, claim, outcome.Records, token), ct);
    }

    public async Task RecordFailureAsync(
        IngestionWindowClaim claim,
        string state,
        AdapterOutcomeKind kind,
        IngestionWindowCounts counts,
        string outcomeCode,
        string errorCode,
        string? detail,
        DateTimeOffset nextAttemptAt,
        CancellationToken ct)
    {
        if (state is not (IngestionWindowStates.RetryableFailed
            or IngestionWindowStates.PermanentFailed
            or IngestionWindowStates.Cancelled
            or IngestionWindowStates.Abandoned))
            throw new ArgumentOutOfRangeException(nameof(state));
        var validKind = state switch
        {
            IngestionWindowStates.RetryableFailed => kind == AdapterOutcomeKind.RetryableFailure,
            IngestionWindowStates.PermanentFailed => kind is AdapterOutcomeKind.PermanentFailure
                or AdapterOutcomeKind.PartialRejected,
            IngestionWindowStates.Cancelled => kind == AdapterOutcomeKind.Cancelled,
            IngestionWindowStates.Abandoned => kind == AdapterOutcomeKind.Abandoned,
            _ => false,
        };
        if (!validKind)
            throw new InvalidOperationException($"Window state/outcome kind uyumsuz: {state}/{kind}");

        await using var context = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(ct);
        var window = await LockClaimAsync(context, claim, ct);
        var now = await GetDatabaseNowAsync(context, tx, ct);
        var requestedDelay = nextAttemptAt - timeProvider.GetUtcNow();
        if (requestedDelay < TimeSpan.Zero) requestedDelay = TimeSpan.Zero;

        ApplyCounts(window, counts);
        window.State = state;
        window.OutcomeCode = Code(outcomeCode);
        window.ErrorCode = state is IngestionWindowStates.RetryableFailed or IngestionWindowStates.PermanentFailed
            ? Code(errorCode) : null;
        window.NextAttemptAt = now.Add(requestedDelay);
        window.LeaseOwner = null;
        window.LeaseToken = null;
        window.LeaseUntil = null;
        window.CompletedAt = state == IngestionWindowStates.PermanentFailed ? now : null;
        window.UpdatedAt = now;

        var job = await context.IngestionJobs.SingleAsync(item => item.Id == claim.JobId
            && item.WindowId == claim.WindowId
            && item.Status == IngestionJobStatuses.Running, ct);
        job.Status = IngestionJobStatuses.Failed;
        job.FinishedAt = now;
        job.RecordsUpserted = null;
        job.OutcomeCode = window.OutcomeCode;
        job.ErrorMessage = Truncate(detail ?? errorCode);

        await faultInjector.BeforeCommitAsync(window.Id, ct);
        await context.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        freshnessTelemetry.RecordTerminal(
            claim.Scope.Source, Cadence(claim.Scope.JobType), kind, counts,
            job.StartedAt, now, claim.To, authoritativeSuccess: false);
        await faultInjector.AfterCommitAsync(window.Id, ct);
    }

    public async Task<WindowTerminalState?> GetTerminalStateAsync(Guid windowId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        return await context.IngestionWindows
            .Where(window => window.Id == windowId
                && (window.State == IngestionWindowStates.Succeeded
                    || window.State == IngestionWindowStates.ExpectedNoData))
            .Select(window => new WindowTerminalState(window.State, window.OutcomeCode))
            .SingleOrDefaultAsync(ct);
    }

    public async Task RequeuePermanentAsync(
        Guid windowId, DateTimeOffset nextAttemptAt, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var affected = await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ingestion_windows
               SET state = 'retryable_failed', next_attempt_at = {nextAttemptAt},
                   completed_at = NULL, outcome_code = 'operator_requeue',
                   error_code = 'operator_requeue', updated_at = clock_timestamp()
             WHERE id = {windowId} AND state = 'permanent_failed'
            """, ct);
        if (affected != 1)
            throw new InvalidOperationException("Yalnız permanent_failed window operator tarafından requeue edilebilir.");
    }

    private async Task CompleteAsync(
        IngestionWindowClaim claim,
        AdapterOutcomeKind kind,
        string outcomeCode,
        IngestionWindowCounts counts,
        Func<SaydinDbContext, CancellationToken, Task> validate,
        Func<SaydinDbContext, CancellationToken, Task<int>> persist,
        CancellationToken ct)
    {
        if (kind is not (AdapterOutcomeKind.Data or AdapterOutcomeKind.ExpectedNoData))
            throw new InvalidOperationException("Failure outcome başarılı tamamlama yoluna verilemez.");
        ValidateTerminalCounts(counts);

        await using var context = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(ct);
        var window = await LockClaimAsync(context, claim, ct);
        await validate(context, ct);
        await SetWriterFenceAsync(context, claim, ct);
        var affected = await persist(context, ct);
        if (affected != counts.AcceptedDistinctCount)
            throw new InvalidOperationException(
                $"Ingestion UPSERT satır sayısı uyuşmuyor: expected={counts.AcceptedDistinctCount}, affected={affected}.");

        var now = await GetDatabaseNowAsync(context, tx, ct);
        ApplyCounts(window, counts);
        window.State = counts.AcceptedDistinctCount == 0
            ? IngestionWindowStates.ExpectedNoData : IngestionWindowStates.Succeeded;
        window.OutcomeCode = Code(outcomeCode);
        window.ErrorCode = null;
        window.LeaseOwner = null;
        window.LeaseToken = null;
        window.LeaseUntil = null;
        window.CompletedAt = now;
        window.UpdatedAt = now;

        var job = await context.IngestionJobs.SingleAsync(item => item.Id == claim.JobId
            && item.WindowId == claim.WindowId
            && item.Status == IngestionJobStatuses.Running, ct);
        job.Status = IngestionJobStatuses.Success;
        job.FinishedAt = now;
        job.RecordsUpserted = counts.AcceptedDistinctCount;
        job.OutcomeCode = window.OutcomeCode;
        job.ErrorMessage = null;

        await faultInjector.BeforeCommitAsync(window.Id, ct);
        await context.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        freshnessTelemetry.RecordTerminal(
            claim.Scope.Source, Cadence(claim.Scope.JobType), kind, counts,
            job.StartedAt, now, claim.To, authoritativeSuccess: true);
        await faultInjector.AfterCommitAsync(window.Id, ct);
    }

    private static IngestionCadence Cadence(string jobType) =>
        jobType is IngestionJobTypes.InflationBackfill or IngestionJobTypes.InflationDaily
            ? IngestionCadence.Monthly : IngestionCadence.Daily;

    private sealed class FreshnessRow
    {
        public required string Source { get; init; }
        public required string Cadence { get; init; }
        public DateTimeOffset? LastAttemptAt { get; init; }
        public DateTimeOffset? LastSuccessAt { get; init; }
        public DateOnly? DataThrough { get; init; }
        public long FailureStreak { get; init; }
    }

    private static async Task<IngestionWindow> LockClaimAsync(
        SaydinDbContext context,
        IngestionWindowClaim claim,
        CancellationToken ct)
    {
        var rows = await context.IngestionWindows
            .FromSqlInterpolated($"""
                SELECT * FROM ingestion_windows
                 WHERE id = {claim.WindowId}
                   AND state = 'running'
                   AND lease_owner = {claim.LeaseOwner}
                   AND lease_token = {claim.LeaseToken}
                   AND lease_until > clock_timestamp()
                 FOR UPDATE
                """)
            .AsTracking()
            .ToListAsync(ct);
        var window = rows.SingleOrDefault();
        if (window is null
            || window.State != IngestionWindowStates.Running
            || window.Source != claim.Scope.Source
            || window.AssetId != claim.Scope.AssetId
            || window.JobType != claim.Scope.JobType
            || window.ContractVersion != claim.Scope.ContractVersion
            || window.RangeStart != claim.From
            || window.RangeEnd != claim.To
            || window.CalendarReleaseId != claim.CalendarReleaseId
            || window.AttemptCount != claim.AttemptCount)
            throw new IngestionLeaseLostException(claim.WindowId);
        return window;
    }

    private static async Task SetWriterFenceAsync(
        SaydinDbContext context,
        IngestionWindowClaim claim,
        CancellationToken ct)
    {
        // is_local=true is PostgreSQL SET LOCAL semantics: pooled connections cannot
        // leak a capability beyond this transaction. The trigger revalidates the live
        // DB lease, scope and row key; the GUC is not a bypass on its own.
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT set_config('saydin.ingestion_window_id', {claim.WindowId.ToString("D")}, TRUE),
                   set_config('saydin.ingestion_lease_token', {claim.LeaseToken.ToString("D")}, TRUE)
            """, ct);
    }

    private static async Task<int> UpsertPricesAsync(
        SaydinDbContext context,
        IngestionWindowClaim claim,
        IReadOnlyList<PricePoint> points,
        CancellationToken ct)
    {
        if (points.Count == 0) return 0;
        BindPriceAuthority(claim, points);
        var assetIds = points.Select(point => point.AssetId).ToArray();
        var dates = points.Select(point => point.PriceDate).ToArray();
        var closes = points.Select(point => point.Close).ToArray();
        var opens = points.Select(point => point.Open).ToArray();
        var highs = points.Select(point => point.High).ToArray();
        var lows = points.Select(point => point.Low).ToArray();
        var volumes = points.Select(point => point.Volume).ToArray();
        var providerSources = points.Select(point => point.ProviderSource).ToArray();
        var observationIds = points.Select(point => point.SourceObservationId).ToArray();
        var asOfTimes = points.Select(point => point.AsOfAt).ToArray();
        var priceKinds = points.Select(point => point.PriceKind).ToArray();
        var finality = points.Select(point => point.IsFinal).ToArray();
        var contractVersions = points.Select(point => point.AuthorityContractVersion).ToArray();
        var sourceRaw = points.Select(point => point.SourceRaw).ToArray();
        var affected = await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO price_points (
                asset_id, price_date, close, open, high, low, volume,
                provider_source, source_observation_id, as_of_at, price_kind, is_final,
                observation_sha256, authority_contract_version, source_raw)
            SELECT asset_id, price_date, close, open, high, low, volume,
                   provider_source, source_observation_id, as_of_at, price_kind, is_final,
                   sha256(convert_to(saydin_canonical_observation(source_raw_text::jsonb)::text,'UTF8')),
                   authority_contract_version, source_raw_text::jsonb
            FROM UNNEST(
                {assetIds}::uuid[], {dates}::date[], {closes}::numeric[],
                {opens}::numeric[], {highs}::numeric[], {lows}::numeric[], {volumes}::numeric[],
                {providerSources}::text[], {observationIds}::text[], {asOfTimes}::timestamptz[],
                {priceKinds}::text[], {finality}::boolean[],
                {contractVersions}::integer[], {sourceRaw}::text[])
                AS t(asset_id, price_date, close, open, high, low, volume,
                     provider_source, source_observation_id, as_of_at, price_kind, is_final,
                     authority_contract_version, source_raw_text)
            ON CONFLICT (asset_id, price_date) DO UPDATE
              SET close = EXCLUDED.close, open = EXCLUDED.open, high = EXCLUDED.high,
                  low = EXCLUDED.low, volume = EXCLUDED.volume,
                  provider_source = EXCLUDED.provider_source,
                  source_observation_id = EXCLUDED.source_observation_id,
                  as_of_at = EXCLUDED.as_of_at, price_kind = EXCLUDED.price_kind,
                  is_final = EXCLUDED.is_final,
                  observation_sha256 = EXCLUDED.observation_sha256,
                  authority_contract_version = EXCLUDED.authority_contract_version,
                  source_raw = EXCLUDED.source_raw
            """, ct);
        if (affected != points.Count) return affected;

        await PersistFetchPayloadsAsync(context, points.Select(point => new FetchPayload(
            point.ProviderSource!, point.PayloadSha256!, point.PayloadByteLength!.Value)), ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH input AS (
                SELECT * FROM UNNEST(
                    {assetIds}::uuid[], {dates}::date[], {Enumerable.Repeat(claim.WindowId, points.Count).ToArray()}::uuid[],
                    {providerSources}::text[], {points.Select(point => point.PayloadSha256).ToArray()}::bytea[],
                    {observationIds}::text[], {contractVersions}::integer[])
                  AS i(asset_id,price_date,ingestion_window_id,provider_source,payload_sha256,
                       source_observation_id,authority_contract_version)
            )
            INSERT INTO price_observation_attributions(
                asset_id,price_date,ingestion_window_id,provider_source,payload_sha256,
                source_observation_id,observation_sha256,authority_contract_version)
            SELECT i.asset_id,i.price_date,i.ingestion_window_id,i.provider_source,i.payload_sha256,
                   i.source_observation_id,p.observation_sha256,i.authority_contract_version
              FROM input i JOIN price_points p USING(asset_id,price_date)
             WHERE p.provider_source=i.provider_source
               AND p.source_observation_id=i.source_observation_id
               AND p.authority_contract_version=i.authority_contract_version
            ON CONFLICT DO NOTHING
            """, ct);
        var attributed = await context.Database.SqlQuery<int>($"""
            WITH input AS (
                SELECT * FROM UNNEST(
                    {assetIds}::uuid[], {dates}::date[], {Enumerable.Repeat(claim.WindowId, points.Count).ToArray()}::uuid[],
                    {providerSources}::text[], {points.Select(point => point.PayloadSha256).ToArray()}::bytea[],
                    {observationIds}::text[], {contractVersions}::integer[])
                  AS i(asset_id,price_date,ingestion_window_id,provider_source,payload_sha256,
                       source_observation_id,authority_contract_version)
            )
            SELECT count(*)::integer AS "Value"
              FROM input i
              JOIN price_points p USING(asset_id,price_date)
              JOIN price_observation_attributions a
                ON a.asset_id=i.asset_id AND a.price_date=i.price_date
               AND a.ingestion_window_id=i.ingestion_window_id
               AND a.provider_source=i.provider_source AND a.payload_sha256=i.payload_sha256
               AND a.source_observation_id=i.source_observation_id
               AND a.authority_contract_version=i.authority_contract_version
               AND a.observation_sha256=p.observation_sha256
             WHERE p.provider_source=i.provider_source
               AND p.source_observation_id=i.source_observation_id
               AND p.authority_contract_version=i.authority_contract_version
            """).SingleAsync(ct);
        if (attributed != points.Count)
            throw new InvalidOperationException("Price observation attribution completeness failed.");

        var scopeAssetId = claim.Scope.AssetId
            ?? throw new InvalidOperationException("Price ingestion window asset_id taşımalıdır.");
        var storedKeys = await context.PricePoints.AsNoTracking()
            .Where(point => point.AssetId == scopeAssetId
                && dates.Contains(point.PriceDate))
            .Select(point => new { point.AssetId, point.PriceDate })
            .ToListAsync(ct);
        var assetSource = await context.Assets.AsNoTracking()
            .Where(asset => asset.Id == scopeAssetId)
            .Select(asset => asset.Source)
            .SingleAsync(ct);
        if (assetSource != claim.Scope.Source
            || storedKeys.Count != points.Count
            || !storedKeys.Select(key => key.PriceDate).ToHashSet().SetEquals(dates))
            throw new InvalidOperationException(
                "Price UPSERT authoritative asset/source/date key-set doğrulaması başarısız.");
        return affected;
    }

    private static async Task<int> UpsertInflationAsync(
        SaydinDbContext context,
        IngestionWindowClaim claim,
        IReadOnlyList<InflationRate> rates,
        CancellationToken ct)
    {
        if (rates.Count == 0) return 0;
        BindInflationAuthority(claim, rates);
        var dates = rates.Select(rate => rate.PeriodDate).ToArray();
        var values = rates.Select(rate => rate.IndexValue).ToArray();
        var sources = rates.Select(rate => rate.Source).ToArray();
        var providerSources = rates.Select(rate => rate.ProviderSource).ToArray();
        var observationIds = rates.Select(rate => rate.SourceObservationId).ToArray();
        var asOfTimes = rates.Select(rate => rate.AsOfAt).ToArray();
        var priceKinds = rates.Select(rate => rate.PriceKind).ToArray();
        var finality = rates.Select(rate => rate.IsFinal).ToArray();
        var contractVersions = rates.Select(rate => rate.AuthorityContractVersion).ToArray();
        var sourceRaw = rates.Select(rate => rate.SourceRaw).ToArray();
        var affected = await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inflation_rates (
                period_date, index_value, source,
                provider_source, source_observation_id, as_of_at, price_kind, is_final,
                observation_sha256, authority_contract_version, source_raw)
            SELECT period_date, index_value, source,
                   provider_source, source_observation_id, as_of_at, price_kind, is_final,
                   sha256(convert_to(saydin_canonical_observation(source_raw_text::jsonb)::text,'UTF8')),
                   authority_contract_version, source_raw_text::jsonb
            FROM UNNEST(
                {dates}::date[], {values}::numeric[], {sources}::text[],
                {providerSources}::text[], {observationIds}::text[], {asOfTimes}::timestamptz[],
                {priceKinds}::text[], {finality}::boolean[],
                {contractVersions}::integer[], {sourceRaw}::text[])
                AS t(period_date, index_value, source,
                     provider_source, source_observation_id, as_of_at, price_kind, is_final,
                     authority_contract_version, source_raw_text)
            ON CONFLICT (period_date, source) DO UPDATE
              SET index_value = EXCLUDED.index_value,
                  provider_source = EXCLUDED.provider_source,
                  source_observation_id = EXCLUDED.source_observation_id,
                  as_of_at = EXCLUDED.as_of_at, price_kind = EXCLUDED.price_kind,
                  is_final = EXCLUDED.is_final,
                  observation_sha256 = EXCLUDED.observation_sha256,
                  authority_contract_version = EXCLUDED.authority_contract_version,
                  source_raw = EXCLUDED.source_raw
            """, ct);
        if (affected != rates.Count) return affected;

        await PersistFetchPayloadsAsync(context, rates.Select(rate => new FetchPayload(
            rate.ProviderSource!, rate.PayloadSha256!, rate.PayloadByteLength!.Value)), ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH input AS (
                SELECT * FROM UNNEST(
                    {dates}::date[], {sources}::text[], {Enumerable.Repeat(claim.WindowId, rates.Count).ToArray()}::uuid[],
                    {providerSources}::text[], {rates.Select(rate => rate.PayloadSha256).ToArray()}::bytea[],
                    {observationIds}::text[], {contractVersions}::integer[])
                  AS i(period_date,source,ingestion_window_id,provider_source,payload_sha256,
                       source_observation_id,authority_contract_version)
            )
            INSERT INTO inflation_observation_attributions(
                period_date,source,ingestion_window_id,provider_source,payload_sha256,
                source_observation_id,observation_sha256,authority_contract_version)
            SELECT i.period_date,i.source,i.ingestion_window_id,i.provider_source,i.payload_sha256,
                   i.source_observation_id,r.observation_sha256,i.authority_contract_version
              FROM input i JOIN inflation_rates r USING(period_date,source)
             WHERE r.provider_source=i.provider_source
               AND r.source_observation_id=i.source_observation_id
               AND r.authority_contract_version=i.authority_contract_version
            ON CONFLICT DO NOTHING
            """, ct);
        var attributed = await context.Database.SqlQuery<int>($"""
            WITH input AS (
                SELECT * FROM UNNEST(
                    {dates}::date[], {sources}::text[], {Enumerable.Repeat(claim.WindowId, rates.Count).ToArray()}::uuid[],
                    {providerSources}::text[], {rates.Select(rate => rate.PayloadSha256).ToArray()}::bytea[],
                    {observationIds}::text[], {contractVersions}::integer[])
                  AS i(period_date,source,ingestion_window_id,provider_source,payload_sha256,
                       source_observation_id,authority_contract_version)
            )
            SELECT count(*)::integer AS "Value"
              FROM input i
              JOIN inflation_rates r USING(period_date,source)
              JOIN inflation_observation_attributions a
                ON a.period_date=i.period_date AND a.source=i.source
               AND a.ingestion_window_id=i.ingestion_window_id
               AND a.provider_source=i.provider_source AND a.payload_sha256=i.payload_sha256
               AND a.source_observation_id=i.source_observation_id
               AND a.authority_contract_version=i.authority_contract_version
               AND a.observation_sha256=r.observation_sha256
             WHERE r.provider_source=i.provider_source
               AND r.source_observation_id=i.source_observation_id
               AND r.authority_contract_version=i.authority_contract_version
            """).SingleAsync(ct);
        if (attributed != rates.Count)
            throw new InvalidOperationException("Inflation observation attribution completeness failed.");

        var storedKeys = await context.InflationRates.AsNoTracking()
            .Where(rate => rate.Source == Saydin.Shared.Constants.InflationSources.Tuik
                && dates.Contains(rate.PeriodDate))
            .Select(rate => new { rate.Source, rate.PeriodDate })
            .ToListAsync(ct);
        if (claim.Scope.Source != "evds"
            || claim.Scope.AssetId is not null
            || storedKeys.Count != rates.Count
            || storedKeys.Any(key => key.Source != Saydin.Shared.Constants.InflationSources.Tuik)
            || !storedKeys.Select(key => key.PeriodDate).ToHashSet().SetEquals(dates))
            throw new InvalidOperationException(
                "Inflation UPSERT authoritative source/month key-set doğrulaması başarısız.");
        return affected;
    }

    private sealed record FetchPayload(string ProviderSource, byte[] Sha256, int ByteLength);

    private static async Task PersistFetchPayloadsAsync(
        SaydinDbContext context,
        IEnumerable<FetchPayload> payloads,
        CancellationToken ct)
    {
        var distinct = payloads
            .GroupBy(payload => $"{payload.ProviderSource}:{Convert.ToHexString(payload.Sha256)}",
                StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                if (group.Any(payload => payload.ByteLength != first.ByteLength))
                    throw new InvalidOperationException("Provider payload hash/length conflict.");
                return first;
            })
            .ToArray();
        var providers = distinct.Select(payload => payload.ProviderSource).ToArray();
        var hashes = distinct.Select(payload => payload.Sha256).ToArray();
        var lengths = distinct.Select(payload => payload.ByteLength).ToArray();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH input AS (
                SELECT * FROM UNNEST({providers}::text[],{hashes}::bytea[],{lengths}::integer[])
                    AS p(provider_source,payload_sha256,payload_byte_length)
            )
            INSERT INTO provider_fetch_payloads(provider_source,payload_sha256,payload_byte_length)
            SELECT * FROM input ON CONFLICT DO NOTHING
            """, ct);
        var accepted = await context.Database.SqlQuery<int>($"""
            WITH input AS (
                SELECT * FROM UNNEST({providers}::text[],{hashes}::bytea[],{lengths}::integer[])
                    AS p(provider_source,payload_sha256,payload_byte_length)
            )
            SELECT count(*)::integer AS "Value"
              FROM input
              JOIN provider_fetch_payloads stored USING(provider_source,payload_sha256)
             WHERE stored.payload_byte_length=input.payload_byte_length
            """).SingleAsync(ct);
        if (accepted != distinct.Length)
            throw new InvalidOperationException("Provider payload hash/length ledger conflict.");
    }

    private static void BindPriceAuthority(
        IngestionWindowClaim claim,
        IReadOnlyList<PricePoint> points)
    {
        var assetId = claim.Scope.AssetId
            ?? throw new InvalidOperationException("Price authority requires an asset-bound window.");
        foreach (var point in points)
        {
            if (point.AssetId != assetId
                || point.ProviderSource != claim.Scope.Source
                || point.IngestionWindowId is { } existingWindow && existingWindow != claim.WindowId
                || point.AuthorityContractVersion is { } existingVersion
                    && existingVersion != claim.Scope.ContractVersion
                || point.PayloadSha256 is not { Length: ObservationAuthorityLimits.Sha256Bytes }
                || point.PayloadByteLength is not (> 0 and <= ObservationAuthorityLimits.SourceRawBytes))
                throw new InvalidOperationException("Price authority does not match the claimed window.");
            point.IngestionWindowId = claim.WindowId;
            point.AuthorityContractVersion = claim.Scope.ContractVersion;
        }
    }

    private static void BindInflationAuthority(
        IngestionWindowClaim claim,
        IReadOnlyList<InflationRate> rates)
    {
        if (claim.Scope.Source != ProviderSources.Evds || claim.Scope.AssetId is not null)
            throw new InvalidOperationException("Inflation authority requires an EVDS global window.");
        foreach (var rate in rates)
        {
            if (rate.ProviderSource != ProviderSources.Evds
                || rate.IngestionWindowId is { } existingWindow && existingWindow != claim.WindowId
                || rate.AuthorityContractVersion is { } existingVersion
                    && existingVersion != claim.Scope.ContractVersion
                || rate.PayloadSha256 is not { Length: ObservationAuthorityLimits.Sha256Bytes }
                || rate.PayloadByteLength is not (> 0 and <= ObservationAuthorityLimits.SourceRawBytes))
                throw new InvalidOperationException("Inflation authority does not match the claimed window.");
            rate.IngestionWindowId = claim.WindowId;
            rate.AuthorityContractVersion = claim.Scope.ContractVersion;
        }
    }

    private static async Task ValidatePriceCompletionAsync(
        SaydinDbContext context,
        IngestionWindowClaim claim,
        AdapterOutcome<PricePoint> outcome,
        IngestionWindowCounts supplied,
        CancellationToken ct)
    {
        var requested = new HashSet<DateOnly>();
        for (var date = claim.From; date <= claim.To; date = date.AddDays(1))
            requested.Add(date);
        var acceptedDates = outcome.Records.Select(point => point.PriceDate).ToArray();
        var accepted = acceptedDates.ToHashSet();
        var noData = outcome.ExpectedNoDataDates;
        var expected = requested.Except(noData).ToHashSet();
        var contractNoData = new HashSet<DateOnly>();
        if (RequiresAuthoritativeCalendar(claim.Scope))
        {
            if (claim.CalendarReleaseId is not { } releaseId)
                throw new CalendarNotReadyException("calendar_release_unbound");
            var authoritativeDays = await context.MarketCalendarDays.AsNoTracking()
                .Where(day => day.ReleaseId == releaseId
                    && day.CalendarDate >= claim.From && day.CalendarDate <= claim.To)
                .Select(day => new { day.CalendarDate, day.ObservationExpected })
                .ToListAsync(ct);
            if (authoritativeDays.Count != requested.Count)
                throw new CalendarNotReadyException("calendar_coverage_missing");
            contractNoData.UnionWith(authoritativeDays
                .Where(day => !day.ObservationExpected)
                .Select(day => day.CalendarDate));
        }
        else if (claim.Scope.Source is "tcmb" or "twelvedata")
        {
            for (var date = claim.From; date <= claim.To; date = date.AddDays(1))
                if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                    contractNoData.Add(date);
            var holidays = await context.Database.SqlQuery<DateOnly>($"""
                SELECT holiday_date AS "Value"
                  FROM market_holidays
                 WHERE asset_id = {claim.Scope.AssetId!.Value}
                   AND holiday_date BETWEEN {claim.From} AND {claim.To}
                """).ToListAsync(ct);
            contractNoData.UnionWith(holidays);
        }
        var actual = new IngestionWindowCounts(
            requested.Count, expected.Count, outcome.RawItemCount, accepted.Count,
            outcome.RejectedCount, noData.Count);

        if (claim.Scope.AssetId is not { } assetId
            || outcome.Records.Any(point => point.AssetId != assetId)
            || acceptedDates.Length != accepted.Count
            || !noData.IsSubsetOf(requested)
            || !noData.SetEquals(contractNoData)
            || accepted.Overlaps(noData)
            || !accepted.SetEquals(expected)
            || outcome.RejectedCount != 0
            || outcome.RawItemCount < accepted.Count
            || actual != supplied
            || (outcome.Kind == AdapterOutcomeKind.Data && accepted.Count == 0)
            || (outcome.Kind == AdapterOutcomeKind.ExpectedNoData
                && (accepted.Count != 0 || expected.Count != 0))
            || outcome.Kind is not (AdapterOutcomeKind.Data or AdapterOutcomeKind.ExpectedNoData))
            throw new InvalidOperationException("Price completion outcome/record cardinality doğrulaması başarısız.");
    }

    private static void ValidateInflationCompletion(
        IngestionWindowClaim claim,
        AdapterOutcome<InflationRate> outcome,
        IngestionWindowCounts supplied)
    {
        var expected = new HashSet<DateOnly>();
        for (var month = new DateOnly(claim.From.Year, claim.From.Month, 1);
             month <= new DateOnly(claim.To.Year, claim.To.Month, 1);
             month = month.AddMonths(1)) expected.Add(month);
        var acceptedDates = outcome.Records.Select(rate => rate.PeriodDate).ToArray();
        var accepted = acceptedDates.ToHashSet();
        var actual = new IngestionWindowCounts(
            expected.Count, expected.Count, outcome.RawItemCount, accepted.Count,
            outcome.RejectedCount, 0);

        if (outcome.Kind != AdapterOutcomeKind.Data
            || outcome.ExpectedNoDataDates.Count != 0
            || outcome.Records.Any(rate => rate.Source != Saydin.Shared.Constants.InflationSources.Tuik)
            || acceptedDates.Length != accepted.Count
            || !accepted.SetEquals(expected)
            || outcome.RejectedCount != 0
            || outcome.RawItemCount < accepted.Count
            || actual != supplied)
            throw new InvalidOperationException("Inflation completion outcome/month cardinality doğrulaması başarısız.");
    }

    private static void ValidateTerminalCounts(IngestionWindowCounts counts)
    {
        if (counts.RequestedCalendarCount <= 0
            || counts.ExpectedObservationCount < 0
            || counts.RawItemCount < 0
            || counts.AcceptedDistinctCount < 0
            || counts.RejectedCount != 0
            || counts.ExpectedNoDataCount < 0
            || counts.ExpectedObservationCount > counts.RequestedCalendarCount
            || counts.AcceptedDistinctCount != counts.ExpectedObservationCount
            || counts.ExpectedNoDataCount != counts.RequestedCalendarCount - counts.ExpectedObservationCount
            || counts.AcceptedDistinctCount > counts.RawItemCount)
            throw new InvalidOperationException("Terminal ingestion completeness invariant ihlali.");
    }

    private static void ApplyCounts(IngestionWindow window, IngestionWindowCounts counts)
    {
        window.RequestedCalendarCount = counts.RequestedCalendarCount;
        window.ExpectedObservationCount = counts.ExpectedObservationCount;
        window.RawItemCount = counts.RawItemCount;
        window.AcceptedDistinctCount = counts.AcceptedDistinctCount;
        window.RejectedCount = counts.RejectedCount;
        window.ExpectedNoDataCount = counts.ExpectedNoDataCount;
    }

    private IngestionWindow NewWindow(
        IngestionWindowScope scope, DateOnly from, DateOnly to) => new()
    {
        Source = scope.Source,
        AssetId = scope.AssetId,
        JobType = scope.JobType,
        RangeStart = from,
        RangeEnd = to,
        ContractVersion = scope.ContractVersion,
        State = IngestionWindowStates.Pending,
        NextAttemptAt = timeProvider.GetUtcNow(),
    };

    private static DateOnly Add(DateOnly date, IngestionCadence cadence, int count) =>
        cadence == IngestionCadence.Daily ? date.AddDays(count) : date.AddMonths(count);

    private static bool RequiresAuthoritativeCalendar(IngestionWindowScope scope) =>
        scope.ContractVersion >= 2 && scope.Source is "tcmb" or "twelvedata";

    private static async Task<MarketCalendarReadiness> ResolveCalendarReadinessAsync(
        SaydinDbContext context,
        IngestionWindowScope scope,
        DateOnly from,
        DateOnly to,
        Guid? boundReleaseId,
        CancellationToken ct)
    {
        if (scope.AssetId is not { } assetId)
            return new(true, false, null, null, "calendar_binding_missing");

        string? calendarCode;
        if (boundReleaseId is not null)
        {
            calendarCode = scope.Source switch
            {
                "tcmb" => "tcmb_indicative_fx",
                "twelvedata" => "bist_pay_xist",
                _ => null,
            };
        }
        else
        {
            calendarCode = await context.AssetMarketCalendars.AsNoTracking()
                .Where(item => item.AssetId == assetId && item.Source == scope.Source)
                .Select(item => item.CalendarCode)
                .SingleOrDefaultAsync(ct);
        }
        if (calendarCode is null)
            return new(true, false, null, null, "calendar_binding_missing");

        var release = boundReleaseId is { } immutableReleaseId
            ? await context.MarketCalendarReleases.AsNoTracking()
                .Where(item => item.Id == immutableReleaseId
                    && item.CalendarCode == calendarCode
                    && item.SealedAt != null)
                .Select(item => new { item.Id, item.CoverageFrom, item.CoverageThrough })
                .SingleOrDefaultAsync(ct)
            : await (from active in context.MarketCalendarActiveReleases.AsNoTracking()
                     join item in context.MarketCalendarReleases.AsNoTracking()
                         on active.ReleaseId equals item.Id
                     where active.CalendarCode == calendarCode
                         && item.CalendarCode == calendarCode
                         && item.SealedAt != null
                     select new { item.Id, item.CoverageFrom, item.CoverageThrough })
                .SingleOrDefaultAsync(ct);
        if (release is null)
            return new(true, false, null, calendarCode, "calendar_active_release_missing");
        if (release.CoverageFrom > from || release.CoverageThrough < to)
            return new(true, false, release.Id, calendarCode, "calendar_coverage_missing");

        var expectedRows = to.DayNumber - from.DayNumber + 1;
        var actualRows = await context.MarketCalendarDays.AsNoTracking()
            .CountAsync(day => day.ReleaseId == release.Id
                && day.CalendarDate >= from && day.CalendarDate <= to, ct);
        if (actualRows != expectedRows)
            return new(true, false, release.Id, calendarCode, "calendar_days_incomplete");
        return new(true, true, release.Id, calendarCode, "calendar_ready");
    }

    private static async Task<bool> AcquireScopeLockAsync(
        SaydinDbContext context,
        IDbContextTransaction transaction,
        IngestionWindowScope scope,
        bool tryOnly,
        CancellationToken ct)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandTimeout = 5;
        command.CommandText = tryOnly
            ? "SELECT pg_try_advisory_xact_lock(hashtextextended(@scope_key, 0));"
            : "SELECT pg_advisory_xact_lock(hashtextextended(@scope_key, 0)); SELECT TRUE;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "scope_key";
        parameter.Value = $"{scope.Source}|{scope.AssetId?.ToString("N") ?? "global"}|{scope.JobType}|{scope.ContractVersion}";
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(ct);
        return result is true;
    }

    private static async Task<DateTimeOffset> GetDatabaseNowAsync(
        SaydinDbContext context, IDbContextTransaction transaction, CancellationToken ct)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "SELECT clock_timestamp();";
        var value = await command.ExecuteScalarAsync(ct);
        return value switch
        {
            DateTimeOffset offset => offset,
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException(
                $"PostgreSQL clock_timestamp beklenmeyen CLR tipi: {value?.GetType().FullName ?? "null"}"),
        };
    }

    private static string Code(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Outcome/error code boş olamaz.", nameof(value));
        return value.Length <= 80 ? value : value[..80];
    }

    private static string Truncate(string value)
    {
        var redacted = Regex.Replace(value,
            @"(?i)(app_id|api[-_]?key|authorization|token)\s*[:=]\s*[^&\s]+",
            "$1=[REDACTED]", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        return redacted.Length <= MaxErrorLength ? redacted : redacted[..MaxErrorLength];
    }
}
