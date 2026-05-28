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

    // Concurrent erişim için ConcurrentDictionary; eski entry'ler kısa pencere
    // olduğu için doğal olarak overwrite ile temizlenir. Sınırsız büyüme riski
    // pratik değil: aktif device sayısı = aktif user sayısı sınırlıdır.
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastUpdates = new();

    public bool ShouldUpdate(Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        var previous = _lastUpdates.GetOrAdd(userId, DateTimeOffset.MinValue);
        if (now - previous < Window)
            return false;

        // Yarış: aynı kullanıcı için iki request birlikte gelse de en az biri
        // UPDATE atar; daha agresif throttle istenirse CompareAndSwap pattern eklenebilir.
        _lastUpdates[userId] = now;
        return true;
    }
}
