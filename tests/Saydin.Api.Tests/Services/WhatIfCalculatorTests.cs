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

public class WhatIfCalculatorTests
{
    private readonly IAssetService                _assetService       = Substitute.For<IAssetService>();
    private readonly ISavedScenarioRepository     _scenarioRepository = Substitute.For<ISavedScenarioRepository>();
    private readonly IInflationRepository         _inflationRepository = Substitute.For<IInflationRepository>();
    private readonly IDailyLimitGuard             _dailyLimitGuard    = Substitute.For<IDailyLimitGuard>();
    private readonly IRedisCacheHelper            _cache              = Substitute.For<IRedisCacheHelper>();
    private readonly IAssetNameLocalizer          _assetNameLocalizer = Substitute.For<IAssetNameLocalizer>();
    private readonly IStringLocalizer<ErrorMessages> _localizer       = Substitute.For<IStringLocalizer<ErrorMessages>>();
    private readonly IDeviceContext                  _deviceContext   = Substitute.For<IDeviceContext>();
    private readonly FakeTimeProvider                _timeProvider    = new();
    private readonly WhatIfCalculator             _sut;

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
        // Varsayılan: cache miss (TryGetAsync returns null)
        _cache.TryGetAsync<WhatIfResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns((WhatIfResponse?)null);
        _cache.TryGetAsync<ReverseWhatIfResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns((ReverseWhatIfResponse?)null);

        // Varsayılan: free kullanıcı
        _scenarioRepository.GetUserByDeviceIdAsync(FreeDeviceId, Arg.Any<CancellationToken>())
                           .Returns(FreeUser);
        _scenarioRepository.GetUserByDeviceIdAsync(PremiumDeviceId, Arg.Any<CancellationToken>())
                           .Returns(PremiumUser);
        _scenarioRepository.GetUserByDeviceIdAsync(DeviceId, Arg.Any<CancellationToken>())
                           .Returns((User?)null);

        // Varsayılan: asset lookup
        _assetService.GetBySymbolAsync("USDTRY", Arg.Any<CancellationToken>())
                     .Returns(UsdTry);

        // Varsayılan: enflasyon verisi yok
        _inflationRepository
            .GetIndexValuesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((null, (DateOnly?)null, null, (DateOnly?)null));

