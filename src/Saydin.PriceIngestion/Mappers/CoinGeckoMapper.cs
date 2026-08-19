using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Saydin.PriceIngestion.Adapters;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Mappers;

public static class CoinGeckoMapper
{
    /// <summary>
    /// CoinGecko günlük authority yalnız exact 00:00:00.000 UTC observation'dır.
    /// Hourly/intraday değerler hiçbir zaman "nearest" seçilmez.
    /// </summary>
    public static IReadOnlyList<PricePoint> Map(
        string json,
        Guid assetId,
        DateOnly from,
        DateOnly to,
        string sourceId = "unknown",
        byte[]? payloadSha256 = null,
        int? payloadByteLength = null)
    {
        using var doc = JsonDocument.Parse(json);
        var prices = doc.RootElement.GetProperty("prices");

        var payloadHash = payloadSha256 ?? SHA256.HashData(Encoding.UTF8.GetBytes(json));
        var daily = new Dictionary<DateOnly, PricePoint>();

        foreach (var pair in prices.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2
                || !pair[0].TryGetInt64(out var timestampMs)
                || !pair[1].TryGetDecimal(out var price) || price <= 0)
                throw new ProviderContractException("contract_invalid_price_pair");
            var utcMoment   = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs);
            var date        = DateOnly.FromDateTime(utcMoment.UtcDateTime);
            if (date < from || date > to) continue;
            var midnight = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);
            if (utcMoment != midnight) continue;
            if (daily.ContainsKey(date))
                throw new ProviderContractException("contract_duplicate_daily_observation");

            var rounded = Math.Round(price, 6, MidpointRounding.AwayFromZero);
            var observationId = $"coingecko:{sourceId}:try:{timestampMs}";
            var evidence = ObservationEvidence.Create(
                ("as_of_at", utcMoment),
                ("close", rounded),
                ("date", date),
                ("observation_id", observationId),
                ("provider_source", ProviderSources.CoinGecko),
                ("quote_currency", "TRY"),
                ("source_timestamp_ms", timestampMs),
                ("symbol", sourceId));
            daily[date] = ProviderAuthority.Price(new PricePoint
            {
                AssetId = assetId,
                PriceDate = date,
                Close = rounded,
            }, ProviderSources.CoinGecko, observationId, utcMoment,
                ObservationPriceKinds.DailyUtcReference, payloadHash,
                payloadByteLength ?? Encoding.UTF8.GetByteCount(json), evidence);
        }

        return daily
            .Values.OrderBy(p => p.PriceDate)
            .ToList()
            .AsReadOnly();
    }
}
