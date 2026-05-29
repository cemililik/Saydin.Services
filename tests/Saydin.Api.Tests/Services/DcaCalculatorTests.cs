using FluentAssertions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;
using Saydin.Api.Options;
using Saydin.Api.Repositories;
using Saydin.Api.Services;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Tests.Services;

public class DcaCalculatorTests
{
    private readonly IAssetService                   _assetService        = Substitute.For<IAssetService>();
    private readonly ISavedScenarioRepository        _scenarioRepository  = Substitute.For<ISavedScenarioRepository>();
    private readonly IInflationRepository            _inflationRepository = Substitute.For<IInflationRepository>();
    private readonly IDailyLimitGuard                _dailyLimitGuard     = Substitute.For<IDailyLimitGuard>();
    private readonly IRedisCacheHelper               _cache               = Substitute.For<IRedisCacheHelper>();
    private readonly IAssetNameLocalizer             _assetNameLocalizer  = Substitute.For<IAssetNameLocalizer>();
    private readonly IStringLocalizer<ErrorMessages> _localizer           = Substitute.For<IStringLocalizer<ErrorMessages>>();
    private readonly IDeviceContext                  _deviceContext       = Substitute.For<IDeviceContext>();
    private readonly FakeTimeProvider                _timeProvider        = new();
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
        _cache.TryGetAsync<DcaResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns((DcaResponse?)null);

        _scenarioRepository.GetUserByDeviceIdAsync(FreeDeviceId, Arg.Any<CancellationToken>())
                           .Returns(FreeUser);
        _scenarioRepository.GetUserByDeviceIdAsync(PremiumDeviceId, Arg.Any<CancellationToken>())
                           .Returns(PremiumUser);

        _assetService.GetBySymbolAsync("USDTRY", Arg.Any<CancellationToken>()).Returns(UsdTry);

        _inflationRepository
            .GetIndexValuesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((null, (DateOnly?)null, null, (DateOnly?)null));

