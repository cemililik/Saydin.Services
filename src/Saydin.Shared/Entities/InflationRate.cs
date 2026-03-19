namespace Saydin.Shared.Entities;

/// <summary>
/// Aylık TÜFE endeks değeri (TÜİK, 2003=100 bazlı).
/// period_date her ayın 1. günüdür.
/// Reel getiri: (satış_endeks / alış_endeks) - 1
/// </summary>
public sealed class InflationRate
{
    public DateOnly        PeriodDate { get; set; }
    public decimal         IndexValue { get; set; }
    public string          Source     { get; set; } = "tuik";
    public DateTimeOffset  CreatedAt  { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset  UpdatedAt  { get; set; } = DateTimeOffset.UtcNow;
}
