using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;
using Saydin.PriceIngestion.Workers;
using Saydin.Shared.Constants;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Tests.Workers;

public class EvdsInflationWorkerTests
{
    [Fact]
    public void ComputeBackfillChunks_IsMonthFirstContiguous()
    {
        var chunks = EvdsInflationWorker.ComputeBackfillChunks(
            new DateOnly(2003, 1, 1), new DateOnly(2013, 3, 1), 60);

        chunks.Should().HaveCount(3);
        chunks[0].Should().Be((new DateOnly(2003, 1, 1), new DateOnly(2007, 12, 1)));
        chunks[1].From.Should().Be(chunks[0].To.AddMonths(1));
        chunks[^1].To.Should().Be(new DateOnly(2013, 3, 1));
        chunks.SelectMany(chunk => new[] { chunk.From, chunk.To })
            .Should().OnlyContain(date => date.Day == 1);
    }

    [Fact]
    public async Task SecondRetryableChunk_StopsThird_AndRestartClaimsSecondFirst()
    {
        var fixture = CreateFixture();
        var calls = 0;
        fixture.Adapter.FetchRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
            Arg.Any<CancellationToken>()).Returns(call =>
            {
                calls++;
                return calls == 2
                    ? AdapterOutcome<InflationRate>.RetryableFailure("http_503")
                    : Success(call.ArgAt<DateOnly>(0), call.ArgAt<DateOnly>(1));
            });

        var first = await fixture.Worker.RunBackfillChunksAsync(ThreeChunks(), default);

        first.Should().BeFalse();
        calls.Should().Be(2);

        var second = await fixture.Worker.RunBackfillChunksAsync(ThreeChunks(), default);