        _localizer[Arg.Any<string>()]
            .Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));
        _localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));

        _assetNameLocalizer.Localize(Arg.Any<string>(), Arg.Any<string?>())
                           .Returns(ci => (string?)ci[1] ?? (string)ci[0]);

        // F2.2-3: device id artık IDeviceContext'ten; varsayılan = free kullanıcı cihazı.
        _deviceContext.DeviceId.Returns(FreeDeviceId);

        // F2.2-22: Default test PlanOptions free PriceHistoryMonths sınırını kapatır,
        // aksi halde geçmişe ait test BuyDate'leri "extended_history" sebebiyle
        // FeatureDisabled fırlatır.
        var defaultPlans = new PlanOptions
        {
            Free    = new TierOptions { Features = new FeatureOptions { PriceHistoryMonths = 0 } },
            Premium = new TierOptions { Features = new FeatureOptions { PriceHistoryMonths = 0 } }
        };
        var options = Microsoft.Extensions.Options.Options.Create(defaultPlans);
        _sut = new DcaCalculator(
            _assetService, _scenarioRepository, _inflationRepository,
            _dailyLimitGuard, _deviceContext, _timeProvider, _cache, _assetNameLocalizer,
            options, _localizer, NullLogger<DcaCalculator>.Instance);
    }

    // ── Hesaplama ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_MonthlyPeriod_ComputesCorrectResult()
    {
        SetupConstantPrice(20m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.AssetSymbol.Should().Be("USDTRY");
        result.Period.Should().Be("monthly");
        result.PeriodicAmount.Should().Be(1000m);
        result.TotalPurchases.Should().Be(6);
        result.TotalInvestedTry.Should().Be(6000m);
        result.TotalUnitsAcquired.Should().Be(300m);
        result.CurrentValueTry.Should().Be(6000m);
        result.ProfitLossTry.Should().Be(0m);
        result.IsProfit.Should().BeTrue();
    }

    [Fact]
    public async Task CalculateAsync_WeeklyPeriod_GeneratesCorrectPurchaseCount()
    {
        var start = new DateOnly(2023, 1, 1);
        var end   = new DateOnly(2023, 1, 29);
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", start, end, 500m, "weekly");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.Period.Should().Be("weekly");
        result.TotalPurchases.Should().Be(5);
    }

    [Fact]
    public async Task CalculateAsync_PriceIncreases_ShowsProfit()
    {
        SetupIncreasingPrices(10m, 2m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.TotalPurchases.Should().Be(6);
        result.TotalInvestedTry.Should().Be(6000m);
        result.IsProfit.Should().BeTrue();
        result.ProfitLossTry.Should().BePositive();
        result.CurrentUnitPrice.Should().Be(22m);
    }

    [Fact]
    public async Task CalculateAsync_PriceDecreases_ShowsLoss()
    {
        SetupDecreasingPrices(20m, 3m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.IsProfit.Should().BeFalse();
        result.ProfitLossTry.Should().BeNegative();
    }

    [Fact]
    public async Task CalculateAsync_AverageCostPerUnit_IsCorrect()
    {
        SetupConstantPrice(25m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.AverageCostPerUnit.Should().Be(25m);
    }

    [Fact]
    public async Task CalculateAsync_ChartDataUnder60_AllPointsIncluded()
    {
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 500m, "monthly");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.ChartData.Should().HaveCount(result.TotalPurchases);
    }

    [Fact]
    public async Task CalculateAsync_ChartDataOver60_SampledTo60()
    {
        var start = new DateOnly(2021, 1, 1);
        var end   = new DateOnly(2023, 1, 1);
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", start, end, 100m, "weekly");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.TotalPurchases.Should().BeGreaterThan(60);
        result.ChartData.Should().HaveCount(60);
    }

    // ── Validasyon ───────────────────────────────────────────────────────────

    // F2.2-3: deviceId boş/whitespace doğrulaması artık RequireDeviceId endpoint
    // filter'ında (400 + ProblemDetails). Servis device id'yi IDeviceContext'ten okur;
    // servis-seviyesi "EmptyDeviceId" testi kaldırıldı (context sözleşmesi DeviceContextTests'te).

    // P1R-008 / P1R-017: Hafta sonu / tatil clip ile aynı PriceDate'e iki alım düşerse
    // satır dedup edilir, toplam tutar ve birim cumulative değerler korunur.
    // 6 niyetli alım → iki tanesi aynı clip tarihine düşer → `purchases.Count` = 5,
    // ama `TotalInvestedTry` ve `TotalUnitsAcquired` 6 alımın toplamı kadar olmalı.
    [Fact]
    public async Task CalculateAsync_WeekendClipCausesSameDayPurchase_AggregatesIntoSingleRow()
    {
        // İlk 2 ay (Ocak, Şubat) ortak bir piyasa gününe (28 Şubat) clip olsun;
        // Mart, Nisan, Mayıs, Haziran ayları kendi tarihleriyle gelsin → 6 niyetli
        // alımdan sadece 1 dedup, beklenen `purchases.Count` = 5.
        var sameClipDate = new DateOnly(2023, 2, 28);
        var callCount    = 0;
        _assetService.GetNearestPriceAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
                     .Returns(ci =>
                     {
                         callCount++;
                         var date = callCount <= 2 ? sameClipDate : (DateOnly)ci[1];
                         return new PricePoint
                         {
                             AssetId   = AssetId,
                             PriceDate = date,
                             Close     = 10m,
                         };
                     });
        _assetService.GetLatestPriceDateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(EndDate);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        // 6 alım niyetlendi, 2 tanesi aynı güne çakıştı → satır sayısı 5.
        result.Purchases.Should().HaveCount(5);

        // Cumulative toplamlar 6 alımın değeriyle eşit kalmalı:
        result.TotalInvestedTry.Should().Be(6000m);
        result.TotalUnitsAcquired.Should().Be(600m);

        // Dedup edilen ilk satırda iki alımın birikmiş UnitsAcquired'ı bulunmalı.
        var clipped = result.Purchases[0];
        clipped.Date.Should().Be(sameClipDate);
        clipped.UnitsAcquired.Should().Be(200m);            // 2 × (1000 / 10)
        clipped.CumulativeCostTry.Should().Be(2000m);       // 2 × 1000
        clipped.CumulativeUnits.Should().Be(200m);
    }

    [Fact]
    public async Task CalculateAsync_StartDateAfterEndDate_ThrowsValidationException()
    {
        SetupConstantPrice(10m);
        var request = MakeRequest("USDTRY", EndDate, StartDate, 1000m, "monthly");

        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CalculateAsync_InvalidPeriod_ThrowsValidationException()
    {
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "daily");

        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CalculateAsync_InvalidAmountType_ThrowsValidationException()
    {
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly", amountType: "units");

        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    // F1.9-4 ([C-F-14]): Negatif / sıfır periyodik tutar pozitif zorunlu.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public async Task CalculateAsync_NonPositivePeriodicAmount_ThrowsValidationException(decimal amount)
    {
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, amount, "monthly");

        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
                 .Where(ex => ex.Field == nameof(request.PeriodicAmount));
    }

    [Fact]
    public async Task CalculateAsync_UnknownAsset_ThrowsAssetNotFoundException()
    {
        _assetService.GetBySymbolAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns((Asset?)null);
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");

        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<AssetNotFoundException>();
    }

    [Fact]
    public async Task CalculateAsync_ZeroPriceMidStream_ThrowsPriceNotFoundException()
    {
        // 3. çağrıdan sonra fiyat 0 dönerse divide-by-zero değil PriceNotFoundException
        // çıkmalı (review C-2 / DCA path).
        var callCount = 0;
        _assetService.GetNearestPriceAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCount++;
                var price = callCount == 3 ? 0m : 10m;
                return new PricePoint { AssetId = AssetId, PriceDate = (DateOnly)ci[1], Close = price };
            });
        _assetService.GetLatestPriceDateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(EndDate);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");

        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<PriceNotFoundException>();
    }

    [Fact]
    public async Task CalculateAsync_TooManyPurchasePoints_ThrowsValidationException()
    {
        // 20 yıl haftalık ≈ 1040 nokta → MaxPurchasePoints (600) üstü → reddedilmeli.
        var start = new DateOnly(2003, 1, 1);
        var end   = new DateOnly(2023, 12, 31);
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", start, end, 100m, "weekly");

        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    // ── Günlük Limit (TryAcquire + Release) ──────────────────────────────────

    [Fact]
    public async Task CalculateAsync_PremiumUser_CallsTryAcquireOnce()
    {
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");
        _deviceContext.DeviceId.Returns(PremiumDeviceId);
        await _sut.CalculateAsync(request, CancellationToken.None);

        await _dailyLimitGuard.Received(1)
            .TryAcquireAsync(PremiumUser, PremiumDeviceId, Arg.Any<string>(), null, Arg.Any<CancellationToken>());
        await _dailyLimitGuard.DidNotReceive()
            .ReleaseAsync(PremiumUser, PremiumDeviceId, Arg.Any<string>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CalculateAsync_FreeUserAtLimit_TryAcquireThrows()
    {
        SetupConstantPrice(10m);

        _dailyLimitGuard.TryAcquireAsync(FreeUser, FreeDeviceId, Arg.Any<string>(), null, Arg.Any<CancellationToken>())
            .ThrowsAsync(new DailyLimitExceededException(20));

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");

        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<DailyLimitExceededException>();
        // Quota rejection sonrası release çağırılmamalı (acquire fail oldu, geri verilecek bir şey yok)
        await _dailyLimitGuard.DidNotReceive()
            .ReleaseAsync(FreeUser, FreeDeviceId, Arg.Any<string>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CalculateAsync_CoreCalculationFails_ReleasesQuota()
    {
        _assetService.GetBySymbolAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns((Asset?)null);
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");

        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<AssetNotFoundException>();
        await _dailyLimitGuard.Received(1)
            .ReleaseAsync(FreeUser, FreeDeviceId, Arg.Any<string>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CalculateAsync_RedisDown_StillCalculates()
    {
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");
        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

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
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.CumulativeInflationPercent.Should().BeApproximately(20m, 0.01m);
        result.RealProfitLossPercent.Should().NotBeNull();
    }

    [Fact]
    public async Task CalculateAsync_InflationNotRequested_DoesNotCallRepository()
    {
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly", includeInflation: false);
        await _sut.CalculateAsync(request, CancellationToken.None);

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
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.EndDate.Should().Be(latestDate);
    }

    [Fact]
    public async Task CalculateAsync_LowercaseSymbol_NormalizesToUpperCase()
    {
        SetupConstantPrice(10m);

        var request = MakeRequest("usdtry", StartDate, EndDate, 1000m, "monthly");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.AssetSymbol.Should().Be("USDTRY");
    }

    [Fact]
    public async Task CalculateAsync_Purchases_HaveCumulativeValues()
    {
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.Purchases.Should().NotBeEmpty();
        result.Purchases[0].CumulativeCostTry.Should().Be(1000m);
        result.Purchases[0].UnitsAcquired.Should().Be(100m);
        result.Purchases[^1].CumulativeCostTry.Should().Be(result.TotalInvestedTry);
    }

    [Fact]
    public async Task CalculateAsync_CacheHit_ReturnsCachedAndSkipsExpensiveCalls()
    {
        var cached = new DcaResponse(
            AssetSymbol: "USDTRY", AssetDisplayName: "Dolar/TL",
            StartDate: StartDate, EndDate: EndDate,
            Period: "monthly", PeriodicAmount: 1000m,
            TotalPurchases: 6, TotalInvestedTry: 6000m, CurrentValueTry: 7200m,
            ProfitLossTry: 1200m, ProfitLossPercent: 20m, IsProfit: true,
            AverageCostPerUnit: 10m, TotalUnitsAcquired: 600m, CurrentUnitPrice: 12m,
            CumulativeInflationPercent: null, RealProfitLossPercent: null,
            InflationDataAsOf: null, Purchases: [], ChartData: []);

        _cache.TryGetAsync<DcaResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(cached);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.TotalPurchases.Should().Be(6);
        result.ProfitLossPercent.Should().Be(20m);
        result.CurrentUnitPrice.Should().Be(12m);

        await _assetService.DidNotReceive()
            .GetNearestPriceAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CalculateAsync_DcaFeatureDisabled_ThrowsFeatureDisabled()
    {
        var planOptions = new PlanOptions
        {
            Free = new TierOptions { Features = new FeatureOptions { Dca = false } }
        };
        var sut = new DcaCalculator(
            _assetService, _scenarioRepository, _inflationRepository,
            _dailyLimitGuard, _deviceContext, _timeProvider, _cache, _assetNameLocalizer,
            Microsoft.Extensions.Options.Options.Create(planOptions),
            _localizer, NullLogger<DcaCalculator>.Instance);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");

        var act = () => sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<FeatureDisabledException>();
    }

    [Fact]
    public async Task CalculateAsync_InflationRequestedButFeatureDisabled_ThrowsFeatureDisabled()
    {
        var planOptions = new PlanOptions
        {
            Free = new TierOptions { Features = new FeatureOptions { InflationAdjustment = false } }
        };
        var sut = new DcaCalculator(
            _assetService, _scenarioRepository, _inflationRepository,
            _dailyLimitGuard, _deviceContext, _timeProvider, _cache, _assetNameLocalizer,
            Microsoft.Extensions.Options.Options.Create(planOptions),
            _localizer, NullLogger<DcaCalculator>.Instance);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly", includeInflation: true);

        var act = () => sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<FeatureDisabledException>();
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
