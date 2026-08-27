# Saydin.Services — Platform, Operasyon, Dokümantasyon ve Kalite İncelemesi

> İnceleme tarihi: 2026-08-18
>
> İnceleme türü: agresif, salt-okunur platform/operasyon/dokümantasyon/test incelemesi
>
> Sonuç: **0 Critical · 8 High · 19 Medium · 5 Low = 32 bulgu**

## 1. Yönetici özeti

Depoda önemli güvenli varsayılanlar bulunuyor: GitHub Actions izinleri daraltılmış ve action'lar tam SHA ile sabitlenmiş; Dockerfile tabanları digest ile sabit ve runtime kullanıcıları root değil; parolalar Compose'ta zorunlu; paket sürümleri merkezi; derleyici uyarıları hata; telemetry, health check ve ADR disiplini mevcut. Kök doğrulamada izole `saydin-review-20260818` Compose projesi ve host portu yayınlamayan override ile **380/380 test geçti** (API unit 286, ingestion unit 86, gerçek PostgreSQL/Redis integration 8; skip 0); fresh DB'de 16 migration uygulandı. Audit kapatılmış davranış doğrulamasında Release solution build 0 warning/0 error ve ingestion image build başarılıydı. Bu güçlü davranış kanıtına rağmen mevcut depo tek başına güvenli ve geri döndürülebilir bir production teslimatı tanımlamıyor.

En acil riskler şunlardır:

1. `dotnet list ... --vulnerable --include-transitive` üretim API zincirinde `Microsoft.OpenApi 2.0.0` için **High** seviye `GHSA-v5pm-xwqc-g5wc` buldu; audit açık normal `docker compose build` API restore aşamasında `NU1903` ile fail oldu. Bu güvenlik kapısı bypass edilmeden API image üretilemiyor.
2. İzole doğrulamada sekiz gerçek PostgreSQL/Redis entegrasyon testi skip olmadan geçti; ancak repository CI workflow'u PostgreSQL/Redis sağlamıyor. Testler altyapı yokluğunu `SkippableFact` ile başarı yerine skip kabul ettiği için mevcut yeşil GitHub Actions run'ı bu sekiz testin çalıştığını hâlâ kanıtlamıyor.
3. Production için kabul edilmiş tek-host Compose modeli, aksi açıkça belirtilmezse uygulamayı `Development` ortamında başlatıyor; rate limiting kapalı, API tüm host arayüzlerine bağlı ve `/metrics` kimlik doğrulamasız/throttle dışı.
4. PostgreSQL/Redis için yedekleme, off-host kopya, RPO/RTO, restore runbook'u ve restore testi yok. Named volume kalıcılık sağlar fakat yedek/DR değildir.
5. Dağıtım/promotion/rollback zinciri ve production alarm/SLO katmanı yok. Worker'lardan biri fatal hata sonrası kalıcı olarak durabilirken bağımsız heartbeat container'ı sağlıklı göstermeye devam edebilir.

Bu durum için önerilen yayın kararı: **High bulgular kapanmadan production readiness onayı verilmemeli.**

## 2. Kapsam ve yöntem

İncelenen yüzey:

- Kök seviyedeki tüm dosyalar; `.github`, `.claude`, `.editorconfig`, `.codacy.yaml`, `.coderabbit.yaml`, `.dockerignore`, `.gitignore`, `.env.example`, solution/MSBuild/NuGet yapılandırmaları, `README.md`, `CHANGELOG.md`, `CLAUDE.md`.
- `docs/` altındaki mevcut belgelerin tamamı; `docs/analysis/` altındaki bu çalışmadan önce oluşmuş inceleme raporları hariç.
- `infrastructure/` altında PostgreSQL klasörü dışındaki GeoIP, pgAdmin ve Prometheus dosyaları.
- Tüm Dockerfile, `appsettings.json`, `launchSettings.json`, Compose ve ilgili startup/health/telemetry kaynak kodu.
- `tests/` ağacının tamamı; proje yapılandırması, test piramidi, skip/flakiness, coverage ve kalite kapıları.
- Doküman-kod/ADR tutarlılığı için `Program.cs`, worker/orchestrator, health, exception, endpoint ve adapter kodları.

Çalıştırılan kontroller:

| Kontrol | Sonuç |
|---|---|
| `docker compose config --quiet` (yalnız dummy zorunlu parolalarla) | Başarılı |
| İzlenen JSON dosyalarını `jq empty` ile parse | Başarılı |
| CI/Codacy/CodeRabbit/Compose/Prometheus YAML parse | Başarılı |
| Markdown göreli link varlık kontrolü | Başarılı; gerçek Markdown linklerinde kırık yerel hedef yok |
| Yüksek güvenli secret kalıbı taraması | Gerçek token/private key bulmadı; yalnız belgelenmiş placeholder'lar bulundu |
| `dotnet list Saydin.Services.sln package --vulnerable --include-transitive --no-restore` | Başarılı; `Microsoft.OpenApi 2.0.0` için High advisory buldu |
| `dotnet nuget why ... Microsoft.OpenApi` | Zincir: `Microsoft.AspNetCore.OpenApi 10.0.8 → Microsoft.OpenApi 2.0.0` |
| İzole `saydin-review-20260818` Compose projesi; host portu yayınlamayan override | **380/380 geçti:** API unit 286, ingestion unit 86, gerçek PostgreSQL/Redis integration 8; **skip 0** |
| Fresh DB migration doğrulaması | **16 migration uygulandı** |
| Audit açık normal `docker compose build` | API restore `NU1903` / `GHSA-v5pm-xwqc-g5wc` nedeniyle **fail**; güvenlik sorunu doğrulandı |
| Yalnız davranış doğrulaması için `NuGetAudit=false` Release solution build | Başarılı; **0 warning / 0 error** |
| `NuGetAudit=false` ingestion image build | Başarılı |
| İzole Compose ad alanı kontrolü | Sabit `container_name` ve `5432/6379` host bind'ları nedeniyle başka Compose projesiyle çakıştı; `-p` tek başına izolasyon sağlamadı, port yayınlamayan/çakışmayı gideren override gerekti |

`NuGetAudit=false` yalnız test/build davranışını supply-chain engelinden ayırmak için kullanılmıştır; PLT-H01'in giderildiği veya audit'in production build'de kapatılması gerektiği anlamına gelmez. Güvenlik açısından kanonik sonuç audit açık build'in `NU1903` ile başarısız olmasıdır.

## 3. Bulgular

### High

#### PLT-H01 — Üretim bağımlılık grafiğinde bilinen High seviye zafiyet var

