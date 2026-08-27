# Remediation Denetimi — Önceki Review Bulguları Gerçekten Kapandı mı?

> Bu hat (`R17`) yalnız bu iş için ayrıldı: [`../pr-review/01-findings-critical-high.md`](../pr-review/01-findings-critical-high.md)
> içindeki **2 Critical + 14 High** bulgunun her biri mevcut kodda tek tek arandı ve
> [`../pr-review/07-remediation-progress.md`](../pr-review/07-remediation-progress.md)'nin
> "Verified" iddiaları bu kanıtla karşılaştırıldı.

## Kapsam beyanı

`docs/analysis/pr-review/01-findings-critical-high.md` içindeki 16 bulgunun her biri için hem düzeltme kodunu hem karşı tarafını (endpoint↔service↔repository↔SQL fonksiyonu, compose↔validator↔CI, script↔workflow, alert↔rules.test/inventory.test, test↔ratchet) okudum; `07-remediation-progress.md`'nin "Verified" iddialarını bu kanıtla karşılaştırdım. Okunanlar: docker-compose.yml + validate-development-compose.py, deploy-release.sh + release_manifest.py/render-deployment-env.py/validate-release.py, ApiPortBoundaryMiddleware + Caddyfile + iki-port Kestrel testi, EndpointExtensions/DistributedSecurityLimiter(+Options/appsettings), DcaCalculator + testleri, ActivityLogBatchStore/Writer + classifier testleri, RoleContract/RoleBootstrapDatabaseOperations + backup-login-renewal runbook + host-backup alertleri, BaseAssetWorker/IngestionWindowRepository/IngestionFreshnessTelemetry, HttpResilienceExtensions, prometheus rules+testleri, restore-drill.sh + backup-static-self-test.py, backup-entrypoint.sh staging kapıları, ingestion fixture/CI schema gate, DataRepair guard test matrisi, 021/023/024 migration'ları ve InstallationRepository. Okunmayan: tam test/CI koşusu (lokal .NET yok — davranış statik olarak doğrulandı), calendar-data ve DQA'nın bu hatta ait olmayan bölümleri.

## Gerçekten kapanan bulgular

Aşağıdakiler kök neden düzeyinde kapatılmış ve çoğu regresyon testiyle kilitlenmiştir:

