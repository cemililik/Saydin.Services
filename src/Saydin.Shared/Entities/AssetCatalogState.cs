namespace Saydin.Shared.Entities;

/// <summary>
/// Database-authoritative version and canonical digest of the asset catalog.
/// The table is a singleton whose key is always <c>1</c>.
/// </summary>
public sealed class AssetCatalogState
{
    public short Singleton { get; init; } = 1;
    public long Revision { get; init; }
    public byte[] CatalogSha256 { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; }
}
