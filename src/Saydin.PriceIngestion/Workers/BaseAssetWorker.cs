using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;

namespace Saydin.PriceIngestion.Workers;

/// <summary>
/// Tüm asset worker'larının ortak backfill + zamanlama mantığı.
/// Her worker BackfillStartDate, ChunkDays ve DailyRunUtcTime'ı override eder.
/// </summary>
public abstract class BaseAssetWorker(
    IExternalPriceAdapter adapter,
    IPriceIngestionRepository repository,
    ILogger logger)
{
    protected abstract DateOnly BackfillStartDate { get; }
    protected abstract int ChunkDays { get; }
    protected abstract TimeOnly DailyRunUtcTime { get; }

    public async Task RunAsync(CancellationToken ct)
    {
        await BackfillAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            var delay = GetDelayUntilNextRun();
            logger.LogInformation("{Source} sonraki çekim: {NextRun:HH:mm} UTC ({Delay:hh\\:mm} içinde)",
                adapter.Source, DateTime.UtcNow.Add(delay), delay);

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

    private async Task BackfillAsync(CancellationToken ct)
    {
        var assets = await repository.GetActiveAssetsBySourceAsync(adapter.Source, ct);

        foreach (var asset in assets)
        {
            var latestDate = await repository.GetLatestPriceDateAsync(asset.Id, ct);
            var from = latestDate?.AddDays(1) ?? BackfillStartDate;
            var to = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));

            if (from > to)
            {
                logger.LogInformation("{Symbol} için backfill gerekmiyor (mevcut: {Latest})",
                    asset.Symbol, latestDate);
                continue;
            }

            logger.LogInformation("{Symbol} backfill başlıyor: {From} → {To}", asset.Symbol, from, to);

            var chunkFrom = from;
            while (chunkFrom <= to && !ct.IsCancellationRequested)
            {
                var chunkTo = chunkFrom.AddDays(ChunkDays - 1);
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

    private async Task FetchAndUpsertAsync(Shared.Entities.Asset asset, DateOnly from, DateOnly to, CancellationToken ct)
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

    private TimeSpan GetDelayUntilNextRun()
    {
        var now = DateTime.UtcNow;
        var todayScheduled = now.Date.Add(DailyRunUtcTime.ToTimeSpan());
        return now < todayScheduled ? todayScheduled - now : todayScheduled.AddDays(1) - now;
    }
}
