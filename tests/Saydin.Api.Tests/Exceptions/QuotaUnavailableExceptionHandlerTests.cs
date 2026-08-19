using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Saydin.Api.Exceptions;
using Saydin.Api.Services;

namespace Saydin.Api.Tests.Exceptions;

public sealed class QuotaUnavailableExceptionHandlerTests
{
    [Fact]
    public async Task QuotaUnavailable_MapsToStable503ProblemDetails()
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "quota-trace";
        context.Response.Body = new MemoryStream();
        var handler = new QuotaUnavailableExceptionHandler();

        var handled = await handler.TryHandleAsync(
            context, new QuotaUnavailableException(), CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(503);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        document.RootElement.GetProperty("code").GetString()
            .Should().Be(QuotaUnavailableException.ErrorCode);
        document.RootElement.GetProperty("traceId").GetString().Should().Be("quota-trace");
        document.RootElement.ToString().Should().NotContain("Redis");
    }

    [Fact]
    public async Task UnrelatedException_IsNotHandled()
    {
        var context = new DefaultHttpContext();
        var handler = new QuotaUnavailableExceptionHandler();

        var handled = await handler.TryHandleAsync(
            context, new InvalidOperationException("sentinel"), CancellationToken.None);

        handled.Should().BeFalse();
    }
}
