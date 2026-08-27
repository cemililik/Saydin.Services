# Saydin.Services — Remediation İlerleme ve Kanıt Kaydı

> **Başlangıç:** 2026-08-18  
> **Branch:** yalnız `development`  
> **Plan:** [`05-remediation-action-plan.md`](05-remediation-action-plan.md)  
> **Durum:** bu belge önceki remediation dalgalarının tarihsel kaydıdır. 2026-08-24 tarihli PR
> review kapanış durumu ve exact güncel kapılar için
> [`pr-review/07-remediation-progress.md`](pr-review/07-remediation-progress.md) otoritatiftir;
> production release dış ortam receipt'leri tamamlanana kadar kapalıdır.

## Branch ve çalışma disiplini

- Başlangıçta `origin` fetch edildi. Uzak `development` dalındaki tekil OCI dokümantasyon commit'i
  korunarak `main/9067dd2`, conflict olmadan `development` üzerine merge edildi.
- Başlangıç entegrasyon commit'i `development/a274c62`; `main/9067dd2` bunun doğrudan atasıdır.
- Feature/yardımcı branch açılmadı. Ortak worktree'de aynı dosya kümesine eşzamanlı owner atanmadı.
- Bu kayıt “kod yazıldı” durumunu değil, work item kabul kriterinin gerçekten geçip geçmediğini izler.

## Durum özeti

| Work item | Bulgular | Uygulama | Kabul durumu |
|---|---|---|---|
| RP-01 | PLT-H01, XVR-H01, API-01 | Güvenli OpenAPI bağımlılık grafiği | **Kapalı** |
| ING-001 containment | C-01, H-01'in hata-yutma dilimi | Worker fail-fast, chunk-stop ve exact fault testleri | **Durable ledger tarafından supersede edildi** |
| RP-02 | PLT-H02, XVR-H02, API-17 | Gerçek-infra required CI ve test DB guard | **Kapalı (repo kapsamı)** |
| DBM-001/002/003/005 | C-02, H-08, H-09, M-09 | Always-run migrator ve schema readiness | **Kapalı (repo); rollout residual açık** |
| DBM-004 | M-07 | Büyük migration disk/headroom ve resumable-batch politikası | **Kapalı (repo); canlı kapasite/change-window provası residual** |
| ING-001 durable ledger | C-01, H-11 ve checkpoint/completeness dilimleri | Window ledger, typed outcome, atomik completion ve DB write fence | **Doğrulandı (repo); rollout/audit residual açık** |
| CAL-001 | C-01 authoritative calendar dilimi | Resmî acquisition, offline snapshot replay, immutable release/day authority, schedule ve freshness alert | **Doğrulandı (repo); canlı acquisition/promotion residual açık** |
| API-04 | API-04 | Cash-flow CPI terminal reel DCA matematiği | **Doğrulandı; gerçek-PG tek-sorgu kanıtı kapalı** |
| API-06 | API-06 | Exact finansal/serbest metin log minimizasyonu | **Doğrulandı; sink/retention governance residual** |
| API-07 | API-07, XVR-H03 | Gerçek channel drop callback metriği | **Doğrulandı; dashboard alarmı residual** |
| API-03/05/11 | API-03, API-05, API-11 | Scenario payload/page/atomic hard-cap ve migration 018 | **Doğrulandı (repo)** |
| PRV-001 | Provider authority tüketici dilimi | Migration 020 authority/evidence ve API final-only görünürlük | **Doğrulandı (repo); production backfill/promotion residual açık** |
| API-TRUST-001 | API-02, API-10, PLT-H03 | Installation principal, credential rotation, dağıtık limiter, catalog revision ve principal retention | **Doğrulandı (repo); production rollout residual açık** |
| SUP-001 | H-06 | Fatal worker supervision ve non-zero process exit | **Doğrulandı** |
| DQ-001..008 | Critical data-quality follow-up | İmzalı, salt-okunur audit ve gerçek-PG kanıtı | **Doğrulandı (repo); production rollout residual açık** |

## Paket 1 — Güvenli build ve yeni veri boşluğu containment'ı

### RP-01 — OpenAPI advisory

Değişiklik:

- `Microsoft.AspNetCore.OpenApi` `10.0.11` sürümüne yükseltildi.
- Transitive çözümlemeyi belirsiz bırakmamak için `Microsoft.OpenApi` merkezi olarak `2.11.0`
  sürümüne sabitlendi.
- Audit suppression/ignore veya `NuGetAudit=false` eklenmedi.

Kabul kanıtı:

- Audit açık, temiz cache'li restore ve Release solution build geçti; warning/error yok.
- Altı projenin top-level ve transitive vulnerability sorgusunda vulnerable paket dönmedi.
- No-cache API image build geçti; runtime OpenAPI dokümanı `3.1.1`, 10 path ve 21 schema üretti;
  Scalar yüzeyi açıldı.
- API unit suite'i 286/286 geçti.

Sonuç: RP-01 ve aynı kök nedene bağlı PLT-H01/XVR-H01/API-01 kapalıdır.

### ING-001 — Immediate containment

Değişiklik:

- `BaseAssetWorker` ve `EvdsInflationWorker` adapter, job-start, repository ve job-finalization
  hatalarını artık başarılı/boş sonuç gibi yutmuyor; hata kaydı sonrası exception üst katmana taşınıyor.
- Backfill başarısız chunk'ta duruyor; sonraki chunk çağrılmıyor.
- Base günlük akışındaki transient retry korunurken retry içindeki programlama/non-transient hata
  fail-fast davranıyor.
- Mevcut worker test dosyalarına adapter/start/repository/finalization fault, cancellation,
  all-success, ikinci chunk sonrası stop ve retry semantiği için 14 regresyon testi eklendi.

Kabul kanıtı:

- PriceIngestion suite'i 100/100 geçti; önceki 86 teste 14 exact regresyon testi eklendi.
- Gerçek PostgreSQL/Redis ile tüm solution: **394 passed, 0 failed, 0 skipped**
  (API unit 286 + ingestion 100 + integration 8).
- Fresh DB'de `001`–`014` zincirinin 16 migration kaydı mevcut.
- Ortak diff için `git diff --check` geçti.

Residual risk ve kapanmama gerekçesi:

- Bu değişiklik yeni bir exception kaynaklı chunk'ın sessizce aşılmasını durdurur; fakat `null`, boş veya
  partial provider payload'ının başarı sayılmasını tek başına çözmez.
- `MAX(date)` hâlâ tek fiili ilerleme referansıdır; durable `ingestion_windows` ledger, completeness
  sözleşmesi, restart reconciliation ve production data-quality audit henüz yoktur.
- Bu nedenle C-01 **kapalı değildir**; yalnız immediate containment kabul edilmiştir.

## Paket 2 — Fail-closed gerçek-altyapı CI

### RP-02 — Required integration job ve test hedefi guard'ı

Değişiklik:

- CI unit baseline'ı yalnız iki unit projesini çalıştırır; integration projesi altyapısız job'da
  sessizce skip edilerek toplam sayıya katılmaz.
