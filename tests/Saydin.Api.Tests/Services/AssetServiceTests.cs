using FluentAssertions;
using System.Globalization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Saydin.Api.Models.Responses;
using Saydin.Api.Repositories;
using Saydin.Api.Services;
using Saydin.Api.Tests.Helpers;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Tests.Services;

public class AssetServiceTests
{
    private static readonly AssetCatalogVersion Catalog = new()
    {
        Revision = 7,
        CatalogSha256 = Enumerable.Repeat((byte)0x5a, 32).ToArray()
    };
    private static readonly string CatalogHash =
        Convert.ToHexString(Catalog.CatalogSha256).ToLowerInvariant();

    private readonly IPriceRepository _repository = Substitute.For<IPriceRepository>();
    private readonly IRedisCacheHelper _cache = Substitute.For<IRedisCacheHelper>();
    private readonly IAssetNameLocalizer _assetNameLocalizer = Substitute.For<IAssetNameLocalizer>();
    private readonly IStringLocalizer<ErrorMessages> _localizer = Substitute.For<IStringLocalizer<ErrorMessages>>();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly AssetService _sut;

    public AssetServiceTests()
    {
        // Varsayılan cache miss
        _cache.TryGetAsync<PriceCacheEntry>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns((PriceCacheEntry?)null);
        _cache.TryGetAsync<PriceRangeCacheEntry>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns((PriceRangeCacheEntry?)null);
        _cache.TryGetAsync<LatestPriceDateCacheEntry>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns((LatestPriceDateCacheEntry?)null);
        _cache.TryGetAsync<AssetListCacheEntry>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns((AssetListCacheEntry?)null);
        _cache.TryGetAsync<AssetInfoCacheEntry>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns((AssetInfoCacheEntry?)null);
        _cache.TryGetAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns((string?)null);

        _repository.GetActiveAssetIdentityAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new AssetReadIdentity(
                AuthorityTestData.DefaultAssetId,
                ((string)call[0]).ToUpperInvariant(),
                ProviderSources.Tcmb));
        _repository.GetAllActiveAssetIdentitiesAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<AssetReadIdentity>());
        _repository.GetAssetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(Catalog);

        _localizer[Arg.Any<string>()]
            .Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));
        _localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));

        _assetNameLocalizer.Localize(Arg.Any<string>(), Arg.Any<string?>())
                           .Returns(ci => (string?)ci[1] ?? (string)ci[0]);

        // SVCR-001/002/003 follow-up: IAssetSymbolIndex singleton testlerde gerçek
        // implementasyonla beslenir (instance scope test isolation'ı bozmaz).
        _sut = new AssetService(
            _repository, _cache, new AssetSymbolIndex(),
            _assetNameLocalizer, _timeProvider, _localizer, NullLogger<AssetService>.Instance);
    }

    // ── GetPriceAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPriceAsync_PriceExists_ReturnsPricePoint()
    {
        var date = new DateOnly(2020, 1, 1);
        var expected = AuthorityTestData.FinalPrice(
            date, 5.9518m, open: 5.9416m);

        _repository.GetPriceAsync("USDTRY", date, Arg.Any<CancellationToken>())
                   .Returns(expected);

        var result = await _sut.GetPriceAsync("USDTRY", date, CancellationToken.None);

        result.Should().NotBeNull();
        result.Close.Should().Be(5.9518m);
        result.PriceDate.Should().Be(date);
    }

    [Fact]
    public async Task GetPriceAsync_PriceNotFound_ThrowsPriceNotFoundException()
    {
        var date = new DateOnly(2020, 1, 1);
        _repository.GetPriceAsync("USDTRY", date, Arg.Any<CancellationToken>())
                   .Returns((PricePoint?)null);

        var act = () => _sut.GetPriceAsync("USDTRY", date, CancellationToken.None);

        await act.Should().ThrowAsync<PriceNotFoundException>()
            .Where(ex => ex.AssetSymbol == "USDTRY" && ex.Date == date);
    }

    [Fact]
    public async Task GetPriceAsync_SymbolNormalized_QueryUpperCase()
    {
        var date = new DateOnly(2020, 1, 1);
        _repository.GetPriceAsync("USDTRY", date, Arg.Any<CancellationToken>())
                   .Returns(AuthorityTestData.FinalPrice(date, 5.95m));

        await _sut.GetPriceAsync("usdtry", date, CancellationToken.None);

        await _repository.Received(1).GetPriceAsync("USDTRY", date, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPriceAsync_CacheHit_DoesNotQueryRepository_AndReturnsCachedValue()
    {
        var date = new DateOnly(2020, 1, 1);
        var cached = AuthorityTestData.FinalPrice(date, 5.95m);

        _cache.TryGetAsync<PriceCacheEntry>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(PriceCacheEntry.Exact(
                  Identity("USDTRY", cached.AssetId), date, cached, Catalog));

        var result = await _sut.GetPriceAsync("USDTRY", date, CancellationToken.None);

        result.Close.Should().Be(5.95m);   // cache'ten gelen değer kullanıldı
        result.PriceDate.Should().Be(date);

        await _repository.DidNotReceive()
            .GetPriceAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPriceAsync_LegacyCacheNamespaceCannotBypassFinalAuthorityRead()
    {
        var date = new DateOnly(2026, 8, 18);
        var expected = AuthorityTestData.FinalPrice(date, 41m);
        _cache.TryGetAsync<PriceCacheEntry>(
                $"price:USDTRY:{date:yyyy-MM-dd}", Arg.Any<CancellationToken>())
            .Returns(PriceCacheEntry.Exact(
                Identity("USDTRY", AuthorityTestData.DefaultAssetId),
                date,
                AuthorityTestData.FinalPrice(date, 1m)));
        _repository.GetPriceAsync("USDTRY", date, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _sut.GetPriceAsync("USDTRY", date, CancellationToken.None);

        result.Should().BeSameAs(expected);
        await _cache.Received(1).TryGetAsync<PriceCacheEntry>(
            CurrentCatalogKey($"price:USDTRY:{date:yyyy-MM-dd}"),
            Arg.Any<CancellationToken>());
        await _cache.DidNotReceive().TryGetAsync<PriceCacheEntry>(
            $"price:USDTRY:{date:yyyy-MM-dd}", Arg.Any<CancellationToken>());
        await _repository.Received(1).GetPriceAsync(
            "USDTRY", date, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPriceAsync_CurrentKeyWrongExactDate_IsCacheMiss()
    {
        var date = new DateOnly(2026, 8, 18);
        var wrongDate = AuthorityTestData.FinalPrice(date.AddDays(-1), 999m);
        var forged = new PriceCacheEntry(
            "USDTRY", ProviderSources.Tcmb, wrongDate.AssetId,
            date, null, wrongDate, Catalog.Revision, CatalogHash);
        var expected = AuthorityTestData.FinalPrice(date, 41m);
        _cache.TryGetAsync<PriceCacheEntry>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(forged);
        _repository.GetPriceAsync("USDTRY", date, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _sut.GetPriceAsync("USDTRY", date, CancellationToken.None);

        result.Should().BeSameAs(expected);
        await _repository.Received(1).GetPriceAsync(
            "USDTRY", date, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPriceAsync_CurrentKeyColludingWrongProviderEnvelope_IsCacheMiss()
    {
        var date = new DateOnly(2026, 8, 18);
        var forgedPoint = AuthorityTestData.FinalPrice(date, 999m);
        forgedPoint.ProviderSource = ProviderSources.CoinGecko;
        forgedPoint.PriceKind = ObservationPriceKinds.DailyUtcReference;
        var forged = new PriceCacheEntry(
            "USDTRY", ProviderSources.CoinGecko, forgedPoint.AssetId,
            date, null, forgedPoint, Catalog.Revision, CatalogHash);
        var expected = AuthorityTestData.FinalPrice(date, 41m);
        _cache.TryGetAsync<PriceCacheEntry>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(forged);
        _repository.GetPriceAsync("USDTRY", date, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _sut.GetPriceAsync("USDTRY", date, CancellationToken.None);

        result.Should().BeSameAs(expected);
        await _repository.Received(1).GetPriceAsync(
            "USDTRY", date, Arg.Any<CancellationToken>());
    }

    // ── GetAllAssetInfoAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetAllAssetInfoAsync_ReturnsMappedAssetResponses()
    {
        var asset = new Asset
        {
            Id = Guid.NewGuid(), Symbol = "USDTRY", DisplayName = "Dolar/TL",
            Category = AssetCategory.Currency, Source = "tcmb", IsActive = true
        };
        var firstDate = new DateOnly(2020, 1, 1);
        var lastDate  = new DateOnly(2024, 12, 31);

        _repository.GetActiveAssetCountAsync(Arg.Any<CancellationToken>()).Returns(1);
        _repository.GetAllActiveAssetIdentitiesAsync(Arg.Any<CancellationToken>())
            .Returns([new AssetReadIdentity(asset.Id, asset.Symbol, asset.Source)]);
        _repository.GetAllActiveAssetsWithDateRangesAsync(Arg.Any<CancellationToken>())
                   .Returns(new List<(Asset Asset, DateOnly? FirstDate, DateOnly? LastDate)>
                   {
                       (asset, firstDate, lastDate)
                   }.AsReadOnly());

        var result = await _sut.GetAllAssetInfoAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Symbol.Should().Be("USDTRY");
        result[0].FirstPriceDate.Should().Be(firstDate);
        result[0].LastPriceDate.Should().Be(lastDate);
    }

    [Fact]
    public async Task GetAllAssetInfoAsync_CacheHit_SkipsRepository_AndReturnsCachedList()
    {
        // F2.3-7: AssetResponse.Category artık string (snake_case server-side projeksiyon).
        var cachedList = new List<AssetResponse>
        {
            new("USDTRY", "Dolar/TL", "currency",
                new DateOnly(2020, 1, 1), new DateOnly(2024, 12, 31))
        };

        _cache.TryGetAsync<string>("assets:sig", Arg.Any<CancellationToken>()).Returns("1");
        var identities = new[]
        {
            new AssetReadIdentity(AuthorityTestData.DefaultAssetId, "USDTRY", ProviderSources.Tcmb),
        };
        _repository.GetAllActiveAssetIdentitiesAsync(Arg.Any<CancellationToken>())
            .Returns(identities);
        _cache.TryGetAsync<AssetInfoCacheEntry>(Arg.Is<string>(k =>
                k.StartsWith(CurrentCatalogKey("assets:info:"), StringComparison.Ordinal)),
                Arg.Any<CancellationToken>())
              .Returns(new AssetInfoCacheEntry(
                  "1", CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
                  identities, cachedList, Catalog.Revision, CatalogHash));

        var result = await _sut.GetAllAssetInfoAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Symbol.Should().Be("USDTRY");
        // F14 follow-up: Category string kontrat regresyonu için assertion eklendi
        // (enum → string projeksiyonu F2.3-7'de yapılmıştı; cache-hit path'i koruma).
        result[0].Category.Should().Be("currency");
        await _repository.DidNotReceive()
            .GetAllActiveAssetsWithDateRangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAllAssetInfoAsync_WrongSignatureEnvelope_IsCacheMiss()
    {
        _cache.TryGetAsync<string>("assets:sig", Arg.Any<CancellationToken>()).Returns("1");
        var identities = new[]
        {
            new AssetReadIdentity(AuthorityTestData.DefaultAssetId, "USDTRY", ProviderSources.Tcmb),
        };
        _repository.GetAllActiveAssetIdentitiesAsync(Arg.Any<CancellationToken>())
            .Returns(identities);
        _cache.TryGetAsync<AssetInfoCacheEntry>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AssetInfoCacheEntry(
                "2", CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
                identities,
                [new AssetResponse("FORGED", "Forged", "currency", null, null)],
                Catalog.Revision,
                CatalogHash));
        _repository.GetAllActiveAssetsWithDateRangesAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<(Asset, DateOnly?, DateOnly?)>());

        var result = await _sut.GetAllAssetInfoAsync(CancellationToken.None);

        result.Should().BeEmpty();
        await _repository.Received(1)
            .GetAllActiveAssetsWithDateRangesAsync(Arg.Any<CancellationToken>());
    }

    // ── GetAllAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAllAsync_ReturnsRepositoryAssets()
    {
        var assets = new List<Asset>
        {
            new() { Id = Guid.NewGuid(), Symbol = "USDTRY", DisplayName = "Dolar/TL",
                    Category = AssetCategory.Currency, Source = "tcmb", IsActive = true },
            new() { Id = Guid.NewGuid(), Symbol = "BTC", DisplayName = "Bitcoin",
                    Category = AssetCategory.Crypto, Source = "coingecko", IsActive = true }
        };

        _repository.GetAllActiveAssetsAsync(Arg.Any<CancellationToken>())
                   .Returns(assets.AsReadOnly());

        var result = await _sut.GetAllAsync(CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().Contain(a => a.Symbol == "USDTRY");
    }

    [Fact]
    public async Task GetBySymbolAsync_FindsAssetCaseInsensitively()
    {
        var assets = new List<Asset>
        {
            new() { Id = Guid.NewGuid(), Symbol = "BTC", DisplayName = "Bitcoin",
                    Category = AssetCategory.Crypto, Source = "coingecko", IsActive = true }
        };

        _repository.GetAllActiveAssetsAsync(Arg.Any<CancellationToken>())
                   .Returns(assets.AsReadOnly());

        var result = await _sut.GetBySymbolAsync("btc", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Symbol.Should().Be("BTC");
    }

    [Fact]
    public async Task GetBySymbolAsync_UnknownSymbol_ReturnsNull()
    {
        _repository.GetAllActiveAssetsAsync(Arg.Any<CancellationToken>())
                   .Returns(new List<Asset>().AsReadOnly());

        var result = await _sut.GetBySymbolAsync("UNKNOWN", CancellationToken.None);

        result.Should().BeNull();
    }

    // ── GetLatestPriceDateAsync ───────────────────────────────────────────

    [Fact]
    public async Task GetLatestPriceDateAsync_ReturnsDate()
    {
        var expected = new DateOnly(2024, 12, 31);
        _repository.GetLatestPriceDateAsync("USDTRY", Arg.Any<CancellationToken>())
                   .Returns(expected);

        var result = await _sut.GetLatestPriceDateAsync("USDTRY", CancellationToken.None);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task GetLatestPriceDateAsync_NullFromRepository_ThrowsPriceNotFoundException()
    {
        _repository.GetLatestPriceDateAsync("USDTRY", Arg.Any<CancellationToken>())
                   .Returns((DateOnly?)null);

        var act = () => _sut.GetLatestPriceDateAsync("USDTRY", CancellationToken.None);

        await act.Should().ThrowAsync<PriceNotFoundException>();
    }

    [Fact]
    public async Task GetLatestPriceDateAsync_CacheHit_SkipsRepository_AndReturnsCachedDate()
    {
        _cache.TryGetAsync<LatestPriceDateCacheEntry>(
                Arg.Is<string>(k => k.StartsWith(CurrentCatalogKey("latest-date:"))),
                Arg.Any<CancellationToken>())
              .Returns(new LatestPriceDateCacheEntry(
                  "USDTRY", ProviderSources.Tcmb, AuthorityTestData.DefaultAssetId,
                  new DateOnly(2024, 12, 31), Catalog.Revision, CatalogHash));

        var result = await _sut.GetLatestPriceDateAsync("USDTRY", CancellationToken.None);

        result.Should().Be(new DateOnly(2024, 12, 31));
        await _repository.DidNotReceive()
            .GetLatestPriceDateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLatestPriceDateAsync_CrossSourceAndWrongAssetEnvelopes_AreCacheMisses()
    {
        var expected = new DateOnly(2026, 8, 18);
        _cache.TryGetAsync<LatestPriceDateCacheEntry>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                new LatestPriceDateCacheEntry(
                    "USDTRY", ProviderSources.CoinGecko,
                    AuthorityTestData.DefaultAssetId, expected.AddDays(-1),
                    Catalog.Revision, CatalogHash),
                new LatestPriceDateCacheEntry(
                    "USDTRY", ProviderSources.Tcmb,
                    Guid.NewGuid(), expected.AddDays(-1),
                    Catalog.Revision, CatalogHash));
        _repository.GetLatestPriceDateAsync("USDTRY", Arg.Any<CancellationToken>())
            .Returns(expected);

        var crossSourceResult = await _sut.GetLatestPriceDateAsync(
            "USDTRY", CancellationToken.None);
        var wrongAssetResult = await _sut.GetLatestPriceDateAsync(
            "USDTRY", CancellationToken.None);

        crossSourceResult.Should().Be(expected);
        wrongAssetResult.Should().Be(expected);
        await _repository.Received(2).GetLatestPriceDateAsync(
            "USDTRY", Arg.Any<CancellationToken>());
    }

    // ── GetPriceRangeAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetPriceRangeAsync_ReturnsOrderedPoints()
    {
        var from = new DateOnly(2020, 1, 1);
        var to   = new DateOnly(2020, 1, 3);
        var points = new List<PricePoint>
        {
            AuthorityTestData.FinalPrice(from, 5.95m),
            AuthorityTestData.FinalPrice(from.AddDays(1), 6.00m),
            AuthorityTestData.FinalPrice(to, 6.05m),
        };

        _repository.GetPriceRangeAsync("USDTRY", from, to, Arg.Any<CancellationToken>())
                   .Returns(points.AsReadOnly());

        var result = await _sut.GetPriceRangeAsync("USDTRY", from, to, "daily", CancellationToken.None);

        result.Should().HaveCount(3);
        result[0].Close.Should().Be(5.95m);
        result[2].Close.Should().Be(6.05m);
    }

    [Fact]
    public async Task GetPriceRangeAsync_ValidBoundEnvelope_IsCacheHit()
    {
        var from = new DateOnly(2026, 8, 17);
        var to = from.AddDays(2);
        var assetId = Guid.NewGuid();
        var points = new List<PricePoint>
        {
            AuthorityTestData.FinalPrice(from, 40m, assetId),
            AuthorityTestData.FinalPrice(to, 42m, assetId),
        };
        _cache.TryGetAsync<PriceRangeCacheEntry>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PriceRangeCacheEntry.Create(
                Identity("USDTRY", assetId), from, to, "daily", points, Catalog));
        _repository.GetActiveAssetIdentityAsync("USDTRY", Arg.Any<CancellationToken>())
            .Returns(Identity("USDTRY", assetId));

        var result = await _sut.GetPriceRangeAsync(
            "USDTRY", from, to, "daily", CancellationToken.None);

        result.Should().Equal(points);
        await _repository.DidNotReceive().GetPriceRangeAsync(
            Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPriceRangeAsync_OutOfRangeOrMixedAssetEnvelope_IsCacheMiss()
    {
        var from = new DateOnly(2026, 8, 17);
        var to = from.AddDays(2);
        var firstAsset = Guid.NewGuid();
        _repository.GetActiveAssetIdentityAsync("USDTRY", Arg.Any<CancellationToken>())
            .Returns(Identity("USDTRY", firstAsset));
        var cached = new PriceRangeCacheEntry(
            "USDTRY", ProviderSources.Tcmb, firstAsset, from, to, "daily",
            [
                AuthorityTestData.FinalPrice(from, 40m, firstAsset),
                AuthorityTestData.FinalPrice(to.AddDays(1), 999m, Guid.NewGuid()),
            ],
            Catalog.Revision,
            CatalogHash);
        var expected = new[] { AuthorityTestData.FinalPrice(from, 41m, firstAsset) };
        _cache.TryGetAsync<PriceRangeCacheEntry>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(cached);
        _repository.GetPriceRangeAsync("USDTRY", from, to, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _sut.GetPriceRangeAsync(
            "USDTRY", from, to, "daily", CancellationToken.None);

        result.Should().Equal(expected);
        await _repository.Received(1).GetPriceRangeAsync(
            "USDTRY", from, to, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPriceRangeAsync_NonDailyInterval_ThrowsValidationException()
    {
        var from = new DateOnly(2020, 1, 1);
        var to   = new DateOnly(2020, 1, 3);

        var act = () => _sut.GetPriceRangeAsync("USDTRY", from, to, "weekly", CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    // ── GetNearestPriceAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetNearestPriceAsync_ExactDateExists_ReturnsThatDay()
    {
        var date = new DateOnly(2020, 1, 2);
        var expected = AuthorityTestData.FinalPrice(date, 5.95m);

        _repository.GetNearestPriceAsync("USDTRY", date, 7, Arg.Any<CancellationToken>())
                   .Returns(expected);

        var result = await _sut.GetNearestPriceAsync("USDTRY", date, CancellationToken.None);

        result.PriceDate.Should().Be(date);
        result.Close.Should().Be(5.95m);
    }

    [Fact]
    public async Task GetNearestPriceAsync_WeekendDate_ReturnsPreviousFriday()
    {
        var saturday = new DateOnly(2020, 1, 4);
        var friday   = new DateOnly(2020, 1, 3);
        var pricePoint = AuthorityTestData.FinalPrice(friday, 5.93m);

        _repository.GetNearestPriceAsync("USDTRY", saturday, 7, Arg.Any<CancellationToken>())
                   .Returns(pricePoint);

        var result = await _sut.GetNearestPriceAsync("USDTRY", saturday, CancellationToken.None);

        result.PriceDate.Should().Be(friday);
    }

    [Fact]
    public async Task GetNearestPriceAsync_NoPriceInWindow_ThrowsPriceNotFoundException()
    {
        var date = new DateOnly(2020, 1, 1);
        _repository.GetNearestPriceAsync("USDTRY", date, 7, Arg.Any<CancellationToken>())
                   .Returns((PricePoint?)null);

        var act = () => _sut.GetNearestPriceAsync("USDTRY", date, CancellationToken.None);

        await act.Should().ThrowAsync<PriceNotFoundException>();
    }

    [Fact]
    public async Task GetNearestPriceAsync_CacheHit_DoesNotCallRepository()
    {
        var date  = new DateOnly(2020, 1, 3);
        var point = AuthorityTestData.FinalPrice(date, 5.95m);

        _cache.TryGetAsync<PriceCacheEntry>(
                Arg.Is<string>(k => k.StartsWith(CurrentCatalogKey("nearest-price:"))),
                Arg.Any<CancellationToken>())
              .Returns(PriceCacheEntry.Nearest(
                  Identity("USDTRY", point.AssetId), date, 7, point, Catalog));

        var result = await _sut.GetNearestPriceAsync("USDTRY", date, CancellationToken.None);

        result.Close.Should().Be(5.95m);
        await _repository.DidNotReceive()
            .GetNearestPriceAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetNearestPriceAsync_OutOfBoundEnvelope_IsCacheMiss()
    {
        var date = new DateOnly(2026, 8, 18);
        var forgedPoint = AuthorityTestData.FinalPrice(date.AddDays(-8), 999m);
        _cache.TryGetAsync<PriceCacheEntry>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PriceCacheEntry(
                "USDTRY", ProviderSources.Tcmb, forgedPoint.AssetId,
                date, 7, forgedPoint, Catalog.Revision, CatalogHash));
        var expected = AuthorityTestData.FinalPrice(date.AddDays(-1), 41m);
        _repository.GetNearestPriceAsync("USDTRY", date, 7, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _sut.GetNearestPriceAsync("USDTRY", date, CancellationToken.None);

        result.Should().BeSameAs(expected);
        await _repository.Received(1).GetNearestPriceAsync(
            "USDTRY", date, 7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DcaSixHundredPurchasesAndTerminal_UseOneIdentityAndOneBulkRead()
    {
        var firstDate = new DateOnly(2020, 1, 1);
        var dates = Enumerable.Range(0, 600)
            .Select(firstDate.AddDays)
            .Append(firstDate.AddDays(599))
            .ToArray();
        _repository.GetNearestPricesAsync(
                "USDTRY", Arg.Any<IReadOnlyList<DateOnly>>(), 7, Arg.Any<CancellationToken>())
            .Returns(call => ((IReadOnlyList<DateOnly>)call[1])
                .Select(date => (PricePoint?)AuthorityTestData.FinalPrice(
                    date, 10m, AuthorityTestData.DefaultAssetId))
                .ToArray());

        var result = await _sut.GetNearestPricesAsync(
            "USDTRY", dates, CancellationToken.None);

        result.Should().HaveCount(601);
        result[^1].Should().NotBeNull();
        result[^1]!.PriceDate.Should().Be(dates[^1]);
        await _repository.Received(1).GetActiveAssetIdentityAsync(
            "USDTRY", Arg.Any<CancellationToken>());
        await _repository.Received(1).GetNearestPricesAsync(
            "USDTRY", Arg.Is<IReadOnlyList<DateOnly>>(value => value.Count == 601),
            7, Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().GetNearestPriceAsync(
            Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConcurrentColdLookups_CoalesceTrustedIdentityRead()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _repository.GetActiveAssetIdentityAsync(
                "USDTRY", Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                started.TrySetResult();
                await release.Task;
                return (AssetReadIdentity?)Identity(
                    "USDTRY", AuthorityTestData.DefaultAssetId);
            });
        var date = new DateOnly(2026, 8, 18);
        _repository.GetPriceAsync("USDTRY", date, Arg.Any<CancellationToken>())
            .Returns(AuthorityTestData.FinalPrice(date, 41m));
        _repository.GetLatestPriceDateAsync("USDTRY", Arg.Any<CancellationToken>())
            .Returns(date);

        var exactTask = _sut.GetPriceAsync("USDTRY", date, CancellationToken.None);
        await started.Task;
        var latestTask = _sut.GetLatestPriceDateAsync("USDTRY", CancellationToken.None);
        release.TrySetResult();
        await Task.WhenAll(exactTask, latestTask);

        await _repository.Received(1).GetActiveAssetIdentityAsync(
            "USDTRY", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelledIdentityLoad_DoesNotPoisonScopedMemo()
    {
        var attempts = 0;
        _repository.GetActiveAssetIdentityAsync(
                "USDTRY", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new OperationCanceledException("cancelled_loader");
                return Identity("USDTRY", AuthorityTestData.DefaultAssetId);
            });
        var date = new DateOnly(2026, 8, 18);
        _repository.GetLatestPriceDateAsync("USDTRY", Arg.Any<CancellationToken>())
            .Returns(date);

        var first = () => _sut.GetLatestPriceDateAsync(
            "USDTRY", CancellationToken.None);
        await first.Should().ThrowAsync<OperationCanceledException>();
        var recovered = await _sut.GetLatestPriceDateAsync(
            "USDTRY", CancellationToken.None);

        recovered.Should().Be(date);
        await _repository.Received(2).GetActiveAssetIdentityAsync(
            "USDTRY", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrustedIdentityMemo_DoesNotCrossScopedServiceInstances()
    {
        var date = new DateOnly(2026, 8, 18);
        _repository.GetLatestPriceDateAsync("USDTRY", Arg.Any<CancellationToken>())
            .Returns(date);
        var secondScope = new AssetService(
            _repository, _cache, new AssetSymbolIndex(),
            _assetNameLocalizer, _timeProvider, _localizer,
            NullLogger<AssetService>.Instance);

        await _sut.GetLatestPriceDateAsync("USDTRY", CancellationToken.None);
        await secondScope.GetLatestPriceDateAsync("USDTRY", CancellationToken.None);

        await _repository.Received(2).GetActiveAssetIdentityAsync(
            "USDTRY", Arg.Any<CancellationToken>());
    }

    private static AssetReadIdentity Identity(string symbol, Guid assetId) =>
        new(assetId, symbol, ProviderSources.Tcmb);

    private static string CurrentCatalogKey(string suffix) =>
        $"authority-final-v1:catalog:{Catalog.Token}:{suffix}";
}
