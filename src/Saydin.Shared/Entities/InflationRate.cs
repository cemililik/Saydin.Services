using Saydin.Shared.Constants;

namespace Saydin.Shared.Entities;

/// <summary>
/// Aylık TÜFE endeks değeri (TÜİK, 2003=100 bazlı).
/// period_date her ayın 1. günüdür.
/// Reel getiri: (satış_endeks / alış_endeks) - 1
/// F2.7-5: (period_date, source) composite PK — aynı ay için seed + tuik bir arada.
/// </summary>
public sealed class InflationRate
{
    public DateOnly        PeriodDate { get; set; }
    public decimal         IndexValue { get; set; }
    public string          Source     { get; set; } = InflationSources.Tuik;
    public DateTimeOffset  CreatedAt  { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset  UpdatedAt  { get; set; } = DateTimeOffset.UtcNow;
}
