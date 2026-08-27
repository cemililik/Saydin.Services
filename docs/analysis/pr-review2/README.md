# PR Review 2 — `development` çalışma ağacı

> **Review tarihi:** 2026-08-27
> **Hedef:** commit'lenmemiş değişiklik seti (`git diff` + untracked), taban `f9f608d`
> **Kapsam:** 361 dosya · +20.011 / −3.397 satır · atlanan dosya **0**
> **Yöntem:** 19 uzman hattı × (bulgu üreten + düşman doğrulayıcı) + ana agent mekanik kapıları
> **Amaç:** sorun avı değil — **birinci sınıf bir ürün ve mühendislik deneyimine** giden farkı ölçmek

> **Remediation durumu (2026-08-27):** Bu dosya review anındaki tarihsel kararı korur. Critical ve
> High bulguların kapanışı, uygulanan Medium aksiyonlar, kalan riskler ve güncel test kanıtı için
> [08-remediation-execution.md](08-remediation-execution.md) esas alınır.

## Bulgu envanteri

| | Critical | High | Medium | Low | Toplam |
|---|---:|---:|---:|---:|---:|
| `defect` | 2 | 18 | 52 | 22 | **94** |
| `excellence-gap` | — | — | 69 | 122 | **191** |
| **Toplam** | **2** | **18** | **121** | **144** | **285** |

268 ham kayıt üretildi; doğrulayıcılar **11 tanesini reddetti** (gerekçeleriyle
[05-lane-summaries.md](05-lane-summaries.md) içinde) ve **28 yeni kayıt** ekledi.

## Yayın kararı

**Bu değişiklik seti merge edilmeye hazır değil — ama gövdesi sağlam.**

Önceki review'in **2 Critical + 14 High** bulgusunun büyük çoğunluğu **kök neden düzeyinde
gerçekten kapatılmış** ve çoğu regresyon testiyle kilitlenmiştir (ayrıntı:
[06-remediation-audit.md](06-remediation-audit.md)). Build 0 warning, unit süitleri
**1.134 test / 0 skip** ile geçiyor — önceki review'e göre **+189 test**.

Engel, düzeltmelerin kendisinin ürettiği **iki yeni Critical** ve tekrar eden bir desendir.

## Bu review'in tek en önemli bulgusu

> **Düzeltmelerin bir kısmı aynı arıza sınıfını bir katman yukarıda yeniden üretti.**

Bu desen dört ayrı yerde bağımsız olarak gözlendi:

| Önceki bulgu | Düzeltme | Yeni durum |
|---|---|---|
| Required CI kapısında bayat `schema_migrations = 23` sabiti | O sabit düzeltildi | **Yepyeni** required kapı `run-development-compose-smoke.sh` bayat `already_applied=25` taşıyor (gerçek: 26) → her koşuda kırmızı |
| Kök compose çalışmıyor (eksik backup argümanları) | Compose düzeltildi | `CLAUDE.md` / `CONTRIBUTING.md` komutları `--env-file .env.database-runtime` içermiyor → **dokümanı izleyen geliştirici için sonuç aynı: hiçbir komut çalışmıyor** |
| Günlük TCMB timer 2. koşuda kırılıyor | `CalendarPlanMaterializer` eklendi | Materializer'ın kendisi 2. günde `materialized_plan_conflict` ile kırılıyor |
| Kimlik doğrulamasız principal üretimi kotayı sıfırlıyor | IP/ağ bazlı admission cap'leri eklendi | Cap'ler CGNAT/paylaşılan NAT arkasındaki **meşru** kullanıcıyı kilitliyor; ayrıca ağ kovası principal kontrolünden önce artırıldığı için tek installation komşularını dışarı atabiliyor |

Ek olarak iki düzeltme **yeni bir risk sınıfı** getirdi:

- **Migration 024**, kimlik doğrulama sıcak yolunu indeksli `UNIQUE(hash_key_version, secret_hash)`
  probe'undan **sargable olmayan** bir PL/pgSQL taramasına taşıdı ve eşleşen satırda koşulsuz
  `FOR UPDATE` kilidi alıyor. Her korumalı istek artık aktif key sürümündeki satır sayısıyla
  lineer. *(İki hat — `R02` ve `R17` — bunu bağımsız olarak buldu.)*
- **Activity-log sınıflandırıcısı** genişletildi ama varsayılan dal hâlâ `FatalHost`; enümere
  edilmeyen her SQLSTATE ve her Postgres-dışı hata hâlâ tüm API host'unu düşürüyor.

## P0 — Merge öncesi zorunlu

### 1. `run-development-compose-smoke.sh` bayat migration sayısı *(Critical)*

