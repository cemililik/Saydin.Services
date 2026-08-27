# Doğrulanmış Critical ve High Bulgular

> Review hedefi: `development` @ `f9f608d` (taban `a274c62`)
> Her bulgu, üreten agent'tan **bağımsız** ikinci bir agent tarafından kodun kendisi okunarak
> doğrulandı; `Critical` işaretli iki bulgu ayrıca **ana agent tarafından birebir yeniden üretildi**.

## Özet

| # | Önem | Hat | Bulgu |
|---:|---|---|---|
| 1 | **Critical** | L08 | Kök docker-compose.yml role-bootstrap servisi zorunlu backup argümanlarını geçmiyor → dev stack ve d |
| 2 | **Critical** | L16 | `deploy-release.sh` manifest bağlama adımı her zaman KeyError ile ölüyor — staging ve production dep |
| 3 | **High** | L01 | Management port sınırı trailing-slash ile atlatılabiliyor: /metrics/ ve /health/ready/ public port't |
| 4 | **High** | L01 | Kimlik doğrulamasız principal üretimi günlük kotayı sıfırlıyor — REM-API-02 kabul kapısı kapanmamış |
| 5 | **High** | L03 | DCA reel getirisi üretim varsayılan yolunda kalıcı olarak null döner: terminal ay CPI'ı hiçbir zaman |
| 6 | **High** | L04 | Activity-log yazıcısı geçici PostgreSQL hatalarını "FatalHost" sayıp tüm API process'ini düşürüyor |
| 7 | **High** | L08 | Backup login'in VALID UNTIL değeri marker'a pinlenmiş: süre dolduğunda her production `ensure` exit  |
| 8 | **High** | L09 | Tek bir asset'in permanent window'u tüm ingestion sürecini süresiz durduruyor ve sınırsız crash-loop |
| 9 | **High** | L09 | Retryable window'ların yeniden denenmesi ledger'ın next_attempt_at sözleşmesini değil worker zamanla |
| 10 | **High** | L10 | Provider yanıt gövdesi hiçbir wall-clock timeout ile sınırlı değil; askıda kalan bir provider worker |
| 11 | **High** | L15 | SaydinActivityLogLoss alert'i yapısal olarak hiç tetiklenemez (vector aritmetiğinde label uyuşmazlığ |
| 12 | **High** | L15 | Otomatik deploy monitoring düzlemini (Prometheus/Alertmanager/exporter'lar) hiç başlatmıyor |
| 13 | **High** | L16 | Restore drill ilk Docker adımında her zaman başarısız olur: `--cap-drop ALL` altında `chown` EPERM v |
| 14 | **High** | L16 | Base backup tüm PGDATA'yı 2 GiB tmpfs'e (RAM) yazıyor; 1 GiB mem_limit ile veri büyüdükçe base yedek |
| 15 | **High** | L18b | Required ingestion-ledger suite'i hard-coded `schema_migrations count = 23` nedeniyle kırmızı; iddia |
| 16 | **High** | L18e | DataRepair'in yıkıcı-senaryo guard'larının büyük bölümü hiçbir testte doğrulanmıyor |

---

### 1. Kök docker-compose.yml role-bootstrap servisi zorunlu backup argümanlarını geçmiyor → dev stack ve dokümante edilmiş test akışı exit 64 ile ölü

| | |
|---|---|
| **Önem** | Critical |
| **Hat** | L08 — RoleBootstrap + DatabaseSecurity |
| **Kategori** | ci-cd |
| **Doğrulama** | CONFIRMED |
| **Konum** | `docker-compose.yml:174-205 (database-role-bootstrap), docker-compose.yml:27 ve 52-55 (secret-source-generator/materializer), src/Saydin.DatabaseRoleBootstrap/BootstrapOptions.cs:60-62,88,168-169` |

**Bulgu.** Kök docker-compose.yml'deki `database-role-bootstrap` servisi `ensure` için zorunlu olan `--backup-v1-valid-until` ve `--backup-password-file` argümanlarını geçmiyor ve dev secret üretim zinciri `backup-v1` dosyasını hiç oluşturmuyor; servis `code=argument_required` ile exit 64 verir, `service_completed_successfully` bağımlılığı nedeniyle database-migrator ve arkasındaki tüm dev/test zinciri başlamaz.

**Etki.** Sıfırdan bir checkout'ta CLAUDE.md'nin zorunlu kıldığı `docker compose build && docker compose up -d` ve `docker compose run --rm tests` yolları çalışmıyor (tests servisi database-migrator'a, o da role-bootstrap'e service_completed_successfully ile bağlı). Etki dev/CI-dışı yerel akışla sınırlı — production ve CI compose dosyaları doğru argümanları geçiyor. Ayrıca docs/analysis/06-remediation-progress.md:284-286'daki "temiz project-scoped gerçek TimescaleDB'de ... role-bootstrap + fresh migrator ... exit 0 tamamlandı" kabul kanıtı mevcut kök compose ile tekrar üretilemez.

**Öneri.** secret-source-generator'ın `make_secret` döngüsüne `backup-v1` ekle; secret-materializer'da `/out-bootstrap/private/backup-v1` olarak 1001:1001 0400 kur; `database-role-bootstrap` command'ine `--backup-v1-valid-until ${SAYDIN_BACKUP_V1_VALID_UNTIL:?...}` ve `--backup-password-file /run/saydin-secrets/private/backup-v1` ekle. Kalıcı çözüm: validate-development-compose.py'a role-bootstrap argüman sözleşmesi kontrolü ekleyip CI'da kapıya bağla (production/CI compose ile kök compose'un ayrışması aynı hatayı tekrar üretir).

