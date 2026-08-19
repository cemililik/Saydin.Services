using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;
using Saydin.PriceIngestion.Workers;
using Saydin.Shared.Constants;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.IntegrationTests;

[Collection(IngestionDatabaseCollection.Name)]
public sealed class WorkerLedgerIntegrationTests(IngestionDatabaseFixture database)
{
    [Fact]
    public async Task PriceWorker_RestartClaimsFailedSecondBeforeThird_OnRealPostgres()
    {
        const string source = ProviderSources.CoinGecko;
        var assetId = await database.CreateAssetAsync(source);
        var asset = new Asset
        {
            Id = assetId, Symbol = "ITPRICE", DisplayName = "IT price",
            Category = AssetCategory.Crypto, IsActive = true, Source = source,
            SourceId = $"it-{assetId:N}",
        };
        try
        {
            var repository = database.Repository();
            var firstAdapter = new SequencedPriceAdapter(source, failSecond: true);
            var firstWorker = Worker(firstAdapter, repository);

            (await firstWorker.BackfillChunkedAsync(
                asset, new(2080, 1, 1), new(2080, 1, 3), default)).Should().BeFalse();
            firstAdapter.Calls.Should().Equal(new DateOnly(2080, 1, 1), new DateOnly(2080, 1, 2));

            var restartAdapter = new SequencedPriceAdapter(source, failSecond: false);
            var restart = Worker(restartAdapter, repository);
            (await restart.BackfillChunkedAsync(
                asset, new(2080, 1, 1), new(2080, 1, 3), default)).Should().BeTrue();
            restartAdapter.Calls.Should().Equal(new DateOnly(2080, 1, 2), new DateOnly(2080, 1, 3));
        }
        finally { await database.CleanupAssetAsync(assetId); }

        TestPriceWorker Worker(IExternalPriceAdapter adapter, IIngestionWindowRepository windows) =>
            new(adapter, new AssetRepositoryStub(), windows, new ConfigurationBuilder().Build(),
                TimeProvider.System, NullLogger.Instance);
    }

    [Fact]
    public async Task EvdsWorker_RestartClaimsFailedSecondBeforeThird_OnRealPostgres()
    {
        const string source = "evds";
        var repository = database.Repository();
        var chunks = new (DateOnly From, DateOnly To)[]
        {
            (new(2081, 1, 1), new(2081, 1, 1)),
            (new(2081, 2, 1), new(2081, 2, 1)),
            (new(2081, 3, 1), new(2081, 3, 1)),
        };
        try
        {
            var firstAdapter = new SequencedInflationAdapter(source, failSecond: true);
            var first = InflationWorker(firstAdapter, repository);
            (await first.RunBackfillChunksAsync(chunks, default)).Should().BeFalse();
            firstAdapter.Calls.Should().Equal(chunks[0].From, chunks[1].From);
            await database.ExecuteAsync("""
                UPDATE ingestion_windows SET next_attempt_at=clock_timestamp()
                 WHERE source=@source AND asset_id IS NULL AND state='retryable_failed'
                """, new Npgsql.NpgsqlParameter("source", source));

            var restartAdapter = new SequencedInflationAdapter(source, failSecond: false);
            var restart = InflationWorker(restartAdapter, repository);
            (await restart.RunBackfillChunksAsync(chunks, default)).Should().BeTrue();
            restartAdapter.Calls.Should().Equal(chunks[1].From, chunks[2].From);
        }
        finally
        {
            await database.CleanupGlobalAsync(source);
            await database.ExecuteAsync("""
                DELETE FROM inflation_rates
                 WHERE source='tuik' AND period_date BETWEEN '2081-01-01' AND '2081-03-01'
                """);
        }

        EvdsInflationWorker InflationWorker(
            IInflationAdapter adapter, IIngestionWindowRepository windows) =>
            new(adapter, windows, new ConfigurationBuilder().Build(), TimeProvider.System,
                NullLogger<EvdsInflationWorker>.Instance);
    }

    private sealed class TestPriceWorker(
        IExternalPriceAdapter adapter,
        IPriceIngestionRepository assets,
        IIngestionWindowRepository windows,
        IConfiguration configuration,
        TimeProvider timeProvider,
        Microsoft.Extensions.Logging.ILogger logger)
        : BaseAssetWorker(adapter, assets, windows, configuration, timeProvider, logger)
    {
        protected override DateOnly BackfillStartDate => new(2080, 1, 1);
        protected override int ChunkDays => 1;
        protected override string WorkerConfigKey => "Integration";
        protected override TimeOnly DefaultDailyRunUtcTime => TimeOnly.MinValue;
        protected override TimeSpan LogicalRetryDelay => TimeSpan.Zero;
    }

    private sealed class SequencedPriceAdapter(string source, bool failSecond) : IExternalPriceAdapter
    {
        public string Source => source;
        public List<DateOnly> Calls { get; } = [];
        public Task<AdapterOutcome<PricePoint>> FetchRangeAsync(
            PriceFetchRequest request, CancellationToken ct)
        {
            Calls.Add(request.From);
            if (failSecond && Calls.Count == 2)
                return Task.FromResult(AdapterOutcome<PricePoint>.RetryableFailure("http_503"));
            return Task.FromResult(AdapterOutcome<PricePoint>.Data(
                [AuthorityTestData.CoinGecko(
                    request.AssetId, request.SourceId, request.From, 10m)], 1));
        }
    }

    private sealed class SequencedInflationAdapter(string source, bool failSecond) : IInflationAdapter
    {
        public string Source => source;
        public List<DateOnly> Calls { get; } = [];
        public Task<AdapterOutcome<InflationRate>> FetchRangeAsync(
            DateOnly from, DateOnly to, CancellationToken ct)
        {
            Calls.Add(from);
            if (failSecond && Calls.Count == 2)
                return Task.FromResult(AdapterOutcome<InflationRate>.RetryableFailure("http_503"));
            return Task.FromResult(AdapterOutcome<InflationRate>.Data(
                [AuthorityTestData.Evds(from)], 1));
        }
    }

    private sealed class AssetRepositoryStub : IPriceIngestionRepository
    {
        public Task<IReadOnlyList<Asset>> GetActiveAssetsBySourceAsync(string source, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Asset>>([]);
        public Task UpsertPricePointsAsync(IReadOnlyList<PricePoint> pricePoints, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<DateOnly?> GetLatestPriceDateAsync(Guid assetId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlySet<DateOnly>> GetExistingDatesAsync(
            Guid assetId, DateOnly from, DateOnly to, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlySet<DateOnly>> GetMarketHolidaysAsync(
            Guid assetId, DateOnly from, DateOnly to, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<DateOnly>>(new HashSet<DateOnly>());
    }
}
