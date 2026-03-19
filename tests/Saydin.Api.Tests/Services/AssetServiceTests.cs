using FluentAssertions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Saydin.Api.Models.Responses;
using Saydin.Api.Repositories;
using Saydin.Api.Services;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;
using StackExchange.Redis;

namespace Saydin.Api.Tests.Services;

public class AssetServiceTests
{
    private readonly IPriceRepository _repository = Substitute.For<IPriceRepository>();
    private readonly IConnectionMultiplexer _redis = Substitute.For<IConnectionMultiplexer>();
    private readonly IDatabase _db = Substitute.For<IDatabase>();
    private readonly IStringLocalizer<ErrorMessages> _localizer = Substitute.For<IStringLocalizer<ErrorMessages>>();
    private readonly AssetService _sut;

    public AssetServiceTests()
    {
        _redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_db);
        // Varsayılan: cache miss
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
           .Returns(RedisValue.Null);

        _localizer[Arg.Any<string>()]
            .Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));
        _localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));

        _sut = new AssetService(_repository, _redis, _localizer, NullLogger<AssetService>.Instance);
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

    // ── Cache ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPriceAsync_CacheHit_DoesNotQueryRepository()
    {
        var date = new DateOnly(2020, 1, 1);
        var cached = new PricePoint { AssetId = Guid.NewGuid(), PriceDate = date, Close = 5.95m };
        var json = System.Text.Json.JsonSerializer.Serialize(cached,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
           .Returns(new RedisValue(json));

        await _sut.GetPriceAsync("USDTRY", date, CancellationToken.None);

        await _repository.DidNotReceive().GetPriceAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPriceAsync_RedisDown_FallsBackToRepository()
    {
        var date = new DateOnly(2020, 1, 1);
        var expected = new PricePoint { AssetId = Guid.NewGuid(), PriceDate = date, Close = 5.95m };

        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
           .Returns<RedisValue>(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "test"));

        _repository.GetPriceAsync("USDTRY", date, Arg.Any<CancellationToken>())
                   .Returns(expected);

        var result = await _sut.GetPriceAsync("USDTRY", date, CancellationToken.None);

        result.Close.Should().Be(5.95m);
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
    public async Task GetAllAssetInfoAsync_NullDates_PassedThroughAsNull()
    {
        var asset = new Asset
        {
            Id = Guid.NewGuid(), Symbol = "NEWASSET", DisplayName = "Yeni Varlık",
            Category = AssetCategory.Crypto, Source = "coingecko", IsActive = true
        };

        _repository.GetActiveAssetCountAsync(Arg.Any<CancellationToken>()).Returns(1);
        _repository.GetAllActiveAssetsWithDateRangesAsync(Arg.Any<CancellationToken>())
                   .Returns(new List<(Asset Asset, DateOnly? FirstDate, DateOnly? LastDate)>
                   {
                       (asset, null, null)
                   }.AsReadOnly());

        var result = await _sut.GetAllAssetInfoAsync(CancellationToken.None);

        result[0].FirstPriceDate.Should().BeNull();
        result[0].LastPriceDate.Should().BeNull();
    }

    [Fact]
    public async Task GetAllAssetInfoAsync_CacheHit_SkipsRepository()
    {
        var cachedList = new List<AssetResponse>
        {
            new("USDTRY", "Dolar/TL", AssetCategory.Currency,
                new DateOnly(2020, 1, 1), new DateOnly(2024, 12, 31))
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            cachedList,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

        // sig cached → "\"1\""
        _db.StringGetAsync(
                Arg.Is<RedisKey>(k => k.ToString() == "assets:sig"),
                Arg.Any<CommandFlags>())
           .Returns(new RedisValue("\"1\""));

        // info cached — key artık dil kodu içeriyor: assets:info:{sig}:{lang}
        var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        _db.StringGetAsync(
                Arg.Is<RedisKey>(k => k.ToString() == $"assets:info:1:{lang}"),
                Arg.Any<CommandFlags>())
           .Returns(new RedisValue(json));

        await _sut.GetAllAssetInfoAsync(CancellationToken.None);

        await _repository.DidNotReceive()
            .GetAllActiveAssetsWithDateRangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAllAssetInfoAsync_SigCachedButInfoNotCached_QueriesRepository()
    {
        var asset = new Asset
        {
            Id = Guid.NewGuid(), Symbol = "USDTRY", DisplayName = "Dolar/TL",
            Category = AssetCategory.Currency, Source = "tcmb", IsActive = true
        };

        // sig hit, info miss
        _db.StringGetAsync(
                Arg.Is<RedisKey>(k => k.ToString() == "assets:sig"),
                Arg.Any<CommandFlags>())
           .Returns(new RedisValue("\"1\""));

        _db.StringGetAsync(
                Arg.Is<RedisKey>(k => k.ToString() == "assets:info:1"),
                Arg.Any<CommandFlags>())
           .Returns(RedisValue.Null);

        _repository.GetAllActiveAssetsWithDateRangesAsync(Arg.Any<CancellationToken>())
                   .Returns(new List<(Asset Asset, DateOnly? FirstDate, DateOnly? LastDate)>
                   {
                       (asset, new DateOnly(2020, 1, 1), new DateOnly(2024, 12, 31))
                   }.AsReadOnly());

        var result = await _sut.GetAllAssetInfoAsync(CancellationToken.None);

        result.Should().HaveCount(1);
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

    // ── GetPriceRangeAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetPriceRangeAsync_ReturnsOrderedPoints()
    {
        var from = new DateOnly(2020, 1, 1);
        var to   = new DateOnly(2020, 1, 3);
        var points = new List<PricePoint>
        {
            new() { AssetId = Guid.NewGuid(), PriceDate = from,         Close = 5.95m },
            new() { AssetId = Guid.NewGuid(), PriceDate = from.AddDays(1), Close = 6.00m },
            new() { AssetId = Guid.NewGuid(), PriceDate = to,           Close = 6.05m }
        };

        _repository.GetPriceRangeAsync("USDTRY", from, to, Arg.Any<CancellationToken>())
                   .Returns(points.AsReadOnly());

        var result = await _sut.GetPriceRangeAsync("USDTRY", from, to, "daily", CancellationToken.None);

        result.Should().HaveCount(3);
        result[0].Close.Should().Be(5.95m);
        result[2].Close.Should().Be(6.05m);
    }

    // ── GetNearestPriceAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetNearestPriceAsync_ExactDateExists_ReturnsThatDay()
    {
        var date = new DateOnly(2020, 1, 2); // Perşembe
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
        var saturday = new DateOnly(2020, 1, 4); // Cumartesi
        var friday   = new DateOnly(2020, 1, 3); // Cuma
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
        var json  = System.Text.Json.JsonSerializer.Serialize(point,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

        _db.StringGetAsync(
               Arg.Is<RedisKey>(k => k.ToString().Contains("nearest-price")),
               Arg.Any<CommandFlags>())
           .Returns(new RedisValue(json));

        await _sut.GetNearestPriceAsync("USDTRY", date, CancellationToken.None);

        await _repository.DidNotReceive()
            .GetNearestPriceAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