- **Severity:** High
- **Dosya/satır:** `Directory.Packages.props:24-28`; `src/Saydin.Api/Saydin.Api.csproj:25-26`
- **Kanıt:** 2026-08-18 tarihinde çalıştırılan NuGet denetimi `Saydin.Api`, `Saydin.Api.Tests` ve `Saydin.Api.IntegrationTests` için transitive `Microsoft.OpenApi 2.0.0` paketini **High**, advisory `GHSA-v5pm-xwqc-g5wc` olarak raporladı. `dotnet nuget why`, paketin `Microsoft.AspNetCore.OpenApi 10.0.8` üzerinden geldiğini doğruladı. Audit açık normal `docker compose build`, API restore'da `NU1903` ile fail oldu. Kök paket-metadata doğrulaması `Microsoft.AspNetCore.OpenApi 10.0.11`in de yalnız `Microsoft.OpenApi >= 2.0.0` alt sınırı verdiğini ve tek başına güvenli transitive sürümü garanti etmediğini; advisory'yi kapatan güvenli 2.x tabanın en az `2.7.5` olması gerektiğini gösterdi. `NuGetAudit=false` yalnız 380 davranış testini ve bağımsız build yüzeyini çalıştırabilmek için kullanıldı.
- **Etki:** OpenAPI üretim/yayınlama yüzeyi bilinen bir zafiyet taşıyor ve audit açık normal API image build'i şu anda bloklanıyor. `NuGetAudit=false` gibi bir bypass ile image üretmek ise bilinen zafiyeti production artefaktına taşır; dolayısıyla hem güvenlik hem teslimat sürekliliği etkilenir.
- **Öneri:** Yalnız `Microsoft.AspNetCore.OpenApi` patch'ine güvenme: `10.0.11` de `Microsoft.OpenApi >= 2.0.0` alt sınırını taşıyabildiğinden vulnerable `2.0.0` resolve edebilir. `Directory.Packages.props` içinde `Microsoft.OpenApi` için advisory'yi kapatan güvenli 2.x sürümü **explicit/central pin et (en az `2.7.5`)**; uyumlu üst paket patch'ini de yükselt. Ardından audit açık restore/build, `dotnet list package --vulnerable --include-transitive` ve API OpenAPI contract/smoke testleriyle resolved sürümü ve uyumluluğu doğrula; CI'da High/Critical bulguda fail eden zorunlu kapıyı koru.

#### PLT-H02 — CI entegrasyon testlerini fiilen çalıştırmadan yeşile dönebiliyor

- **Severity:** High
- **Dosya/satır:** `.github/workflows/ci.yml:54-61`; `tests/Saydin.Api.IntegrationTests/Saydin.Api.IntegrationTests.csproj:10-15`; `tests/Saydin.Api.IntegrationTests/ErrorContractHttpTests.cs:16-22`; `tests/Saydin.Api.IntegrationTests/Fixtures/DatabaseFixture.cs:28-35`; `tests/Saydin.Api.IntegrationTests/Fixtures/RedisFixture.cs:15-21`
- **Kanıt:** CI doğrudan `dotnet test` çalıştırıyor fakat service container veya test connection string tanımlamıyor. Entegrasyon projesindeki sekiz testin tamamı `SkippableFact`; env/altyapı yokluğu `Skip` oluyor. Proje yorumu da skip'in kırmızıya dönmediğini açıkça söylüyor. Buna karşı kök doğrulama, izole Compose projesinde gerçek PostgreSQL/Redis sağlandığında sekiz testin tamamının geçtiğini ve skip sayısının sıfır olduğunu kanıtladı; boşluk test kodunda değil, CI orchestration/gating katmanındadır.
- **Etki:** PostgreSQL constraint/EF mapping, Redis Lua atomikliği, gerçek middleware sırası, HTTP problem contract ve activity-log yazımı PR kapısında hiç doğrulanmayabilir. “Test geçti” sinyali yanıltıcıdır.
- **Öneri:** CI job'ına digest-pinned TimescaleDB ve Redis service container ekle; migration zincirini kur; benzersiz test DB'si üret; entegrasyon job'unda `Skip` sayısını sıfır zorunlu yap. Altyapısız yerel kolaylık gerekiyorsa skippable testleri ayrı kategori/job'a ayır, fakat required CI job'u fail-closed olsun.

#### PLT-H03 — Kabul edilmiş production Compose akışı varsayılan olarak Development çalışıyor

- **Severity:** High
- **Dosya/satır:** `docker-compose.yml:191-205`, `docker-compose.yml:222-243`; `.env.example:49-54`; `src/Saydin.Api/appsettings.json:14-18`; `src/Saydin.Api/Program.cs:408-412`; `docs/decisions/ADR-005-secrets-management.md:39-43`, `docs/decisions/ADR-005-secrets-management.md:63-68`
- **Kanıt:** Compose, `ASPNETCORE_ENVIRONMENT` verilmezse `Development`; örnek `.env` de `Development`. API rate limiter baseline'ı kapalı. Development ortamında OpenAPI ve Scalar map ediliyor. ADR-005 production MVP için aynı host `.env` + Compose modelini seçiyor, fakat production overlay/fail-fast guard yok.
- **Etki:** Operatör `.env` değerini değiştirmeyi unutursa production geliştirme modunda, API keşif yüzeyi açık ve burst koruması kapalı çalışır. Bu sessiz bir güvenlik/config drift'idir.
- **Öneri:** `compose.yaml`ı dev tabanı olarak adlandır; ayrı `compose.production.yaml`da `Production`, rate limit, proxy trust, auth ve secret şartlarını zorunlu kıl. Production entrypoint/config doğrulaması Development veya rate-limit-disabled durumda fail etsin. Örnek dosyayı `.env.development.example` olarak ayır.

#### PLT-H04 — API ve telemetry yüzeyi tüm host arayüzlerine açık

- **Severity:** High
- **Dosya/satır:** `docker-compose.yml:204-207`; `src/Saydin.Api/appsettings.json:14`; `src/Saydin.Api/Program.cs:326-329`, `src/Saydin.Api/Program.cs:414-421`; `docs/development-guide.md:341-342`
- **Kanıt:** `5080:8080` bind ifadesi Postgres/Redis/admin servislerinden farklı olarak `127.0.0.1` ile sınırlı değil. `AllowedHosts="*"`. `/metrics` production'da koşulsuz map ediliyor, rate limiter özellikle `/metrics`i hariç tutuyor ve authentication/authorization yok.
- **Etki:** Geliştirici LAN'ında veya doğrudan internete açık hostta API ile yüksek ayrıntılı runtime/business metrikleri dışarı açılır. Device-ID gerçek kimlik doğrulama değildir; saldırgan keyfi ID üretebilir.
- **Öneri:** Dev varsayılanını `127.0.0.1:${API_PORT:-5080}:8080` yap. Production'da API'yi yalnız reverse-proxy private network'üne aç; `/metrics`i ayrı management port/network'e taşı veya mTLS/auth/IP allowlist uygula. Dış sınırda TLS, WAF ve rate limit zorunlu olsun.