- Ayrı `integration-test` job'ı digest-pinned TimescaleDB, Redis ve .NET SDK 10.0.400 image'larıyla
  çalışır. Her run UUID-bazlı farklı Compose project, `saydin_test_<uuid>` DB, network ve volume
  kullanır; sabit `container_name` ve host portu yoktur.
- Required mod PostgreSQL host'unu exact `postgres`, DB adını exact run UUID'si; Redis'i exact
  `redis:6379` olarak bağlantı kurulmadan önce doğrular. Eksik env, yanlış hedef, schema veya
  erişilemeyen infra skip yerine non-zero failure üretir.
- Fresh schema gate güncel zincirde 24 migration ve `price_points`/`activity_logs` hypertable'larını bekler.
- API TRX parser en az 31 executed/passed sonuç, tüm non-success sayaçlarında sıfır ve her test sonucunda
  `Passed` ister. Eksik/bozuk/zero/skipped TRX fail-closed davranır.
- Docker image job'ı hem unit hem required integration job'ına bağımlıdır.

Kabul kanıtı:

- İlk native `linux/arm64` gerçek stack: **20/20 passed, 0 failed, 0 skipped**; güncel discovery/
  required gate **31**'dir. Release build 0 warning, 0 error'dır.
- 20 sonucun sekizi gerçek PostgreSQL/Redis davranış testi, 12'si hedef/guard regresyonudur.
- Fresh DB ilk RP-02 kabulünde 16, 015/016 entegrasyonu sonrasında 18, 017 sonrasında 19,
  018 sonrasında 20, privilege separation 019 sonrasında 21 ve provider authority 020 sonrasında 22
  migration ile iki hypertable raporlar.
  PostgreSQL/Redis `PortBindings` boş.
- İki farklı UUID Compose projesi aynı anda çalıştırıldı: dört ayrı container, iki ayrı DB, iki ayrı
  network ve proje-prefix'li volume'lar; güncel gate her DB için 24 migration ister.
- Required modu kapalı runner non-zero çıktı. Eksik, malformed, zero-test ve skipped TRX
  fixture'larının dördü parser tarafından non-zero reddedildi.
- SDK digest'i hem `linux/amd64` hem `linux/arm64` için 10.0.400 olarak smoke edildi; manifest ayrıca
  `linux/arm/v7` taşır.

Sonuç: repository içindeki RP-02/PLT-H02/XVR-H02/API-17 uygulaması kapalıdır. GitHub branch
protection'ta `Integration tests (TimescaleDB + Redis)` status check'inin required seçilmesi repo dışı
governance kanıtı olarak halen açık kalır; workflow içindeki `docker-build` bağımlılığı job'ı CI
akışında şimdiden zorunlu kılar.

## Paket 3 — Fail-closed migration control-plane

### DBM-001..005 / C-02 — Always-run migrator ve schema readiness

Değişiklik:

- `Saydin.DatabaseMigrator`, 001–022 immutable trust-root raw-byte SHA-256 pinleri, physical
  system/database advisory lock'u, deterministic `search_path`, runner-owned transaction ve
  commit-outcome reconciliation ile eklendi. DQA aynı canonical trust-root sınıfını derler.
- Runner-owned control-plane ve tarihsel migration transaction'ları declared
  `search_path=public,pg_temp` kullanır: `pg_catalog` explicit listede olmadığında PostgreSQL onu
  lookup için implicit ilk sıraya koyarken tarihsel unqualified `CREATE` hedefi `public` kalır.
  Fully-qualified 019 gövdesi contract GUC kurulumundan sonra `pg_catalog,pg_temp` path'ine daralır;
  her iki effective/declared sıra runner tarafından exact assert edilir.
- Blank DB bootstrap ve complete-014 legacy baseline desteklenir. Partial/ambiguous DB, unknown/newer
  version, checksum/fingerprint drift ve system DB target non-zero reddedilir; otomatik drop/recreate
  veya back-registration yapılmaz.
- Ana Compose'ta initdb mount kaldırıldı. Digest-pinned binary + migration manifest image'ı one-shot
  çalışır; API, ingestion, pgAdmin, test runner ve PostgreSQL exporter
  `service_completed_successfully` bekler. `pg_isready` yalnız bağlantı health'idir.
- Required CI iki UUID-izole gerçek TimescaleDB cluster üzerinde role-bootstrap → managed-login
  one-shot migrator → `--verify-only`, her hedefte
  `24 migration + 2 hypertable + 24 checksum + 24 terminal state + ready` SQL kapısı ve migrator
  TRX'inde en az 124 executed, sıfır skipped/failed/notExecuted şartını uygular.
- Eski `apply-migrations.sh`, çift migration owner oluşmaması için exit 64 dönen retired compatibility
  tombstone'a çevrildi; normal veya recovery deploy yolu değildir.

Kabul kanıtı:

- UUID-bazlı disposable Compose stack'te dört hedef için role-bootstrap, one-shot migration ve ikinci
  `--verify-only` sıfır exit; güncel required gate exact `24,2,24,24,ready` bekler.
- Güncel API integration required floor **57**, iki ayrı cluster kullanan migrator discovery/gate
  **124**'tür. Tüm TRX'lerde
  failed/skipped/notExecuted sıfır ve fail-closed parser kapıları geçti.
- Migrator suite blank, complete-014 baseline, partial/unknown/checksum/system-target rejection,
  concurrent runner tek-body, transaction/session-kill rollback, commit-ACK convergence, optional
  012b target isolation ve secret redaction senaryolarını kapsar.
- Complete-014→019 ve managed-through-018→019 gerçek-PG varyantlarında `public.!~(text,text)`
  volatile sentinel operatörü kuruldu; iki akış da catalog operatörünü kullandı, sentinel sayısı
  sıfır kaldı ve 019 terminal fingerprint'i geçti. Extension/kullanıcı nesnelerinde false-positive
  yaratacak blanket `public` operator/function yasağı yerine resolver izolasyonu kanıtlanır.
- Önceki root bağımsız kabul koşusu o aşamadaki 30/30 matrisi geçti; güncel iki-cluster kapısı
  **124/124** sonucudur. İlk image incelemesinde amd64 child digest
  kullanımı yakalanıp gerçek multi-arch SDK/runtime manifest digest'leriyle düzeltildi; ardından native
  `linux/arm64` build ve smoke'da runtime 10.0.11, UID 1001 doğrulandı; güncel control-plane image'ı
  migrator ile role-bootstrap executable'larını ve 24 migration raw byte'ını birlikte taşır.
- Doğrulama project adı 32-hex UUID guard'lıydı; container, network, dört named volume, test results
  dizini ve local Compose image'ı exact proje scope'unda temizlendi.

Residual rollout/governance:

- Branch protection'ta required job ve gerçek deployment ortamında migrator-first rollout ayrıca
  kanıtlanmalıdır. Red durumda consumer'ı zorla başlatmama ve clone audit/forward-fix runbook'u
  operasyon ekibince prova edilmelidir.
