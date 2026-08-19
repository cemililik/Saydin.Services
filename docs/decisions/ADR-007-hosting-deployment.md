# ADR-007 — Production Hosting & Deployment Stratejisi

- **Durum:** Kabul edildi (MVP) — 2026-05-31
- **Tarih:** 2026-05-31
- **Karar verenler:** Backend ekibi (tek geliştirici / solo MVP)
- **İlgili belgeler:** [`docs/deployment/hosting-comparison.md`](../deployment/hosting-comparison.md) (tam karşılaştırma + kaynaklar), [`docs/deployment/oci-migration-plan.md`](../deployment/oci-migration-plan.md) (geçiş planı), [ADR-001](ADR-001-migration-strategy.md) (migration mekanizması), [ADR-005](ADR-005-secrets-management.md) (secrets)

---

## Bağlam

Backend şu ana kadar **yerel geliştirme makinesinde Docker Compose** ile çalıştırılıp,
mobil istemciye **ngrok tüneli** üzerinden açılıyordu. Bu yapı geliştirme için yeterli ama
production için uygun değil: makine kapanınca servis düşer, ngrok URL'i kalıcı/güvenilir
değil, TLS ngrok'a bağımlı, ve gerçek bir uptime/yedek garantisi yok.

Amaç: backend'i **gerçek, her zaman açık bir sunucuya** taşımak.

**Birincil kısıt (kullanıcı tarafından net belirtildi): maliyet. Hedef kalıcı $0; mümkün
değilse mümkün olan en düşük (aylık birkaç €).**

Projenin teknik bağımlılıkları kararı kuvvetle kısıtlıyor:

1. **PostgreSQL + TimescaleDB 2.16.1.** İki hypertable (`price_points` migration 001,
   `activity_logs` migration 008) **ve** `activity_logs` üzerinde **compression**
   (`add_compression_policy`, 008/008b/013). Vanilya Postgres'te migration zinciri patlar.
2. **Redis** — cache + atomik **Lua script** (`ScriptEvaluate`) ile rate limiting.
3. **İki .NET 10 servisi** — `Saydin.Api` (always-on HTTP) + profile-gated
   `Saydin.PriceIngestion` (başlatılırken en az bir provider explicit açık olmalıdır).
4. Servisler **yalnız PostgreSQL üzerinden** haberleşir (message bus yok) → tek makinede
   mükemmel uyumlu.

**Belirleyici teknik gerçek:** TimescaleDB **compression** özelliği Timescale Community
License (TSL) altındadır ve bu lisans, üçüncü-taraf DBaaS sağlayıcılarının bu özelliği
sunmasını **açıkça yasaklar**. Sonuç: **kalıcı ücretsiz + TimescaleDB compression sunan
hiçbir managed PostgreSQL yoktur** (Neon: "compression not supported"; Aiven/Supabase:
yalnız Apache-2 sürüm; Timescale/Tiger Cloud: 30 gün deneme sonrası ücretli). Detaylı
kanıt: [`hosting-comparison.md`](../deployment/hosting-comparison.md).

Bu, kararı ikiye indirir: **(a)** TimescaleDB'yi koru → Postgres'i **kendi VM'inde
self-host et** (TSL self-host'a izin verir), veya **(b)** managed ücretsiz DB için
**TimescaleDB compression'dan vazgeç** (migration'ları değiştir, Neon/Aiven hypertable-only).

---

## Değerlendirilen Seçenekler

### Seçenek A — Tek "always-free" VM'e lift-and-shift (Oracle Cloud A1)

Mevcut `docker-compose` (yalnız prod-zorunlu 4 servis: postgres+timescale, redis, api,
worker) tek bir **Oracle Cloud Always Free Ampere A1** (ARM, 4 OCPU / 24 GB, kalıcı $0)
sanal makinesinde çalıştırılır. Önüne TLS için Caddy reverse proxy konur.

