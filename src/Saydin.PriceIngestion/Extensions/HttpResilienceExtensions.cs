using Microsoft.Extensions.Http.Resilience;

namespace Saydin.PriceIngestion.Extensions;

/// <summary>
/// Tüm dış API <see cref="HttpClient"/>'larına tutarlı resilience pipeline'ı uygular
/// (review F1.1-1). CLAUDE.md "Dış API Adaptörleri" spesifikasyonu:
/// 3 retry (exponential backoff + jitter), 5 ardışık hata → circuit breaker,
/// her istekte 30s timeout.
/// </summary>
internal static class HttpResilienceExtensions
{
    public static IHttpClientBuilder AddSaydinResilience(this IHttpClientBuilder builder)
    {
        // PR #11 follow-up: HttpClient.Timeout'u devre dışı bırak. Polly pipeline'ı
        // AttemptTimeout (30s) + TotalRequestTimeout (3 dk) ile cancel kontrolünü
        // tek noktadan yürütüyor. HttpClient'ın default 100s timeout'u veya per-client
        // 30s ayarı pipeline'ı erken iptal edip retry zincirini bozabilir; özellikle
        // backoff sırasında pencere açıldığında. ConfigureHttpClient delegate'i
        // AddHttpClient lambda'sından SONRA çalıştığı için per-client Timeout
        // ayarlarının üzerine yazılır.
        builder.ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);

        builder.AddStandardResilienceHandler(opts =>
        {
            // Retry: 3 deneme, exponential backoff (varsayılan delay backoff),
            // jitter ile thundering-herd riski azalır.
            opts.Retry.MaxRetryAttempts = 3;
            opts.Retry.BackoffType = Polly.DelayBackoffType.Exponential;
            opts.Retry.UseJitter = true;

            // Per-attempt timeout: CLAUDE.md 30s.
            opts.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);

            // Sampling duration AttemptTimeout'un en az 2 katı olmalı (framework kuralı).
            opts.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120);
            opts.CircuitBreaker.MinimumThroughput = 5;
            // FailureRatio=1.0 → SamplingDuration boyunca tüm istekler (en az
            // MinimumThroughput) başarısızsa devre açılır; CLAUDE.md spec'in
            // "5 ardışık hata" semantiğine en yakın yaklaşım.
            opts.CircuitBreaker.FailureRatio = 1.0;

            // Toplam istek timeout'u retry zincirini de kapsamalı: 4 attempt * 30s + backoff.
            opts.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(3);
        });
        return builder;
    }
}
