# ADR-003 — Dağıtık Rate Limiting ve Günlük Kota

- **Durum:** Kabul edildi — Redis tabanlı, production'da zorunlu ve fail-closed
- **Tarih:** 2026-08-19
- **Karar verenler:** Backend ekibi
- **İlgili bulgular:** API-02, API-10, PLT-H03, API-TRUST-001

## Bağlam

İstemcinin seçtiği `X-Device-ID` kimlik veya abuse-control anahtarı olamaz. Process-local
fixed-window limiter iki replica'da limiti katlıyor; eski günlük kota ise Redis hatasında
fail-open ilerliyor ve acquire sonrası release günü yeniden hesaplandığı için gece yarısında
yanlış sayacı azaltabiliyordu.

## Karar

Koruma iki ayrı Redis sözleşmesidir:

1. **Security admission limiter:** `UseForwardedHeaders` sonrası exact IP ile IPv4 `/24`
   veya IPv6 `/64` ağı atomik Lua içinde birlikte sayılır. Installation credential
   doğrulandıktan sonra principal için ayrı atomik bucket uygulanır; IP ikinci kez sayılmaz.
   Anahtarlar yalnız private-file HMAC-SHA-256 pseudonym taşır. Redis `TIME` pencere
   otoritesidir. Limit `429`, bilinmeyen/güvenilmeyen istemci IP'si veya Redis karar hatası
   `503` üretir.
2. **Ürün kotası:** finite plan acquire işlemi exact Redis key'i ve 128-bit nonce'u
   taşıyan immutable `QuotaLease` döndürür. Release yalnız bu lease'i tüketir; aynı
   lease'in ikinci release'i no-op'tur. Sayaç/nonce 48 saat tutulur; 23:59 acquire, 00:00
   release doğru günü azaltır. ACK kaybı aynı nonce ile bounded retry ve `HEXISTS`
   reconciliation kullanır. Finite plan Redis hatası `quota_unavailable` ile fail-closed;
   gerçekten limitsiz plan no-op lease ile Redis'e gitmez.

Production'da limiter `Enabled=true`, pozitif bounded limitler, strict proxy trust ve private
HMAC dosyası startup'ta zorunludur. Process-local ASP.NET `RateLimiter` kaldırılmıştır.

## Sonuçlar

- Replicalar aynı limitleri paylaşır; credential rotation abuse bütçesini sıfırlamaz.
- Ham IP, principal, nonce ve secret log/metric/Redis key'ine girmez.
- Cache Redis'inin degrade semantiği ile güvenlik/kota Redis kararı birbirinden ayrıdır;
  güvenlik kararı fail-open olamaz.
- Redis'in tamamen kaybı finite hesaplamaları ve yeni request admission'ı bilinçli olarak
  durdurur; runbook ve availability alarmı zorunludur.

## İlgili Dökümanlar

- [ADR-010](ADR-010-installation-principal.md)
- [Redis unavailable runbook](../runbooks/redis-unavailable.md)
- `src/Saydin.Api/Security/`
- `src/Saydin.Api/Services/DailyLimitGuard.cs`