- PR1 bulgusu #1 (kök compose role-bootstrap backup argümanları) → **FIXED** ve sınıfı kapatılmış: `docker-compose.yml:36,67,282-297,325` secret üretim/mount/argüman zincirini tamamlıyor, `validate-development-compose.py:94-102` argüman sözleşmesini statik kapıya bağlıyor ve satır 180-183'teki mutation fixture'ları (`missing_backup_argument`, `missing_post_bootstrap`, `post_bootstrap_bypasses_migrator/hba`) kapının gerçekten reddettiğini kanıtlıyor — semptom değil kök neden kapanmış.
- PR1 bulgusu #2 (deploy-release.sh KeyError) → **FIXED** ve tek-kaynak ilkesiyle: inline `runtime` sözlüğü tamamen kaldırılıp `render-deployment-env.py --verify-existing`'e devredildi; `release_manifest.py:31 EXPECTED_RUNTIME_IMAGES = tuple(sorted(RUNTIME_IMAGE_ENV_KEYS))` iki eşlemeyi tek kaynaktan türetiyor ve `validate-release.py:117` sözlüğün geri gelmesini (`runtime={` / `RUNTIME_IMAGE_ENV_KEYS` metni) statik olarak yasaklıyor.
- PR1 bulgusu #3 (management port trailing-slash bypass) → **FIXED** ve savunma derinliğiyle: `ApiPortBoundaryMiddleware.NormalizePath` segment tabanlı normalizasyon + OrdinalIgnoreCase karşılaştırma yapıyor, `ApiPortEndpointSelectorPolicy` + `ApiEndpointSurfaceMetadata` ile sınıflandırma hatası endpoint eşleşmesini etkileyemiyor, Caddyfile `path_regexp` ile `//metrics//`-tipi varyantları da kapatıyor ve `ApiManagementBoundaryHttpTests` gerçek iki Kestrel listener'ı üzerinde `/HEALTH/READY/`, `//health//ready//`, `/METRICS/`, `//metrics//` ile birlikte X-Forwarded-Host/Port spoof senaryolarını da kilitliyor.
- PR1 bulgusu #7 (backup login VALID UNTIL kilidi) → **FIXED**: `RoleBootstrapDatabaseOperations.cs:195-230` forward-only `ALTER ROLE ... VALID UNTIL` uzatma yolunu marker güncellemesiyle birlikte açıyor, satır 219-220 geriye alma girişimini `backup_valid_until_regression` ile reddediyor; `saydin_backup_login_valid_until_timestamp_seconds` metriği + `host-backup.yml:62-80`'deki missing/expiring/expired alert üçlüsü + `rules.test.yml:111-159`'daki üç promtool senaryosu + `docs/runbooks/backup-login-renewal.md` ile prosedür, alarm ve test birlikte geldi.
- PR1 bulgusu #10 (provider gövde timeout'u) → **FIXED** ve iki katmanlı: `HttpResilienceExtensions.cs:45-47` 3 dk `TotalRequestTimeout`'u pipeline'a geri koydu, ayrıca `BaseAssetWorker.WithLeaseRenewalAsync` (satır 384-428) adapter çağrısını `ProviderDeadline` ile mutlak bütçeye bağlayıp aşımda durable `RetryableFailure("provider_deadline")` üretiyor ve askıda kalan task'ı `ObserveDetachedAsync` ile sızdırmadan gözlemliyor — CLAUDE.md'nin timeout sözleşmesi tekrar geçerli.
- PR1 bulgusu #11 (SaydinActivityLogLoss ölü alert) → **FIXED** ve testle kilitlenmiş: `api.yml:42` önerilen `sum(increase({__name__=~...}[10m])) > 0` formuna geçti; `inventory.test.yml:18-40` tek sayacın (yalnız `queue_drops`) arttığı pozitif senaryoyu ve satır 283'teki sabit seri ile hiçbir sayacın artmadığı negatif senaryoyu birlikte doğruluyor — bulgu raporundaki tam öneri uygulanmış.
- PR1 bulguları #8 ve #9 (permanent window izolasyonu + next_attempt_at) → **FIXED**: `BaseAssetWorker.BackfillAsync` asset listesini `OrderBy(Symbol, Ordinal).ThenBy(Id)` ile deterministik sıralıyor, permanent scope artık süreci düşürmeden `DrainResult.PermanentBlocked` ile izole ediliyor ve sibling'lar devam ediyor; `WorkerPass`/`GetDelayUntilNextRun` (satır 372-382) uyanmayı `min(en yakın next_attempt_at, sonraki planlı koşu)` semantiğine çevirdi. Freshness sorgusundaki source başına `min(last_success_at)` sayesinde izole edilen asset yine de staleness alarmı üretiyor.
- PR1 bulgusu #16 (DataRepair guard test boşluğu) → **FIXED** ve ratchet yükseltilmiş: `RepairGuardIntegrationTests.cs` gerçek PG üzerinde bulgunun (a)-(g) önerilerinin tamamını karşılıyor (`repair_window_missing`, `repair_running_job_rejected`, `repair_newer_terminal_window_rejected`, `repair_guard_row_budget_exceeded`, `repair_guard_changed_inside_transaction`, `repair_cas_failed`, `repair_target_lock_lost`, tamperlanmış final receipt); integration test sayısı 7'den 27'ye çıkmış ve CI `--minimum-executed` 7'den 32'ye yükseltilmiş.
- Coverage kapıları gevşetilmemiş: `coverage-thresholds.json` / `-unit.json` global `overall` eşikleri `f9f608d` ile birebir aynı kalmış, yalnız `Saydin.Api.Services` için yeni critical-namespace eşiği eklenmiş — remediation sırasında ratchet düşürme (kapı gevşetme) yapılmamış.

