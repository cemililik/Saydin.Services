using System.Collections.Frozen;
using Saydin.Api.Repositories;
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
    Asset? Lookup(IReadOnlyList<Asset> assets, string symbol, AssetCatalogVersion catalogVersion);
}

public sealed class AssetSymbolIndex : IAssetSymbolIndex
{
    private Snapshot? _current;

    public Asset? Lookup(IReadOnlyList<Asset> assets, string symbol)
        => LookupCore(assets, symbol, $"legacy:{ComputeSignature(assets)}");

    public Asset? Lookup(
        IReadOnlyList<Asset> assets,
        string symbol,
        AssetCatalogVersion catalogVersion)
    {
        ArgumentNullException.ThrowIfNull(catalogVersion);
        return LookupCore(assets, symbol, catalogVersion.Token);
    }

    private Asset? LookupCore(IReadOnlyList<Asset> assets, string symbol, string signature)
    {
        var upper = symbol.ToUpperInvariant();

        var snapshot = Volatile.Read(ref _current);
        if (snapshot is null || !string.Equals(snapshot.Signature, signature, StringComparison.Ordinal))
        {
            // Return the snapshot built from this call's list even if another
            // catalog publication wins the singleton swap immediately afterward.
            // Re-reading the global slot here can cross catalog versions.
            var built = new Snapshot(signature, BuildIndex(assets));
            Interlocked.Exchange(ref _current, built);
            snapshot = built;
        }

        return snapshot is not null && snapshot.Index.TryGetValue(upper, out var found)
            ? found
            : null;
    }

    private static FrozenDictionary<string, Asset> BuildIndex(IReadOnlyList<Asset> assets)
    {
        // F8 follow-up: Lookup `symbol.ToUpperInvariant()` ile sorguluyordu ama
        // BuildIndex raw `a.Symbol` ile saklıyordu. AssetConfiguration sembolü
        // normalize etmiyor → mixed-case asset satırı pratikte kalıcı cache miss'e
        // yol açıyordu. Burada da ToUpperInvariant ile saklarız; Lookup ile birebir uyum.
        // Aynı sembolü taşıyan iki satır pratikte yok (Asset.Symbol UNIQUE) — ama
        // savunma: son giren kazanır.
        var dict = new Dictionary<string, Asset>(assets.Count, StringComparer.Ordinal);
        foreach (var a in assets)
            dict[a.Symbol.ToUpperInvariant()] = a;
        return dict.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static int ComputeSignature(IReadOnlyList<Asset> assets)
    {
        // SVCR-001/002/003 follow-up (Codacy uyarısı): HashCode.Combine sıraya
        // duyarlıdır. PostgreSQL `ORDER BY` belirtilmediği için VACUUM / UPDATE
        // sonrası dönen sıra değişebilir → aynı içerik, farklı imza → gereksiz
        // FrozenDictionary yeniden inşası.
        //
        // XOR'lu order-independent imza: her asset için içerik hash'i tek tek
        // hesapla, sonuçları XOR ile birleştir. Listenin uzunluğunu Count haline
        // dahil et ki delete-then-add senaryosu aynı hash'e düşmesin.
        var hash = 0;
        foreach (var a in assets)
            hash ^= HashCode.Combine(a.Symbol, a.IsActive, a.DisplayName, a.Category);
        return HashCode.Combine(hash, assets.Count);
    }

    private sealed record Snapshot(string Signature, FrozenDictionary<string, Asset> Index);
}