#### PLT-H05 — Backup/restore ve disaster recovery tasarımı yok

- **Severity:** High
- **Dosya/satır:** `docker-compose.yml:25-29`, `docker-compose.yml:98`, `docker-compose.yml:300-307`; `docs/architecture/activity-logging.md:728-745`; `docs/high-traffic-checklist.md:117-119`
- **Kanıt:** Kritik veri yalnız local named volume'larda. Activity-log cold export bile “planlanan/manual”; PostgreSQL base backup/WAL, Redis dump/AOF politikası, şifreli off-host kopya, RPO/RTO, restore adımları, restore testi ve sorumlu kişi yok.
- **Etki:** Host/disk/volume kaybı fiyat geçmişini, kullanıcı/scenario verisini ve audit/analytics kaydını geri döndürülemez biçimde silebilir. Named volume backup değildir.
- **Öneri:** Production öncesi RPO/RTO belirle; PostgreSQL için düzenli şifreli full backup + WAL/PITR ve off-host retention kur; Redis'in source-of-truth olmayan/olan verisini sınıflandır; secret/GeoIP/config yedeğini tanımla. En az aylık otomatik restore drill'i ve ölçülen restore süresini runbook'a ekle.

#### PLT-H06 — Build var, güvenli teslimat/promotion/rollback zinciri yok

- **Severity:** High
- **Dosya/satır:** `.github/workflows/ci.yml:120-173`; `docs/development-guide.md:371-379`
- **Kanıt:** Tek workflow CI. Docker action'ları image'ı yalnız local runner'a `load` ediyor; `push:false`. Registry publish, immutable release tag/digest, provenance/attestation, imza, environment approval, migration deployment, smoke test, rollback veya deployment status yok. Geliştirme kılavuzu “CI/CD” başlığında yalnız build/test/docker build anlatıyor.
- **Etki:** Hangi commit/digest'in production'a gittiği, nasıl onaylandığı ve nasıl geri alınacağı denetlenemez. Elle build/deploy, config ve migration skew riskini yükseltir.
- **Öneri:** CI ve CD'yi ayır. Main/tag sonrası tek kez image üretip registry'ye digest ile push et; SBOM/provenance üret, cosign ile imzala; staging smoke/integration sonrası production environment approval uygula; dedicated migration job ve digest-based rollback runbook'u ekle.

#### PLT-H07 — Metrik var fakat alarm, SLO ve olay müdahale kapısı yok

- **Severity:** High
- **Dosya/satır:** `infrastructure/prometheus/prometheus.yml:1-46`; `docs/high-traffic-checklist.md:78-92`; `docs/architecture/observability.md:514-519`
- **Kanıt:** Prometheus yalnız scrape config içeriyor; `rule_files`, Alertmanager ve recording/alert rules yok. P95, error rate ve Redis alarmı dokümanda production öncesi yapılacak iş olarak işaretsiz. Activity log drop, ingestion failure ve price-not-found sayaçları mevcut ama hiçbir eşik/route yok.
- **Etki:** Veri ingestion'ı veya activity-log yazımı günlerce bozuk kalabilir; kullanıcılar stale finansal veri alırken sistem teknik olarak “up” görünebilir. MTTA/MTTR ölçülemez.
- **Öneri:** SLI/SLO tanımla: availability, latency, error ratio, veri tazeliği, son başarılı ingestion, queue drop/write failure, DB/Redis saturation, disk ve backup yaşı. Prometheus rules + Alertmanager route/escalation, dashboard ve on-call runbook'u ekle; deploy readiness'i alarm testine bağla.

#### PLT-H08 — Tek worker fatal hata sonrası kalıcı durabilir; liveness bunu görmez

- **Severity:** High
- **Dosya/satır:** `src/Saydin.PriceIngestion/Workers/IngestionOrchestrator.cs:57-72`, `src/Saydin.PriceIngestion/Workers/IngestionOrchestrator.cs:76-91`; `src/Saydin.PriceIngestion/BackgroundServices/LivenessHeartbeatService.cs:39-50`; `docker-compose.yml:247-256`
- **Kanıt:** `RunSafelyAsync` fatal worker exception'ını loglayıp yutuyor ve o task bitiyor. Başka worker çalışıyorsa `Task.WhenAll` tamamlanmıyor, host durmuyor ve ölen worker restart edilmiyor. Heartbeat ise worker başarı/freshness'inden bağımsız her 30 saniye dosyaya dokunuyor; container healthy kalıyor.
- **Etki:** Örneğin yalnız CoinGecko/BIST kaynağı sessizce kalıcı durabilir, diğer kaynaklar ve heartbeat çalıştığı için operasyon sinyal alamaz. API stale/eksik fiyat sunar.
- **Öneri:** Her worker için supervisor/restart policy ve bounded exponential backoff uygula veya herhangi bir fatal worker hatasında host'u fail ettirip orchestrator'a restart ettir. `last_success_timestamp`, data-lag ve per-source failure streak metriği/health check ekle; readiness'i kaynak SLA'sına göre değerlendir.

### Medium

#### PLT-M01 — CI güvenlik/supply-chain kalite kapıları içermiyor

- **Severity:** Medium
- **Dosya/satır:** `.github/workflows/ci.yml:54-71`, `.github/workflows/ci.yml:153-171`
- **Kanıt:** Restore/build/test/coverage artifact ve Docker build var. SDK'nın varsayılan NuGet audit'i + warnings-as-errors birleşimi PLT-H01'i `NU1903` ile fiilen blokladı; bu olumlu bir örtük kapıdır. Ancak audit seviye/scope/config'i açıkça sabit değil ve dependency-review, CodeQL/SAST, secret scan, Dockerfile lint, image CVE scan, SBOM, license policy veya IaC scan adımı yok.
- **Etki:** NuGet tarafındaki davranış SDK/default değişimine bağlı; ayrıca sızmış sır, riskli lisans, first-party SAST bulgusu veya image OS CVE'si yeşil build alabilir.
- **Öneri:** NuGet audit mode/severity'yi repo config'inde açıkça sabitle ve audit'i production build'de kapatmayı yasakla. PR'da dependency-review + secret scan + CodeQL/SAST; build sonrası Trivy/Grype image scan; SBOM ve license policy ekle. High/Critical bulguları gerekçeli, süreli istisna dışında fail ettir.

