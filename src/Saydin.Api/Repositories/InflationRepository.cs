using Microsoft.EntityFrameworkCore;
using Saydin.Shared.Constants;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;

namespace Saydin.Api.Repositories;

public sealed class InflationRepository(SaydinDbContext context) : IInflationRepository
{
    public async Task<(InflationIndexObservation? Buy, InflationIndexObservation? Sell)>
        GetIndexValuesAsync(DateOnly buyDate, DateOnly sellDate, CancellationToken ct)
    {
        // period_date her ayın 1'idir; LKV: period_date <= hedef ay
        var buyMonth  = new DateOnly(buyDate.Year,  buyDate.Month,  1);
        var sellMonth = new DateOnly(sellDate.Year, sellDate.Month, 1);

        var buyRow  = await GetNearestRowAsync(buyMonth,  ct);
        var sellRow = await GetNearestRowAsync(sellMonth, ct);

        return (ToObservation(buyRow), ToObservation(sellRow));
    }

    public async Task<IReadOnlyDictionary<DateOnly, InflationIndexObservation>> GetExactIndexValuesAsync(
        IReadOnlyCollection<DateOnly> months,
        CancellationToken ct)
    {
        if (months.Count == 0)
            return new Dictionary<DateOnly, InflationIndexObservation>();

        var normalizedMonths = months
            .Select(month => new DateOnly(month.Year, month.Month, 1))
            .Distinct()
            .ToArray();

        var rows = await context.InflationRates
            .AsNoTracking()
            .WhereCompleteFinalAuthority()
            .Where(rate => normalizedMonths.Contains(rate.PeriodDate))
            .ToListAsync(ct);

        return rows
            .ToDictionary(rate => rate.PeriodDate, rate => ToObservation(rate)!);
    }

    /// <summary>
    /// En yakın (≤ <paramref name="month"/>) complete final EVDS/TÜİK CPI gözlemini seçer.
    /// Migration 020 expand fazında bırakılan all-null seed/legacy satırlar görünmez.
    /// </summary>
    private async Task<InflationRate?> GetNearestRowAsync(DateOnly month, CancellationToken ct) =>
        await context.InflationRates
            .AsNoTracking()
            .WhereCompleteFinalAuthority()
            .Where(r => r.PeriodDate <= month)
            .OrderByDescending(r => r.PeriodDate)
            .FirstOrDefaultAsync(ct);

    private static InflationIndexObservation? ToObservation(InflationRate? rate) =>
        rate is null
            ? null
            : new InflationIndexObservation(
                rate.PeriodDate, rate.IndexValue, FinalObservationAuthority.ToValue(rate));
}
