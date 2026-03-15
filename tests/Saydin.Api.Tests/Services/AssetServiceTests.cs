using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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
    private readonly AssetService _sut;

    public AssetServiceTests()
    {
        _redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_db);
        // Varsayılan: cache miss
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
           .Returns(RedisValue.Null);

        _sut = new AssetService(_repository, _redis, NullLogger<AssetService>.Instance);
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
}
