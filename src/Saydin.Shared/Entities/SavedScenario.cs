using System.Text.Json;

namespace Saydin.Shared.Entities;

public sealed class SavedScenario
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public Guid? AssetId { get; init; }
    public string AssetSymbol { get; init; } = default!;
    public string AssetDisplayName { get; init; } = default!;
    public string Type { get; init; } = "what_if";
    public JsonElement? ExtraData { get; init; }
    public DateOnly BuyDate { get; init; }
    public DateOnly? SellDate { get; init; }
    public decimal Quantity { get; init; }
    public string QuantityUnit { get; init; } = default!;
    public string? Label { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    // Navigation
    public User User { get; init; } = default!;
    public Asset? Asset { get; init; }
}