---

### 2. `deploy-release.sh` manifest bağlama adımı her zaman KeyError ile ölüyor — staging ve production deploy'ları tamamen bloke

| | |
|---|---|
| **Önem** | Critical |
| **Hat** | L16 — Backup/restore ve supply chain |
| **Kategori** | correctness |
| **Doğrulama** | CONFIRMED (verifier) |
| **Konum** | `infrastructure/release/deploy-release.sh:38-44,50-52` |

**Bulgu.** Satır 38-41'deki `runtime` sözlüğü 9 anahtar içeriyor (`timescale, redis, postgresExporter, redisExporter, otel, prometheus, alertmanager, blackbox, nodeExporter`). Satır 43 ise `expected.update({runtime[name]:reference for name,reference in manifest["runtimeImages"].items()})` ile manifest'in TÜM runtimeImages anahtarları üzerinde dönüyor. `release_manifest.py:15` `EXPECTED_RUNTIME_IMAGES` 11 anahtar tanımlıyor (ek olarak `tempo` ve `loki`) ve satır 137 `exact_keys(root["runtimeImages"], set(EXPECTED_RUNTIME_IMAGES), "runtimeImages")` ile tam eşleşmeyi zorunlu kılıyor; üstelik satır 26'da `release_manifest.py verify` zaten başarıyla koşmuş olduğundan manifest'te bu iki anahtar kesinlikle vardır. Heredoc'ta try/except yok → KeyError → python non-zero → satır 50-52 `die "deployment_manifest_binding_failed" 78`. Sözlüğü ve gerçek anahtar setini birebir kopyalayıp çalıştırdım: `KEYERROR 'loki'`. Karşı taraf olarak rollback-release.sh:80-82'de aynı hata YOK (`if item["name"] in keys` filtresi var). Statik kapılar da yakalamıyor: validate-release.py:54-62 yalnız metin varlığı arıyor, release-images.yml:66-72'deki shellcheck python heredoc'unun içine bakmıyor, `sh -n` sözdizimi kontrolü yapıyor.

**Etki.** Ne staging deploy'u ne de üretim promotion'ı tamamlanabilir; script satır 44'ten öteye hiç geçemez, dolayısıyla ne compose doğrulaması, ne HBA kurulumu, ne bootstrap/migration kapıları, ne backup kapıları, ne de `receipt.json` üretimi çalışır. promote-production.yml ≤7 günlük imzalı staging receipt'i şart koştuğu için üretim akışı da bağımlı olarak ölü. Fail-closed olduğundan veri/güvenlik riski yok, ama README'deki 'imzalı manifest'e bağlı deploy' kapısının hiç çalıştırılmamış olduğunu gösteriyor.

