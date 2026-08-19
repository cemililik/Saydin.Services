using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Saydin.PriceIngestion.Adapters;

namespace Saydin.PriceIngestion.Tests.Adapters;

public sealed class ProviderPayloadTests
{
    [Fact]
    public async Task Exact64KiB_IsAcceptedAndHashed()
    {
        using var content = new ByteArrayContent(new byte[65_536]);
        var payload = await BoundedHttpContent.ReadAsync(content, default);
        payload.Bytes.Should().HaveCount(65_536);
        payload.Sha256.Should().HaveCount(32);
    }

    [Fact]
    public async Task Chunked64KiBPlusOne_IsRejectedWithoutContentLength()
    {
        using var content = new UnknownLengthContent(new byte[65_537]);
        content.Headers.ContentLength.Should().BeNull();

        var act = () => BoundedHttpContent.ReadAsync(content, default);

        await act.Should().ThrowAsync<ProviderPayloadTooLargeException>();
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
