# PR Review — `development` @ `f9f608d`

> **Durum notu (2026-08-24):** Bu dosya review anındaki snapshot'tır; aşağıdaki bulgu ve yayın
> değerlendirmeleri başlangıç durumunu anlatır. Repo remediation'ı tamamlandı. Güncel exact test
> kanıtları ve yalnız dış ortamda kalan release koşulları için
> [07-remediation-progress.md](07-remediation-progress.md) esas alınır.

> **Review tarihi:** 2026-08-20
> **Hedef:** `git diff a274c62..f9f608d` — 554 dosya, ~92,5 bin eklenen satır (snapshot arşivi hariç)
> **Yöntem:** 22 paralel uzman hattı + hat başına bağımsız doğrulayıcı agent + ana agent mekanik kapıları
> **Kaynak:** 44 agent, 2.369 tool çağrısı, 0 agent hatası

## Review anındaki yayın kararı

**Review anında bu commit üretime alınamazdı — sebep kod kalitesi değil, kontrol düzlemi
regresyonlarıydı.** Bu repo-içi regresyonlar artık giderildi; production promotion'ın güncel
blocker'ları operatör ortamında üretilecek dış receipt'lerdir.

Değişikliğin özü (installation principal modeli, ingestion window ledger + DB write fence, kolon
düzeyinde privilege separation, imzalı DQA/DataRepair kanıt zinciri, resmî takvim otoritesi,
supply-chain sertleştirmesi) **yüksek kalitede ve büyük ölçüde doğru uygulanmıştır**. Solution
0 warning ile derleniyor, 945 unit test sıfır skip ile geçiyor, mimari yasak listesinde ihlal yok
ve migration değişmezliği korunmuş.

Buna karşılık **iki Critical regresyon**, kodun kendisinin değil onu çalıştıran boru hattının
kırılmasından geliyor ve ikisi de ampirik olarak yeniden üretildi:

1. Kök `docker-compose.yml` kontrol düzlemi ayağa kalkmıyor → CLAUDE.md'nin **zorunlu kıldığı**
   yerel build/çalıştırma/test akışının tamamı ölü.
2. `deploy-release.sh` manifest bağlama adımı her zaman `KeyError` veriyor → **hiçbir** staging
   deploy'u veya üretim promotion'ı tamamlanamıyor.

Her ikisi de CI yeşil olduğu için görünmez kalmış: CI kendi compose dosyasını kullanıyor ve
deploy script'i CI'da hiç çalıştırılmıyor.

## Bulgu envanteri

| Önem | Adet | Rapor |
|---|---:|---|
| **Critical** | 2 | [01-findings-critical-high.md](01-findings-critical-high.md) |
| **High** | 14 | [01-findings-critical-high.md](01-findings-critical-high.md) |
| **Medium** | 56 | [02-findings-medium.md](02-findings-medium.md) |
| **Low** | 149 | [03-findings-low.md](03-findings-low.md) |
| **Toplam** | **221** | |

Bu tablo kaynak raporun numaralı envanteridir. Remediation planı severity drift ve mükerrer
kapsamları kaybetmeden `2 Critical + 16 High-priority + 54 Medium + 149 Low` olarak normalize eder;
ayrıca bir release-sequencing koşulu izler.

Ham üretim 200 bulguydu; doğrulayıcılar **6 tanesini reddetti** (gerekçeleriyle
[05-lane-summaries.md](05-lane-summaries.md) içinde), **28 yeni bulgu** ekledi. Kaynak özette geçen
“5 `PLAUSIBLE`” toplamı tekil bulgu ID'lerine geri izlenemediği için remediation durum hesabında
kullanılmıyor. Ana agent iki bulguyu Critical'a yükseltti ve bir mükerrer kaydı birleştirdi.

## P0 — Merge/yayın öncesi zorunlu

### 1. Kök `docker-compose.yml` kontrol düzlemini onar *(Critical)*

