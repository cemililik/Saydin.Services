namespace Saydin.PriceIngestion.BackgroundServices;

/// <summary>
/// Worker süreci canlılığını dosya üzerinden yayımlar (review F1.7-6).
/// Docker HEALTHCHECK <see cref="HeartbeatPath"/> dosyasının yakın geçmişte
/// dokunulmuş olup olmadığına bakar — process tamamen donmuş veya orchestrator
/// `Task.WhenAll` ile kilitlenmişse mtime güncellenmez ve container unhealthy olur.
/// </summary>
public sealed class LivenessHeartbeatService(ILogger<LivenessHeartbeatService> logger) : BackgroundService
{
    public const string HeartbeatPath = "/tmp/saydin-ingestion-healthy";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

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
        catch (OperationCanceledException)
        {
            // host shutdown — beklenen
        }
    }

    private void TouchSafe()
    {
        try
        {
            if (!File.Exists(HeartbeatPath))
            {
                File.WriteAllText(HeartbeatPath, DateTimeOffset.UtcNow.ToString("O"));
            }
            else
            {
                File.SetLastWriteTimeUtc(HeartbeatPath, DateTime.UtcNow);
            }
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Heartbeat dosyası güncellenemedi: {Path}", HeartbeatPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Heartbeat dosyasına yazma izni yok: {Path}", HeartbeatPath);
        }
    }
}