`MigrationRunner.cs:240` `--verify-only` için `already_applied = manifest.Migrations.Count` = **26**
döndürüyor; script `already_applied=25` arıyor → `set -eu` altında her koşuda non-zero.
Yeni **required** CI kapısı deterministik olarak kırmızı.

**Yap:** sabiti kaldır. Sayıyı `MigratorMigrationTrustRoot.Checksums.Count`'tan veya migrator'ın
kendi çıktısından türet; hiçbir kapıda migration sayısını elle yazma. *(Bu sayı şu an
[sekiz ayrı noktada](03-findings-low.md) elle senkronize ediliyor.)*

### 2. OTel Collector readiness probe'u loopback'e bağlı endpoint'i ağdan yokluyor *(Critical)*

`otel-collector.production.yml:3` → `health_check.endpoint: 127.0.0.1:13133`.
`deploy-release.sh:286` → Prometheus container'ından `wget --spider http://otel-collector:13133/`.
Bağlantı reddedilir; 60 denemeden sonra `deployment_monitoring_readiness_failed`.
**Bu commit'le eklenen monitoring readiness kapısı, her deploy'u bloke eder.**

**Yap:** health endpoint'i `0.0.0.0:13133`e bağla (veya probe'u collector container'ının içine al).
Aynı sınıf için: probe edilen her endpoint'in bind adresini doğrulayan bir self-test ekle.

### 3. Dokümante edilmiş Compose komutlarını çalışır hale getir *(High — üç hat bağımsız buldu)*

`CLAUDE.md:25-38,70-76` ve `CONTRIBUTING.md:44` içindeki dört Compose komutu
`--env-file .env.database-runtime` bayrağını taşımıyor ve bootstrap adımını anmıyor.
Önceki review'in Critical'ı compose dosyasında kapatıldı; **dokümanı izleyen geliştirici için
sonuç değişmedi.**

**Yap:** dört komutu düzelt, bootstrap ön koşulunu `CLAUDE.md`'ye ekle ve `check-doc-links.py`
sınıfına "dokümandaki compose komutu gerçekten çalışıyor mu" kapısı ekle.

### 4. Kimlik doğrulama sıcak yolunu indekse geri döndür *(High)*

Migration 024 `resolve_installation_and_rehash`'i sargable olmayan hale getirdi ve her başarılı
auth'ta satır kilidi alıyor.

**Yap:** verifier lookup'ını indeksli eşitlik probe'una geri al; rehash'i ayrı, koşullu ve
kilitsiz bir yola taşı (ör. arka plan job veya yalnız gerçekten gerekli olduğunda).

### 5. Admission cap'lerini gerçek ağ topolojisine uyarla *(High — iki hat)*

Exact IP ve /24 kovaları CGNAT arkasındaki mobil kullanıcı kitlesini kilitliyor; ayrıca
`EndpointExtensions.cs:64-78` ağ kovasını **principal kontrolünden önce** koşulsuz artırıyor.

**Yap:** ağ kovasını principal kontrolünden sonra ve yalnız kabul edilen isteklerde artır;
IPv4 için /24 yerine daha geniş bir kova veya principal-ağırlıklı bir model kullan;
`docs/cache-strategy.md`'deki olgusal olarak yanlış NAT ifadesini düzelt.

### 6. Kalan High'lar

`RequireLinuxRoot` dinamik skip'i xunit 2.9.2'de **skip değil FAIL** üretiyor (CI unit kapısı) ·
`CalendarPlanMaterializer` 2. günde `materialized_plan_conflict` · TCMB coverage guard yalnız son
günü denetliyor · TwelveData/TCMB aynı-gün "henüz yayınlanmadı" kaçış yolu yok → kalıcı blok ·
`retire` sonrası `ensure`/migrator v1'e pinli kaldığı için deploy kırılıyor ·
`restic-wal-observation-smoke` Linux CI'da `PermissionError` ·
`database-role-credential-lifecycle.md` yanlış secret yolu veriyor · activity-log varsayılan
`FatalHost` dalı. Tümü: [01-findings-critical-high.md](01-findings-critical-high.md).

## Birinci sınıfa giden fark

`excellence-gap` olarak **191 kayıt** üretildi — bunlar defect değildir, yayın kararını
etkilemezler, ama kod tabanını birinci sınıftan ayıran şeydir. Boyut dağılımı:

| Boyut | Kayıt |
|---|---:|
| İşletilebilirlik | 66 |
| Test kalitesi | 62 |
| Geliştirici deneyimi | 29 |
| Ürün deneyimi | 21 |
| Güvenlik derinliği | 17 |
| Dokümantasyon | 15 |
| Sadelik ve tekrar | 14 |
| Diğer | 36 |

En yüksek getirili üç tema:

