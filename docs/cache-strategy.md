# Cache Stratejisi

Bu belge Saydin.Services'teki Redis cache kullanımını belgeler.
**Cache ile ilgili herhangi bir değişiklik yapmadan önce bu belgeyi oku.
Değişiklik sonrası etkilenen bölümleri güncelle.**

---

## Genel İlke

Redis iki ayrı amaç için kullanılır:

1. **Yanıt cache'i** — Pahalı DB sorgularının ve hesaplamaların sonuçları önbelleğe alınır
2. **Kullanım sayacı** — Kullanıcı başına günlük istek kotası takibi

Her ikisi de aynı Redis instance'ına yazar; key namespace'leri ile ayrılır.

---

## Cache Key Yapısı

| Amaç | Key Formatı | TTL | Servis |
|---|---|---|---|
| What-if hesaplama | `whatif:v2:{symbol}:{buyDate}:{sellDate}:{amount}:{amountType}` | 1 saat | `WhatIfCalculator` |
| Asset listesi | `assets:list` | 6 saat | `AssetService` |
| Tek fiyat noktası | `price:{symbol}:{date}` | 24 saat | `AssetService` |
| En yakın fiyat noktası | `nearest-price:{symbol}:{date}` | 24 saat | `AssetService` |
| Fiyat aralığı | `prices:{symbol}:{from}:{to}:{interval}` | 1 saat | `AssetService` (F2.2-1: interval suffix Faz 2; F3.1-2: `interval` değeri artık `Saydin.Shared.Constants.PriceIntervals` sabitinden — key formatı değişmedi) |
| En son fiyat tarihi | `latest_date:{symbol}` | 1 saat | `AssetService` |
| DCA hesaplama | `dca:v1:{symbol}:{startDate}:{endDate}:{amount}:{period}:{amountType}{:inf?}:{lang}` | 1 saat | `DcaCalculator` |
| Günlük kullanım sayacı (What-If) | `usage:whatif:{userId}:{yyyy-MM-dd}` | Gece yarısına kadar | `DailyLimitGuard` |
| Günlük kullanım sayacı (DCA) | `usage:dca:{userId}:{yyyy-MM-dd}` | Gece yarısına kadar | `DailyLimitGuard` |

### Key Versiyonlama

`whatif:v2:...` formatındaki `v2` prefix'i kasıtlıdır. Cache yapısını kıran bir değişiklik yapılırsa
(yeni alan eklenmesi, format değişikliği) prefix'i `v3` olarak artır — eski key'ler TTL dolunca
otomatik temizlenir, manuel flush gerekmez.

### Faz 2 — Process-local Caches (Redis dışı)