## Kapanmayan, kısmen kapanan veya yeni risk üreten yüzeyler

Bunlar doğrulayıcı agent tarafından bağımsız olarak teyit edilmiştir:

### [High] `resolve_installation_and_rehash` her kimlik doğrulamasında index kullanamayan tarama + satır kilidi yapıyor

| | |
|---|---|
| **Konum** | `infrastructure/postgres/migrations/024_installation_credential_rehash.sql:92-102,136-147; src/Saydin.Api/Repositories/InstallationRepository.cs:26-36; src/Saydin.Api/Endpoints/EndpointExtensions.cs:41; infrastructure/postgres/migrations/021_api_trust_expand.sql:93,257` |
| **Doğrulama** | CONFIRMED |

**Durum.** 024 ile kimlik doğrulama hot-path'i sargable olmayan bir predikata taşındı: her korumalı istek, aktif key version'daki tüm `installation_credentials` satırları için 32 iterasyonluk bir PL/pgSQL karşılaştırması çalıştırabilir ve eşleşen satırda koşulsuz `FOR UPDATE` kilidi alır. Geçerli token'da istek başına 1 tarama, geçersiz/eski token'da 3'e kadar tarama olur (keyring active=max key olduğu için azalan sırada denenir).

**Etki.** Kullanıcı sayısıyla doğrusal büyüyen kimlik doğrulama maliyeti: 100k installation'da her istek milyonlarca PL/pgSQL işlemi tetikler; geçersiz token floodu maliyeti 3x'ler. Ayrıca aynı principal'ın paralel istekleri `FOR UPDATE OF principal` üzerinde serileşir. Ölçekte kendi kendine DoS ve p99 latency çöküşü.

**Öneri.** Eşitlik predikatını geri getir (`credential.secret_hash=p_secret_hash`) ve `installation_verifier_matches`'i yalnız index'ten seçilen tek satır üzerinde guard olarak çalıştır. Steady-state'te (p_key_version=p_active_key_version) hiç yazma/kilit gerekmediği için 021'in `STABLE`/`LANGUAGE sql` yolunu kullan; rehash'i yalnız versiyon farkı varsa ayrı yazma transaction'ında yap. ≥100k credential seed'i ile latency regresyon testi ekle.

### [Medium] Activity-log yazıcısının catch-all dalı bilinmeyen SQLSTATE'te tüm API host'unu düşürüyor (PARTIALLY-FIXED)

| | |
|---|---|
| **Konum** | `src/Saydin.Api/BackgroundServices/ActivityLogBatchStore.cs (Classify, catch-all FatalHost dalları); src/Saydin.Api/BackgroundServices/ActivityLogWriter.cs (FlushAsync FatalHost dalı, ExecuteAsync); src/Saydin.Api/Program.cs (HostOptions ayarı yok)` |
| **Doğrulama** | CONFIRMED |

**Durum.** Bilinen SQLSTATE listesi genişletildi ve 57P01/53300 gibi somut örnekler kapandı; ancak enümere edilmeyen her SQLSTATE (25006, 58030, 58P01, 54000, XX000) ve her PostgresException olmayan non-transient hata hâlâ FatalHost'a düşüp exception'ı ExecuteAsync dışına fırlatıyor. API HostOptions ayarlamadığı için bu, denetim (audit) yolundaki bir hatanın tüm ürün API host'unu durdurması demektir.

**Etki.** Kritik olmayan audit yazma yolundaki bilinmeyen bir DB hata sınıfı tüm ürün API'sini düşürüp `restart: unless-stopped` altında crash-loop üretebilir; her restart kuyruktaki activity log'ları kaybeder. `07-remediation-progress.md`'nin "writer-local bounded recovery" özeti catch-all dalı için geçerli değil.

