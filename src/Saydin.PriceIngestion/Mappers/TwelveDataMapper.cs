using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Saydin.PriceIngestion.Adapters;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Mappers;

public static class TwelveDataMapper
{
    public static IReadOnlyList<PricePoint> Map(
        string json,
        Guid assetId,
        string sourceId,
        string source = "twelvedata",
        byte[]? payloadSha256 = null,
        int? payloadByteLength = null)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new ProviderContractException("contract_source_id_missing");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ProviderContractException("contract_value_kind_invalid");

        // F1.1-6: status="error" yanıtlarında veri yok olarak sessizce dönmek yerine
        // ExternalApiException fırlat — caller ingestion_jobs failed olarak kaydetsin.
        if (root.TryGetProperty("status", out var statusEl))
        {
            var status = ProviderValueParser.ReadString(statusEl);
            if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            {
                throw new ProviderContractException("provider_error");
            }
            if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                // ok / error dışı status — gelecekteki "rate_limited" gibi değerler için defansif:
                // log seviyesinde değil, exception olarak yukarı bildir.
                throw new ProviderContractException("provider_status_invalid");
            }
        }

        var instrumentType = ValidateIdentity(root, sourceId);

        if (!root.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<PricePoint>();
        var payloadHash = payloadSha256 ?? SHA256.HashData(Encoding.UTF8.GetBytes(json));

        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ProviderContractException("contract_value_kind_invalid");

            // PR #11 follow-up: TwelveData API her zaman "yyyy-MM-dd" formatı döner.
            // DateOnly.TryParse current culture'a duyarlı (tr-TR locale'de bazı tarih
            // varyantları beklenmedik parse edebilir) — invariant exact format ile
            // tek doğru girdi kabul edilir, diğer her şey atlanır.
            if (!item.TryGetProperty("datetime", out var dateEl) ||
                !DateOnly.TryParseExact(ProviderValueParser.ReadString(dateEl), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;

            if (!item.TryGetProperty("close", out var closeEl) ||
                !ProviderValueParser.TryReadDecimal(closeEl, out var close))
                continue;

            var open = ParseDecimal(item, "open");
            var high = ParseDecimal(item, "high");
            var low = ParseDecimal(item, "low");
            var volume = ParseDecimal(item, "volume");
            if (open is null || high is null || low is null || volume is null
                || close <= 0 || open <= 0 || high <= 0 || low <= 0
                || high < Math.Max(open.Value, close) || low > Math.Min(open.Value, close)
                || high < low || volume < 0)
                continue;

            var observationId = $"twelvedata:{sourceId}:{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}:1day";
            // Daily bars are keyed by exchange-local bar-open date. Convert exact
            // Europe/Istanbul local midnight through time-zone rules; no close second
            // is invented and the stored instant remains globally unambiguous.
            var localMidnight = DateTime.SpecifyKind(
                date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
            var asOf = new DateTimeOffset(
                TimeZoneInfo.ConvertTimeToUtc(localMidnight,
                    TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul")), TimeSpan.Zero);
            var evidence = ObservationEvidence.Create(
                ("as_of_at", asOf), ("close", close), ("currency", "TRY"), ("date", date),
                ("exchange", "BIST"), ("exchange_timezone", "Europe/Istanbul"),
                ("high", high.Value), ("instrument_type", instrumentType), ("interval", "1day"),
                ("low", low.Value), ("mic_code", "XIST"), ("observation_id", observationId),
                ("open", open.Value), ("provider_source", ProviderSources.TwelveData),
                ("symbol", sourceId.Split(':', 2, StringSplitOptions.TrimEntries)[0]),
                ("volume", volume));

            results.Add(ProviderAuthority.Price(new PricePoint
            {
                AssetId   = assetId,
                PriceDate = date,
                Close     = close,
                Open      = open,
                High      = high,
                Low       = low,
                Volume    = volume,
            }, ProviderSources.TwelveData, observationId, asOf,
                ObservationPriceKinds.DailyClose, payloadHash,
                payloadByteLength ?? Encoding.UTF8.GetByteCount(json), evidence));
        }

        return results.OrderBy(p => p.PriceDate).ToList().AsReadOnly();
    }

    private static decimal? ParseDecimal(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var prop)) return null;
        return ProviderValueParser.TryReadDecimal(prop, out var value) ? value : null;
    }

    private static string ValidateIdentity(JsonElement root, string sourceId)
    {
        if (!root.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
            throw new ProviderContractException("contract_meta_missing");

        var parts = sourceId.Split(':', 2, StringSplitOptions.TrimEntries);
        var symbol = meta.TryGetProperty("symbol", out var symbolElement)
            ? ProviderValueParser.ReadString(symbolElement) : null;
        var interval = meta.TryGetProperty("interval", out var intervalElement)
            ? ProviderValueParser.ReadString(intervalElement) : null;
        var exchange = meta.TryGetProperty("exchange", out var exchangeElement)
            ? ProviderValueParser.ReadString(exchangeElement) : null;
        var timezone = meta.TryGetProperty("exchange_timezone", out var timezoneElement)
            ? ProviderValueParser.ReadString(timezoneElement) : null;
        var micCode = meta.TryGetProperty("mic_code", out var micElement)
            ? ProviderValueParser.ReadString(micElement) : null;
        var currency = meta.TryGetProperty("currency", out var currencyElement)
            ? ProviderValueParser.ReadString(currencyElement) : null;
        var instrumentType = meta.TryGetProperty("type", out var typeElement)
            ? ProviderValueParser.ReadString(typeElement) : null;
        if (!string.Equals(symbol, parts[0], StringComparison.Ordinal)
            || interval != "1day"
            || exchange != "BIST" || micCode != "XIST"
            || timezone != "Europe/Istanbul" || currency != "TRY"
            || instrumentType is not ("Common Stock" or "Stock")
            || parts.Length == 2 && parts[1] != "BIST")
            throw new ProviderContractException("contract_identity_mismatch");
        return instrumentType;
    }
}
