# Yüksek Trafik Hazırlık Listesi

> **Kapsam:** Bu belge MVP sonrası kullanıcı büyümesiyle birlikte ele alınması gereken **backend /
> altyapı** ölçeklendirme konularını listeler. Her madde bir eşik veya tetikleyici koşulla verilmiştir.
> Ürün/metrik-tetikleyici ölçek planının üst seviyesi için Saydın meta repo `docs/roadmap.md` "Faz 5 —
> Ölçeklendirme" bölümüne bakın; bu liste o fazın backend uygulama detayıdır.

---

## Redis

- [ ] **Bellek limiti ve eviction politikası ayarlanmalı**
  - `maxmemory-policy` güvenlik/kota sayaçları için sabit **`noeviction`** olmalıdır; resmi
    production validator başka bir policy'yi reddeder. `maxmemory` gerçek key kardinalitesi ve
    headroom ölçümüne göre ayrıca boyutlandırılır; policy değişikliği kapasite çözümü değildir.
  - Tetikleyici: Redis bellek kullanımı %70'i aşıyorsa

- [ ] **Quota lease hash yükü izlenmeli**
  - Redis `TIME` tabanlı günlük hash; `count` ve 128-bit nonce-bağlı lease field'larını 48 saat tutar.
  - Yüksek kullanıcı sayısında (>500k aktif/gün) atomik Lua/lease field yükü izlenmeli
  - Tetikleyici: Günlük aktif kullanıcı 100k'yı aşarsa

- [ ] **Redis Cluster veya Redis Sentinel kurulumu**
  - Tek node Redis, single point of failure
  - Tetikleyici: Uptime SLA %99.9 hedefleniyorsa veya aktif kullanıcı 50k+

- [ ] **Bağlantı havuzu (connection pool) boyutu izlenmeli**
  - `StackExchange.Redis` varsayılan pool yeterli olup olmadığı kontrol edilmeli
  - Tetikleyici: P99 latency artışı veya timeout hataları görülürse

- [x] **Cache key namespace'leri ve veri otoritesi versiyonlandı**
  - Data-bearing key'ler `authority-final-v1` revision'ı ile catalog revision+SHA ve request
    identity'sini bağlar; eski/malformed envelope yalnız miss sayılır.
  - Tetikleyici: Cache yapısını kıran her backend değişikliğinde (bkz. `docs/cache-strategy.md`)

---

## PostgreSQL / TimescaleDB

- [ ] **`price_points` hypertable chunk interval optimize edilmeli**
  - Şu an 1 aylık chunk; sorgu paternine göre 1 haftalık daha verimli olabilir
  - Tetikleyici: Ortalama sorgu süresi 100ms'yi aşarsa

- [ ] **Sık kullanılan asset+tarih aralığı sorgularına partial index eklenmeli**
  - Tetikleyici: EXPLAIN ANALYZE'da seq scan görünürse

- [ ] **`activity_logs` retention/compression politikası gözden geçirilmeli**
  - Şu an 7 günden eski chunk'lar compress edilir (migration 013); soğuk veri için Parquet/cold-storage düşünülebilir
  - Tetikleyici: `activity_logs` boyutu hızlı büyürse veya raporlama yavaşlarsa

- [ ] **Connection pool boyutu (Npgsql) gözden geçirilmeli**
  - Varsayılan: 100 bağlantı — API replica sayısına göre artırılmalı
  - Tetikleyici: "connection pool exhausted" hatası görülürse

- [ ] **Read replica eklenmeli**
  - Hesaplama sorguları (SELECT ağırlıklı) read replica'ya yönlendirilebilir
  - Tetikleyici: Günlük aktif kullanıcı 50k+ veya DB CPU sürekli >70%

---

## API

- [ ] **Dağıtık uygulama limiter'ına edge DDoS katmanı eklenmeli**
  - Uygulama exact IP + IPv4 `/24` veya IPv6 `/64` + installation principal bucket'larını Redis
    `TIME` ile atomik ve iki-replika tutarlı uygular; Redis/istemci-IP belirsizliğinde fail-closed 503'tür.
  - Caddy önünde volumetrik DDoS/scraping için operator-seçimli edge/WAF katmanı ayrıca gerekir.
  - Tetikleyici: Anormal trafik desenleri görülürse veya API yatay ölçeklenince

- [ ] **Horizontal scaling: API stateless mi doğrulanmalı**
  - Şu an: Evet, stateless. Doğrulama: Session state veya kalıcı in-memory state yok mu? (`LastSeenThrottle`
    ve `IAssetSymbolIndex` process-local cache'leri sticky-session gerektirmez, semantik kayıp yaratmaz.)
  - Tetikleyici: İkinci API replica eklemeden önce

- [ ] **`/v1/what-if/calculate` endpoint'i için ayrı rate limit katmanı**
  - Hesaplama endpoint'i diğerlerinden daha pahalı; ayrı bir limitle korunabilir
  - Tetikleyici: Bu endpoint'in toplam CPU'nun %50'sini tükettiği görülürse

---

## Gözlemlenebilirlik (Observability)

> Detaylı observability mimarisi: [`architecture/observability.md`](architecture/observability.md).

- [x] **Alerting kuralları ve runbook bağlantıları tanımlandı**
  - API availability/latency/error, ingestion freshness, PostgreSQL/Redis/disk, backup, TLS,
    restart ve telemetry queue/export kuralları promtool fixture'larıyla doğrulanır.

- [ ] **Daily limit aşım oranı izlenmeli**
  - Kaç kullanıcı limiti dolduruyor? Bu premium conversion için sinyal
  - Tetikleyici: Uygulama canlıya geçtiğinde

- [ ] **Cache hit/miss oranı Prometheus metric'i eklenmeli**
  - Şu an loglarda var; Grafana dashboard'a taşınmalı
  - Tetikleyici: Grafana dashboard kurulduğunda

---

## Güvenlik

- [x] **Client-chosen Device-ID authentication ve kota kökü kaldırıldı**
  - Server-issued 256-bit installation credential hash-only doğrulanır; limiter anahtarları HMAC ile
    pseudonymize edilir. `X-Device-ID` route authorize etmez ve auto-claim yolu yoktur.

- [ ] **Secret rotation planı**
  - Veritabanı, Redis bağlantı string'leri ve API key'leri için rotasyon prosedürü
  - Strateji + runbook: [`docs/decisions/ADR-005-secrets-management.md`](decisions/ADR-005-secrets-management.md)
    (precautionary rotation F1.7-2 hâlâ insan aksiyonu olarak beklemede)
  - Tetikleyici: İlk prodüksiyon yayınından önce

---

## Maliyet

- [ ] **macOS GitHub Actions runner kullanımı izlenmeli** *(client-CI; saydin-client kapsamı)*
  - iOS release build'leri macOS runner kullanıyor (10x dakika tüketimi)
  - Tetikleyici: Aylık Actions dakikasının %50'si dolduğunda

- [ ] **TimescaleDB veri retention politikası**
  - Freemium kullanıcılar için 1 yıllık veri yeterli; eski veriler daha ucuz depolamaya taşınabilir
  - Tetikleyici: `price_points` tablosu 10GB'ı aşarsa
