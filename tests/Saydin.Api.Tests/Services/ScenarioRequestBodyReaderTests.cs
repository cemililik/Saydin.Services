using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Saydin.Api.Exceptions;
using Saydin.Api.Services;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Tests.Services;

public sealed class ScenarioRequestBodyReaderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IStringLocalizer<ErrorMessages> _localizer = CreateLocalizer();

    [Fact]
    public async Task ReadAsync_ContentLengthExactly32KiB_Accepts()
    {
        var json = BuildRequestWithExactUtf8Size(ScenarioRequestBodyReader.MaxBodyBytes);
        var request = CreateRequest(json, includeContentLength: true);

        var result = await ScenarioRequestBodyReader.ReadAsync(
            request, JsonOptions, _localizer, CancellationToken.None);

        Encoding.UTF8.GetByteCount(json).Should().Be(32 * 1024);
        result.AssetSymbol.Should().Be("BTC");
    }

    [Fact]
    public async Task ReadAsync_ContentLengthOver32KiB_RejectsBeforeReading()
    {
        var json = BuildRequestWithExactUtf8Size(ScenarioRequestBodyReader.MaxBodyBytes + 1);
        var request = CreateRequest(json, includeContentLength: true);

        var act = async () => await ScenarioRequestBodyReader.ReadAsync(
            request, JsonOptions, _localizer, CancellationToken.None);

        await act.Should().ThrowAsync<RequestBodyTooLargeException>()
            .Where(ex => ex.MaxBytes == 32 * 1024);
        request.Body.Position.Should().Be(0, "known oversized Content-Length should short-circuit");
    }

    [Fact]
    public async Task ReadAsync_ChunkedBodyWithoutContentLengthOver32KiB_RejectsWhileReading()
    {
        var json = BuildRequestWithExactUtf8Size(ScenarioRequestBodyReader.MaxBodyBytes + 1);
        var request = CreateRequest(json, includeContentLength: false);

        var act = async () => await ScenarioRequestBodyReader.ReadAsync(
            request, JsonOptions, _localizer, CancellationToken.None);

        await act.Should().ThrowAsync<RequestBodyTooLargeException>()
            .Where(ex => ex.MaxBytes == 32 * 1024);
    }

    [Fact]
    public async Task ReadAsync_Utf8Bom_UsesTheSameJsonContractAndAccepts()
    {
        const string json = "{\"assetSymbol\":\"BTC\",\"assetDisplayName\":\"Bitcoin\",\"buyDate\":\"2020-01-01\",\"amount\":100,\"amountType\":\"try\"}";
        var context = new DefaultHttpContext();
        var payload = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(json)).ToArray();
        context.Request.Body = new MemoryStream(payload);
        context.Request.ContentType = "application/json; charset=utf-8";

        var result = await ScenarioRequestBodyReader.ReadAsync(
            context.Request, JsonOptions, _localizer, CancellationToken.None);

        result.AssetSymbol.Should().Be("BTC");
    }

    [Theory]
    [InlineData("{\"assetSymbol\":")]
    [InlineData("")]
    [InlineData("null")]
    public async Task ReadAsync_MalformedEmptyOrNullBody_ReturnsDomainValidation(string json)
    {
        var request = CreateRequest(json, includeContentLength: false);

        var act = async () => await ScenarioRequestBodyReader.ReadAsync(
            request, JsonOptions, _localizer, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
            .Where(ex => ex.Field == "request");
    }

    [Theory]
    [InlineData("{\"assetSymbol\":\"BTC\",\"assetSymbol\":\"ETH\"}")]
    [InlineData("{\"extraData\":{\"mode\":\"reverse\"},\"ExtraData\":{\"mode\":\"normal\"}}")]
    [InlineData("{\"extraData\":{\"mode\":\"reverse\",\"mode\":\"normal\"}}")]
    [InlineData("{\"extraData\":{\"items\":[{\"amount\":\"1\",\"amount\":\"2\"}]}}")]
    public async Task ReadAsync_DuplicatePropertyAtAnyObjectLevel_Rejects(string json)
    {
        var request = CreateRequest(json, includeContentLength: false);

        var act = async () => await ScenarioRequestBodyReader.ReadAsync(
            request, JsonOptions, _localizer, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
            .Where(ex => ex.Field == "request" && ex.Detail == "DuplicateJsonProperty");
    }

    [Fact]
    public async Task ReadAsync_EndpointJsonDepthBoundary_AcceptsExactAndRejectsOneMore()
    {
        var exact = CreateRequest(
            BuildRequestWithNestedPadding(ScenarioRequestBodyReader.MaxRequestJsonDepth - 1),
            includeContentLength: false);
        var over = CreateRequest(
            BuildRequestWithNestedPadding(ScenarioRequestBodyReader.MaxRequestJsonDepth),
            includeContentLength: false);

        var accepted = async () => await ScenarioRequestBodyReader.ReadAsync(
            exact, JsonOptions, _localizer, CancellationToken.None);
        var rejected = async () => await ScenarioRequestBodyReader.ReadAsync(
            over, JsonOptions, _localizer, CancellationToken.None);

        await accepted.Should().NotThrowAsync();
        await rejected.Should().ThrowAsync<ValidationException>()
            .Where(ex => ex.Field == "request" && ex.Detail == "MalformedJsonBody");
    }

    private static HttpRequest CreateRequest(string body, bool includeContentLength)
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/json; charset=utf-8";
        context.Request.ContentLength = includeContentLength ? bytes.Length : null;
        return context.Request;
    }

    private static string BuildRequestWithExactUtf8Size(int targetBytes)
    {
        var baseJson = JsonSerializer.Serialize(new
        {
            assetSymbol = "BTC",
            assetDisplayName = "Bitcoin",
            buyDate = "2020-01-01",
            sellDate = (string?)null,
            amount = 100,
            amountType = "try",
            padding = string.Empty,
        });
        var paddingLength = targetBytes - Encoding.UTF8.GetByteCount(baseJson);
        paddingLength.Should().BeGreaterThanOrEqualTo(0);

        var result = JsonSerializer.Serialize(new
        {
            assetSymbol = "BTC",
            assetDisplayName = "Bitcoin",
            buyDate = "2020-01-01",
            sellDate = (string?)null,
            amount = 100,
            amountType = "try",
            padding = new string('p', paddingLength),
        });
        Encoding.UTF8.GetByteCount(result).Should().Be(targetBytes);
        return result;
    }

    private static string BuildRequestWithNestedPadding(int nestedArrays)
    {
        var nested = "0";
        for (var i = 0; i < nestedArrays; i++)
            nested = $"[{nested}]";

        return $$"""
                 {"assetSymbol":"BTC","assetDisplayName":"Bitcoin","buyDate":"2020-01-01","amount":100,"amountType":"try","padding":{{nested}}}
                 """;
    }

    private static IStringLocalizer<ErrorMessages> CreateLocalizer()
    {
        var localizer = Substitute.For<IStringLocalizer<ErrorMessages>>();
        localizer[Arg.Any<string>()]
            .Returns(call => new LocalizedString((string)call[0], (string)call[0]));
        return localizer;
    }
}