        second.Should().BeTrue();
        fixture.Windows.Claimed.Should().Equal(
            new DateOnly(2020, 1, 1), new DateOnly(2020, 2, 1),
            new DateOnly(2020, 2, 1), new DateOnly(2020, 3, 1));
    }

    [Fact]
    public async Task MissingMonthOrWrongSource_IsPermanentAndScopeIsIsolated()
    {
        var fixture = CreateFixture();
        var calls = 0;
        fixture.Adapter.FetchRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
            Arg.Any<CancellationToken>()).Returns(call =>
            {
                calls++;
                if (calls == 2)
                    return AdapterOutcome<InflationRate>.Data(
                        [new InflationRate
                        {
                            PeriodDate = call.ArgAt<DateOnly>(0),
                            IndexValue = 100,
                            Source = InflationSources.SeedApproximation,
                        }], 1);
                return Success(call.ArgAt<DateOnly>(0), call.ArgAt<DateOnly>(1));
            });

        var result = await fixture.Worker.RunBackfillChunksAsync(ThreeChunks(), default);

        result.Should().BeFalse();
        calls.Should().Be(2);
        fixture.Windows.PermanentFailures.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_IsRecordedAndPropagated()
    {
        var fixture = CreateFixture();
        using var cts = new CancellationTokenSource();
        fixture.Adapter.FetchRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
            Arg.Any<CancellationToken>()).Returns<AdapterOutcome<InflationRate>>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        var act = () => fixture.Worker.RunBackfillChunksAsync(ThreeChunks(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fixture.Windows.Cancelled.Should().Be(1);
    }

    [Fact]
    public async Task NonCancellationException_BlockingFinalizerIsBounded()
    {
        var fixture = CreateFixture();
        fixture.Windows.BlockFailureFinalization = true;
        fixture.Adapter.FetchRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
            Arg.Any<CancellationToken>()).Returns<AdapterOutcome<InflationRate>>(_ =>
                throw new InvalidOperationException("bug"));

        var result = await fixture.Worker.RunBackfillChunksAsync(ThreeChunks(), default);

        result.Should().BeFalse();
        fixture.Windows.FailureFinalizeAttempts.Should().Be(1);
    }

    [Fact]
    public async Task AllChunksSucceed_UnchangedHappyPath()
    {
        var fixture = CreateFixture();
        fixture.Adapter.FetchRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
            Arg.Any<CancellationToken>()).Returns(call =>
                Success(call.ArgAt<DateOnly>(0), call.ArgAt<DateOnly>(1)));

        var result = await fixture.Worker.RunBackfillChunksAsync(ThreeChunks(), default);

        result.Should().BeTrue();
        fixture.Windows.Completed.Should().Be(3);
    }

    [Fact]
    public async Task LedgerNotDue_WakesAtExactThirtyMinuteRetryInsteadOfMonthlySchedule()
    {
        var time = new FakeTimeProvider(
            new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero));
        var adapter = Substitute.For<IInflationAdapter>();
        adapter.Source.Returns("evds");
        var windows = Substitute.For<IIngestionWindowRepository>();
        windows.PlanWindowsAsync(Arg.Any<IngestionWindowScope>(), Arg.Any<DateOnly>(),
                Arg.Any<DateOnly>(), Arg.Any<int>(), Arg.Any<IngestionCadence>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var due = time.GetUtcNow().AddMinutes(30);
        var claimCalls = 0;
        windows.ClaimNextAsync(Arg.Any<IngestionWindowScope>(), Arg.Any<string>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref claimCalls);
                return new WindowClaimResult(WindowClaimStatus.NotDue, NextAttemptAt: due);
            });
        var worker = new EvdsInflationWorker(adapter, windows,
            new ConfigurationBuilder().Build(), time,
            NullLogger<EvdsInflationWorker>.Instance);
        using var stop = new CancellationTokenSource();

        var execution = worker.RunAsync(stop.Token);
        await WaitUntilAsync(() => Volatile.Read(ref claimCalls) >= 1);

        time.Advance(TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(59));
        await Task.Yield();
        Volatile.Read(ref claimCalls).Should().Be(1);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => Volatile.Read(ref claimCalls) >= 2);

        stop.Cancel();
        await execution.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static Fixture CreateFixture()
    {
        var adapter = Substitute.For<IInflationAdapter>();
        adapter.Source.Returns("evds");
        var windows = new RecordingWindows();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            { ["IngestionWorkers:EvdsInflation:FailureFinalizeTimeoutMs"] = "20" }).Build();
        var worker = new EvdsInflationWorker(adapter, windows,
            configuration, TimeProvider.System,
            NullLogger<EvdsInflationWorker>.Instance);
        return new Fixture(adapter, windows, worker);
    }

    private static IReadOnlyList<(DateOnly From, DateOnly To)> ThreeChunks() =>
    [
        (new(2020, 1, 1), new(2020, 1, 1)),
        (new(2020, 2, 1), new(2020, 2, 1)),
        (new(2020, 3, 1), new(2020, 3, 1)),
    ];

    private static AdapterOutcome<InflationRate> Success(DateOnly from, DateOnly to)
    {
        var records = new List<InflationRate>();
        for (var month = from; month <= to; month = month.AddMonths(1))
            records.Add(new InflationRate
            {
                PeriodDate = month,
                IndexValue = 100,
                Source = InflationSources.Tuik,
            });
        return AdapterOutcome<InflationRate>.Data(records, records.Count);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(1);
        while (!predicate())
        {
            if (DateTime.UtcNow >= timeout)
                throw new TimeoutException("Asynchronous worker condition was not reached.");
            await Task.Yield();
        }
    }

    private sealed record Fixture(
        IInflationAdapter Adapter, RecordingWindows Windows, EvdsInflationWorker Worker);

    private sealed class RecordingWindows : IIngestionWindowRepository
    {
        private sealed record Row(Guid Id, IngestionWindowRange Range)
        {
            public string State { get; set; } = IngestionWindowStates.Pending;
            public string? Code { get; set; }
        }
        private readonly List<Row> _rows = [];
        public List<DateOnly> Claimed { get; } = [];
        public int PermanentFailures { get; private set; }
        public int Cancelled { get; private set; }
        public int Completed { get; private set; }
        public bool BlockFailureFinalization { get; set; }
        public int FailureFinalizeAttempts { get; private set; }

        public Task<IngestionFreshnessState> ReadFreshnessStateAsync(CancellationToken ct) =>
            Task.FromResult(new IngestionFreshnessState(DateTimeOffset.UnixEpoch, [], []));

        public Task<MarketCalendarReadiness> CheckCalendarReadinessAsync(
            IngestionWindowScope scope, DateOnly from, DateOnly to, CancellationToken ct) =>
            Task.FromResult(MarketCalendarReadiness.NotRequired);

        public Task<MarketCalendarTargetResolution> ResolveLatestExpectedObservationAsync(
            string calendarCode, DateOnly notAfter, CancellationToken ct) =>
            Task.FromResult(new MarketCalendarTargetResolution(
                true, notAfter, null, calendarCode, "calendar_target_ready"));

        public Task<IReadOnlySet<DateOnly>> GetExpectedNoDataDatesAsync(
            Guid releaseId, DateOnly from, DateOnly to, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<DateOnly>>(new HashSet<DateOnly>());

        public Task PlanWindowsAsync(IngestionWindowScope scope, DateOnly start, DateOnly end,
            int chunkSize, IngestionCadence cadence, CancellationToken ct) => Task.CompletedTask;

        public Task EnsureWindowsAsync(IngestionWindowScope scope,
            IReadOnlyList<IngestionWindowRange> ranges, CancellationToken ct)
        {
            foreach (var range in ranges)
                if (_rows.All(row => row.Range != range)) _rows.Add(new(Guid.NewGuid(), range));
            return Task.CompletedTask;
        }

        public Task<WindowClaimResult> ClaimNextAsync(IngestionWindowScope scope, string owner,
            TimeSpan leaseDuration, CancellationToken ct)
        {
            var row = _rows.OrderBy(item => item.Range.From)
                .FirstOrDefault(item => item.State is not (IngestionWindowStates.Succeeded
                    or IngestionWindowStates.ExpectedNoData));
            if (row is null) return Task.FromResult(new WindowClaimResult(WindowClaimStatus.Complete));
            if (row.State == IngestionWindowStates.PermanentFailed)
                return Task.FromResult(new WindowClaimResult(
                    WindowClaimStatus.PermanentBlocked, OutcomeCode: row.Code));
            row.State = IngestionWindowStates.Running;
            Claimed.Add(row.Range.From);
            return Task.FromResult(new WindowClaimResult(WindowClaimStatus.Claimed,
                new IngestionWindowClaim(row.Id, Guid.NewGuid(), scope,
                    row.Range.From, row.Range.To, owner, Guid.NewGuid(), 1)));
        }

        public Task<bool> RenewLeaseAsync(IngestionWindowClaim claim, TimeSpan duration,
            CancellationToken ct) => Task.FromResult(true);

        public Task CompletePriceAsync(IngestionWindowClaim claim, AdapterOutcome<PricePoint> outcome,
            IngestionWindowCounts counts, CancellationToken ct) => throw new NotSupportedException();

        public Task CompleteInflationAsync(IngestionWindowClaim claim,
            AdapterOutcome<InflationRate> outcome, IngestionWindowCounts counts, CancellationToken ct)
        {
            var row = _rows.Single(item => item.Id == claim.WindowId);
            row.State = IngestionWindowStates.Succeeded;
            row.Code = outcome.Code;
            Completed++;
            return Task.CompletedTask;
        }

        public async Task RecordFailureAsync(IngestionWindowClaim claim, string state,
            AdapterOutcomeKind kind, IngestionWindowCounts counts, string outcomeCode,
            string errorCode, string? detail, DateTimeOffset nextAttemptAt, CancellationToken ct)
        {
            FailureFinalizeAttempts++;
            if (BlockFailureFinalization)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            var row = _rows.Single(item => item.Id == claim.WindowId);
            row.State = state;
            row.Code = outcomeCode;
            if (state == IngestionWindowStates.PermanentFailed) PermanentFailures++;
            if (state == IngestionWindowStates.Cancelled) Cancelled++;
        }

        public Task<WindowTerminalState?> GetTerminalStateAsync(Guid windowId, CancellationToken ct) =>
            Task.FromResult<WindowTerminalState?>(null);

        public Task RequeuePermanentAsync(Guid windowId, DateTimeOffset nextAttemptAt,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