1. **Ürün sözleşmesi tutarlılığı** — `ProblemDetails.field` bazen PascalCase bazen camelCase;
   iki farklı 404 şekli; 503/429 yanıtlarında `Retry-After` yok;
   `GET /v1/scenarios/page` OpenAPI'de `limit`/`cursor` bildirmiyor (codegen kırılıyor).
   Bir istemci geliştiricisi bir endpoint'i öğrenince diğerini tahmin edemiyor.
2. **Operatör ergonomisi** — CLI'lar `argument_required` gibi *hangi* argüman olduğunu söylemeyen
   kodlar döndürüyor, `--help` yok; yeni fail-closed kapıların çoğu için "tetiklenirse ne yapılır"
   cevabı runbook'ta yok; yeni security admission metriği ölçülüyor ama alarma bağlanmamış.
3. **Tekrar** — `CanonicalJson` hâlâ iki kopyada ve `MaxDepth` sözleşmeleri farklı; aynı admission
   gövdesi iki kez, aynı reason→string eşlemesi üç kez; `load_validator` helper'ı beş Python
   self-test'inde kopyalanmış; migration sayısı sekiz noktada elle senkron.

Tam liste ve her biri için "nasıl kapanır": [07-excellence-roadmap.md](07-excellence-roadmap.md).

## Doğrulanan güçlü kararlar

- **Önceki Critical'ların ikisi de gerçek anlamda kapatılmış** — üstelik sınıf düzeyinde:
  `deploy-release.sh` inline map'ten kurtulup tek-kaynak ilkesine geçmiş ve `loki`/`data_repair`
  için negatif regresyon testi kazanmış.
- **Port boundary bypass'ı savunma derinliğiyle kapatılmış:** `NormalizePath` segment tabanlı
  normalizasyon + `OrdinalIgnoreCase` karşılaştırma.
- **Provider gövde timeout'u iki katmanlı çözülmüş:** pipeline'a 3 dk `TotalRequestTimeout` geri
  gelmiş, ayrıca worker tarafında lease-aware mutlak bütçe eklenmiş.
- **`SaydinActivityLogLoss` alert'i düzeltilmiş ve testle kilitlenmiş** (`inventory.test.yml`).
- **DataRepair guard kapsamı ratchet'lenmiş:** `RepairGuardIntegrationTests` gerçek PG üzerinde
  önceki bulgunun tüm önerilerini karşılıyor.
- **Coverage kapıları gevşetilmemiş** — global eşikler `f9f608d` ile birebir aynı, üstüne
  `Saydin.Api.Services` için yeni bir critical-namespace tabanı eklenmiş.
- **+189 yeni test**, hepsi sıfır skip.

## Raporlar

| Rapor | İçerik |
|---|---|
| [00-review-plan.md](00-review-plan.md) | Kapsam matrisi, 19 hat, boyutlar, kapsam dışı |
| [01-findings-critical-high.md](01-findings-critical-high.md) | 2 Critical + 18 High, tam kanıt |
| [02-findings-medium.md](02-findings-medium.md) | 121 Medium |
| [03-findings-low.md](03-findings-low.md) | 144 Low |
| [04-mechanical-gates.md](04-mechanical-gates.md) | Build, test, Critical'ların ampirik doğrulaması |
| [05-lane-summaries.md](05-lane-summaries.md) | Hat kapsamları, reddedilen iddialar, güçlü kararlar |
| [06-remediation-audit.md](06-remediation-audit.md) | Önceki review bulgularının gerçek kapanma denetimi |
| [07-excellence-roadmap.md](07-excellence-roadmap.md) | 191 `excellence-gap` — birinci sınıfa giden fark |
| [08-remediation-execution.md](08-remediation-execution.md) | Uygulanan aksiyonlar, güncel kanıt ve açık riskler |
| [09-ci-gate-remediation.md](09-ci-gate-remediation.md) | PR #16 kırmızı CI kapıları, SonarCloud ve Codacy bulgu kapanışı |

## Yöntem sınırı (dürüstlük kaydı)

19 hattın **15'inde** bağımsız doğrulayıcı koştu. `R09`, `R10`, `R16`, `R18` doğrulayıcıları
**oturum limiti** nedeniyle koşamadı; bu hatlardaki **58 kayıt** yalnız üreten agent'a dayanır ve
raporlarda `DOĞRULANMADI (yalnız üreten agent)` olarak işaretlidir. Bu dört hattaki High kayıtlar
ana agent tarafından elle denetlendi (`R16`/`R18`'in High'ı zaten `R15` doğrulayıcısı tarafından
`CONFIRMED` edilmişti). Bu 58 kayıt, doğrulanmış 227 kayıtla **aynı güven düzeyinde değildir**.

Ayrıca: gerçek PostgreSQL/Redis gerektiren integration testleri çalıştırılmadı (review salt-okunur
tutuldu); üretim telemetrisi, canlı provider davranışı ve Saydın meta repo'su erişim dışıdır.
