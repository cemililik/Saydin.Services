using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;
using Saydin.Api.Repositories;
using Saydin.Api.Services;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;
using StackExchange.Redis;

namespace Saydin.Api.Tests.Services;

public class WhatIfCalculatorTests
{
    private readonly IAssetService            _assetService       = Substitute.For<IAssetService>();
    private readonly ISavedScenarioRepository _scenarioRepository = Substitute.For<ISavedScenarioRepository>();
    private readonly IConnectionMultiplexer   _redis              = Substitute.For<IConnectionMultiplexer>();
    private readonly IDatabase                _db                 = Substitute.For<IDatabase>();
    private readonly WhatIfCalculator         _sut;

    private const string DeviceId  = "test-device-001";
    private const string FreeDeviceId  = "free-device";
    private const string PremiumDeviceId = "premium-device";

    private static readonly Guid   AssetId  = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Asset  UsdTry   = new()
    {
        Id          = AssetId,
        Symbol      = "USDTRY",
        DisplayName = "Dolar/TL",
        Category    = AssetCategory.Currency,
        Source      = "tcmb",
        IsActive    = true
    };

    private static readonly User FreeUser = new()
    {
        Id       = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"),
        DeviceId = FreeDeviceId,
        Tier     = "free",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static readonly User PremiumUser = new()
    {
        Id       = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"),
        DeviceId = PremiumDeviceId,
        Tier     = "premium",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static readonly DateOnly BuyDate  = new(2020, 1, 1);
    private static readonly DateOnly SellDate = new(2021, 1, 1);

    public WhatIfCalculatorTests()
    {
        _redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_db);

        // Varsayılan: cache miss, Lua script 1 döner (ilk kullanım)
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
           .Returns(RedisValue.Null);
        _db.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
           .Returns(RedisResult.Create(1L));

        // Varsayılan: free kullanıcı
        _scenarioRepository.GetUserByDeviceIdAsync(FreeDeviceId, Arg.Any<CancellationToken>())
                           .Returns(FreeUser);
        _scenarioRepository.GetUserByDeviceIdAsync(PremiumDeviceId, Arg.Any<CancellationToken>())
                           .Returns(PremiumUser);
        _scenarioRepository.GetUserByDeviceIdAsync(DeviceId, Arg.Any<CancellationToken>())
                           .Returns((User?)null);

        // Varsayılan: asset listesi
        _assetService.GetAllAsync(Arg.Any<CancellationToken>())
                     .Returns(new List<Asset> { UsdTry }.AsReadOnly());

        _sut = new WhatIfCalculator(
            _assetService,
            _scenarioRepository,
            _redis,
            NullLogger<WhatIfCalculator>.Instance);
    }

    // ── Hesaplama (AmountType: try) ──────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_AmountTypeTry_ComputesCorrectResult()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 10_000m, "try");

        var result = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.AssetSymbol.Should().Be("USDTRY");
        result.BuyPrice.Should().Be(5.95m);
        result.SellPrice.Should().Be(8.50m);
        result.InitialValueTry.Should().Be(10_000m);

        // unitsAcquired = round(10000 / 5.95, 6) = 1680.672269
        result.UnitsAcquired.Should().Be(Math.Round(10_000m / 5.95m, 6, MidpointRounding.AwayFromZero));

        // finalValue = round(unitsAcquired * 8.50, 2)
        var expectedFinal = Math.Round(result.UnitsAcquired * 8.50m, 2, MidpointRounding.AwayFromZero);
        result.FinalValueTry.Should().Be(expectedFinal);
        result.ProfitLossTry.Should().Be(expectedFinal - 10_000m);
        result.IsProfit.Should().BeTrue();
    }

