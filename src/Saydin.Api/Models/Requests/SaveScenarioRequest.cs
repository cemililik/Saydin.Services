using System.Text.Json;

namespace Saydin.Api.Models.Requests;

public record SaveScenarioRequest(
    string AssetSymbol,
    string AssetDisplayName,
    DateOnly BuyDate,
    DateOnly? SellDate,
    decimal Amount,
    string AmountType,
    string? Label,
    string Type = "what_if",
    JsonElement? ExtraData = null
);