`BootstrapOptions` `--backup-v1-valid-until` + `--backup-password-file`, `MigratorOptions`
`SAYDIN_BACKUP_V1_VALID_UNTIL` zorunlu kılıyor; kök compose'da "backup" kelimesi **hiç geçmiyor**.

```
role-bootstrap failed: code=argument_required   EXIT=64
migration rejected:   code=argument_required    EXIT=3
```

`database-role-bootstrap` exit 64 → `database-migrator` (`service_completed_successfully`) hiç
çalışmaz → `saydin-api`, `price-ingestion`, `pgadmin`, `postgres-exporter` ve `tests` profili
başlamaz. `bootstrap-dev-database.sh` de `backup-v1` secret'ını üretmiyor.

**Yap:** compose'a eksik argümanları/env'i ekle, dev secret zincirini `backup-v1` üretecek şekilde
genişlet, ve CI'a "kök compose ile stack ayağa kalkıyor mu" duman testi koy.

### 2. `deploy-release.sh` runtime image eşlemesini tamamla *(Critical)*

`release-manifest.schema.json` `runtimeImages` için 11 anahtarı `required` yapıyor;
`deploy-release.sh:40-42`'deki `runtime` sözlüğünde `loki` ve `tempo` yok → `KeyError` →
`deployment_manifest_binding_failed` (exit 78). Staging ve production deploy'u hiç ilerleyemiyor.

**Yap:** iki anahtarı ekle ve `release-manifest-self-test.py`'ye şema `required` kümesi ile
script'in `runtime` map'inin **birebir eşit** olduğunu doğrulayan bir test ekle.

### 3. Required CI ingestion-ledger kapısını yeşile döndür *(High)*

`PriceAuthorityMigrationIntegrationTests.cs:120` `schema_migrations count = 23` sabitini taşıyor;
ağaçta 24 migration var ve hem fixture readiness probe'u hem CI fresh-schema kapısı 24 bekliyor.
Required `integration-test` job'ındaki `ingestion-ledger-tests` adımı bu yüzden kırmızı.

**Yap:** sabiti 24'e çek; daha iyisi, sayıyı `MigrationTrustRoot.Checksums` uzunluğundan türet ki
her yeni migration'da bir daha kırılmasın.

### 4. Restore drill'i çalışır hale getir *(High)*

`restore-drill.sh:112-115` `--user 0:0` ile birlikte `--cap-drop ALL` kullanıyor; taze volume
root:root olduğu için `chown 1001:1001` her koşuda EPERM veriyor ve `set -eu` altında drill
restic restore'a hiç ulaşamıyor. Bu, projenin **tek** PITR/DR kanıt mekanizması ve
`promote-production.yml` üretim admission'ı ≤31 günlük imzalı restore receipt şart koşuyor.