    [Fact]
    public async Task CalculateAsync_AmountTypeUnits_ComputesCorrectResult()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 100m, "units");

        var result = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.UnitsAcquired.Should().Be(100m);
        result.InitialValueTry.Should().Be(Math.Round(100m * 5.95m, 2, MidpointRounding.AwayFromZero));
        result.FinalValueTry.Should().Be(Math.Round(100m * 8.50m, 2, MidpointRounding.AwayFromZero));
        result.IsProfit.Should().BeTrue();
    }

    [Fact]
    public async Task CalculateAsync_AmountTypeGrams_ComputesCorrectResult()
    {
        SetupPrices(buyPrice: 1000m, sellPrice: 1500m);

        var request = MakeRequest("XAUTRY", BuyDate, SellDate, 50m, "grams");

        _assetService.GetAllAsync(Arg.Any<CancellationToken>())
                     .Returns(new List<Asset>
                     {
                         new() { Id = Guid.NewGuid(), Symbol = "XAUTRY", DisplayName = "Altın/TL",
                                 Category = AssetCategory.PreciousMetal, Source = "goldapi", IsActive = true }
                     }.AsReadOnly());

        var result = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.UnitsAcquired.Should().Be(50m);
        result.InitialValueTry.Should().Be(Math.Round(50m * 1000m, 2, MidpointRounding.AwayFromZero));
        result.FinalValueTry.Should().Be(Math.Round(50m * 1500m, 2, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public async Task CalculateAsync_LossScenario_IsProfitFalse()
    {
        SetupPrices(buyPrice: 10m, sellPrice: 5m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.IsProfit.Should().BeFalse();
        result.ProfitLossTry.Should().BeNegative();
        result.ProfitLossPercent.Should().Be(-50m);
    }

    [Fact]
    public async Task CalculateAsync_BreakevenScenario_IsProfitTrue()
    {
        SetupPrices(buyPrice: 10m, sellPrice: 10m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.IsProfit.Should().BeTrue();   // profitLoss == 0 → IsProfit
        result.ProfitLossTry.Should().Be(0m);
        result.ProfitLossPercent.Should().Be(0m);
    }

    [Fact]
    public async Task CalculateAsync_NoSellDate_UsesTodayAsDefault()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        SetupPrices(buyPrice: 5.95m, sellPrice: 30m, sellDate: today);

        var request = new WhatIfRequest("USDTRY", BuyDate, SellDate: null, 10_000m, "try");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.SellDate.Should().Be(today);
    }

    // ── Validasyon ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CalculateAsync_EmptyDeviceId_ThrowsArgumentException(string deviceId)
    {
        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");

        var act = () => _sut.CalculateAsync(deviceId, request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CalculateAsync_BuyDateAfterSellDate_ThrowsArgumentException()
    {
        var request = MakeRequest("USDTRY", SellDate, BuyDate, 1000m, "try");  // tersine çevrildi

        var act = () => _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
                 .WithMessage("*Alış tarihi satış tarihinden sonra olamaz*");
    }

    [Fact]
    public async Task CalculateAsync_InvalidAmountType_ThrowsArgumentException()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "eur");

        var act = () => _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
                 .WithMessage("*Geçersiz amountType*");
    }

    [Fact]
    public async Task CalculateAsync_UnknownAsset_ThrowsPriceNotFoundException()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);
        _assetService.GetAllAsync(Arg.Any<CancellationToken>())
                     .Returns(new List<Asset>().AsReadOnly()); // boş liste

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");

        var act = () => _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        await act.Should().ThrowAsync<PriceNotFoundException>();
    }

    // ── Günlük Limit ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_PremiumUser_SkipsDailyLimitCheck()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        await _sut.CalculateAsync(PremiumDeviceId, request, CancellationToken.None);

        // Premium kullanıcı için Redis Lua script çağrılmamalı
        await _db.DidNotReceive()
                 .ScriptEvaluateAsync(
                     Arg.Any<string>(),
                     Arg.Any<RedisKey[]>(),
                     Arg.Any<RedisValue[]>(),
                     Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task CalculateAsync_FreeUserUnderLimit_Succeeds()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        _db.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
           .Returns(RedisResult.Create(5L)); // limit = 10, count = 5 → izin ver

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        var act     = () => _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        await act.Should().NotThrowAsync<DailyLimitExceededException>();
    }

    [Fact]
    public async Task CalculateAsync_FreeUserAtLimit_ThrowsDailyLimitExceededException()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        _db.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
           .Returns(RedisResult.Create(11L)); // 11 > 10 → limit aşıldı

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");

        var act = () => _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        await act.Should().ThrowAsync<DailyLimitExceededException>()
                 .Where(ex => ex.Limit == 10);
    }

    [Fact]
    public async Task CalculateAsync_UnknownDevice_UsesDeviceIdAsKey()
    {
        // Bilinmeyen cihaz: repository null döner, deviceId itself key olarak kullanılır
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        await _sut.CalculateAsync(DeviceId, request, CancellationToken.None);

        // Redis key'in deviceId'yi içermesi beklenir
        await _db.Received().ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Is<RedisKey[]>(keys => keys[0].ToString().Contains(DeviceId)),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task CalculateAsync_RedisDownForLimitCheck_StillCalculates()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        _db.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
           .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "test"));

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");

        // Redis hata → hesaplama yine de çalışmalı
        var act = () => _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── Cache ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_CacheHit_DoesNotCallAssetService()
    {
        var cached = new WhatIfResponse(
            AssetSymbol: "USDTRY", AssetDisplayName: "Dolar/TL",
            BuyDate: BuyDate, SellDate: SellDate,
            BuyPrice: 5.95m, SellPrice: 8.50m,
            UnitsAcquired: 1m, InitialValueTry: 5.95m, FinalValueTry: 8.50m,
            ProfitLossTry: 2.55m, ProfitLossPercent: 42.86m, IsProfit: true);

        var json = System.Text.Json.JsonSerializer.Serialize(
            cached,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
           .Returns(new RedisValue(json));

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1m, "units");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.BuyPrice.Should().Be(5.95m);
        await _assetService.DidNotReceive().GetPriceAsync(
            Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    // ── Symbol Normalizasyon ─────────────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_LowercaseSymbol_NormalizesToUpperCase()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var request = MakeRequest("usdtry", BuyDate, SellDate, 1000m, "try");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.AssetSymbol.Should().Be("USDTRY");
        await _assetService.Received().GetPriceAsync("USDTRY", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    // ── Yardımcı Metodlar ────────────────────────────────────────────────────

    private void SetupPrices(
        decimal buyPrice, decimal sellPrice,
        DateOnly? buyDate = null, DateOnly? sellDate = null)
    {
        var effectiveBuy  = buyDate  ?? BuyDate;
        var effectiveSell = sellDate ?? SellDate;

        _assetService.GetPriceAsync(Arg.Any<string>(), effectiveBuy, Arg.Any<CancellationToken>())
                     .Returns(new PricePoint { AssetId = AssetId, PriceDate = effectiveBuy, Close = buyPrice });

        _assetService.GetPriceAsync(Arg.Any<string>(), effectiveSell, Arg.Any<CancellationToken>())
                     .Returns(new PricePoint { AssetId = AssetId, PriceDate = effectiveSell, Close = sellPrice });

        // Bugün için de ihtiyaç olabilir (SellDate null durumu)
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today != effectiveSell)
        {
            _assetService.GetPriceAsync(Arg.Any<string>(), today, Arg.Any<CancellationToken>())
                         .Returns(new PricePoint { AssetId = AssetId, PriceDate = today, Close = sellPrice });
        }
    }

    private static WhatIfRequest MakeRequest(
        string symbol, DateOnly buyDate, DateOnly? sellDate,
        decimal amount, string amountType)
        => new(symbol, buyDate, sellDate, amount, amountType);
}
