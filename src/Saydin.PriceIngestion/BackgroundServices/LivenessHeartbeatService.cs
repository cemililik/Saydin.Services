using System.Diagnostics.CodeAnalysis;

namespace Saydin.PriceIngestion.BackgroundServices;

/// <summary>
/// Worker süreci canlılığını dosya üzerinden yayımlar (review F1.7-6).
/// Docker HEALTHCHECK <see cref="DefaultHeartbeatPath"/> dosyasının yakın geçmişte
/// dokunulmuş olup olmadığına bakar — process tamamen donmuş veya orchestrator
/// `Task.WhenAll` ile kilitlenmişse mtime güncellenmez ve container unhealthy olur.
///
/// PR #11 follow-up (Sonar S5443): Dosya yolu artık `LivenessProbe:HeartbeatPath`
/// konfigürasyonu ile override edilebilir. Default değer container-scoped
/// `/tmp/saydin-ingestion-healthy` — image non-root `appuser` ile çalışır ve
/// container filesystem'i ephemeral / single-tenant olduğu için `/tmp` üzerinde
/// race-condition saldırı yüzeyi yok. Yine de paranoyak ortamlar için operasyon
/// ekibi `LivenessProbe__HeartbeatPath=/var/run/saydin/ingestion-healthy` gibi
/// dedicated bir path verebilir; Dockerfile ve compose HEALTHCHECK'lerinin aynı
/// path'i kullanması gerekir.
/// </summary>
public sealed class LivenessHeartbeatService : BackgroundService
{
    public const string DefaultHeartbeatPath = "/tmp/saydin-ingestion-healthy";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly string _heartbeatPath;
    private readonly ILogger<LivenessHeartbeatService> _logger;

    [SuppressMessage("Security", "S5443:Make sure publicly writable directories are used safely here.",
        Justification = "Container-scoped /tmp default; image non-root appuser ile çalışır, " +
                        "filesystem single-tenant ve ephemeral. Operasyon ekibi LivenessProbe:HeartbeatPath " +
                        "config'i ile alternate path verebilir.")]
    public LivenessHeartbeatService(IConfiguration configuration, ILogger<LivenessHeartbeatService> logger)
    {
        _logger = logger;
        _heartbeatPath = configuration.GetValue<string>("LivenessProbe:HeartbeatPath")
                         ?? DefaultHeartbeatPath;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TouchSafe();
        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                TouchSafe();
            }
        }
        catch (OperationCanceledException ex)
        {
            // host shutdown — beklenen, ama operasyon ekibinin "heartbeat ne zaman durdu?"
            // sorusunu cevaplayabilmesi için Debug seviyesinde exception ile kaydedilir
            // (stack trace shutdown diagnostiklerinde korunur).
            _logger.LogDebug(ex, "LivenessHeartbeatService host shutdown ile durdu");
        }
    }

    private void TouchSafe()
    {
        try
        {
            if (!File.Exists(_heartbeatPath))
            {
                File.WriteAllText(_heartbeatPath, DateTimeOffset.UtcNow.ToString("O"));
            }
            else
            {
                File.SetLastWriteTimeUtc(_heartbeatPath, DateTime.UtcNow);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Heartbeat dosyası güncellenemedi: {Path}", _heartbeatPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Heartbeat dosyasına yazma izni yok: {Path}", _heartbeatPath);
        }
    }
}
