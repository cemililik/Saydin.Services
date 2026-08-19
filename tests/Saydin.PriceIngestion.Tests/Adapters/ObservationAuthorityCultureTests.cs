using System.Globalization;
using FluentAssertions;
using Saydin.PriceIngestion.Mappers;

namespace Saydin.PriceIngestion.Tests.Adapters;

public sealed class ObservationAuthorityCultureTests
{
    [Fact]
    public void ProviderIdsEvidenceAndHashes_AreByteIdenticalAcrossCultures()
    {
        var fingerprints = new[] { "en-US", "tr-TR", "th-TH", "ar-SA" }
            .Select(culture => UnderCulture(culture, CreateFingerprint))
            .ToArray();

        fingerprints.Should().OnlyContain(value => value == fingerprints[0]);
    }

    private static string CreateFingerprint()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var day = new DateOnly(2024, 1, 3);
        var tcmb = TcmbMapper.Map("""
            <Tarih_Date Tarih="03.01.2024" Date="01/03/2024">
              <Currency CurrencyCode="USD"><Unit>1</Unit><ForexBuying>30.5</ForexBuying></Currency>
            </Tarih_Date>
            """, id, "USD", day)!;
        var oxr = OpenExchangeRatesMapper.Map(
            """{"base":"USD","timestamp":1704240000,"rates":{"XAU":0.0005,"TRY":30}}""",
            id, day, "XAU")!;
        var twelve = TwelveDataMapper.Map("""
            {"status":"ok","meta":{"symbol":"THYAO","interval":"1day","exchange":"BIST",
             "mic_code":"XIST","exchange_timezone":"Europe/Istanbul","currency":"TRY","type":"Common Stock"},
             "values":[{"datetime":"2024-01-03","open":"99","high":"102","low":"98","close":"100.5","volume":"1"}]}
            """, id, "THYAO:BIST").Single();
        var evds = EvdsInflationMapper.Map(
            """{"items":[{"Tarih":"2024-1","TP_FG_J0":"100.5"}]}""").Single();
        var coin = CoinGeckoMapper.Map(
            """{"prices":[[1704240000000,42000.5]]}""", id, day, day, "bitcoin").Single();

        foreach (var observation in new[] { tcmb, oxr, twelve, coin })
            observation.ObservationSha256.Should().BeNull();
        evds.ObservationSha256.Should().BeNull();

        return string.Join('|', new[]
        {
            tcmb.SourceObservationId, tcmb.SourceRaw,
            oxr.SourceObservationId, oxr.SourceRaw,
            twelve.SourceObservationId, twelve.SourceRaw,
            evds.SourceObservationId, evds.SourceRaw,
            coin.SourceObservationId, coin.SourceRaw,
        });
    }

    private static T UnderCulture<T>(string name, Func<T> action)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
