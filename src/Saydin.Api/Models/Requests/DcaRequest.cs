namespace Saydin.Api.Models.Requests;

public record DcaRequest(
    string   AssetSymbol,
    DateOnly StartDate,
    DateOnly? EndDate,
    decimal  PeriodicAmount,
    string   Period,           // "weekly" | "monthly"
    string   AmountType,       // "try"
    bool     IncludeInflation = false
);