- `docs/deployment/oci-migration-plan.md` içindeki eski migration anlatımı RP-07/RP-08 deploy
  hardening kapsamındadır; bu C-02 fazında geniş rewrite yapılmadı ve kanonik işletim kaynağı değildir.
- M-07/DBM-004 repository kapsamında kapatıldı: detached P-256 imzalı impact manifesti exact
  target/predecessor/prefix ve SQL sınıflandırmasına bağlanır; disk/tablespace, lock/blocker, replica,
  WAL/slot ve süre bütçeleri ilk mutasyondan önce ölçülür. Transactional tail hard ceiling ile,
  online UUID-keyset planı ise CAS checkpoint/nonce ve uncertain-commit reconciliation ile yürür.
  PostgreSQL 16 / TimescaleDB 2.16.1 kabulü disk/wrong-target/lock/slot negatiflerini, kill/resume,
  checkpoint driftini ve compressed-chunk policy restore'u kapsar. Production veri hacmi, gerçek
  kapasite ve change-window provası repo dışı release onayı olarak kalır.

### RP-07 / SEC-001 — Database privilege separation ve migration 019

Değişiklik:

- Repository-owned `Saydin.DatabaseRoleBootstrap` yalnız bounded secret-file kullanan `ensure`,
  `verify` ve ayrı `rotate` komutlarını sunar. Admin otoritesi fail-closed biçimde cluster bootstrap
  superuser OID 10'dur; physical `system_identifier` + database advisory lock'u farklı
  deployment/prefix claim'lerini de serialize eder. Marker, role attrs, PG16 membership multiset'i
  (grantor/admin/inherit/set dahil), DB/schema/`pg_control_system` ACL'i ve extension version/owner
  kontratı exact doğrulanır; foreign/unmarked rol adopt/alter/drop edilmez.
- Stable graph; NOLOGIN owner, altı NOLOGIN capability, versioned v1/v2 login ve passwordless
  `timescale_scheduler` rolüdür. Login parolaları yalnız absolute owner-only 0400/0600 file'dan okunur;
  Linux reader `openat2`/`statx` no-follow, ancestor, inode/device/mount/ctime/mtime ve link-count
  kontrolleriyle race/reparse saldırılarını fail-closed reddeder. `ensure` mevcut parolayı değiştirmez;
  `rotate` yeni version yaratır ve eski login'i kendi kendine kapatmaz.
- Additive `019_privilege_separation.sql`, verified GUC role contract'ını DB-local
  `saydin_role_contract` singleton'ına pinler; 18 public relation, column/type/function/default ACL,
  trigger, RLS, owner ve Timescale source/compressed/future chunk fingerprint'ini exact uygular.
  Yalnız calendar seal/activate/release-assembly üçlüsü dar ACL'li SECURITY DEFINER'dır. API,
  ingestion, calendar importer ve audit capability'leri exact allowlist alır; exporter public
  schema/object hakkı almaz.
- `activity_logs` ve compression job yalnız null-password, `CONNECTION LIMIT 0` dedicated scheduler
  sahibidir; diğer relationlar NOLOGIN owner'a aittir. Fresh 008/013 policy create'leri full-019
  manifestte strict olarak defer edilir; legacy policy 019 transaction'ında scheduler altında
  yeniden kurulur. One-shot private bridge yalnız geçiş transaction'ına gereken internal CREATE
  grantını taşır ve committe schema/function/grant bütünüyle yok olur. Aynı bridge
  `_timescaledb_internal` üzerindeki PUBLIC `USAGE` hakkını kaldırır; terminal ACL extension/bootstrap
  admin schema-owner'ında `CREATE+USAGE`, altı managed capability, NOLOGIN uygulama relation-owner'ı
  ve scheduler'da yalnız `USAGE` olacak biçimde exact kapanır. Loginlere doğrudan ve foreign rollere
  hiçbir internal-schema grant yoktur.
- Bu internal `USAGE` listesi bir erişim kolaylığı değil TimescaleDB 2.16.1 planner/chunk-owner
  invariant'ıdır: capability `USAGE` olmadan sıradan relation planı, NOLOGIN hypertable-owner
  `USAGE` olmadan ilk future-chunk write `42501` olur. Timescale root grant'lerini physical source ve
  compressed chunk ACL'lerine exact taşır; bu nedenle managed direct chunk SELECT, aynı kök SELECT
  yetkisine eşdeğerdir. Direct chunk INSERT/UPDATE ise propagated ingestion fence ve canlı
  window/lease GUC sözleşmesiyle korunur; foreign role schema/table seviyesinde reddedilir.
- Normal migrator password/connection URL env kabul etmez; nonsecret host/port/database/login ile
  absolute secret-file kullanır, exact session/marker/membership'i doğrular ve owner'a `SET ROLE`
  eder. Complete-014 veya managed-through-018 legacy cutover yalnız explicit admin connection file
  ile çalışır. 019 raw SHA-256:
  `213fd3dbe4d8de5f0ad6e88bddc3d059bc73917bf15f511f17713f81c920f31d`.

Kabul kanıtı:

- Bu 019-fazı kabul snapshot'ında RoleBootstrap pinli SDK suite'i **64/64 unit + 7/7 real-PG**;
  migrator iki-cluster suite'i **76/76** idi; failed/skipped/notExecuted sıfırdı. Fresh,
  legacy014/018, v2 rotation-stable singleton,
  wrong target/system/prefix/marker/grantor/ACL, concurrent claim, scheduler normal-login rejection,
  scheduled BGW compression success ve exact cleanup kapsandı.
- Internal-schema/chunk regresyon kapısı managed root write ile future chunk üretimini, exact
  source/compressed ACL multiset'ini (owner/grantor/grantable/RLS/policy dahil), direct chunk
  SELECT eşdeğerliğini, window'suz direct INSERT/UPDATE `42501`, foreign-role deny, BGW compression
  ve verify öncesi job'ın production `12 hours` schedule'ına geri alınmasını doğrular. Post-019
  RoleBootstrap iki ardışık `ensure` çağrısında idempotenttir; ek foreign ACL drift'i repair etmeden
  fail-closed reddeder.
- Gerçek uncertain-commit testi minimal PostgreSQL wire proxy ile frontend `COMMIT`i backend'e
  iletti, backend `CommandComplete(COMMIT)` cevabını client'a vermeden bağlantıyı kesti; ayrı process
  aynı legacy CLI argümanlarıyla terminal 019 fingerprint'ini reconcile etti. Bu kanıt yalnız
  post-ACK disconnect değildir.
- Production control-plane image build'i 0 warning/0 error geçti; runtime UID 1001 ve
  `/run/saydin-secrets` 0700'dür. Required CI role-bootstrap ve managed migrator servisleri raw login
  secret'ını argv/environment'a koymaz, read-only secret mount kullanır. Güncel required TRX
  ratchet'leri RoleBootstrap **76 unit + 7 real-PG** ve Migrator **124** olarak aşağıdaki doğrulama
  tablosunda sabitlenmiştir.

