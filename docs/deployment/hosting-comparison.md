# Hosting Karşılaştırması & Karar Süreci

> **Amaç:** Backend'i yerel Docker + ngrok'tan gerçek bir sunucuya taşırken değerlendirilen
> tüm seçenekleri, karşılaştırma kriterlerini ve nihai kararın gerekçesini **denetlenebilir**
> biçimde kayda almak. Karar özeti: [ADR-007](../decisions/ADR-007-hosting-deployment.md).
> Uygulama adımları: [`oci-migration-plan.md`](oci-migration-plan.md).
>
> **Araştırma tarihi:** 2026-05-31. Free-tier koşulları sık değişir — kritik kararlardan
> önce aşağıdaki kaynak linklerinden güncel durumu teyit edin.

---

## 1. Karar kriterleri (öncelik sırasıyla)

1. **Maliyet** — birincil kısıt. Hedef **kalıcı $0**; mümkün değilse aylık birkaç €.
2. **TimescaleDB uyumu** — `price_points` + `activity_logs` hypertable'ları **ve**
   `activity_logs` compression'ı çalışmalı (yoksa migration zinciri patlar).
3. **Redis + Lua** — atomik `ScriptEvaluate` (EVAL) rate limiter çalışmalı.
4. **İki .NET 10 container** — always-on API + zamanlı worker host edilebilmeli.
5. **Değişim maliyeti** — mevcut kod/migration/compose ne kadar az değişirse o kadar iyi.
6. **Ops yükü / dayanıklılık** — yedek, TLS, uptime, tek-kişi yönetilebilirliği.

---

## 2. Projenin gerçek bağımlılıkları (repo taramasından)

| Bileşen | Prod-zorunlu? | Kanıt | Hosting etkisi |
|---|---|---|---|
| PostgreSQL + **TimescaleDB 2.16.1** | 🔴 Evet | 2 hypertable (`price_points` [001], `activity_logs` [008]) + compression (008/008b/013) | **Belirleyici kısıt** — managed free'lerde compression yok |
| Redis + Lua | 🟠 Evet | `DailyLimitGuard.ScriptEvaluateAsync` (EVAL) | Upstash/Redis Cloud free yeterli |
| `Saydin.Api` (.NET 10) | 🔴 Evet | Always-on HTTP :8080, `/health` | Container / VM |
| `Saydin.PriceIngestion` (.NET 10) | 🟠 Evet | Günlük/aylık worker; şu an `Enabled:false` | Always-on container ya da cron job |
| pgadmin, redis-insight, aspire, prometheus, exporter'lar, tests | ⚪ Hayır (dev) | docker-compose | Prod'da çıkarılır / opsiyonel |

➡️ **Prod-zorunlu çekirdek = 4 servis** (postgres, redis, api, worker). Geri kalanı geliştirme/gözlem.

---

## 3. Belirleyici kısıt: TimescaleDB compression lisansı

TimescaleDB iki lisans katmanında dağıtılır:

| Özellik | Lisans | Managed free'de sunulabilir mi? |
|---|---|---|
| **Hypertable** (`create_hypertable`) | Apache-2 (açık kaynak) | ✅ Evet (Neon, Aiven) |
| **Compression** + policy, continuous aggregate | **Timescale Community License (TSL)** | ❌ Hayır — TSL üçüncü-taraf DBaaS'ı **açıkça yasaklar** |

Saydın `activity_logs` üzerinde compression kullandığından (migration 008 `SET
(timescaledb.compress…)` + `add_compression_policy`), **managed bir ücretsiz Postgres'te
migration 008/013 hata verir.**

**Kritik sonuç:** TSL yasağı yalnız **DBaaS sağlayıcısını** bağlar; **kendi VM'inde
self-host** edilen açık kaynak TimescaleDB compression dahil her şeyi içerir. Bu, VM
seçeneğini "compression'ı koru" hedefi için tek ücretsiz yol yapar.

---

## 4. Sağlayıcı değerlendirmeleri

### 4.1 Managed PostgreSQL (TimescaleDB ile, kalıcı free)