#### PLT-M02 — Restore ve SDK yeterince yeniden üretilebilir değil

- **Severity:** Medium
- **Dosya/satır:** `Directory.Packages.props:1-13`; `.gitignore:9-17`; `global.json:1-5`; `docker-compose.yml:169-178`
- **Kanıt:** Central Package Management ve exact direct sürümler olumlu; ancak hiçbir `packages.lock.json` yok ve CI locked mode kullanmıyor. `global.json` `latestFeature`a roll-forward ediyor; test container'ı `sdk:10.0` mutable tag. Dependabot/Renovate config'i yok, yalnız CI yorumunda adı geçiyor.
- **Etki:** Aynı commit zaman içinde farklı transitive paket/SDK feature band ile restore olabilir; güvenlik güncellemesi sahipliği manuel ve gecikmeye açık.
- **Öneri:** `RestorePackagesWithLockFile=true`, commit edilmiş lock dosyaları ve CI'da `--locked-mode`; kabul edilen SDK roll-forward politikasını açıkça sınırla. Dependabot/Renovate ile NuGet, GitHub Actions ve Docker digest güncelleme PR'ları üret.

#### PLT-M03 — Compose runtime image referansları mutable tag

- **Severity:** Medium
- **Dosya/satır:** `docker-compose.yml:7-9`, `docker-compose.yml:40-42`, `docker-compose.yml:81-83`, `docker-compose.yml:130-149`, `docker-compose.yml:264-285`
- **Kanıt:** Dockerfile build/runtime tabanları digest ile iyi şekilde sabitlenmiş; buna karşı Compose'taki TimescaleDB, pgAdmin, Redis, Aspire, Prometheus ve exporter'lar tag ile sabit. Patch tag bile registry'de yeniden işaretlenebilir; `aspire-dashboard:9.0` patch seviyesinde de açık.
- **Etki:** Aynı commit yeni pull'da farklı binary çalıştırabilir; rollback ve incident forensics zorlaşır.
- **Öneri:** Production overlay'de `image:tag@sha256:digest` kullan; otomatik digest bump PR'ı ve release notu üret. Multi-arch manifest digest'ini hedef platformlarda doğrula.

#### PLT-M04 — Container/host hardening, kaynak sınırı ve ölçeklenebilirlik eksik

- **Severity:** Medium
- **Dosya/satır:** `src/Saydin.Api/Dockerfile:24-47`; `src/Saydin.PriceIngestion/Dockerfile:19-38`; `docker-compose.yml:7-298`; `docs/high-traffic-checklist.md:12-27`, `docs/high-traffic-checklist.md:67-70`
- **Kanıt:** Uygulama image'ları non-root; fakat Compose'ta `read_only`, `tmpfs`, `cap_drop`, `security_opt:no-new-privileges`, pids/memory/cpu limit/reservation ve network segmentasyonu yok. Redis için `maxmemory`/eviction yok. Sabit `container_name` tanımları Compose replica scaling'i engeller. Kök doğrulamada farklı `-p saydin-review-20260818` proje adı kullanılmasına rağmen sabit container adları ve `127.0.0.1:5432/6379` host bind'ları mevcut başka Compose projesiyle somut olarak çakıştı; host portu yayınlamayan/çakışmayı gideren override olmadan izole test ortamı başlatılamadı.
- **Etki:** Container breakout etkisi ve noisy-neighbor/OOM riski büyür; Redis host belleğini tüketebilir; yatay ölçek dokümanı gerçek Compose ile uygulanamaz. Ayrıca Compose project-name izolasyonu çalışmaz; paralel CI, başka repo ve geliştirici stack'leri isim/port çakışmasıyla birbirini bloke eder.
- **Öneri:** Uygulamalarda read-only rootfs + yalnız gerekli tmpfs, tüm capability drop, no-new-privileges ve resource limit ekle. DB/cache/management/app network'lerini ayır. Redis bellek/eviction politikasını workload'a göre belirle. Production servislerinden `container_name` kaldır.

#### PLT-M05 — Liveness ile readiness tek endpoint'te karışmış

- **Severity:** Medium
- **Dosya/satır:** `src/Saydin.Api/Program.cs:159-179`, `src/Saydin.Api/Program.cs:414-415`; `docker-compose.yml:213-219`; `docs/architecture/observability.md:439-470`
- **Kanıt:** Tek `/health` PostgreSQL ve Redis'i birlikte kontrol ediyor. Kod Redis down olduğunda cache-aside ile API'nin çalışmasını amaçlıyor, fakat aynı aggregate endpoint container probe'u. Liveness/readiness tag filtreleriyle ayrılmıyor.
- **Etki:** Bu endpoint Kubernetes/ECS liveness olarak yeniden kullanılırsa geçici Redis/DB kesintisi sağlıklı process restart fırtınasına dönüşebilir; yalnız process liveness de ayrı ölçülemez.
- **Öneri:** `/health/live` yalnız process; `/health/ready` zorunlu dependency/freshness; opsiyonel cache için `Degraded` politikası tanımla. Compose/Kubernetes probe'larını doğru endpoint'e bağla ve JSON health response üret.

#### PLT-M06 — 30 saniyelik audit-log drain için stop grace garantisi yok

- **Severity:** Medium
- **Dosya/satır:** `src/Saydin.Api/BackgroundServices/ActivityLogWriter.cs:14-16`, `src/Saydin.Api/BackgroundServices/ActivityLogWriter.cs:58-89`; `docker-compose.yml:191-220`
- **Kanıt:** Writer shutdown drain için 30 saniye ayırıyor; API service'te buna uyumlu `stop_grace_period` tanımlı değil.
- **Etki:** Orchestrator process'i writer'ın drain süresi bitmeden zorla sonlandırırsa kuyruğun kalan audit/activity kayıtları kaybolur; kayıp metriği de flush olamayabilir.
- **Öneri:** `stop_grace_period`i drain + telemetry flush için yeterli değere getir; SIGTERM altında dolu kuyruklu shutdown testi ekle; kabul edilen kayıp bütçesini belgele.

#### PLT-M07 — Telemetry kimliği ve saklama modeli production forensics için güvenilir değil

