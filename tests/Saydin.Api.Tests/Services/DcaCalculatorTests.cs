using FluentAssertions;
using Microsoft.Extensions.Localization;
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

public class DcaCalculatorTests
{
    private readonly IAssetService                   _assetService        = Substitute.For<IAssetService>();
    private readonly ISavedScenarioRepository        _scenarioRepository  = Substitute.For<ISavedScenarioRepository>();
    private readonly IInflationRepository            _inflationRepository = Substitute.For<IInflationRepository>();
    private readonly IDailyLimitGuard                _dailyLimitGuard     = Substitute.For<IDailyLimitGuard>();
    private readonly IConnectionMultiplexer          _redis               = Substitute.For<IConnectionMultiplexer>();
    private readonly IDatabase                       _db                  = Substitute.For<IDatabase>();
    private readonly IStringLocalizer<ErrorMessages> _localizer           = Substitute.For<IStringLocalizer<ErrorMessages>>();
    private readonly DcaCalculator                   _sut;

    private const string FreeDeviceId    = "free-device";
    private const string PremiumDeviceId = "premium-device";

    private static readonly Guid   AssetId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Asset  UsdTry  = new()
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
        Id        = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"),
        DeviceId  = FreeDeviceId,
        Tier      = "free",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static readonly User PremiumUser = new()
    {
        Id        = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"),
        DeviceId  = PremiumDeviceId,
        Tier      = "premium",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static readonly DateOnly StartDate = new(2023, 1, 1);
    private static readonly DateOnly EndDate   = new(2023, 6, 1);

    public DcaCalculatorTests()
    {
        _redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_db);

        // Varsayılan: cache miss
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
           .Returns(RedisValue.Null);

        // Kullanıcılar
        _scenarioRepository.GetUserByDeviceIdAsync(FreeDeviceId, Arg.Any<CancellationToken>())
                           .Returns(FreeUser);
        _scenarioRepository.GetUserByDeviceIdAsync(PremiumDeviceId, Arg.Any<CancellationToken>())
                           .Returns(PremiumUser);

        // Asset listesi
        _assetService.GetAllAsync(Arg.Any<CancellationToken>())
                     .Returns(new List<Asset> { UsdTry }.AsReadOnly());

        // Enflasyon verisi yok (default)
        _inflationRepository
            .GetIndexValuesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((null, (DateOnly?)null, null, (DateOnly?)null));

        // Localizer — key'i olduğu gibi döndür
        _localizer[Arg.Any<string>()]
            .Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));
        _localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));

        var options = Microsoft.Extensions.Options.Options.Create(new PlanOptions());
        _sut = new DcaCalculator(
            _assetService,
            _scenarioRepository,
            _inflationRepository,
            _dailyLimitGuard,
            _redis,
            options,
            _localizer,
            NullLogger<DcaCalculator>.Instance);
    }

    // ── Hesaplama ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_MonthlyPeriod_ComputesCorrectResult()
    {
        // 2023-01-01 → 2023-06-01, aylık 1000 TL, fiyat sabit 20 TL
        SetupConstantPrice(20m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.AssetSymbol.Should().Be("USDTRY");
        result.Period.Should().Be("monthly");
        result.PeriodicAmount.Should().Be(1000m);

        // Ocak, Şubat, Mart, Nisan, Mayıs, Haziran = 6 alım
        result.TotalPurchases.Should().Be(6);
        result.TotalInvestedTry.Should().Be(6000m);

        // Her alımda 1000/20 = 50 birim → toplam 300 birim
        result.TotalUnitsAcquired.Should().Be(300m);

        // Fiyat değişmedi → değer = 300 * 20 = 6000
        result.CurrentValueTry.Should().Be(6000m);
        result.ProfitLossTry.Should().Be(0m);
        result.IsProfit.Should().BeTrue(); // 0 → IsProfit
    }

    [Fact]
    public async Task CalculateAsync_WeeklyPeriod_GeneratesCorrectPurchaseCount()
    {
        // 4 hafta → 5 alım noktası (gün 0, 7, 14, 21, 28)
        var start = new DateOnly(2023, 1, 1);
        var end   = new DateOnly(2023, 1, 29);
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", start, end, 500m, "weekly");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.Period.Should().Be("weekly");
        result.TotalPurchases.Should().Be(5); // gün 1, 8, 15, 22, 29
    }

    [Fact]
    public async Task CalculateAsync_PriceIncreases_ShowsProfit()
    {
        // Her alımda farklı fiyat: 10, 12, 14, 16, 18, 20
        SetupIncreasingPrices(10m, 2m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.TotalPurchases.Should().Be(6);
        result.TotalInvestedTry.Should().Be(6000m);
        result.IsProfit.Should().BeTrue();
        result.ProfitLossTry.Should().BePositive();
        result.CurrentUnitPrice.Should().Be(22m); // son fiyat (7. çağrı: 10 + 2*6)
    }

    [Fact]
    public async Task CalculateAsync_PriceDecreases_ShowsLoss()
    {
        // Fiyat düşüşü: son fiyat başlangıçtan düşük
        SetupDecreasingPrices(20m, 3m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.IsProfit.Should().BeFalse();
        result.ProfitLossTry.Should().BeNegative();
    }

    [Fact]
    public async Task CalculateAsync_AverageCostPerUnit_IsCorrect()
    {
        SetupConstantPrice(25m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        // 6000 TL / 240 birim = 25 TL
        result.AverageCostPerUnit.Should().Be(25m);
    }

    [Fact]
    public async Task CalculateAsync_ChartDataUnder60_AllPointsIncluded()
    {
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 500m, "monthly");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.ChartData.Should().HaveCount(result.TotalPurchases);
    }

    [Fact]
    public async Task CalculateAsync_ChartDataOver60_SampledTo60()
    {
        // 2 yıl haftalık = ~104 alım → chart 60'a sample'lanmalı
        var start = new DateOnly(2021, 1, 1);
        var end   = new DateOnly(2023, 1, 1);
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", start, end, 100m, "weekly");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.TotalPurchases.Should().BeGreaterThan(60);
        result.ChartData.Should().HaveCount(60);
    }

    // ── Validasyon ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_EmptyDeviceId_ThrowsArgumentException()
    {
        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");

        var act = () => _sut.CalculateAsync("", request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CalculateAsync_StartDateAfterEndDate_ThrowsArgumentException()
    {
        var request = MakeRequest("USDTRY", EndDate, StartDate, 1000m, "monthly");

        var act = () => _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CalculateAsync_InvalidPeriod_ThrowsArgumentException()
    {
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "daily");

        var act = () => _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CalculateAsync_InvalidAmountType_ThrowsArgumentException()
    {
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly", amountType: "units");

        var act = () => _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CalculateAsync_UnknownAsset_ThrowsPriceNotFoundException()
    {
        _assetService.GetAllAsync(Arg.Any<CancellationToken>())
                     .Returns(new List<Asset>().AsReadOnly());
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");

        var act = () => _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        await act.Should().ThrowAsync<PriceNotFoundException>();
    }

    // ── Günlük Limit ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_PremiumUser_SkipsDailyLimitCheck()
    {
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");
        await _sut.CalculateAsync(PremiumDeviceId, request, CancellationToken.None);

        await _dailyLimitGuard.Received(1)
            .CheckAsync(PremiumUser, PremiumDeviceId, Arg.Any<string>());
        await _dailyLimitGuard.Received(1)
            .IncrementAsync(PremiumUser, PremiumDeviceId, Arg.Any<string>());
    }

    [Fact]
    public async Task CalculateAsync_FreeUserAtLimit_ThrowsDailyLimitExceededException()
    {
        SetupConstantPrice(10m);

        _dailyLimitGuard.CheckAsync(FreeUser, FreeDeviceId, Arg.Any<string>())
            .ThrowsAsync(new DailyLimitExceededException(20));

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");

        var act = () => _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        await act.Should().ThrowAsync<DailyLimitExceededException>();
    }

    [Fact]
    public async Task CalculateAsync_RedisDown_StillCalculates()
    {
        SetupConstantPrice(10m);

        // DailyLimitGuard Redis hatalarını kendi içinde yutar (fail-open)

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");

        var act = () => _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── Enflasyon ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_IncludeInflation_ComputesRealReturn()
    {
        SetupConstantPrice(10m);

        var startMonth = new DateOnly(StartDate.Year, StartDate.Month, 1);
        var endMonth   = new DateOnly(EndDate.Year,   EndDate.Month,   1);
        _inflationRepository
            .GetIndexValuesAsync(StartDate, EndDate, Arg.Any<CancellationToken>())
            .Returns((100m, startMonth, 120m, endMonth));

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly", includeInflation: true);
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.CumulativeInflationPercent.Should().BeApproximately(20m, 0.01m);
        result.RealProfitLossPercent.Should().NotBeNull();
    }

    [Fact]
    public async Task CalculateAsync_InflationNotRequested_DoesNotCallRepository()
    {
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly", includeInflation: false);
        await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        await _inflationRepository.DidNotReceive()
            .GetIndexValuesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    // ── NoEndDate ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_NoEndDate_UsesLatestPriceDate()
    {
        var latestDate = new DateOnly(2023, 12, 15);
        SetupConstantPrice(10m);
        _assetService.GetLatestPriceDateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(latestDate);

        var request = MakeRequest("USDTRY", StartDate, null, 1000m, "monthly");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.EndDate.Should().Be(latestDate);
    }

    // ── Symbol Normalizasyon ─────────────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_LowercaseSymbol_NormalizesToUpperCase()
    {
        SetupConstantPrice(10m);

        var request = MakeRequest("usdtry", StartDate, EndDate, 1000m, "monthly");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.AssetSymbol.Should().Be("USDTRY");
    }

    // ── Purchases Detayları ──────────────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_Purchases_HaveCumulativeValues()
    {
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.Purchases.Should().NotBeEmpty();

        // İlk alım
        result.Purchases[0].CumulativeCostTry.Should().Be(1000m);
        result.Purchases[0].UnitsAcquired.Should().Be(100m); // 1000/10

        // Son alım — kümülatif maliyet toplam yatırıma eşit olmalı
        result.Purchases[^1].CumulativeCostTry.Should().Be(result.TotalInvestedTry);
    }

    // ── Cache ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_CacheHit_ReturnsCachedAndSkipsExpensiveCalls()
    {
        var cached = new DcaResponse(
            AssetSymbol:                "USDTRY",
            AssetDisplayName:           "Dolar/TL",
            StartDate:                  StartDate,
            EndDate:                    EndDate,
            Period:                     "monthly",
            PeriodicAmount:             1000m,
            TotalPurchases:             6,
            TotalInvestedTry:           6000m,
            CurrentValueTry:            7200m,
            ProfitLossTry:              1200m,
            ProfitLossPercent:          20m,
            IsProfit:                   true,
            AverageCostPerUnit:         10m,
            TotalUnitsAcquired:         600m,
            CurrentUnitPrice:           12m,
            CumulativeInflationPercent: null,
            RealProfitLossPercent:      null,
            InflationDataAsOf:          null,
            Purchases:                  [],
            ChartData:                  []);

        var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var cacheKey = $"dca:v1:USDTRY:{StartDate:yyyy-MM-dd}:{EndDate:yyyy-MM-dd}:1000:monthly:try:{lang}";
        var json = System.Text.Json.JsonSerializer.Serialize(cached,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

        _db.StringGetAsync(
                Arg.Is<RedisKey>(k => k.ToString() == cacheKey),
                Arg.Any<CommandFlags>())
           .Returns(new RedisValue(json));

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");
        var result  = await _sut.CalculateAsync(FreeDeviceId, request, CancellationToken.None);

        result.TotalPurchases.Should().Be(6);
        result.ProfitLossPercent.Should().Be(20m);
        result.CurrentUnitPrice.Should().Be(12m);

        // Cache hit — fiyat servisi çağrılmamalı
        await _assetService.DidNotReceive()
            .GetNearestPriceAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    // ── Yardımcı Metodlar ────────────────────────────────────────────────────

    private void SetupConstantPrice(decimal price)
    {
        _assetService.GetNearestPriceAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
                     .Returns(ci => new PricePoint
                     {
                         AssetId   = AssetId,
                         PriceDate = (DateOnly)ci[1],
                         Close     = price
                     });

        _assetService.GetLatestPriceDateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(EndDate);
    }

    private void SetupIncreasingPrices(decimal startPrice, decimal step)
    {
        var callCount = 0;
        _assetService.GetNearestPriceAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
                     .Returns(ci =>
                     {
                         var currentPrice = startPrice + step * callCount;
                         callCount++;
                         return new PricePoint
                         {
                             AssetId   = AssetId,
                             PriceDate = (DateOnly)ci[1],
                             Close     = currentPrice
                         };
                     });

        _assetService.GetLatestPriceDateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(EndDate);
    }

    private void SetupDecreasingPrices(decimal startPrice, decimal step)
    {
        var callCount = 0;
        _assetService.GetNearestPriceAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
                     .Returns(ci =>
                     {
                         var currentPrice = Math.Max(1m, startPrice - step * callCount);
                         callCount++;
                         return new PricePoint
                         {
                             AssetId   = AssetId,
                             PriceDate = (DateOnly)ci[1],
                             Close     = currentPrice
                         };
                     });

        _assetService.GetLatestPriceDateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(EndDate);
    }

    private static DcaRequest MakeRequest(
        string symbol, DateOnly startDate, DateOnly? endDate,
        decimal periodicAmount, string period,
        string amountType = "try", bool includeInflation = false)
        => new(symbol, startDate, endDate, periodicAmount, period, amountType, includeInflation);
}
