using Microsoft.EntityFrameworkCore;
using Saydin.Shared.Constants;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;

namespace Saydin.Api.Repositories;

public sealed class InflationRepository(SaydinDbContext context) : IInflationRepository
{
    public async Task<(decimal? BuyIndex, DateOnly? BuyIndexDate, decimal? SellIndex, DateOnly? SellIndexDate)>
        GetIndexValuesAsync(DateOnly buyDate, DateOnly sellDate, CancellationToken ct)
    {
        // period_date her ayın 1'idir; LKV: period_date <= hedef ay
        var buyMonth  = new DateOnly(buyDate.Year,  buyDate.Month,  1);
        var sellMonth = new DateOnly(sellDate.Year, sellDate.Month, 1);

        var buyRow  = await GetNearestRowAsync(buyMonth,  ct);
        var sellRow = await GetNearestRowAsync(sellMonth, ct);

        return (buyRow?.IndexValue, buyRow?.PeriodDate, sellRow?.IndexValue, sellRow?.PeriodDate);
    }

    /// <summary>
    /// F2.7-5: composite PK (period_date, source) ile aynı ay için birden çok kaynak
    /// (seed-approximation + tuik) bulunabilir. En yakın (≤ <paramref name="month"/>) tarihi
    /// seçer ve aynı tarihte birden çok kaynak varsa gerçek TÜİK verisini
    /// (<see cref="InflationSources.Tuik"/>) yaklaşık seed'e tercih eder.
    /// </summary>
    private async Task<InflationRate?> GetNearestRowAsync(DateOnly month, CancellationToken ct) =>
        await context.InflationRates
            .Where(r => r.PeriodDate <= month)
            .OrderByDescending(r => r.PeriodDate)
            .ThenBy(r => r.Source == InflationSources.Tuik ? 0 : 1)
            .FirstOrDefaultAsync(ct);
}
