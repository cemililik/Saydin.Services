using System.Net;

namespace Saydin.PriceIngestion.Tests.Adapters;

/// <summary>
/// Adapter HTTP testleri için sıralı/şartlı yanıt üreten test handler'ı
/// (review P1R-004 / P1R-010 — `IHttpClientFactory` üzerinden inject edilir).
/// Her `SendAsync` çağrısında handler factory üzerinden `HttpResponseMessage`
/// üretir; çağrılar `Requests` listesinde toplanır, böylece testler
/// header / URL / sayım assert'leri yapabilir.
/// </summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = new();

    public int CallCount => Requests.Count;

    public static StubHttpMessageHandler Status(HttpStatusCode statusCode, string? content = null) =>
        new(_ =>
        {
            var response = new HttpResponseMessage(statusCode);
            if (content is not null)
                response.Content = new StringContent(content);
            return response;
        });

    public static StubHttpMessageHandler Ok(string content) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(responder(request));
    }
}
