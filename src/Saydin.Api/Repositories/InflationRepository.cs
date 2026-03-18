using Microsoft.EntityFrameworkCore;
using Saydin.Shared.Data;

namespace Saydin.Api.Repositories;

public sealed class InflationRepository(SaydinDbContext context) : IInflationRepository
{
    public async Task<(decimal? BuyIndex, DateOnly? BuyIndexDate, decimal? SellIndex, DateOnly? SellIndexDate)>
        GetIndexValuesAsync(DateOnly buyDate, DateOnly sellDate, CancellationToken ct)
    {
        // period_date her ayın 1'idir; LKV: period_date <= hedef ay
        var buyMonth  = new DateOnly(buyDate.Year,  buyDate.Month,  1);
        var sellMonth = new DateOnly(sellDate.Year, sellDate.Month, 1);

        var buyRow = await context.InflationRates
            .Where(r => r.PeriodDate <= buyMonth)
            .OrderByDescending(r => r.PeriodDate)
            .FirstOrDefaultAsync(ct);

        var sellRow = await context.InflationRates
            .Where(r => r.PeriodDate <= sellMonth)
            .OrderByDescending(r => r.PeriodDate)
            .FirstOrDefaultAsync(ct);

        return (buyRow?.IndexValue, buyRow?.PeriodDate, sellRow?.IndexValue, sellRow?.PeriodDate);
    }
}