**Öneri.** Bilinmeyen SQLSTATE varsayılanını TransientBatch (bounded retry sonrası drop + metrik) yap; FatalHost'u yalnız açıkça enümere edilmiş şema/yetki sınıflarıyla sınırla. Ek olarak API'de `HostOptions.BackgroundServiceExceptionBehavior = Ignore` + `saydin_activity_log_writer_stopped` metriği + critical alert ile fail-fast'i yalnız writer'a lokalize et. Test setine 25006 ve 58030 ekle.

### [Medium] CGNAT arkasındaki mobil kullanıcılar için registration kapısı public IP başına 5/gün ile kilitleniyor; cache-strategy.md NAT davranışını ters anlatıyor

| | |
|---|---|
| **Konum** | `src/Saydin.Api/Security/DistributedSecurityLimiter.cs (TryAcquireRegistrationAsync, TryNormalizeAddress); src/Saydin.Api/appsettings.json:37-41; src/Saydin.Api/Security/DistributedSecurityLimiterMiddleware.cs (TryGetTrustedClientAddress); docs/cache-strategy.md (registration paragrafı)` |
| **Doğrulama** | CONFIRMED |

**Durum.** Registration exact bucket anahtarı tam istemci IP'sinin HMAC pseudonym'idir; CGNAT arkasındaki tüm aboneler aynı bucket'ı paylaşır ve public IP başına günde 5 (saatte 3) kayıtla sınırlanır. `docs/cache-strategy.md`'deki NAT ifadesi olgusal olarak yanlıştır. Aynı /24 için 500/gün hesaplama sınırı da paylaşılan bir tavan üretir.

**Etki.** Türkiye'de en yaygın erişim yolu olan CGNAT mobil ağlarda yeni kullanıcı onboarding'i ve hesaplama akışı meşru kullanıcılar için bloklanabilir; doküman bu riski kapatılmış gibi anlattığı için operatör/geliştirici yanlış varsayımla ilerler.

**Öneri.** (a) cache-strategy.md'deki NAT cümlesini düzelt. (b) Registration'ı attestation (App Attest/Play Integrity) veya proof-of-work gibi kimlik sinyaline bağla; IP cap'i yalnız sinyal yoksa uygula. (c) Bilinen CGNAT/mobil ASN'ler için ayrı ve ölçüme dayalı bucket sınıfı tanımla; SecurityAdmissionTelemetry bucket/outcome dağılımını dashboard'la ve alarm kur. (d) 429 yanıtına registration ve calculation'ı ayırt eden lokalize `code` ekle.

### [Medium] DR düzeltmelerinin tek davranışsal kanıtı Docker'ın varlığına koşullu ve kontrol sayısı hiçbir yerde pinlenmemiş

| | |
|---|---|
| **Konum** | `infrastructure/backup/tests/backup-static-self-test.py (docker koşullu blok ve son özet satırı); .github/workflows/ci.yml:115-121; .github/scripts/validate-workflows.py (ratchet yok)` |
| **Doğrulama** | CONFIRMED |

**Durum.** İddia doğru. Tek fark: GitHub-hosted `ubuntu-latest` runner'da Docker her zaman mevcut olduğu için kapı bugün fiilen çalışıyor; risk, runner/daemon konfigürasyonu değiştiğinde kapının sessizce boşalması ve buna karşı hiçbir sayı ratchet'inin bulunmamasıdır.

**Etki.** #13 (`--cap-add CHOWN`) ve #14 (disk-backed staging) için tek davranışsal regresyon koruması, hiçbir uyarı üretmeden devre dışı kalabilir; PITR/DR güvencesi sessizce kaybolur ve CI yeşil kalır.