Runtime credential rollout (2026-08-19):

- Ortak `Saydin.DatabaseSecurity` runtime boundary explicit nonsecret PG topology, exact
  deployment/system/prefix/login sözleşmesi ve purpose-specific absolute owner-only password file
  kullanır. Raw DB URL/password/passfile/options env yüzeyi fail-closed reddedilir. Npgsql datasource
  secret logging/error detail kapalı ve bounded pool/timeout/search-path ile kurulur.
- Startup probe DB'ye iş yükünden önce exact `session_user=current_user`, login/capability marker ve
  attributes, OID-10 tarafından verilmiş direct membership multiset'i, outgoing capability membership
  bütçesi ve tüm mevcut v1/v2 managed rollere karşı USAGE/SET negative matrix'ini doğrular.
- API, ingestion, calendar importer ve DQA aynı boundary'yi tüketir. API health aynı datasource'u
  kullanır; ingestion worker başlatılmadan; calendar lock/import/activate yapılmadan; DQA relation
  taraması başlamadan önce kimlik doğrulanır. Fixture setup/cleanup admin datasource'ları SUT managed
  datasource'larından ayrılmıştır.
- Kök Compose root-only one-shot source generator/materializer ile API, ingestion, calendar, audit,
  exporter ve migrator için ayrı named volume üretir. Runtime container yalnız kendi read-only
  volume'unu görür; Postgres `POSTGRES_PASSWORD_FILE`, exporter `DATA_SOURCE_URI` +
  `DATA_SOURCE_USER` + `DATA_SOURCE_PASS_FILE` kullanır. Bootstrap script metadata yokken yalnız
  başlanmayan servisler için syntactically valid nonsecret placeholder sağlar, DB identity çıktısını
  secret-shape filtresinden geçirip gitignored 0600 metadata dosyasına atomik yazar. Plain Compose
  config metadata yokken non-zero'dur. Ingestion istemsiz dış çağrı yapmamak için explicit profile'dır.
- Required CI DB parolasını `GITHUB_ENV`/argv'ye yazmaz; bootstrap bundle'ları normal runtime'dan
  ayrıdır. Migrator ve calendar one-shot'ları yalnız kendi tek-file dizinlerini mount eder; API,
  ingestion ve audit test fixture'ları yalnız exact managed purpose secret'ı ile açıkça ayrı setup
  admin file'ını görür. Workflow exact filename/UID/mode/link-count ve Docker mount-source census'i
  uygular.

Bu lane için çalıştırılan kabul kanıtı:

- Release solution build: 0 warning/0 error. DatabaseSecurity/RoleBootstrap unit **64/64** ve
  real-PG **7/7**, calendar **62/62**, API **397/397**, ingestion **140/140**; failed/skipped sıfır.
- Temiz, project-scoped gerçek TimescaleDB'de metadata dosyası yokken dev bootstrap exit 0;
  role-bootstrap + fresh migrator güncel 24-migration terminal setiyle exit 0 tamamlandı. Managed API health healthy,
  exporter `/metrics` altında `pg_up 1`; API/exporter secret dosyaları sırasıyla UID 1001/65534,
  0400, link-count 1 ve mount read-only doğrulandı.
- Managed ingestion login gerçek SCRAM ile startup probe'u geçti. Ingestion capability'sine geçici
  owner outgoing membership drift'i eklendiğinde process yalnız stable
  `runtime_database_identity_rejected` ile exit 78 verdi; drift exact revoke edildi. Tüm-worker-off
  explicit ingestion profile koşusu ise DB probe'undan sonra beklenen worker fail-fast kapısında
  non-zero oldu.
- Root plain `docker compose config` runtime metadata yokken non-zero; generated metadata ile config
  ve bootstrap→migrator başarılı. Required CI Compose render, tüm shell script syntax ve YAML parse
  kapıları geçti. Disposable container/network/volume'lar exact project label ile kaldırıldı.

Residual rollout/operasyon:

- Production secret backend materialization, v1→v2 rotation + eski session drain ve gerçek deployment
  ortamındaki backend census repo dışı release kanıtı olarak açıktır; raw-secret fallback yoktur.
- Branch protection'ta required status check ve gerçek ortamda OID-10 bootstrap erişiminin yalnız
  control-plane job'a verilmesi repo dışı governance kanıtı olarak açıktır.

## Paket 4 — Durable ingestion ledger ve DB yazma çiti

### ING-001 / C-01 — Window completeness, crash convergence ve legacy fence

Değişiklik:

- Additive `015_ingestion_windows.sql`, nullable job korelasyonu ve EF modeli eklendi. Logical
  `(source, nullable asset, job type, range, contract version)` anahtarı `NULLS NOT DISTINCT` ile
  tekilleşir; interior/başlangıç gap'leri `MAX(date)` kullanılmadan planlanır.
- Provider sözleşmeleri `Data`, reason'lı `ExpectedNoData`, `RetryableFailure`,
  `PermanentFailure`, `PartialRejected`, cancellation/abandoned typed outcome'larına ayrıldı.
  Auth/5xx/parse/mapping/partial/boş sonuç artık başarılı `0` kaydı değildir.
- En eski çözülmemiş window DB saatine göre claim edilir. Owner, UUID fencing token, attempt ve lease
  expiry iki replica/stale-owner yarışını engeller; uzun fetch sırasında lease renewal vardır.
- Adapter sonucu; requested calendar, expected observation, raw item, accepted distinct, rejected ve
  expected-no-data sayaçlarıyla exact doğrulanır. Price asset/source/date seti ve EVDS `tuik` source/ay
  seti transaction içinde yeniden okunur.
- Data UPSERT, authoritative key-set kontrolü, window terminal state ve `ingestion_jobs` terminal
  state tek DbContext/transaction'dadır. Commit-ACK kaybı terminal state üzerinden reconcile edilir.
  Actual affected-row sayısı expected accepted count ile birebir değilse transaction rollback olur.
- `016_ingestion_write_fence.sql`, transaction-local window/token capability'si olmadan price ve
  inflation INSERT/UPDATE'ini DB sınırında reddeder. Böylece eski binary/repository ledger'ı sessizce
  bypass edemez. Güvenli rollout sırası old replica stop/drain → 016 migrate/verify → new binary'dir.
- OXR auth/plan/rate-limit ayrımı ve secret redaction, tamamlanmış gün hedefi; EVDS month-first anchor;
  TCMB bilinmeyen iş günü 404 fail-closed davranışı exact testlerle sabitlendi.

Kabul kanıtı:

- Docker Release build: PriceIngestion, ingestion integration ve API integration projelerinin üçünde
  de 0 warning/0 error.
- PriceIngestion unit **104/104**; gerçek TimescaleDB ingestion suite **28/28**; required PostgreSQL +
  Redis API integration discovery/gate **31**. Tümünde failed/skipped sıfır.