- **Severity:** Medium
- **Dosya/satır:** `infrastructure/prometheus/prometheus.yml:1-6`; `docker-compose.yml:130-162`, `docker-compose.yml:300-305`; `src/Saydin.Api/Program.cs:59-80`; `src/Saydin.PriceIngestion/Program.cs:38-54`
- **Kanıt:** Prometheus external label daima `env: development`. Aspire Dashboard için volume/retention yok. Her iki servisin `service.version`ı kaynakta `1.0.0` hardcode; commit/image digest/deployment id resource attribute'u yok.
- **Etki:** Production metriği development diye etiketlenebilir; dashboard restart'ında log/trace kaybolur; iki farklı release telemetry'de ayırt edilemez.
- **Öneri:** Environment, semantic version, git SHA ve image digest'i build/deploy metadata'sından enjekte et. Production telemetry'yi kalıcı, erişim kontrollü collector/backend'e gönder; retention, PII redaction ve maliyet politikası tanımla.

#### PLT-M08 — Production secret modeli plaintext `.env` ve bilinen placeholder'ı kabul ediyor

- **Severity:** Medium
- **Dosya/satır:** `.env.example:7-28`; `docker-compose.yml:11-19`, `docker-compose.yml:44-49`, `docker-compose.yml:85-93`; `docs/decisions/ADR-005-secrets-management.md:39-50`, `docs/decisions/ADR-005-secrets-management.md:63-78`
- **Kanıt:** Compose yalnız değişkenin boş olmamasını kontrol ediyor; `change_me_in_production` geçerli kabul edilir. ADR production MVP için host `.env` seçiyor ve precautionary rotation hâlâ pending.
- **Etki:** Örnek dosyayı değiştirmeden deploy etmek bilinen DB/Redis/pgAdmin parolaları üretir. Plaintext dosyanın backup, process environment veya yanlış izinlerle sızma riski var.
- **Öneri:** Production startup'ta placeholder/uzunluk/entropy kontrolüyle fail et; `.env` yerine en az Compose secrets/file mount'a geç; dosya izin ve sahipliğini doğrula. Rotation'ı tamamla, tarih/sorumlu/kanıt kaydı tut.

#### PLT-M09 — PostgreSQL exporter varsayılan olarak CRUD yetkili uygulama hesabına düşüyor

- **Severity:** Medium
- **Dosya/satır:** `.env.example:12-20`; `docker-compose.yml:259-269`
- **Kanıt:** Exporter user/password boş bırakılırsa DSN doğrudan ana `POSTGRES_USER/POSTGRES_PASSWORD`a fallback ediyor. Örnek `.env` exporter alanlarını boş bırakıyor.
- **Etki:** Salt metrik okuyucusu gereksiz yazma/uygulama yetkileri alıyor; exporter compromise DB bütünlüğüne sıçrayabilir.
- **Öneri:** Production'da fallback'i kaldır; exporter credential çiftini zorunlu ve yalnız `pg_monitor`/gereken view yetkileriyle sınırla. Yarı-yapılandırmayı startup validation ile reddet.

#### PLT-M10 — Yönetim UI'ları production Compose'ta varsayılan olarak birlikte dağıtılıyor

- **Severity:** Medium
- **Dosya/satır:** `docker-compose.yml:40-75`, `docker-compose.yml:109-143`; `infrastructure/pgadmin/servers.json:1-12`
- **Kanıt:** pgAdmin, Redis Insight ve unsecured-default Aspire ayrı profile arkasında değil; `docker compose up` hepsini açıyor. pgAdmin `SERVER_MODE=False`, master password kapalı. Loopback bind riski azaltıyor ama production topology aynı dosyayı kullanıyor.
- **Etki:** Gereksiz image/servis ve yönetim yeteneği attack surface'i artırır; yanlış proxy/port publish değişikliği yönetim konsolunu açabilir.
- **Öneri:** `devtools` profile'a taşı; production overlay'den tamamen çıkar veya SSO/VPN/mTLS ve ayrı management network uygula.

#### PLT-M11 — Coverage yalnız raporlanıyor; kalite kapısı değil ve ortalama yanıltıcı

- **Severity:** Medium
- **Dosya/satır:** `.github/workflows/ci.yml:60-71`, `.github/workflows/ci.yml:73-118`
- **Kanıt:** Cobertura upload ediliyor ama minimum line/branch threshold yok. Coverage dosyası yoksa step `exit 0`. “Average” proje yüzdelerinin basit aritmetik ortalaması; satır sayısına göre ağırlıklı değil. Integration projesi skip olsa bile rapor yeşil olabilir.
- **Etki:** Coverage sert biçimde düşebilir veya kritik assembly hiç ölçülmeden CI geçebilir; summary gerçekte kapsanan kod oranını temsil etmez.
- **Öneri:** Raporları merge edip assembly/path bazlı threshold uygula; genel ve changed-lines eşikleri belirle. Coverage dosyası yoksa fail et; integration coverage'i ayrı göster; generated code exclusion'larını açıkça sabitle.

#### PLT-M12 — Test piramidinde kritik platform ve I/O boşlukları var

- **Severity:** Medium
- **Dosya/satır:** `tests/Saydin.PriceIngestion.Tests/Saydin.PriceIngestion.Tests.csproj:10-25`; `tests/Saydin.Api.IntegrationTests/Saydin.Api.IntegrationTests.csproj:10-24`; `.coderabbit.yaml:81-87`
- **Kanıt:** Test ağacında `OpenExchangeRatesAdapter/Mapper`, `IngestionOrchestrator`, `LivenessHeartbeatService`, `HttpResilienceExtensions`, `ActivityLogWriter`, `RedisCacheHelper`, API endpoint happy-path/route contract'ları, `PriceRepository`, `SavedScenarioRepository`, ingestion repository'leri ve diğer dört worker için doğrudan test yok. Fresh schema/Compose/config/container smoke, backup restore, security ve load testi yok. Mevcut testler çoğunlukla service/mapper unit testleri.
- **Etki:** Worker supervision, schedule, resilience, cache serialization, repository SQL/EF, endpoint wiring ve operability regresyonları derleme geçerken kaçabilir.
- **Öneri:** Risk tabanlı test matrisi oluştur. Önce H08 worker supervisor, OXR adapter/mapper, cache helper, repositories ve tüm route happy/error contract'larını ekle. Digest-pinned disposable infra ile fresh migration + smoke; Compose health; graceful shutdown; restore; concurrency ve temel load testlerini ayrı required job'lara koy.