| Sağlayıcı | Kalıcı free? | Hypertable | **Compression** | Limit / not |
|---|---|---|---|---|
| **Neon** | ✅ | ✅ (PG18, Şub 2026) | ❌ *"Compression is not supported"* | 0.5 GB/proje; scale-to-zero soğuk başlangıç |
| **Aiven** | ✅ | ✅ (`CREATE EXTENSION timescaledb`) | ❌ (yalnız Apache-2 sürüm) | 1 CPU/1 GB/**1 GB depo** (2025-05'te 5→1 GB) |
| **Supabase** | ✅ | ⚠️ PG15 deprecate / PG17 kaldırıldı | ❌ | 500 MB; 1 hafta hareketsizlikte duraklar |
| **Timescale/Tiger Cloud** | ❌ (30 gün deneme) | ✅ | ✅ ama **ücretli** | Deneme sonrası veriler silinebilir |
| **Crunchy Bridge** | ❌ | ✅ | ✅ ama **~$10/ay** | Free yok |
| **Tembo** | ✅ (Hobby) | ✅ | belki | Ürün geleceği belirsiz → kalıcı bağımlılık için riskli |
| **Render / Railway Postgres** | ❌ | — | — | Free DB 30 günde silinir / kalıcı değil |

**Sonuç:** **Kalıcı ücretsiz + TimescaleDB compression sunan managed Postgres YOK.** Yalnız
hypertable yeterliyse Neon (en temiz) veya Aiven kullanılabilir; compression gerekiyorsa
seçenek ya **self-host** ya **ücretli** (Tiger/Crunchy).

### 4.2 Managed Redis (Lua/EVAL ile, free)

| Sağlayıcı | Kalıcı free | Limit (2026) | EVAL/EVALSHA |
|---|---|---|---|
| **Upstash Redis** | ✅ | 256 MB · **500K komut/ay** (~16.7K/gün) · 10 GB/ay BW | ✅ EVAL destekli |
| **Redis Cloud (free)** | ✅ | 30 MB | ✅ |
| **Aiven Valkey (free)** | ✅ | ~1 GB | ✅ (Redis-OSS uyumlu) |

**Sonuç:** Sorun yok — **Upstash free** pragmatik seçim; tek dikkat: **500K komut/ay** tavanı
(yoğun rate-limiter için Redis Cloud / Aiven Valkey by-command sınırsız alternatif). *Tek VM
senaryosunda Redis'i compose içinde host etmek bu sınırları tamamen ortadan kaldırır.*

### 4.3 Serverless container hosting (.NET 10) — free

| Platform | Aylık free kota | Scale-to-zero | Cron/worker | Always-on maliyeti |
|---|---|---|---|---|
| **Google Cloud Run** | 180K vCPU-sn + 360K GiB-sn + 2M istek | ✅ (soğuk başlangıç) | **Cloud Run Jobs** (cron) | min-instance=1 → 7/24 ücret, free dışı |
| **Azure Container Apps** | 180K vCPU-sn + 360K GiB-sn + 2M istek | ✅ (sıfırda ücret yok) | **ACA Jobs** (cron) | min-replica=1 → kotayı sürekli tüketir |
| **AWS App Runner** | ❌ anlamlı free yok | ❌ | yok | Boşta bile bellek faturalanır |

**Sonuç:** API+worker **$0** host edilebilir (Cloud Run / ACA), ama **veritabanı sorununu
çözmez** (yine TimescaleDB-compression engeli). Ayrıca .NET'te soğuk başlangıç (~1–3 sn;
Native AOT/ReadyToRun ile düşer).

### 4.4 Always-free VM (tüm yığını host eder)

| Sağlayıcı | Free compute | Kalıcı? | ARM? | Tüm yığın? | Ana sorun |
|---|---|---|---|---|---|
| **Oracle A1 (Ampere)** | **4 OCPU / 24 GB** + 200 GB disk + 10 TB BW | ✅ kalıcı | ✅ ARM64 | ✅ Rahatça | Kapasite kıtlığı + boşta reclaim (PAYG ile çözülür) |
| **Oracle E2.1.Micro (AMD)** | 2× (1/8 OCPU / 1 GB) | ✅ kalıcı | ❌ x86 | ❌ 1 GB çok az | — |
| **GCP e2-micro** | 1 vCPU(shared) / **1 GB** | ✅ kalıcı | ❌ x86 | ❌ 1 GB + 1 GB egress | RAM yetersiz |
| **AWS t3.micro** | 750 sa/ay | ❌ 6–12 ay | ✅/❌ | ✅ | Süre sınırlı → sonra ücretli |
| **Azure B1s / B2pts** | 750 sa/ay | ❌ 12 ay | ✅ (B2pts) | sınırlı | Süre sınırlı |
| **Fly.io / Render / Railway** | yok / kısıtlı | ❌ | — | ❌ | Free tier kaldırıldı / spin-down / 30g Postgres silme |

**Sonuç:** **Oracle A1**, "kalıcı $0 + tüm docker-compose + TimescaleDB" üçlüsünü karşılayan
**tek** seçenek. 24 GB RAM, ~2–4 GB ihtiyacın çok üstünde.

### 4.5 Ucuz ücretli VPS yedeği (Oracle kapasite vermezse)

| Sağlayıcı | Plan | vCPU/RAM/Disk | Arch | ≈ Fiyat |
|---|---|---|---|---|
| **Hetzner** | CAX11 (ARM) | 2 / 4 GB / 40 GB | ARM64 | **€3.79/ay** |
| **Hetzner** | CX22 (x86) | 2 / 4 GB / 40 GB | x86 | €3.79/ay, 20 TB trafik |
| **Netcup** | VPS lite | 2 / 4 GB / 128 GB | x86 | €3.99/ay |
| **Contabo** | Cloud VPS | 4 / 8 GB | x86 | €4.99/ay (en çok RAM/€, güvenilirlik zayıf) |

**En iyi yedek:** **Hetzner CAX11 (ARM, 4 GB, €3.79/ay)** — A1 ile aynı ARM imajları, yüksek
güvenilirlik, Oracle kaprisleri yok.

---

## 5. Firebase neden host değil?

Firebase bir **Backend-as-a-Service**'tir (Firestore, Auth, Cloud Messaging/FCM, Hosting,
Cloud Functions). **Cloud Functions yalnız JS/TS, Python, Dart çalıştırır — C#/.NET YOK.**
Firebase'in .NET *Admin SDK*'sı vardır (sunucundan Firebase'e konuşabilirsin) ama senin .NET
kodunu **çalıştırmaz**. Google tarafında .NET container host etmenin tek yolu **Cloud Run**'dır
— o da Firebase değil, GCP'dir. → Firebase bu backend için host olarak **elenir**; ileride
yalnız **push bildirim (FCM)** veya auth yan-servisi olarak düşünülebilir.

---

## 6. Karar matrisi (özet)

| Seçenek | Maliyet | TimescaleDB tam? | Kod değişimi | Ops yükü | Karar |
|---|---|---|---|---|---|
| **Oracle A1 VM (lift-and-shift)** | **$0 kalıcı** | ✅ | Yok | Sende | ✅ **SEÇİLDİ** |
| Hetzner CAX11 (VPS) | ≈€3.79/ay | ✅ | Yok | Sende (Oracle kaprisi yok) | 🥈 Yedek plan |
| Cloud Run + Neon + Upstash | $0* | ❌ compression yok | Migration düzenleme | Düşük | ❌ (işlev kaybı) |
| AWS / Azure VM | $0→ücretli | ✅ (self-host) | Yok | Sende | ❌ (süre sınırı) |
| GCP e2-micro | $0 | self-host | Yok | Sende | ❌ (1 GB RAM) |
| Tiger / Crunchy managed | $10+/ay | ✅ | Yok | Yok | ❌ (maliyet) |
| Firebase | — | — | — | — | ❌ (.NET çalıştıramaz) |

\* *Neon 0.5 GB / Aiven 1 GB depo sınırı `activity_logs` retention'sız büyüdüğü için risk;
ayrıca scale-to-zero soğuk başlangıç.*

---

## 7. Nihai karar

**Oracle Cloud Always Free Ampere A1 (ARM, 4 OCPU / 24 GB) VM'e mevcut `docker-compose`'u
lift-and-shift.** Kalıcı $0, kod/migration değişmez, TimescaleDB tam korunur. Oracle kapasite
vermezse / ops istenmezse **Hetzner CAX11 (€3.79/ay)** yedek planı. Gerekçenin tam metni:
[ADR-007](../decisions/ADR-007-hosting-deployment.md). Geçiş adımları:
[`oci-migration-plan.md`](oci-migration-plan.md).

---

## 8. Kaynaklar (erişim 2026-05-31)

- **Oracle Always Free** — [Always Free Resources](https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm) · [Free Tier FAQ](https://www.oracle.com/cloud/free/faq/) · idle-reclaim & PAYG: techtutelage.net · A1 kapasite çözümü: Medium guide
- **TimescaleDB lisans / managed** — Neon: [timescaledb extension docs](https://neon.com/docs/extensions/timescaledb) ("compression not supported") · Aiven: [timescaledb concepts](https://aiven.io/docs/products/postgresql/concepts/timescaledb) + [free tier](https://aiven.io/free-tier) · Supabase: [timescaledb deprecation](https://supabase.com/docs/guides/database/extensions/timescaledb) · Tiger Cloud: [pricing](https://www.tigerdata.com/pricing)
- **Redis** — Upstash: [pricing](https://upstash.com/docs/redis/overall/pricing) + [EVAL](https://upstash.com/docs/redis/sdks/py/commands/scripts/eval) · Redis Cloud free
- **Serverless** — [Cloud Run pricing](https://cloud.google.com/run/pricing) · [Azure Container Apps pricing](https://azure.microsoft.com/en-us/pricing/details/container-apps/) · [AWS App Runner pricing](https://aws.amazon.com/apprunner/pricing/)
- **Diğer free tier** — [AWS Free Tier 2025](https://aws.amazon.com/free/) · [Azure free account](https://azure.microsoft.com/en-us/pricing/purchase-options/azure-account) · [GCP free](https://docs.cloud.google.com/free/docs/free-cloud-features) · [Fly.io pricing](https://fly.io/docs/about/pricing/) · [Render free](https://render.com/docs/free) · [Railway pricing](https://railway.com/pricing)
- **Ücretli VPS** — [Hetzner CX/CAX plans](https://www.hetzner.com/cloud) · [Netcup](https://www.netcup.com/en/server/vps-lite) · [Contabo](https://contabo.com/en-us/pricing/)
- **Firebase / .NET** — [Firebase Functions language support](https://firebase.google.com/docs/functions)

> **Doğrulama notu:** "Aiven free tier TimescaleDB **compression**'ı engeller" iddiası,
> Aiven'in "yalnız Apache-2 lisanslı TimescaleDB sunulur + TSL özellikleri DBaaS'ta yasak"
> beyanından **çıkarımdır**; compression Aiven'de hard go/no-go ise commit'ten önce Aiven
> desteğine doğrudan teyit ettirin.
