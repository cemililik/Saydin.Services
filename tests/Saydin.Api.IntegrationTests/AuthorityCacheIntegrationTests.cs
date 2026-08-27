using FluentAssertions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Saydin.Api.IntegrationTests.Fixtures;
using Saydin.Api.Models.Responses;
using Saydin.Api.Repositories;
using Saydin.Api.Services;
using Saydin.Shared.Entities;

namespace Saydin.Api.IntegrationTests;

[Collection(RedisCollection.Name)]
public sealed class AuthorityCacheIntegrationTests(RedisFixture redis)
{
    private static readonly AssetCatalogVersion Catalog = new()
    {
        Revision = 7,
        CatalogSha256 = Enumerable.Repeat((byte)0x7c, 32).ToArray(),
    };
    private static string CatalogHash => Convert.ToHexString(Catalog.CatalogSha256).ToLowerInvariant();

    [SkippableFact]
    public async Task RealRedis_LegacyPriceNamespaceCannotBypassRepositoryBoundary()
    {
        Skip.IfNot(redis.Available, redis.SkipReason);
        var symbol = $"CACHE{Guid.NewGuid():N}".ToUpperInvariant();
        var date = new DateOnly(2026, 8, 18);
        var oldKey = $"price:{symbol}:{date:yyyy-MM-dd}";
        var newKey = $"authority-final-v1:catalog:{Catalog.Token}:price:{symbol}:{date:yyyy-MM-dd}";
        var helper = new RedisCacheHelper(redis.Multiplexer!,
            NullLogger<RedisCacheHelper>.Instance);
        var repository = new StaticPriceRepository(FinalPoint(date));
        var service = new AssetService(
            repository,
            helper,
            new AssetSymbolIndex(),
            new PassthroughAssetNameLocalizer(),
            TimeProvider.System,
            new PassthroughStringLocalizer(),
            NullLogger<AssetService>.Instance);

        try
        {
            await helper.TrySetAsync(oldKey,
                new PricePoint { PriceDate = date, Close = 999m },
                TimeSpan.FromMinutes(5));

            var result = await service.GetPriceAsync(symbol, date, CancellationToken.None);

            result.Close.Should().Be(41m);
            repository.PriceReadCount.Should().Be(1);
            (await helper.TryGetAsync<PricePoint>(oldKey))!.Close.Should().Be(999m);
            (await helper.TryGetAsync<PriceCacheEntry>(newKey))!.Point!.Close.Should().Be(41m);
        }
        finally
        {
            await redis.Multiplexer!.GetDatabase().KeyDeleteAsync([oldKey, newKey]);
        }
    }