        // Varsayılan: localizer — key'i olduğu gibi döndür
        _localizer[Arg.Any<string>()]
            .Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));
        _localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));

        _assetNameLocalizer.Localize(Arg.Any<string>(), Arg.Any<string?>())
                           .Returns(ci => (string?)ci[1] ?? (string)ci[0]);

        // F2.2-21: Default test PlanOptions free PriceHistoryMonths sınırını kapatır,
        // aksi halde 2020-2021 tarihli WhatIf testleri "extended_history" sebebiyle
        // FeatureDisabled fırlatır. Sınır enforcement'ı için ayrı testler vardır.
        var defaultPlans = new PlanOptions
        {
            Free    = new TierOptions { Features = new FeatureOptions { PriceHistoryMonths = 0 } },
            Premium = new TierOptions { Features = new FeatureOptions { PriceHistoryMonths = 0 } }
        };
        // F2.2-3: device id artık IDeviceContext'ten; varsayılan = free kullanıcı cihazı.
        _deviceContext.DeviceId.Returns(FreeDeviceId);

        var options = Microsoft.Extensions.Options.Options.Create(defaultPlans);
        _sut = new WhatIfCalculator(
            _assetService,
            _scenarioRepository,
            _inflationRepository,
            _dailyLimitGuard,
            _deviceContext,
            _timeProvider,
            _cache,
            _assetNameLocalizer,
            options,
            _localizer,
            NullLogger<WhatIfCalculator>.Instance);
    }

    // ── Hesaplama (AmountType: try) ──────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_AmountTypeTry_ComputesCorrectResult()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 10_000m, "try");

        var result = await _sut.CalculateAsync(request, CancellationToken.None);

        result.AssetSymbol.Should().Be("USDTRY");
        result.BuyPrice.Should().Be(5.95m);
        result.SellPrice.Should().Be(8.50m);
        result.InitialValueTry.Should().Be(10_000m);

        result.UnitsAcquired.Should().Be(Math.Round(10_000m / 5.95m, 6, MidpointRounding.AwayFromZero));

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

        var result = await _sut.CalculateAsync(request, CancellationToken.None);

        result.UnitsAcquired.Should().Be(100m);
        result.InitialValueTry.Should().Be(Math.Round(100m * 5.95m, 2, MidpointRounding.AwayFromZero));
        result.FinalValueTry.Should().Be(Math.Round(100m * 8.50m, 2, MidpointRounding.AwayFromZero));
        result.IsProfit.Should().BeTrue();
    }

    [Fact]
    public async Task CalculateAsync_AmountTypeGrams_ComputesCorrectResult()
    {
        SetupPrices(buyPrice: 1000m, sellPrice: 1500m);

        var xau = new Asset { Id = Guid.NewGuid(), Symbol = "XAUTRY", DisplayName = "Altın/TL",
                              Category = AssetCategory.PreciousMetal, Source = "goldapi", IsActive = true };
        _assetService.GetBySymbolAsync("XAUTRY", Arg.Any<CancellationToken>()).Returns(xau);

        var request = MakeRequest("XAUTRY", BuyDate, SellDate, 50m, "grams");

        var result = await _sut.CalculateAsync(request, CancellationToken.None);

        result.UnitsAcquired.Should().Be(50m);
        result.InitialValueTry.Should().Be(Math.Round(50m * 1000m, 2, MidpointRounding.AwayFromZero));
        result.FinalValueTry.Should().Be(Math.Round(50m * 1500m, 2, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public async Task CalculateAsync_LossScenario_IsProfitFalse()
    {
        SetupPrices(buyPrice: 10m, sellPrice: 5m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.IsProfit.Should().BeFalse();
        result.ProfitLossTry.Should().BeNegative();
        result.ProfitLossPercent.Should().Be(-50m);
    }

    [Fact]
    public async Task CalculateAsync_BreakevenScenario_IsProfitTrue()
    {
        SetupPrices(buyPrice: 10m, sellPrice: 10m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.IsProfit.Should().BeTrue();
        result.ProfitLossTry.Should().Be(0m);
        result.ProfitLossPercent.Should().Be(0m);
    }

    [Fact]
    public async Task CalculateAsync_NoSellDate_UsesTodayAsDefault()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        SetupPrices(buyPrice: 5.95m, sellPrice: 30m, sellDate: today);

        var request = new WhatIfRequest("USDTRY", BuyDate, SellDate: null, 10_000m, "try");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.SellDate.Should().Be(today);
    }

    // ── Validasyon ───────────────────────────────────────────────────────────

    // F2.2-3: deviceId boş/whitespace doğrulaması artık servis katmanında DEĞİL —
    // RequireDeviceId endpoint filter'ında yapılır (400 + ProblemDetails). Servis,
    // device id'yi scoped IDeviceContext'ten okur. Önceki "CalculateAsync_EmptyDeviceId"
    // servis-seviyesi testi bu nedenle kaldırıldı; context sözleşmesi DeviceContextTests'te.

    [Fact]
    public async Task CalculateAsync_BuyDateAfterSellDate_ThrowsValidationException()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);
        var request = MakeRequest("USDTRY", SellDate, BuyDate, 1000m, "try");

        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
                 .WithMessage("*BuyDateAfterSellDate*");
    }

    [Fact]
    public async Task CalculateAsync_InvalidAmountType_ThrowsValidationException()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "eur");

        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
                 .WithMessage("*InvalidAmountType*");
    }

    [Fact]
    public async Task CalculateAsync_UnknownAsset_ThrowsAssetNotFoundException()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);
        _assetService.GetBySymbolAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns((Asset?)null);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");

        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<AssetNotFoundException>();
    }

    [Fact]
    public async Task CalculateAsync_BuyPriceZero_ThrowsPriceNotFoundException()
    {
        SetupPrices(buyPrice: 0m, sellPrice: 8.50m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");

        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<PriceNotFoundException>();
    }

    // ── Günlük Limit (TryAcquire + Release) ──────────────────────────────────

    [Fact]
    public async Task CalculateAsync_PremiumUser_StillCallsTryAcquire()
    {
        // Guard içinde tier check var; calculator hiçbir bypass yapmaz.
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        _deviceContext.DeviceId.Returns(PremiumDeviceId);
        await _sut.CalculateAsync(request, CancellationToken.None);

        await _dailyLimitGuard.Received(1)
            .TryAcquireAsync(PremiumUser, PremiumDeviceId, Arg.Any<string>(), null, Arg.Any<CancellationToken>());
        // Premium success → release çağırılmaz (yalnızca failure path'inde release var)
        await _dailyLimitGuard.DidNotReceive()
            .ReleaseAsync(PremiumUser, PremiumDeviceId, Arg.Any<string>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CalculateAsync_FreeUserUnderLimit_Succeeds()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        var act     = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().NotThrowAsync<DailyLimitExceededException>();
    }

    [Fact]
    public async Task CalculateAsync_FreeUserAtLimit_TryAcquireThrows()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        _dailyLimitGuard.TryAcquireAsync(FreeUser, FreeDeviceId, Arg.Any<string>(), null, Arg.Any<CancellationToken>())
            .ThrowsAsync(new DailyLimitExceededException(20));

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");

        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<DailyLimitExceededException>()
                 .Where(ex => ex.Limit == 20);

        // TOCTOU race: limit reddi sonrası release çağrılmaz (acquire fail oldu, geri verilecek bir şey yok)
        await _dailyLimitGuard.DidNotReceive()
            .ReleaseAsync(FreeUser, FreeDeviceId, Arg.Any<string>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CalculateAsync_CoreCalculationFails_ReleasesQuota()
    {
        // İç hesap sürecinde fırlatılan exception kotanın iade edilmesini tetiklemeli
        // ("başarısız hesap kotadan düşmesin", review H-6).
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);
        _assetService.GetBySymbolAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns((Asset?)null); // → AssetNotFoundException

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");

        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<AssetNotFoundException>();
        await _dailyLimitGuard.Received(1)
            .ReleaseAsync(FreeUser, FreeDeviceId, Arg.Any<string>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CalculateAsync_UnknownDevice_PassesNullUserToGuard()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        _deviceContext.DeviceId.Returns(DeviceId);
        await _sut.CalculateAsync(request, CancellationToken.None);

        await _dailyLimitGuard.Received(1)
            .TryAcquireAsync(null, DeviceId, Arg.Any<string>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CalculateAsync_RedisDownForLimitCheck_StillCalculates()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        // DailyLimitGuard Redis hatalarını kendi içinde yutar (fail-open).
        // Varsayılan mock davranışı: exception fırlatmaz.

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

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

        _cache.TryGetAsync<WhatIfResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(cached);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1m, "units");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

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
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.AssetSymbol.Should().Be("USDTRY");
        await _assetService.Received().GetNearestPriceAsync("USDTRY", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    // ── SamplePriceHistory ────────────────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_PriceHistoryUnder60Points_AllPointsIncluded()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);
        SetupPriceRange(30);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.PriceHistory.Should().HaveCount(30);
    }

    [Fact]
    public async Task CalculateAsync_PriceHistoryOver60Points_SampledTo60()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);
        SetupPriceRange(100);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

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
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.PriceHistory.Should().HaveCount(60);
        result.PriceHistory[0].Date.Should().Be(points.First().PriceDate);
        result.PriceHistory[^1].Date.Should().Be(points.Last().PriceDate);
        result.PriceHistory.Should().BeInAscendingOrder(p => p.Date);
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
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.PriceHistory.Should().BeEmpty();
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
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

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

        _assetService.GetLatestPriceDateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(effectiveSell);

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
        var saturday = new DateOnly(2020, 1, 4);
        var friday   = new DateOnly(2020, 1, 3);

        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m,
            buyDate: saturday, actualBuyDate: friday);

        var request = MakeRequest("USDTRY", saturday, SellDate, 1000m, "try");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.ActualBuyDate.Should().Be(friday);
        result.ActualSellDate.Should().BeNull();
    }

    [Fact]
    public async Task CalculateAsync_SellDateAdjusted_PopulatesActualSellDate()
    {
        var sunday   = new DateOnly(2021, 1, 3);
        var friday   = new DateOnly(2021, 1, 1);

        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m,
            sellDate: sunday, actualSellDate: friday);

        var request = MakeRequest("USDTRY", BuyDate, sunday, 1000m, "try");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.ActualSellDate.Should().Be(friday);
        result.ActualBuyDate.Should().BeNull();
    }

    [Fact]
    public async Task CalculateAsync_DatesExactlyMatch_ActualDatesNull()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try");
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.ActualBuyDate.Should().BeNull();
        result.ActualSellDate.Should().BeNull();
    }

    // ── Enflasyon Düzeltmesi ─────────────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_IncludeInflation_ComputesRealReturn()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var buyMonth  = new DateOnly(BuyDate.Year,  BuyDate.Month,  1);
        var sellMonth = new DateOnly(SellDate.Year, SellDate.Month, 1);
        _inflationRepository
            .GetIndexValuesAsync(BuyDate, SellDate, Arg.Any<CancellationToken>())
            .Returns((100m, buyMonth, 150m, sellMonth));

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 10_000m, "try", includeInflation: true);
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.CumulativeInflationPercent.Should().BeApproximately(50m, 0.01m);
        result.RealProfitLossPercent.Should().NotBeNull();
        result.RealProfitLossPercent.Should().BeLessThan(0);
        result.InflationDataAsOf.Should().BeNull();
    }

    [Fact]
    public async Task CalculateAsync_InflationSellMonthLagged_PopulatesInflationDataAsOf()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var buyMonth     = new DateOnly(BuyDate.Year,  BuyDate.Month,  1);
        var laggedMonth  = new DateOnly(2020, 11, 1);
        var expectedSell = new DateOnly(SellDate.Year, SellDate.Month, 1);

        (laggedMonth < expectedSell).Should().BeTrue();

        _inflationRepository
            .GetIndexValuesAsync(BuyDate, SellDate, Arg.Any<CancellationToken>())
            .Returns((100m, buyMonth, 140m, laggedMonth));

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 10_000m, "try", includeInflation: true);
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.InflationDataAsOf.Should().Be(laggedMonth);
        result.RealProfitLossPercent.Should().NotBeNull();
    }

    [Fact]
    public async Task CalculateAsync_InflationDataUnavailable_NullRealReturn()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 10_000m, "try", includeInflation: true);
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.RealProfitLossPercent.Should().BeNull();
        result.CumulativeInflationPercent.Should().BeNull();
        result.InflationDataAsOf.Should().BeNull();
    }

    [Fact]
    public async Task CalculateAsync_InflationNotRequested_DoesNotCallRepository()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 10_000m, "try", includeInflation: false);
        await _sut.CalculateAsync(request, CancellationToken.None);

        await _inflationRepository.DidNotReceive()
            .GetIndexValuesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────────
    // CalculateReverseAsync
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CalculateReverseAsync_TargetTypeTry_ComputesCorrectResult()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var request = MakeReverseRequest("USDTRY", BuyDate, SellDate, 100_000m, "try");
        var result  = await _sut.CalculateReverseAsync(request, CancellationToken.None);

        result.AssetSymbol.Should().Be("USDTRY");
        result.BuyPrice.Should().Be(5.95m);
        result.SellPrice.Should().Be(8.50m);

        var expectedUnits = Math.Round(100_000m / 8.50m, 6, MidpointRounding.AwayFromZero);
        result.UnitsAcquired.Should().Be(expectedUnits);

        // F4-3: targetValueTry forward-consistent türetilir (bu fiyatlarda 100.000,00'a yuvarlanır).
        var expectedTarget = Math.Round(expectedUnits * 8.50m, 2, MidpointRounding.AwayFromZero);
        result.TargetValueTry.Should().Be(expectedTarget);

        var expectedInvestment = Math.Round(expectedUnits * 5.95m, 2, MidpointRounding.AwayFromZero);
        result.RequiredInvestmentTry.Should().Be(expectedInvestment);

        result.ProfitLossTry.Should().Be(expectedTarget - expectedInvestment);
        result.IsProfit.Should().BeTrue();

        // F4-3 forward-consistency kilidi: UnitsAcquired × SellPrice == TargetValueTry.
        Math.Round(result.UnitsAcquired * result.SellPrice, 2, MidpointRounding.AwayFromZero)
            .Should().Be(result.TargetValueTry);
    }

    [Fact]
    public async Task CalculateReverseAsync_TargetTypeTry_TargetValueIsForwardConsistent_NotRawEcho()
    {
        // sellPrice büyük + bölünme tam çıkmıyor → birim yuvarlaması (6 hane) ham hedeften
        // ölçülebilir (kuruş üstü) bir sapma yaratır: 100.000 / 30.000 = 3,333333… birim,
        // 3,333333 × 30.000 = 99.999,99. Forward-consistency targetValueTry'yi 99.999,99
        // yapar (ham 100.000 echo'su DEĞİL); böylece UI'da UnitsAcquired × SellPrice uyuşur.
        SetupPrices(buyPrice: 10_000m, sellPrice: 30_000m);

        var request = MakeReverseRequest("USDTRY", BuyDate, SellDate, 100_000m, "try");
        var result  = await _sut.CalculateReverseAsync(request, CancellationToken.None);

        result.UnitsAcquired.Should().Be(3.333333m);
        result.TargetValueTry.Should().Be(99_999.99m);
        result.TargetValueTry.Should().NotBe(100_000m); // ham echo olmadığını kanıtlar
        Math.Round(result.UnitsAcquired * result.SellPrice, 2, MidpointRounding.AwayFromZero)
            .Should().Be(result.TargetValueTry);
    }

    [Fact]
    public async Task CalculateReverseAsync_InvalidTargetAmountType_ThrowsValidationException()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);

        var request = MakeReverseRequest("USDTRY", BuyDate, SellDate, 1000m, "eur");

        var act = () => _sut.CalculateReverseAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CalculateReverseAsync_SellPriceZero_ThrowsPriceNotFoundException()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 0m);

        var request = MakeReverseRequest("USDTRY", BuyDate, SellDate, 1000m, "try");

        var act = () => _sut.CalculateReverseAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<PriceNotFoundException>();
    }

    [Fact]
    public async Task CalculateReverseAsync_BuyDateAfterSellDate_ThrowsValidationException()
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);
        var request = MakeReverseRequest("USDTRY", SellDate, BuyDate, 1000m, "try");

        var act = () => _sut.CalculateReverseAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    // ── Feature Flags ───────────────────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_InflationRequestedButFeatureDisabled_ThrowsFeatureDisabled()
    {
        var planOptions = new PlanOptions
        {
            Free = new TierOptions { Features = new FeatureOptions { InflationAdjustment = false } }
        };
        var sut = CreateSutWithOptions(planOptions);

        var request = MakeRequest("USDTRY", BuyDate, SellDate, 1000m, "try", includeInflation: true);

        var act = () => sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<FeatureDisabledException>();
    }

    [Fact]
    public async Task CalculateReverseAsync_InflationRequestedButFeatureDisabled_ThrowsFeatureDisabled()
    {
        var planOptions = new PlanOptions
        {
            Free = new TierOptions { Features = new FeatureOptions { InflationAdjustment = false } }
        };
        var sut = CreateSutWithOptions(planOptions);

        var request = MakeReverseRequest("USDTRY", BuyDate, SellDate, 1000m, "try", includeInflation: true);

        var act = () => sut.CalculateReverseAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<FeatureDisabledException>();
    }

    [Fact]
    public async Task CompareAsync_ComparisonFeatureDisabled_ThrowsFeatureDisabled()
    {
        var planOptions = new PlanOptions
        {
            Free = new TierOptions { Features = new FeatureOptions { Comparison = false } }
        };
        var sut = CreateSutWithOptions(planOptions);

        var request = new CompareRequest(["USDTRY", "BTC"], BuyDate, SellDate, 1000m, "try");

        var act = () => sut.CompareAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<FeatureDisabledException>();
    }

    [Fact]
    public async Task CompareAsync_InflationTierDisabled_ThrowsFeatureDisabled()
    {
        // Tier inflation kapalı + CompareRequest IncludeInflation=true →
        // CalculateAsync / CalculateReverseAsync ile TUTARLI: flag'i sessizce drop etmek
        // yerine FeatureDisabled (4xx) fırlatılır (üç entry-point aynı davranır).
        var planOptions = new PlanOptions
        {
            Free = new TierOptions { Features = new FeatureOptions {
                Comparison = true, InflationAdjustment = false,
                // F2.2-21 history sınırı bu testin niyeti dışında: history'yi sınırsız tut.
                PriceHistoryMonths = 0,
            } }
        };
        var sut = CreateSutWithOptions(planOptions);

        var request = new CompareRequest(["USDTRY", "BTC"], BuyDate, SellDate, 1000m, "try",
            IncludeInflation: true);

        var act = () => sut.CompareAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<FeatureDisabledException>();
    }

    // ── Amount Validation (F1.9-4) ─────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public async Task CalculateAsync_NonPositiveAmount_ThrowsValidationException(decimal amount)
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);
        var request = MakeRequest("USDTRY", BuyDate, SellDate, amount, "try");

        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
                 .Where(ex => ex.Field == nameof(request.Amount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public async Task CalculateReverseAsync_NonPositiveTargetAmount_ThrowsValidationException(decimal target)
    {
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);
        var request = MakeReverseRequest("USDTRY", BuyDate, SellDate, target, "try");

        var act = () => _sut.CalculateReverseAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
                 .Where(ex => ex.Field == nameof(request.TargetAmount));
    }

    [Fact]
    public async Task CompareAsync_NonPositiveAmount_ThrowsValidationException()
    {
        var request = new CompareRequest(["USDTRY", "BTC"], BuyDate, SellDate, -100m, "try");

        var act = () => _sut.CompareAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
                 .Where(ex => ex.Field == nameof(request.Amount));
    }

    // ── CompareAsync Happy Path (F1.9-5) ───────────────────────────────────

    [Fact]
    public async Task CompareAsync_TwoSymbols_RanksByProfitDescending()
    {
        // Arrange: USDTRY %43, BTC %200. Ranking: BTC (1), USDTRY (2).
        var btc = new Asset
        {
            Id          = Guid.Parse("dddddddd-0000-0000-0000-000000000001"),
            Symbol      = "BTC",
            DisplayName = "Bitcoin",
            Category    = AssetCategory.Crypto,
            Source      = "coingecko",
            IsActive    = true
        };
        _assetService.GetBySymbolAsync("BTC", Arg.Any<CancellationToken>()).Returns(btc);

        // USDTRY 5.95 → 8.50 (~%43 kâr)
        _assetService.GetNearestPriceAsync("USDTRY", BuyDate, Arg.Any<CancellationToken>())
                     .Returns(new PricePoint { AssetId = AssetId, PriceDate = BuyDate, Close = 5.95m });
        _assetService.GetNearestPriceAsync("USDTRY", SellDate, Arg.Any<CancellationToken>())
                     .Returns(new PricePoint { AssetId = AssetId, PriceDate = SellDate, Close = 8.50m });
        // BTC 10000 → 30000 (%200 kâr)
        _assetService.GetNearestPriceAsync("BTC", BuyDate, Arg.Any<CancellationToken>())
                     .Returns(new PricePoint { AssetId = btc.Id, PriceDate = BuyDate, Close = 10_000m });
        _assetService.GetNearestPriceAsync("BTC", SellDate, Arg.Any<CancellationToken>())
                     .Returns(new PricePoint { AssetId = btc.Id, PriceDate = SellDate, Close = 30_000m });
        _assetService.GetLatestPriceDateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(SellDate);

        var request = new CompareRequest(["USDTRY", "BTC"], BuyDate, SellDate, 1000m, "try");

        // Act
        var result = await _sut.CompareAsync(request, CancellationToken.None);

        // Assert
        result.Results.Should().HaveCount(2);
        result.Results.Select(r => r.Calculation.AssetSymbol).Should()
              .ContainInOrder(new[] { "BTC", "USDTRY" },
                  "yüksek getirili sembol Rank=1 olmalı");
        result.Results[0].Rank.Should().Be(1);
        result.Results[0].Calculation.IsProfit.Should().BeTrue();
        result.Results[0].Calculation.ProfitLossPercent.Should().BeGreaterThan(
            result.Results[1].Calculation.ProfitLossPercent);
        result.Results[1].Rank.Should().Be(2);
    }

    [Fact]
    public async Task CompareAsync_ComputesSymbolsSequentially_NoConcurrentRepositoryAccess()
    {
        // CONC-001 guard: Önceki sürüm Task.WhenAll ile paralel hesaplıyordu; tüm task'lar aynı
        // scoped SaydinDbContext'i paylaştığı için soğuk cache'te eşzamanlı EF sorguları
        // "second operation on this context" (500) veriyordu. Mock'lar thread-safe olduğundan
        // sonuç testleri bunu yakalamaz; bu test inflation repo'ya (cache'siz, her sembolde
        // çağrılır) re-entrancy dedektörü koyarak hesabın ARDIŞIK yapıldığını (overlap yok) doğrular.
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);
        _assetService.GetBySymbolAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(UsdTry);

        var inFlight = 0;
        var overlapped = false;
        _inflationRepository
            .GetIndexValuesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                if (Interlocked.Increment(ref inFlight) > 1) overlapped = true;
                await Task.Delay(25);
                Interlocked.Decrement(ref inFlight);
                return ((decimal?)null, (DateOnly?)null, (decimal?)null, (DateOnly?)null);
            });

        var request = new CompareRequest(
            ["USDTRY", "EURTRY", "GBPTRY"], BuyDate, SellDate, 1000m, "try", IncludeInflation: true);

        await _sut.CompareAsync(request, CancellationToken.None);

        overlapped.Should().BeFalse(
            "CompareAsync sembolleri ardışık hesaplamalı — paralel hesap paylaşılan DbContext'i çökertir (CONC-001)");
    }

    // P1R-014: Multi-symbol ranking — 3, 4, 5 sembol için Rank pozisyonları doğru;
    // aynı `ProfitLossPercent`'e sahip semboller arasında OrderByDescending stable
    // davranır (LINQ Enumerable.OrderByDescending kararlı bir sıralama uygular).
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task CompareAsync_MultiSymbol_RanksAllPositionsByProfitDescending(int symbolCount)
    {
        // Her sembol için farklı sell price → farklı ProfitLossPercent.
        // Buy price 10, sell price 10+i*5 → kâr i*50% (i=1..5).
        // Sembol-i'nin daha yüksek olduğu için Rank=1 i=N, Rank=N i=1.
        var symbols = Enumerable.Range(1, symbolCount).Select(i => $"SYM{i}").ToList();

        for (var i = 0; i < symbolCount; i++)
        {
            var sym  = symbols[i];
            var sell = 10m + (i + 1) * 5m;
            var asset = new Asset
            {
                Id          = Guid.Parse($"eeeeeeee-0000-0000-0000-{i + 1:D12}"),
                Symbol      = sym,
                DisplayName = sym,
                Category    = AssetCategory.Currency,
                Source      = "tcmb",
                IsActive    = true
            };
            _assetService.GetBySymbolAsync(sym, Arg.Any<CancellationToken>()).Returns(asset);
            _assetService.GetNearestPriceAsync(sym, BuyDate, Arg.Any<CancellationToken>())
                         .Returns(new PricePoint { AssetId = asset.Id, PriceDate = BuyDate,  Close = 10m  });
            _assetService.GetNearestPriceAsync(sym, SellDate, Arg.Any<CancellationToken>())
                         .Returns(new PricePoint { AssetId = asset.Id, PriceDate = SellDate, Close = sell });
        }
        _assetService.GetLatestPriceDateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(SellDate);

        var request = new CompareRequest(symbols, BuyDate, SellDate, 1000m, "try");

        var result = await _sut.CompareAsync(request, CancellationToken.None);

        result.Results.Should().HaveCount(symbolCount);

        // Tüm Rank'lar 1..N arasında benzersiz ve azalan ProfitLossPercent sırasında.
        result.Results.Select(r => r.Rank).Should().Equal(Enumerable.Range(1, symbolCount));
        result.Results.Should().BeInDescendingOrder(r => r.Calculation.ProfitLossPercent);

        // En tepedeki sembol en yüksek sell price'a sahip olan olmalı.
        result.Results[0].Calculation.AssetSymbol.Should().Be(symbols[symbolCount - 1]);
        result.Results[^1].Calculation.AssetSymbol.Should().Be(symbols[0]);
    }

    // P1R-014: Aynı `ProfitLossPercent`'e sahip semboller — Rank yine benzersiz 1..N
    // olarak atanır; LINQ stable sort sayesinde girdi sırası tiebreak'i belirler.
    [Fact]
    public async Task CompareAsync_DuplicateProfitPercent_AssignsUniqueRanksDeterministically()
    {
        var assetA = new Asset
        {
            Id = Guid.Parse("ffffffff-0000-0000-0000-000000000001"),
            Symbol = "AAA", DisplayName = "A", Category = AssetCategory.Currency,
            Source = "tcmb", IsActive = true
        };
        var assetB = new Asset
        {
            Id = Guid.Parse("ffffffff-0000-0000-0000-000000000002"),
            Symbol = "BBB", DisplayName = "B", Category = AssetCategory.Currency,
            Source = "tcmb", IsActive = true
        };
        _assetService.GetBySymbolAsync("AAA", Arg.Any<CancellationToken>()).Returns(assetA);
        _assetService.GetBySymbolAsync("BBB", Arg.Any<CancellationToken>()).Returns(assetB);

        foreach (var (sym, id) in new[] { ("AAA", assetA.Id), ("BBB", assetB.Id) })
        {
            _assetService.GetNearestPriceAsync(sym, BuyDate, Arg.Any<CancellationToken>())
                         .Returns(new PricePoint { AssetId = id, PriceDate = BuyDate,  Close = 10m });
            _assetService.GetNearestPriceAsync(sym, SellDate, Arg.Any<CancellationToken>())
                         .Returns(new PricePoint { AssetId = id, PriceDate = SellDate, Close = 15m });
        }
        _assetService.GetLatestPriceDateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(SellDate);

        var request = new CompareRequest(["AAA", "BBB"], BuyDate, SellDate, 1000m, "try");
        var result  = await _sut.CompareAsync(request, CancellationToken.None);

        result.Results.Select(r => r.Rank).Should().Equal(1, 2);
        // Eşit ProfitLossPercent → her iki sembol de aynı yüzdeye sahip olmalı.
        result.Results[0].Calculation.ProfitLossPercent
              .Should().Be(result.Results[1].Calculation.ProfitLossPercent);
    }

    // ── CompareAsync Distinct Validation ───────────────────────────────────

    [Fact]
    public async Task CompareAsync_LessThanTwoUniqueSymbols_ThrowsValidationException()
    {
        var request = new CompareRequest(["USDTRY", "USDTRY"], BuyDate, SellDate, 1000m, "try");

        var act = () => _sut.CompareAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CompareAsync_MoreThanFiveUniqueSymbols_ThrowsValidationException()
    {
        var request = new CompareRequest(
            ["USDTRY", "EURTRY", "BTC", "ETH", "XAU_TRY_GRAM", "THYAO"],
            BuyDate, SellDate, 1000m, "try");

        var act = () => _sut.CompareAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CompareAsync_NullAssetSymbols_ThrowsValidationException()
    {
        var request = new CompareRequest(null!, BuyDate, SellDate, 1000m, "try");

        var act = () => _sut.CompareAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CompareAsync_DuplicateSymbolsWithDifferentCase_TreatedAsSingleSymbol()
    {
        var request = new CompareRequest(
            ["usdtry", "USDTRY", "Usdtry"], BuyDate, SellDate, 1000m, "try");

        var act = () => _sut.CompareAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    // ── PriceHistoryMonths penceresi (C-Low-2: kontrollü saat / TimeProvider) ────────

    [Fact]
    public async Task CalculateAsync_BuyDateBeforeHistoryWindow_ThrowsFeatureDisabled()
    {
        // TimeProvider'ın deterministik kıldığı davranış: clock=2026-05-29, Free penceresi=6 ay
        // → en erken izinli ~2025-11-29. BuyDate 2025-01-01 pencereden eski → extended_history.
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 5, 29, 0, 0, 0, TimeSpan.Zero));
        var sut = CreateSutWithOptions(new PlanOptions
        {
            Free = new TierOptions { Features = new FeatureOptions { PriceHistoryMonths = 6 } },
        });

        var request = MakeRequest("USDTRY", new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1), 1000m, "try");

        var act = () => sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<FeatureDisabledException>()
                 .Where(ex => ex.FeatureKey == "extended_history");
    }

    [Fact]
    public async Task CalculateAsync_BuyDateWithinHistoryWindow_DoesNotThrowFeatureDisabled()
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 5, 29, 0, 0, 0, TimeSpan.Zero));
        SetupPrices(buyPrice: 5.95m, sellPrice: 8.50m);
        var sut = CreateSutWithOptions(new PlanOptions
        {
            Free = new TierOptions { Features = new FeatureOptions { PriceHistoryMonths = 6 } },
        });

        // BuyDate pencere içinde (2026-03-01 > ~2025-11-29) → window guard tetiklenmez.
        var request = MakeRequest("USDTRY", new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1), 1000m, "try");

        var act = () => sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().NotThrowAsync<FeatureDisabledException>();
    }

    // ── Yardımcı Metodlar ────────────────────────────────────────────────────

    private WhatIfCalculator CreateSutWithOptions(PlanOptions planOptions)
    {
        return new WhatIfCalculator(
            _assetService, _scenarioRepository, _inflationRepository,
            _dailyLimitGuard, _deviceContext, _timeProvider, _cache, _assetNameLocalizer,
            Microsoft.Extensions.Options.Options.Create(planOptions),
            _localizer, NullLogger<WhatIfCalculator>.Instance);
    }

    private static WhatIfRequest MakeRequest(
        string symbol, DateOnly buyDate, DateOnly? sellDate,
        decimal amount, string amountType, bool includeInflation = false)
        => new(symbol, buyDate, sellDate, amount, amountType, includeInflation);

    private static ReverseWhatIfRequest MakeReverseRequest(
        string symbol, DateOnly buyDate, DateOnly? sellDate,
        decimal targetAmount, string targetAmountType, bool includeInflation = false)
        => new(symbol, buyDate, sellDate, targetAmount, targetAmountType, includeInflation);
}