#### PLT-M13 — Entegrasyon testleri yanlışlıkla herhangi bir gerçek DB'yi değiştirebilir

- **Severity:** Medium
- **Dosya/satır:** `tests/Saydin.Api.IntegrationTests/Fixtures/DatabaseFixture.cs:28-58`; `tests/Saydin.Api.IntegrationTests/InflationRepositoryIntegrationTests.cs:24-59`; `tests/Saydin.Api.IntegrationTests/ErrorContractHttpTests.cs:94-98`, `tests/Saydin.Api.IntegrationTests/ErrorContractHttpTests.cs:248-275`
- **Kanıt:** Connection string doğrudan env'den kabul ediliyor; DB adı/host için `test` allowlist'i veya ephemeral DB oluşturma yok. Testler 2099 tarihli satır ekleyip siliyor ve paylaşılan `activity_logs` tablosunu poll edip temizliyor.
- **Etki:** Yanlış env ile production/staging DB üzerinde veri mutasyonu ve cleanup yapılabilir; paralel testler paylaşılan state/flakiness üretebilir.
- **Öneri:** Fixture yalnız `*_test_<guid>` adı kabul etsin veya container başına disposable DB yaratsın; production host/name guard ekle. Her test transaction/schema/database izolasyonu kullansın ve teardown başarısızlığını görünür kılsın.

#### PLT-M14 — Kök README hızlı başlangıcı fresh checkout'ta çalışmıyor

- **Severity:** Medium
- **Dosya/satır:** `README.md:13-29`, `README.md:43-63`; `docker-compose.yml:14-15`, `docker-compose.yml:46-71`; `src/Saydin.Api/Endpoints/AssetsEndpoints.cs:41-59`
- **Kanıt:** README doğrudan `docker compose build && up` diyor; `.env` kopyalama/şifre değiştirme ve zorunlu `infrastructure/pgadmin/pgpassfile` oluşturma adımı yok. Compose required variable ve `create_host_path:false` nedeniyle fail eder. README'deki `GET /v1/assets` örneğinde zorunlu `X-Device-ID` yok ve 400 döner.
- **Etki:** İlk kurulum başarısız; geliştirici hatayı kod/Compose problemi sanabilir. Yayınlanan API örneği sözleşmeye aykırı.
- **Öneri:** Tek kanonik bootstrap script/runbook tanımla: `.env`, güçlü dev parolaları, pgpass, `docker compose config`, up ve health doğrulaması. Tüm korumalı curl örneklerine header ekle ve CI'da docs smoke testi çalıştır.

#### PLT-M15 — Geliştirme kılavuzundaki yürütülebilir talimatlar config ile çelişiyor

- **Severity:** Medium
- **Dosya/satır:** `docs/development-guide.md:13-43`, `docs/development-guide.md:127-174`, `docs/development-guide.md:176-195`, `docs/development-guide.md:257-270`; `.env.example:22-35`; `src/Saydin.PriceIngestion/Adapters/EvdsInflationAdapter.cs:11-36`
- **Kanıt:** Kılavuz pgAdmin parolasını `admin` diyor, örnek `change_me_in_production`; Redis Insight'a “şifre yok” diyor, Redis `requirepass`; Docker run Redis string'i ve `redis-cli` komutu parolasız; EVDS “key gerektirmez” ve default enabled deniyor, adaptör key yoksa boş dönüyor. `/health | jq` düz metin `Healthy` yanıtını JSON sanıyor. Asset GET örneklerinde Device-ID yok. Ayrıca pgpass hazırlanmıyor.
- **Etki:** Onboarding, smoke test ve ingestion doğrulaması sistematik biçimde yanlış sonuç verir; EVDS açık görünüp hiç veri çekmeyebilir.
- **Öneri:** Kılavuzu gerçek `.env`/Compose/adaptör davranışından üretilen ve CI'da smoke edilen komutlarla güncelle. EVDS key gereksinimini düzelt; health JSON formatter ekle veya `jq`yi kaldır; password-aware örnekler kullan.

#### PLT-M16 — Normatif observability belgeleri gerçek middleware ve hata güvenliğiyle çelişiyor

- **Severity:** Medium
- **Dosya/satır:** `docs/architecture/activity-logging.md:541-565`; `docs/architecture/observability.md:294-306`, `docs/architecture/observability.md:474-488`; `src/Saydin.Api/Program.cs:390-406`; `src/Saydin.Api/Exceptions/ExternalApiExceptionHandler.cs:38-54`
- **Kanıt:** Activity logging belgesindeki “gerçek” snippet `ExceptionHandler → ActivityLog` sırası gösteriyor; gerçek kod ve aynı belgenin başka bölümleri `ActivityLog → ExceptionHandler`. Observability tablosu `ExternalApiException` response'unda `source` extension'ı var diyor; kod EC-9 nedeniyle özellikle koymuyor. Writer için UPSERT deniyor, kod `AddRange/SaveChanges` insert.
- **Etki:** Bakım yapan kişi eski sıralamayı geri getirerek hatalı 200 activity status regresyonunu veya upstream kimliği sızıntısını yeniden yaratabilir.
- **Öneri:** Normatif örnekleri derlenen snippet/test veya kaynak linkinden üret; middleware sıra testini required integration job'a bağla. Stale response contract ve “UPSERT” ifadesini kaldır.

#### PLT-M17 — `CLAUDE.md` ve Claude komutları güncel mimariyi yanlış öğretiyor

- **Severity:** Medium
- **Dosya/satır:** `CLAUDE.md:69-80`, `CLAUDE.md:217-253`, `CLAUDE.md:355-367`, `CLAUDE.md:390-403`; `.claude/commands/add-asset.md:36-61`, `.claude/commands/add-asset.md:90-127`; `.claude/commands/check-architecture.md:71-78`
- **Kanıt:** `CLAUDE.md` aktif migration olarak `dotnet ef` komutlarını öğretirken ADR-001 ve DB belgesi numaralı SQL'i kanonik kabul ediyor. Exception örneği hardcoded title, `ex.Message`, eksik stable `code` ve yanlış content-type kalıbı içeriyor. “8 adım” dediği komut 9 adım. Add-asset, var olmayan `I{Source}Adapter`, her asset'i orchestrator'a ekleme, kullanılmayan market-holidays ve var olmayan manual trigger/“Desteklenen Asset'ler” tablosunu istiyor. Architecture check beklenen handler sırası gerçek Program'dan farklı.
- **Etki:** Agent/geliştirici doğru kodu yanlış kurala göre bozabilir veya hayali dosya/endpoint üretir; otomasyon false positive/negative verir.
- **Öneri:** Agent kurallarını ADR/source-of-truth'a indirge; geçersiz örnekleri derlenen test fixture'larından üret. Add-asset akışını source bazlı worker + DB seed modeline göre yeniden yaz; architecture check'i `rg`/Roslyn architecture testleriyle otomatikleştir.

