using System.Collections.Concurrent;

namespace Saydin.Api.Services;

/// <summary>
/// `users.last_seen_at` UPDATE'lerini installation principal/zaman bazında throttling yapar.
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

public sealed class LastSeenThrottle(TimeProvider timeProvider) : ILastSeenThrottle
{
    // Pencere süresi: ilk geliştirme aşamasında sabit, ileride IOptions ile dışarı alınabilir.
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    // SVCR-010: Eviction sınırı — sözlüğün sınırsız büyümesi (adversarial client veya
    // uzun ömürlü process'te aktif principal birikimi) engellenir. Sınır aşıldığında
    // en eski entry'lerin yarısı atılır; window doğal olarak entry'lerin TTL'ini
    // sağladığı için "stale" tutmuş olsak bile semantik kayıp yok.
    private const int MaxEntries = 100_000;

    // SVCR-009/010: Lock-free TryGetValue/TryAdd/TryUpdate döngüsü ile çalışan
    // concurrent map (bkz. ShouldUpdate). Factory side-effect tipiyle race
    // riskini taşıyan AddOrUpdate kullanılmaz.
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastUpdates = new();

    public bool ShouldUpdate(Guid userId)
    {
        var now = timeProvider.GetUtcNow();

        // SVCR-009 follow-up (Codacy uyarısı): `AddOrUpdate` factory delegate'leri
        // contention durumunda birden fazla kez çağrılabilir; factory içinde local
        // bayrak set etmek "winner" bilgisinin yanlış kalmasına yol açabiliyordu.
        // Lock-free TryGetValue/TryAdd/TryUpdate döngüsü ile factory side-effect'siz:
        //   1. Snapshot oku → pencere içinde mi karar ver.
        //   2. Snapshot bulunmazsa TryAdd ile yarış: kazanan winner=true.
        //   3. Pencere dışındaysa TryUpdate ile yarış: kazanan winner=true.
        //   4. Race kaybedersek (concurrent başka thread güncelledi) baştan tara.
        while (true)
        {
            if (_lastUpdates.TryGetValue(userId, out var previous))
            {
                if (now - previous < Window)
                    return false; // pencere içinde — UPDATE atılmaz

                if (_lastUpdates.TryUpdate(userId, now, previous))
                {
                    MaybeEvict(now);
                    return true; // pencere dışıydı, biz güncelledik → UPDATE atılır
                }
                // TryUpdate fail → başka thread snapshot değiştirdi; baştan tara.
                continue;
            }

            if (_lastUpdates.TryAdd(userId, now))
            {
                MaybeEvict(now);
                return true; // yeni kullanıcı, biz ekledik → UPDATE atılır
            }
            // TryAdd fail → başka thread aynı anda ekledi; döngüye dön ve TryGetValue ile bak.
        }
    }

    private void MaybeEvict(DateTimeOffset now)
    {
        // SVCR-010: en eski yarıyı evict. Hot path'te O(N) tarama olmasın diye
        // sınıra ulaşıldığında bir kez yapılır; aynı `now`'da paralel iki eviction
        // tetiklenirse Interlocked flag ile tek seferlik koşulur.
        if (_lastUpdates.Count <= MaxEntries
            || Interlocked.CompareExchange(ref _evicting, 1, 0) != 0)
            return;
        try
        {
            EvictOldestHalf(now);
        }
        finally
        {
            Volatile.Write(ref _evicting, 0);
        }
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
