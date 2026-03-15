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
    public DateOnly? DataAvailableFrom { get; init; }
    public DateOnly? DataAvailableTo { get; init; }
}