- Gerçek DB matrisi: two-replica earliest claim/reclaim, DB-clock skew, lease expiry/stale token,
  forged claim, transaction rollback, before/after-commit fault, interior gap, permanent blocker +
  operator requeue, weekday holiday positive/negative, inflation wrong-source ve NULL-safe uniqueness.
- 016 matrisi tokenless legacy price/inflation write, forged/wrong scope/date/source/job, expired/reclaimed
  token ve silent suppressing trigger'ı reddetti; data/window/job rollback birlikte doğrulandı.
- `001`–`014` immutable dosyalarında diff yok; 015 sonraki fence diliminde değiştirilmedi.

Residual rollout/operasyon:

- TimescaleDB hypertable `ENABLE ALWAYS TRIGGER` desteklemez. `price_points` trigger modu normal `O`,
  `inflation_rates` modu `A` olarak gerçek DB'de doğrulandı. Runtime rolü table/function owner,
  migrator/superuser veya `session_replication_role` değiştirme yetkisi taşımamalıdır.
- Contract-v2 TCMB/Twelve window'ları artık 017'nin sealed authoritative release/day authority'sine
  bağlanır. `market_holidays` yalnız contract-v1 backward-compatible projection'dır. Coverage eksikliği
  provider çağrısından önce retryable `CalendarNotReady` üretir; production update acquisition/schedule
  ve alert artifact'ları aşağıdaki CAL-001 residualı olarak açıktır.
- Repository/kod crash invariants kapandı; production-benzeri restore üzerinde read-only gap,
  invalid/provenance/stale-job audit ve hedefli repair manifesti ayrı Critical follow-up'tır.
- Enabled worker fatal olduğunda host/process non-zero ve bounded restart davranışı SUP-001 ile
  kapatıldı.

### CAL-001 — Authoritative TCMB/BIST calendar release lifecycle

Değişiklik:

- Phase A, content-addressed resmî TCMB aylık arşivleri ve Borsa İstanbul Pay Piyasası PDF'lerini
  deterministic/offline replay eder. TCMB 2006-01-01–2026-08-17 için 7.534; BIST
  2024-01-01–2026-12-31 için 1.096 tam günlük satır üretir. Raw snapshot byte'ları DB'ye girmez;
  offline verifier raw SHA/parser trust boundary'sidir.
- Additive `017_authoritative_market_calendars.sql`, immutable release/source/day modeli, tek mutable
  active pointer, deterministic asset binding ve window'a immutable release binding'i ekler. DB seal
  kapısı normalize gün byte'larını ve tüm persisted source-manifest provenance metadata aggregate'ini
  PostgreSQL 16 core `sha256(bytea)` ile yeniden hesaplar; `pgcrypto` yoktur.
- Sealed release payload/membership, asset binding ve bound window logical tuple'ı triggerlarla
  immutable'dır. Child DML parent row lock'u ile seal/payload TOCTOU serialize edilir; seal ve activate
  yalnız dar DB fonksiyonlarından geçer. Critical trigger/function event, enable-state, body hash,
  search-path/security/ACL sözleşmesi migrator fingerprint'ine dahildir.
- TCMB ve Twelve worker contract v2'dir. Coverage/readiness plan/claim/provider çağrısından önce
  fail-closed doğrulanır; unknown/missing coverage retryable `CalendarNotReady` ve metric üretir.
  Twelve yalnız resmî open/partial BIST session'ını 18:10 Europe/Istanbul kapanışı + bounded provider
  delay sonrasında hedefler; open-day 404/boş sonuç başarı değildir.
- Image CLI default olarak offline `verify`; explicit `import` aynı immutable in-memory verified bundle'ı
  staging → DB verify → seal → CAS activate transaction'ında kullanır. `activate` kontrollü rollback'tir;
  bounded connection/lock/statement timeout ve stdout/stderr secret redaction vardır.

Kabul kanıtı:

- CalendarData replay/unit TRX **80/80**, skip 0; exact normalized/source-metadata aggregate hash'leri
  TCMB `de8f0ff7654ae4972d081f1d2a225de6997986cd8297736715b3e71bfda1b1da` /
  `e95b9889c8857ca4e5ae5804704795265655deb30cd15f3224882a9f419feec9`, BIST
  `82c463fec5abf9663b689d863da9e7efcd93b976747e869c5aaeccfe7a4feed0` /
  `a93a6905c5213cdd30ad5a4ab4b9bcdb0698cf943c890aa44f462a1f3323c9b3` olarak yeniden üretildi.
- Gerçek TimescaleDB ingestion TRX **39/39**, skip 0: iki source için manuel bind olmadan asset
  auto-provision, stale/pointer lifecycle, manifest mutation-before-DB, same-release provenance
  conflict, wrong CAS full rollback ve açıkça test-only sentetik TCMB coverage+1 provider 0→1 dahil.
- İki gerçek TimescaleDB cluster kullanan güncel migrator discovery/gate **124**, skip 0: fresh 24 migration/2
  hypertable, exact bootstrap hashes, seal/payload iki-connection commit-order serialization,
  temp-shadow bypass negatives ve disabled-trigger/replaced-function `--verify-only` exit 3 fingerprint
  rejection dahil. Build 0 warning/0 error.
- Current calendar image `--network none` offline verify etti; iki eşzamanlı aynı-release import
  imported/idempotent yakınsadı; rollback/reactivate ve secret sentinel geçti. Current migrator image
  fresh UUID DB'ye 24 migration uyguladı; operational extra sealed release sonrası ve pointer rollback
  sonrası `--verify-only` geçti.

Residual rollout/operasyon:

- Bounded acquisition executable'ı, idempotent plan materializer, günlük/yıllık systemd schedule,
  stale/expiring-horizon alert'i ve imzalı review/promotion kapısı artık repoda ve pinli Docker
  davranış testleriyle doğrulanmıştır. Kalan koşul kod artefaktı değil, gerçek reviewer anahtarıyla
  yeni resmî bundle acquisition→review→promotion receipt'inin staging ortamında üretilmesidir.
- Calendar importer capability/ACL ve owner/superuser ayrımı migration 019 ile DB sınırında
  kurulmuştur. Production credential mount'u ve gerçek resmî-source promotion receipt'i hâlâ
  release-blocking dış ortam kabulüdür; runtime artefaktları artık eksik değildir.

### SUP-001 — Fatal worker görünürlüğü ve process recovery

İlk fatal/permanent/erken dönen enabled worker sibling worker'ları linked token ile iptal eder,
1–30.000 ms aralığında yapılandırılabilir bounded drain (default 5 s) uygular, worker kimliğini
Critical loglar, original exception'ı host'a taşır ve injectable sink üzerinden process exit code'u
1 yapar. Normal host cancellation sessiz/0'dır. .NET 10'un `ExecuteAsync` background-start semantiği
nedeniyle zero-enabled preflight `StartAsync` içinde yapılır; host kısa süreli started görünmez.
Unit **109/109** ve targeted supervision **5/5** geçti; gerçek zero-enabled container smoke exit 1
döndü. Compose `restart: unless-stopped` host dışı bounded recovery sahibidir.

