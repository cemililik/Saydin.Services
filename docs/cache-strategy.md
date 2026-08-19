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
| What-if hesaplama | `authority-final-v1:catalog:{revision.sha}:whatif:v3:{...}:{lang}` | 1 saat | `WhatIfCalculator` |
| Reverse what-if | `authority-final-v1:catalog:{revision.sha}:whatif:reverse:v2:{...}:{lang}` | 1 saat | `WhatIfCalculator` |
| Asset listesi / bilgi | `authority-final-v1:catalog:{revision.sha}:assets:list` / `…:assets:info:{lang}` | 6 saat / 1 saat | `AssetService` |
| Tek / en yakın fiyat | `authority-final-v1:catalog:{revision.sha}:price:{symbol}:{date}` / `…:nearest-price:{symbol}:{date}` | 24 saat | `AssetService` |
| Fiyat aralığı | `authority-final-v1:catalog:{revision.sha}:prices:{symbol}:{from}:{to}:{interval}` | 1 saat | `AssetService` |
| En son fiyat tarihi | `authority-final-v1:catalog:{revision.sha}:latest-date:{symbol}` | 1 saat | `AssetService` |
| DCA hesaplama | `authority-final-v1:catalog:{revision.sha}:dca:v2:{...}:{lang}` | 1 saat | `DcaCalculator` |
| Günlük kullanım hash'i | `usage:{feature}:{server-subject}:{redis-utc-day}` | 48 saat retention | `DailyLimitGuard` |

### Key Versiyonlama

`authority-final-v1` yalnız complete-final provider authority satırlarından üretilen cache
sözleşmesidir. Önceki namespace bu prefix üzerinden atomik olarak dışlanır. Her data-bearing key
DB-owned monoton catalog revision ve 32-byte catalog SHA taşır; yalnız count/signature cache'i yoktur.
Response veya envelope shape'i kırılırsa ilgili `whatif:v3`/`dca:v2` alt sürümü de artırılır.

Cache değeri güvenilir kabul edilmez. Envelope requested symbol/tarih/amount/dil/inflation kimliği,
asset ID+source, catalog revision+SHA ve complete-final authority özetini exact doğrular. Null,
malformed, başka request'e ait veya eski authority entry silinip miss sayılır. Raw provider evidence,
observation ID ve hash API response/cache envelope'una taşınmaz.

### Faz 2 — Process-local Caches (Redis dışı)

