using System.Globalization;
using FluentAssertions;
using Saydin.Shared.Constants;

namespace Saydin.Api.Tests.Constants;

/// <summary>
/// F4-6 (KVKK): <see cref="AmountBucket.Coarse"/> sınır değerlerini kilitler. Ham finansal
/// tutar yerine activity_logs'a kaba aralık yazıldığından, sınırların kayması analytics
/// anlamını ve gizlilik garantisini bozar — bu yüzden bucket eşikleri kontrat testidir.
/// Girdi string olarak verilip InvariantCulture ile decimal'e çevrilir (float/kültür belirsizliği yok).
/// </summary>
public class AmountBucketTests
{
    [Theory]
    [InlineData("0", "0")]
    [InlineData("-50", "0")]
    [InlineData("0.01", "0-1k")]
    [InlineData("999.99", "0-1k")]
    [InlineData("1000", "1k-10k")]
    [InlineData("9999.99", "1k-10k")]
    [InlineData("10000", "10k-100k")]
    [InlineData("99999.99", "10k-100k")]
    [InlineData("100000", "100k-1M")]
    [InlineData("999999.99", "100k-1M")]
    [InlineData("1000000", "1M+")]
    [InlineData("50000000", "1M+")]
    public void Coarse_MapsAmountToExpectedBucket(string amountLiteral, string expected)
    {
        var amount = decimal.Parse(amountLiteral, CultureInfo.InvariantCulture);
        AmountBucket.Coarse(amount).Should().Be(expected);
    }

    [Fact]
    public void Coarse_NeverReturnsRawAmount()
    {
        // KVKK garantisi: çıktı hiçbir zaman ham tutarın ondalık temsili değildir.
        var bucket = AmountBucket.Coarse(123_456.78m);
        bucket.Should().Be("100k-1M");
        bucket.Should().NotContain("123");
    }
}
