using System.Collections.Concurrent;

namespace Saydin.Api.Services;

/// <summary>
/// `users.last_seen_at` UPDATE'lerini cihaz/zaman bazında throttling yapar.
/// F2.2-12 ([C-B-SavedScenario-3]): Önceki sürümde her listele/kaydet/sil
/// çağrısı bir UPDATE atıyordu — hot path için gereksiz yazma yükü, replication
/// lag için risk. Throttle penceresi varsayılan 5 dakikadır; aynı kullanıcı için
/// pencere içinde tekrar talep gelirse UPDATE atılmaz.
/// </summary>
public interface ILastSeenThrottle
{
    /// <summary>Pencere içinde değilse <c>true</c> döner ve pencereyi günceller — UPDATE atılmalı.</summary>
    bool ShouldUpdate(Guid userId);
}

public sealed class LastSeenThrottle : ILastSeenThrottle
{
    // Pencere süresi: ilk geliştirme aşamasında sabit, ileride IOptions ile dışarı alınabilir.
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    // SVCR-010: Eviction sınırı — sözlüğün sınırsız büyümesi (adversarial client veya
    // uzun ömürlü process'te aktif device birikimi) engellenir. Sınır aşıldığında
    // en eski entry'lerin yarısı atılır; window doğal olarak entry'lerin TTL'ini
    // sağladığı için "stale" tutmuş olsak bile semantik kayıp yok.
    private const int MaxEntries = 100_000;

    // SVCR-009/010: Concurrent map. Race-free güncelleme `AddOrUpdate` üzerinden;
    // factory paralel çağrılırsa atomik şekilde yalnız bir tanesi pencere açar.
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastUpdates = new();

    public bool ShouldUpdate(Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        var winner = false;

        // SVCR-009: check-then-write yarışı kapalı. AddOrUpdate atomik: aynı
        // kullanıcı için iki paralel çağrıdan yalnız biri `winner=true` alır.
        _lastUpdates.AddOrUpdate(
            userId,
            addValueFactory: _ =>
            {
                winner = true;
                return now;
            },
            updateValueFactory: (_, previous) =>
            {
                if (now - previous < Window)
                    return previous; // pencere içinde — UPDATE atılmaz
                winner = true;
                return now;
            });

        // SVCR-010: en eski yarıyı evict. Hot path'te O(N) tarama olmasın diye
        // sınıra ulaşıldığında bir kez yapılır; aynı `now`'da paralel iki eviction
        // tetiklenirse Interlocked flag ile tek seferlik koşulur.
        if (_lastUpdates.Count > MaxEntries && Interlocked.Exchange(ref _evicting, 1) == 0)
        {
            try
            {
                EvictOldestHalf(now);
            }
            finally
            {
                Volatile.Write(ref _evicting, 0);
            }
        }

        return winner;
    }

    private int _evicting;

    private void EvictOldestHalf(DateTimeOffset now)
    {
        // Tüm pencere dışı entry'leri tek geçişte atar (timestamp'i < now - Window).
        // Hâlâ MaxEntries üstündeysek en eski yarıyı (sıralı) atar — sınır altında kal.
        var stale = new List<Guid>();
        foreach (var kv in _lastUpdates)
        {
            if (now - kv.Value >= Window)
                stale.Add(kv.Key);
        }
        foreach (var key in stale)
            _lastUpdates.TryRemove(key, out _);

        if (_lastUpdates.Count <= MaxEntries) return;

        var ordered = _lastUpdates
            .OrderBy(kv => kv.Value)
            .Take(_lastUpdates.Count / 2)
            .Select(kv => kv.Key)
            .ToArray();
        foreach (var key in ordered)
            _lastUpdates.TryRemove(key, out _);
    }
}
