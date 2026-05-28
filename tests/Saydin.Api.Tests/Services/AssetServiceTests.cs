using FluentAssertions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Saydin.Api.Models.Responses;
using Saydin.Api.Repositories;
using Saydin.Api.Services;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Tests.Services;

public class AssetServiceTests
{
    private readonly IPriceRepository _repository = Substitute.For<IPriceRepository>();
    private readonly IRedisCacheHelper _cache = Substitute.For<IRedisCacheHelper>();
    private readonly IAssetNameLocalizer _assetNameLocalizer = Substitute.For<IAssetNameLocalizer>();
    private readonly IStringLocalizer<ErrorMessages> _localizer = Substitute.For<IStringLocalizer<ErrorMessages>>();
    private readonly AssetService _sut;

    public AssetServiceTests()
    {
        // Varsayılan cache miss
        _cache.TryGetAsync<PricePoint>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns((PricePoint?)null);
        _cache.TryGetAsync<List<PricePoint>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns((List<PricePoint>?)null);
        _cache.TryGetAsync<List<Asset>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns((List<Asset>?)null);
        _cache.TryGetAsync<List<AssetResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns((List<AssetResponse>?)null);
        _cache.TryGetAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns((string?)null);

        _localizer[Arg.Any<string>()]
            .Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));
        _localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));

        _assetNameLocalizer.Localize(Arg.Any<string>(), Arg.Any<string?>())
                           .Returns(ci => (string?)ci[1] ?? (string)ci[0]);

        _sut = new AssetService(
            _repository, _cache, _assetNameLocalizer, _localizer, NullLogger<AssetService>.Instance);
    }

    // ── GetPriceAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPriceAsync_PriceExists_ReturnsPricePoint()
    {
        var date = new DateOnly(2020, 1, 1);
        var expected = new PricePoint
        {
            AssetId   = Guid.NewGuid(),
            PriceDate = date,
            Close     = 5.9518m,
            Open      = 5.9416m
        };

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
                   .Returns(new PricePoint { AssetId = Guid.NewGuid(), PriceDate = date, Close = 5.95m });

        await _sut.GetPriceAsync("usdtry", date, CancellationToken.None);

        await _repository.Received(1).GetPriceAsync("USDTRY", date, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPriceAsync_CacheHit_DoesNotQueryRepository_AndReturnsCachedValue()
    {
        var date = new DateOnly(2020, 1, 1);
        var cached = new PricePoint { AssetId = Guid.NewGuid(), PriceDate = date, Close = 5.95m };

        _cache.TryGetAsync<PricePoint>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(cached);

        var result = await _sut.GetPriceAsync("USDTRY", date, CancellationToken.None);

        result.Close.Should().Be(5.95m);   // cache'ten gelen değer kullanıldı
        result.PriceDate.Should().Be(date);

        await _repository.DidNotReceive()
            .GetPriceAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
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
        _cache.TryGetAsync<List<AssetResponse>>(Arg.Is<string>(k => k.StartsWith("assets:info:")), Arg.Any<CancellationToken>())
              .Returns(cachedList);

        var result = await _sut.GetAllAssetInfoAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Symbol.Should().Be("USDTRY");
        await _repository.DidNotReceive()
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
        _cache.TryGetAsync<string>(Arg.Is<string>(k => k.StartsWith("latest-date:")), Arg.Any<CancellationToken>())
              .Returns("2024-12-31");

        var result = await _sut.GetLatestPriceDateAsync("USDTRY", CancellationToken.None);

        result.Should().Be(new DateOnly(2024, 12, 31));
        await _repository.DidNotReceive()
            .GetLatestPriceDateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── GetPriceRangeAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetPriceRangeAsync_ReturnsOrderedPoints()
    {
        var from = new DateOnly(2020, 1, 1);
        var to   = new DateOnly(2020, 1, 3);
        var points = new List<PricePoint>
        {
            new() { AssetId = Guid.NewGuid(), PriceDate = from,             Close = 5.95m },
            new() { AssetId = Guid.NewGuid(), PriceDate = from.AddDays(1),  Close = 6.00m },
            new() { AssetId = Guid.NewGuid(), PriceDate = to,               Close = 6.05m }
        };

        _repository.GetPriceRangeAsync("USDTRY", from, to, Arg.Any<CancellationToken>())
                   .Returns(points.AsReadOnly());

        var result = await _sut.GetPriceRangeAsync("USDTRY", from, to, "daily", CancellationToken.None);

        result.Should().HaveCount(3);
        result[0].Close.Should().Be(5.95m);
        result[2].Close.Should().Be(6.05m);
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
        var expected = new PricePoint { AssetId = Guid.NewGuid(), PriceDate = date, Close = 5.95m };

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
        var pricePoint = new PricePoint { AssetId = Guid.NewGuid(), PriceDate = friday, Close = 5.93m };

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
        var point = new PricePoint { AssetId = Guid.NewGuid(), PriceDate = date, Close = 5.95m };

        _cache.TryGetAsync<PricePoint>(Arg.Is<string>(k => k.StartsWith("nearest-price:")), Arg.Any<CancellationToken>())
              .Returns(point);

        var result = await _sut.GetNearestPriceAsync("USDTRY", date, CancellationToken.None);

        result.Close.Should().Be(5.95m);
        await _repository.DidNotReceive()
            .GetNearestPriceAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
