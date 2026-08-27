# PR #16 — CI, Sonar ve Codacy Kapı Onarımı

> **Tarih:** 2026-08-27 · **Kapsam:** PR #16 (`development` → `main`) üzerindeki kırmızı
> GitHub Actions job'ları ile SonarCloud / Codacy bulguları.
> Bu dosya [08-remediation-execution.md](08-remediation-execution.md)'in devamıdır;
> oradaki Critical/High kapanışı değişmemiştir.

---

## 1. Kırmızı kapılar ve kök nedenleri

| Kapı | Belirti | Kök neden | Aksiyon |
|------|---------|-----------|---------|
| Integration tests (TimescaleDB + Redis) | 67 testin 12'si `Npgsql.PostgresException 42501: permission denied for table activity_logs` | `EfActivityLogBatchStore` idempotent yazımı `ON CONFLICT (id,created_at)` **hedefli** arbiter kullanıyordu; TimescaleDB hypertable'ında index inference `SELECT` yetkisi ister, migration 019 ise API capability rolüne yalnız `INSERT` verir | Arbiter kaldırıldı → `ON CONFLICT DO NOTHING` |
| Dependency, license, vulnerability, secret and IaC gates | `Dependency review is not supported on this repository` (5 sn'de fail) | Repo'da Dependency Graph kapalıydı | `PUT /repos/…/vulnerability-alerts` ile Dependabot alerts + Dependency Graph açıldı |
| SonarCloud Code Analysis | Quality Gate: `new_reliability_rating=4`, `new_security_rating=5`, `new_duplicated_lines_density=4.9%` | 2.663 bulgunun 1.919'u analiz kapsamı dışında kalması gereken artefaktlarda | Kapsam daraltıldı + gerçek bug/vulnerability'ler düzeltildi |
| Codacy Static Code Analysis | 169 yeni bulgu (eşik 0) | Ağırlıklı olarak kontrol-düzlemi ve self-test script'lerinde yapısal yanlış-pozitif | Gerçek olanlar düzeltildi; artefakt yolu hariç tutuldu; kalanlar için Codacy tarafında pattern kararı gerekiyor (§4) |

---

## 2. `activity_logs` yazma yolu — yetki sözleşmesi (KRİTİK)

`INSERT … ON CONFLICT` bir **arbiter** aldığında (ister `(id,created_at)` ister
`ON CONSTRAINT activity_logs_pkey`) PostgreSQL index inference yapar; TimescaleDB
hypertable'ında bu çıkarım `public.activity_logs` üzerinde `SELECT` gerektirir.
Migration 019 API capability rolüne kasıtla **yalnız `INSERT`** verir (audit izi API
için yazılır-okunmaz). Sonuç: hedefli arbiter fail-closed `42501` üretiyordu,
`ActivityLogWriter` bunu `FatalHost` olarak sınıflandırıp host'u durduruyordu — bu
yüzden 12 test, biri hariç, aynı tek kök nedenin `ObjectDisposedException` türevleriydi.

Ampirik doğrulama (`timescale/timescaledb:2.16.1-pg16`, yalnız `INSERT` yetkili rol):

| Biçim | Sonuç |
|-------|-------|
| `INSERT …` (düz) | `INSERT 0 1` |
| `INSERT … ON CONFLICT (id,created_at) DO NOTHING` | `ERROR: permission denied for table activity_logs` |
| `INSERT … ON CONFLICT ON CONSTRAINT activity_logs_pkey DO NOTHING` | `ERROR: permission denied for table activity_logs` |
| `INSERT … ON CONFLICT DO NOTHING` (hedefsiz) | `INSERT 0 1`, tekrarda `INSERT 0 0` |
| `GRANT SELECT (id,created_at)` + hedefli arbiter | çalışır — ama yazılır-okunmaz sözleşmesini genişletir |

Aynı davranış, migration 001–025'in tamamı uygulanmış **canlı development veritabanında**
gerçek `…_api_login_v1` rolüyle de doğrulandı (`information_schema.role_table_grants`:
`api_cap → INSERT`, tek satır):

```text
SET ROLE …_api_login_v1;
INSERT … ON CONFLICT (id,created_at) DO NOTHING;  -- ERROR: permission denied for table activity_logs
INSERT … ON CONFLICT DO NOTHING;                  -- INSERT 0 1
INSERT … ON CONFLICT DO NOTHING;  (aynı satır)    -- INSERT 0 0   → tabloda tam 1 satır
INSERT … action='not_a_database_action' …         -- ERROR: activity action rejected  (toxic-row yolu korunuyor)
```

**Seçilen çözüm:** hedefsiz `ON CONFLICT DO NOTHING`. Yetki grafiği hiç değişmez, yeni
migration gerekmez, ACL fingerprint'i sabit kalır. Burada birebir eşdeğerdir çünkü
`pk(id,created_at)` tablodaki tek unique/exclusion kısıtıdır. CHECK ihlalleri
(`chk_activity_action`) `ON CONFLICT` kapsamında olmadığından toxic-row bisection yolu
korunur — `ManagedApi_ToxicConstraintRowIsBisected…` testinin ölçtüğü davranış budur.

> **Bakım notu:** `activity_logs` üzerine yeni bir unique index eklenirse bu eşdeğerlik
> bozulur ve hedefsiz form istenmeyen susturma yapar. O noktada ya kolon-düzeyinde
> `GRANT SELECT (id,created_at)` ile hedefli arbiter'a dönülmeli ya da idempotency
> başka bir mekanizmaya taşınmalıdır. Detay: [`activity-logging.md` §4.3](../../architecture/activity-logging.md).

---

## 3. SonarCloud

### 3.1 Analiz kapsamı — `.sonarcloud.properties`

2.663 bulgunun **1.919'u** iki yolda toplanıyordu:

- `tools/calendar-data/data/**` — SHA-256 ile pinlenmiş, birebir yakalanmış üçüncü taraf
  takvim sayfaları (274 HTML) ve JSON manifestleri. Bunlar kanıt artefaktıdır; bir lint
  kuralı için düzenlenmeleri offline doğrulama zincirini bozar. Rapor edilen 587 BUG'ın
  548'i buradaki HTML erişilebilirlik kuralları (`Web:S5254`, `Web:S5256`) ve
  **new code duplication'ının tamamı** (~4.800 satır) bu dizinden geliyordu.
- `infrastructure/postgres/migrations/**` — değişmez, sıralı migration'lar. Uygulanmış bir
  migration asla düzenlenmez (CLAUDE.md), dolayısıyla 141 `plsql:S1192` yapısal olarak
  aksiyona alınamaz. `.codacy.yaml` ve `.coderabbit.yaml` aynı yolu zaten hariç tutuyordu.

### 3.2 Düzeltilen gerçek bulgular

| Kural | Konum | Düzeltme |
|-------|-------|----------|
| `python:S5850` (BUG) | `validate-observability.py:191,200` | Lookahead içindeki alternasyon `(?:…)` ile gruplandı; `^` yalnız sol dala uygulanma belirsizliği kalktı |
| `csharpsquid:S2583` (BUG) | `MigrationRunner.cs:1042` | `finalState` bir `const` olduğundan `finalState == "succeeded"` dalı ölüydü; ternary kaldırıldı |
| `python:S5852` | `validate-production.py:16` | `DIGEST` regex'inde segment sınıfı `[^\s@]` → `[^\s@:/]`; ayrıştırma tek-anlamlı ve doğrusal oldu. Eşdeğerlik 11 örnek ve repo'daki 12 digest-pinned image üzerinde doğrulandı |
| `csharpsquid:S6444` ×13 | `MigrationImpactManifest`, `MigrationManifest`, `BootstrapOptions`, `RoleContract`, `RuntimeDatabase`, `CalendarAcquisition` | Yeni `Saydin.DatabaseSecurity.RegexTimeouts.Default` (1 sn) her `Regex`'e match timeout olarak verildi |
| `python:S2068` ×10 | `validate-private-material.py` | Secret **format** etiketleri (`"scalar"`, `"json"`, …) adlandırılmış sabitlere bağlandı; `validate_content` da aynı sabitleri kullanıyor, literal drift'i kapandı |
| `PyLint W0102` | `monitoring-runtime-self-test.py:56` | Mutable default (`series_value: dict = series`) → `None` + fonksiyon içinde çözümleme |
| `Prospector pyflakes` | `backup-static-self-test.py:9` | Kullanılmayan `import shutil` kaldırıldı |
| `csharpsquid:S6966` ×7 | `BaseAssetWorker`, `EvdsInflationWorker`, `IngestionOrchestrator` | `cts.Cancel()` → `await cts.CancelAsync()` |
| `csharpsquid:S1854` | `RepairDatabase.cs:269` | `RollbackCasAsync` sonucu 6 satır sonra üzerine yazılıyordu; atama kaldırıldı, CAS yan etkisi korundu |
| `csharpsquid:S1125` ×2 | `PriceRepository.cs:237`, `AuditRunner.cs:669` | `bool?` karşılaştırmaları `is true` / `is not true` pattern'ine çevrildi |

### 3.3 Gerekçelendirilerek bastırılanlar (`NOSONAR`)

| Kural | Konum | Gerekçe |
|-------|-------|---------|
| `csharpsquid:S6418` (BLOCKER) | `EmbeddedMigrations.cs:27` | Değer, `024_installation_credential_rehash.sql`'in SHA-256 içerik digest'idir. Sır değil; migration drift'ini tespit edilebilir kılan **bütünlük çıpasıdır** |
| `csharpsquid:S5344` | `PostgresScramSha256Verifier.cs` | `Iterations = 4096`, PostgreSQL'in kendi `scram_iterations` varsayılanıdır. Bu bir password-at-rest KDF'i değil, sunucunun aynı sayıyla yeniden ürettiği bir **authentication verifier**'dır; sayıyı yükseltmek `PASSWORD`/`\password` ile üretilen verifier'dan sapardı (interop sabiti) |
| `python:S5443` | `validate-development-compose.py` | `/tmp/saydin-ingestion-` bu script'in açtığı bir dizin değil, `docker-compose.yml`'e karşı doğrulanan **sözleşme değeridir** |
| `python:S5332` | `monitoring-runtime-self-test.py` | `http://metadata.invalid/` kasıtlı olarak TLS'siz ve çözülemez bir mutasyon hedefidir; https yapmak testin ölçtüğü özelliği yok eder |
| `yaml:S2068` | `ci.yml:223` | Blok içindeki her parola `openssl rand` ile çalışma anında üretilip `::add-mask::` ile maskelenir; literal olan yalnız connection-string **anahtar adıdır** |

### 3.4 Kapatılmayanlar ve nedeni

| Kural | Adet | Neden kapatılmadı |
|-------|------|-------------------|
| `csharpsquid:S8970` | 129 | **Yanlış-pozitif.** "nullable warnings are disabled here" diyor; oysa `Directory.Build.props` repo genelinde `<Nullable>enable</Nullable>` set eder. SonarCloud **Automatic Analysis** MSBuild çalıştırmadığı için bu property'yi görmez. Bu `!` operatörlerini silmek `TreatWarningsAsErrors=true` altında build'i kırar. Yapısal çözüm §5'te |
| `csharpsquid:S125` | 9 | **Yanlış-pozitif.** Hepsi açıklayıcı düzyazı yorum; "commented out code" değil |
| `csharpsquid:S2589`, `S1994`, `S3459` | 11 | Fail-closed savunma tekrarı ve ORM materyalizasyonu. "Düzeltmek" güvenlik marjını azaltır |
| `csharpsquid:S6667` | 38 | Adapter catch blokları exception'ı kasıtla loglamıyor (sağlayıcı mesajı API key/URL taşıyabilir). Doğru düzeltme `ProviderExceptionSanitizer.ForLog(ex)` kullanmaktır — ama sanitizer'ın kendisi açık **Medium #30** kaydıdır; ikisi birlikte ele alınmalıdır |
| `csharpsquid:S3776` (58), `S107` (22), `S1192` (40), `S3267` (16), `S3358` (15), `S127` (10) | 161 | Gerçek ama kapsamlı refaktör. 904 dosyalık bir PR'a eklenmesi regresyon riskini kabul edilemez kılar; ayrı bir PR'a alınmalı |
| `shelldre:S131` (41), `S7679` (26), `S1192` (8) | 75 | Aynı gerekçe; shell kapı script'lerinde ayrı bir tur |

---

## 4. Codacy

169 bulgunun dağılımı: 30 `Semgrep csharp SQLInjection`, 24 `Bandit B105`, 18 `Bandit B603`,
14 `SonarCSharp S2068`, 12 `Semgrep dangerous-subprocess-use` ve kuyruk.

- **Düzeltilenler:** §3.2'deki Python bulguları (mutable default, kullanılmayan import) ile
  `validate-private-material.py` sabitleri Codacy tarafında da kapanır.
- **Hariç tutulan yol:** `tools/calendar-data/data/**` `.codacy.yaml`'a eklendi.
- **Kalanlar yapısal yanlış-pozitiftir ve kod tarafında kapatılamaz:**
  - `Semgrep csharp_injection_rule-SQLInjection` (30) — kontrol-düzlemi kodu tanımlayıcıları
    `pg_catalog.format('… %I', …)` / `quote_ident` ile parametreleştirir; Semgrep dinamik SQL
    metnini enjeksiyon sanıyor. Repo kuralı zaten string interpolation'ı yasaklıyor.
  - `Bandit B105` (24) — `"postgres_secret": "postgres"` gibi **volume→servis** ve
    `"password": SCALAR` gibi **dosya→format** eşlemeleri; parola değil.
  - `Bandit B603/B404` + `Semgrep dangerous-subprocess-use` (30) — self-test script'lerinin
    `docker`/`psql` çağrıları. Girdi repo-kontrollüdür ve `shell=False` kullanılır.
  - `SonarCSharp S2068` (14) — test connection string'lerindeki ephemeral CI parolaları.

  Bunlar için doğru aksiyon **Codacy repository ayarlarında bu pattern'leri devre dışı
  bırakmak** ya da PR eşiğini yükseltmektir; `.codacy.yaml` pattern-düzeyinde devre dışı
  bırakmayı desteklemez (yalnız `exclude_paths`).

---

## 5. Açık yapısal öneri — Sonar analiz yöntemi

SonarCloud bu repo'da **Automatic Analysis** ile çalışıyor (workflow'da scanner adımı yok).
Automatic Analysis MSBuild çalıştırmaz; bu yüzden `Directory.Build.props`'taki
`<Nullable>enable</Nullable>` görünmez ve tek başına **129 yanlış-pozitif** (`S8970`) üretir.
Doğru çözüm, mevcut `build-and-test` job'ının içinde **SonarScanner for .NET** çalıştırmaktır:

