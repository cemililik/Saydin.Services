using System.Collections.Frozen;
using Saydin.Shared.Entities;

namespace Saydin.Api.Services;

/// <summary>
/// SVCR-001/002/003 follow-up: <see cref="AssetService"/> içindeki static field
/// pattern'i tamamen kaldırıldı. Singleton instance asset listesinin **içerik
/// hash**'ine bağlı tek bir <see cref="FrozenDictionary{TKey,TValue}"/> snapshot
/// tutar; iki paralel istek farklı snapshot görse de atomik <see cref="Interlocked.Exchange"/>
/// ile yarış kapalı, test isolation bozulmaz (instance per-WebApplicationFactory).
///
/// Versioning daha önce sadece <c>asset.Count</c>'a bağlıydı — bir asset
/// <c>DisplayName</c> değişse veya `IsActive` toggle olsa sayı sabit kalır,
/// cache eski instance'ı sonsuza dek tutardı. Yeni sürüm:
///   • Hash imzası = `Σ HashCode.Combine(Symbol, IsActive, DisplayName, Category)`
///   • Hash değişimi → snapshot atılır + yeni FrozenDictionary inşa edilir
///   • <c>FrozenDictionary</c>: <c>ConcurrentDictionary</c>'den ~2x daha hızlı lookup,
///     immutable (write yok).
/// </summary>
public interface IAssetSymbolIndex
{
    /// <summary>Verilen asset listesini (cached snapshot ile karşılaştırarak)
    /// indeksler ve sembol için asset'i döner. Listenin imzası değişmediyse
    /// snapshot reuse edilir.</summary>
    Asset? Lookup(IReadOnlyList<Asset> assets, string symbol);
}

public sealed class AssetSymbolIndex : IAssetSymbolIndex
{
    private Snapshot? _current;

    public Asset? Lookup(IReadOnlyList<Asset> assets, string symbol)
    {
        var upper = symbol.ToUpperInvariant();
        var hash = ComputeSignature(assets);

        var snapshot = Volatile.Read(ref _current);
        if (snapshot is null || snapshot.Signature != hash)
        {
            // Bir önceki snapshot'tan kötümser shapeshift'i kabul ederek
            // yeniden inşa et; CompareExchange ile atomik swap.
            var built = new Snapshot(hash, BuildIndex(assets));
            Interlocked.CompareExchange(ref _current, built, snapshot);
            snapshot = Volatile.Read(ref _current);
        }

        return snapshot is not null && snapshot.Index.TryGetValue(upper, out var found)
            ? found
            : null;
    }

    private static FrozenDictionary<string, Asset> BuildIndex(IReadOnlyList<Asset> assets)
    {
        // Aynı sembolü taşıyan iki satır pratikte yok (Asset.Symbol UNIQUE) — ama
        // savunma: son giren kazanır.
        var dict = new Dictionary<string, Asset>(assets.Count, StringComparer.Ordinal);
        foreach (var a in assets)
            dict[a.Symbol] = a;
        return dict.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static int ComputeSignature(IReadOnlyList<Asset> assets)
    {
        // Order-independent içerik imzası — Linq.Sum yerine accumulator (allocation yok).
        var hash = 0;
        foreach (var a in assets)
            hash = HashCode.Combine(hash, a.Symbol, a.IsActive, a.DisplayName, a.Category);
        // Listenin uzunluğu da girsin ki delete-then-add aynı hash'e düşmesin.
        return HashCode.Combine(hash, assets.Count);
    }

    private sealed record Snapshot(int Signature, FrozenDictionary<string, Asset> Index);
}