    [SkippableFact]
    public async Task RealRedis_CurrentKeysRejectCrossSourceDateBoundAndRangeContamination()
    {
        Skip.IfNot(redis.Available, redis.SkipReason);
        var symbol = $"CACHE{Guid.NewGuid():N}".ToUpperInvariant();
        var date = new DateOnly(2026, 8, 18);
        var exactKey = $"authority-final-v1:catalog:{Catalog.Token}:price:{symbol}:{date:yyyy-MM-dd}";
        var nearestKey = $"authority-final-v1:catalog:{Catalog.Token}:nearest-price:{symbol}:{date:yyyy-MM-dd}";
        var latestKey = $"authority-final-v1:catalog:{Catalog.Token}:latest-date:{symbol}";
        var rangeKey = $"authority-final-v1:catalog:{Catalog.Token}:prices:{symbol}:{date.AddDays(-1):yyyy-MM-dd}:{date.AddDays(1):yyyy-MM-dd}:daily";
        var helper = new RedisCacheHelper(redis.Multiplexer!,
            NullLogger<RedisCacheHelper>.Instance);
        var expected = FinalPoint(date);
        var repository = new StaticPriceRepository(expected);
        var service = new AssetService(
            repository, helper, new AssetSymbolIndex(),
            new PassthroughAssetNameLocalizer(), TimeProvider.System,
            new PassthroughStringLocalizer(), NullLogger<AssetService>.Instance);
        var identity = new AssetReadIdentity(
            expected.AssetId, symbol, ProviderSources.Tcmb);

        try
        {
            var crossSource = FinalPoint(date);
            crossSource.ProviderSource = ProviderSources.CoinGecko;
            crossSource.PriceKind = ObservationPriceKinds.DailyUtcReference;
            await helper.TrySetAsync(exactKey,
                new PriceCacheEntry(symbol, ProviderSources.CoinGecko, expected.AssetId,
                    date, null, crossSource, Catalog.Revision, CatalogHash), TimeSpan.FromMinutes(5));
            (await service.GetPriceAsync(symbol, date, CancellationToken.None)).Close.Should().Be(41m);

            var wrongDate = FinalPoint(date.AddDays(-1));
            await helper.TrySetAsync(exactKey,
                new PriceCacheEntry(symbol, ProviderSources.Tcmb, expected.AssetId,
                    date, null, wrongDate, Catalog.Revision, CatalogHash), TimeSpan.FromMinutes(5));
            (await service.GetPriceAsync(symbol, date, CancellationToken.None)).PriceDate.Should().Be(date);

            var outOfBound = FinalPoint(date.AddDays(-8));
            await helper.TrySetAsync(nearestKey,
                new PriceCacheEntry(symbol, ProviderSources.Tcmb, expected.AssetId,
                    date, 7, outOfBound, Catalog.Revision, CatalogHash), TimeSpan.FromMinutes(5));
            (await service.GetNearestPriceAsync(symbol, date, CancellationToken.None))
                .PriceDate.Should().Be(date);

            await helper.TrySetAsync(latestKey,
                new LatestPriceDateCacheEntry(
                    symbol, ProviderSources.CoinGecko, expected.AssetId, date.AddDays(-10),
                    Catalog.Revision, CatalogHash),
                TimeSpan.FromMinutes(5));
            (await service.GetLatestPriceDateAsync(symbol, CancellationToken.None))
                .Should().Be(date);
            await helper.TrySetAsync(latestKey,
                new LatestPriceDateCacheEntry(
                    symbol, ProviderSources.Tcmb, Guid.NewGuid(), date.AddDays(-10),
                    Catalog.Revision, CatalogHash),
                TimeSpan.FromMinutes(5));
            (await service.GetLatestPriceDateAsync(symbol, CancellationToken.None))
                .Should().Be(date);

            var differentAsset = FinalPoint(date.AddDays(1), Guid.NewGuid());
            await helper.TrySetAsync(rangeKey,
                new PriceRangeCacheEntry(
                    symbol, ProviderSources.Tcmb, expected.AssetId,
                    date.AddDays(-1), date.AddDays(1), "daily",
                    [FinalPoint(date), differentAsset], Catalog.Revision, CatalogHash),
                TimeSpan.FromMinutes(5));
            var range = await service.GetPriceRangeAsync(
                symbol, date.AddDays(-1), date.AddDays(1), "daily", CancellationToken.None);

            range.Should().ContainSingle().Which.Should().BeSameAs(expected);
            repository.PriceReadCount.Should().Be(2);
            repository.NearestReadCount.Should().Be(1);
            repository.LatestReadCount.Should().Be(2);
            repository.RangeReadCount.Should().Be(1);
            identity.Source.Should().Be(ProviderSources.Tcmb);
        }
        finally
        {
            await redis.Multiplexer!.GetDatabase().KeyDeleteAsync(
                [exactKey, nearestKey, latestKey, rangeKey]);
        }
    }