| Cache | Tip | Sınır | Eviction | Hedef |
|---|---|---|---|---|
| `IAssetSymbolIndex` symbol→asset snapshot | `FrozenDictionary<string, Asset>` (immutable snapshot, singleton) | DB-owned catalog revision+SHA ile versiyonlu | revision değişiminde atomik snapshot swap | O(1) sembol lookup, content-aware invalidation |
| `OpenExchangeRatesAdapter._dayCache` | `ConcurrentDictionary<DateOnly, CachedJson>` | 10_000 entry üst sınırı + 24sa entry TTL | TTL miss → entry silinir; sınır aşılınca `EvictOldestHalf` (CachedAt'a göre en eski yarısı atılır, `Interlocked.CompareExchange` flag ile tek seferlik) | F2.4-4 / INGR-008/009 bellek kontrolü, race-free eviction |
| `TcmbAdapter` day-level XML | `ConcurrentDictionary<DateOnly, CachedXmlEntry>` | 10_000 entry üst sınırı + 60 dk entry TTL | TTL bazlı stale silme + sınır aşılınca en eski entry'ler atılır (CachedAt sıralı) | F1.1-2 day dedup |
| `LastSeenThrottle` | `ConcurrentDictionary<Guid, DateTimeOffset>` (lock-free TryGetValue/TryAdd/TryUpdate loop) | **MaxEntries=100_000** sabit üst sınır | sınır aşılınca `MaybeEvict`: (a) pencere dışı (>5dk) entry'ler tek geçişte silinir, (b) hâlâ sınır üstündeyse en eski yarısı atılır (deterministik); `Interlocked.CompareExchange` flag ile tek seferlik | F2.2-12 / SVCR-009/010 last_seen UPDATE throttling, race-free + bounded |

---

## Yanıt Cache'i

### What-If Hesaplama (`whatif:v3:...`)

**Neden cache'leniyor:** Hesaplama birden fazla DB sorgusu içeriyor (buy price, sell price, price range).
Aynı parametrelerle gelen istek (farklı kullanıcıdan bile olsa) aynı matematiksel sonucu verir.

**Davranış:**
- Cache hit olsa bile istek sahibinin **günlük kotası exact lease ile rezerve edilir**.
- Cache miss durumunda hesaplama yapılır, yalnız complete response cache'e yazılır.
- Hesaplama başarısız olursa alınan exact `QuotaLease` idempotent release edilir.
- Cache Redis arızasında miss'e düşebilir; finite kota Redis arızasında istek 503 ile fail-closed olur.

```
İstek geldi
    ↓
Finite kota için nonce'lu QuotaLease acquire
    ↓
Cache'te var mı?
  ├─ Evet → Cache'ten dön
  └─ Hayır → Hesapla
               ├─ Başarılı → Cache'e yaz → Dön
               └─ Hata → exact lease release → Exception fırlat
```

**TTL seçimi:** 1 saat — Günlük fiyat verisi değişmiyor, ancak `sellDate` null gelirse
"bugünün fiyatı" kullanılıyor; bu durumda gün içinde fiyat güncellenirse 1 saate kadar
eski veri dönebilir. Kabul edilebilir bir trade-off.

### Asset Listesi (`authority-final-v1:catalog:{revision.sha}:assets:*`)

**TTL seçimi:** 6 saat (`assets:list`) / 1 saat (`assets:info`) — Asset ekleme/çıkarma nadir.
`{revision.sha}` migration 021'in DB-owned catalog singleton'ından gelir. Asset identity/content
değişiminde transaction içinde revision ve SHA değişir; bütün data-bearing key'ler yeni namespace'e
geçer, eskiler TTL ile düşer. `assets:sig` gibi count-only ikinci bir doğruluk kaynağı yoktur.
Elle inceleme gerekirse yalnız prefix sayılır; üretimde toplu `DEL` normal invalidation yolu değildir:
```bash
redis-cli --scan --pattern 'authority-final-v1:catalog:*:assets:*'
```

### DCA Hesaplama (`dca:v2:...`)

**Neden cache'leniyor:** DCA hesaplaması geniş tarih aralığında fiyat verisi çeker (haftalık/aylık).
Aynı parametrelerle gelen istek aynı sonucu verir.

**Key formatı:** `dca:v2:{SYMBOL}:{START}:{END}:{PERIODIC_AMOUNT}:{PERIOD}:{AMOUNT_TYPE}{:inf?}:{LANG}`
- `:inf` suffix'i yalnızca `IncludeInflation == true` olduğunda eklenir
- `PERIOD`: `weekly` veya `monthly`
- `v2`, eski aggregate Fisher sonucunun yeni cash-flow CPI sonucu olarak okunmasını önler.
- İstenen exact CPI aylarından biri eksik/geçersizse nullable reel alanlar `null` döner ve
  incomplete sonuç cache'e yazılmaz; TÜFE yayınlandığı anda sonraki istek yeniden hesaplar.

**TTL seçimi:** 1 saat — What-If ile aynı mantık.

### Fiyat Noktaları (`authority-final-v1:catalog:{revision.sha}:price:{symbol}:{date}`)

**TTL seçimi:** 24 saat — Tarihi fiyatlar değişmez. Bugünün fiyatı için TTL daha kısa
tutulabilir ama şu an ayrım yapılmıyor. Kabul edilebilir.

### En Yakın Fiyat Noktası (`authority-final-v1:catalog:{revision.sha}:nearest-price:{symbol}:{date}`)

**Neden cache'leniyor:** Kullanıcı hafta sonu veya tatil günü seçtiğinde `GetNearestPriceAsync`
çağrılır. Bu sorgu ±7 günlük pencerede DB'yi tarar ve aynı tarih için her seferinde aynı
sonucu döner.

**Davranış:** İstek edilen tarihe ≤ olan en yakın işlem günü önce denenir (geriye doğru);
bulunamazsa > olan ilk işlem günü döner (ileriye doğru). Sonuç `PricePoint` olarak cache'lenir.

**TTL seçimi:** 24 saat — Tarihi piyasa tatilleri değişmez. Bugünün fiyatı `whatif:v3:...`
cache'inden bağımsız olduğundan ayrım yapılmıyor. Kabul edilebilir.

---

## Kullanım Sayacı

### Key: `usage:{feature}:{server-subject}:{redis-utc-date}`

**Prefix'ler:** `usage:whatif:` (WhatIfCalculator), `usage:dca:` (DcaCalculator)

**Hangi işlem hangi sayaca düşer (ADR-002):** Tek What-If (`/calculate`), Reverse What-If
(`/reverse`) ve Karşılaştırma (`/compare`) **aynı** `usage:whatif:` sayacını paylaşır;
DCA (`/dca`) ayrı `usage:dca:` sayacını kullanır. **Compare**, sembol sayısından (2-5)
bağımsız olarak sayaçtan **yalnız 1** düşer (tek atomik acquire). Per-feature alt-kotalar
(roadmap'teki compare=5 / reverse=3 / dca=3) post-MVP'ye ertelendi — bkz.
[ADR-002](decisions/ADR-002-compare-quota.md).

**Nasıl çalışır:** Redis `TIME` hem UTC günü hem karar saatini belirler. `TryAcquireAsync`
128-bit nonce üretir ve atomik Lua script'i hash içindeki `count` ile `lease:<nonce>` alanını
birlikte yazar. Script aynı nonce'la replay edilirse tekrar artırmadan success döner; command commit
edip ACK kaybolsa bile bounded reconciliation orphan kota oluşturmaz. Dönen immutable `QuotaLease`
exact Redis key+nonce taşır. `ReleaseAsync(lease)` yalnız bu alanı silerse count'u azaltır; double
release ve UTC gün dönümü güvenlidir. Key 48 saat sonra tamamen düşer.

`CheckAsync` yalnız mevcut count'u karar için okur. Limitte 429; Redis TIME/script/shape arızasında
`quota_unavailable` 503 üretilir. Host cancellation aynen propagate edilir. Sınırsız plan için
`QuotaLease.Noop` döner ve Redis'e yazılmaz.

Raw installation credential Redis key'e girmez. Subject, doğrulanmış server principal kimliği veya
sunucunun ürettiği bounded pseudonym'dir; loglar key, nonce, subject veya exception payload yazmaz.

---

## Hata Yönetimi

Redis bağlantı hatasında:
- **Response cache:** yalnız cache read/write/delete degrade olur; cancellation propagate edilir.
  Catalog/authority DB doğrulaması cache katmanı değildir ve hatası yutulmaz.
- **Finite günlük kota:** 503 `quota_unavailable`; fail-open yoktur.
- **Dağıtık security limiter:** Redis veya trusted client-IP kararı yoksa 503; gerçek limitte 429.
- **Unlimited plan:** tasarlanmış no-op lease; hata saklayan bir fallback değildir.

Redis availability, API health ve limiter/quota hata oranı Prometheus tarafından izlenir;
[`runbooks/redis-unavailable.md`](runbooks/redis-unavailable.md) uygulanır. Ham Redis key veya
subject log/metric label'ına yazılmaz.

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
- Bkz. [`high-traffic-checklist.md`](high-traffic-checklist.md) → Redis bölümü