## Paket 5 — API reel getiri ve hassas telemetri

### API-04 — Cash-flow CPI terminal ROI

Her katkı fiili alım ayının exact CPI endeksinden fiili terminal fiyat ayının satın alma gücüne taşınır.
Raw terminal portföy değeri kullanılır; yuvarlama yalnız response sınırındadır. Additive nullable
`InflationAdjustedInvestedTry`, `RealProfitLossTry`, `RealReturnMethod` ve
`InflationTerminalMonth` alanları eklendi; cache namespace'i `dca:v2` oldu. Eksik/geçersiz exact CPI
ayı reel alanları null bırakır ve incomplete sonuç cache'lenmez. Üç cash-flow exact fixture, tek katkı
Fisher parity, missing CPI ve rounding testleri API unit suite'inde geçmektedir. Bulk exact-CPI EF
sorgusu gerçek PostgreSQL üzerinde tek-sorgu projection ve terminal ay kapsamıyla doğrulanmıştır;
bu başlıkta repo-içi test residualı kalmamıştır.

### API-06/API-07 — Finansal minimizasyon ve doğru drop telemetrisi

WhatIf/Reverse/DCA Information ve activity logları exact input/output tutar ve yüzdeleri taşımaz;
yalnız kaba amount bucket ve `profit/loss/flat/unavailable` outcome kullanır. Scenario serbest label'ı
yerine `hasLabel` tutulur. Bounded channel gerçek `DropWrite` callback'inden drop metriği üretir;
completed writer rejection ayrı metriktir, action tag allowlist'lidir ve warning'ler rate-limited'dır.
Sentinel logger/activity ve gerçek `MeterListener` testleri bu sözleşmeleri kilitler. Console/OTLP
sink-level sentinel, production retention/access owner'ı ve yeni metric dashboard/alarmı repo dışı
governance residualıdır.

### API-03/API-05/API-11 — Bounded scenario contract ve immutable 018

Scenario save gövdesi binding öncesi bounded okunur; versioned/type-specific `extraData` allowlist,
JSON depth ve PostgreSQL canonical 8 KiB sınırı birlikte uygulanır. Additive cursor endpoint'i
`(created_at DESC, id DESC)` keyset pagination ve bounded page size kullanır; legacy liste yüzeyi de
hard-bounded kalır. Repository ile direct writer aynı per-user advisory transaction lock'ında serialize
olur ve sistem hard cap'i 100'dür. `018_scenario_integrity.sql` mevcut uyumsuz satırı silmeden/normalize
etmeden `23514` ile fail-closed durur; object/size/type-unit CHECK'leri, covering keyset index ve hard-cap
trigger'ı additive kurar. Immutable raw SHA-256
`8f6f76c12862c5f3696f9241c9e6566e75d048875552656b32b7eca84f65a056` hem migrator hem DQA trust
boundary'sinde pinlidir. Docker API unit **393/393**, skip 0 geçti; gerçek-DB required discovery/gate
**31**'dir.

### PRV-001 — Final observation authority ve API final-only görünürlük

Migration 020, normalize observation authority'sini fetch payload/attribution ledger'ından ayırır;
raw gövdeyi saklamadan bounded payload hash'i, stable observation kimliği, provider-specific kind,
`as_of_at`, contract ve database-canonical SHA-256 taşır. `price_points` ve `inflation_rates` için
all-null legacy satırlar korunur, partial tuple ve provisional/non-final yeni yazılar fail-closed
reddedilir. Immutable raw SHA-256
`8cb3f07bffef6013f42d196a20f0c08ed3e02547028d5694d6fba5f9749c52a8` migrator ve DQA canonical
trust-root'unda pinlidir; güncel fresh zincir 24 gövdedir.

API fiyat exact/nearest/latest/range/date-range ve CPI exact/last-known-value sorguları sıralama,
gruplama veya aggregate'den önce yalnız complete-final authority tuple'ını kabul eder. Fiyat sorgusu
32-byte observation hash'ini, `provider_source = asset.source` eşleşmesini ve desteklenen provider/kind
matrisini; CPI sorgusu exact `tuik/evds/cpi_index`, final, contract-positive ve 32-byte hash
sözleşmesini uygular. Böylece legacy all-null ve forged/eksik authority satırları hesaplamalara
giremez. Tüm data-bearing cache anahtarları tek `authority-final-v1` namespace'ine taşınmıştır;
asset catalog `assets:list`/signature anahtarları API-TRUST 021 kapsamı olduğu için değiştirilmemiştir.
Fiyat ve hesaplama response'ları mevcut alanları koruyarak nullable `basis`/`data` metadata'sı ekler;
raw evidence, observation hash/id veya provider payload'u wire yüzeyine çıkmaz.

Redis fiyat cache'i authority kaynağı sayılmaz: exact/nearest/latest/range ve asset-info hit'inden önce
aktif asset'in `id/symbol/source` kimliği doğrudan PostgreSQL'den okunur; envelope bu trusted kimliği,
istenen tarih/aralık ve nearest sınırını exact doğrular. Cache içindeki envelope ile point birlikte yanlış
provider/source beyan etse, yanlış exact tarih veya sınır dışı/mixed range taşısa hit reddedilip final-only
repository sorgusuna dönülür. WhatIf/DCA yalnız complete ve warning'siz sonucu cache'ler; beklenen optional
no-data/transient dependency sınıfı dışındaki cancellation, auth, EF ve programmer hataları propagate edilir.
WhatIf forward/reverse ve DCA cache envelope'ları normalized sembol, tüm tarih/amount/type/inflation ve DCA
period alanlarını response ile birlikte request'e exact bağlar; current namespace altındaki başka request'e ait
complete payload hit sayılmaz. Trusted asset identity sorgusu yalnız scoped servis ömründe, concurrency-safe
per-symbol memo ile coalesce edilir; 600 alımlı DCA aynı requestte sembol başına bir DB identity okuması yapar,
başarısız/cancelled loader cache'lenmez ve memo requestler arasında taşınmaz.
Response authority özeti contract version listesini büyütmez; observation count ile min/max version ve sabit
allowlist'ten provider/kind kümesi O(1) output bütçesinde tutulur.

Kabul kanıtı: pinned SDK 10.0.400 ile API unit **545/545**, skip 0; disposable PostgreSQL 16 / TimescaleDB
2.16.1 + Redis stack'ında role-bootstrap ve fresh 24 migration sonrası managed API login ile **57/57**
integration/HTTP/Redis testi, failed/skipped/notExecuted 0. Required fixture 020 checksum yanında named
constraint, kolon/default, PK-index, table/column ACL, canonical function body/security/ACL ve trigger tuple
fingerprint'ini exact doğrular; transaction içindeki function-body/constraint/default/index/ACL driftleri
readiness'i kapatıp rollback sonrası yeniden açmıştır. 020 raw SHA-256 değişmeden
`8cb3f07bffef6013f42d196a20f0c08ed3e02547028d5694d6fba5f9749c52a8` kalmıştır.