**Yap:** `--cap-add CHOWN` (veya volume'ü doğru uid ile yaratan bir yaklaşım) ekle ve drill'i
CI'da gerçekten koştur.

### 5. Backup dayanıklılığını düzelt *(High ×2)*

- `base_backup` tüm PGDATA'yı yalnız 2 GiB `tmpfs` olan `/tmp`'e açıyor, container `mem_limit: 1g`.
  Veri dizini ~1 GiB'a yaklaştığında base backup kalıcı olarak OOM/ENOSPC veriyor; WAL retention
  14 gün olduğu için son base 14 günü aştığında **PITR tamamen imkânsız** hale geliyor.
- WAL yalnız segment sınırında off-host'a gidiyor (`*.partial` hariç, `archive_timeout` yok);
  ilan edilen 15 dk RPO garanti edilmiyor ve WAL freshness metriği bunu ölçmüyor.

### 6. Monitoring düzlemini deploy'a bağla *(High ×2)*

- `deploy-release.sh` Prometheus, Alertmanager ve dört exporter'ı hiç başlatmıyor; hiçbiri
  başlatılan servislerin bağımlılığı değil ve doğrulayan kapı yok — deploy yine de "passed".
- `SaydinActivityLogLoss` alert'i Prometheus vector aritmetiğinde label uyuşmazlığı nedeniyle
  **yapısal olarak hiç tetiklenemiyor** (promtool ile ampirik doğrulandı). ADR-006 kapsamındaki
  finansal denetim izinin kayıp alarmı ölü.

### 7. API kimlik/izolasyon açıklarını kapat *(High ×2)*

- **Management port sınırı trailing-slash ile atlatılabiliyor:** `ApiPortBoundaryMiddleware`
  normalize edilmemiş yol üzerinde tam eşitlik yapıyor; `GET /metrics/` ve `GET /health/ready/`
  public port'tan erişilebilir. Caddy'nin `@internal` exact-path kuralı da aynı şekilde atlanıyor.
- **Kimlik doğrulamasız principal üretimi günlük kotayı sıfırlıyor:** `POST /v1/installations`
  cap'siz; tek ek istekle yeni principal mint edip 20 istekli free kotayı sıfırlamak mümkün
  (üst sınır yalnız IP başına 60 istek/dk) ve her sıfırlama kalıcı `users` +
  `installation_credentials` satırı bırakıyor. REM-API-02 kabul kapısı kapanmamış.

### 8. Ingestion dayanıklılığını sertleştir *(High ×3)*

- Tek bir asset'in `permanent_failed` window'u **tüm** ingestion sürecini düşürüyor ve
  `restart: unless-stopped` altında sınırsız crash-loop üretiyor.
- Ledger'ın `next_attempt_at` sözleşmesi (5 dk / 30 dk) worker zamanlayıcısı tarafından
  yok sayılıyor; fiili retry gecikmesi 24 saat (price) / 1 ay (EVDS).
- Provider gövde okuması hiçbir wall-clock timeout'a bağlı değil (`HttpClient.Timeout = Infinite`,
  `TotalRequestTimeout` bu commit'te silinmiş); askıda bir bağlantı worker'ı süresiz kilitliyor.

### 9. DCA reel getirisini kullanılabilir hale getir *(High)*

API-04 ile gelen exact-CPI terminal sözleşmesi terminal ayı **fiyat verisinin son gününden**
türetiyor; varsayılan (endDate boş) istekte bu her zaman içinde bulunulan ay oluyor ve o ayın
CPI'ı ne planlanıyor ne de TÜİK tarafından yayınlanmış oluyor → reel getiri alanları **kalıcı
olarak null** ve bu istekler hiç cache'lenmiyor.

### 10. Kalan High'lar

- Backup login `VALID UNTIL` marker'a pinlenmiş; süre dolduğunda her production `ensure` exit 69
  ile kilitleniyor ve belgelenmiş kurtarma yolu yok.
- `DataRepair`'in 77 fail-closed reject kodundan **60'ı** hiçbir testte tetiklenmiyor — üretim
  ledger'ını mutasyona uğratan tek aracın güvenliği tamamen bu guard'lara dayanıyor.

## Kayda değer güçlü kararlar

Review yalnız negatif bulgudan ibaret değil. Bağımsız olarak doğrulanan üst düzey işler:

- **Kimlik:** ham credential DB'ye hiç gitmiyor (in-process CSPRNG + HMAC verifier); base64url
  çözümü kanonik (credential aliasing kapalı); iki fazlı rotation principal başına advisory lock
  altında atomik; malformed/bilinmeyen/revoked hepsi tek tip 401 (enumeration kapalı).
- **Veri bütünlüğü:** migration 016 write fence, canlı lease + asset/source/job/date scope ile
  DB sınırında fail-closed. Migration 019/020/021 **kolon düzeyinde** GRANT kullanıyor;
  `ingestion_cap`'e `price_points` üzerinde DELETE verilmiyor. Installation credential tablosuna
  doğrudan erişim yok — yalnız `search_path` kilitli `SECURITY DEFINER` fonksiyonları.
- **EF ↔ SQL paritesi:** finansal kolonlarda precision paritesi kusursuz (`numeric(18,6)` ↔
  `HasPrecision(18,6)`); `ingestion_windows`'un 25 kolonu, 10 CHECK'i ve 3 index'i birebir tutuyor.
- **Migrator:** checksum'lar normalize edilmiş değil **ham** byte üzerinden; `--verify-only`
  gerçekten salt-okunur; crash-safety için kalıcı `running` işareti + tek runner-owned transaction.
- **Provider katmanı:** typed `AdapterOutcome`/`AdapterCompleteness` "boş liste = başarı"
  belirsizliğini kapatıyor; beş mapper en-US/tr-TR/th-TH/ar-SA altında byte-özdeş çıktı üretiyor
  (kültür regresyonu mühürlenmiş); secret yalnız header'da.
- **Supply chain:** 16/16 action commit SHA ile pinli, tüm workflow'lar `permissions: {}` ile
  başlıyor, `continue-on-error`/`|| true` yok, `verify-integration-trx.py` gerçekten fail-closed
  (total==executed==passed + 13 yasak counter + zero-skip).
- **Production topolojisi:** yalnız Caddy 80/443 yayımlıyor; `read_only` + `cap_drop: ALL` +
  `no-new-privileges` + non-root uid tüm servislerde; secret'lar environment/argv'de değil,
  tüketici başına ayrı read-only volume + `*_FILE`.

## Raporlar

| Rapor | İçerik |
|---|---|
| [00-review-plan.md](00-review-plan.md) | Kapsam matrisi, 22 hat, severity ölçütü, kapsam dışı bırakılanlar |
| [01-findings-critical-high.md](01-findings-critical-high.md) | 2 Critical + 14 High, tam kanıt/etki/öneri |
| [02-findings-medium.md](02-findings-medium.md) | 56 Medium |
| [03-findings-low.md](03-findings-low.md) | 149 Low (tablo) |
| [04-mechanical-gates.md](04-mechanical-gates.md) | Build, test, yasak-liste, kritik bulgu yeniden üretimleri |
| [05-lane-summaries.md](05-lane-summaries.md) | Hat bazlı kapsam, reddedilen iddialar, güçlü kararlar, açık sorular |
| [06-remediation-action-plan.md](06-remediation-action-plan.md) | Tüm bulgular için kritik→düşük, bağımlılık ve kabul kapısı odaklı uygulama planı |
| [07-remediation-progress.md](07-remediation-progress.md) | Uygulanan paketler, gerçek test sayaçları, residual risk ve açık kabul kapıları |

## Yöntem notu ve sınırlar

- Her bulgu, üreten agent'tan bağımsız ikinci bir agent tarafından kod okunarak doğrulandı;
  şüphede REJECTED'a meyilli davranıldı. `PLAUSIBLE` işaretli 5 bulgu repo dışı doğrulama ister.
- Gerçek PostgreSQL/Redis gerektiren integration testleri **çalıştırılmadı** —
  `bootstrap-dev-database.sh` yerel makinede secret/volume üretecek bir yan etki oluşturur ve
  bu review salt-okunur tutuldu. Bu nedenle integration davranışı kod okuması ve CI
  konfigürasyonu üzerinden değerlendirildi.
- `tools/calendar-data/data/snapshots/**` (~800 HTML/PDF) ve `data/normalized/*.csv` içerik
  olarak review dışıdır; yalnız store/replay mekanizması incelendi.
- Üretim telemetrisi, canlı provider davranışı ve Saydın meta repo'su erişim sınırı dışındadır;
  bunlardan doğan sorular hat bazlı "açık sorular" bölümlerinde residual risk olarak kayıtlıdır.