**Artı:**
- **Kalıcı $0** — Oracle A1 always-free, süre sınırı yok (AWS/Azure 6–12 ay değil).
- **Kod ve migration DEĞİŞMEZ** — TimescaleDB tam (hypertable + compression) korunur.
- 200 GB blok depolama (managed free'lerdeki 0.5–1 GB'a karşı; `activity_logs`
  retention'sız büyüdüğü için kritik), 10 TB/ay trafik, 20 GB Object Storage (yedek).
- Tek makine, tek mental model; servisler-arası Postgres iletişimi yerel.

**Eksi:**
- **Ops sende**: yedek, TLS yenileme, OS patch, izleme senin sorumluluğun.
- **Tek hata noktası** (managed DB'deki otomatik HA/yedek yok).
- **Oracle kaprisleri**: A1 kapasite kıtlığı ("Out of host capacity") + boşta-kalma
  reclaim'i (azaltma: az-yoğun region seç + Pay-As-You-Go'ya yükselt, hâlâ $0).
- ARM64 → base imaj digest pinleri için küçük düzeltme (bkz. geçiş planı).

### Seçenek B — Managed/serverless ayrışık mimari (Cloud Run + Neon + Upstash)

API/worker → Google Cloud Run (+ Cloud Run Jobs cron) ya da Azure Container Apps;
DB → Neon/Aiven free; Redis → Upstash free.

**Artı:** Ops yükü düşük (managed yedek/ölçek); her bileşen ücretsiz kotada.
**Eksi:**
- **TimescaleDB compression KAYBI** — Neon/Aiven yalnız hypertable; migration 008/013'ün
  compression blokları hata verir → migration zincirini **değiştirmek/guard'lamak** gerekir.
- Managed free DB depolama 0.5–1 GB → `activity_logs` retention'sız hızla taşar.
- Scale-to-zero → .NET API'sinde **soğuk başlangıç** (~1–3 sn).
- Çok parçalı dağıtım, ağ/egress, secret yönetimi her serviste ayrı.
- Net kazanç yok: bu yol da $0 ama ek refactor + işlevsellik kaybıyla.

### Seçenek C — Ucuz ücretli VPS (Hetzner CAX11 ARM)

Seçenek A ile aynı lift-and-shift, ama Oracle yerine Hetzner CAX11 (ARM, 4 GB, ≈ €3.79/ay).

**Artı:** Seçenek A'nın tüm teknik avantajları + Oracle kapasite/reclaim kaprisleri YOK,
yüksek güvenilirlik.
**Eksi:** $0 değil (≈ €3.79/ay). "Tamamen ücretsiz" hedefini kaçırır ama "neredeyse sıfır"dır.

### Reddedilen seçenekler (kısa)

- **Firebase:** .NET backend **çalıştıramaz** (BaaS; Cloud Functions C# desteklemez). Host
  olarak elenir; ileride yalnız push (FCM)/auth yan-servisi olarak düşünülebilir.
- **AWS / Azure free tier:** Yalnız 6–12 ay ücretsiz compute → "kalıcı $0" değil.
- **GCP e2-micro always-free:** Kalıcı $0 ama yalnız **1 GB RAM** → tüm yığını taşıyamaz.
- **Render / Railway / Fly.io:** Free tier kaldırıldı/kısıtlı; free Postgres 30 günde
  silinir / spin-down → stateful TimescaleDB için uygun değil.

---

## Karar

> **Seçenek A — Oracle Cloud Always Free Ampere A1 (ARM, 4 OCPU / 24 GB) VM'e
> lift-and-shift.** Hesabı **Pay-As-You-Go'ya yükselt** (reclaim'i kapatır, Always Free
> kaynaklar yine $0 kalır). **Yedek plan: Oracle kapasite vermezse Hetzner CAX11 (≈ €3.79/ay).**

### Gerekçe

1. **$0 hedefini karşılayan tek seçenek** TimescaleDB'yi tam koruyarak. Seçenek B de $0 ama
   compression kaybı + migration değişikliği + storage darlığı pahasına; net getiri yok.
2. **En düşük değişim riski** — mevcut, review'lardan geçmiş `docker-compose` neredeyse
   olduğu gibi çalışır (portlar zaten `127.0.0.1` bind, Redis requirepass, Postgres parola
   zorunlu, healthcheck'ler mevcut). Kod/migration dokunulmaz.
3. **Solo MVP profili** managed HA'ya henüz ihtiyaç duymuyor; ops yükü (yedek/TLS) küçük ve
   dokümante edilebilir. OCI block-volume otomatik yedeği + `pg_dump` ile risk azaltılır.
4. **Yükseltme yolu açık:** trafik/uptime baskısı gelirse Hetzner'a (€3.79) ya da managed
   Tiger Cloud / Crunchy'ye (ücretli) geçiş mekaniği aynıdır (aynı imajlar, aynı compose).

### Önemli uygulama notları (geçiş planında detaylı)

- **ARM64:** Dockerfile base imaj **digest pinleri amd64'tür**; A1'de multi-arch tag'e
  (`:10.0`) veya arm64 digest'e geçilmeli (prod build override).
- **TLS:** ngrok'un yerini Caddy + Let's Encrypt + gerçek domain alır.
- **Firewall:** OCI security list 80/443 **ve** instance içi iptables (Oracle Ubuntu
  imajları kısıtlı iptables ile gelir) birlikte açılmalı.
- **Yedek:** OCI block-volume otomatik yedek policy (free 5) + periyodik `pg_dump`
  (TimescaleDB `timescaledb_pre_restore()/post_restore()` notuyla).
- **Workers:** prod'da fiyat çekmek için `WORKER_*_ENABLED` ve dış API key'leri set edilmeli.

---

## Sonuçlar / Risk

**Olumlu:**
- Kalıcı $0 production; ngrok bağımlılığı kalkar; kalıcı HTTPS domain.
- TimescaleDB işlevselliği tam korunur; kod/migration değişmez.
- Bol kaynak (24 GB / 200 GB) → ileride büyümeye yer var.

**Risk ve azaltma:**
- **Tek hata noktası / veri kaybı.** Azaltma: OCI otomatik block-volume yedek + günlük
  `pg_dump` → Object Storage; restore prosedürü dokümante (geçiş planı §6).
- **A1 "Out of host capacity".** Azaltma: kayıtta az-yoğun home region (Frankfurt/Singapur);
  alınamazsa Hetzner CAX11 yedek planı.
- **Boşta reclaim.** Azaltma: PAYG'a yükselt (hâlâ $0).
- **Ops zamanı (patch/TLS/izleme) tek kişide.** Azaltma: Caddy otomatik TLS yeniler;
  unattended-upgrades; healthcheck + opsiyonel uptime-monitor (ör. ücretsiz UptimeRobot).
- **ARM derleme sürprizi.** Azaltma: geçiş planı §3'te base-imaj düzeltmesi + native A1
  derleme doğrulaması.

**Yeniden değerlendirme tetikleyicileri (high-traffic-checklist ile uyumlu):** sürekli
yüksek trafik, uptime SLA ihtiyacı, veya ops yükünün katlanılamaz hale gelmesi → managed
DB (Tiger Cloud/Crunchy) + Cloud Run/Container Apps ya da Hetzner'da çok-makineli kuruluma geç.

---

## İlgili Dökümanlar

- [`docs/deployment/hosting-comparison.md`](../deployment/hosting-comparison.md) — tüm sağlayıcılar, TimescaleDB lisans analizi, kaynaklar (karar süreci)
- [`docs/deployment/oci-migration-plan.md`](../deployment/oci-migration-plan.md) — fazlı geçiş runbook'u + prod config'ler
- [`docs/deployment/README.md`](../deployment/README.md) — deployment doküman haritası
- [ADR-001](ADR-001-migration-strategy.md) — migration uygulama mekanizması (fresh-init + `apply-migrations.sh`)
- [ADR-005](ADR-005-secrets-management.md) — secrets (prod'da `.env`/secret store)
- [`docs/high-traffic-checklist.md`](../high-traffic-checklist.md) — ölçeklendirme tetikleyicileri