    [SkippableFact]
    public async Task RealRedis_NullAuthorityEnvelopes_AreMissesAndDeleted()
    {
        Skip.IfNot(redis.Available, redis.SkipReason);
        var symbol = $"CACHE{Guid.NewGuid():N}".ToUpperInvariant();
        var date = new DateOnly(2026, 8, 18);
        var exactKey = $"authority-final-v1:catalog:{Catalog.Token}:price:{symbol}:{date:yyyy-MM-dd}";
        var helper = new RedisCacheHelper(redis.Multiplexer!, NullLogger<RedisCacheHelper>.Instance);
        var expected = FinalPoint(date);
        var service = new AssetService(
            new StaticPriceRepository(expected), helper, new AssetSymbolIndex(),
            new PassthroughAssetNameLocalizer(), TimeProvider.System,
            new PassthroughStringLocalizer(), NullLogger<AssetService>.Instance);

        try
        {
            await helper.TrySetAsync(
                exactKey,
                new PriceCacheEntry(
                    symbol, ProviderSources.Tcmb, expected.AssetId, date, null, null,
                    Catalog.Revision, CatalogHash),
                TimeSpan.FromMinutes(5));

            (await service.GetPriceAsync(symbol, date, CancellationToken.None)).Close.Should().Be(41m);
            (await redis.Multiplexer!.GetDatabase().KeyExistsAsync(exactKey)).Should().BeTrue(
                "the invalid value is replaced by a valid repository result after deletion");
            (await helper.TryGetAsync<PriceCacheEntry>(exactKey))!.Point.Should().NotBeNull();
        }
        finally
        {
            await redis.Multiplexer!.GetDatabase().KeyDeleteAsync(exactKey);
        }
    }