#### PLT-M18 — Konfigüre edilebilir ingestion heartbeat yolu probe'larla birlikte değişmiyor

- **Severity:** Medium
- **Dosya/satır:** `src/Saydin.PriceIngestion/BackgroundServices/LivenessHeartbeatService.cs:11-18`, `src/Saydin.PriceIngestion/BackgroundServices/LivenessHeartbeatService.cs:32-36`; `src/Saydin.PriceIngestion/Dockerfile:32-38`; `docker-compose.yml:247-256`
- **Kanıt:** Uygulama `LivenessProbe:HeartbeatPath` ile dosya yolunu override edebiliyor; Dockerfile ve Compose probe'ları `/tmp/saydin-ingestion-healthy` hardcode. Kod yorumu üçünün aynı olması gerektiğini söylüyor ama tek config kaynağı yok.
- **Etki:** Operatör güvenlik amacıyla yolu değiştirirse uygulama sağlıklı çalışırken container sürekli unhealthy olur.
- **Öneri:** Tek environment variable'ı uygulama ve shell probe'da kullan; image içinde güvenli, önceden sahipliği verilmiş sabit dizin tercih et. Non-default path için container smoke testi ekle.

#### PLT-M19 — Mimari/metric belgelerinde stale operasyon gerçekleri var

- **Severity:** Medium
- **Dosya/satır:** `docs/architecture.md:49-74`; `docs/architecture/observability.md:241-257`; `src/Saydin.PriceIngestion/appsettings.json:20-25`; `src/Saydin.PriceIngestion/Workers/EvdsInflationWorker.cs:15-18`; `src/Saydin.Shared/Diagnostics/SaydinMetrics.cs:36-50`
- **Kanıt:** Mimari belge CoinGecko'yu 06:00 UTC ve TwelveData'yı 19:00 Türkiye gösteriyor; config 02:00 UTC ve 15:00 UTC. Aynı belge EVDS'nin `ingestion_jobs` yazmadığını söylüyor, worker artık yazıyor. Observability/metrics yorumları eski outcome kümelerini ve “worker job yazmıyor” gerekçesini taşıyor.
- **Etki:** Operatör yanlış çalışma penceresinde alarm/incident araştırır; job tablosunu kullanmayıp yalnız metriğe güvenir; dashboard tag sorguları veri üretmez.
- **Öneri:** Schedule ve metric outcome sözleşmelerini kod/config'ten üretilen tabloya dönüştür. Docs drift testiyle worker config key/saat, metric adı ve izinli tag değerlerini karşılaştır.

### Low

#### PLT-L01 — PR template doğrulama komutu ve doküman hedefi hatalı

- **Severity:** Low
- **Dosya/satır:** `.github/pull_request_template.md:14-28`; `CLAUDE.md:24-31`; `docs/README.md:42-51`
- **Kanıt:** Template `docker compose run --rm saydin-api dotnet test` istiyor; runtime image SDK/test içermiyor ve `CLAUDE.md` bunu açıkça yasaklıyor. `api-contract.md` hedefinin meta repo'da olduğu template'te belirtilmiyor.
- **Etki:** PR kontrol listesi uygulanamaz; checkbox sahte güven üretir.
- **Öneri:** Kanonik `docker compose run --rm tests` komutunu kullan; meta-repo değişikliği gerekiyorsa link/ayrı PR referansı alanı ekle.

#### PLT-L02 — CODEOWNERS tek kişiye bağlı

- **Severity:** Low
- **Dosya/satır:** `.github/CODEOWNERS:1-2`
- **Kanıt:** Tüm repo tek owner `@cemililik`.
- **Etki:** Bus factor 1; owner'ın kendi değişikliğinde bağımsız review ve izin ayrılığı sağlanamayabilir.
- **Öneri:** En az iki owner/team, infra/security için ayrı sahiplik ve branch protection required review/last-push approval uygula.

#### PLT-L03 — Kod stili kalite kapısı değil

- **Severity:** Low
- **Dosya/satır:** `.editorconfig:7-13`, `.editorconfig:34-70`; `Directory.Build.props:1-9`
- **Kanıt:** Naming kuralları bilinçli olarak `suggestion`; `EnforceCodeStyleInBuild` yok. `TreatWarningsAsErrors` olumlu fakat repo-özel Roslyn/architecture analyzers veya formatter check yok.
- **Etki:** Mimari/naming drift IDE'ye göre değişir ve CI'da yakalanmaz.
- **Öneri:** Borç baseline'ı sonrası önemli kuralları warning/error yap; `dotnet format --verify-no-changes` ve NetArchTest/Roslyn architecture testleri ekle.

#### PLT-L04 — Güvenlik bildirimi ve katkı/release yönetişimi belgeleri eksik

- **Severity:** Low
- **Dosya/satır:** `README.md:1-107`; `.github/pull_request_template.md:1-28`
- **Kanıt:** Kök envanterde `SECURITY.md`, `CONTRIBUTING.md`, `LICENSE`, support/escalation ve release policy yok.
- **Etki:** Zafiyetlerin özel bildirim kanalı, desteklenen sürümler, katkı ve lisans beklentileri belirsiz.
- **Öneri:** Private vulnerability reporting adresi/SLA'sı, desteklenen sürümler, katkı akışı, DCO/CLA kararı ve lisansı ekle.

#### PLT-L05 — Tarihsel review referansları repo içinde denetlenebilir değil

- **Severity:** Low
- **Dosya/satır:** `.gitignore:62-63`; `CHANGELOG.md:9-13`, `CHANGELOG.md:80-84`; `docs/decisions/ADR-001-migration-strategy.md:216-220`; `docs/decisions/ADR-003-rate-limiting.md:88-94`
- **Kanıt:** Birçok ADR/CHANGELOG `docs/code-reviews/ACTION-PLAN.md` ve benzeri kanıtlara atıf yapıyor; klasör gitignored ve dosyalar depoda yok. Bunlar Markdown linki değil, bu yüzden link checker da yakalamıyor.
- **Etki:** Karar izi ve “bulgu kapandı” kanıtı yeni ekip üyesi/auditor için doğrulanamaz.
- **Öneri:** Hassas olmayan nihai action-plan/decision özetlerini tracked `docs/analysis` veya issue/PR kalıcı linklerine taşı; geçici local artifact ile kalıcı kanıtı ayır.

