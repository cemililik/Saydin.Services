using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.IntegrationTests;

internal static class AuthorityTestData
{
    public static PricePoint CoinGecko(
        Guid assetId,
        string sourceId,
        DateOnly date,
        decimal close = 42m)
    {
        var asOf = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var timestamp = asOf.ToUnixTimeMilliseconds();
        var observationId = $"coingecko:{sourceId}:try:{timestamp.ToString(CultureInfo.InvariantCulture)}";
        return Price(assetId, date, close, ProviderSources.CoinGecko, observationId, asOf,
            ObservationPriceKinds.DailyUtcReference, new Dictionary<string, object>
            {
                ["as_of_at"] = asOf.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["close"] = close,
                ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["observation_id"] = observationId,
                ["provider_source"] = ProviderSources.CoinGecko,
                ["quote_currency"] = "TRY",
                ["source_timestamp_ms"] = timestamp,
                ["symbol"] = sourceId,
            });
    }

    public static PricePoint Tcmb(
        Guid assetId,
        string sourceId,
        DateOnly date,
        decimal close = 42m)
    {
        var asOf = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var parts = sourceId.Split('.');
        var currency = parts.Length >= 3 ? parts[2] : sourceId;
        var observationId = $"tcmb:{currency}:{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}:forex_buying";
        return Price(assetId, date, close, ProviderSources.Tcmb, observationId, asOf,
            ObservationPriceKinds.OfficialReference, new Dictionary<string, object>
            {
                ["as_of_at"] = asOf.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["close"] = close,
                ["currency"] = currency,
                ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["observation_id"] = observationId,
                ["provider_source"] = ProviderSources.Tcmb,
                ["unit"] = 1m,
            });
    }

    public static PricePoint TwelveData(
        Guid assetId,
        string sourceId,
        DateOnly date,
        decimal close = 42m)
    {
        var asOf = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3))
            .ToUniversalTime();
        var observationId = $"twelvedata:{sourceId}:{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}:1day";
        var point = Price(assetId, date, close, ProviderSources.TwelveData, observationId, asOf,
            ObservationPriceKinds.DailyClose, new Dictionary<string, object>
            {
                ["as_of_at"] = asOf.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["close"] = close,
                ["currency"] = "TRY",
                ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["exchange"] = "BIST",
                ["exchange_timezone"] = "Europe/Istanbul",
                ["high"] = close,
                ["instrument_type"] = "Common Stock",
                ["interval"] = "1day",
                ["low"] = close,
                ["mic_code"] = "XIST",
                ["observation_id"] = observationId,
                ["open"] = close,
                ["provider_source"] = ProviderSources.TwelveData,
                ["symbol"] = sourceId.Split(':', 2)[0],
                ["volume"] = 0m,
            });
        point.Open = close;
        point.High = close;
        point.Low = close;
        point.Volume = 0m;
        return point;
    }

    public static InflationRate Evds(DateOnly date, decimal value = 100m)
    {
        var asOf = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var observationId = $"evds:TP_FG_J0:{date.ToString("yyyy-MM", CultureInfo.InvariantCulture)}";
        var raw = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["as_of_at"] = asOf.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["index_value"] = value,
            ["observation_id"] = observationId,
            ["provider_source"] = ProviderSources.Evds,
            ["series"] = "TP.FG.J0",
        });
        return new InflationRate
        {
            PeriodDate = date,
            IndexValue = value,
            Source = "tuik",
            ProviderSource = ProviderSources.Evds,
            SourceObservationId = observationId,
            AsOfAt = asOf,
            PriceKind = ObservationPriceKinds.CpiIndex,
            IsFinal = true,
            PayloadSha256 = PayloadHash(ProviderSources.Evds, observationId),
            PayloadByteLength = Encoding.UTF8.GetByteCount(raw),
            SourceRaw = raw,
        };
    }

    private static PricePoint Price(
        Guid assetId,
        DateOnly date,
        decimal close,
        string provider,
        string observationId,
        DateTimeOffset asOf,
        string kind,
        IReadOnlyDictionary<string, object> evidence)
    {
        var raw = JsonSerializer.Serialize(evidence);
        return new PricePoint
        {
            AssetId = assetId,
            PriceDate = date,
            Close = close,
            ProviderSource = provider,
            SourceObservationId = observationId,
            AsOfAt = asOf,
            PriceKind = kind,
            IsFinal = true,
            PayloadSha256 = PayloadHash(provider, observationId),
            PayloadByteLength = Encoding.UTF8.GetByteCount(raw),
            SourceRaw = raw,
        };
    }

    private static byte[] PayloadHash(string provider, string observationId) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"integration-payload:{provider}:{observationId}"));
}