Production rollout sırası fail-closed'dur: authority-aware ingestion + 020 migrate/verify, historical
price ve CPI backfill/replay, DQA ile beklenen kapsamdaki legacy/partial/invalid authority sayısının
sıfır ve attribution/hash kapılarının clean kanıtlanması, ardından final-only API binary'si. Bu coverage
kanıtı tamamlanmadan API production switch'i yapılmaz. Geri alma yalnız API binary'si ve yeni cache
namespace'i için kontrollüdür; 020 schema/trigger sözleşmesi kaldırılmaz ve authority-unaware worker
yeniden başlatılmaz. Asset catalog revision API-TRUST 021'in ayrı release kapısıdır.

API-TRUST retention residualı migration 022 ile kapatıldı. Eski `ON DELETE SET NULL` RI action'ı,
Timescale scheduler-owned locked-search-path `SECURITY DEFINER` BEFORE DELETE redaction ve fail-closed
`NO ACTION` FK ile değiştirildi. Redaction `user_id=NULL` ve sabit `server-redacted` device bağı
uygular; activity olayı korunur. Owner/API/audit/PUBLIC için geniş `UPDATE` verilmedi. Scheduler'ın
hypertable permission path'i için gereken explicit `SELECT,UPDATE` self-grant'i current/future source
chunk, compression root ve physical compressed chunk üzerinde exact fingerprint edilir. Role bootstrap
tek-kullanımlık transition helper'ını 022 öncesi kurar; migration helper'ı ve geçici CREATE/REFERENCES
yetkilerini commit öncesi tüketir.

### API-TRUST-001 — Installation principal, dağıtık admission ve catalog revision

`X-Device-ID` auth/ownership yüzeyi kaldırıldı; server-issued 256-bit opaque installation
credential, hash-only database verifier, generic 401 ve iki fazlı rotation kullanılıyor. Existing
users `legacy_quarantined`; compiled auto-claim yolu yoktur. Migration 021 SHA-256
`1f44aa1413d611cb8b078541e0100985c33614274e2fd700a8f8b94303045c1e` ile migrator ve DQA
trust-root'unda pinlidir; 022 öncesi zincir 23 gövdedir. Asset catalog revision+SHA tüm data-bearing cache
envelope'larını invalid eder.

Process-local limiter kaldırıldı. Redis `TIME` tabanlı exact IP+ağ ve doğrulanmış principal
bucket'ları ile nonce-bağlı günlük quota lease kullanılır; finite Redis hataları fail-closed'dur.
Migration 022 SHA-256
`568017c27eb6038a06b48ee00f2f0820bba6cf7b577dd5f283291ac9995e8afd` ile migrator ve DQA
trust-root'unda pinlidir; fresh zincir artık 24 gövdedir. Pinned SDK 10.0.400 ve PostgreSQL 16 /
TimescaleDB 2.16.1 kabulünde migrator **124/124**, DQA unit **84/84**, DQA gerçek PostgreSQL
**72/72**, RoleBootstrap unit **76/76** ve phase-aware backup gerçek PostgreSQL **7/7**
geçti; fail/skip 0. Fresh actual RoleBootstrap → 24 migration, 001–021 upgrade → 022, current/future/
compressed redaction, iki commit sıralı concurrent insert/delete, transaction rollback+rereun,
unknown-tail pre-DDL ve function/trigger/FK/root+chunk ACL/policy/helper tamper matrisleri temizdir.
API unit **545/545** ve gerçek PostgreSQL+Redis **57/57** Phase A/B kanıtı korunur.

## Paket 6 — Salt-okunur, imzalı data-quality audit çekirdeği

### DQ-001..008 — Fail-closed audit ve kanıt paketi

Değişiklik:

- Yeni `Saydin.DataQualityAudit` executable'ı yalnız `scan` ve `verify-evidence` komutlarını sunar;
  DML, repair veya apply yolu içermez. Signed input exact veritabanı adı, `pg_control_system()` system
  identifier hash'i, lane/scope ve bütçeleri bağlar; duplicate JSON property/lane ve scope dışı hedef
  exit 64/3 ile fail-closed reddedilir.
- Scan, dedicated gerçek read-only role ile `REPEATABLE READ` + `READ ONLY` transaction'da ve bounded
  connection/lock/statement/idle/total timeout altında çalışır. Super/owner/elevated rol, DB TEMP,
  schema CREATE, audited tablolarda INSERT/UPDATE/DELETE/TRUNCATE ve calendar seal/activate EXECUTE
  yetkileri preflight'ta reddedilir.
- Preflight embedded raw-byte checksum/readiness setini migration control plane ile exact karşılaştırır.
  DQA ile migrator aynı derlenmiş canonical trust-root kaynağını tüketir; immutable `001`–`022` setindeki
  24 gövdenin her biri raw SHA-256 ile fail-closed pinlidir. 018 hash'i
  `8f6f76c12862c5f3696f9241c9e6566e75d048875552656b32b7eca84f65a056`, 020 hash'i
  `8cb3f07bffef6013f42d196a20f0c08ed3e02547028d5694d6fba5f9749c52a8` değeridir.
- DQ-001..008; nonterminal window'ların Critical görünürlüğü, exact window-data/calendar seti,
  scope'a kırpılmış price/inflation interior-trailing ledger gap'leri ve overlap'leri,
  constraint/fence drift ve gerektiğinde duplicate taraması, OHLC/pozitiflik/CPI, provenance/secret
  varlık sayıları, calendar payload/coverage verification, stale/job-window mismatch ve post-fence
  unattested/legacy-null-window verisini kapsar. Secret/raw değerler evidence'a yazılmaz; örnek business
  key'leri ayrı secret-file HMAC anahtarıyla pseudonymize edilir ve bounded/truncated tutulur.
- Evidence process-owned `0700` random sibling staging dizininden atomik directory publish ile yazılır;
  prospective toplam fiziksel byte bütçesi aşılırsa hiçbir final bundle yaratılmaz. Canonical content ve
  per-file SHA-256 listesi detached ECDSA NIST P-256 signature ile bağlanır. Verify; signature/hash,
  unsigned extra file, symlink,
  traversal, incomplete ve size ihlalini reddeder. Recommendation manifest yalnız allowlisted
  `requeue`/`refetch`/`manual_review` metadata'sıdır; gerçek preimage yokken hash uydurmaz ve repair
  çalıştırmaz.
- Signed input `keyId` ile input yetkilendirme signer'ını, ayrı bounded `evidenceKeyId` ile izin verilen
  evidence signer'ını bağlar; iki kimlik de exact NIST P-256 public SPKI SHA-256 fingerprint'idir ve
  output yalnız ikincisini taşır. Unknown/duplicate/null JSON shape'leri,
  oversized pre-budget manifest/signature/PEM/HMAC ve declared evidence dosyaları reddedilir. Writer
  output root/ancestor symlink-reparse traversalını dosya oluşturmadan önce fail-closed durdurur.
  Hard cap'ler input/evidence manifest 1 MiB, detached signature 4 KiB, PEM 64 KiB, HMAC 4 KiB,
  connection file 64 KiB, evidence bundle 256 MiB ve 4.096 evidence dosyasıdır; payload hash'leri
  declared/actual boyut eşleşmesinden sonra streaming hesaplanır.