    [SkippableFact]
    public async Task RealRedis_NullCalculationResponseWarningsAndBasis_AreCacheMisses()
    {
        Skip.IfNot(redis.Available, redis.SkipReason);
        var helper = new RedisCacheHelper(redis.Multiplexer!, NullLogger<RedisCacheHelper>.Instance);
        var date = new DateOnly(2026, 8, 18);
        var prefix = $"authority-final-v1:catalog:{Catalog.Token}:null-calc:{Guid.NewGuid():N}";
        var keys = new[] { $"{prefix}:response", $"{prefix}:warnings", $"{prefix}:basis" };
        var priceBasis = new ObservationBasisSummaryResponse(
            AuthorityDataStatuses.Final,
            [ProviderSources.Tcmb],
            [ObservationPriceKinds.OfficialReference],
            1, 1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var noInflation = new ObservationBasisSummaryResponse(
            AuthorityDataStatuses.NotRequested, [], [], 0, null, null, null, null);

        try
        {
            await helper.TrySetAsync(
                keys[0],
                new WhatIfCacheEntry(
                    "USDTRY", date, date, 100m, "try", false, "tr", null,
                    Catalog.Revision, CatalogHash),
                TimeSpan.FromMinutes(5));
            await helper.TrySetAsync(
                keys[1],
                CalculationEntry(new CalculationDataResponse(
                    AuthorityDataStatuses.Complete, null!, priceBasis, noInflation)),
                TimeSpan.FromMinutes(5));
            await helper.TrySetAsync(
                keys[2],
                CalculationEntry(new CalculationDataResponse(
                    AuthorityDataStatuses.Complete, [], null!, noInflation)),
                TimeSpan.FromMinutes(5));

            foreach (var key in keys)
            {
                var entry = await helper.TryGetAsync<WhatIfCacheEntry>(key);
                entry.Should().NotBeNull();
                entry!.IsValid("USDTRY", date, date, 100m, "try", false, "tr", Catalog)
                    .Should().BeFalse();
                await helper.TryDeleteAsync(key);
                (await redis.Multiplexer!.GetDatabase().KeyExistsAsync(key)).Should().BeFalse();
            }
        }
        finally
        {
            await redis.Multiplexer!.GetDatabase().KeyDeleteAsync(keys.Select(key => (StackExchange.Redis.RedisKey)key).ToArray());
        }

        WhatIfCacheEntry CalculationEntry(CalculationDataResponse data) => new(
            "USDTRY", date, date, 100m, "try", false, "tr",
            new WhatIfResponse(
                "USDTRY", "Dollar/TRY", date, date,
                1m, 1m, 100m, 100m, 100m, 0m, 0m, true,
                [], null, null, null, null, null, data),
            Catalog.Revision, CatalogHash);
    }

    private static PricePoint FinalPoint(DateOnly date, Guid? assetId = null) => new()
    {
        AssetId = assetId ?? Guid.Parse("aaaaaaaa-0000-0000-0000-000000000077"),
        PriceDate = date,
        Close = 41m,
        ProviderSource = ProviderSources.Tcmb,
        SourceObservationId = $"tcmb:USD:{date:yyyy-MM-dd}:forex_buying",
        AsOfAt = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        PriceKind = ObservationPriceKinds.OfficialReference,
        IsFinal = true,
        ObservationSha256 = Enumerable.Repeat((byte)0x2a, 32).ToArray(),
        AuthorityContractVersion = 2,
        SourceRaw = "{}",
    };

    private sealed class StaticPriceRepository(PricePoint point) : IPriceRepository
    {
        internal int PriceReadCount { get; private set; }
        internal int NearestReadCount { get; private set; }
        internal int LatestReadCount { get; private set; }
        internal int RangeReadCount { get; private set; }

        public Task<PricePoint?> GetPriceAsync(string symbol, DateOnly date, CancellationToken ct)
        {
            PriceReadCount++;
            return Task.FromResult<PricePoint?>(point);
        }

        public Task<IReadOnlyList<Asset>> GetAllActiveAssetsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Asset>>(Array.Empty<Asset>());

        public Task<AssetReadIdentity?> GetActiveAssetIdentityAsync(
            string symbol, CancellationToken ct) => Task.FromResult<AssetReadIdentity?>(
                new AssetReadIdentity(point.AssetId, symbol, ProviderSources.Tcmb));

        public Task<IReadOnlyList<AssetReadIdentity>> GetAllActiveAssetIdentitiesAsync(
            CancellationToken ct) => Task.FromResult<IReadOnlyList<AssetReadIdentity>>(
                [new AssetReadIdentity(point.AssetId, "CACHE", ProviderSources.Tcmb)]);

        public Task<int> GetActiveAssetCountAsync(CancellationToken ct) => Task.FromResult(0);

        public Task<AssetCatalogVersion> GetAssetCatalogVersionAsync(CancellationToken ct) =>
            Task.FromResult(Catalog);

        public Task<IReadOnlyList<(Asset Asset, DateOnly? FirstDate, DateOnly? LastDate)>>
            GetAllActiveAssetsWithDateRangesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<(Asset, DateOnly?, DateOnly?)>>(
                Array.Empty<(Asset, DateOnly?, DateOnly?)>());

        public Task<PricePoint?> GetNearestPriceAsync(
            string symbol, DateOnly date, int maxDays, CancellationToken ct)
        {
            NearestReadCount++;
            return Task.FromResult<PricePoint?>(point);
        }

        public Task<IReadOnlyList<PricePoint?>> GetNearestPricesAsync(
            string symbol,
            IReadOnlyList<DateOnly> dates,
            int maxDays,
            CancellationToken ct)
        {
            NearestReadCount++;
            return Task.FromResult<IReadOnlyList<PricePoint?>>(
                dates.Select(_ => (PricePoint?)point).ToArray());
        }

        public Task<DateOnly?> GetLatestPriceDateAsync(string symbol, CancellationToken ct)
        {
            LatestReadCount++;
            return Task.FromResult<DateOnly?>(point.PriceDate);
        }

        public Task<IReadOnlyList<PricePoint>> GetPriceRangeAsync(
            string symbol, DateOnly from, DateOnly to, CancellationToken ct)
        {
            RangeReadCount++;
            return Task.FromResult<IReadOnlyList<PricePoint>>(new[] { point });
        }
    }

    private sealed class PassthroughAssetNameLocalizer : IAssetNameLocalizer
    {
        public string Localize(string symbol, string? fallbackDisplayName) => fallbackDisplayName ?? symbol;
    }

    private sealed class PassthroughStringLocalizer : IStringLocalizer<ErrorMessages>
    {
        public LocalizedString this[string name] => new(name, name, true);
        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(name, arguments), true);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
