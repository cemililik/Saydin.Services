# Saydın Backend — Dokümantasyon Haritası

Bu dizin **yalnızca backend (`saydin-services`) teknik dokümantasyonunu** içerir: Flutter
istemcisini etkilemeyen, servislere/altyapıya özgü belgeler. Ürün, istemci+servis sözleşmesi
ve cross-component belgeler **Saydın meta repo** `docs/`'unda yaşar (aşağıya bkz.).

> Doküman yerleşim kuralının kaynağı: [`../CLAUDE.md`](../CLAUDE.md) → "Dokümantasyon Standardı"
> ve F4-10 kararı ([`decisions/README.md`](decisions/README.md)).

## Bu repoda ne var?

| Doküman | İçerik |
|---|---|
| [`architecture.md`](architecture.md) | Servis mimarisi genel bakışı: katmanlar, sınırlar, resilience, cache, DailyLimitGuard, rate limiting, exception zinciri, finansal yuvarlama, DCA anchor-day. **Başlangıç noktası.** |
| [`cache-strategy.md`](cache-strategy.md) | Redis cache key sözleşmesi, TTL'ler, kullanım sayaçları (`usage:*`). Cache değişikliklerinden önce/sonra **zorunlu okuma** (CLAUDE.md). |
| [`development-guide.md`](development-guide.md) | .NET geliştirme iş akışı: Docker, migration, test, config anahtarları, sorun giderme. |
| [`high-traffic-checklist.md`](high-traffic-checklist.md) | MVP sonrası backend ölçeklendirme/ops kontrol listesi (eşik-tetikleyicili). |
| [`deployment/`](deployment/README.md) | Production'a alma: hosting karşılaştırması/karar süreci + Oracle Cloud A1 geçiş runbook'u (provisioning, ARM build, TLS, yedek, cutover). |
| [`architecture/`](architecture/) | Derin teknik referanslar (aşağıda). |
| [`decisions/`](decisions/README.md) | Backend mimari karar kayıtları (ADR-001..007) + ADR organizasyon konvansiyonu. |

### `architecture/` — derin teknik referanslar

| Doküman | İçerik |
|---|---|
| [`architecture/database-schema.md`](architecture/database-schema.md) | Tam DB şeması (PostgreSQL + TimescaleDB), `infrastructure/postgres/migrations/`'ın tek-noktada özeti. |
| [`architecture/observability.md`](architecture/observability.md) | Serilog/OTel/Prometheus, IExceptionHandler zinciri, health checks — detaylı referans (kurallar: CLAUDE.md "Observability Kuralları"). |
| [`architecture/activity-logging.md`](architecture/activity-logging.md) | `activity_logs` özelliği: şema, Channel yazım yolu, GeoIP, KVKK maskeleme, raporlama. (Migration 008 yorumları buraya işaret eder.) |

> **Not — `architecture.md` (dosya) vs `architecture/` (dizin):** Birlikte var olurlar. `architecture.md`
> üst-seviye servis mimarisi özetidir ve çok sayıda yerden referans alınır; `architecture/` derin
> teknik dosyaları barındırır. `activity-logging.md` bu yola konur çünkü migration 008 (değiştirilemez)
> tam olarak `docs/architecture/activity-logging.md`'ye atıfta bulunur.

## Mimari karar kayıtları (ADR) — iki uzay

- **Backend ADR'lar** → [`decisions/`](decisions/README.md) (bu repo, bağımsız `ADR-001+` uzayı):
  migration stratejisi, compare kotası, rate limiting, GeoIP dağıtımı, secrets, activity-log finansal politika, hosting/deployment.
- **Ürün / cross-component ADR'lar** → Saydın meta repo `docs/decisions/` (bağımsız `ADR-001..ADR-014`):
  no-kafka, TimescaleDB, daily-granularity, device-id auth, monorepo, Flutter kararları, plan-config vb.
- İki numara uzayı **kasıtlı olarak ayrıdır**; çakışma beklenir. Detay: [`decisions/README.md`](decisions/README.md).

## Meta repo'da ne var? (burada DEĞİL)

Saydın meta repo `docs/`'unda kalan cross-component / ürün / legal belgeler:

| Belge | Neden meta'da |
|---|---|
| `architecture/api-contract.md` | İstemci↔backend HTTP sözleşmesi (nötr repo; iki taraf da tüketir). Yeni endpoint eklenince **orada** güncellenir. |
| `architecture/overview.md` | Proje geneli mimari (Flutter + servisler birlikte). |
| `architecture/tier-system.md`, `architecture/plan-config.md` | Free/premium tier + plan-config (ürün + backend + client). |
| `runbooks/local-dev-setup.md` | Full-stack onboarding (Flutter + backend). Backend-only kurulum için bu repodaki `development-guide.md`. |
| `roadmap.md`, `decisions/` (ürün ADR'ları), `analysis/`, `ideas/`, website (`*.html`) | Ürün/araştırma/legal. |