1. SonarCloud UI → Administration → Analysis Method → *Automatic Analysis* kapat.
2. Repo secret'ı olarak `SONAR_TOKEN` ekle.
3. `build-and-test` job'ına `dotnet sonarscanner begin/end` sarmalı ekle — bu aynı zamanda
   üretilen Cobertura coverage'ını da Sonar'a taşır.

Bu adım repo dışı yetki gerektirdiğinden bu PR kapsamında **yapılmamıştır**.

---

## 6. Repo ayarı değişikliği

`PUT /repos/cemililik/Saydin.Services/vulnerability-alerts` ile Dependabot alerts açıldı;
bu Dependency Graph'ı da etkinleştirir. Doğrulama: `GET /repos/…/dependency-graph/sbom`
önce `404`, sonra 38 paketle `200` döndü. Otomatik güvenlik PR'ları (
`dependabot_security_updates`) **açılmadı** — ayrı bir karardır.

---

## 7. Doğrulama kanıtı (lokal)

| Kapı | Sonuç |
|------|-------|
| Pinned SDK solution build (`Debug`) | **Build succeeded — 0 Warning(s), 0 Error(s)** |
| `Saydin.Api.Tests` | 658/658 passed, 0 failed, 0 skipped |
| `Saydin.PriceIngestion.Tests` | 182/182 |
| `Saydin.DataQualityAudit.Tests` | 97/97 |
| `Saydin.DataRepair.Tests` | 29/29 |
| `Saydin.DatabaseRoleBootstrap.Tests` | 98/98 |
| `Saydin.CalendarData.Tests` | 94/94 |
| `validate-development-compose.sh` | `development_compose_validation_passed:mutations=21` |
| `validate-production-assets.sh` | `production_assets_validation_passed` |
| `backup-static-self-test.py` | `backup_static_self_test_passed:64` |
| `private-material-self-test.py` | `private_material_self_test_passed:11` |
| `validate-workflows.py` | `workflow_validation_passed:files=6` |
| `check-doc-links.py` | `documentation_link_validation_passed:files=99` |
| Canlı dev DB `activity_logs` yazma probu | §2'deki tablo |

`Saydin.DatabaseMigrator.Tests` ve tüm `*.IntegrationTests` lokalde `run-local-tests.sh`
kapsam koruması tarafından reddedilir (purpose-specific credential yok); kanonik koşum
`.github/compose.integration.yml` üzerindeki required CI job'ıdır.
