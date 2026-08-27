using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Saydin.Api.Services;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Tests.Services;

public sealed class ScenarioExtraDataValidatorTests
{
    private readonly IStringLocalizer<ErrorMessages> _localizer = CreateLocalizer();

    [Theory]
    [InlineData("what_if", "{\"includeInflation\":true,\"mode\":\"reverse\"}")]
    [InlineData("comparison", "{\"winnerSymbol\":\"BTC\",\"winnerName\":\"Bitcoin\",\"winnerReturn\":12.5,\"includeInflation\":false}")]
    [InlineData("dca", "{\"includeInflation\":true,\"period\":\"monthly\",\"periodicAmount\":\"1000.25\"}")]
    [InlineData("portfolio", "{\"totalReturn\":8.2,\"includeInflation\":true,\"items\":[{\"assetSymbol\":\"BTC\",\"assetDisplayName\":\"Bitcoin\",\"amount\":\"1000\",\"amountType\":\"try\"}]}")]
    public void Validate_RepositoryProvenLegacyV1Payload_Accepts(string type, string json)
    {
        var extraData = Parse(json);

        var act = () => ScenarioExtraDataValidator.Validate(extraData, type, _localizer);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_Null_Accepts()
    {
        var act = () => ScenarioExtraDataValidator.Validate(null, "what_if", _localizer);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("true")]
    public void Validate_NonObjectRoot_Rejects(string json)
    {
        var act = () => ScenarioExtraDataValidator.Validate(Parse(json), "what_if", _localizer);

        act.Should().Throw<ValidationException>()
            .Where(ex => ex.Field == "ExtraData" && ex.Detail == "ExtraDataMustBeObject");
    }

    [Fact]
    public void Validate_ExtraDataUtf8Bytes_Exact8192Accepts_8193Rejects()
    {
        var exact = CreatePortfolioPayloadWithUtf8Size(ScenarioExtraDataValidator.MaxUtf8Bytes);
        var over = CreatePortfolioPayloadWithUtf8Size(ScenarioExtraDataValidator.MaxUtf8Bytes + 1);

        ScenarioExtraDataValidator.Validate(exact, "portfolio", _localizer);
        var act = () => ScenarioExtraDataValidator.Validate(over, "portfolio", _localizer);

        ScenarioExtraDataValidator.GetStorageUtf8Size(exact).Should().Be(8192);
        ScenarioExtraDataValidator.GetStorageUtf8Size(over).Should().Be(8193);
        act.Should().Throw<ValidationException>()
            .Where(ex => ex.Detail == "ExtraDataTooLarge");
    }

    [Fact]
    public void GetStorageUtf8Size_LiteralEmojiAndEscapedSurrogatePair_AreEquivalent()
    {
        var literal = Parse("{\"emoji\":\"🙂\"}");
        var escaped = Parse("{\"emoji\":\"\\uD83D\\uDE42\"}");
        var postgresText = "{\"emoji\": \"🙂\"}";

        ScenarioExtraDataValidator.GetStorageUtf8Size(literal)
            .Should().Be(Encoding.UTF8.GetByteCount(postgresText));
        ScenarioExtraDataValidator.GetStorageUtf8Size(escaped)
            .Should().Be(Encoding.UTF8.GetByteCount(postgresText));
    }

    [Fact]
    public void GetStorageUtf8Size_ControlCharacters_UsesPostgresEscapes()
    {
        var value = Parse("{\"c\":\"\\u0001\\b\\f\\n\\r\\t\\\"\\\\\"}");
        var postgresText = "{\"c\": \"\\u0001\\b\\f\\n\\r\\t\\\"\\\\\"}";

        ScenarioExtraDataValidator.GetStorageUtf8Size(value)
            .Should().Be(Encoding.UTF8.GetByteCount(postgresText));
    }

    [Fact]
    public void GetStorageUtf8Size_EmojiPropertyName_UsesDecodedUtf8()
    {
        var literal = Parse("{\"🙂\":true}");
        var escaped = Parse("{\"\\uD83D\\uDE42\":true}");
        var postgresText = "{\"🙂\": true}";

        ScenarioExtraDataValidator.GetStorageUtf8Size(literal)
            .Should().Be(Encoding.UTF8.GetByteCount(postgresText));
        ScenarioExtraDataValidator.GetStorageUtf8Size(escaped)
            .Should().Be(Encoding.UTF8.GetByteCount(postgresText));
    }

    [Theory]
    [InlineData("1.2300", "1.2300")]
    [InlineData("1.2300e2", "123.00")]
    [InlineData("1e-2", "0.01")]
    [InlineData("1.0e-2", "0.010")]
    [InlineData("1E+3", "1000")]
    [InlineData("-0.00", "0.00")]
    [InlineData("1e-100", "0.0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001")]
    [InlineData("123456789012345678901234567890.125", "123456789012345678901234567890.125")]
    public void GetStorageUtf8Size_DecimalAndExponent_UsesPostgresNumericText(
        string json,
        string postgresText)
    {
        ScenarioExtraDataValidator.GetStorageUtf8Size(Parse(json))
            .Should().Be(Encoding.UTF8.GetByteCount(postgresText));
    }

    [Fact]
    public void Validate_StringUtf8Bytes_Exact2048Accepts_OverBoundaryRejects()
    {
        // U+015F UTF-8'de iki byte: sınırın karakter değil gerçek UTF-8 byte
        // olduğunu kanıtlar (1024*2=2048, 1025*2=2050).
        var exact = Parse(JsonSerializer.Serialize(new { mode = string.Concat(Enumerable.Repeat("ş", 1024)) }));
        var over = Parse(JsonSerializer.Serialize(new { mode = string.Concat(Enumerable.Repeat("ş", 1025)) }));

        ScenarioExtraDataValidator.Validate(exact, "what_if", _localizer);
        var act = () => ScenarioExtraDataValidator.Validate(over, "what_if", _localizer);

        act.Should().Throw<ValidationException>()
            .Where(ex => ex.Detail == "ExtraDataStringTooLong");
    }

    [Fact]
    public void Validate_Depth8PassesBudget_Depth9Rejects()
    {
        var exact = Parse(BuildNestedObject(8));
        var over = Parse(BuildNestedObject(9));

        var exactAct = () => ScenarioExtraDataValidator.Validate(exact, "what_if", _localizer);
        var overAct = () => ScenarioExtraDataValidator.Validate(over, "what_if", _localizer);

        exactAct.Should().Throw<ValidationException>()
            .Where(ex => ex.Detail == "ExtraDataUnknownField",
                "depth=8 bütçeyi geçip legacy-v1 şema kontrolüne ulaşmalı");
        overAct.Should().Throw<ValidationException>()
            .Where(ex => ex.Detail == "ExtraDataTooDeep");
    }

    [Fact]
    public void Validate_TotalProperties_Exact64PassesBudget_65Rejects()
    {
        var exact = Parse(BuildFlatObject(propertyCount: 64));
        var over = Parse(BuildFlatObject(propertyCount: 65));

        var exactAct = () => ScenarioExtraDataValidator.Validate(exact, "what_if", _localizer);
        var overAct = () => ScenarioExtraDataValidator.Validate(over, "what_if", _localizer);

        exactAct.Should().Throw<ValidationException>()
            .Where(ex => ex.Detail == "ExtraDataUnknownField");
        overAct.Should().Throw<ValidationException>()
            .Where(ex => ex.Detail == "ExtraDataTooManyProperties");
    }

    [Fact]
    public void Validate_TotalNodes_Exact256PassesBudget_257Rejects()
    {
        var exact = Parse(BuildNodeBoundaryObject(arrayItems: 127));
        var over = Parse(BuildNodeBoundaryObject(arrayItems: 128));

        var exactAct = () => ScenarioExtraDataValidator.Validate(exact, "what_if", _localizer);
        var overAct = () => ScenarioExtraDataValidator.Validate(over, "what_if", _localizer);

        exactAct.Should().Throw<ValidationException>()
            .Where(ex => ex.Detail == "ExtraDataUnknownField");
        overAct.Should().Throw<ValidationException>()
            .Where(ex => ex.Detail == "ExtraDataTooManyNodes");
    }

    [Fact]
    public void Validate_TotalArrayItems_Exact128Accepts_129Rejects()
    {
        var exact = Parse(BuildPortfolioWithEmptyItems(128));
        var over = Parse(BuildPortfolioWithEmptyItems(129));

        ScenarioExtraDataValidator.Validate(exact, "portfolio", _localizer);
        var act = () => ScenarioExtraDataValidator.Validate(over, "portfolio", _localizer);

        act.Should().Throw<ValidationException>()
            .Where(ex => ex.Detail == "ExtraDataTooManyArrayItems");
    }

    [Theory]
    [InlineData("what_if", "{\"winnerReturn\":1}")]
    [InlineData("dca", "{\"mode\":\"reverse\"}")]
    [InlineData("portfolio", "{\"items\":[{\"privateNote\":\"sentinel\"}]}")]
    public void Validate_FieldOutsideTypeAllowlist_Rejects(string type, string json)
    {
        var act = () => ScenarioExtraDataValidator.Validate(Parse(json), type, _localizer);

        act.Should().Throw<ValidationException>()
            .Where(ex => ex.Detail == "ExtraDataUnknownField");
    }

    [Theory]
    [InlineData("what_if", "{\"includeInflation\":\"true\"}")]
    [InlineData("comparison", "{\"winnerReturn\":\"12.5\"}")]
    [InlineData("dca", "{\"period\":[]}")]
    [InlineData("portfolio", "{\"items\":{}}")]
    public void Validate_AllowlistedFieldWithWrongJsonType_Rejects(string type, string json)
    {
        var act = () => ScenarioExtraDataValidator.Validate(Parse(json), type, _localizer);

        act.Should().Throw<ValidationException>()
            .Where(ex => ex.Detail == "ExtraDataInvalidFieldType");
    }

    [Fact]
    public void Validate_NumberOutsideBoundedDecimalDomain_RejectsFailClosed()
    {
        var act = () => ScenarioExtraDataValidator.Validate(
            Parse("{\"winnerReturn\":1e10000}"),
            "comparison",
            _localizer);

        act.Should().Throw<ValidationException>()
            .Where(ex => ex.Detail == "ExtraDataTooLarge");
    }

    [Theory]
    [InlineData("{\"mode\":\"reverse\",\"mode\":\"normal\"}", "what_if")]
    [InlineData("{\"items\":[{\"amount\":\"1\",\"amount\":\"2\"}]}", "portfolio")]
    public void Validate_DuplicatePropertyAtRootOrPortfolioItem_Rejects(string json, string type)
    {
        var act = () => ScenarioExtraDataValidator.Validate(Parse(json), type, _localizer);

        act.Should().Throw<ValidationException>()
            .Where(ex => ex.Detail == "DuplicateJsonProperty");
    }

    private static IStringLocalizer<ErrorMessages> CreateLocalizer()
    {
        var localizer = Substitute.For<IStringLocalizer<ErrorMessages>>();
        localizer[Arg.Any<string>()]
            .Returns(call => new LocalizedString((string)call[0], (string)call[0]));
        return localizer;
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement CreatePortfolioPayloadWithUtf8Size(int targetBytes)
    {
        var fixedString = new string('a', 2048);
        var json = JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new
                {
                    assetSymbol = fixedString,
                    assetDisplayName = fixedString,
                    amount = fixedString,
                    amountType = string.Empty,
                }
            }
        });
        var remaining = targetBytes - ScenarioExtraDataValidator.GetStorageUtf8Size(Parse(json));
        remaining.Should().BeInRange(0, ScenarioExtraDataValidator.MaxStringUtf8Bytes);

        return Parse(JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new
                {
                    assetSymbol = fixedString,
                    assetDisplayName = fixedString,
                    amount = fixedString,
                    amountType = new string('b', remaining),
                }
            }
        }));
    }

    private static string BuildNestedObject(int totalDepth)
    {
        var json = "0";
        for (var i = 1; i < totalDepth; i++)
            json = $"{{\"x\":{json}}}";
        return json;
    }

    private static string BuildFlatObject(int propertyCount) =>
        "{" + string.Join(',', Enumerable.Range(0, propertyCount).Select(i => $"\"p{i}\":null")) + "}";

    private static string BuildNodeBoundaryObject(int arrayItems)
    {
        var properties = new List<string>
        {
            $"\"p0\":[{string.Join(',', Enumerable.Repeat("null", arrayItems))}]"
        };
        properties.AddRange(Enumerable.Range(1, 63).Select(i => $"\"p{i}\":null"));
        return "{" + string.Join(',', properties) + "}";
    }

    private static string BuildPortfolioWithEmptyItems(int itemCount) =>
        $"{{\"items\":[{string.Join(',', Enumerable.Repeat("{}", itemCount))}]}}";
}
