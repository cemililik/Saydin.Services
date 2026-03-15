using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Workers;

/// <summary>
/// TCMB döviz kurlarını çeken worker.
/// Başlangıçta eksik günleri backfill eder, ardından her gün 16:30 Türkiye saatinde (13:30 UTC) çalışır.
/// </summary>
public sealed class TcmbWorker(
    IExternalPriceAdapter adapter,
    IPriceIngestionRepository repository,
    ILogger<TcmbWorker> logger)
{
    // 16:30 Türkiye = 13:30 UTC (Türkiye UTC+3, DST kullanmıyor — 2016'dan beri)
    private static readonly TimeOnly ScheduledUtcTime = new(13, 30, 0);

    public async Task RunAsync(CancellationToken ct)
    {
        logger.LogInformation("TcmbWorker başlatıldı");

        // İlk çalışmada eksik günleri tamamla
        await BackfillAsync(ct);

        // Günlük zamanlama döngüsü
        while (!ct.IsCancellationRequested)
        {
            var delay = GetDelayUntilNextRun();
            logger.LogInformation("TCMB sonraki çekim: {NextRun:HH:mm} UTC ({Delay:hh\\:mm} içinde)",
                DateTime.UtcNow.Add(delay), delay);

            try
            {
                await Task.Delay(delay, ct);
                await FetchTodayAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // Backfill başlangıç tarihi — TCMB 1995'ten itibaren yayında; 2010 makul MVP başlangıcı
    private static readonly DateOnly BackfillStart = new(2010, 1, 1);

    // Chunk boyutu: ara ara kaydet, ilerleme logla, bellek baskısı azalt
    private const int BackfillChunkDays = 90;

    private async Task BackfillAsync(CancellationToken ct)
    {
        var assets = await repository.GetActiveAssetsBySourceAsync(adapter.Source, ct);

        foreach (var asset in assets)
        {
            var latestDate = await repository.GetLatestPriceDateAsync(asset.Id, ct);
            var from = latestDate?.AddDays(1) ?? BackfillStart;
            var to = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1)); // dün

            if (from > to)
            {
                logger.LogInformation("{Symbol} için backfill gerekmiyor (mevcut: {Latest})",
                    asset.Symbol, latestDate);
                continue;
            }

            logger.LogInformation("{Symbol} backfill başlıyor: {From} → {To}", asset.Symbol, from, to);

            // Chunk'lar halinde işle — her 90 günde bir kaydet
            var chunkFrom = from;
            while (chunkFrom <= to && !ct.IsCancellationRequested)
            {
                var chunkTo = chunkFrom.AddDays(BackfillChunkDays - 1);
                if (chunkTo > to) chunkTo = to;

                await FetchAndUpsertAsync(asset, chunkFrom, chunkTo, ct);
                chunkFrom = chunkTo.AddDays(1);
            }
        }
    }

    private async Task FetchTodayAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var assets = await repository.GetActiveAssetsBySourceAsync(adapter.Source, ct);

        foreach (var asset in assets)
            await FetchAndUpsertAsync(asset, today, today, ct);
    }

    private async Task FetchAndUpsertAsync(Asset asset, DateOnly from, DateOnly to, CancellationToken ct)
    {
        try
        {
            var points = await adapter.FetchRangeAsync(
                asset.Id, asset.Symbol, asset.SourceId ?? string.Empty, from, to, ct);

            if (points.Count == 0)
            {
                logger.LogInformation("{Symbol}: {From}–{To} arasında alınacak veri yok", asset.Symbol, from, to);
                return;
            }

            await repository.UpsertPricePointsAsync(points, ct);

            logger.LogInformation("{Symbol}: {Count} fiyat noktası kaydedildi ({From}–{To})",
                asset.Symbol, points.Count, from, to);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Symbol} veri çekimi başarısız ({From}–{To})", asset.Symbol, from, to);
        }
    }

    private static TimeSpan GetDelayUntilNextRun()
    {
        var now = DateTime.UtcNow;
        var todayScheduled = now.Date.Add(ScheduledUtcTime.ToTimeSpan());

        return now < todayScheduled
            ? todayScheduled - now
            : todayScheduled.AddDays(1) - now;
    }
}