## 4. Önceliklendirilmiş aksiyon planı

### P0 — Production/release bloklayan

1. `Microsoft.OpenApi` High advisory'sini güvenli 2.x central pin ile kapat; mevcut örtük NuGet audit engelini açık, sabit ve zorunlu bir vulnerability gate'e dönüştür.
2. Entegrasyon testlerini gerçek, disposable TimescaleDB/Redis ile required CI job'ında skip=0 çalıştır.
3. Production Compose/manifesti ayır: `Production`, private bind/network, metrics koruması, rate limit, proxy trust ve secret validation.
4. PostgreSQL backup + PITR + off-host retention + restore drill ve RPO/RTO runbook'u hazırla.
5. Worker per-source freshness/supervision ve production alarm/SLO katmanını kur.

### P1 — Güvenli teslimat ve kalite

1. Immutable registry artifact, SBOM/provenance/imza, staging promotion, migration job ve rollback zinciri.
2. Dependency lock/update automation, image digest pin ve security/image scan kapıları.
3. Liveness/readiness ayrımı, graceful shutdown testi ve container hardening/resource sınırları.
4. Coverage threshold + risk bazlı eksik testler + test DB güvenlik guard'ı.

### P2 — Dokümantasyon ve geliştirici deneyimi

1. README/development guide komutlarını fresh-checkout smoke testiyle düzelt.
2. `CLAUDE.md`, `.claude/commands`, observability/activity logging belgelerini kanonik koda hizala.
3. Schedule/metric tablolarını code/config'ten üret; docs drift CI testi ekle.
4. PR template, security/contribution/release yönetişimi ve kalıcı review izlerini tamamla.

## 5. İyi uygulamalar

- `.github/workflows/ci.yml:10-19`, `35-40`, `128-137`: least-privilege `contents:read`, concurrency cancellation, checkout credential persistence kapalı ve third-party action'lar full SHA pinned.
- `src/Saydin.Api/Dockerfile:5-6`, `24-37` ve PriceIngestion karşılığı: base image digest pinned, multi-stage build, `--no-install-recommends`, apt list temizliği ve non-root runtime user.
- `docker-compose.yml:14-24`, `46-53`, `85-107`: zorunlu parola değişkenleri; Postgres/Redis/admin UI'larında loopback bind; Redis auth-aware health check.
- `.dockerignore:15-20`, `.gitignore:41-60`: local env, credential, certificate, GeoIP DB ve gerçek pgpass image/git dışında.
- `Directory.Packages.props:1-13`, `Directory.Build.props:1-9`: central package versioning/transitive pinning, nullable ve warnings-as-errors.
- `.github/workflows/ci.yml:63-71`: coverage artifact'ı kaybolmasın diye `always()` upload ve retention.
- `Program.cs` ve `docs/architecture/observability.md`: structured JSON logging, trace/metric resource ayrımı, custom metric'ler ve dependency health check temeli.
- Testlerde `FakeTimeProvider`, HTTP stub handler, gerçek Redis Lua testi ve veri cleanup desenleri kullanılmış; service/calculator test yoğunluğu yüksek. İzole Compose doğrulamasında **380/380 test geçti**, sekiz gerçek PostgreSQL/Redis entegrasyon testi çalıştı ve skip olmadı.
- Fresh DB doğrulamasında 16 migration uygulandı; audit'ten bağımsız Release solution build 0 warning/0 error ve ingestion image build başarılı oldu.
- ADR klasörü karar/durum/risk ayrımını iyi kuruyor. Çalıştırılan link kontrolünde tüm gerçek göreli Markdown linkleri mevcut hedefe çözüldü.
- Yüksek güvenli secret regex taramasında izlenen dosyalarda gerçek token/private key bulunmadı.

## 6. Residual riskler ve inceleme sınırları

- Kullanıcı talebi gereği `infrastructure/postgres/` dosyalarının içerik incelemesi bu raporun platform kapsamı dışındadır. Bununla birlikte kök doğrulama fresh DB üzerinde 16 migration'ın uygulanabildiğini kanıtladı; bu sonuç tek tek migration semantiği, upgrade/rollback ve production veri dönüşümü incelemesinin yerine geçmez.
- GitHub branch protection, repository secret scanning, environment approval, CodeRabbit/Codacy SaaS ayarları ve organization policy repo içinden doğrulanamaz.
- Production host, reverse proxy, firewall, registry, secret store, gerçek backup sistemi, monitoring backend ve deployment geçmişi erişilebilir değildi. Repo dışı kontroller varsa bulgular kanıt sunulana kadar açık kabul edilmelidir.
- Meta repo'daki API contract, privacy policy ve ürün ADR dosyaları bu çalışma alanında yoktu; çapraz-repo doğruluk tam doğrulanamadı.
- İzole doğrulamada 380/380 test geçti ve skip yoktu; ancak coverage raporlarının birleşik line/branch oranı ile changed-lines eşiği bu revizyonda ayrıca ölçülmedi. PLT-M11 coverage kalite kapısı bulgusu bu nedenle açık kalır.
- `NuGetAudit=false` ile alınan test/build başarıları yalnız davranış doğrulamasıdır. Audit açık normal API image build'i `NU1903` ile fail olduğundan supply-chain riski kapanmamıştır ve audit'i kapatmak kabul edilebilir remediation değildir.
- Paket vulnerability sonucu 2026-08-18 NuGet advisory durumunun anlık görünümüdür; remediation sonrası yeniden taranmalıdır.

## 7. Nihai değerlendirme

Kod tabanında sağlam uygulama-seviyesi disiplin ve önceki review'lerden gelen çok sayıda iyi sertleştirme var. İzole 380/380 test, skip=0 entegrasyon, fresh 16-migration zinciri ve temiz Release build uygulama davranışının güçlü bir baseline'a sahip olduğunu gösteriyor. Ancak platformun güvenli üretim işletimi henüz “CI build + tek-host dev Compose” seviyesini aşmamış durumda: audit açık API image build bilinen High advisory nedeniyle fail ediyor; repository CI gerçek entegrasyon altyapısını sağlamıyor; güvenli teslimat, DR ve alarm katmanları eksik. En büyük risk, izole doğrulamanın mevcut CI/CD ve production-operability kanıtı sanılmasıdır. P0 maddeleri kapanıp kanıtlanana kadar production readiness statüsü **hazır değil** olmalıdır.
