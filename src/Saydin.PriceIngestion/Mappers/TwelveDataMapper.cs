using System.Globalization;
using System.Text.Json;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.PriceIngestion.Mappers;

public static class TwelveDataMapper
{
    public static IReadOnlyList<PricePoint> Map(
        string json,
        Guid assetId,
        string assetSymbol = "",
        string source = "twelvedata")
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // F1.1-6: status="error" yanıtlarında veri yok olarak sessizce dönmek yerine
        // ExternalApiException fırlat — caller ingestion_jobs failed olarak kaydetsin.
        if (root.TryGetProperty("status", out var statusEl))
        {
            var status = statusEl.GetString();
            if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            {
                var code = root.TryGetProperty("code", out var codeEl) ? codeEl.ToString() : "n/a";
                var message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
                throw new ExternalApiException(source,
                    $"TwelveData error response (symbol={assetSymbol}, code={code}): {message}");
            }
            if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                // ok / error dışı status — gelecekteki "rate_limited" gibi değerler için defansif:
                // log seviyesinde değil, exception olarak yukarı bildir.
                throw new ExternalApiException(source,
                    $"TwelveData beklenmeyen status '{status}' (symbol={assetSymbol})");
            }
        }

        if (!root.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<PricePoint>();

        foreach (var item in values.EnumerateArray())
        {
            // PR #11 follow-up: TwelveData API her zaman "yyyy-MM-dd" formatı döner.
            // DateOnly.TryParse current culture'a duyarlı (tr-TR locale'de bazı tarih
            // varyantları beklenmedik parse edebilir) — invariant exact format ile
            // tek doğru girdi kabul edilir, diğer her şey atlanır.
            if (!item.TryGetProperty("datetime", out var dateEl) ||
                !DateOnly.TryParseExact(dateEl.GetString(), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;

            if (!item.TryGetProperty("close", out var closeEl) ||
                !decimal.TryParse(closeEl.GetString(),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var close))
                continue;

            results.Add(new PricePoint
            {
                AssetId   = assetId,
                PriceDate = date,
                Close     = close,
                Open      = ParseDecimal(item, "open"),
                High      = ParseDecimal(item, "high"),
                Low       = ParseDecimal(item, "low"),
                Volume    = ParseDecimal(item, "volume")
            });
        }

        return results.OrderBy(p => p.PriceDate).ToList().AsReadOnly();
    }

    private static decimal? ParseDecimal(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var prop)) return null;
        var s = prop.GetString();
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
