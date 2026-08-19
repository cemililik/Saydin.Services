using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;
using Saydin.PriceIngestion.Workers;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Tests.Workers;

public class BaseAssetWorkerTests
{
    [Fact]
    public void ComputeMissingRanges_InteriorGap_IsNotHiddenByLaterData()
    {
        var ranges = BaseAssetWorker.ComputeMissingRanges(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 5),
            new HashSet<DateOnly> { new(2024, 1, 1), new(2024, 1, 5) });

        ranges.Should().Equal((new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4)));
    }

    [Fact]
    public async Task SecondRetryableChunk_StopsThird_AndRestartClaimsSecondBeforeThird()
    {
        var fixture = CreateFixture();
        var calls = 0;
        fixture.Adapter.FetchRangeAsync(Arg.Any<PriceFetchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls++;
                var request = call.Arg<PriceFetchRequest>();
                if (calls == 2)
                    return AdapterOutcome<PricePoint>.RetryableFailure("http_503");
                return Success(request);
            });

        var first = await fixture.Worker.BackfillChunkedAsync(
            fixture.Asset, new(2024, 1, 1), new(2024, 1, 3), default);

        first.Should().BeFalse();
        calls.Should().Be(2);
        fixture.Windows.CompletedRanges.Should().Equal(
            new IngestionWindowRange(new(2024, 1, 1), new(2024, 1, 1)));

        var second = await fixture.Worker.BackfillChunkedAsync(
            fixture.Asset, new(2024, 1, 1), new(2024, 1, 3), default);

        second.Should().BeTrue();
        fixture.Windows.ClaimedRanges.Should().Equal(
            new IngestionWindowRange(new(2024, 1, 1), new(2024, 1, 1)),
            new IngestionWindowRange(new(2024, 1, 2), new(2024, 1, 2)),
            new IngestionWindowRange(new(2024, 1, 2), new(2024, 1, 2)),
            new IngestionWindowRange(new(2024, 1, 3), new(2024, 1, 3)));
    }

    [Fact]
    public async Task PermanentOrPartialOutcome_BlocksThirdAndThrows()
    {
        var fixture = CreateFixture();
        var calls = 0;
        fixture.Adapter.FetchRangeAsync(Arg.Any<PriceFetchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls++;
                var request = call.Arg<PriceFetchRequest>();
                return calls == 2
                    ? AdapterOutcome<PricePoint>.PartialRejected([], 1, 1, "parse_rejected")
                    : Success(request);
            });

        var act = () => fixture.Worker.BackfillChunkedAsync(
            fixture.Asset, new(2024, 1, 1), new(2024, 1, 3), default);

        await act.Should().ThrowAsync<PermanentIngestionWindowException>();
        calls.Should().Be(2);
        fixture.Windows.PermanentFailureCount.Should().Be(1);
    }

    [Fact]
    public async Task ClaimRepositoryFault_PropagatesBeforeAdapter()
    {
        var fixture = CreateFixture();
        fixture.Windows.ClaimException = new TimeoutException("claim failed");

        var act = () => fixture.Worker.BackfillChunkedAsync(
            fixture.Asset, new(2024, 1, 1), new(2024, 1, 1), default);

        await act.Should().ThrowAsync<TimeoutException>();
        await fixture.Adapter.DidNotReceiveWithAnyArgs()
            .FetchRangeAsync(default!, default);
    }

    [Fact]
    public async Task CompletionFault_StopsLaterWindow()
    {
        var fixture = CreateFixture();
        fixture.Windows.CompleteExceptionAt = 2;
        fixture.Adapter.FetchRangeAsync(Arg.Any<PriceFetchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Success(call.Arg<PriceFetchRequest>()));

        var act = () => fixture.Worker.BackfillChunkedAsync(
            fixture.Asset, new(2024, 1, 1), new(2024, 1, 3), default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        fixture.Windows.ClaimedRanges.Should().HaveCount(2);
    }

    [Fact]
    public async Task CommitAckLoss_TerminalRereadContinuesWithoutDuplicateAttempt()
    {
        var fixture = CreateFixture();
        fixture.Windows.ThrowAfterCommitAt = 2;
        fixture.Adapter.FetchRangeAsync(Arg.Any<PriceFetchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Success(call.Arg<PriceFetchRequest>()));

        var result = await fixture.Worker.BackfillChunkedAsync(
            fixture.Asset, new(2024, 1, 1), new(2024, 1, 3), default);

        result.Should().BeTrue();
        fixture.Windows.ClaimedRanges.Should().HaveCount(3);
        fixture.Windows.CompletedRanges.Should().HaveCount(3);
    }

    [Fact]
    public async Task Cancellation_IsTerminalizedWithIndependentBoundedToken()
    {
        var fixture = CreateFixture();
        using var cts = new CancellationTokenSource();
        fixture.Adapter.FetchRangeAsync(Arg.Any<PriceFetchRequest>(), Arg.Any<CancellationToken>())
            .Returns<AdapterOutcome<PricePoint>>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        var act = () => fixture.Worker.BackfillChunkedAsync(
            fixture.Asset, new(2024, 1, 1), new(2024, 1, 1), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fixture.Windows.CancelledCount.Should().Be(1);
    }

    [Fact]
    public async Task ShutdownCancellation_BlockingFinalizerIsBounded_AndOriginalCancellationPropagates()
    {
        var fixture = CreateFixture();
        fixture.Windows.BlockFailureFinalization = true;
        using var cts = new CancellationTokenSource();
        fixture.Adapter.FetchRangeAsync(Arg.Any<PriceFetchRequest>(), Arg.Any<CancellationToken>())
            .Returns<AdapterOutcome<PricePoint>>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        var act = () => fixture.Worker.BackfillChunkedAsync(
            fixture.Asset, new(2024, 1, 1), new(2024, 1, 1), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fixture.Windows.FailureFinalizeAttempts.Should().Be(1);
    }

    [Fact]
    public async Task NonCancellationException_BlockingFailureFinalizerIsBounded()
    {
        var fixture = CreateFixture();
        fixture.Windows.BlockFailureFinalization = true;
        fixture.Adapter.FetchRangeAsync(Arg.Any<PriceFetchRequest>(), Arg.Any<CancellationToken>())
            .Returns<AdapterOutcome<PricePoint>>(_ => throw new InvalidOperationException("bug"));

        var act = () => fixture.Worker.BackfillChunkedAsync(
            fixture.Asset, new(2024, 1, 1), new(2024, 1, 1), default);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fixture.Windows.FailureFinalizeAttempts.Should().Be(1);
    }

    [Fact]
    public async Task RenewalFailure_IsControlFlow_AndDoesNotWritePermanentFailure()
    {
        var fixture = CreateFixture(leaseDuration: TimeSpan.FromMilliseconds(30));
        fixture.Windows.RenewException = new TimeoutException("db unavailable");
        fixture.Adapter.FetchRangeAsync(Arg.Any<PriceFetchRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), call.Arg<CancellationToken>());
                return Success(call.Arg<PriceFetchRequest>());
            });

        var act = () => fixture.Worker.BackfillChunkedAsync(
            fixture.Asset, new(2024, 1, 1), new(2024, 1, 1), default);

        await act.Should().ThrowAsync<IngestionLeaseLostException>();
        fixture.Windows.PermanentFailureCount.Should().Be(0);
        fixture.Windows.CompletedRanges.Should().BeEmpty();
    }

    [Fact]
    public async Task WrongAssetOrEmptyUnknown_CannotReachSuccessfulCompletion()
    {
        var fixture = CreateFixture();
        fixture.Adapter.FetchRangeAsync(Arg.Any<PriceFetchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => AdapterOutcome<PricePoint>.Data(
                [new PricePoint
                {
                    AssetId = Guid.NewGuid(),
                    PriceDate = call.Arg<PriceFetchRequest>().From,
                    Close = 1,
                }], 1));

        var act = () => fixture.Worker.BackfillChunkedAsync(
            fixture.Asset, new(2024, 1, 1), new(2024, 1, 1), default);

        await act.Should().ThrowAsync<PermanentIngestionWindowException>();
        fixture.Windows.CompletedRanges.Should().BeEmpty();
    }

    [Fact]
    public void OpenExchangeRatesWorker_TargetsCompletedPreviousUtcDay()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            { ["ExternalApis:OpenExchangeRates:AppId"] = "test" }).Build();
        var adapter = new OpenExchangeRatesAdapter(factory, configuration, TimeProvider.System,
            NullLogger<OpenExchangeRatesAdapter>.Instance);
        var worker = new OpenExchangeRatesWorker(adapter,
            Substitute.For<IPriceIngestionRepository>(),
            Substitute.For<IIngestionWindowRepository>(), configuration, TimeProvider.System,
            NullLogger<OpenExchangeRatesWorker>.Instance);

        worker.ResolveTargetDate(new DateTime(2026, 8, 18, 22, 0, 0, DateTimeKind.Utc))
            .Should().Be(new DateOnly(2026, 8, 17));
    }

    [Fact]
    public void TcmbWorker_TargetsCompletedIstanbulYesterday()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var configuration = new ConfigurationBuilder().Build();
        var adapter = new TcmbAdapter(factory, NullLogger<TcmbAdapter>.Instance);
        var worker = new TcmbWorker(adapter, Substitute.For<IPriceIngestionRepository>(),
            Substitute.For<IIngestionWindowRepository>(), configuration, TimeProvider.System,
            NullLogger<TcmbWorker>.Instance);

        worker.ResolveTargetDate(new DateTime(2026, 8, 18, 20, 59, 0, DateTimeKind.Utc))
            .Should().Be(new DateOnly(2026, 8, 17));
        worker.ResolveTargetDate(new DateTime(2026, 8, 18, 21, 1, 0, DateTimeKind.Utc))
            .Should().Be(new DateOnly(2026, 8, 18));
    }

    [Fact]
    public void TwelveDataWorker_TargetsTodayOnlyAfterIstanbulCloseAndBoundedDelay()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ExternalApis:TwelveData:ApiKey"] = "test",
                ["IngestionWorkers:TwelveData:ProviderSettlementDelayMinutes"] = "10",
            }).Build();
        var adapter = new TwelveDataAdapter(factory, configuration,
            NullLogger<TwelveDataAdapter>.Instance);
        var worker = new TwelveDataWorker(adapter, Substitute.For<IPriceIngestionRepository>(),
            Substitute.For<IIngestionWindowRepository>(), configuration, TimeProvider.System,
            NullLogger<TwelveDataWorker>.Instance);

        worker.ResolveTargetDate(new DateTime(2026, 8, 18, 15, 19, 59, DateTimeKind.Utc))
            .Should().Be(new DateOnly(2026, 8, 17));
        worker.ResolveTargetDate(new DateTime(2026, 8, 18, 15, 20, 0, DateTimeKind.Utc))
            .Should().Be(new DateOnly(2026, 8, 18));
    }

    [Fact]
    public async Task ContractV2_CalendarNotReady_StopsBeforePlanClaimAndProvider()
    {
        var fixture = CreateFixture(source: "tcmb", contractVersion: 2);
        fixture.Windows.Readiness = new(true, false, null,
            "tcmb_indicative_fx", "calendar_coverage_missing");

        var result = await fixture.Worker.BackfillChunkedAsync(
            fixture.Asset, new(2026, 8, 18), new(2026, 8, 18), default);

        result.Should().BeFalse();
        fixture.Windows.EnsureCalls.Should().Be(0);
        fixture.Windows.ClaimedRanges.Should().BeEmpty();
        await fixture.Adapter.DidNotReceiveWithAnyArgs().FetchRangeAsync(default!, default);
    }

    [Fact]
    public async Task ContractV2_TestOnlySyntheticCoverageExtension_ProviderCallsMoveFromZeroToOne()
    {
        var fixture = CreateFixture(source: "tcmb", contractVersion: 2);
        var nextDay = new DateOnly(2026, 8, 18);
        fixture.Windows.Readiness = new(true, false, null,
            "tcmb_indicative_fx", "calendar_coverage_missing");

        (await fixture.Worker.BackfillChunkedAsync(
            fixture.Asset, nextDay, nextDay, default)).Should().BeFalse();
        await fixture.Adapter.DidNotReceiveWithAnyArgs().FetchRangeAsync(default!, default);

        // This release id is deliberately synthetic test state, not official evidence.
        fixture.Windows.Readiness = new(true, true, Guid.Parse(
            "ca1f0000-0000-7000-8000-000000000001"),
            "tcmb_indicative_fx", "calendar_ready");
        fixture.Adapter.FetchRangeAsync(Arg.Any<PriceFetchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Success(call.Arg<PriceFetchRequest>()));

        (await fixture.Worker.BackfillChunkedAsync(
            fixture.Asset, nextDay, nextDay, default)).Should().BeTrue();
        await fixture.Adapter.Received(1).FetchRangeAsync(
            Arg.Is<PriceFetchRequest>(request => request.From == nextDay && request.To == nextDay),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContractV2_RegisteredClosedDay_TerminalizesExpectedNoData()
    {
        var fixture = CreateFixture(source: "tcmb", contractVersion: 2);
        var releaseId = Guid.NewGuid();
        var day = new DateOnly(2026, 8, 16);
        fixture.Windows.Readiness = new(true, true, releaseId,
            "tcmb_indicative_fx", "calendar_ready");
        fixture.Windows.ExpectedNoDataDates = new HashSet<DateOnly> { day };
        fixture.Adapter.FetchRangeAsync(Arg.Any<PriceFetchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Success(call.Arg<PriceFetchRequest>()));

        var result = await fixture.Worker.BackfillChunkedAsync(
            fixture.Asset, day, day, default);

        result.Should().BeTrue();
        fixture.Windows.CompletedRanges.Should().ContainSingle();
        await fixture.Adapter.Received(1).FetchRangeAsync(
            Arg.Is<PriceFetchRequest>(request => request.CalendarClosedDates.SetEquals(new[] { day })),
            Arg.Any<CancellationToken>());
    }

    private static Fixture CreateFixture(
        TimeSpan? leaseDuration = null, string source = "test", int contractVersion = 1)
    {
        var adapter = Substitute.For<IExternalPriceAdapter>();
        adapter.Source.Returns(source);
        var asset = new Asset
        {
            Id = Guid.NewGuid(),
            Symbol = "TEST",
            DisplayName = "Test",
            Category = AssetCategory.Crypto,
            IsActive = true,
            Source = source,
            SourceId = "test-id",
        };
        var assets = Substitute.For<IPriceIngestionRepository>();
        assets.GetActiveAssetsBySourceAsync(source, Arg.Any<CancellationToken>())
            .Returns([asset]);
        assets.GetMarketHolidaysAsync(asset.Id, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
            Arg.Any<CancellationToken>()).Returns(new HashSet<DateOnly>());
        var windows = new RecordingWindowRepository();
        var worker = new TestWorker(adapter, assets, windows,
            new ConfigurationBuilder().Build(), TimeProvider.System,
            NullLogger.Instance, leaseDuration ?? TimeSpan.FromMinutes(1), contractVersion);
        return new Fixture(adapter, windows, worker, asset);
    }

    private static AdapterOutcome<PricePoint> Success(PriceFetchRequest request)
    {
        var records = AdapterCompleteness.Dates(request.From, request.To)
            .Where(date => !request.CalendarClosedDates.Contains(date))
            .Select(date => new PricePoint
            {
                AssetId = request.AssetId,
                PriceDate = date,
                Close = 1,
            }).ToArray();
        return records.Length == 0
            ? AdapterOutcome<PricePoint>.ExpectedNoData(request.CalendarClosedDates, "market_closed")
            : AdapterOutcome<PricePoint>.Data(records, records.Length, request.CalendarClosedDates);
    }

    private sealed record Fixture(
        IExternalPriceAdapter Adapter,
        RecordingWindowRepository Windows,
        TestWorker Worker,
        Asset Asset);

    private sealed class TestWorker(
        IExternalPriceAdapter adapter,
        IPriceIngestionRepository assets,
        IIngestionWindowRepository windows,
        IConfiguration configuration,
        TimeProvider timeProvider,
        Microsoft.Extensions.Logging.ILogger logger,
        TimeSpan leaseDuration,
        int contractVersion)
        : BaseAssetWorker(adapter, assets, windows, configuration, timeProvider, logger)
    {
        protected override DateOnly BackfillStartDate => new(2024, 1, 1);
        protected override int ChunkDays => 1;
        protected override string WorkerConfigKey => "Test";
        protected override TimeOnly DefaultDailyRunUtcTime => TimeOnly.MinValue;
        protected override TimeSpan LogicalRetryDelay => TimeSpan.Zero;
        protected override TimeSpan LeaseDuration => leaseDuration;
        protected override TimeSpan FailureFinalizeTimeout => TimeSpan.FromMilliseconds(20);
        protected override int ContractVersion => contractVersion;
    }

    private sealed class RecordingWindowRepository : IIngestionWindowRepository
    {
        private sealed record Row(Guid Id, IngestionWindowRange Range)
        {
            public string State { get; set; } = IngestionWindowStates.Pending;
            public string? OutcomeCode { get; set; }
            public Guid? JobId { get; set; }
            public Guid? Token { get; set; }
        }

        private readonly List<Row> _rows = [];
        public List<IngestionWindowRange> ClaimedRanges { get; } = [];
        public List<IngestionWindowRange> CompletedRanges { get; } = [];
        public Exception? ClaimException { get; set; }
        public int? CompleteExceptionAt { get; set; }
        public int? ThrowAfterCommitAt { get; set; }
        public Exception? RenewException { get; set; }
        public int PermanentFailureCount { get; private set; }
        public int CancelledCount { get; private set; }
        public bool BlockFailureFinalization { get; set; }
        public int FailureFinalizeAttempts { get; private set; }
        public int EnsureCalls { get; private set; }
        public MarketCalendarReadiness Readiness { get; set; } = MarketCalendarReadiness.NotRequired;
        public IReadOnlySet<DateOnly> ExpectedNoDataDates { get; set; } = new HashSet<DateOnly>();
        private int _completeCalls;

        public Task PlanWindowsAsync(IngestionWindowScope scope, DateOnly start, DateOnly end,
            int chunkSize, IngestionCadence cadence, CancellationToken ct) => Task.CompletedTask;

        public Task EnsureWindowsAsync(IngestionWindowScope scope,
            IReadOnlyList<IngestionWindowRange> ranges, CancellationToken ct)
        {
            EnsureCalls++;
            foreach (var range in ranges)
                if (_rows.All(row => row.Range != range)) _rows.Add(new Row(Guid.NewGuid(), range));
            return Task.CompletedTask;
        }

        public Task<MarketCalendarReadiness> CheckCalendarReadinessAsync(
            IngestionWindowScope scope, DateOnly from, DateOnly to, CancellationToken ct) =>
            Task.FromResult(Readiness);

        public Task<IReadOnlySet<DateOnly>> GetExpectedNoDataDatesAsync(
            Guid releaseId, DateOnly from, DateOnly to, CancellationToken ct) =>
            Task.FromResult(ExpectedNoDataDates);

        public Task<WindowClaimResult> ClaimNextAsync(IngestionWindowScope scope, string owner,
            TimeSpan duration, CancellationToken ct)
        {
            if (ClaimException is not null) throw ClaimException;
            var row = _rows.OrderBy(item => item.Range.From)
                .FirstOrDefault(item => item.State is not (IngestionWindowStates.Succeeded
                    or IngestionWindowStates.ExpectedNoData));
            if (row is null) return Task.FromResult(new WindowClaimResult(WindowClaimStatus.Complete));
            if (row.State == IngestionWindowStates.PermanentFailed)
                return Task.FromResult(new WindowClaimResult(
                    WindowClaimStatus.PermanentBlocked, OutcomeCode: row.OutcomeCode));
            row.State = IngestionWindowStates.Running;
            row.JobId = Guid.NewGuid();
            row.Token = Guid.NewGuid();
            ClaimedRanges.Add(row.Range);
            return Task.FromResult(new WindowClaimResult(WindowClaimStatus.Claimed,
                new IngestionWindowClaim(row.Id, row.JobId.Value, scope,
                    row.Range.From, row.Range.To, owner, row.Token.Value, 1,
                    Readiness.ReleaseId)));
        }

        public Task<bool> RenewLeaseAsync(IngestionWindowClaim claim, TimeSpan duration, CancellationToken ct)
        {
            if (RenewException is not null) throw RenewException;
            return Task.FromResult(true);
        }

        public Task CompletePriceAsync(IngestionWindowClaim claim, AdapterOutcome<PricePoint> outcome,
            IngestionWindowCounts counts, CancellationToken ct)
        {
            _completeCalls++;
            if (CompleteExceptionAt == _completeCalls)
                throw new InvalidOperationException("before commit");
            var row = _rows.Single(item => item.Id == claim.WindowId);
            row.State = counts.AcceptedDistinctCount == 0
                ? IngestionWindowStates.ExpectedNoData : IngestionWindowStates.Succeeded;
            row.OutcomeCode = outcome.Code;
            CompletedRanges.Add(row.Range);
            if (ThrowAfterCommitAt == _completeCalls)
                throw new InvalidOperationException("ack lost");
            return Task.CompletedTask;
        }

        public Task CompleteInflationAsync(IngestionWindowClaim claim,
            AdapterOutcome<InflationRate> outcome, IngestionWindowCounts counts, CancellationToken ct) =>
            throw new NotSupportedException();

        public async Task RecordFailureAsync(IngestionWindowClaim claim, string state,
            AdapterOutcomeKind kind, IngestionWindowCounts counts, string outcomeCode,
            string errorCode, string? detail, DateTimeOffset nextAttemptAt, CancellationToken ct)
        {
            FailureFinalizeAttempts++;
            if (BlockFailureFinalization)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            var row = _rows.Single(item => item.Id == claim.WindowId);
            row.State = state;
            row.OutcomeCode = outcomeCode;
            if (state == IngestionWindowStates.PermanentFailed) PermanentFailureCount++;
            if (state == IngestionWindowStates.Cancelled) CancelledCount++;
        }

        public Task<WindowTerminalState?> GetTerminalStateAsync(Guid windowId, CancellationToken ct)
        {
            var row = _rows.Single(item => item.Id == windowId);
            WindowTerminalState? terminal = row.State is IngestionWindowStates.Succeeded
                or IngestionWindowStates.ExpectedNoData
                ? new WindowTerminalState(row.State, row.OutcomeCode) : null;
            return Task.FromResult(terminal);
        }

        public Task RequeuePermanentAsync(Guid windowId, DateTimeOffset nextAttemptAt,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
