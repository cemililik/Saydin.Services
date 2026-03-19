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
| Fiyat aralığı | `prices:{symbol}:{from}:{to}` | 1 saat | `AssetService` |
| En son fiyat tarihi | `latest_date:{symbol}` | 1 saat | `AssetService` |
| DCA hesaplama | `dca:v1:{symbol}:{startDate}:{endDate}:{amount}:{period}:{amountType}{:inf?}:{lang}` | 1 saat | `DcaCalculator` |
| Günlük kullanım sayacı (What-If) | `usage:whatif:{userId}:{yyyy-MM-dd}` | Gece yarısına kadar | `DailyLimitGuard` |
| Günlük kullanım sayacı (DCA) | `usage:dca:{userId}:{yyyy-MM-dd}` | Gece yarısına kadar | `DailyLimitGuard` |

### Key Versiyonlama

`whatif:v2:...` formatındaki `v2` prefix'i kasıtlıdır. Cache yapısını kıran bir değişiklik yapılırsa
(yeni alan eklenmesi, format değişikliği) prefix'i `v3` olarak artır — eski key'ler TTL dolunca
otomatik temizlenir, manuel flush gerekmez.

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