**Öneri.** `deploy-release.sh` içindeki `runtime` sözlüğünü `render-deployment-env.py:20-27` `RUNTIME_IMAGE_KEYS` ile birebir eşitle (tempo, loki ekle). Daha kalıcı çözüm: iki eşlemeyi tek kaynaktan türet (ör. `release_manifest.py`'de export edilen bir sabit) ve `validate-release.py`'ye bu eşitliği doğrulayan statik bir kontrol ekle. Ayrıca `release-manifest-self-test.py`'ye örnek bir manifest+env ile deploy bağlama bloğunu gerçekten çalıştıran bir test ekle.

---

### 3. Management port sınırı trailing-slash ile atlatılabiliyor: /metrics/ ve /health/ready/ public port'tan erişilebilir

| | |
|---|---|
| **Önem** | High |
| **Hat** | L01 — API kimlik ve güvenlik yüzeyi |
| **Kategori** | security |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Middleware/ApiPortBoundaryMiddleware.cs:26-36; src/Saydin.Api/Program.cs:301, 341-350; infrastructure/deployment/Caddyfile:11-12` |

**Bulgu.** Port sınırı sınıflandırması normalize edilmemiş yol üzerinde tam eşitlikle çalıştığından, public port üzerinden `GET /metrics/` ve `GET /health/ready/` istekleri PublicProduct olarak sınıflandırılıp ilgili management endpoint'lerine ulaşır; Caddy'nin exact-path `@internal` kuralı da aynı trailing-slash ile atlatıldığı için savunma derinliği katmanı da devre dışı kalır.

**Etki.** Kimlik doğrulamasız internet trafiği Prometheus scrape çıktısının tamamını (endpoint bazlı istek/latency histogramları, quota ve activity-log drop metrikleri, .NET runtime/GC, DB/Redis havuz göstergeleri) ve readiness ayrıntısını okuyabilir. `/health/ready/` üstelik PostgreSQL ve Redis'e gerçek sağlık sorgusu tetiklediğinden IP başına 60 istek/dk bütçesiyle küçük bir amplifikasyon yüzeyi de açar. Commit'in getirdiği public/management izolasyonunun ve mimari dokümanın iddiası geçersizleşir.

**Öneri.** Classify içinde yolu normalize et (ör. `context.Request.Path.Value?.TrimEnd('/')` üzerinden OrdinalIgnoreCase karşılaştırma, kök '/' için özel durum) veya `PathString.StartsWithSegments` kullan. Daha sağlamı: management endpoint'lerini `app.MapWhen(ctx => ctx.Connection.LocalPort == runtime.ManagementPort, ...)` ile ayrı bir branch'e taşı, böylece sınıflandırma hatası endpoint eşleşmesini etkilemesin. Regresyon testine `/metrics/`, `/Metrics/`, `/health/ready/`, `/health/ready//` varyantlarını ekle; Caddyfile'daki `@internal` matcher'ını da `path /metrics /metrics/ ...` veya `path_regexp` ile sağlamlaştır.

---

### 4. Kimlik doğrulamasız principal üretimi günlük kotayı sıfırlıyor — REM-API-02 kabul kapısı kapanmamış

| | |
|---|---|
| **Önem** | High |
| **Hat** | L01 — API kimlik ve güvenlik yüzeyi |
| **Kategori** | security |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Endpoints/InstallationEndpoints.cs:16-20, 43-63; src/Saydin.Api/Services/DailyLimitGuard.cs:244-258; infrastructure/postgres/migrations/021_api_trust_expand.sql:185-225; src/Saydin.Api/appsettings.json (Plans.Free.DailyCalculationLimit=20, DistributedSecurityLimiter.ExactIpLimit=60)` |

**Bulgu.** `POST /v1/installations` kimlik doğrulamasızdır ve registration'a özgü hiçbir cap taşımaz; günlük kota subject'i principal id olduğundan tek ek istekle yeni principal mint edip 20 istekli free kotayı sıfırlamak mümkündür — üst sınır yalnız IP başına 60 istek/dk'dır ve her sıfırlama kalıcı users + installation_credentials satırı bırakır.

**Etki.** Free/premium ayrımını taşıyan günlük hesaplama kotası pratikte etkisiz hâle gelir (20/gün yerine IP başına ~86k istek/gün). Kimlik doğrulamasız istekler sınırsız kalıcı DB satırı yaratabildiğinden depolama/indeks büyümesi de bir kaynak tüketim vektörüdür. Remediation kaydının 'Doğrulandı' işareti bu kabul kapısı için gerçeği yansıtmıyor.

**Öneri.** Registration'a ayrı ve çok daha dar bir Redis bucket ekle (exact IP + /24 için saatlik/günlük kayıt cap'i; mevcut atomik Lua deseniyle) ve `MapPost("", RegisterAsync)` üzerine filtre olarak bağla. Günlük hesaplama kotasını principal'ın yanında ağ pseudonym'ine bağlı ikinci bir bucket ile de sınırla veya yeni principal'ların ilk gün kotasını düşür. Attestation (App Attest/Play Integrity) ürün kararını planla. 06-remediation-progress.md'deki API-TRUST-001 satırını bu kapının açık olduğunu belirtecek şekilde düzelt ve 'aynı IP'den binlerce registration → 429' regresyon testini ekle.

---

### 5. DCA reel getirisi üretim varsayılan yolunda kalıcı olarak null döner: terminal ay CPI'ı hiçbir zaman mevcut değil

| | |
|---|---|
| **Önem** | High |
| **Hat** | L03 — Finansal hesaplama, cache, kota |
| **Kategori** | correctness |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Services/DcaCalculator.cs:262-314,359; src/Saydin.PriceIngestion/Workers/EvdsInflationWorker.cs:53-56,86-90; src/Saydin.Api/Repositories/InflationRepository.cs:23-42` |

**Bulgu.** API-04 ile getirilen exact-CPI terminal sözleşmesi, terminal ayı fiyat verisinin son gününden türettiği için varsayılan (endDate boş) DCA isteklerinde her zaman içinde bulunulan ayı ister; bu ayın CPI'ı ne EVDS worker tarafından planlanır ne de TÜİK tarafından yayınlanmıştır → reel getiri alanları kalıcı olarak null döner ve sonuç hiç cache'lenmez.

**Etki.** DCA ekranının enflasyona göre düzeltilmiş getiri özelliği varsayılan kullanımda fiilen kapalı; kullanıcı yalnız nominal getiri ve `inflation_incomplete` uyarısı görür. Aynı üründe `/calculate` (LKV kullanıyor, WhatIfCalculator.cs:334-352) reel getiri döndürdüğü için iki ekran çelişir. Ek olarak `inflationCalculationComplete=false` iken response cache'lenmediğinden (DcaCalculator.cs:359) her istek 601 noktaya kadar bulk fiyat sorgusu + CPI sorgusunu yeniden çalıştırır.

**Öneri.** Terminal ay için kademeli sözleşme: exact CPI yoksa `period_date <= terminal` en son final gözlemi terminal deflatör olarak kullan, gerçekten kullanılan ayı `InflationTerminalMonth`/`InflationDataAsOf` ile bildir ve `RealReturnMethod`'u ayrı bir değere (`cashflow_cpi_lkv_terminal_v1`) çevir; yalnız gerçekten eksik ARA katkı aylarında null'a düş. `FakeTimeProvider` ile 'terminal ay = bugünün ayı, CPI yalnız önceki aya kadar var' senaryosunu kilitleyen bir unit test ekle.

---

### 6. Activity-log yazıcısı geçici PostgreSQL hatalarını "FatalHost" sayıp tüm API process'ini düşürüyor

| | |
|---|---|
| **Önem** | High |
| **Hat** | L04 — API runtime ve activity logging |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/BackgroundServices/ActivityLogBatchStore.cs:44-50, src/Saydin.Api/BackgroundServices/ActivityLogWriter.cs:29-53,135-138, src/Saydin.Api/Program.cs:288` |

**Bulgu.** ActivityLogWriteFailureClassifier'ın varsayılan dalı (ActivityLogBatchStore.cs:50) 23xx/40001/40P01/08xx dışındaki tüm PostgresException SQLSTATE'lerini FatalHost sayar; ActivityLogWriter bu durumda exception'ı ExecuteAsync dışına fırlatır ve Saydin.Api'de BackgroundServiceExceptionBehavior ayarlanmadığı için .NET varsayılanı (StopHost) tüm API host'unu sonlandırır — PG restart (57P01) veya bağlantı doygunluğu (53300) gibi geçici koşullar da bu dala düşer.

**Etki.** Kritik olmayan audit yazma yolundaki geçici bir PostgreSQL koşulu (PG failover/restart, too_many_connections, out_of_memory) tüm Saydin.Api host'unu sonlandırır → tam servis kesintisi ve `restart: unless-stopped` altında crash-loop; her restart kuyruktaki activity log'ları kaybettirir.

**Öneri.** Transient allowlist'i 53xxx/57xxx/55P03/25P02'yi kapsayacak şekilde genişlet; FatalHost'u yalnız gerçek şema/yetki sınıflarına (42xxx, 3D000, 28xxx) daralt; 22xxx'i ToxicRow'a taşı. Ayrıca audit yolunun ürün yolunu düşürmemesi için `HostOptions.BackgroundServiceExceptionBehavior = Ignore` + bounded restart döngüsü + LogCritical/metrik ile görünür kıl. ActivityLogWriterTests InlineData setini yeni sınıflandırmayla güncelle.

---

### 7. Backup login'in VALID UNTIL değeri marker'a pinlenmiş: süre dolduğunda her production `ensure` exit 69 ile deploy'u kilitler, süreyi uzatmak ise exit 67 verir

| | |
|---|---|
| **Önem** | High |
| **Hat** | L08 — RoleBootstrap + DatabaseSecurity |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED (verifier) |
| **Konum** | `src/Saydin.DatabaseRoleBootstrap/RoleBootstrapRunner.cs:39-52 (ensure→AuthenticateBackupAsync), 317-363 (EnsureAsync), 493-499 (RejectRoleCollisionsAsync marker eşitliği); src/Saydin.DatabaseSecurity/RoleContract.cs:206-218 (BackupLogin marker'ı valid-until içerir); src/Saydin.DatabaseRoleBootstrap/RoleBootstrapDatabaseOperations.cs:775-789 (ValidateNewBackupValidityAsync: +24h..+93 gün), 1055-1064 (backup_authentication_failed → exit 69)` |

**Bulgu.** RoleContract.BackupLogin (satır 206-218) marker'a `valid-until=<zaman>` gömüyor. EnsureAsync (RoleBootstrapRunner.cs:327-350) her koşuda `BackupLogin(1, options.BackupV1ValidUntilUtc)` bekliyor ve backupPhaseReady (migrasyonlar uygulandıktan sonra true) olduğunda RunAsync satır 49-52'de `AuthenticateBackupAsync` ile v1 backup login'ine gerçek fiziksel replication bağlantısı kuruyor. Rol VALID UNTIL süresi dolduğunda bu bağlantı reddedilir ve RoleBootstrapDatabaseOperations.cs:1055-1064 `backup_authentication_failed` → BootstrapExitCodes.AuthenticationRejected (69) döner; role-bootstrap `service_completed_successfully` olmadığı için migrator ve tüm downstream servisler başlamaz. Süreyi uzatmak için `SAYDIN_BACKUP_V1_VALID_UNTIL` değiştirilirse beklenen marker değişir; RejectRoleCollisionsAsync satır 491-495 ve EnsureRoleAsync'in `existing.Marker != role.Marker` dalı (RoleBootstrapDatabaseOperations.cs:140-143) RoleCollision (exit 67) fırlatır. ValidateNewBackupValidityAsync (satır 785) yeni rolde valid-until'i en fazla +93 gün ile sınırladığı için bu tarih kaçınılmaz olarak ~3 ay içinde gelir. `rotate --login backup --login-version 2` yeni bir v2 rolü yaratır ama ensure hâlâ v1'i doğruladığı için sorunu çözmez. Doküman kontrolü: `grep -rn BACKUP_V1_VALID_UNTIL` yalnız compose/test/CI dosyalarını buluyor; `grep -ril 'valid.until|backup_login|backup-v1' docs/` boş — yenileme prosedürü hiçbir yerde yazılı değil. infrastructure/deployment/deploy-release.sh'de backup kimlik bilgisi yenilemeye dair hiçbir adım yok.

**Etki.** Deploy zinciri tamamen kilitlenir ve desteklenen bir kurtarma yolu yoktur; tek çıkış elle `DROP ROLE <prefix>_backup_login_v1` + yeni valid-until ile ensure'dur — bu da hiçbir yerde belgelenmemiştir. Ayrıca aynı anda backup kimlik bilgisi de geçersizleşeceği için yedekleme akışı sessizce durur.

**Öneri.** (a) Marker'dan valid-until'i çıkar veya ensure'da mevcut backup rolünün VALID UNTIL değerini `ALTER ROLE ... VALID UNTIL` ile ileri taşımaya izin ver (marker'ı yeni değere güncelleyerek); (b) süre dolmadan önce uyaran bir kontrol/metric ekle; (c) backup kimlik bilgisi yenileme ve v1→v2 kesme prosedürünü `docs/deployment/` altında runbook olarak yaz ve deploy-release.sh'a ön-kontrol ekle.

---

### 8. Tek bir asset'in permanent window'u tüm ingestion sürecini süresiz durduruyor ve sınırsız crash-loop üretiyor

| | |
|---|---|
| **Önem** | High |
| **Hat** | L09 — Ingestion ledger ve write fence |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:134-137,198-199,233-234; Workers/IngestionOrchestrator.cs:104-136; Workers/Program.cs:160-161; Repositories/IngestionWindowRepository.cs:275-302,516-529; Adapters/AdapterCompleteness.cs:38-42; docker-compose.yml:485` |

**Bulgu.** Tek bir asset/scope için permanent_failed olan bir ingestion window, worker'da yakalanmayan PermanentIngestionWindowException üretir; orchestrator bunu paylaşılan fatal alanı sayarak tüm sibling worker'ları iptal eder ve süreci exit 1 ile düşürür — compose `restart: unless-stopped` süreci yeniden başlattığında aynı window tekrar PermanentBlocked döndüğü için sınırsız crash-loop oluşur ve kurtarma yalnız imzalı DataRepair `requeue_permanent_window` planıyla mümkündür.

**Etki.** Tek sembol veya tek provider credential sorunu tüm price + TÜFE ingestion'ını durdurur; süreç sürekli flap ettiği için metrik/telemetri export'u da kesilir. Kurtarma insan müdahalesi + imzalı repair planı gerektirir. docs/analysis/06-remediation-progress.md 'permanent blocker + operator requeue' kanıtını listeler ama worker izolasyonu olmadığını residual olarak açıklamaz.

**Öneri.** Permanent window'u worker/asset düzeyinde izole et (o scope'u devre dışı bırakıp CalendarNotReady benzeri 'lane blocked' metrik + Critical log üret), süreci yalnız gerçekten kurtarılamaz altyapı hatalarında düşür. Ek olarak asset başına history/backfill başlangıç tarihi alanı ekleyerek yeni asset eklemenin tüm hattı kilitlemesini engelle.

---

### 9. Retryable window'ların yeniden denenmesi ledger'ın next_attempt_at sözleşmesini değil worker zamanlayıcısını izliyor: fiili gecikme 24 saat (price) / 1 ay (EVDS)

| | |
|---|---|
| **Önem** | High |
| **Hat** | L09 — Ingestion ledger ve write fence |
| **Kategori** | correctness |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:50-51,60,79,82-83,113,115,128-133,345-350; Workers/EvdsInflationWorker.cs:34-49,104-106,320-332; Repositories/IngestionWindowRepository.cs:472-480,275-314; Repositories/PriceIngestionRepository.cs:16-21; Adapters/EvdsInflationAdapter.cs:111-118` |

**Bulgu.** Ledger'a yazılan `next_attempt_at` (price 5 dk, EVDS 30 dk) yalnız claim tarafında 'due' kontrolü olarak kullanılıyor; worker'ların steady-state döngüsü bu zamana göre uyanmadığı için retryable bir hata pratikte bir sonraki günlük koşuya (price) veya bir sonraki aylık koşuya (EVDS) ertelenir, üstelik başarısız olan ilk asset o kaynağın kalan tüm asset'lerini o tur için işlemsiz bırakır.

**Etki.** Geçici bir provider hatası bir günlük fiyat boşluğuna; TÜİK yayın kayması bir ay boyunca eksik CPI'a (DCA reel getiri alanlarının null kalmasına) yol açar. Sürekli flaky tek bir asset, aynı kaynağın (deterministik olmayan sırada) sonraki asset'lerini süresiz aç bırakabilir.

**Öneri.** DrainAsync'te `NotDue`/`Busy` durumunu 'tüm scope'u bırak' yerine 'bu window'u/asset'i atla, diğerleriyle devam et' semantiğine çevir; steady-state beklemesini `min(en yakın next_attempt_at, bir sonraki planlı koşu)` ile kısalt ve asset listesini deterministik sırala (ORDER BY symbol/id).

---

### 10. Provider yanıt gövdesi hiçbir wall-clock timeout ile sınırlı değil; askıda kalan bir provider worker'ı süresiz kilitler

| | |
|---|---|
| **Önem** | High |
| **Hat** | L10 — Provider adapter/mapper |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Extensions/HttpResilienceExtensions.cs:18,44-47; src/Saydin.PriceIngestion/Program.cs:81-124; src/Saydin.PriceIngestion/Adapters/ProviderPayload.cs:22-35; src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:356-410` |

**Bulgu.** Beş provider HTTP client'ında `HttpClient.Timeout` Infinite'e çekilmiş, Polly pipeline'ında yalnız SendAsync'i saran 30 sn attempt timeout kalmıştır ve TotalRequestTimeout bu commit'te silinmiştir; ResponseHeadersRead ile yapılan gövde okuması ve worker tarafındaki lease-renewal döngüsü hiçbir wall-clock sınıra bağlı değildir.

**Etki.** Header'ları gönderip gövdeyi askıya alan bir provider bağlantısı (yarı-açık TCP, proxy stall) ilgili worker'ı süresiz bloke eder; lease sonsuz yenilendiği için başka replica pencereyi devralamaz, ingestion_jobs terminal duruma geçmez ve o kaynağın fiyat/TÜFE güncelliği durur. Süreç-seviyesi heartbeat healthcheck'i yeşil kaldığı için otomatik restart da tetiklenmez. CLAUDE.md 'Dış API isteklerinde timeout zorunludur' + '+3 dk TotalRequestTimeout' maddelerinin ihlalidir.

**Öneri.** Pipeline'a en dışta bir total-request timeout stratejisi geri ekle (retry zincirini kapsayan ~3 dk) ve/veya `client.Timeout` Infinite override'ını kaldır; ayrıca adapter çağrısını worker'da `CreateLinkedTokenSource(ct)` + `CancelAfter(deadline)` ile mutlak bütçeye bağlayıp aşımda `RetryableFailure("provider_deadline")` üret. Gövde okumasını kapsayan bir stall testi (yavaş stream fake handler) ekle.

---

### 11. SaydinActivityLogLoss alert'i yapısal olarak hiç tetiklenemez (vector aritmetiğinde label uyuşmazlığı)

| | |
|---|---|
| **Önem** | High |
| **Hat** | L15 — Production deployment ve observability |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/prometheus/rules/api.yml:40-48; src/Saydin.Api/BackgroundServices/ActivityLogWriter.cs:210-213; src/Saydin.Api/Services/ActivityLogChannelTelemetry.cs:22-42` |

**Bulgu.** SaydinActivityLogLoss kuralındaki üç sayaç toplaması, sayaçların tag setleri (outcome vs action vs action+reason) farklı olduğu için Prometheus one-to-one vector matching kuralı gereği daima boş vektör üretir; alert hiçbir koşulda firing olamaz — promtool ile ampirik olarak doğrulandı.

**Etki.** ADR-006 kapsamındaki finansal denetim izinin (activity_logs) yazım kaybı için tanımlanmış tek critical alert ölüdür; runbook, Alertmanager critical route'u ve 30 dk repeat_interval atıl kalır. Kayıp yalnız Serilog warning'lerinden (Loki) fark edilebilir, alarm üretilmez.

**Öneri.** İfadeyi label eşleşmesi gerektirmeyen forma çevir: `sum(increase({__name__=~"saydin_activity_log_(write_failures|queue_drops|queue_rejected_writes)_total",job="saydin-api"}[10m])) > 0`. `sum(A)+sum(B)+sum(C)` biçimi de yetersiz (bir seri hiç yoksa yine boş). Düzeltmeyi rules.test.yml'e hem tek-sayaç-artıyor pozitif hem hiçbiri-artmıyor negatif senaryosuyla kilitle.

---

### 12. Otomatik deploy monitoring düzlemini (Prometheus/Alertmanager/exporter'lar) hiç başlatmıyor

| | |
|---|---|
| **Önem** | High |
| **Hat** | L15 — Production deployment ve observability |
| **Kategori** | ci-cd |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/release/deploy-release.sh:80,156,165,174; infrastructure/deployment/compose.production.yml:444-527; infrastructure/deployment/README.md:99-106` |

**Bulgu.** Üretim/staging deploy otomasyonunun tamamı olan deploy-release.sh, Prometheus/Alertmanager ve dört exporter'ı hiç başlatmaz, hiçbiri başlatılan servislerin bağımlılığı değildir ve script bunların çalıştığını doğrulayan bir kapı içermez; buna rağmen deploy 'passed' olarak imzalanır.

**Etki.** Temiz bir host'ta 35 alert kuralının tamamı değerlendirilmez (API down, backup stale/failure, ingestion stale, sertifika süresi, disk baskısı dahil); backup freshness textfile metriklerini okuyacak node-exporter da ayakta olmaz. Deploy başarılı işaretlenirken toplam alarm kaybı yaşanır.

**Öneri.** deploy-release.sh'e telemetri/exporter aşaması ekle (`compose up -d prometheus alertmanager postgres-exporter redis-exporter blackbox-exporter node-exporter`) ve backup'taki gibi `ps --status running` + Prometheus `/-/ready` ve `/api/v1/rules` (kural sayısı > 0) kapısı koy; alternatif olarak compose'da bu servisleri bir bağımlılık zincirine bağla. README adım 5 ile otomasyon arasındaki çelişkiyi kapat.

---

### 13. Restore drill ilk Docker adımında her zaman başarısız olur: `--cap-drop ALL` altında `chown` EPERM verir

| | |
|---|---|
| **Önem** | High |
| **Hat** | L16 — Backup/restore ve supply chain |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/backup/restore-drill.sh:112-115 (karşı taraf: infrastructure/backup/Dockerfile:14-15, infrastructure/backup/restore_target_guard.py:36-49)` |

**Bulgu.** `restore-drill.sh` volume hazırlama adımı `--user 0:0` ile birlikte `--cap-drop ALL` kullandığından CAP_CHOWN yoktur; taze `docker volume create` volume'ü root:root olduğu için `chown 1001:1001 /restore-drill` her koşuda EPERM verir ve `set -eu` altında drill daha restic restore'a gelmeden ölür.

**Etki.** Projenin tek PITR/DR kanıt mekanizması hiç uçtan uca çalışmaz; RPO/RTO iddiası doğrulanmamış kalır. promote-production.yml:66-86 üretim admission'ı için ≤31 günlük imzalı `restore-receipt-*.json` şart koştuğundan üretim promotion'ı da kalıcı olarak bloke olur. Fail-closed olduğu için veri kaybı/güvenlik açığı yaratmaz.

**Öneri.** Bu tek adıma `--cap-add CHOWN` ver (diğer container'lar `--cap-drop ALL` kalsın), veya volume'ü baştan 1001 sahipli materyalize eden bir yöntem kullan (ör. `docker run --user 1001:1001 ... busybox true` ile init edip guard'ın `expected_uid`'ini eşleştir). Değişiklikten sonra drill'i bir kez uçtan uca yeşil koştur ve gerçek bir receipt üret; ayrıca backup-static-self-test.py'ye yalnız metin değil, en az bu volume-hazırlama adımını gerçekten çalıştıran bir smoke ekle.

---

### 14. Base backup tüm PGDATA'yı 2 GiB tmpfs'e (RAM) yazıyor; 1 GiB mem_limit ile veri büyüdükçe base yedekleri kırılıyor

| | |
|---|---|
| **Önem** | High |
| **Hat** | L16 — Backup/restore ve supply chain |
| **Kategori** | data-integrity |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/backup/backup-entrypoint.sh:50-52,124-140; infrastructure/deployment/compose.production.yml:696-731; infrastructure/deployment/production.env.example:65` |

**Bulgu.** `base_backup` tüm PGDATA'yı yalnız tmpfs olan `/tmp` altına materyalize eder; container'ın `mem_limit` değeri 1g olduğundan tmpfs sayfaları memory cgroup'una sayılır ve veri dizini ~1 GiB'a yaklaştığında base backup OOM/ENOSPC ile kalıcı olarak başarısız olur.

**Etki.** Base yedek zinciri kalıcı olarak kırılır. WAL retention `--keep-within 14d` (backup-entrypoint.sh:172) olduğundan, son başarılı base 14 günü aştığı anda o base'e ait replay WAL'i de budanır ve PITR tamamen imkânsız hale gelir — host/volume kaybında geri dönüşsüz veri kaybı. Sessiz değil (host-backup.yml:46-52 base yaşı >93600s ve :54-59 failure metriği alarm üretir) ama tasarımda çözüm yolu yok; tmpfs boyutu compose'da sabit kodlanmış.

**Öneri.** `pg_basebackup --format=tar --pgdata=- --wal-method=fetch` çıktısını `restic backup --stdin` ile stream et (RAM/diske materyalize etme), ya da `database-backup` servisine base staging için disk-backed ayrı bir volume ekleyip `/tmp`'ten çıkar. Streaming'e geçilirse `pg_verifybackup` doğrulaması restore tarafına taşınmalı. Ayrıca `RESTIC_CACHE_DIR`'ı da tmpfs dışına al.

---

### 15. Required ingestion-ledger suite'i hard-coded `schema_migrations count = 23` nedeniyle kırmızı; iddia edilen "39/39, 0 failed" kanıtı güncel ağaçla çelişiyor

| | |
|---|---|
| **Önem** | High |
| **Hat** | L18b — PriceIngestion/calendar test kalitesi |
| **Kategori** | ci-cd |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.PriceIngestion.IntegrationTests/PriceAuthorityMigrationIntegrationTests.cs:119-124 ↔ IngestionDatabaseFixture.cs:57-60 ↔ .github/workflows/ci.yml:572-577` |

**Bulgu.** `Migration020_ManagedSchema_PreservesMultiWindowPayloadProvenanceAndRejectsDrift` testi `schema_migrations` terminal-state sayısını `=23` olarak sabitliyor; oysa ağaçtaki migration seti 24 (001…022, `012b` dahil) ve hem fixture readiness probe'u hem CI schema kapısı 24 bekliyor — bu Fact required ingestion-ledger suite'inde deterministik olarak fail eder ve `docs/analysis/06-remediation-progress.md`'deki "39 passed, 0 failed" kabul kanıtı bu ağaç için geçerli değildir.

**Etki.** Required CI `integration-test` job'ında `ingestion-ledger-tests` adımı non-zero döner; write fence + authority + calendar importer davranış kapısı tamamen bloke olur. Aynı zamanda remediation dokümanındaki kabul kanıtı bayat (022 eklenmeden önceki snapshot) — "kapandı" denen bir kapı gerçekte kapalı değil.

**Öneri.** Sabit sayıyı testten kaldır: `MigrationTrustRoot.Versions.Count` üzerinden türet veya yalnız `NOT EXISTS (SELECT 1 FROM schema_migrations WHERE state NOT IN ('succeeded','skipped_optional'))` + gerekli versiyonların varlığını doğrula. Migration sayısı tek bir yerde (fixture probe'u veya CI kapısı) yaşasın. Ardından progress tablosundaki 39/39 satırını yeniden ölçülmüş değerle (40 executable case) güncelle.

---

### 16. DataRepair'in yıkıcı-senaryo guard'larının büyük bölümü hiçbir testte doğrulanmıyor

| | |
|---|---|
| **Önem** | High |
| **Hat** | L18e — DQA/DataRepair test kalitesi |
| **Kategori** | test-quality |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.DataRepair/RepairDatabase.cs:118,199,280,283,297,299,334,360,526; src/Saydin.DataRepair/RepairExecutor.cs:112,161,189; src/Saydin.DataRepair/RepairTrustLease.cs:56; src/Saydin.DataRepair/ReceiptStore.cs:53,84,96,116,120,325; tests/Saydin.DataRepair.IntegrationTests/RepairExecutorIntegrationTests.cs:1-207` |

**Bulgu.** DataRepair'de 77 benzersiz fail-closed reject kodu var; bunlardan 60'ı ne unit ne integration testlerinde hiç geçmiyor — plan-dışı/çoklu window, çalışan job, daha yeni terminal window, guard satır bütçesi, transaction-içi guard değişimi, apply/rollback CAS, preimage restore hatası, commit belirsizliği, lease kaybı ve receipt bütünlüğünün tamamı dahil. docs/analysis/06-remediation-progress.md:623'teki %83,63 line kapsaması bu karar dallarının tetiklendiğini göstermiyor.

**Etki.** DataRepair, production ingestion ledger'ını mutasyona uğratan tek araçtır ve tüm güvenliği bu fail-closed guard'lara dayanır. Bir guard'ın koşulunu ters çeviren/gevşeten bir refactor, mevcut 15 unit + 7 integration testinin tamamı yeşil kalarak ve `--minimum-executed 7` kapısından geçerek CI'dan çıkabilir; sonuç plan dışı satır güncellenmesi, yanlış preimage'a rollback veya denetlenemez receipt olabilir.

**Öneri.** Her yıkıcı guard için en az bir gerçek-PG negatif testi ekleyin: (a) plan target'ı olmayan window'a işaret etsin (repair_window_missing), (b) window'a 'running' job eklenip apply denensin, (c) daha yeni terminal window eklensin, (d) guard bütçesini aşacak ilişkili satır üretilsin, (e) transaction içinde guard'ı değiştiren ikinci oturum tetiklensin (repair_guard_changed_inside_transaction), (f) lease bağlantısı `pg_terminate_backend` ile düşürülüp repair_target_lock_lost beklensin ve lease canlılığı mutasyon transaction'ı içinde yeniden doğrulansın, (g) final receipt imzası/dosya bütünlüğü bozulup idempotent yolun fail-closed olduğu gösterilsin. Ardından `--minimum-executed 7` ratchet'ini yeni sayıya yükseltin.

---