**Öneri.** Docker smoke'larını `required` dict'ine KOŞULSUZ ekle (Docker yoksa `False` yazıp fail et; lokal geliştirici için açık `--allow-no-docker` bayrağı bırak, CI'da verme). Ayrıca `backup_static_self_test_passed:<n>` beklenen sayısını validate-workflows.py'a release manifest self-test'teki gibi ratchet olarak pinle.

### [Low] Migration sayısı `26` sekiz ayrı noktada elle senkronize ediliyor; tek kaynak yok

| | |
|---|---|
| **Konum** | `tests/Saydin.PriceIngestion.IntegrationTests/IngestionDatabaseFixture.cs (schema_migrations sayacı); .github/workflows/ci.yml:612-640; .github/scripts/validate-workflows.py:92-95` |
| **Doğrulama** | CONFIRMED |

**Durum.** `26` sabiti gerçekten sekiz noktada tekrar ediyor ve tek kaynaktan türetilmiyor; ancak eksik güncelleme sessiz değil, isimlendirilmiş ve deterministik bir CI kırmızısı üretir. Sorun yanlış kapı riski değil, migration eklemenin sekiz noktalı elle senkronizasyon gerektirmesi ve bu noktaların hiçbir yerde listelenmemiş olmasıdır.

**Etki.** Rutin olması gereken migration ekleme işlemi kırılgan ve keşfedilmesi zor bir çoklu-dosya güncellemesine dönüşüyor; geliştirici hangi dosyaları güncelleyeceğini yalnız CI kırmızıya döndükten sonra arayarak öğreniyor.

**Öneri.** Kapıların sabit kalması iyi; eksik olan keşfedilebilirlik. `docs/development-guide.md`'ye "yeni migration eklerken güncellenecek kapı listesi" ekle veya `validate-workflows.py`'a beklenen sayıyı `infrastructure/postgres/migrations/` dizin sayısıyla karşılaştıran tek bir tutarlılık kontrolü koy; böylece tek bir hata mesajı bütün noktaları isimlendirir. Alternatif olarak fixture probe'unu sayıdan kurtarıp MigrationTrustRoot.Versions kümesinin tamamının terminal olduğunu doğrula.

### [Medium] Permanent-blocked ingestion lane'i için operatör kurtarma yolu alarm→runbook zincirinde yok

| | |
|---|---|
| **Konum** | `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs (RecordPermanentBlocked); docs/runbooks/ingestion-stale.md; docs/runbooks/data-repair.md; infrastructure/prometheus/rules/ingestion.yml (runbook_url)` |
| **Doğrulama** | CONFIRMED |

**Durum.** İddia doğru ve aslında bir adım daha kötü: yalnız `ingestion-stale.md` permanent-window kurtarmasını anlatmıyor değil, `data-repair.md` de `requeue_permanent_window` planını hiç anmıyor. Alarmdan kurtarma prosedürüne giden hiçbir doküman yolu yok; plan tipi yalnız kaynak kodda (SignedRepairPlan/RepairDatabase) ve testlerde adlandırılmış.

**Etki.** Tek bir asset'in permanent window izolasyonu nedeniyle çalan SaydinDailyIngestionStale alarmında nöbetçi operatör, durumun ne olduğunu ve tek çıkış yolunun imzalı DataRepair planı olduğunu runbook zincirinden öğrenemez; MTTR uzar ve runbook adım 2'nin yasakladığı worker restart'ı denenmeye açıktır.

**Öneri.** (a) SaydinMetrics'e bounded label'lı (`source`,`job_type`,`outcome_code`) `saydin_ingestion_permanent_blocked` sayacı ekle ve ayrı critical alert tanımla. (b) ingestion-stale.md'ye `ingestion_windows.state='permanent_failed'` teşhis sorgusu ve data-repair.md'ye link içeren bir adım ekle. (c) data-repair.md'de `requeue_permanent_window` planını açıkça belgele. (d) check-doc-links.py'a alert runbook'unun ilgili kurtarma runbook'una link verdiğini doğrulayan kural ekle.

### [Medium] DCA reel getirisi ara katkı ayları için exact-only kaldığından, her ayın ilk günlerinde tüm reel getiri null'a düşüyor ve /calculate ile çelişiyor

| | |
|---|---|
| **Konum** | `src/Saydin.Api/Services/DcaCalculator.cs (requiredExactMonths / missingMonths dalı / cache koşulu); src/Saydin.Api/Repositories/InflationRepository.cs (GetExactIndexValuesAsync vs GetIndexValuesAsync/GetNearestRowAsync); src/Saydin.Api/Services/WhatIfCalculator.cs` |
| **Doğrulama** | CONFIRMED |

**Durum.** Terminal ay LKV ile çözüldüğü için #5 kapandı; ancak M-1 gibi ara katkı ayları hâlâ exact-only. TÜİK M-1 TÜFE'sini tipik olarak ayın 3'ünde yayınladığından, ayın 1-3'ü arasında M-1'de katkısı olan (yani neredeyse tüm) aylık DCA planlarında tüm reel getiri alanları null döner; aynı anda /calculate LKV kullandığı için reel getiri gösterir. Bu istekler ayrıca hiç cache'lenmez.

**Etki.** Enflasyona göre düzeltilmiş getiri özelliği her ay birkaç gün için kapanıyor, aynı üründe iki ekran çelişiyor ve kullanıcıya tek sinyal jenerik bir uyarı kodu oluyor; cache'lenmeme nedeniyle bu pencerede DB yükü de artıyor.

**Öneri.** Ara aylar için de kademeli sözleşme uygula: eksik ara ay için `period_date <= o ay` en son final gözlemi deflatör kabul et, kullanılan ayı `InflationDataAsOf` ile bildir ve `RealReturnMethod`'u ayırt edici bir değere çevir (örn. `cashflow_cpi_lkv_v1`); yalnız hiç gözlem yoksa null'a düş. `inflationCalculationComplete=false` yolunu kısa TTL ile cache'le. FakeTimeProvider ile "ayın 2'si, M-1 CPI'ı yok" senaryosunu kilitleyen test ekle.

### [Medium] `07-remediation-progress.md`'nin "repo kapsamında açık kusur kalmadı" iddiası desteklenmiyor

| | |
|---|---|
| **Konum** | `docs/analysis/pr-review/07-remediation-progress.md (Sonuç bölümü ve "Otoritatif test kanıtı" tablosu)` |
| **Doğrulama** | CONFIRMED |

**Durum.** Belge, düzeltmelerin çoğunun gerçekten kök nedeni kapattığı doğru olmakla birlikte, blanket "açık kusur kalmadı" iddiasıyla en az dört doğrulanmış residual yüzeyi gizliyor ve pinlenmemiş sayılara (backup static 57, TRX/coverage tablosu) otorite atfediyor.

**Etki.** Belge production promotion kararını yönlendirdiği için ("dört dış kabul koşulu dışında repo hazır"), release kararı eksik bilgiyle alınır; bir sonraki reviewer bu yüzeyleri yeniden denetlemez.

**Öneri.** "Bilinen residual" bölümü ekle ve R17-01/02/03/04'ü açık yüzey olarak listele; "kusur kalmadı" cümlesini "envanterdeki bulgular için kök-neden düzeltmeleri uygulandı; aşağıdaki residual yüzeyler bilinçli olarak açık" ile değiştir. Test kanıt tablosunu bir CI artefaktına bağla veya sayıları çıkarıp yalnız CI'daki `--minimum-executed` ratchet'lerini otorite say.

### [Low] CLAUDE.md zorunlu hale gelen `bootstrap-dev-database.sh` ön koşulunu belgelemiyor

| | |
|---|---|
| **Konum** | `CLAUDE.md (Geliştirme Ortamı Kuralı bloğu); docker-compose.yml:249,271-283,316-325; README.md:27; docs/development-guide.md:21; CONTRIBUTING.md:10` |
| **Doğrulama** | CONFIRMED |

**Durum.** İddia doğru. Compose'un `:?` mesajı doğru script'i adlandırdığı için kurtarma anlıktır; sorun, agent'lara "varsayılan davranışı OVERRIDE eder" diye sunulan CLAUDE.md'nin dört dokümandan tek uyumsuz olanı olmasıdır.

**Etki.** Temiz checkout'ta CLAUDE.md'yi otoritatif kabul eden agent/geliştirici ilk komutta compose hatası alır; düşük etkili ama sözleşme dokümanının kendisiyle çelişen bir tutarsızlıktır.

**Öneri.** CLAUDE.md'nin ilgili kod bloğunun ilk satırına `./infrastructure/secrets/bootstrap-dev-database.sh   # tek sefer: purpose-specific dev secret'ları üretir` ekle. Kalıcı çözüm: validate-development-compose.py'a, compose'daki her `:?` zorunlu değişkeni için CLAUDE.md/README/development-guide'da bootstrap adımının bulunduğunu doğrulayan kontrol ekle.

### [Low] İstemci kaynaklı `X-Forwarded-*` durumunda kalıcı 503 üretiliyor ve Redis kesintisinden ayırt edilemiyor

| | |
|---|---|
| **Konum** | `src/Saydin.Api/Security/DistributedSecurityLimiterMiddleware.cs (InvokeAsync untrusted dalı, TryGetTrustedClientAddress); src/Saydin.Api/Security/SecurityAdmissionProblem.cs (WriteAsync); src/Saydin.Api/Runtime/ApiRuntimeContract.cs (ForwardLimit/RequireHeaderSymmetry); infrastructure/deployment/Caddyfile` |
| **Doğrulama** | CONFIRMED |

**Durum.** Fail-closed karar doğru ve güvenlik açığı değil; sorun sözleşmenin şekli: istemci kaynaklı, kalıcı ve istemci tarafından düzeltilebilir bir red, sunucu tarafı geçici arıza olan Redis kesintisiyle aynı 503/`security-limiter-unavailable`/`code` üçlüsüyle raporlanıyor. Metrikte reason ayrı (`ClientAddressUntrustedReason`) ama yanıt sözleşmesi ayırt etmiyor.

**Etki.** Kendi `X-Forwarded-*` header'ını ekleyen bir istemci/proxy arkasındaki kullanıcı kalıcı 503 alır; hem kullanıcı hem destek bunu backend arızası sanar ve alarm/metriklerde gerçek Redis kesintisiyle karışabilir.

**Öneri.** Untrusted-address durumunu ayrı bir ProblemDetails sözleşmesine bağla (örn. 400 `https://saydin.app/errors/forwarded-header-rejected` + ayrı `code`), lokalize `Detail`'de istemcinin `X-Forwarded-*` göndermemesi gerektiğini belirt ve meta repo api-contract.md'ye ekle. Alert tarafında bu reason'ı Redis kesintisinden ayıran ayrı seri tanımla.

## Ana agentin bağımsız teyidi

| Önceki bulgu | Durum | Kanıt |
|---|---|---|
| PR1 Critical #1 — kök compose backup argümanları | **FIXED** | `grep -ci backup docker-compose.yml` 0 → 19; secret üretimi `:36`, kurulum `:67`, `SAYDIN_BACKUP_V1_VALID_UNTIL` türetimi `:166-178`, `--backup-v1-valid-until` `:282`; yeni `database-backup-hba` servisi `:193` |
| PR1 Critical #2 — `deploy-release.sh` KeyError | **FIXED** | Hard-coded `runtime` map kaldırılmış, bağlama `render-deployment-env.py:45-60`'a taşınmış; şema `required` 12 anahtara çıkmış; `release-manifest-self-test.py:137-145` `loki`/`data_repair` için negatif test içeriyor; `deploy-release.sh:277` monitoring düzlemini başlatıyor |
| PR1 High — required CI `schema_migrations = 23` | **FIXED ama sınıf kapanmamış** | O örnek düzeltilmiş; ancak aynı anti-desen yepyeni bir required kapıda tekrar üretilmiş (bkz. Critical #1 bu raporda) |
| PR1 High — günlük TCMB timer 2. koşuda kırılıyor | **YENİ KATMANDA TEKRAR ETMİŞ** | `CalendarPlanMaterializer.cs:39,45-46` + `SecureBundleStorage.cs:58-63`: `SnapshotSetId`/`CoverageThrough` her gün değişiyor, plan yolu sabit, `run-acquisition.sh` dosyayı silmiyor → 2. günde `materialized_plan_conflict` |
