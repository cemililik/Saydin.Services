using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;
using Saydin.Api.Options;
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
    private readonly IInflationRepository     _inflationRepository = Substitute.For<IInflationRepository>();
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

        // Varsayılan: enflasyon verisi yok (IncludeInflation = false testleri etkilenmez)
        _inflationRepository
            .GetIndexValuesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((null, (DateOnly?)null, null, (DateOnly?)null));

        var options = Options.Create(new PlanOptions());
        _sut = new WhatIfCalculator(
            _assetService,
            _scenarioRepository,
            _inflationRepository,
            _redis,
            options,
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
            ProfitLossTry: 2.55m, ProfitLossPercent: 42.86m, IsProfit: true,
            PriceHistory: Array.Empty<PriceHistoryPoint>(),
            CumulativeInflationPercent: null, RealProfitLossPercent: null,
            InflationDataAsOf: null, ActualBuyDate: null, ActualSellDate: null);

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
        await _assetService.Received().GetNearestPriceAsync("USDTRY", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    // ── SamplePriceHistory (CalculateAsync üzerinden) ─────────────────────────

    [Fact]
    public async Task CalculateAsync_PriceHistoryUnder60Points_AllPointsIncluded()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);
        SetupPriceRange(30);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.PriceHistory.Should().HaveCount(30);
    }

    [Fact]
    public async Task CalculateAsync_PriceHistoryOver60Points_SampledTo60()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);
        SetupPriceRange(100);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.PriceHistory.Should().HaveCount(60);
    }

    [Fact]
    public async Task CalculateAsync_PriceHistoryOver60Points_FirstAndLastAlwaysIncluded()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var points = Enumerable.Range(0, 100)
            .Select(i => new PricePoint
            {
                AssetId   = AssetId,
                PriceDate = BuyDate.AddDays(i),
                Close     = 5.95m + i * 0.02m
            })
            .ToList();

        _assetService.GetPriceRangeAsync(
                Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(points.AsReadOnly());

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.PriceHistory.Should().HaveCount(60);
        result.PriceHistory[0].Date.Should().Be(points.First().PriceDate);
        result.PriceHistory[^1].Date.Should().Be(points.Last().PriceDate);
        result.PriceHistory.Should().BeInAscendingOrder(p => p.Date);
        var first = points.First().PriceDate;
        var last  = points.Last().PriceDate;
        result.PriceHistory.Should().AllSatisfy(p =>
            (p.Date >= first && p.Date <= last).Should().BeTrue());
    }

    [Fact]
    public async Task CalculateAsync_EmptyPriceRange_ReturnsEmptyPriceHistory()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        _assetService.GetPriceRangeAsync(
                Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(Array.Empty<PricePoint>().ToList().AsReadOnly());

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.PriceHistory.Should().BeEmpty();
    }

    [Fact]
    public async Task CalculateAsync_PriceHistory_ExactlyContainedInResponse()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);
        SetupPriceRange(5);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.PriceHistory.Should().HaveCount(5);
        result.PriceHistory[0].Date.Should().Be(BuyDate);
        result.PriceHistory[0].Price.Should().Be(5.95m);
        result.PriceHistory[4].Date.Should().Be(BuyDate.AddDays(4));
        result.PriceHistory[4].Price.Should().Be(5.99m);
        result.PriceHistory.Should().BeInAscendingOrder(p => p.Date);
    }

    [Fact]
    public async Task CalculateAsync_PriceHistoryFetchFails_ReturnsEmptyPriceHistory()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        _assetService.GetPriceRangeAsync(
                Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .ThrowsAsync(new TimeoutException("Bağlantı zaman aşımı"));

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.PriceHistory.Should().BeEmpty();
    }

    // ── Yardımcı Metodlar ────────────────────────────────────────────────────

    private void SetupPriceRange(int count)
    {
        var points = Enumerable.Range(0, count)
            .Select(i => new PricePoint
            {
                AssetId   = AssetId,
                PriceDate = BuyDate.AddDays(i),
                Close     = 5.95m + i * 0.01m
            })
            .ToList();

        _assetService.GetPriceRangeAsync(
                Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(points.AsReadOnly());
    }

    /// <summary>
    /// Fiyat mock'larını GetNearestPriceAsync üzerinden kurar.
    /// actualBuyDate / actualSellDate verilirse dönen PricePoint.PriceDate onlar olur (tarih kaydırma simülasyonu).
    /// </summary>
    private void SetupPrices(
        decimal buyPrice, decimal sellPrice,
        DateOnly? buyDate = null, DateOnly? sellDate = null,
        DateOnly? actualBuyDate = null, DateOnly? actualSellDate = null)
    {
        var effectiveBuy  = buyDate  ?? BuyDate;
        var effectiveSell = sellDate ?? SellDate;

        _assetService.GetNearestPriceAsync(Arg.Any<string>(), effectiveBuy, Arg.Any<CancellationToken>())
                     .Returns(new PricePoint
                     {
                         AssetId   = AssetId,
                         PriceDate = actualBuyDate ?? effectiveBuy,
                         Close     = buyPrice
                     });

        _assetService.GetNearestPriceAsync(Arg.Any<string>(), effectiveSell, Arg.Any<CancellationToken>())
                     .Returns(new PricePoint
                     {
                         AssetId   = AssetId,
                         PriceDate = actualSellDate ?? effectiveSell,
                         Close     = sellPrice
                     });

        // SellDate null olduğunda GetLatestPriceDateAsync çağrılır — effectiveSell döndür
        _assetService.GetLatestPriceDateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(effectiveSell);

        // Bugün için de ihtiyaç olabilir (SellDate null durumu)
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today != effectiveSell)
        {
            _assetService.GetNearestPriceAsync(Arg.Any<string>(), today, Arg.Any<CancellationToken>())
                         .Returns(new PricePoint { AssetId = AssetId, PriceDate = today, Close = sellPrice });
        }
    }

    // ── Haftasonu / Tarih Düzeltmesi ────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_BuyDateAdjusted_PopulatesActualBuyDate()
    {
        // Kullanıcı Cumartesi seçti; nearest price → Cuma fiyatını döndürür
        var saturday = new DateOnly(2020, 1, 4); // Cumartesi
        var friday   = new DateOnly(2020, 1, 3); // Cuma

        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m,
            buyDate: saturday, actualBuyDate: friday);

        var request = MakeRequest("USDTRY", saturday, SellDate, 1000m, "try");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.ActualBuyDate.Should().Be(friday);
        result.ActualSellDate.Should().BeNull();
    }

    [Fact]
    public async Task CalculateAsync_SellDateAdjusted_PopulatesActualSellDate()
    {
        var sunday   = new DateOnly(2021, 1, 3); // Pazar
        var friday   = new DateOnly(2021, 1, 1); // Cuma

        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m,
            sellDate: sunday, actualSellDate: friday);

        var request = MakeRequest("USDTRY", BuyDate, sunday, 1000m, "try");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.ActualSellDate.Should().Be(friday);
        result.ActualBuyDate.Should().BeNull();
    }

    [Fact]
    public async Task CalculateAsync_DatesExactlyMatch_ActualDatesNull()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.ActualBuyDate.Should().BeNull();
        result.ActualSellDate.Should().BeNull();
    }

    // ── Enflasyon Düzeltmesi ─────────────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_IncludeInflation_ComputesRealReturn()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        // Alış endeksi: 100, Satış endeksi: 150 → birikimli enflasyon %50
        var buyMonth  = new DateOnly(BuyDate.Year,  BuyDate.Month,  1);
        var sellMonth = new DateOnly(SellDate.Year, SellDate.Month, 1);
        _inflationRepository
            .GetIndexValuesAsync(BuyDate, SellDate, Arg.Any<CancellationToken>())
            .Returns((100m, buyMonth, 150m, sellMonth));

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 10_000m, "try", includeInflation: true);
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.CumulativeInflationPercent.Should().BeApproximately(50m, 0.01m);
        // nominal ~%42.86, enflasyon %50 → reel negatif
        result.RealProfitLossPercent.Should().NotBeNull();
        result.RealProfitLossPercent.Should().BeLessThan(0);
        result.InflationDataAsOf.Should().BeNull(); // tam ay eşleşmesi
    }

    [Fact]
    public async Task CalculateAsync_InflationSellMonthLagged_PopulatesInflationDataAsOf()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        // Satış ayı Ocak 2021, mevcut veri Kasım 2020
        var buyMonth     = new DateOnly(BuyDate.Year,  BuyDate.Month,  1);
        var laggedMonth  = new DateOnly(2020, 11, 1);   // Kasım 2020 (2 ay eski)
        var expectedSell = new DateOnly(SellDate.Year, SellDate.Month, 1); // Ocak 2021

        laggedMonth.Should().BeLessThan(expectedSell); // LKV geçerliliğini doğrula

        _inflationRepository
            .GetIndexValuesAsync(BuyDate, SellDate, Arg.Any<CancellationToken>())
            .Returns((100m, buyMonth, 140m, laggedMonth));

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 10_000m, "try", includeInflation: true);
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.InflationDataAsOf.Should().Be(laggedMonth);
        result.RealProfitLossPercent.Should().NotBeNull();
    }

    [Fact]
    public async Task CalculateAsync_InflationDataUnavailable_NullRealReturn()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);
        // Repository zaten null döndürüyor (constructor default)

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 10_000m, "try", includeInflation: true);
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.RealProfitLossPercent.Should().BeNull();
        result.CumulativeInflationPercent.Should().BeNull();
        result.InflationDataAsOf.Should().BeNull();
    }

    [Fact]
    public async Task CalculateAsync_InflationNotRequested_DoesNotCallRepository()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 10_000m, "try", includeInflation: false);
        await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        await _inflationRepository.DidNotReceive()
            .GetIndexValuesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    // ── Yardımcı Metodlar ────────────────────────────────────────────────────

    private static WhatIfRequest MakeRequest(
        string symbol, DateOnly buyDate, DateOnly? sellDate,
        decimal amount, string amountType, bool includeInflation = false)
        => new(symbol, buyDate, sellDate, amount, amountType, includeInflation);
}
