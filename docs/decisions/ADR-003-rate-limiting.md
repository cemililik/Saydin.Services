# ADR-003 — Rate Limiting / Throttling Stratejisi

- **Durum:** Kabul edildi — IP-bazlı katman config-gated, MVP'de **varsayılan kapalı**
- **Tarih:** 2026-05-29
- **Karar verenler:** Backend ekibi
- **İlgili bulgular (code review):** Claude `[C-A-22]`, `[C-A-4]`; Faz 0 `F0.3-1` (madde 4), Faz 1 `F1.2-3`; Faz 4 `F4-5` (Açık Soru 5)

---

## Bağlam

Mevcut tek koruma katmanı **cihaz-bazlı günlük iş kotasıdır** (`IDailyLimitGuard`,
Redis `usage:*` key'leri, günlük pencere). Bu, ürün adilliği / kötüye-kullanımı için
tasarlandı; ama **burst / DoS** senaryolarına (tek IP'den saniyede yüzlerce istek,
asset enumeration) karşı koruma sağlamaz. `F0.3-1`'in 1–3. maddeleri (RequireDeviceId,
asset query guard, price-range gün sınırı) uygulandı; 4. madde (per-IP throttle) Faz 4'e
ertelendi.

Client IP'si `UseForwardedHeaders` sonrası doğru çözülür; `ForwardedHeaders:KnownProxies`
/ `KnownNetworks` config'ten okunur (`F1.2-3`, Program.cs). Boş trust-list → yalnız
loopback güvenilir (spoofing'e kapalı).

---

## Değerlendirilen Seçenekler

### Seçenek A — Eklemeyelim, yalnız cihaz kotasına güvenelim
- **Eksi:** Cihaz kotası IP burst/DoS'u durdurmaz; reverse-proxy/WAF yoksa açık kalır.

### Seçenek B — In-memory ASP.NET Core RateLimiter, IP-bazlı sabit pencere — SEÇİLEN
- `PartitionedRateLimiter` + `FixedWindowRateLimiter`, IP'ye göre partition.
- **Artı:** Framework built-in (ek paket yok), düşük karmaşıklık, kaba DoS koruması.
  Config-gated + varsayılan kapalı → mevcut davranışı/dev/test etkilemez.
- **Eksi:** In-memory → çok-instance'ta instance başına ayrı sayar (MVP tek instance).

### Seçenek C — Redis-destekli dağıtık sliding window
- **Artı:** Çok-instance tutarlı. **Eksi:** Ek altyapı/karmaşıklık; MVP'de gereksiz.

---

## Karar

**İki katmanlı, ortogonal model — ikisi de korunur:**

| Katman | Mekanizma | Pencere | Amaç |
|---|---|---|---|
| 1 | `IDailyLimitGuard` (Redis, mevcut) | günlük | ürün adilliği / kota |
| 2 | `RateLimiter` middleware (yeni) | saniye/dakika | burst / DoS koruması |

**Seçenek B** uygulandı (`Program.cs`):
- **Config-gated, varsayılan KAPALI** — `RateLimiting:Enabled=false`. Açıkken cömert
  varsayılanlar (`PermitLimit=100 / WindowSeconds=60 / QueueLimit=0`) → meşru istemci
  cihaz kotasından önce throttle'a takılmaz.
- **Global limiter**, IP'ye göre partition (`FixedWindow`). `/health` ve `/metrics`
  `NoLimiter`'a düşer (OTel filtresiyle tutarlı, gözlemlenebilirlik gürültüsüz).
- **Reddetme:** `OnRejected` → 429 + RFC 7807 `ProblemDetails`
  (`type=https://saydin.app/errors/rate-limited`, `IStringLocalizer<ErrorMessages>` ile
  lokalize `title`/`detail`, `traceId` extension, `Retry-After` header) —
  `DailyLimitExceededExceptionHandler` ile aynı şekil.
- **IP doğruluğu:** `UseForwardedHeaders` sonrası gerçek IP. Reverse-proxy ortamında
  `ForwardedHeaders:KnownProxies`/`KnownNetworks` **yapılandırılmalıdır**; aksi halde
  `UseForwardedHeaders` `X-Forwarded-For`'a güvenmez → `RemoteIpAddress` tüm istekler için
  proxy IP'si olur ve TÜM istemciler **tek partition**'a düşer (over-aggregation /
  over-throttling: ortak `PermitLimit` paylaşılır, meşru kullanıcılar 429 alır). Spoof
  edilemez (trust-list açık) ama bu **fail-safe değil** — yanlış yapılandırma meşru trafiği
  bloklar. Rollout/troubleshooting: yaygın 429 + tek-IP partition gözlemlenirse ilk olarak
  `ForwardedHeaders:KnownProxies`/`KnownNetworks` config'ini doğrula.

**Dağıtık (çok-instance) rate limiting**, API yatay ölçeklendiğinde eklenecek
**dokümante edilmiş takip işidir** (Seçenek C) — bilinçli erteleme, eksik değil.

---

## Sonuçlar / Risk

**Olumlu:** Kaba DoS koruması framework primitifleriyle; varsayılan kapalı olduğundan
sıfır regresyon riski; lokalize 429 + Retry-After sözleşmesi tutarlı.

**Risk:**
- In-memory limiter çok-instance'ta tutarsız sayar → ADR'da bilinçli kabul; ölçeklenince
  Seçenek C.
- Yanlış proxy trust config → tüm istemciler proxy IP'sinin tek partition'ına düşer →
  **over-throttling** (meşru kullanıcılar 429 alır); spoofing açığı doğmaz ama bu fail-safe
  değildir. Deploy checklist'inde `KnownProxies`/`KnownNetworks` vurgulanmalı.

---

## İlgili Dökümanlar

- [`docs/architecture.md`](../architecture.md) — Rate limiting / throttling bölümü
- [`docs/development-guide.md`](../development-guide.md) — `RateLimiting:*` ve
  `ForwardedHeaders:*` config anahtarları
- `Program.cs` — `AddRateLimiter` / `UseRateLimiter` (config-gated)
- Faz aksiyon planı: `docs/code-reviews/ACTION-PLAN.md` (F4-5)
