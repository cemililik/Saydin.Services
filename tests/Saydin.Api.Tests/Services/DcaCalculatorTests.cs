using FluentAssertions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Saydin.Api.Helpers;
using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;
using Saydin.Api.Options;
using Saydin.Api.Repositories;
using Saydin.Api.Services;
using Saydin.Api.Tests.Helpers;
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
    private readonly IInstallationPrincipalContext   _principalContext =
        Substitute.For<IInstallationPrincipalContext>();
    private readonly FakeTimeProvider                _timeProvider        = new();
    private readonly DcaCalculator                   _sut;

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
        DeviceId  = null,
        Tier      = "free",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static readonly User PremiumUser = new()
    {
        Id        = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"),
        DeviceId  = null,
        Tier      = "premium",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static readonly DateOnly StartDate = new(2023, 1, 1);
    private static readonly DateOnly EndDate   = new(2023, 6, 1);
    private static readonly AssetCatalogVersion Catalog = new()
    {
        Revision = 7,
        CatalogSha256 = Enumerable.Repeat((byte)0x5a, 32).ToArray()
    };
    private static readonly string CatalogHash =
        Convert.ToHexString(Catalog.CatalogSha256).ToLowerInvariant();

    public DcaCalculatorTests()
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 19, 22, 0, 0, TimeSpan.Zero));
        _dailyLimitGuard.TryAcquireAsync(
                Arg.Any<User?>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(QuotaLease.Noop);
        _cache.TryGetAsync<DcaCacheEntry>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns((DcaCacheEntry?)null);

        _scenarioRepository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>())
                           .Returns(FreeUser);
        _scenarioRepository.GetUserByIdAsync(PremiumUser.Id, Arg.Any<CancellationToken>())
                           .Returns(PremiumUser);

        _assetService.GetBySymbolAsync("USDTRY", Arg.Any<CancellationToken>()).Returns(UsdTry);
        _assetService.GetCatalogVersionAsync(Arg.Any<CancellationToken>()).Returns(Catalog);
        // Existing per-date arrangements remain expressive, while the SUT itself
        // is required to cross the asset-service boundary exactly once.
        _assetService.GetNearestPricesAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<DateOnly>>(), Arg.Any<CancellationToken>())
            .Returns(ci => LoadNearestPricesFromSingleStubAsync(
                (string)ci[0],
                (IReadOnlyList<DateOnly>)ci[1],
                (CancellationToken)ci[2]));

        _inflationRepository
            .GetIndexValuesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(((InflationIndexObservation?)null, (InflationIndexObservation?)null));
        _inflationRepository
            .GetExactIndexValuesAsync(
                Arg.Any<IReadOnlyCollection<DateOnly>>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<DateOnly, InflationIndexObservation>());

        _localizer[Arg.Any<string>()]
            .Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));
        _localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));

        _assetNameLocalizer.Localize(Arg.Any<string>(), Arg.Any<string?>())
                           .Returns(ci => (string?)ci[1] ?? (string)ci[0]);

        _principalContext.PrincipalId.Returns(FreeUser.Id);

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
            _dailyLimitGuard, _principalContext, _timeProvider, _cache, _assetNameLocalizer,
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
        await _assetService.Received(1).GetNearestPricesAsync(
            "USDTRY",
            Arg.Is<IReadOnlyList<DateOnly>>(dates =>
                dates.Count == 7 && dates[dates.Count - 1] == EndDate),
            Arg.Any<CancellationToken>());
        result.CurrentValueTry.Should().Be(6000m);
        result.ProfitLossTry.Should().Be(0m);
        result.IsProfit.Should().BeTrue();
    }

    [Fact]
    public async Task CalculateAsync_HighUnitPriceAtBreakeven_UsesRequestedCapitalForMath()
    {
        SetupConstantPrice(3_000_000m);

        var result = await _sut.CalculateAsync(
            MakeRequest("USDTRY", StartDate, StartDate, 100m, "monthly"),
            CancellationToken.None);

        result.TotalUnitsAcquired.Should().Be(0.000033m);
        result.TotalInvestedTry.Should().Be(100m);
        result.CurrentValueTry.Should().Be(100m);
        result.ProfitLossTry.Should().Be(0m);
        result.ProfitLossPercent.Should().Be(0m);
    }

    [Fact]
    public async Task CalculateAsync_OneMissingPurchasePrice_ReturnsTransparentPartialResult()
    {
        var start = new DateOnly(2023, 1, 1);
        var end = new DateOnly(2023, 3, 1);
        _assetService.GetNearestPricesAsync(
                "USDTRY", Arg.Any<IReadOnlyList<DateOnly>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var dates = (IReadOnlyList<DateOnly>)call[1];
                dates[2].Should().Be(dates[3],
                    "the terminal valuation keeps a distinct duplicate ordinal");
                return (IReadOnlyList<PricePoint?>)
                [
                    AuthorityTestData.FinalPrice(dates[0], 10m, AssetId),
                    null,
                    AuthorityTestData.FinalPrice(dates[2], 10m, AssetId),
                    AuthorityTestData.FinalPrice(dates[3], 10m, AssetId),
                ];
            });

        var result = await _sut.CalculateAsync(
            MakeRequest("USDTRY", start, end, 100m, "monthly"),
            CancellationToken.None);

        result.TotalPurchases.Should().Be(2);
        result.TotalInvestedTry.Should().Be(200m);
        result.SkippedPurchaseDates.Should().Equal(new DateOnly(2023, 2, 1));
        result.Data!.DataStatus.Should().Be(AuthorityDataStatuses.Degraded);
        result.Data.Warnings.Should().Equal(AuthorityDataWarnings.PurchasePriceUnavailable);
        await _cache.DidNotReceive().TrySetAsync(
            Arg.Any<string>(), Arg.Any<DcaCacheEntry>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CalculateAsync_AllPurchasePricesMissingButTerminalExists_ThrowsAndDoesNotCache()
    {
        var start = new DateOnly(2023, 1, 1);
        var end = new DateOnly(2023, 3, 15);
        _assetService.GetNearestPricesAsync(
                "USDTRY", Arg.Any<IReadOnlyList<DateOnly>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var dates = (IReadOnlyList<DateOnly>)call[1];
                return (IReadOnlyList<PricePoint?>)
                [
                    null,
                    null,
                    null,
                    AuthorityTestData.FinalPrice(dates[^1], 10m, AssetId),
                ];
            });

        var act = () => _sut.CalculateAsync(
            MakeRequest("USDTRY", start, end, 100m, "monthly"),
            CancellationToken.None);

        await act.Should().ThrowAsync<PriceNotFoundException>();
        await _cache.DidNotReceive().TrySetAsync(
            Arg.Any<string>(), Arg.Any<DcaCacheEntry>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CalculateAsync_PurchaseResolvedAfterTerminalValuation_IsSkipped()
    {
        var start = new DateOnly(2023, 1, 1);
        var end = new DateOnly(2023, 2, 1);
        _assetService.GetNearestPricesAsync(
                "USDTRY", Arg.Any<IReadOnlyList<DateOnly>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<PricePoint?>)
            [
                AuthorityTestData.FinalPrice(start, 10m, AssetId),
                AuthorityTestData.FinalPrice(end.AddDays(1), 10m, AssetId),
                AuthorityTestData.FinalPrice(end.AddDays(-1), 10m, AssetId),
            ]);

        var result = await _sut.CalculateAsync(
            MakeRequest("USDTRY", start, end, 100m, "monthly"),
            CancellationToken.None);

        result.TotalPurchases.Should().Be(1);
        result.SkippedPurchaseDates.Should().Equal(end);
        result.Data!.Warnings.Should().Equal(AuthorityDataWarnings.PurchasePriceUnavailable);
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

    // Installation credential ve principal çözümlemesi endpoint filter'ında yapılır;
    // servis yalnızca doğrulanmış IInstallationPrincipalContext kimliğini kullanır.

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
                         return AuthorityTestData.FinalPrice(date, 10m, AssetId);
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
                return AuthorityTestData.FinalPrice((DateOnly)ci[1], price, AssetId);
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
        _principalContext.PrincipalId.Returns(PremiumUser.Id);
        await _sut.CalculateAsync(request, CancellationToken.None);

        await _dailyLimitGuard.Received(1)
            .TryAcquireAsync(PremiumUser, PremiumUser.Id.ToString("N"), Arg.Any<string>(), null, Arg.Any<CancellationToken>());
        await _dailyLimitGuard.DidNotReceive()
            .ReleaseAsync(Arg.Any<QuotaLease>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CalculateAsync_FreeUserAtLimit_TryAcquireThrows()
    {
        SetupConstantPrice(10m);

        _dailyLimitGuard.TryAcquireAsync(FreeUser, FreeUser.Id.ToString("N"), Arg.Any<string>(), null, Arg.Any<CancellationToken>())
            .ThrowsAsync(new DailyLimitExceededException(20));

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");

        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<DailyLimitExceededException>();
        // Quota rejection sonrası release çağırılmamalı (acquire fail oldu, geri verilecek bir şey yok)
        await _dailyLimitGuard.DidNotReceive()
            .ReleaseAsync(Arg.Any<QuotaLease>(), Arg.Any<CancellationToken>());
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
            .ReleaseAsync(Arg.Any<QuotaLease>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CalculateAsync_QuotaLeaseAcquired_Calculates()
    {
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");
        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── Enflasyon ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CalculateAsync_ThreeCashFlows_UsesEffectiveAndTerminalMonthsForRealReturn()
    {
        // Planlanan tarihler 31 Oca / 28 Şub / 31 Mar; gerçekleşen piyasa günleri
        // 1 Şub / 1 Mar / 1 Nis. Terminal fiyat da 1 Nis. CPI ayları bu effective
        // tarihlerden türetilmeli; request aylarından değil.
        var start = new DateOnly(2023, 1, 31);
        var end = new DateOnly(2023, 3, 31);
        var effectiveDates = new[]
        {
            new DateOnly(2023, 2, 1),
            new DateOnly(2023, 3, 1),
            new DateOnly(2023, 4, 1),
            new DateOnly(2023, 4, 1), // terminal price lookup
        };
        var callCount = 0;
        _assetService.GetNearestPriceAsync(
                Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(_ => AuthorityTestData.FinalPrice(
                effectiveDates[callCount++], 10m, AssetId));

        var february = new DateOnly(2023, 2, 1);
        var march = new DateOnly(2023, 3, 1);
        var april = new DateOnly(2023, 4, 1);
        var indexes = AuthorityTestData.FinalCpi(new Dictionary<DateOnly, decimal>
        {
            [february] = 100m,
            [march] = 110m,
            [april] = 121m,
        });
        _inflationRepository
            .GetExactIndexValuesAsync(
                Arg.Any<IReadOnlyCollection<DateOnly>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var months = (IReadOnlyCollection<DateOnly>)callInfo[0];
                months.Should().BeEquivalentTo([february, march]);
                return indexes;
            });
        _inflationRepository
            .GetLatestFinalIndexValueAsync(april, Arg.Any<CancellationToken>())
            .Returns(indexes[april]);

        var request = MakeRequest("USDTRY", start, end, 100m, "monthly", includeInflation: true);
        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        // Terminal-adjusted cost = 100×121/100 + 100×121/110 + 100×121/121 = 331.
        // Constant asset price gives terminal portfolio 300; real P/L = -31;
        // real ROI = (300/331 - 1)×100 = -9.3655... → -9.37.
        result.TotalInvestedTry.Should().Be(300m);
        result.CurrentValueTry.Should().Be(300m);
        result.InflationAdjustedInvestedTry.Should().Be(331m);
        result.RealProfitLossTry.Should().Be(-31m);
        result.RealProfitLossPercent.Should().Be(-9.37m);
        result.CumulativeInflationPercent.Should().Be(21m);
        result.RealReturnMethod.Should().Be("cashflow_cpi_lkv_terminal_v1");
        result.InflationTerminalMonth.Should().Be(april);
        result.InflationDataAsOf.Should().BeNull();
    }

    [Fact]
    public async Task CalculateAsync_CurrentTerminalMonthUsesLatestFinalCpi_AndReturnsUsedMonth()
    {
        var start = new DateOnly(2026, 6, 15);
        var terminalDate = new DateOnly(2026, 8, 15);
        var june = new DateOnly(2026, 6, 1);
        var july = new DateOnly(2026, 7, 1);
        var august = new DateOnly(2026, 8, 1);
        SetupConstantPrice(10m);
        _assetService.GetLatestPriceDateAsync("USDTRY", Arg.Any<CancellationToken>())
            .Returns(terminalDate);
        _inflationRepository.GetExactIndexValuesAsync(
                Arg.Any<IReadOnlyCollection<DateOnly>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                ((IReadOnlyCollection<DateOnly>)call[0]).Should().BeEquivalentTo([june]);
                return AuthorityTestData.FinalCpi(new Dictionary<DateOnly, decimal>
                {
                    [june] = 100m,
                    [july] = 110m,
                });
            });
        _inflationRepository.GetLatestFinalIndexValueAsync(
                august, Arg.Any<CancellationToken>())
            .Returns(AuthorityTestData.FinalCpi(july, 110m));

        var result = await _sut.CalculateAsync(
            MakeRequest("USDTRY", start, null, 100m, "monthly", includeInflation: true),
            CancellationToken.None);

        result.EndDate.Should().Be(terminalDate);
        result.InflationAdjustedInvestedTry.Should().Be(310m);
        result.RealProfitLossTry.Should().Be(-10m);
        result.RealProfitLossPercent.Should().Be(-3.23m);
        result.RealReturnMethod.Should().Be("cashflow_cpi_lkv_terminal_v1");
        result.InflationTerminalMonth.Should().Be(july);
        result.InflationDataAsOf.Should().Be(july);
        result.Data!.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task CalculateAsync_ExplicitFutureEndDate_IsRejectedBeforeQuota()
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(
            2026, 8, 19, 22, 0, 0, TimeSpan.Zero));
        BusinessClock.TodayInIstanbul(_timeProvider).Should().Be(new DateOnly(2026, 8, 20));
        var request = MakeRequest(
            "USDTRY", StartDate, new DateOnly(2026, 8, 21), 100m, "monthly");

        var act = () => _sut.CalculateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
            .Where(error => error.Field == nameof(request.EndDate));
        await _dailyLimitGuard.DidNotReceiveWithAnyArgs().TryAcquireAsync(
            default, default!, default!, default, default);
    }

    [Fact]
    public async Task CalculateAsync_SingleContribution_MatchesFisherParity()
    {
        var start = new DateOnly(2023, 1, 31);
        var end = new DateOnly(2023, 2, 1);
        var callCount = 0;
        _assetService.GetNearestPriceAsync(
                Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(_ => AuthorityTestData.FinalPrice(
                callCount++ == 0 ? start : end, 10m, AssetId));
        _inflationRepository
            .GetExactIndexValuesAsync(
                Arg.Any<IReadOnlyCollection<DateOnly>>(),
                Arg.Any<CancellationToken>())
            .Returns(AuthorityTestData.FinalCpi(new Dictionary<DateOnly, decimal>
            {
                [new DateOnly(2023, 1, 1)] = 100m,
                [new DateOnly(2023, 2, 1)] = 120m,
            }));
        _inflationRepository
            .GetLatestFinalIndexValueAsync(
                new DateOnly(2023, 2, 1), Arg.Any<CancellationToken>())
            .Returns(AuthorityTestData.FinalCpi(new DateOnly(2023, 2, 1), 120m));

        var result = await _sut.CalculateAsync(
            MakeRequest("USDTRY", start, end, 100m, "monthly", includeInflation: true),
            CancellationToken.None);

        // Tek nakit akışında cash-flow yöntemi Fisher ile birebir:
        // nominal=0%, inflation=20% => (1/1.2 - 1)×100 = -16.67%.
        result.TotalPurchases.Should().Be(1);
        result.InflationAdjustedInvestedTry.Should().Be(120m);
        result.RealProfitLossTry.Should().Be(-20m);
        result.RealProfitLossPercent.Should().Be(-16.67m);
    }

    [Fact]
    public async Task CalculateAsync_RealReturn_RoundsOnlyAtResponseBoundary()
    {
        var date = new DateOnly(2023, 1, 1);
        var callCount = 0;
        _assetService.GetNearestPriceAsync(
                Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(_ => AuthorityTestData.FinalPrice(
                date,
                // 1 TRY / 3 = 0.333333 units. Raw terminal value is
                // 0.333333 × 3.01803 = 1.00600899399 TRY.
                callCount++ == 0 ? 3m : 3.01803m,
                AssetId));
        _inflationRepository
            .GetExactIndexValuesAsync(
                Arg.Any<IReadOnlyCollection<DateOnly>>(),
                Arg.Any<CancellationToken>())
            .Returns(AuthorityTestData.FinalCpi(
                new Dictionary<DateOnly, decimal> { [date] = 100m }));
        _inflationRepository
            .GetLatestFinalIndexValueAsync(date, Arg.Any<CancellationToken>())
            .Returns(AuthorityTestData.FinalCpi(date, 100m));

        var result = await _sut.CalculateAsync(
            MakeRequest("USDTRY", date, date, 1m, "monthly", includeInflation: true),
            CancellationToken.None);

        result.CurrentValueTry.Should().Be(1.01m);
        result.InflationAdjustedInvestedTry.Should().Be(1m);
        // Raw terminal value gives 0.600899399%; using rounded CurrentValueTry would
        // incorrectly yield 1.00%.
        result.RealProfitLossPercent.Should().Be(0.60m);
        result.RealProfitLossTry.Should().Be(0.01m);
    }

    [Fact]
    public async Task CalculateAsync_MissingExactInflationMonth_ReturnsNullRealFieldsAndDoesNotCache()
    {
        SetupConstantPrice(10m);
        _inflationRepository
            .GetExactIndexValuesAsync(
                Arg.Any<IReadOnlyCollection<DateOnly>>(),
                Arg.Any<CancellationToken>())
            .Returns(AuthorityTestData.FinalCpi(new Dictionary<DateOnly, decimal>
            {
                [new DateOnly(2023, 1, 1)] = 100m,
                // February is intentionally absent.
                [new DateOnly(2023, 3, 1)] = 120m,
                [new DateOnly(2023, 4, 1)] = 130m,
                [new DateOnly(2023, 5, 1)] = 140m,
                [new DateOnly(2023, 6, 1)] = 150m,
            }));
        _inflationRepository
            .GetLatestFinalIndexValueAsync(
                new DateOnly(2023, 6, 1), Arg.Any<CancellationToken>())
            .Returns(AuthorityTestData.FinalCpi(new DateOnly(2023, 6, 1), 150m));

        var result = await _sut.CalculateAsync(
            MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly", includeInflation: true),
            CancellationToken.None);

        result.CumulativeInflationPercent.Should().BeNull();
        result.InflationAdjustedInvestedTry.Should().BeNull();
        result.RealProfitLossTry.Should().BeNull();
        result.RealProfitLossPercent.Should().BeNull();
        result.RealReturnMethod.Should().Be("cashflow_cpi_lkv_terminal_v1");
        await _cache.DidNotReceive().TrySetAsync(
            Arg.Any<string>(), Arg.Any<DcaCacheEntry>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CalculateAsync_InflationNotRequested_DoesNotCallRepository()
    {
        SetupConstantPrice(10m);

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly", includeInflation: false);
        await _sut.CalculateAsync(request, CancellationToken.None);

        await _inflationRepository.DidNotReceive()
            .GetIndexValuesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await _inflationRepository.DidNotReceive()
            .GetExactIndexValuesAsync(
                Arg.Any<IReadOnlyCollection<DateOnly>>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CalculateAsync_InformationLog_ContainsBucketButNoExactFinancialAmounts()
    {
        SetupIncreasingPrices(10m, 2m);
        var logger = new TestLogger<DcaCalculator>();
        var options = Microsoft.Extensions.Options.Options.Create(new PlanOptions
        {
            Free = new TierOptions { Features = new FeatureOptions { PriceHistoryMonths = 0 } },
            Premium = new TierOptions { Features = new FeatureOptions { PriceHistoryMonths = 0 } },
        });
        var sut = new DcaCalculator(
            _assetService, _scenarioRepository, _inflationRepository,
            _dailyLimitGuard, _principalContext, _timeProvider, _cache, _assetNameLocalizer,
            options, _localizer, logger);
        const decimal sentinelAmount = 8_765_432m;

        var result = await sut.CalculateAsync(
            MakeRequest("USDTRY", StartDate, EndDate, sentinelAmount, "monthly"),
            CancellationToken.None);

        var entry = logger.Entries.Should().ContainSingle(log => log.Level == Microsoft.Extensions.Logging.LogLevel.Information).Subject;
        entry.Properties.Should().Contain("AmountBucket", "1M+");
        entry.Properties.Values
            .OfType<decimal>()
            .Intersect([
                sentinelAmount,
                result.TotalInvestedTry,
                result.CurrentValueTry,
                result.ProfitLossTry,
                result.ProfitLossPercent,
            ])
            .Should().BeEmpty();
        entry.Message.Should().NotContain("8765432");
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
        var cached = CompleteDcaResponse();

        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");
        _cache.TryGetAsync<DcaCacheEntry>(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(DcaCacheEntry.Create(
                  "USDTRY", StartDate, EndDate, 1000m,
                  "monthly", "try", false,
                  System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
                  cached,
                  Catalog));

        var result  = await _sut.CalculateAsync(request, CancellationToken.None);

        result.TotalPurchases.Should().Be(6);
        result.ProfitLossPercent.Should().Be(20m);
        result.CurrentUnitPrice.Should().Be(12m);

        await _assetService.DidNotReceive()
            .GetNearestPriceAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("symbol")]
    [InlineData("start")]
    [InlineData("end")]
    [InlineData("amount")]
    [InlineData("period")]
    [InlineData("type")]
    [InlineData("inflation")]
    [InlineData("language")]
    [InlineData("catalog_hash")]
    public async Task CalculateAsync_CurrentKeyWrongRequestEnvelope_IsCacheMiss(string mutation)
    {
        var request = MakeRequest("USDTRY", StartDate, EndDate, 1000m, "monthly");
        var entry = new DcaCacheEntry(
            "USDTRY", StartDate, EndDate, 1000m, "monthly", "try", false,
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            CompleteDcaResponse(),
            Catalog.Revision,
            CatalogHash);
        entry = mutation switch
        {
            "symbol" => entry with { Symbol = "EURTRY" },
            "start" => entry with { StartDate = StartDate.AddDays(-1) },
            "end" => entry with { EndDate = EndDate.AddDays(1) },
            "amount" => entry with { PeriodicAmount = 999m },
            "period" => entry with { Period = "weekly" },
            "type" => entry with { AmountType = "units" },
            "inflation" => entry with { IncludeInflation = true },
            "language" => entry with { Language = new string('x', 4_096) },
            "catalog_hash" => entry with { CatalogHash = new string('a', 4_096) },
            _ => throw new InvalidOperationException("unknown_test_mutation"),
        };
        _cache.TryGetAsync<DcaCacheEntry>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(entry);
        SetupConstantPrice(10m);

        var result = await _sut.CalculateAsync(request, CancellationToken.None);

        result.AssetSymbol.Should().Be("USDTRY");
        await _assetService.Received().GetNearestPriceAsync(
            "USDTRY", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
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
            _dailyLimitGuard, _principalContext, _timeProvider, _cache, _assetNameLocalizer,
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
            _dailyLimitGuard, _principalContext, _timeProvider, _cache, _assetNameLocalizer,
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
                     .Returns(ci => AuthorityTestData.FinalPrice(
                         (DateOnly)ci[1], price, AssetId));
        _assetService.GetLatestPriceDateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(EndDate);
    }

    private async Task<IReadOnlyList<PricePoint?>> LoadNearestPricesFromSingleStubAsync(
        string symbol,
        IReadOnlyList<DateOnly> dates,
        CancellationToken ct)
    {
        var result = new PricePoint?[dates.Count];
        for (var index = 0; index < dates.Count; index++)
            result[index] = await _assetService.GetNearestPriceAsync(symbol, dates[index], ct);
        return result;
    }

    private void SetupIncreasingPrices(decimal startPrice, decimal step)
    {
        var callCount = 0;
        _assetService.GetNearestPriceAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
                     .Returns(ci =>
                     {
                         var currentPrice = startPrice + step * callCount;
                         callCount++;
                         return AuthorityTestData.FinalPrice(
                             (DateOnly)ci[1], currentPrice, AssetId);
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
                         return AuthorityTestData.FinalPrice(
                             (DateOnly)ci[1], currentPrice, AssetId);
                     });
        _assetService.GetLatestPriceDateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(EndDate);
    }

    private static DcaResponse CompleteDcaResponse() => new(
        AssetSymbol: "USDTRY", AssetDisplayName: "Dolar/TL",
        StartDate: StartDate, EndDate: EndDate,
        Period: "monthly", PeriodicAmount: 1000m,
        TotalPurchases: 6, TotalInvestedTry: 6000m, CurrentValueTry: 7200m,
        ProfitLossTry: 1200m, ProfitLossPercent: 20m, IsProfit: true,
        AverageCostPerUnit: 10m, TotalUnitsAcquired: 600m, CurrentUnitPrice: 12m,
        CumulativeInflationPercent: null, RealProfitLossPercent: null,
        InflationDataAsOf: null, Purchases: [], ChartData: [],
        Data: AuthorityDataResponseFactory.Calculation(
            [new ObservationAuthorityValue(
                ProviderSources.Tcmb,
                ObservationPriceKinds.OfficialReference,
                new DateTimeOffset(StartDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                1)],
            [],
            inflationRequested: false,
            warnings: []));

    private static DcaRequest MakeRequest(
        string symbol, DateOnly startDate, DateOnly? endDate,
        decimal periodicAmount, string period,
        string amountType = "try", bool includeInflation = false)
        => new(symbol, startDate, endDate, periodicAmount, period, amountType, includeInflation);
}
