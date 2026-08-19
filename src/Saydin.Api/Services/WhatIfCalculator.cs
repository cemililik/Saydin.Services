using System.Globalization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Saydin.Api.Helpers;
using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;
using Saydin.Api.Options;
using Saydin.Api.Repositories;

using Saydin.Shared.Constants;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Services;

public sealed class WhatIfCalculator(
    IAssetService assetService,
    ISavedScenarioRepository scenarioRepository,
    IInflationRepository inflationRepository,
    IDailyLimitGuard dailyLimitGuard,
    IInstallationPrincipalContext principalContext,
    TimeProvider timeProvider,
    IRedisCacheHelper cache,
    IAssetNameLocalizer assetNameLocalizer,
    IOptions<PlanOptions> options,
    IStringLocalizer<ErrorMessages> localizer,
    ILogger<WhatIfCalculator> logger) : IWhatIfCalculator
{
    private const string WhatIfUsageKeyPrefix = "usage:whatif:";
    private const int    MaxPriceHistoryPoints = 60;

    // Sonar S1192: `IStringLocalizer["FeatureDisabled"]` 4 yerde tekrarlanıyordu —
    // tek sabite indirgendi. Resx key adı buradan referans alınır; key'i yeniden
    // adlandırmak gerekirse tek yer güncellenir.
    private const string FeatureDisabledKey = "FeatureDisabled";

    public Task<WhatIfResponse> CalculateAsync(WhatIfRequest request, CancellationToken ct) =>
        CalculationTelemetry.ObserveWhatIfAsync(
            "calculate", () => CalculateObservedAsync(request, ct));

    private async Task<WhatIfResponse> CalculateObservedAsync(
        WhatIfRequest request,
        CancellationToken ct)
    {
        EnsureRequest(request);
        var usageIdentity = principalContext.PrincipalId.ToString("N");
        EnsureRequired(request.AssetSymbol, nameof(request.AssetSymbol));
        EnsureRequired(request.AmountType, nameof(request.AmountType));
        EnsurePositive(request.Amount, nameof(request.Amount));

        var user = await GetAuthenticatedUserAsync(ct);
        var features = options.Value.GetTierOptions(user.Tier).Features;

        // F2.2-21 ([G-B-01]): Free plan kullanıcılarına `PriceHistoryMonths` sınırı
        // dayatılır — BuyDate bu pencerenin gerisindeyse 400 döner. 0 = sınırsız.
        EnsureWithinHistoryWindow(request.BuyDate, features.PriceHistoryMonths);

        if (request.IncludeInflation && !features.InflationAdjustment)
            throw new FeatureDisabledException(localizer[FeatureDisabledKey], featureKey: "inflation");

        // Önce atomik reserve — TOCTOU race kapatıldı. Pahalı hesap öncesi limit dayatılır.
        var lease = await dailyLimitGuard.TryAcquireAsync(
            user, usageIdentity, WhatIfUsageKeyPrefix, ct: ct);
        try
        {
            return await CalculateCoreAsync(request, ct);
        }
        catch
        {
            // Hesap başarısız → kotayı iade et ("başarısız hesap kotadan düşmesin").
            // Release fırlatırsa orijinal exception'ı maskelemesin: best-effort + log.
            await TryReleaseAsync(lease, WhatIfUsageKeyPrefix);
            throw;
        }
    }

    public Task<CompareResponse> CompareAsync(CompareRequest request, CancellationToken ct) =>
        CalculationTelemetry.ObserveWhatIfAsync(
            "compare", () => CompareObservedAsync(request, ct));

    private async Task<CompareResponse> CompareObservedAsync(
        CompareRequest request,
        CancellationToken ct)
    {
        EnsureRequest(request);
        var usageIdentity = principalContext.PrincipalId.ToString("N");

        if (request.AssetSymbols is null)
            throw new ValidationException(
                string.Format(localizer["RequestPayloadMissing"], nameof(request.AssetSymbols)),
                field: nameof(request.AssetSymbols));
        EnsureRequired(request.AmountType, nameof(request.AmountType));
        EnsurePositive(request.Amount, nameof(request.Amount));

        var user = await GetAuthenticatedUserAsync(ct);

        var features = options.Value.GetTierOptions(user?.Tier).Features;
        if (!features.Comparison)
            throw new FeatureDisabledException(localizer[FeatureDisabledKey], featureKey: "comparison");

        // Karar: ADR-002-compare-quota.md — Compare = 1 hesaplama (paylaşılan usage:whatif:
        // havuzu, 2-5 sembol fark etmez).
        // inflation tier kuralı CalculateAsync / CalculateReverseAsync ile TUTARLI: özellik
        // kapalıyken IncludeInflation set edilmişse bayrağı sessizce yok saymak yerine
        // FeatureDisabled (4xx) fırlat — üç entry-point'in davranışı aynı olmalı.
        if (request.IncludeInflation && !features.InflationAdjustment)
            throw new FeatureDisabledException(localizer[FeatureDisabledKey], featureKey: "inflation");

        // Tekrarlanan semboller kaldırıldıktan sonra 2-5 arasında unique sembol gerekli
        var symbols = request.AssetSymbols
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (symbols.Count is < 2 or > 5)
            throw new ValidationException(localizer["CompareSymbolCount"], field: nameof(request.AssetSymbols));

        // F2.2-21: Compare için de history sınırı uygulanır.
        EnsureWithinHistoryWindow(request.BuyDate, features.PriceHistoryMonths);

        var lease = await dailyLimitGuard.TryAcquireAsync(
            user, usageIdentity, WhatIfUsageKeyPrefix, ct: ct);

        try
        {
            // CONC-001: Sembol başına hesap SIRAYLA yapılır. Önceki sürüm `Task.WhenAll` ile
            // paralel hesaplıyordu; ancak tüm task'lar aynı scoped `SaydinDbContext`'i paylaşır
            // (inflationRepository cache'siz; soğuk cache'te price okumaları da DbContext'e iner)
            // → eşzamanlı EF sorguları "A second operation was started on this context instance"
            // (InvalidOperationException → 500) verir. Compare ≤5 sembol; sıralı maliyet ihmal
            // edilebilir, doğruluk sözde-paralellikten önemli. Gerçek paralellik istenirse her
            // task için ayrı DbContext scope (IDbContextFactory / IServiceScopeFactory) gerekir.
            // Kota Lua script ile atomik; single Acquire yeterli (ADR-002: compare = 1 işlem).
            var results = new List<WhatIfResponse>(symbols.Count);
            foreach (var symbol in symbols)
            {
                results.Add(await CalculateCoreAsync(new WhatIfRequest(
                    AssetSymbol:       symbol,
                    BuyDate:           request.BuyDate,
                    SellDate:          request.SellDate,
                    Amount:            request.Amount,
                    AmountType:        request.AmountType,
                    IncludeInflation:  request.IncludeInflation), ct));
            }

            // Karlılığa göre sırala (en yüksek ProfitLossPercent → Rank 1)
            var ranked = results
                .OrderByDescending(r => r.ProfitLossPercent)
                .Select((r, i) => new CompareResultItem(Rank: i + 1, Calculation: r))
                .ToList();

            logger.LogInformation(
                "Karşılaştırma hesaplandı: {Symbols} {BuyDate}→{SellDate}",
                string.Join(",", symbols), request.BuyDate, request.SellDate);

            return new CompareResponse(ranked);
        }
        catch
        {
            await TryReleaseAsync(lease, WhatIfUsageKeyPrefix);
            throw;
        }
    }

    public Task<ReverseWhatIfResponse> CalculateReverseAsync(
        ReverseWhatIfRequest request, CancellationToken ct) =>
        CalculationTelemetry.ObserveWhatIfAsync(
            "reverse", () => CalculateReverseObservedAsync(request, ct));

    private async Task<ReverseWhatIfResponse> CalculateReverseObservedAsync(
        ReverseWhatIfRequest request,
        CancellationToken ct)
    {
        EnsureRequest(request);
        var usageIdentity = principalContext.PrincipalId.ToString("N");
        EnsureRequired(request.AssetSymbol, nameof(request.AssetSymbol));
        EnsureRequired(request.TargetAmountType, nameof(request.TargetAmountType));
        EnsurePositive(request.TargetAmount, nameof(request.TargetAmount));

        var user = await GetAuthenticatedUserAsync(ct);
        var features = options.Value.GetTierOptions(user.Tier).Features;

        // F2.2-21: Reverse What-If için de history sınırı uygulanır.
        EnsureWithinHistoryWindow(request.BuyDate, features.PriceHistoryMonths);

        if (request.IncludeInflation && !features.InflationAdjustment)
            throw new FeatureDisabledException(localizer[FeatureDisabledKey], featureKey: "inflation");

        var lease = await dailyLimitGuard.TryAcquireAsync(
            user, usageIdentity, WhatIfUsageKeyPrefix, ct: ct);
        try
        {
            return await CalculateReverseCoreAsync(request, ct);
        }
        catch
        {
            await TryReleaseAsync(lease, WhatIfUsageKeyPrefix);
            throw;
        }
    }

    private async Task TryReleaseAsync(QuotaLease lease, string usageKeyPrefix)
    {
        try
        {
            await dailyLimitGuard.ReleaseAsync(lease, CancellationToken.None);
        }
        catch (Exception releaseEx)
        {
            // Release best-effort: orijinal exception kullanıcıya yansımalı, release hatası loglanır.
            logger.LogWarning(releaseEx, "Daily limit release başarısız: {Prefix}", usageKeyPrefix);
        }
    }

    private async Task<User> GetAuthenticatedUserAsync(CancellationToken ct)
        => await scenarioRepository.GetUserByIdAsync(principalContext.PrincipalId, ct)
           ?? throw new InvalidOperationException("Authenticated installation principal is missing.");

    private async Task<ReverseWhatIfResponse> CalculateReverseCoreAsync(
        ReverseWhatIfRequest request, CancellationToken ct)
    {
        var symbol           = request.AssetSymbol.ToUpperInvariant();
        var sellDate         = request.SellDate
            ?? await assetService.GetLatestPriceDateAsync(symbol, ct);
        var targetAmountType = request.TargetAmountType.ToLowerInvariant();

        if (request.BuyDate > sellDate)
            throw new ValidationException(localizer["BuyDateAfterSellDate"], field: nameof(request.BuyDate));

        var inflationSuffix = request.IncludeInflation ? ":inf" : "";
        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var amountStr = request.TargetAmount.ToString("G", CultureInfo.InvariantCulture);
        var catalog = await assetService.GetCatalogVersionAsync(ct);
        var cacheKey = AuthorityCacheNamespace.Key(
            $"catalog:{catalog.Token}:whatif:reverse:v2:{symbol}:{request.BuyDate:yyyy-MM-dd}:{sellDate:yyyy-MM-dd}:{amountStr}:{targetAmountType}{inflationSuffix}:{lang}");

        var cached = await cache.TryGetAsync<ReverseWhatIfCacheEntry>(cacheKey, ct);
        if (cached is not null && cached.IsValid(
                symbol, request.BuyDate, sellDate, request.TargetAmount,
                targetAmountType, request.IncludeInflation, lang, catalog))
            return cached.Response!;
        if (cached is not null)
            await cache.TryDeleteAsync(cacheKey, ct);

        var buyPricePoint  = await assetService.GetNearestPriceAsync(symbol, request.BuyDate, ct);
        var sellPricePoint = await assetService.GetNearestPriceAsync(symbol, sellDate, ct);
        var priceAuthorities = new List<ObservationAuthorityValue>
        {
            FinalObservationAuthority.ToValue(buyPricePoint),
            FinalObservationAuthority.ToValue(sellPricePoint),
        };
        var inflationAuthorities = new List<ObservationAuthorityValue>();
        var dataWarnings = new List<string>();

        var actualBuyDate  = buyPricePoint.PriceDate  != request.BuyDate ? buyPricePoint.PriceDate  : (DateOnly?)null;
        var actualSellDate = sellPricePoint.PriceDate != sellDate         ? sellPricePoint.PriceDate : (DateOnly?)null;

        var asset = await assetService.GetBySymbolAsync(symbol, ct)
            ?? throw new AssetNotFoundException(symbol);

        var buyPrice  = buyPricePoint.Close;
        var sellPrice = sellPricePoint.Close;

        // F2.2-23 ([G-B-04]): Servis sınırında non-positive fiyatları PriceNotFound
        // ile reddet — bozuk veriyle 0 ya da negatif yatırım hesaplamasına izin verme.
        if (buyPrice <= 0)
            throw new PriceNotFoundException(symbol, request.BuyDate);

        // Ters hesaplama: hedef son değerden gereken başlangıç yatırımını bul
        decimal targetValueTry;
        decimal unitsAcquired;
        decimal requiredInvestmentTry;

        if (sellPrice <= 0)
            throw new PriceNotFoundException(symbol, sellDate);

        switch (targetAmountType)
        {
            case QuantityUnits.Try:
                // F1.3-2 / F4-3: hedef TL değeri → kaç birim → kaç TL yatırmalıydın.
                // targetValueTry ham hedeften DEĞİL, birim granülasyonundan (6 hane)
                // ileri-türetilir (forward-consistency): UnitsAcquired × SellPrice ==
                // TargetValueTry UI'da birebir uyuşur. Ham hedeften sapma alt-kuruş
                // mertebesindedir ve birim yuvarlamasının gerçek karşılığıdır (kullanıcının
                // o birim adediyle fiilen elde edebileceği TL değeri). Bkz. ADR yok —
                // api-contract.md/architecture.md "Yuvarlama politikası".
                unitsAcquired         = Math.Round(request.TargetAmount / sellPrice, 6, MidpointRounding.AwayFromZero);
                targetValueTry        = Math.Round(unitsAcquired * sellPrice, 2, MidpointRounding.AwayFromZero);
                requiredInvestmentTry = Math.Round(unitsAcquired * buyPrice, 2, MidpointRounding.AwayFromZero);
                break;
            case QuantityUnits.Units:
            case QuantityUnits.Grams:
                // Hedef birim/gram sayısı → son değer TL → gereken TL
                unitsAcquired       = request.TargetAmount;
                targetValueTry      = Math.Round(request.TargetAmount * sellPrice, 2, MidpointRounding.AwayFromZero);
                requiredInvestmentTry = Math.Round(request.TargetAmount * buyPrice, 2, MidpointRounding.AwayFromZero);
                break;
            default:
                throw new ValidationException(
                    string.Format(localizer["InvalidAmountType"], request.TargetAmountType),
                    field: nameof(request.TargetAmountType));
        }

        var profitLossTry     = targetValueTry - requiredInvestmentTry;
        var profitLossPercent = requiredInvestmentTry == 0
            ? 0m
            : Math.Round(profitLossTry / requiredInvestmentTry * 100, 2, MidpointRounding.AwayFromZero);

        IReadOnlyList<PriceHistoryPoint> priceHistory;
        try
        {
            var range = await assetService.GetPriceRangeAsync(symbol, request.BuyDate, sellDate, PriceIntervals.Daily, ct);
            priceAuthorities.AddRange(range.Select(FinalObservationAuthority.ToValue));
            priceHistory = ChartSampler.Downsample(
                range, p => new PriceHistoryPoint(p.PriceDate, p.Close), MaxPriceHistoryPoints);
        }
        catch (Exception ex) when (OptionalDataFailure.IsExpected(ex))
        {
            logger.LogWarning(ex, "Fiyat geçmişi alınamadı: {Symbol}", symbol);
            priceHistory = Array.Empty<PriceHistoryPoint>();
            dataWarnings.Add(AuthorityDataWarnings.PriceHistoryUnavailable);
        }

        // ── Enflasyon düzeltmesi ────────────────────────────────────────────
        decimal?  cumulativeInflationPercent = null;
        decimal?  realProfitLossPercent      = null;
        DateOnly? inflationDataAsOf          = null;

        if (request.IncludeInflation)
        {
            try
            {
                var (buyObservation, sellObservation) =
                    await inflationRepository.GetIndexValuesAsync(request.BuyDate, sellDate, ct);

                if (buyObservation is not null && sellObservation is not null
                    && buyObservation.IndexValue != 0)
                {
                    inflationAuthorities.Add(buyObservation.Authority);
                    inflationAuthorities.Add(sellObservation.Authority);
                    cumulativeInflationPercent = Math.Round(
                        (sellObservation.IndexValue / buyObservation.IndexValue - 1m) * 100,
                        2, MidpointRounding.AwayFromZero);

                    var nominalFactor   = 1m + profitLossPercent / 100m;
                    var inflationFactor = 1m + cumulativeInflationPercent.Value / 100m;
                    realProfitLossPercent = Math.Round(
                        (nominalFactor / inflationFactor - 1m) * 100, 2, MidpointRounding.AwayFromZero);

                    var expectedSellMonth = new DateOnly(sellDate.Year, sellDate.Month, 1);
                    if (sellObservation.PeriodDate < expectedSellMonth)
                        inflationDataAsOf = sellObservation.PeriodDate;
                }
                else
                {
                    logger.LogWarning(
                        "Enflasyon endeksi bulunamadı: {BuyDate} / {SellDate}", request.BuyDate, sellDate);
                    dataWarnings.Add(AuthorityDataWarnings.InflationIncomplete);
                }
            }
            catch (Exception ex) when (OptionalDataFailure.IsExpected(ex))
            {
                logger.LogWarning(ex, "Enflasyon hesabı başarısız, nominal getiri kullanılıyor");
                dataWarnings.Add(AuthorityDataWarnings.InflationUnavailable);
            }
        }

        var response = new ReverseWhatIfResponse(
            AssetSymbol:                symbol,
            AssetDisplayName:           assetNameLocalizer.Localize(symbol, asset.DisplayName),
            BuyDate:                    request.BuyDate,
            SellDate:                   sellDate,
            BuyPrice:                   buyPrice,
            SellPrice:                  sellPrice,
            RequiredInvestmentTry:      requiredInvestmentTry,
            UnitsAcquired:              unitsAcquired,
            TargetValueTry:             targetValueTry,
            ProfitLossTry:              profitLossTry,
            ProfitLossPercent:          profitLossPercent,
            IsProfit:                   profitLossTry >= 0,
            PriceHistory:               priceHistory,
            CumulativeInflationPercent: cumulativeInflationPercent,
            RealProfitLossPercent:      realProfitLossPercent,
            InflationDataAsOf:          inflationDataAsOf,
            ActualBuyDate:              actualBuyDate,
            ActualSellDate:             actualSellDate,
            Data: AuthorityDataResponseFactory.Calculation(
                priceAuthorities, inflationAuthorities, request.IncludeInflation, dataWarnings)
        );

        if (dataWarnings.Count == 0)
            await cache.TrySetAsync(
                cacheKey,
                ReverseWhatIfCacheEntry.Create(
                    symbol, request.BuyDate, sellDate, request.TargetAmount,
                    targetAmountType, request.IncludeInflation, lang, response, catalog),
                TimeSpan.FromHours(1), ct);

        logger.LogInformation(
            "Reverse WhatIf hesaplandı: {Symbol} {BuyDate}→{SellDate} hedef:{TargetAmountType}:{TargetAmountBucket} → {Outcome}",
            symbol, request.BuyDate, sellDate, targetAmountType,
            AmountBucket.Coarse(request.TargetAmount), TelemetryOutcome.From(profitLossTry));

        return response;
    }

    private async Task<WhatIfResponse> CalculateCoreAsync(WhatIfRequest request, CancellationToken ct)
    {
        var symbol     = request.AssetSymbol.ToUpperInvariant();
        var sellDate   = request.SellDate
            ?? await assetService.GetLatestPriceDateAsync(symbol, ct);
        var amountType = request.AmountType.ToLowerInvariant();

        if (request.BuyDate > sellDate)
            throw new ValidationException(localizer["BuyDateAfterSellDate"], field: nameof(request.BuyDate));

        var inflationSuffix = request.IncludeInflation ? ":inf" : "";
        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        // Decimal formatlamasını kültür-bağımsız tutuyoruz (tr-TR'de virgül üretmesin → cache key fragmentation).
        var amountStr = request.Amount.ToString("G", CultureInfo.InvariantCulture);
        var catalog = await assetService.GetCatalogVersionAsync(ct);
        var cacheKey = AuthorityCacheNamespace.Key(
            $"catalog:{catalog.Token}:whatif:v3:{symbol}:{request.BuyDate:yyyy-MM-dd}:{sellDate:yyyy-MM-dd}:{amountStr}:{amountType}{inflationSuffix}:{lang}");

        var cached = await cache.TryGetAsync<WhatIfCacheEntry>(cacheKey, ct);
        if (cached is not null && cached.IsValid(
                symbol, request.BuyDate, sellDate, request.Amount,
                amountType, request.IncludeInflation, lang, catalog))
            return cached.Response!;
        if (cached is not null)
            await cache.TryDeleteAsync(cacheKey, ct);

        // Fiyatlar AssetService üzerinden gelir — Redis cache'li
        // Haftasonu/tatil durumunda en yakın işlem günü kullanılır (±7 gün)
        var buyPricePoint  = await assetService.GetNearestPriceAsync(symbol, request.BuyDate, ct);
        var sellPricePoint = await assetService.GetNearestPriceAsync(symbol, sellDate, ct);
        var priceAuthorities = new List<ObservationAuthorityValue>
        {
            FinalObservationAuthority.ToValue(buyPricePoint),
            FinalObservationAuthority.ToValue(sellPricePoint),
        };
        var inflationAuthorities = new List<ObservationAuthorityValue>();
        var dataWarnings = new List<string>();

        // Kullanıcının seçtiği tarih ile fiilen kullanılan tarih farklıysa bildir
        var actualBuyDate  = buyPricePoint.PriceDate  != request.BuyDate ? buyPricePoint.PriceDate  : (DateOnly?)null;
        var actualSellDate = sellPricePoint.PriceDate != sellDate         ? sellPricePoint.PriceDate : (DateOnly?)null;

        var asset = await assetService.GetBySymbolAsync(symbol, ct)
            ?? throw new AssetNotFoundException(symbol);

        var buyPrice  = buyPricePoint.Close;
        var sellPrice = sellPricePoint.Close;

        // F2.2-23 ([G-B-04]): non-positive (≤0) fiyatları PriceNotFound olarak reddet.
        // Bozuk veriyle divide-by-zero ya da negatif PnL üretmeyiz.
        if (buyPrice <= 0)
            throw new PriceNotFoundException(symbol, request.BuyDate);
        if (sellPrice <= 0)
            throw new PriceNotFoundException(symbol, sellDate);

        decimal initialValueTry;
        decimal unitsAcquired;

        switch (amountType)
        {
            case QuantityUnits.Try:
                initialValueTry = request.Amount;
                unitsAcquired   = Math.Round(request.Amount / buyPrice, 6, MidpointRounding.AwayFromZero);
                break;
            case QuantityUnits.Units:
            case QuantityUnits.Grams:
                unitsAcquired   = request.Amount;
                initialValueTry = Math.Round(request.Amount * buyPrice, 2, MidpointRounding.AwayFromZero);
                break;
            default:
                throw new ValidationException(
                    string.Format(localizer["InvalidAmountType"], request.AmountType),
                    field: nameof(request.AmountType));
        }

        var finalValueTry     = Math.Round(unitsAcquired * sellPrice, 2, MidpointRounding.AwayFromZero);
        var profitLossTry     = finalValueTry - initialValueTry;
        var profitLossPercent = initialValueTry == 0
            ? 0m
            : Math.Round(profitLossTry / initialValueTry * 100, 2, MidpointRounding.AwayFromZero);

        IReadOnlyList<PriceHistoryPoint> priceHistory;
        try
        {
            var range = await assetService.GetPriceRangeAsync(symbol, request.BuyDate, sellDate, PriceIntervals.Daily, ct);
            priceAuthorities.AddRange(range.Select(FinalObservationAuthority.ToValue));
            priceHistory = ChartSampler.Downsample(
                range, p => new PriceHistoryPoint(p.PriceDate, p.Close), MaxPriceHistoryPoints);
        }
        catch (Exception ex) when (OptionalDataFailure.IsExpected(ex))
        {
            logger.LogWarning(ex, "Fiyat geçmişi alınamadı: {Symbol}", symbol);
            priceHistory = Array.Empty<PriceHistoryPoint>();
            dataWarnings.Add(AuthorityDataWarnings.PriceHistoryUnavailable);
        }

        // ── Enflasyon düzeltmesi ────────────────────────────────────────────
        decimal?  cumulativeInflationPercent = null;
        decimal?  realProfitLossPercent      = null;
        DateOnly? inflationDataAsOf          = null;

        if (request.IncludeInflation)
        {
            try
            {
                var (buyObservation, sellObservation) =
                    await inflationRepository.GetIndexValuesAsync(request.BuyDate, sellDate, ct);

                if (buyObservation is not null && sellObservation is not null
                    && buyObservation.IndexValue != 0)
                {
                    inflationAuthorities.Add(buyObservation.Authority);
                    inflationAuthorities.Add(sellObservation.Authority);
                    // Birikimli enflasyon: (satış_endeksi / alış_endeksi) - 1
                    cumulativeInflationPercent = Math.Round(
                        (sellObservation.IndexValue / buyObservation.IndexValue - 1m) * 100,
                        2, MidpointRounding.AwayFromZero);

                    // Fisher denklemi: reel_getiri = (1 + nominal) / (1 + enflasyon) - 1
                    var nominalFactor   = 1m + profitLossPercent / 100m;
                    var inflationFactor = 1m + cumulativeInflationPercent.Value / 100m;
                    realProfitLossPercent = Math.Round(
                        (nominalFactor / inflationFactor - 1m) * 100, 2, MidpointRounding.AwayFromZero);

                    // Satış ayının tam verisi yoksa (TÜİK gecikmesi) gerçek tarih bildirilir
                    var expectedSellMonth = new DateOnly(sellDate.Year, sellDate.Month, 1);
                    if (sellObservation.PeriodDate < expectedSellMonth)
                        inflationDataAsOf = sellObservation.PeriodDate;
                }
                else
                {
                    logger.LogWarning(
                        "Enflasyon endeksi bulunamadı: {BuyDate} / {SellDate}", request.BuyDate, sellDate);
                    dataWarnings.Add(AuthorityDataWarnings.InflationIncomplete);
                }
            }
            catch (Exception ex) when (OptionalDataFailure.IsExpected(ex))
            {
                logger.LogWarning(ex, "Enflasyon hesabı başarısız, nominal getiri kullanılıyor");
                dataWarnings.Add(AuthorityDataWarnings.InflationUnavailable);
            }
        }

        var response = new WhatIfResponse(
            AssetSymbol:                symbol,
            AssetDisplayName:           assetNameLocalizer.Localize(symbol, asset.DisplayName),
            BuyDate:                    request.BuyDate,
            SellDate:                   sellDate,
            BuyPrice:                   buyPrice,
            SellPrice:                  sellPrice,
            UnitsAcquired:              unitsAcquired,
            InitialValueTry:            initialValueTry,
            FinalValueTry:              finalValueTry,
            ProfitLossTry:              profitLossTry,
            ProfitLossPercent:          profitLossPercent,
            IsProfit:                   profitLossTry >= 0,
            PriceHistory:               priceHistory,
            CumulativeInflationPercent: cumulativeInflationPercent,
            RealProfitLossPercent:      realProfitLossPercent,
            InflationDataAsOf:          inflationDataAsOf,
            ActualBuyDate:              actualBuyDate,
            ActualSellDate:             actualSellDate,
            Data: AuthorityDataResponseFactory.Calculation(
                priceAuthorities, inflationAuthorities, request.IncludeInflation, dataWarnings)
        );

        if (dataWarnings.Count == 0)
            await cache.TrySetAsync(
                cacheKey,
                WhatIfCacheEntry.Create(
                    symbol, request.BuyDate, sellDate, request.Amount,
                    amountType, request.IncludeInflation, lang, response, catalog),
                TimeSpan.FromHours(1), ct);

        // Nullable decimal'i doğrudan structured field olarak geç — null tutarlı yansır.
        logger.LogInformation(
            "WhatIf hesaplandı: {Symbol} {BuyDate}→{SellDate} {AmountType}:{AmountBucket} → {Outcome} (reel: {RealOutcome})",
            symbol, request.BuyDate, sellDate, amountType, AmountBucket.Coarse(request.Amount),
            TelemetryOutcome.From(profitLossTry), TelemetryOutcome.From(realProfitLossPercent));

        return response;
    }

    private void EnsureRequired(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ValidationException(
                string.Format(localizer["RequestPayloadMissing"], field), field: field);
    }

    // P1R-003: ArgumentException base type framework / altyapı tarafından da fırlatılır
    // (Redis bağlantı yapılandırma, EF Core, Npgsql vs.). Endpoint katmanından gelen
    // request guard'larını domain ValidationException'a çevirerek
    // ValidationExceptionHandler'ın ArgumentException case'ini kaldırabilir hale getirdik.
    private void EnsureRequest(object? request)
    {
        if (request is null)
            throw new ValidationException(
                string.Format(localizer["RequestPayloadMissing"], "request"), field: "request");
    }

    // F1.9-4 ([C-F-14]): Negatif / sıfır amount semantik olarak anlamsız —
    // "ya 0 TL alsaydım?" pratik bir hesap değil. Validation tüm calculator
    // entry-point'lerinde aynı şekilde uygulanır.
    private void EnsurePositive(decimal value, string field)
    {
        if (value <= 0m)
            throw new ValidationException(localizer["AmountMustBePositive"], field: field);
    }

    /// <summary>
    /// F2.2-21 ([G-B-01]): Tier'ın <c>PriceHistoryMonths</c> sınırı (0 = sınırsız)
    /// alış tarihine dayatılır. BuyDate "bugünden <c>months</c> ay önceki tarihten önce"
    /// ise <see cref="FeatureDisabledException"/> fırlatır (PaidUpgradeRequired semantiği).
    /// </summary>
    private void EnsureWithinHistoryWindow(DateOnly buyDate, int months)
    {
        if (months <= 0) return;
        var earliestAllowed = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime).AddMonths(-months);
        if (buyDate < earliestAllowed)
            throw new FeatureDisabledException(localizer[FeatureDisabledKey], featureKey: "extended_history");
    }

}
