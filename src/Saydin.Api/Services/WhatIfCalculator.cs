using System.Text.Json;
using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;
using Saydin.Api.Repositories;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;
using StackExchange.Redis;

namespace Saydin.Api.Services;

public sealed class WhatIfCalculator(
    IAssetService assetService,
    ISavedScenarioRepository scenarioRepository,
    IConnectionMultiplexer redis,
    ILogger<WhatIfCalculator> logger) : IWhatIfCalculator
{
    private const int    FreeUserDailyLimit    = 10;
    private const string PremiumTier           = "premium";
    private const string WhatIfUsageKeyPrefix  = "usage:whatif:";
    private const int    MaxPriceHistoryPoints = 60;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<WhatIfResponse> CalculateAsync(string deviceId, WhatIfRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        await EnforceDailyLimitAsync(deviceId, ct);

        var symbol     = request.AssetSymbol.ToUpperInvariant();
        var sellDate   = request.SellDate
            ?? await assetService.GetLatestPriceDateAsync(symbol, ct);
        var amountType = request.AmountType.ToLowerInvariant();

        if (request.BuyDate > sellDate)
            throw new ArgumentException("Alış tarihi satış tarihinden sonra olamaz.");

        var cacheKey = $"whatif:v2:{symbol}:{request.BuyDate:yyyy-MM-dd}:{sellDate:yyyy-MM-dd}:{request.Amount}:{amountType}";

        var cached = await TryGetCachedAsync<WhatIfResponse>(cacheKey);
        if (cached is not null) return cached;

        // Fiyatlar AssetService üzerinden gelir — Redis cache'li
        var buyPricePoint  = await assetService.GetPriceAsync(symbol, request.BuyDate, ct);
        var sellPricePoint = await assetService.GetPriceAsync(symbol, sellDate, ct);

        var assets = await assetService.GetAllAsync(ct);
        var asset  = assets.FirstOrDefault(a => a.Symbol == symbol)
            ?? throw new PriceNotFoundException(symbol, request.BuyDate);

        var buyPrice  = buyPricePoint.Close;
        var sellPrice = sellPricePoint.Close;

        decimal initialValueTry;
        decimal unitsAcquired;

        switch (amountType)
        {
            case "try":
                initialValueTry = request.Amount;
                unitsAcquired   = Math.Round(request.Amount / buyPrice, 6, MidpointRounding.AwayFromZero);
                break;
            case "units":
            case "grams":
                unitsAcquired   = request.Amount;
                initialValueTry = Math.Round(request.Amount * buyPrice, 2, MidpointRounding.AwayFromZero);
                break;
            default:
                throw new ArgumentException(
                    $"Geçersiz amountType: '{request.AmountType}'. Beklenen: try, units, grams");
        }

        var finalValueTry     = Math.Round(unitsAcquired * sellPrice, 2, MidpointRounding.AwayFromZero);
        var profitLossTry     = finalValueTry - initialValueTry;
        var profitLossPercent = initialValueTry == 0
            ? 0m
            : Math.Round(profitLossTry / initialValueTry * 100, 2, MidpointRounding.AwayFromZero);

        IReadOnlyList<PriceHistoryPoint> priceHistory;
        try
        {
            var range = await assetService.GetPriceRangeAsync(symbol, request.BuyDate, sellDate, "daily", ct);
            priceHistory = SamplePriceHistory(range);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fiyat geçmişi alınamadı: {Symbol}", symbol);
            priceHistory = Array.Empty<PriceHistoryPoint>();
        }

        var response = new WhatIfResponse(
            AssetSymbol:       symbol,
            AssetDisplayName:  asset.DisplayName,
            BuyDate:           request.BuyDate,
            SellDate:          sellDate,
            BuyPrice:          buyPrice,
            SellPrice:         sellPrice,
            UnitsAcquired:     unitsAcquired,
            InitialValueTry:   initialValueTry,
            FinalValueTry:     finalValueTry,
            ProfitLossTry:     profitLossTry,
            ProfitLossPercent: profitLossPercent,
            IsProfit:          profitLossTry >= 0,
            PriceHistory:      priceHistory
        );

        await TrySetCacheAsync(cacheKey, response, TimeSpan.FromHours(1));

        logger.LogInformation(
            "WhatIf hesaplandı: {Symbol} {BuyDate}→{SellDate} {AmountType}:{Amount} → %{ProfitLossPercent}",
            symbol, request.BuyDate, sellDate, amountType, request.Amount, profitLossPercent);

        return response;
    }

    private static IReadOnlyList<PriceHistoryPoint> SamplePriceHistory(
        IReadOnlyList<PricePoint> points, int maxPoints = MaxPriceHistoryPoints)
    {
        if (points.Count == 0) return Array.Empty<PriceHistoryPoint>();
        if (points.Count <= maxPoints)
            return points.Select(p => new PriceHistoryPoint(p.PriceDate, p.Close)).ToList();

        var result = new List<PriceHistoryPoint>(maxPoints);
        for (var i = 0; i < maxPoints; i++)
        {
            var idx = (int)Math.Round((double)i * (points.Count - 1) / (maxPoints - 1));
            result.Add(new PriceHistoryPoint(points[idx].PriceDate, points[idx].Close));
        }
        return result;
    }

    private async Task<T?> TryGetCachedAsync<T>(string key) where T : class
    {
        try
        {
            var db    = redis.GetDatabase();
            var value = await db.StringGetAsync(key);
            if (!value.HasValue) return null;
            return JsonSerializer.Deserialize<T>(value.ToString(), JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis okuma hatası: {Key}", key);
            return null;
        }
    }

    private async Task TrySetCacheAsync<T>(string key, T value, TimeSpan ttl)
    {
        try
        {
            var db = redis.GetDatabase();
            await db.StringSetAsync(key, JsonSerializer.Serialize(value, JsonOptions), ttl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis yazma hatası: {Key}", key);
        }
    }

    private async Task EnforceDailyLimitAsync(string deviceId, CancellationToken ct)
    {
        // Repository hataları (DB bağlantısı vb.) kasıtlı olarak yukarı kabarcıklanır
        var user = await scenarioRepository.GetUserByDeviceIdAsync(deviceId, ct);
        if (user?.Tier == PremiumTier)
            return;

        var userId  = user?.Id.ToString() ?? deviceId;
        var dateKey = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var key     = $"{WhatIfUsageKeyPrefix}{userId}:{dateKey}";

        try
        {
            var db     = redis.GetDatabase();
            var ttlMs  = (long)(DateTime.UtcNow.Date.AddDays(1) - DateTime.UtcNow).TotalMilliseconds;

            // Atomik: INCR + PEXPIRE (sadece ilk artışta) tek script'te
            const string script = """
                local count = redis.call('INCR', KEYS[1])
                if count == 1 then
                  redis.call('PEXPIRE', KEYS[1], ARGV[1])
                end
                return count
                """;

            var count = (long)await db.ScriptEvaluateAsync(
                script,
                keys: [key],
                values: [ttlMs]);

            if (count > FreeUserDailyLimit)
                throw new DailyLimitExceededException(FreeUserDailyLimit);
        }
        catch (Exception ex) when (ex is not DailyLimitExceededException)
        {
            // Redis erişim hatası — limit kontrolünü pas geç, hesaplamaya devam et
            logger.LogWarning(ex, "Daily limit Redis kontrolü başarısız, hesaplama devam ediyor");
        }
    }
}
