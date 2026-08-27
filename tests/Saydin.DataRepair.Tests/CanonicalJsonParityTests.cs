using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Saydin.DataRepair.Tests;

public sealed class CanonicalJsonParityTests
{
    private delegate byte[] Canonicalize(ReadOnlySpan<byte> json);

    [Theory]
    [InlineData("{\"z\":1,\"a\":true,\"m\":null}")]
    [InlineData("{\"nested\":{\"b\":2,\"a\":1},\"array\":[3,\"x\",false]}")]
    [InlineData("{\"unicode\":\"Saydın İstanbul\",\"integer\":-9223372036854775808}")]
    public void RepairAndDqaCanonicalizers_ProduceIdenticalBytes(string json)
    {
        var input = Encoding.UTF8.GetBytes(json);
        CanonicalJson.Canonicalize(input).Should().Equal(DqaCanonicalize()(input));
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"a\":1.5}")]
    public void RepairAndDqaCanonicalizers_BothRejectUnsafeContracts(string json)
    {
        var input = Encoding.UTF8.GetBytes(json);
        var repair = () => CanonicalJson.Canonicalize(input);
        var dqa = () => DqaCanonicalize()(input);

        repair.Should().Throw<Exception>();
        dqa.Should().Throw<Exception>();
    }

    [Fact]
    public void RepairAndDqaCanonicalizers_UseTheSameDepthBoundary()
    {
        var atLimit = Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("{\"v\":", 32)) + "0" +
            string.Concat(Enumerable.Repeat("}", 32)));
        var aboveLimit = Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("{\"v\":", 33)) + "0" +
            string.Concat(Enumerable.Repeat("}", 33)));

        CanonicalJson.Canonicalize(atLimit).Should().Equal(DqaCanonicalize()(atLimit));
        var repair = () => CanonicalJson.Canonicalize(aboveLimit);
        var dqa = () => DqaCanonicalize()(aboveLimit);
        repair.Should().Throw<JsonException>();
        dqa.Should().Throw<JsonException>();
    }

    private static Canonicalize DqaCanonicalize()
    {
        var assembly = Assembly.Load("Saydin.DataQualityAudit");
        var type = assembly.GetType("Saydin.DataQualityAudit.CanonicalJson", throwOnError: true)!;
        var method = type.GetMethod(
            "Canonicalize", BindingFlags.Public | BindingFlags.Static,
            binder: null, [typeof(ReadOnlySpan<byte>)], modifiers: null)!;
        return method.CreateDelegate<Canonicalize>();
    }
}
