using System.Text.Json;

namespace Saydin.Shared.Entities;

public sealed class Asset
{
    public Guid Id { get; init; }
    public string Symbol { get; init; } = default!;
    public string DisplayName { get; init; } = default!;
    public AssetCategory Category { get; init; }
    public bool IsActive { get; init; }
    public string Source { get; init; } = default!;
    public string? SourceId { get; init; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// JSONB metadata: <c>decimal_places</c>, <c>display_unit</c>, <c>lot_size</c>,
    /// (TCMB için) <c>unit_multiplier</c> gibi serbest biçimli ek bilgiler.
    /// EF Core <c>jsonb</c> tipiyle map edilir; null kabul edilir.
    /// </summary>
    public JsonElement? Metadata { get; init; }

    // Navigation
    public ICollection<PricePoint> PricePoints { get; init; } = [];
}