| Cache | Tip | Sınır | Eviction | Hedef |
|---|---|---|---|---|
| `IAssetSymbolIndex` symbol→asset snapshot | `FrozenDictionary<string, Asset>` (immutable snapshot, singleton) | asset listesi **içerik hash'i** (XOR / Count) ile versiyonlu | hash değişiminde `Interlocked.CompareExchange` ile snapshot atılır + yeni `FrozenDictionary` inşa edilir | F2.2-20 / SVCR-001..003 O(1) sembol lookup, content-aware invalidation |
| `OpenExchangeRatesAdapter._dayCache` | `ConcurrentDictionary<DateOnly, CachedJson>` | 10_000 entry üst sınırı + 24sa entry TTL | TTL miss → entry silinir; sınır aşılınca `EvictOldestHalf` (CachedAt'a göre en eski yarısı atılır, `Interlocked.CompareExchange` flag ile tek seferlik) | F2.4-4 / INGR-008/009 bellek kontrolü, race-free eviction |
| `TcmbAdapter` day-level XML | `ConcurrentDictionary<DateOnly, CachedXmlEntry>` | 10_000 entry üst sınırı + 60 dk entry TTL | TTL bazlı stale silme + sınır aşılınca en eski entry'ler atılır (CachedAt sıralı) | F1.1-2 day dedup |
| `LastSeenThrottle` | `ConcurrentDictionary<Guid, DateTimeOffset>` (lock-free TryGetValue/TryAdd/TryUpdate loop) | **MaxEntries=100_000** sabit üst sınır | sınır aşılınca `MaybeEvict`: (a) pencere dışı (>5dk) entry'ler tek geçişte silinir, (b) hâlâ sınır üstündeyse en eski yarısı atılır (deterministik); `Interlocked.CompareExchange` flag ile tek seferlik | F2.2-12 / SVCR-009/010 last_seen UPDATE throttling, race-free + bounded |

---

## Yanıt Cache'i

### What-If Hesaplama (`whatif:v2:...`)

**Neden cache'leniyor:** Hesaplama birden fazla DB sorgusu içeriyor (buy price, sell price, price range).
Aynı parametrelerle gelen istek (farklı kullanıcıdan bile olsa) aynı matematiksel sonucu verir.

**Davranış:**
- Cache hit olsa bile istek sahibi kullanıcının **günlük kotası düşülür**
- Cache miss durumunda hesaplama yapılır, sonuç cache'e yazılır, kota düşülür
- Hesaplama başarısız olursa (fiyat bulunamadı vb.) kota **düşülmez**

```
İstek geldi
    ↓
Limit kontrolü (CheckDailyLimitAsync)
    ↓
Cache'te var mı?
  ├─ Evet → Kota düş → Cache'ten dön
  └─ Hayır → Hesapla
               ├─ Başarılı → Cache'e yaz → Kota düş → Dön
               └─ Hata → Kota düşme → Exception fırlat
```

**TTL seçimi:** 1 saat — Günlük fiyat verisi değişmiyor, ancak `sellDate` null gelirse
"bugünün fiyatı" kullanılıyor; bu durumda gün içinde fiyat güncellenirse 1 saate kadar
eski veri dönebilir. Kabul edilebilir bir trade-off.

### Asset Listesi (`assets:list`)

**TTL seçimi:** 6 saat — Asset ekleme/çıkarma nadir, sık değişmiyor.
Yeni asset eklendiğinde manuel olarak bu key silinebilir:
```bash
redis-cli DEL assets:list
```

### DCA Hesaplama (`dca:v1:...`)

**Neden cache'leniyor:** DCA hesaplaması geniş tarih aralığında fiyat verisi çeker (haftalık/aylık).
Aynı parametrelerle gelen istek aynı sonucu verir.

**Key formatı:** `dca:v1:{SYMBOL}:{START}:{END}:{AMOUNT}:{PERIOD}:{AMOUNT_TYPE}{:inf?}:{LANG}`
- `:inf` suffix'i yalnızca `IncludeInflation == true` olduğunda eklenir
- `PERIOD`: `weekly` veya `monthly`

**TTL seçimi:** 1 saat — What-If ile aynı mantık.

### Fiyat Noktaları (`price:{symbol}:{date}`)

**TTL seçimi:** 24 saat — Tarihi fiyatlar değişmez. Bugünün fiyatı için TTL daha kısa
tutulabilir ama şu an ayrım yapılmıyor. Kabul edilebilir.

### En Yakın Fiyat Noktası (`nearest-price:{symbol}:{date}`)

**Neden cache'leniyor:** Kullanıcı hafta sonu veya tatil günü seçtiğinde `GetNearestPriceAsync`
çağrılır. Bu sorgu ±7 günlük pencerede DB'yi tarar ve aynı tarih için her seferinde aynı
sonucu döner.

**Davranış:** İstek edilen tarihe ≤ olan en yakın işlem günü önce denenir (geriye doğru);
bulunamazsa > olan ilk işlem günü döner (ileriye doğru). Sonuç `PricePoint` olarak cache'lenir.

**TTL seçimi:** 24 saat — Tarihi piyasa tatilleri değişmez. Bugünün fiyatı `whatif:v2:...`
cache'inden bağımsız olduğundan ayrım yapılmıyor. Kabul edilebilir.

---

## Kullanım Sayacı

### Key: `usage:{prefix}:{userId}:{yyyy-MM-dd}`

**Prefix'ler:** `usage:whatif:` (WhatIfCalculator), `usage:dca:` (DcaCalculator)

**Nasıl çalışır:**
Her iki prefix de `DailyLimitGuard` servisi tarafından yönetilir:
1. `CheckAsync` — key'i okur (INCR yapmaz), eşik aşıldıysa 429 döner
2. `IncrementAsync` — Lua script ile atomik INCR yapar, ilk artışta TTL set eder

**TTL:** Gece yarısına kalan milisaniye (`DateTime.UtcNow.Date.AddDays(1) - DateTime.UtcNow`)
Türkiye saati UTC+3; "günlük limit" UTC bazlı sıfırlanıyor. İleride timezone-aware yapılabilir.

**Lua script (atomik INCR + PEXPIRE):**
```lua
local count = redis.call('INCR', KEYS[1])
if count == 1 then
  redis.call('PEXPIRE', KEYS[1], ARGV[1])
end
return count
```
İlk INCR'da TTL set edilir; sonraki INCR'lar TTL'yi değiştirmez.

**Premium kullanıcılar:** `user.Tier == "premium"` ise ne limit kontrolü ne de INCR yapılır.

---

## Hata Yönetimi

Redis bağlantı hatasında:
- **Yanıt cache'i:** Miss olarak kabul edilir, DB'ye düşülür (cache-aside)
- **Limit kontrolü:** Hata loglanır, istek **devam ettirilir** (kullanıcıyı engellemez)
- **Limit sayacı:** Hata loglanır, sessizce geçilir (kota düşmez)

Bu tasarım bilinçlidir: Redis'in geçici olarak erişilememesi kullanıcıyı bloke etmemeli.

### DailyLimitGuard fail-open politikası (review P1R-006)

`DailyLimitGuard.CheckAsync` / `TryAcquireAsync` / `ReleaseAsync` Redis erişiminde
hata aldığında `LogWarning` ile loglayıp istek path'inin devam etmesine izin verir
(**fail-open**). Tasarım nedenleri:

1. **Kullanıcı UX'i:** Tek nokta arızası (Redis flap, cluster failover) tüm ücretsiz
   kullanıcıları "günlük limit doluymuş gibi" bloklamamalı.
2. **Plan kuralları DB'den okunur:** Limit kontrolü ikincil savunma; ana plan/sınır
   kuralları PostgreSQL'deki `users` ve `PlanOptions`'tan gelir.

**Risk:** Bir saldırgan Redis'i bilinçli olarak bozarsa rate limit devre dışı kalır
ve DDoS koruması zayıflar. Bu nedenle:

- `redis` health check sağlıksız olduğunda operasyon ekibi alarmlanmalı
  (`Program.cs` `AddHealthChecks().AddRedis(..., tags: ["cache"])` Aspire Dashboard /
  Prometheus tarafından izlenir).
- WAF/CDN katmanında genel rate limit (per-IP) ayrıca uygulanmalı —
  `DailyLimitGuard` yalnızca application-tier kotadır.
- Lua script `ScriptEvaluateAsync` dönüşü cast hatası fırlatırsa da aynı fail-open
  yoluna düşer (orijinal exception loglanır, request engellenmez).

---

## Cache'i Etkileyen Kod Değişikliklerinde Yapılacaklar

Aşağıdaki durumlarda bu belgeyi güncelle:

- [ ] Yeni bir şey cache'leniyorsa → Key formatı, TTL ve amacı tabloya ekle
- [ ] Cache yapısı (format, alan) değişiyorsa → Key versiyonunu artır (`v2` → `v3`)
- [ ] TTL değerleri değişiyorsa → Tablodaki değerleri güncelle
- [ ] Limit mantığı değişiyorsa → "Kullanım Sayacı" bölümünü güncelle
- [ ] Hata yönetimi değişiyorsa → "Hata Yönetimi" bölümünü güncelle

---

## İleride Değerlendirilecekler

- **Timezone-aware limit sıfırlama:** Şu an UTC gece yarısı; Türk kullanıcı için UTC+3 daha doğal
- **Cache hit/miss Prometheus metriği:** Şu an sadece log'da var
- **`assets:list` invalidation:** Asset eklenince otomatik flush (şu an manuel)
- Bkz. `/docs/high-traffic-checklist.md` → Redis bölümü