- Exit sözleşmesi: clean `0`, data violation `2`, preflight/target/privilege `3`, budget `4`, runtime/
  timeout `5`, evidence/signature `6`, invalid argument/input `64`.

Kabul kanıtı:

- Docker Release unit **84/84**, gerçek TimescaleDB audit acceptance **72/72**; failed/skipped sıfır.
  Solution-wide Docker Release build 0 warning/0 error'dır.
- Gerçek DB matrisi clean ve deterministic hash rerun, DQ-001 nonterminal/window-data, DQ-002 clipped
  containing-window/overlap/trailing gap, exact DQ-003 constraint ve fence tgtype/function-body drift,
  OHLC/CPI, secret sentinel redaction, wrong target/privilege, relation/evidence bütçesi, cancellation,
  PostgreSQL lock timeout, evidence tamper ve kalan DQ-004..008 anomalilerini exact check/code ile
  tetikledi; targeted cleanup sonrası aynı DB yeniden clean `0` verdi.
- Dedicated audit role `pg_monitor` üyesi olmadan yalnız gerekli `pg_control_system()`/calendar verify
  EXECUTE ve SELECT grant'larıyla clean scan çalıştırdı. INSERT/TRUNCATE/trigger disable/elevation
  denemeleri gerçek PostgreSQL'de `42501` verdi. Fixture, UUID-türetilmiş exact disposable DB/role
  guard'ı ve `DROP OWNED` cleanup'ı kullanır.
- CI ile aynı read-only-source runner, boş project NuGet volume'ünde iki ayrı normal TRX üretti;
  güncel required ratchet DQA unit `84/84` ve gerçek PostgreSQL `72/72`, sıfır
  failed/skipped/notExecuted doğrular. Fresh audit DB schema kapısı `24,2,24,24,ready` olur.

Residual rollout/operasyon:

- Production-benzeri veya production DB'ye erişim yapılmadı; bu kanıt executable ve disposable fixture
  davranışına aittir. Dedicated runtime audit rolü/secret-file wiring'i ve scheduled deployment ayrı
  operasyon kapısıdır.
- PEM private key yalnız test/dev signer sözleşmesidir. Production Compose ve restore drill yalnız OCI
  instance-principal KMS signer, exact public SPKI ve bounded key-id allowlist kabul eder; raw private key
  image, argv, environment veya evidence dizininde reddedilir.
- OCI IAM policy, gerçek key/version rotasyonu ve operator allowlist promotion onayı repo dışı operasyon
  sorumluluğudur; tracked deployment bu girdiler eksikken fail-closed kalır.
- Audit repair yapmaz. İmzalı recommendation/preimage/postimage metadata'sını tüketen ayrı DataRepair
  executable'ı aşağıdaki exact dry-run/apply/rollback ve managed-role sözleşmesiyle eklenmiştir;
  production plan/key/operator onayı yine audit sürecinden bağımsız kalır.

## Paket 7 — İmzalı, CAS-korumalı DataRepair yürütücüsü ve CI kabulü

Değişiklik ve kabul kanıtı:

- `Saydin.DataRepair`, signed plan/target, DQA evidence, exact migration trust-root ve bounded trust-lease
  doğrulamasından sonra dry-run/apply/rollback çalıştırır. Runtime veritabanı oturumu yalnız exact managed
  ingestion login'i; ayrı evidence doğrulaması exact audit login/password-file ile açılır. Admin secret
  yalnız UUID-bound disposable fixture setup/cleanup kapsamındadır.
- Apply/rollback exact preimage/postimage CAS ve durable receipt ile bağlıdır; concurrent state change,
  target/system/deployment/role mismatch, yanlış audit kimliği, migrator lock, production-target local
  evidence ve commit-ACK kaybı deterministik fail/reconciliation yollarıyla kapsanır.
- Pinned SDK 10.0.400 unit **15/15**; pinned PostgreSQL 16 / TimescaleDB 2.16.1 gerçek managed-login
  suite **7/7**, skip/fail 0 geçti. Aynı akış pre-bootstrap `required=true` → migration `24` → exact HBA →
  post-bootstrap `required=false` → verify-only `24` sırasını uygular ve UUID-bound container/network/
  volume/image setini exit 0 sonrası exact temizler.
- DataRepair unit+gerçek-PG birleşik kapsamı executable namespace için `%83,63` line / `%56,72` branch'tir;
  yeni executable satırları changed-line `%80` admission floor'unun üstündedir.
- LOW inventory-cap kapanışında değişen üç bounded inventory source dosyasının konservatif tüm-dosya
  instrumented line kapsamı `%80,07`'dir; DQA unit `84`, DataRepair unit `15` ratchet'leri bu negatif
  cap+1/cancellation matrisini required CI'da korur.
- Required CI, API/ingestion/DQA'dan bağımsız `saydin_data_repair_test_<32hex>` veritabanı ve role prefix'i
  oluşturur. DataRepair test container'ı yalnız `admin,audit-v1,ingestion-v1`; normal repair migratorı yalnız
  `migrator-v1` private bundle'ını görür. Read-only source runner exact `7` TRX ve ayrı Cobertura üretir;
  unit/integration coverage cardinality kapıları sırasıyla `7` ve `5`'tir.

## Güncel doğrulama tabanı

Bu tarihsel kaydın önceki bölümlerindeki sayaçlar, ilgili dalganın çalıştırıldığı andaki kanıtlardır;
güncel ratchet olarak yorumlanmamalıdır. 2026-08-27 final ağacının otoritatif tabanı şöyledir:

| Kanıt | Güncel sonuç |
|---|---|
| Root unit matrisi | 1.236/1.236: API 658, ingestion 182, DQA 97, migrator-unit 78, RoleBootstrap 98, DataRepair 29, CalendarData 94; fail/skip 0 |
| Gerçek infrastructure | API 66, ingestion 44, DQA 106, DataRepair 32, RoleBootstrap 13, migrator iki-cluster 185; fail/skip/notExecuted 0 |
| Fresh migration | Development smoke'ta exact `27` migration ve `ready`; 001–022 byte-identical; 023/024/025 trust-root ve function-body pin paritesi |
| Solution Docker Release build | 0 warning, 0 error |
| Unit coverage | Weighted line `%78,57`, branch `%66,19`; changed executable lines `%84,03` |
| Production/development assurance | 68 production, 21 development Compose, 18 observability, 11 private-material, 12 runtime ve 2 volume mutation; backup static 64, HBA 8 |

Tam komut/koşu kapsamı, migration SHA-256 değerleri, açık riskler ve dış release koşulları için
[`pr-review2/08-remediation-execution.md`](pr-review2/08-remediation-execution.md) esas alınır.
