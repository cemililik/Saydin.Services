# Hat Bazlı Review Kaydı

> 19 hattın her biri için kapsam beyanı, bulgu dağılımı, **reddedilen** iddialar ve güçlü kararlar.
> Reddedilen iddialar bilinçli olarak kayıtta tutulur — bir iddianın neden yanlış olduğunu bilmek
> iddianın kendisi kadar değerlidir.

## Dağılım

| Hat | Kapsam | Crit | High | Med | Low | Reddedilen | Doğrulama |
|---|---|---:|---:|---:|---:|---:|---|
| R01 | API güvenlik ve admission yüzeyi |  | 2 | 6 | 9 |  | bağımsız doğrulayıcı |
| R02 | Installation kimlik yaşam döngüsü + migration 023/024 |  | 2 | 3 | 6 | 1 | bağımsız doğrulayıcı |
| R03 | Activity logging ve pseudonymization |  | 1 | 8 | 8 |  | bağımsız doğrulayıcı |
| R04 | API servis/repository katmanı + Shared |  |  | 4 | 8 | 1 | bağımsız doğrulayıcı |
| R05 | PriceIngestion worker/repository |  | 2 | 11 | 4 |  | bağımsız doğrulayıcı |
| R06 | PriceIngestion adapter/mapper |  |  | 8 | 7 |  | bağımsız doğrulayıcı |
| R07 | Migrator + RoleBootstrap + DatabaseSecurity |  | 1 | 2 | 8 | 2 | bağımsız doğrulayıcı |
| R08 | DataQualityAudit + DataRepair |  |  | 6 | 10 | 2 | bağımsız doğrulayıcı |
| R09 | calendar-data ve calendar infrastructure |  | 2 | 5 | 6 |  | ⚠ oturum limiti — doğrulanmadı |
| R10 | infrastructure/backup |  | 1 | 9 | 6 |  | ⚠ oturum limiti — doğrulanmadı |
| R11 | Deployment, Prometheus, Alertmanager, OTEL | 1 |  | 8 | 6 | 1 | bağımsız doğrulayıcı |
| R12 | Release supply chain, CI workflow, kapılar | 1 |  | 6 | 8 | 2 | bağımsız doğrulayıcı |
| R13 | Saydin.Api test kalitesi |  |  | 8 | 9 | 2 | bağımsız doğrulayıcı |
| R15 | Dokümantasyon, ADR, runbook |  | 2 | 5 | 10 |  | bağımsız doğrulayıcı |
| R16 | Compose, solution, build konfigürasyonu |  | 1 | 4 | 5 |  | ⚠ oturum limiti — doğrulanmadı |
| R17 | REMEDIATION DENETİMİ |  | 1 | 6 | 4 |  | bağımsız doğrulayıcı |
| R18 | ÜRÜN VE GELİŞTİRİCİ DENEYİMİ |  | 1 | 11 | 10 |  | ⚠ oturum limiti — doğrulanmadı |
| R14a | PriceIngestion + calendar test kalitesi |  | 1 | 8 | 8 |  | bağımsız doğrulayıcı |
| R14b | Migrator/RoleBootstrap/DQA/DataRepair test kalitesi |  | 1 | 3 | 12 |  | bağımsız doğrulayıcı |

---

## R01 — API güvenlik ve admission yüzeyi

**Bulgu:** 0 Critical · 2 High · 6 Medium · 9 Low

**Kapsam.** R01 dosya listesindeki 10 dosyanın tamamı okundu (Security/*, Middleware/ApiPortBoundaryMiddleware.cs, Middleware/ActivityLogMiddleware.cs, Runtime/ApiEndpointSurface.cs, Endpoints/EndpointExtensions.cs) — yeni dosyalar tam, değişenler `git diff` ile. Karşı taraf olarak Program.cs pipeline sırası, ApiRuntimeContract, ErrorMessages.resx/.en.resx, ApiErrorCodes, ActivityLogBuilder, SecureSecretFile, InstallationEndpoints, infrastructure/deployment/Caddyfile + compose.production.yml, infrastructure/prometheus/rules/*, docs/decisions/ADR-003, docs/architecture.md, ilgili runbook'lar ve `tests/Saydin.Api.Tests/{Middleware,Security}` + `Saydin.Api.IntegrationTests/DistributedSecurityLimiterIntegrationTests.cs` okundu. Okunmayan: Redis multiplexer/ConnectionString kurulumu detayı, installation credential keyring iç mantığı (başka hatta ait), Caddy imajının tam sürümü (digest ile pinlenmiş).

**Güçlü kararlar.**

- Port sınırı atlatması gerçekten kök nedende kapatılmış ve üç bağımsız katmana yayılmış: `NormalizePath` (boş segment eleme + `OrdinalIgnoreCase`), yol yazımından tamamen bağımsız çalışan yeni `ApiPortEndpointSelectorPolicy` (routing seçim aşamasında aday eleme) ve Caddyfile'daki `^/+(?:(?:metrics|health/+ready)(?:/)*|...)$` regex'i. Semptom örtme değil, üç farklı mekanizmayla gerçek savunma derinliği.
- Endpoint yüzeyi elle tutulan bir yol listesi değil, `MapGroup("").WithMetadata(...)` ile mapping noktasında türetiliyor — yeni product endpoint'i eklemek ek bir kayıt adımı gerektirmiyor ve unutulma yüzeyi mümkün olan en küçük noktaya indirilmiş.
- Admission yanıtları hata zarfı sözleşmesine tam uyuyor: `IStringLocalizer<ErrorMessages>` ile TR/EN lokalize `Title`/`Detail`, kararlı `code` (`ApiErrorCodes.SecurityRateLimited` / `SecurityLimiterUnavailable`), `traceId` fallback'i ve `application/problem+json` content-type — hem `IResult` (filtre) hem doğrudan yazım (middleware) yolunda.
- Redis anahtarları purpose-separated HMAC-SHA-256 pseudonym taşıyor; `Hash` uzunluk-prefix'li domain separation uyguluyor (`exact-ip`, `network`, `registration-exact`, `calculation-network`, ...), tüm ara buffer'lar `CryptographicOperations.ZeroMemory` ile temizleniyor ve hem unit hem integration testleri ham IP/principal/anahtarın ne Redis key'ine ne log'a girmediğini sentinel değerlerle doğruluyor.
- Lua script'i tüm kovaları önce kontrol edip sonra artırıyor (kısmi tüketim yok), pencere otoritesi olarak Redis `TIME` kullanıyor ve `PEXPIRE` ile kendi kendini topluyor. C# tarafı yanıtı çapraz alan tutarlılığıyla doğruluyor (`allowed==1 && retry!=0`, `allowed==0 && retry==0`, `retry > max(window)`) ve tutarsızlıkta fail-closed `MalformedReply` üretiyor — testler bu üç kombinasyonu da kapsıyor.
- Startup fail-closed: `ValidateOnStart` + `HasValidShape` (çapraz alan kuralları dahil: hourly ≤ daily, exact ≤ network) + `Path.IsPathFullyQualified` + `SecureSecretFile` üzerinden min-24-bayt gizli dosya kontrolü. `InvalidOptions_FailDuringHostStartup` testi bunu gerçek host başlatarak doğruluyor.
- Cancellation semantiği doğru ve test edilmiş: `ScriptEvaluateAsync(...).WaitAsync(ct)`, `catch (OperationCanceledException) { throw; }` ile iptal yutulmuyor, ve hem önceden iptal edilmiş token hem uçuşta iptal ayrı testlerle kilitlenmiş.
- Runbook'lar (`redis-unavailable.md`, `api-availability.md`) `saydin_security_admission_decisions_total`'ı `bucket,reason` ile gruplayıp `redis_failure` / `malformed_reply` / `client_address_untrusted` kök nedenlerini nasıl ayıracağını adım adım anlatıyor ve "fail-open'a çevirme" cazibesini açıkça yasaklıyor — telemetri şeması ile operasyon prosedürü birbirine gerçekten bağlanmış.

**Repo dışı bilgi gerektiren sorular.**

- Caddy imajı digest ile pinlenmiş (`caddy@sha256:b4e39523...`); bu sürüm ≥2.7 mi ve `trusted_proxies` varsayılanı ile istemciden gelen `X-Forwarded-For` taşınıyor mu yoksa ezilir mi? R01-04'ün gerçek üretim etkisi tamamen buna bağlı — imajı çekip `caddy version` çıktısı gerekiyor.
- Türk mobil operatörlerinde tek public IPv4 başına ortalama eşzamanlı abone sayısı ve IPv6 benimseme oranı nedir? R01-01'in şiddeti bu iki sayıya bağlı; repo dışı analitik/telemetri (ör. mevcut uygulama sürümünün IP dağılımı) olmadan kesinleştirilemiyor.
- Alertmanager tarafında repo dışında (ör. imzalı deployment env'inde) `saydin_security_admission_decisions_total` üzerine tanımlı bir kural veya bir Grafana alarmı var mı? Repo içindeki `infrastructure/prometheus/rules/` altında yok, ancak ADR-003 zorunlu kılıyor.
- `DistributedSecurityLimiter__ExactIpLimit` / `NetworkLimit` / `PrincipalLimit` üretimde compose'da açıkça set edilmiyor (yalnız registration ve calculation değerleri pinlenmiş) — bunlar bilinçli olarak mı kod varsayılanına (60/300/120) bırakıldı, yoksa imzalı env dosyasında override ediliyor mu?

---

## R02 — Installation kimlik yaşam döngüsü + migration 023/024

**Bulgu:** 0 Critical · 2 High · 3 Medium · 6 Low

**Kapsam.** R02 dosya listesindeki 6 untracked dosyanın tamamı (023/024 migration'ları, InstallationEndpoints, IInstallationRepository/InstallationRepository, InstallationCredentialKeyring) baştan sona okundu. Karşı taraf olarak 021_api_trust_expand.sql'in tablo DDL'i ve beş lifecycle fonksiyonu, 011'deki chk_activity_action tanımı, EndpointExtensions'ın üç admission filtresi, DistributedSecurityLimiter(+Options/Middleware/AdmissionProblem/Telemetry), Program.cs DI kayıtları, ActivityLogMiddleware/ActivityLogBuilder, ActivityActions, appsettings.json limitleri, MigrationTrustRoot checksum'ları (dosyalarla birebir doğrulandı), ApiTrustAuditSql beklentileri, ilgili unit/integration testler ve ADR-003/ADR-010/architecture/database-schema/rollback/high-traffic dokümanları incelendi. Test çalıştırılmadı (SDK lokal yok); PostgreSQL plan davranışı EXPLAIN ile değil, indeks/predikat analiziyle çıkarıldı.

**Reddedilen iddialar.**

- *023, activity_logs'un CHECK→trigger dönüşümünü kendi adıyla ilgisiz bir migration'a gömüyor; postcondition yalnız katalog şeklini kanıtlıyor* — İki merkezi iddia da kodla çürüdü. (1) 'İlgisiz migration' YANLIŞ: 023 dört yeni installation lifecycle action'ını (`installation_register`, `installation_rotation_begin`, `installation_rotation_commit`, `installation_revoke`) allowlist'e eklemek zorunda (023:157-162) ve CLAUDE.md'de belgelenen TS 2.16.1 kısıtı yüzünden compression açıkken CHECK yeniden eklenemez — dönüşüm tam da bu migration'ın işidir; başlık da bunu söylüyor: '023: installation lifecycle audit actions and pending-commit admission' (023:1). (2) 'Davranışsal kanıt yok' YANLIŞ:

**Güçlü kararlar.**

- Ham bearer secret hiçbir koşulda PostgreSQL'e gitmiyor: keyring in-process 32 byte CSPRNG üretiyor, DB'ye yalnız HMAC-SHA256 verifier + key version yazılıyor; API rolünün `installation_credentials` üzerinde INSERT/UPDATE/DELETE yetkisi olmadığı hem 024 preflight'ında hem postcondition'ında `has_table_privilege` ile doğrulanıyor.
- Migration'lar GRANT'e güvenmek yerine sonucu kanıtlıyor: 023 ve 024 `aclexplode` ile fonksiyonun tam ACL şeklini (tek grantee = api_cap, grantor = owner, EXECUTE, is_grantable=false), `proowner`'ı ve `search_path` konfigürasyonunu postcondition olarak assert edip aksi hâlde 42501 ile transaction'ı düşürüyor.
- 023'ün preflight'ı, öncül `chk_activity_action` tanımının SHA-256 parmak izini birebir şart koşuyor — `IF EXISTS` tahmini yerine pre-state hakkında gerçek bir kanıt; migration yanlış bir şema üzerinde asla çalışmıyor.
- Rehash fonksiyonunun eşzamanlılık tasarımı doğru: `FOR UPDATE` sonrası EPQ recheck ile satırın kaybolması durumunda çağıranın verdiği aktif verifier ile idempotent yeniden okuma yapılıyor, pending/revoked/expired satırlar bu yola asla giremiyor; integration testi iki eşzamanlı resolve ile bunu gerçekten yürütüyor.
- Generic 401 sözleşmesi tutarlı korunmuş: endpoint'ler kabul edilen tüm key sürümlerini sırayla deneyip 28000'i yutuyor ve hangi sürümün (varsa) eşleştiğini dışarı sızdırmıyor; malformed/unknown/expired/revoked hepsi aynı `WWW-Authenticate: Installation` + `code=invalid_installation_credential` yanıtına düşüyor.
- Secret hijyeni titiz: keyring JSON'undan base64 doğrudan UTF-8 buffer üzerinden decode ediliyor (ara managed string bırakmamak için), her hata ve finally yolunda `CryptographicOperations.ZeroMemory` çağrılıyor, `GeneratedInstallationCredential.ToString()` [REDACTED] döndürüyor ve çözülmüş secret uygulama kodu çalışmadan önce sıfırlanıyor.
- Admission telemetrisi kardinalite-güvenli: `SecurityAdmissionTelemetry.Record` bucket/outcome/reason değerlerini derleme zamanı sabit bir sözlüğe karşı doğrulayıp aksi hâlde `ArgumentOutOfRangeException` fırlatıyor — metrik etiketlerine serbest metin sızamıyor; Redis limiter anahtarları da yalnız private-file HMAC pseudonym taşıyor.
- Trust root disiplini gerçek: `MigrationTrustRoot` içindeki 023/024 SHA-256 değerleri diskteki dosyalarla birebir eşleşiyor (doğrulandı) ve `ApiTrustAuditSql` yeni fonksiyonların `prosecdef`/`proconfig`/gövde hash'ini pinliyor, böylece deploy sonrası drift ayrı bir denetimle yakalanabiliyor.

**Repo dışı bilgi gerektiren sorular.**

- Production'da istemci IP topolojisi nedir: Caddy önünde per-abone sonlandıran bir CDN/edge var mı ve Türk mobil operatörlerinden gelen trafiğin ne kadarı CGNAT'lı IPv4 /24'lerden geliyor? Bu oran, R02-02'nin 'sert onboarding duvarı' mı yoksa kabul edilmiş bir takas mı olduğunu belirler.
- İlk 12 ayda beklenen `installation_credentials` satır sayısı (kurulum hedefi) nedir? R02-01'deki O(N) tarama uçurumunun ne zaman vurduğunu bu sayı belirliyor.
- Repo dışında installation keyring key rotasyonu için yazılı bir operasyon prosedürü (drain penceresi, artık-sayım sorgusu, key düşürme kapısı) var mı? R02-03, repoda hiçbir izi olmadığı için 'yok' varsayımıyla yazıldı.
- Flutter istemcisinin ilk açılışta `POST /v1/installations`'tan gelen 429 + `Retry-After: 86400` yanıtı için tanımlı bir davranışı (offline mod, retry politikası, kullanıcıya gösterilecek metin) var mı? Yoksa R02-02'nin kullanıcıya yansıyan etkisi tarif edilenden daha sert olur.

---

## R03 — Activity logging ve pseudonymization

**Bulgu:** 0 Critical · 1 High · 8 Medium · 8 Low

**Kapsam.** R03 dosya listesindeki 10 dosyanın tamamı okundu (yeni: ActivityLogChannelLifetime, JsonbStorageSize, ActivityPrincipalPseudonymOptions/Pseudonymizer; değişen: ActivityLogWriter, ActivityLogBatchStore, ActivityLogBuilder, ActivityAuditOutcome, CalculationTelemetry, ActivityLogMiddleware). Karşı taraf olarak Program.cs kayıt sırası, EndpointExtensions/InstallationEndpoints pseudonym yazımı, ActivityLogConfiguration, ActivityActions, migration 008/011/013/022/023, SaydinMetrics + prometheus rules/tests + api-errors/container-restart runbook'ları, docker-compose/compose.production secret materyali ve ilgili unit/integration testler okundu; ayrıca eski review'in H-06 (FatalHost) maddesi ve 07-remediation-progress.md iddiası doğrulandı. Okunmayan: DQA/migrator'ın 023 imza-pin mekanizmasının tamamı, GeoIP/IpMasker iç detayı ve R03 dışı hatların dosyaları.

**Güçlü kararlar.**

- ActivityLogChannelLifetime'ın ayrı bir hosted-service fazı olarak eklenmesi doğru ve zarif: writer artık kendi channel'ını kapatmıyor, ingress Kestrel drain'inden SONRA kapanıyor ve kapanış anında üretilen satırlar kaybolmak yerine ya yazılıyor ya da `queue_rejected_writes` ile sayaçlanıyor. Program.cs:301-305'teki yorum ters-durdurma sırasını açıkça belgeliyor.
- Transient sınıflandırmanın genişlemesi mock'la değil gerçek PostgreSQL ile kanıtlanmış: `TerminateConnectionOnceStore` kendi backend'ini `pg_terminate_backend` ile düşürüp writer'ın batch'i gerçekten kurtardığını doğruluyor (ActivityLogWriterIntegrationTests).
- JsonbStorageSize, JSON-metin uzunluğu proxy'sinin kör noktasını kapatıyor: allocation'sız, taşmada doyuma giden bir üst sınır; gerçek `pg_column_size` ile karşılaştırmalı integration testi ve bilimsel-gösterim/exponent taşması için kapsamlı Theory setiyle birlikte geliyor.
- ActivityPrincipalPseudonymizer'ın anahtar yaşam döngüsü credential keyring'inden bağımsız; sabit uzunluklu domain-separation prefix'i, 128-bit'e kırpılmış HMAC, bilinen-cevap test vektörü (`p1:87410fc9…`), yol sızdırmayan sabit hata mesajı ve ZeroMemory hijyeni birlikte düşünülmüş.
- TelemetryPrivacyTests artık gerçek endpoint payload factory'lerini (`WhatIfEndpoints.CreateCalculateActivityData` vb.) sentinel tutarlarla çalıştırıyor — önceki review'in "tautolojik redaksiyon testi" bulgusu gerçekten kapatılmış, semptom örtülmemiş.
- MetricsTestCollection, process-global instrument'ları dinleyen testlerin birbirini kirletmesini engelleyen gerçek bir determinizm düzeltmesi; gerekçesi sınıf yorumunda açıkça yazılmış.
- SaydinActivityLogLoss alarmı label-eşleşmesi gerektirmeyen `{__name__=~...}` formuna çevrilmiş ve promtool'da hem pozitif hem "hiçbiri artmıyor" negatif senaryosuyla, ayrıca validate-prometheus-runtime.py'de canlı label-şekli admission'ıyla kilitlenmiş.
- Migration 022/023 tarafındaki KVKK silme sınırı eksiksiz: principal silinirken hem `user_id` NULL'lanıyor hem `device_id='server-redacted'` yazılıyor, yani pseudonym de erasure kapsamına giriyor.

**Repo dışı bilgi gerektiren sorular.**

- Üretimdeki PostgreSQL topolojisi tek instance mı, yoksa failover/standby içeren bir kurulum mu? R03-01'in 25006 (read-only transaction) tetikleyicisinin gerçekçiliği ve dolayısıyla severity'si buna bağlı.
- `activity-principal-hmac` materyali operatör tarafında hangi KMS/secret store'dan üretiliyor ve kurum politikası bu anahtar için periyodik rotasyon zorunlu kılıyor mu? (Repo'da ne prosedür ne de sürüm alanı var.)
- Rolling deploy sırasında critical Alertmanager route'una düşen bir sayfa operasyonel olarak kabul edilebilir mi, yoksa deploy penceresi için susturma (silence) prosedürü dışarıda tanımlı mı? R03-02'nin etkisi buna göre değişir.

---

## R04 — API servis/repository katmanı + Shared

**Bulgu:** 0 Critical · 0 High · 4 Medium · 8 Low

**Kapsam.** R04.files listesindeki 35 dosyanın tamamının `git diff`'i okundu (DcaCalculator, WhatIfCalculator, CalculationCacheEntries, PriceRepository, InflationRepository, FinalObservationAuthority, ScenarioExtraDataValidator/BodyReader, Program.cs, resx, appsettings, Dockerfile ve tüm Saydin.Shared EF configuration/entity/metrics dosyaları). İddiaları doğrulamak için lane dışındaki karşı taraflar da okundu: `AssetService`, `AuthorityCacheEntries`, `ApiRuntimeContract`/`ApiPortBoundaryMiddleware`/`ApiEndpointSurface`, `JsonbStorageSize`, `CalculationTelemetry`, migration 011/021/022/023/024, `DcaCalculatorTests`, `SavedScenarioRepositoryIntegrationTests`, `docs/cache-strategy.md`, `docs/architecture.md` ve önceki review'in 01/02/07 raporları. Okunmayan: DataQualityAudit/DataRepair/PriceIngestion/CalendarData kaynakları, CI/infra script'leri ve Saydin.Api'nin lane dışı Security/Middleware/BackgroundServices dosyalarının derinlemesine incelemesi (yalnız çağrı sınırı düzeyinde bakıldı).

**Reddedilen iddialar.**

- *El yazımı PostgreSQL jsonb::text boyut sayacının en riskli sayısal dalları gerçek-PG parite testinde yok* — İddianın dar kısmı doğru (gerçek-PG parite testi SavedScenarioRepositoryIntegrationTests.cs:26-30 yalnız üç düz payload içeriyor), ama asıl iddia — 'bu yüzeyin çoğu doğrulanmamış' — yanlış. tests/Saydin.Api.Tests/Services/ScenarioExtraDataValidatorTests.cs:101-116 tam da 'en riskli sayısal dalları' beklenen PostgreSQL numeric metniyle kilitliyor: `1.2300`, `1.2300e2`→`123.00`, `1e-2`→`0.01`, `1.0e-2`→`0.010`, `1E+3`→`1000`, `-0.00`→`0.00`, `1e-100`→100 ondalıklı gösterim, 33 haneli yüksek-scale. Satır 218-227 ise exponent overflow dalını (`1e10

**Güçlü kararlar.**

- `source_raw` redaksiyonu kökten çözülmüş: `PriceRepository.ApiProjection` + raw SQL'de `source_raw IS NOT NULL AS has_source_raw` + `PricePoint.SourceRaw` üzerinde `[JsonIgnore]` üçlüsü, potansiyel olarak büyük provider kanıtının API sorgularında materyalize edilmesini, Redis'e ve public DTO'ya sızmasını aynı anda kapatıyor — üstelik `GetPriceRangeAsync` gibi bir yıllık aralık sorgularında ciddi bir I/O kazancı da sağlıyor.
- `GetNearestPricesAsync` raw SQL'i, `FinalObservationAuthority` LINQ predicate'ini (asset.source paritesi ve provider/price_kind çiftleri dahil) birebir aynalıyor ve `WITH ORDINALITY` ile mükerrer istek tarihlerinin ordinal konumunu koruyor; test de `dates[2] == dates[3]` invariant'ını açıkça assert ediyor.
- Terminal CPI kademesi önceki review'in tam olarak istediği şekilde uygulanmış: LKV deflatörü + ayrı `RealReturnMethod = cashflow_cpi_lkv_terminal_v1` + gerçekten kullanılan ayı bildiren `InflationTerminalMonth`/`InflationDataAsOf`, ve `CalculateAsync_CurrentTerminalMonthUsesLatestFinalCpi_AndReturnsUsedMonth` testi senaryoyu literal beklenen değerlerle (310 / -10 / -3,23) kilitliyor.
- Eksik fiyatlı katkılar artık 404 yerine `SkippedPurchaseDates` + `purchase_price_unavailable` warning + `DataStatus=degraded` ile şeffaf kısmi sonuca dönüşüyor; hepsi eksikse yine fail-closed `PriceNotFoundException` atılıyor — davranış hem testte hem `docs/architecture.md` ve `docs/cache-strategy.md`'de belgelenmiş.
- `ScenariosEndpoints.CreateSaveActivityData` audit payload'ına artık istemcinin gönderdiği ham `request.AssetSymbol` yerine repository tarafından doğrulanmış `scenario.AssetSymbol` yazıyor — küçük ama doğru bir audit-integrity düzeltmesi ve yorumla gerekçelendirilmiş.
- Lokalizasyon disiplinli: `QuotaUnavailableExceptionHandler`'daki hardcoded İngilizce başlık `IStringLocalizer` ile değiştirilmiş, yeni `RouteNotFound`/`SecurityLimiterUnavailable`/`QuotaUnavailable`/`CalculationEndDateCannotBeInFuture` anahtarları TR ve EN resx'lerine eksiksiz eklenmiş — iki dosya 88/88 anahtarla tam parite gösteriyor ve kodda kullanılan hiçbir anahtar eksik değil.
- `ScenarioExtraDataValidator`'ın yeni `PostgresJsonbTextSizeCounter`'ı ikinci bir serialize buffer'ı tamamen kaldırıyor, limitin bir byte üstünde satüre oluyor ve U+0000 ile eşleşmemiş surrogate'leri fail-closed reddediyor — saldırgan kontrollü allocation yüzeyini gerçekten daraltan bir tasarım.
- `IngestionWindow`'daki `= DateTimeOffset.UtcNow` CLR varsayılanlarının kaldırılması, `HasDefaultValueSql("NOW()")` kolon varsayılanlarının fiilen devreye girmesini sağlıyor (EF artık sentinel değeri görüp kolonu INSERT'ten düşürüyor) — sessiz bir uygulama/DB saat ikiliğini kapatan doğru düzeltme.

**Repo dışı bilgi gerektiren sorular.**

- BTC/TRY'nin güncel büyüklük mertebesi ve ürünün desteklemeyi hedeflediği minimum periyodik tutar nedir? R04-02'nin etkisi (100 TL'de ~%1) bu iki değere bağlı; birim hassasiyetini asset kategorisine göre ölçeklemek bir ürün kararı gerektiriyor.
- TÜİK/EVDS'nin TÜFE yayın takvimi ve ingestion'ın gerçek gecikmesi tam olarak nedir? R04-01'in penceresi (ayın ilk ~3 günü) bu takvime dayanıyor; yayın gününün kayması pencereyi büyütür.
- Flutter istemcisi `InitialValueTry`/`TotalInvestedTry` alanlarını kullanıcının girdiği tutarla eşit varsayıyor mu, ve meta repo `docs/architecture/api-contract.md` `SkippedPurchaseDates`, `RealReturnMethod=cashflow_cpi_lkv_terminal_v1` ve değişen `InflationDataAsOf` semantiği için güncellendi mi? Meta repo bu repoda değil.
- Production container profili `/proc` erişimini kısıtlıyor mu (R04-10'un tetiklenebilirliği), ve deploy sırasında Redis'i boşaltan bir adım var mı (R04-11'in gerçek etkisi)?

---

## R05 — PriceIngestion worker/repository

**Bulgu:** 0 Critical · 2 High · 11 Medium · 4 Low

**Kapsam.** R05.files listesindeki tüm dosyalar okundu: `Workers/*` (BaseAssetWorker, EvdsInflationWorker, IngestionOrchestrator, IngestionFreshnessHydrationService, TcmbWorker, yeni ProviderDeadlineExceededException/ProviderExceptionSanitizer, silinen PermanentIngestionWindowException) ve `Repositories/*` (IngestionWindowRepository/Contracts, PriceIngestionRepository, silinen IngestionJobRepository/InflationIngestionRepository + interface'leri) — hem `git diff` hem tam dosya gövdeleri. İddiaları doğrulamak için hat dışı karşı taraflar da okundu: `Adapters/AdapterCompleteness.cs`, `TcmbAdapter`, `TwelveDataAdapter`, `OpenExchangeRatesAdapter`, `EvdsInflationAdapter`, `HttpResilienceExtensions`, `Program.cs`, `appsettings.json`, `IngestionFreshnessTelemetry`, `SaydinMetrics`, `tools/calendar-data/CalendarDataGenerator`, `tests/Saydin.PriceIngestion.Tests/Workers/*`, `IngestionOrchestratorTests`, `docs/runbooks/ingestion-stale.md` ve `docs/analysis/pr-review/01,07`. Migration SQL gövdeleri, DQA/DataRepair iç mantığı ve API hattı okunmadı (başka hatlar).

**Güçlü kararlar.**

- Permanent-window izolasyonu doğru katmanda çözülmüş: `DrainAsync` artık tipli `DrainResult` döndürüyor, `PermanentIngestionWindowException` tamamen silinmiş ve `BackfillAsync` bloke olan asset'ten sonra `continue` ile sonraki asset'e geçiyor — hiçbir provider-permanent durum orchestrator'ın fatal alanına ulaşmıyor (önceki review'ın 8 numaralı High'ının crash-loop kısmı gerçekten kapanmış).
- `next_attempt_at` sözleşmesi artık zamanlayıcıyı gerçekten yönetiyor: `WorkerPass` bir geçişteki en yakın uyanma zamanını topluyor ve `GetDelayUntilNextRun` `min(due, bir sonraki planlı koşu)` uyguluyor; `Busy` durumunda `LeaseUntil` bile uyanma zamanı olarak kullanılıyor.
- Silinen `IngestionJobRepository`/`InflationIngestionRepository`'nin sorumluluğu `IngestionWindowRepository`'nin tek transaction'ına taşınmış: veri UPSERT'i + `ingestion_windows` terminal state'i + `ingestion_jobs` terminal state'i (ve `records_upserted`) aynı DbContext/connection/transaction'da commit ediliyor — eski tasarımdaki job/window drift sınıfı tamamen ortadan kalkmış, davranış kaybı yok.
- Mutlak provider deadline'ı temiz uygulanmış: `linked.Cancel()` sonrası askıda kalan task `ObserveDetachedAsync` ile gözlemleniyor (unobserved-exception yok), `finally` bloğu deadline timer'ını ve lease yenilemesini kesin olarak kapatıyor, ve sonuç ledger'a `provider_deadline` retryable outcome'u olarak yazılıyor.
- Authority immutability ihlali (`chk_price_points_authority_immutable` / `chk_inflation_rates_authority_immutable`) çıplak `PostgresException` olarak patlamak yerine tipli `provider_revision_conflict` permanent outcome'una çevriliyor — provider revizyonu artık sessiz crash değil, denetlenebilir bir ledger kaydı.
- TCMB hedef günü artık duvar saatinden değil, `ResolveLatestExpectedObservationAsync` ile kanıt-sınırlı sealed takvim release'inden seçiliyor; `CalendarDataGenerator.ResolveTcmbCoverageThrough` coverage'ı gerçek yayın kanıtına bağladığı için worker TCMB'nin henüz yayınlamadığı bir günü hiç istemiyor — bu fail-closed tasarım örnek alınacak nitelikte.
- Freshness hydration fail-soft yapılırken `OperationCanceledException` bilinçli olarak yeniden fırlatılıyor; host cancellation "hydration hatası" olarak yanlış sınıflanmıyor ve bu ayrım testle korunuyor.
- `ReadFreshnessStateAsync` sorgusu asset kümesini `assets` tablosundan türetiyor (job'lardan değil) ve `min(data_through)` alıyor; yeni etkinleştirilen bir asset sağlıklı bir kardeşin arkasına saklanamıyor — bu incelikli tuzak bilinçli olarak kapatılmış ve yorumla açıklanmış.

**Repo dışı bilgi gerektiren sorular.**

- TwelveData'nın kullanılan planında BIST günlük (1day) barı kapanıştan (18:10 Europe/Istanbul) sonra pratikte ne kadar sürede erişilebilir oluyor? R05-01'in tetiklenme sıklığı doğrudan buna bağlı.
- Üretimdeki `bist_pay_xist` aktif release'inin `coverage_through` değeri gerçekten bugünü ve ötesini kapsıyor mu (45 günlük horizon gereksinimi bunu ima ediyor)? Kapsamıyorsa TwelveData her gün `calendar_coverage_missing` ile erteleniyor olabilir ve arıza modu R05-01 yerine "hiç ilerlemeyen worker" olur.
- OpenExchangeRates'in gerçek istek gecikmesi ve rate-limit davranışı nedir? 365 günlük ilk backfill penceresinin 3 dakikalık mutlak deadline'a sığıp sığmadığı (R05-14) buna bağlı.
- `permanent_failed` pencereleri izleyen, bu repo dışında (meta repo dashboard'ları veya operasyon runbook'ları) bir sorgu/panel var mı? Yoksa R05-07 tek gözlem yolu olarak kalıyor.
- Ingestion worker'ı üretimde tek replica mı çalışıyor? Lease sahibinin kimliğine bakılmaması (R05-02'deki 30 dk self-block) yalnız tek-replica varsayımında güvenle optimize edilebilir.

---

## R06 — PriceIngestion adapter/mapper

**Bulgu:** 0 Critical · 0 High · 8 Medium · 7 Low

**Kapsam.** Hat listesindeki 12 dosyanın tamamı okundu (5 adapter, 6 mapper + yeni `ProviderValueParser`, `HttpResilienceExtensions`); karşı taraf olarak `BaseAssetWorker`/`EvdsInflationWorker` deadline+lease akışı, `ProviderPayload`/`AdapterCompleteness`/`ProviderAuthority`/`ObservationEvidence`, `ProviderFailureClassifier`, `ProviderExceptionSanitizer`, `Program.cs` HTTP client kayıtları, `IngestionWindowRepository.RecordFailureAsync`, migration `020_price_authority_expand.sql` authority trigger grameri, `infrastructure/prometheus/rules/ingestion.yml` ve ilgili `PriceIngestion.Tests` test diff'leri okundu. Okunmayan: `Repositories/*` tam diff'i, `IngestionOrchestrator`/`IngestionFreshnessHydrationService` iç mantığı, integration test compose akışı — bunlar başka hatların kapsamı.

**Güçlü kararlar.**

- Kültür/binlik-ayırıcı riski gerçekten kökten kapatılmış: `NumberStyles.Any` yerine tek merkezde tanımlı `ProviderValueParser.FinancialNumberStyles` (AllowThousands, AllowParentheses ve AllowExponent bilinçli olarak dışarıda) + `CultureInfo.InvariantCulture`. "23,45" artık 2345 olarak parse edilemiyor. Üstelik semptom değil kök neden kapatılmış ve TCMB/TwelveData/EVDS mapper'larında `[InlineData("2115,19")] [InlineData("2.115,19")] [InlineData("(30.5)")]` negatif testleriyle, ayrıca dört kültür altında byte-identical evidence üreten `ObservationAuthorityCultureTests` ile korunuyor.
- `TcmbAdapter.ExtractCurrencyCode` `RemoveEmptyEntries`'ten düz `Split('.')`'e çevrilerek PostgreSQL authority trigger'ının `LIKE '%.%.%'` + `split_part(source_id,'.',3)` semantiğiyle birebir hizalanmış; `TP..USD`, `TP.DK..A`, `A.B` ve çıplak `USD` uçlarında migration 020 ile davranış eşleşmesi doğrulanabiliyor ve parametreli bir testle sabitlenmiş. Kanıt JSON'u ile DB'nin yeniden türettiği değerin ayrışması bu sayede yapısal olarak imkânsız.
- `TwelveDataMapper.MapContractlessFixture` — yalnızca testlerin identity doğrulamasını atlaması için var olan bir production API'si — silinmiş ve testler gerçek sözleşme yolundan (`Envelope(...)` + gerçek `meta`) yeniden yazılmış. Test kolaylığı için üretim koduna açılmış bir bypass'ı kapatmak doğru yön.
- Beş mapper'ın tamamında her `TryGetX`/`GetString` öncesine açık `ValueKind` kapısı konmuş; sağlayıcı bir alanın JSON tipini değiştirdiğinde sonuç yakalanmayan `InvalidOperationException` veya sessiz sıfır değil, tipli `contract_value_kind_invalid` oluyor.
- `AdapterCompleteness.Price` sayesinde boru hattı fail-closed: atlanan, mükerrer ve aralık dışı satırlar `PartialRejected` + sayaçlara dönüşüyor, `acceptedSet.SetEquals(expected)` şartı sağlanmadan hiçbir pencere başarılı sayılmıyor. Yani kısmen yanlış parse edilmiş bir sağlayıcı yanıtı sessizce başarı olarak veritabanına yazılamıyor.
- Worker deadline'ı `Task.Delay(ProviderDeadline, timeProvider, ...)` ile enjekte edilmiş saate bağlanmış, iptal `linked.Token` üzerinden `BoundedHttpContent.ReadAsync`'e kadar geçiyor, kopan task `ObserveDetachedAsync` ile gözleniyor (unobserved exception yok) ve sonuç durable, tipli `provider_deadline` oluyor. Gerçek bir `StalledReadStream` ile gövde-okuma stall'ını süren `StalledHttpResponseBody_UsesWorkerDeadlineAndTypedProviderOutcome` testi, lease yenilemesinin de durduğunu ayrıca doğruluyor.
- Kontrol akışı `PermanentIngestionWindowException` fırlatmaktan açık bir `DrainResult`/`WorkerPass` durum makinesine taşınmış: zehirli tek bir pencere artık tüm worker pass'ini düşürmüyor, kardeş asset'ler devam ediyor ve bir sonraki uyanma `NextWakeAt` ile veri-güdümlü hâle gelmiş (sabit günlük tick yerine).
- `chk_price_points_authority_immutable` check ihlalinin yakalanıp tipli `provider_revision_conflict` permanent sonucuna çevrilmesi, append-once authority sözleşmesini ham `PostgresException` sızdırmadan worker seviyesinde görünür kılıyor.

**Repo dışı bilgi gerektiren sorular.**

- EVDS `TP.FG.J0` serisi, yayınlanmamış bir ay için JSON `null` mı yoksa `"ND"` string'i mi dönüyor? R06-02'nin gerçek tetiklenme olasılığı buna bağlı — gerçek bir EVDS yanıt örneği gerekiyor.
- tcmb.gov.tr ve openexchangerates.org için üretimden ölçülmüş p95 istek gecikmesi nedir? R06-03'te 365 günlük OER chunk'ının ve 90 günlük TCMB chunk'ının soğuk cache ile 3 dakikalık pencere bütçesine sığıp sığmadığı bu sayıya bağlı.
- 365 günlük bir CoinGecko `market_chart/range` (precision=6, `market_caps` + `total_volumes` dahil) ve 5000 satırlık bir TwelveData `time_series` yanıtının gerçek boyutu nedir? R06-06'daki 64 KiB sınırına ne kadar pay kaldığı ölçülmeden severity kesinleştirilemez.
- Kullanılan Microsoft.Extensions.Http.Resilience 10.6.0 sürümünün `DelayBackoffType.Exponential` + `UseJitter=true` için ürettiği tam gecikme dağılımı (decorrelated-jitter-v2 katsayıları) — R06-12'deki ~%3 flake tahmini bu formüle dayanıyor ve paket kaynağından teyit edilmeli.
- OpenExchangeRates free plan (1.000 istek/ay) altında 365 günlük bir chunk'ın tekrarlanan deadline denemelerinde kota tüketimi nasıl davranıyor? Adapter'ın 24 saatlik in-memory cache'i restart sonrası kaybolduğunda aylık kotanın tükenip tükenmeyeceği ölçülmeli.

---

## R07 — Migrator + RoleBootstrap + DatabaseSecurity

**Bulgu:** 0 Critical · 1 High · 2 Medium · 8 Low

**Kapsam.** R07.files listesindeki 19 dosyanın tamamı okundu (migrator diff'i satır satır, RoleBootstrap/DatabaseSecurity diff'leri + yeni `SensitivePassword.cs`/`PostgresScramSha256Verifier.cs` tam metin, `Dockerfile.migrator`, `apply-migrations.sh`, `bootstrap-dev-database.sh`, `migration-impact/`). Karşı taraf olarak `infrastructure/deployment/compose.production.yml`, `validate-production.py`, `docker-compose.yml`, 023/024 SQL checksum'ları, DQA embedded manifest'i, RoleBootstrap unit/integration testleri ve iki yeni runbook da okundu; SCRAM vektörü bağımsız PBKDF2/HMAC hesabıyla doğrulandı. API/ingestion iş mantığı, backup entrypoint'inin tamamı ve calendar hattı bu hattın dışında bırakıldı.

**Reddedilen iddialar.**

- *SCRAM iteration sayısı 4096'ya sabitlenmiş — yükseltme yolu yok* — Kod iddiası doğru (PostgresScramSha256Verifier.cs:11 `const int Iterations = 4096`, :32-34 `IsCanonical` iteration alanının tam olarak bu değer olmasını şart koşuyor, RoleBootstrapDatabaseOperations.cs:940-941 `VerifyRoleAttributes` bunu kullanıyor), fakat tetikleme senaryosu YANLIŞ ve bulgunun tamamı ona dayanıyor.
`scram_iterations` GUC'u yalnız sunucunun DÜZ METİN parolayı hash'lediği yolda etkilidir. Bu kod tabanı sunucuya asla düz metin göndermiyor: `EnsureRoleAsync` (:122-124) ve `AlterRolePasswordAsync` (:161-163) her ikisi de `PostgresS
- *İki kimlik doğrulama probe'u farklı sertleştirme ayarlarıyla kuruluyor* — İddiayı doğrudan çürüttüm. Uygulama login probe'u (RoleBootstrapDatabaseOperations.cs:1082-1091) builder'ını `new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)` üzerinden kuruyor ve `adminBuilder` `BuildAdminConnection` içinde ZATEN `LogParameters = false` set ediyor (RoleBootstrapRunner.cs:234) — yani ayar miras alınıyor, atlanmıyor. `PersistSecurityInfo` ise Npgsql'de varsayılan olarak zaten false; backup probe'u (:1146-1162) bunu yalnız açıkça yeniden yazıyor. Sonuç: iki yol arasında DAVRANIŞSAL fark yok.
Finder kaydın kendisi

**Güçlü kararlar.**

- Client-side SCRAM-SHA-256 verifier kriptografik olarak doğru: bağımsız PBKDF2/HMAC hesabıyla test vektörü (`PostgresScramSha256VerifierTests.cs:16-20`) birebir doğrulandı — SaltedPassword/ClientKey/StoredKey/ServerKey türetimi ve `SCRAM-SHA-256$it:salt$stored:server` biçimi PostgreSQL'in beklediğiyle aynı; salt CSPRNG'den, ara anahtarlar `finally` içinde `CryptographicOperations.ZeroMemory` ile siliniyor.
- Plaintext parolanın SQL metnine girdiği son yol da kapandı: migrator'daki `ApplyExporterRoleBodyAsync` (`format('ALTER ROLE saydin_exporter WITH LOGIN PASSWORD %L', $1)`) tamamen kaldırıldı ve 012b artık koşulsuz `skipped_optional`; RoleBootstrap ise `CREATE/ALTER ROLE ... PASSWORD %L`'e yalnız verifier gönderiyor. `RoleCredentialLifecycleIntegrationTests.cs:35-43` bunu `pg_stat_activity` üzerinden gerçek PG ile doğruluyor.
- Önceki review'in "backup login VALID UNTIL marker'a pinli, süre dolunca kalıcı kilit" bulgusu gerçekten kapandı: `ExtendManagedBackupValidityAsync` (RoleBootstrapDatabaseOperations.cs:210-231) süresi geçmiş bir rolde bile forward-only uzatmaya izin veriyor, regresyonu (`backup_valid_until_regression`) ve DB saatine göre [24s, 93g] sınırlarını fail-closed uyguluyor, marker'ı aynı transaction'da güncelliyor; `docs/runbooks/backup-login-renewal.md` prosedürü yazılı.
- `SensitivePassword`/`LoadedSecrets` yaşam döngüsü doğru kurulmuş: `LoadPasswordInputs`'ta kısmi yükleme hatasında o ana kadar okunanlar `catch` içinde dispose ediliyor, `using var secrets` ile deterministik sıfırlama var, `Dispose` idempotent ve `Material` dispose sonrası `ObjectDisposedException` atıyor.
- Online migration lease fencing'i tek sorguya indirildi: checkpoint `SELECT ... FOR UPDATE` artık `lease_nonce=$4 AND lease_expires_at>clock_timestamp()`'i aynı satırda değerlendiriyor ve her iki checkpoint `UPDATE`'ine de aynı yüklem eklendi (OnlineMigrationExecutor.cs) — read/update arasındaki split-brain penceresi kapandı.
- `MigrationImpactPreflight` artık çağıranın gerçek `lock_timeout`/`statement_timeout` değerlerini okuyup geri yüklüyor (önceki kod körlemesine `'0'`'a resetliyordu) ve `SetSessionTimeoutsAsync` `try` bloğunun içine alındığı için kısmi set durumunda da geri yükleme çalışıyor.
- 023/024 trust-root pin'leri diskteki dosyalarla bayt bayt eşleşiyor (`shasum -a 256` ile doğrulandı) ve `MigrationTrustRoot`, `EmbeddedMigrations` sabitleri ile `Saydin.DataQualityAudit.csproj` embedded resource listesi tutarlı; ayrıca `::regprocedure` cast'i `to_regprocedure`'a çevrilerek eksik fonksiyonun 42883 yerine düzgün fingerprint mismatch üretmesi sağlandı.
- Operatör tanılaması belirgin biçimde iyileşti: `migration rejected: code=schema_fingerprint_mismatch; fingerprint=<check>` artık hangi kontrolün düştüğünü söylüyor (Program.cs:48-51) ve `DatabaseCode` SQLSTATE'i sunucu metnini sızdırmadan, şekli doğrulanmış biçimde kod adına gömüyor (`DatabaseFailureCodeTests`).

**Repo dışı bilgi gerektiren sorular.**

- Npgsql'in SASL akışı SASLprep (RFC 4013) uyguluyor mu? R07-03'ün yarıçapı buna bağlı: uygulamıyorsa ayrışma yalnız libpq tüketicilerinde (pg_basebackup/pg_receivewal/psql), uyguluyorsa RoleBootstrap'ın kendi probe'u da non-ASCII parolada kırılır.
- Production `backup-v1` ve uygulama login secret'ları hangi üreteçle oluşturuluyor (karakter kümesi ASCII ile sınırlı mı)? Depoda yalnız dev üreteci (hex) var; production secret üretimi operatör ortamında.
- `docs/analysis/pr-review/07-remediation-progress.md`'de kanıt olarak gösterilen `eb03bf08631d4517841b5faba65c203a` koşusunda `retire` sonrası `ensure` ve `database-migrator --verify-only` gerçekten çalıştırıldı mı, yoksa yalnız `verify` mi? Depodaki test kodunda retire→ensure zinciri yok.
- Production PostgreSQL'de `scram_iterations` GUC'u varsayılan 4096'da mı bırakılıyor (R07-04'ün tetiklenebilirliği buna bağlı)?

---

## R08 — DataQualityAudit + DataRepair

**Bulgu:** 0 Critical · 0 High · 6 Medium · 10 Low

**Kapsam.** `src/Saydin.DataQualityAudit/*` ve `src/Saydin.DataRepair/*` (yeni `Dockerfile` dahil) tam okundu; ayrıca karşı taraf olarak `tests/Saydin.DataQualityAudit.Tests`, `tests/Saydin.DataQualityAudit.IntegrationTests`, `tests/Saydin.DataRepair.Tests|IntegrationTests`, `docs/runbooks/data-repair.md`, `infrastructure/deployment/compose.production.yml` (data-repair servisi), `infrastructure/release/validate-release.py`, `infrastructure/release/Dockerfile.dqa`, `.github/scripts/verify-integration-trx.py`, migration 017 FK'ları ve `docs/analysis/pr-review/02-findings-medium.md` (#21, #22, #23) incelendi. Okunmayanlar: DQA integration fixture'ının tamamı (yalnız ledger/multi-window ve session_replication_role bölümleri), `Saydin.DatabaseSecurity` içindeki `SecureSecretFile`/`RoleContract` gövdeleri (yalnız çağrı sözleşmesi), OCI KMS SDK yolu.

**Reddedilen iddialar.**

- *İki SQL sözleşme testi karşılığı konmadan silindi* — Dosyaların silindiği ve unit tarafta artık `ApiTrustAuditSql`/`PrincipalRetentionAuditSql`'e referans kalmadığı doğru (grep: yalnız src/ altında geçiyorlar). Ancak 'yeni yüzeyi kapsayan koruma yok' iddiası yanlış: tests/Saydin.DataQualityAudit.IntegrationTests/DataQualityAuditAcceptanceTests.cs:343-402 `EveryAuditedFunctionMutation_IsDetectedAndRestored` testi tam olarak `resolve_installation_and_rehash(...)`, `installation_verifier_matches(...)`, `resolve_installation_rotation_commit(...)`, `enforce_activity_action_allowlist()` dahil 13 fonksi
- *`ValidateTarget` içinde hiçbir zaman doğru olamayacak bir koruma koşulu var* — Olgu doğru: Program.cs:36-37 `FromEnvironment(LoginPurpose.Ingestion, ...)` çağırıyor ve RuntimeDatabase.cs:72-73 purpose'u olduğu gibi döndürülen kayda koyuyor, dolayısıyla Program.cs:121'deki `runtime.Purpose != LoginPurpose.Ingestion` bugün her zaman false. Ancak bu 'yanlış güvence' değil, aynı ifade içinde gerçek korumayı zaten `runtime.Login.Name != runtime.Contract.Login(LoginPurpose.Ingestion, 1).Name` sağlıyor (login adı purpose'tan türediği için purpose değiştirilirse bu kontrol kırılır) ve `FromEnvironment` içinde ayrıca `runtime_data

**Güçlü kararlar.**

- `full_window` yükleminin daraltılması (02-findings-medium.md #21) gerçekten kök nedeninden kapatılmış: `AuditSql.cs:10` ve `:88` `= $2 AND = $3` yerine `>= $2 AND <= $3` oldu ve `AuditDatabaseFixture.CreateMultiWindowLedgerMismatchAsync` + `MultiWindowPriceAndInflation_MetadataMismatchIsAuditedWithinSignedScope` ile çok-pencereli lane'de `ledger_requested_count_mismatch`, `ledger_success_actual_count_mismatch`, `ledger_month_count_mismatch` kodlarının tetiklendiği gerçek-PG ile mühürlenmiş (session_replication_role=replica ile CHECK atlanarak).
- Rollback pending-receipt uzlaştırma sırası bulgusu (#22) tam doğru şekilde düzeltilmiş: `RepairExecutor.cs`'te `PendingExists("rollback")` bloğu apply-postimage assert'inin önüne alınmış ve postimage kontrolüne `verifyGuard: false` eklenmiş; `RollbackCommitThenPublishFailure_ReconcilesPendingBeforeApplyPostimageCheck` adlı özel integration testiyle enjeksiyonlu olarak kanıtlanmış.
- Guard/CAS/lease negatif matrisi (#23) gerçekten dolduruldu: `RepairGuardIntegrationTests` (14 senaryo — guard bütçesi, transaction içi ilgili-state mutasyonu, tam-satır CAS drift'i, rollback restore doğrulaması, lease kaybı, tamper edilmiş final receipt) ve `RepairLiveTrustIntegrationTests` (fiziksel kimlik/deployment/rol/migration-control/read-only ACL drift'i) eklendi.
- `run-isolated.sh` kapısı sertleştirildi: `TEST_FILTER` fail-closed reddediliyor, TRX üretiliyor ve `verify-integration-trx.py --minimum-executed 32` ile total=executed=passed, failed/skipped/notExecuted=0 zorunlu kılınıyor; `readonly` atamaları `openssl`/`python3` çıkış kodunu maskelemeyecek şekilde ikiye ayrılmış.
- Sır ele alışı iki executable'da da SecureSecretFile sözleşmesine taşındı (HMAC anahtarı ve private PEM), `finally` içinde `CryptographicOperations.ZeroMemory`/`Array.Clear` ile temizleniyor ve symlink/group-readable reddi `SecretFileContractTests` ile gerçek dosya sistemi üzerinde doğrulanıyor.
- P1363→DER dönüşümündeki baştaki sıfır baytı hatası hem `AuditCryptography` hem `RepairCryptography` içinde `TrimP1363Component` ile aynı anda düzeltildi ve `P1363Signature_WithLeadingZeroComponents_NormalizesToCanonicalDer` ile tam beklenen DER baytlarına karşı pinlendi.
- Daha önce sınırsız olan global taramalar fail-closed bütçelere bağlandı (`MaxGlobalRows`, `MaxCalendarReleases`; `global_scan_budget_exceeded`, `calendar_release_budget_exceeded`, `calendar_scan_budget_exceeded`) ve `bounded ... LIMIT $5 + 1` deseni `total_count`'un bütçeyi aşan koşularda yanlış rapor edilmesi yerine reddedilmesini sağlıyor; audit hâlâ RepeatableRead + `SET TRANSACTION READ ONLY` + explicit rollback ile katı salt-okunur.
- Yeni `src/Saydin.DataRepair/Dockerfile` üretim sertliği bakımından örnek: digest-pinned SDK/runtime, `-p:RestoreLockedMode=true`, uid 1001 nologin kullanıcı, 0700 dizinler, `--chown` ile kopyalanan çıktı — ve compose tarafı (`read_only`, `cap_drop: ALL`, `no-new-privileges`, `profiles: [data-repair-operator]`, `command: [operator-command-required]`) ile `validate-release.py`'daki release-manifest bağlaması bunu uçtan uca doğruluyor.

**Repo dışı bilgi gerektiren sorular.**

- İmzalı onarım planının (`plan.json` + `plan.sig`) üretimi ve P-256 imzalama seremonisi kasıtlı olarak repo dışında mı tutuluyor? Eğer öyleyse hangi belge/araç bunu tanımlıyor ve `preimageSha256` ile `migrationTrust.manifestSha256` değerleri orada nasıl hesaplanıyor?
- `--production-target-authority-file` tasarım niyeti hangisi: out-of-band bir otorite mi (bu durumda değerin manifest'ten türetilebilir olmaması gerekir), yoksa yalnız operatörün üretim hedefini bilinçli onaylaması mı?
- DQ-006 ve DQ-009 kontrollerinin imzalı scope'a daraltılması bir ADR veya kabul edilmiş maliyet/kapsam takasına mı dayanıyor; yoksa yalnız bütçe bulgusuna cevap olarak mı yapıldı? Kapsam kaybının kabul edildiği bir kayıt var mı?
- DQA kanıt paketini tüketen repo dışı bir araç/otomasyon var mı? (Varsa `repair-recommendations.json` şemasının değişmesi ve yeni aksiyon değerleri o tarafta kırılma yaratabilir.)

---

## R09 — calendar-data ve calendar infrastructure

**Bulgu:** 0 Critical · 2 High · 5 Medium · 6 Low

> ⚠️ Bu hattın doğrulayıcısı oturum limiti nedeniyle koşamadı. Kayıtlar yalnız üreten
> agent'a dayanır; High'ları ana agent elle denetlemiştir.

**Kapsam.** R09.files listesindeki tüm dosyalar okundu: `tools/calendar-data/src/**` (yeni `CalendarPlanMaterializer.cs` dahil), `Dockerfile`, silinen `tools/calendar-data/Directory.Packages.props` + kök CPM/lock karşılığı, 4 test dosyası (3'ü yeni), `infrastructure/calendar/**` (run-acquisition, verify-candidate, promote-reviewed-bundle, systemd unit, env örneği, README), `docs/runbooks/calendar-release.md`, `.github/scripts/run-calendar-data-tests.sh`. Karşı taraflar da okundu: `SourceSnapshotStore.ValidateSource`, `CalendarReleaseImporter.ParseDays`, migration `017` (`market_calendar_days` CHECK/genişlik), `IngestionFreshnessTelemetry.RecordCalendarHorizon` ve `infrastructure/prometheus/rules/ingestion.yml`. İki bulgu (R09-01, R09-02) pinned SDK imajında derlenip scratchpad kopyası üzerinde fiilen çalıştırılarak doğrulandı; repo çalışma ağacına hiçbir yazma yapılmadı. Okunmayan: BIST PDF parser içselleri (`BistPayCalendarParser`, diff dışı) ve calendar release import/activate SQL akışının tamamı.

**Güçlü kararlar.**

- Paket yönetimi merkezileştirildi: `tools/calendar-data/Directory.Packages.props` silinip kök CPM'e taşındı, sürümler birebir korundu (PdfPig 0.1.15, Npgsql 10.0.3, xunit 2.9.2) ve `CentralPackageTransitivePinningEnabled` sayesinde Newtonsoft.Json transitive olarak 13.0.1'den 13.0.4'e yükseltilip lock dosyasına `CentralTransitive` olarak yazıldı — pinleme bozulmadı, aksine sıkılaştı.
- Dockerfile gerçek multi-stage'e geçirildi: build SDK + `-p:RestoreLockedMode=true` ile lock'lu restore, runtime katmanı `dotnet/runtime` digest'i, `groupadd/useradd -u 1001` ile non-root; test/veri/publish ayrımı ve csproj+lock önce kopyalama ile katman önbelleği doğru kuruldu. `.github/scripts/run-calendar-data-tests.sh` de `--force-evaluate` yerine `--locked-mode` kullanıyor — tedarik zinciri kapısı artık gerçekten kapalı.
- `verify-candidate.sh` iki noktada anlamlı biçimde sertleşti: reviewer public key'in SHA-256'sı `SAYDIN_CALENDAR_REVIEWER_PUBLIC_KEY_SHA256` ile pinlendi, signature/public key için canonical-path kontrolü eklendi, candidate uid/gid doğrulanıp container aynı kimlikle koşuyor ve offline replay SONRASI manifest/expected/public-key hash'leri yeniden kontrol edilerek TOCTOU penceresi kapatıldı.
- `SecureBundleStorage.DeletePrivateTree` örnek bir yıkıcı-işlem tasarımı: beklenen parent + `.pending-` prefix + reparse-point kontrolü olmadan hiçbir şey silmiyor; `CalendarAcquisition` hata yolunda artık staging'de yarım candidate bırakmıyor.
- `RequireUri` artık her zaman unescape ederek karşılaştırıyor ama önce `%2f`/`%5c` kodlanmış ayırıcıları reddediyor — hem TCMB SSS URI'sindeki `%2B` gerçeğini destekliyor hem de encoded-separator kaçışını kapatıyor; `FailClosedParserTests`'e tek ve çift kodlanmış iki vaka için tablo testi eklenmiş.
- BIST açık-gün satırlarının `regular_weekday` yerine `inferred_open_from_official_closure_schedule` reason code'una geçirilmesi ve buna eşlik eden `ValidateBistIndexLink` (index→exact yıllık PDF çapraz bağı) kanıtın niteliğini gizlemek yerine dürüstçe kodluyor; `NormalizedCalendarReplayTests` bunu `Assert.DoesNotContain(rows, r => r.ReasonCode == "regular_weekday")` ile kalıcı olarak sabitliyor.
- Testlerde `DateOnly.Parse` çağrıları `ParseExact(..., CultureInfo.InvariantCulture)` ile değiştirilmiş ve sessiz `return` ile geçen platform-koşullu testler açık skip'e çevrilmiş — determinizm ve dürüstlük yönünde doğru hamleler.
- `VerifyCandidateBehaviorTests` gerçek `verify-candidate.sh`'ı beş mutasyonla (geçerli, manifest hash, envanter dışı dosya, yabancı imza anahtarı, sahibi yanlış) uçtan uca çalıştırıyor; script kontratını metin assert'i yerine davranışla doğrulaması bu değişiklik setinin en güçlü test katkısı.

**Repo dışı bilgi gerektiren sorular.**

- TCMB'nin aylık arşiv sayfası (`https://www.tcmb.gov.tr/kurlar/YYYYMM/Mon_tr.html`) ilgili ayın ilk yayını yapılmadan önce de erişilebilir mi, yoksa 404 mü döner? R09-03'ün ay/yıl dönümü hard-fail senaryosunun sıklığı buna bağlı.
- `https://www.tcmb.gov.tr/kurlar/kurYYYY_tr.html` yıllık indeks sayfası, o yılın ilk yayınından önce (ör. 1-2 Ocak) yayında mı?
- Üretimdeki `saydin-calendar` host hesabının uid/gid'i gerçekten 1001 mi? Aksi halde `run-acquisition.sh` ve `promote-reviewed-bundle.sh` `runtime_identity_mismatch` ile fail-closed olur ve akış hiç başlamaz.
- Promotion sonrası quarantine candidate'ının kalıcı olarak silinmesi (R09-04) denetim/adli gereksinimlerle uyumlu mu; promote edilen kopyanın dışında bağımsız bir arşiv/yedek tutuluyor mu?
- TCMB günlük acquisition job'ının başarısızlığı için systemd `OnFailure=` benzeri doğrudan bir alarm kanalı üretimde tanımlı mı, yoksa tek sinyal `saydin_market_calendar_coverage_horizon_days` metriği üzerinden dolaylı mı geliyor?

---

## R10 — infrastructure/backup

**Bulgu:** 0 Critical · 1 High · 9 Medium · 6 Low

> ⚠️ Bu hattın doğrulayıcısı oturum limiti nedeniyle koşamadı. Kayıtlar yalnız üreten
> agent'a dayanır; High'ları ana agent elle denetlemiştir.

**Kapsam.** infrastructure/backup altındaki tüm değişen/yeni dosyalar (backup-entrypoint.sh, restore-drill.sh, prepare-recovery.sh, Dockerfile, README, wal-highwater.py, wal-recovery-evidence.py ve altı smoke/self-test) satır satır okundu; karşı taraf olarak compose.production.yml backup servisleri, host-backup.yml + rules.test.yml, .github/workflows/{ci,restore-drill,promote-production}.yml, .github/scripts/run-backup-auth-tests.sh, manage_backup_hba.py, deploy-release.sh validity kapısı ve docs/runbooks/{restore-drill,backup-failure,backup-login-renewal}.md incelendi. İki iddia (restic repo dizin modu, Linux'ta TemporaryDirectory temizliği) yerel Docker ile fiilen üretilerek doğrulandı. DataQualityAudit/RoleBootstrap iç mantığı ve OCI KMS tarafı bu hattın kapsamı dışında bırakıldı.

**Güçlü kararlar.**

- `archive_timeout=300s` yalnız compose'a eklenmekle kalmamış: `archive-timeout-receiver-smoke.py` pinlenmiş üretim PostgreSQL imajıyla gerçek bir `pg_receivewal --synchronous` kurup, `archive_mode=off` iken zorlanmış segment dönüşünü sunucu log satırı (`write-ahead log switch forced (archive_timeout=30)`) ve tam 16 MiB tamamlanmış dosya boyutuyla kanıtlıyor. Bu, metin grep'i değil gerçek davranış kanıtı ve init sunucusu ile nihai postmaster'ı bile ayırt ediyor.
- Base staging tmpfs'ten çıkarılıp harici disk volume'üne taşınmış ve tek bir yol değişikliğiyle yetinilmemiş: mountpoint zorunluluğu, `/proc/mounts` üzerinden tmpfs/ramfs reddi, kanonik yol, 1001:1001/0700 sahiplik-mod kontrolü, `df` ile ≥8 GiB serbest alan kapısı, flock ile serileştirme ve tüm çıkış yollarında (EXIT/HUP/INT/TERM) guard'lı `current` temizliği eklenmiş; `/tmp` tmpfs 2 GiB'dan 64 MiB'a indirilmiş.
- WAL tazeliği artık gerçek recovery point'e bağlı: aynı fiziksel credential ile `IDENTIFY_SYSTEM` + `SHOW wal_segment_size` alınıp `wal-highwater.py` ile sunucunun current/previous segment adı hesaplanıyor, system-id hash'i doğrulanıyor ve yerel en yeni tamamlanmış segment bunlardan biri değilse hiçbir metrik yazılmıyor. Bu, önceki review'in "metrik döngü canlılığını ölçüyor" bulgusunu yapısal olarak kapatan doğru tasarım.
- `wal-recovery-evidence.py` sözleşmeyi kapalı-küme anahtar eşitliğiyle doğruluyor ve self-test'i gerçek negatif senaryoları koşuyor: bayat gözlem, gelecek zaman damgası, symlink enjeksiyonu, receiver-geride ve high-water yeniden-türetme uyuşmazlığı. Tautoloji yok; her vaka gerçekten reddediliyor.
- Managed login'in CONNECTION LIMIT 2 sınırı, iki ayrı container arasında paylaşılan metrics volume'ü üzerinde `flock` ile gerçekten korunuyor (base backup bloklayarak, WAL turu non-blocking deneyerek) — kod yorumu ve README bu tasarım kararını gerekçesiyle birlikte açıklıyor.
- Restic lock yarışı temiz kapatılmış: tüm çağrılara `--retry-lock 15m`, `forget --prune` ayrıştırılıp `forget` (hot path) ile `prune --no-cache` (en fazla 7 günde bir, marker dosyası sahiplik/mod/saat-gerilemesi doğrulamalı) ayrılmış; prune başarısızlığı base zamanlayıcısını düşürmüyor, `backup_repository_prune_deferred` ile erteleniyor.
- Restore drill'in cleanup/idempotans katmanı gerçekten test edilir hale gelmiş: run_id-run_attempt ile benzersiz kaynak adları, çalıştırma öncesi ön-varlık taraması, daemon erişilemezliğini "yok" saymayan üçlü durum (0/1/2) mantığı, residual tespitinde exit 70, ve `restore-cleanup-behavior-self-test.py` bu fonksiyonları sahte `docker` ile gerçekten koşturup başarı/sinyal/residual/daemon-down dört yolunu doğruluyor.
- `base-backup-behavior-smoke.py` gerçek container davranışını çok geniş bir negatif yelpazeyle ölçüyor: fail-once backoff, taze metrik varken base'in bastırılması, symlink artığında fail-closed, SIGTERM'de staging temizliği, auth hatasının 78 ile kapanması, lokalize Türkçe HBA mesajının kabulü, metrik dizini kilitliyken watermark'ın ilerlememesi ve başarısız restic yüklemesinde recovery point'in ilerlememesi.

**Repo dışı bilgi gerektiren sorular.**

- Üretimdeki gerçek PGDATA boyutu ve `pg_basebackup --format=plain --wal-method=fetch` süresi nedir? Bu, R10-02'nin (base penceresinde WAL yüklemesinin durması) 15 dk RPO'yu fiilen ihlal edip etmediğini belirler.
- `SAYDIN_BACKUP_WAL_VOLUME` ve `SAYDIN_BACKUP_BASE_STAGING_VOLUME` hangi kapasiteyle sağlanıyor ve host disk profili nedir? R10-05'teki ~63 GiB'lık yerel WAL üst sınırı mevcut sağlamada karşılanıyor mu?
- Restore drill runner'ı GitHub-hosted mı yoksa self-hosted mı, hangi kullanıcı/Docker modu (rootful/rootless) ile koşuyor? Bu, R10-01'in CI'da kesin olarak tetiklenip tetiklenmediğini kapatır (mekanizma Linux+Python 3.12'de birebir üretildi, ancak rootless/userns-remap bir runner'da maskelenir).
- `vars.SAYDIN_RESTORE_SCHEDULE_RELEASE_TAG` operasyonel olarak nasıl güncel tutuluyor? Zamanlanmış drill receipt'i bu tag'in release'ine yüklendiği için, tag bayatlarsa promotion kapısının beklediği receipt yanlış release'e düşer.
- Object-store tarafında istek sayısı/maliyet sınırı var mı? WAL hattı günde ~288 snapshot + 288 `forget` üretiyor (R10-14); bu profil sağlayıcı kotalarıyla uyumlu mu?

---

## R11 — Deployment, Prometheus, Alertmanager, OTEL

**Bulgu:** 1 Critical · 0 High · 8 Medium · 6 Low

**Kapsam.** R11 dosya listesinin tamamı okundu: `infrastructure/deployment/*` (compose.production.yml 941 satır, Caddyfile, blackbox.yml, production.env.example, 6 validator + 5 self-test, tests/ fixture'ları), `infrastructure/prometheus/*` (6 rule dosyası + rules.test.yml + yeni inventory.test.yml + prometheus.production.yml), `infrastructure/alertmanager/*` ve `infrastructure/otel/*`. Karşı taraflar da doğrulandı: `infrastructure/release/deploy-release.sh` (monitoring aşaması, volume/blackbox admission), `.github/workflows/ci.yml` + `release-images.yml` + `verify-release-ci-admission.py` (kapıların gerçekten koştuğu yer), `src/Saydin.Shared/Diagnostics/SaydinMetrics.cs` ve her iki `Program.cs` (alert ifadelerinin dayandığı metrik ad/label'ları), `ApiPortBoundaryMiddleware` (Caddy `@internal` regex'inin karşı tarafı), `IngestionFreshnessHydrationService` (deploy kapısının beklediği serilerin startup'ta materyalize olup olmadığı), `backup-entrypoint.sh` metrik yazıcıları ve ilgili runbook'lar. Okunmayanlar: backup/DR script gövdeleri, migration SQL'leri, API iş mantığı ve DataRepair/DQA kodu — başka hatların kapsamı.

**Reddedilen iddialar.**

- *Healthy-fixture negatif testi SaydinBackupFailure için exporter'ın hiç yazmadığı bir değere ve 1 dakikalık marja dayanıyor* — Olguları doğruladım: fixture (inventory.test.yml:297-298) `saydin_backup_last_failure_timestamp_seconds{kind="wal"}` için `values: '0x40'` kullanıyor, negatif iddia `eval_time: 31m` (time()=1860), kural `time() - metric < 1800` — marj 60 saniye. backup-entrypoint.sh:207-214'teki `write_failure_metric` gerçekten yalnız gerçek hatada `date +%s` yazıyor, yani 0 değeri üretimde oluşmaz. ANCAK bulgunun merkezî etki iddiası yanlış: 'kural eşiği 1800'den 3600'e çıkarılırsa test bunu yakalamaz' doğru değil — 1860 - 0 = 1860 < 3600 olacağı için alert fi

**Güçlü kararlar.**

- `SaydinActivityLogLoss` kök nedeni gerçekten kapatılmış: kural `sum(increase({__name__=~"saydin_activity_log_(write_failures|queue_drops|queue_rejected_writes)_total",job="saydin-api"}[10m])) > 0` ile label uyuşmazlığından bağımsız hale gelmiş VE `SaydinMetrics.InitializeActivityLogContractSeries()` (Program.cs:381) üç sayacı startup'ta 0 ile materyalize ettiği için hem alert hem canlı metric admission'ı ilk gerçek kaybı beklemeden çalışıyor. Semptomu örten değil, iki taraflı bir düzeltme.
- Deploy otomasyonu artık monitoring düzlemini gerçekten ayağa kaldırıyor ve kapıya bağlıyor: aday Prometheus/Alertmanager/Collector/Tempo/Loki config'leri kendi digest-pinned binary'leriyle doğrulanıyor (deploy-release.sh:265-274), sonra force-recreate ediliyor, ardından Prometheus container'ının içinden sekiz readiness ucu + `/api/v1/rules` + `/api/v1/targets` + `/api/v1/series` kapıları geçilmeden deployment receipt'i yazılmıyor.
- `validate-observability.py` her alert için POZİTİF ve (watchdog hariç, gerekçesi kodda yazılı) NEGATİF promtool testi zorunlu kılıyor; ayrıca runbook varlığı, runbook origin'i ve runbook'lardaki hayalet alert referanslarını da kapatıyor. 16 mutasyonlu `observability-self-test.py` bu kapıların gerçekten fail-closed olduğunu kanıtlıyor. 40 alert'in 40'ı da test ediliyor (22 inventory + 18 rules pozitif).
- Tek `management` ağı beş amaca özel internal segmente ayrılmış (`telemetry-ingest`, `monitoring-core`, `monitoring-scrape`, `blackbox-control`, `host-scrape`) ve her servisin ağ kümesi `validate-production.py`'de birebir eşitlikle makine tarafından zorlanıyor; egress ağlarının tek tüketicili olması da ayrıca doğrulanıyor.
- Container hardening validator'ı önceki review'in istediği tüm kaçış yollarını reddediyor: `privileged`, `cap_add`, `devices`, `sysctls`, `group_add`, `network_mode`, `pid/ipc/uts/userns=host`, `/var/run/docker.sock` bind ve host-root bind — her biri için `validation-self-test.py`'de ayrı mutasyon var. node-exporter'ın `/:/host:ro,rslave` istisnası tam olarak (servis adı + hedef + propagation + collector allowlist) sınırlanmış ve README'de gerekçelendirilmiş.
- Dead-man's-switch uçtan uca eklenmiş: `SaydinWatchdog` kuralı, `severity="watchdog"` için ayrı route (group_wait 0s, repeat 1m), ayrı `external-watchdog` receiver, private-material ön kontrolü ve `telemetry-pipeline.md`'de 'asla susturma/inhibit etme' talimatı. Inhibit kuralı da watchdog'u kapsamıyor.
- Blackbox hedef materyali artık tek bir allowlist'li dış URL: `validate-blackbox-targets.py` 0700/uid 65534 dizinde yalnız `blackbox.json` kabul ediyor, içeriği tam olarak `https://<SAYDIN_PUBLIC_HOST>/health/live` sözleşmesine eşitliyor; self-test 169.254.169.254 metadata hedefinin reddedildiğini kanıtlıyor — probe yüzeyi üzerinden SSRF/keşif kapatılmış.
- Collector'da iki ince ama doğru karar yorumla birlikte alınmış: `resource_to_telemetry_conversion: enabled: false` (kardinalite/gizlilik patlaması engelleniyor) ve `service.instance.id` için `upsert`→`insert` (her servis ve restart'ın tek kimliğe çökmesi engelleniyor); ikisi de observability-self-test mutasyonlarıyla mühürlenmiş.

**Repo dışı bilgi gerektiren sorular.**

- Staging ve production ortamlarında GitHub `vars.SAYDIN_ENABLE_INGESTION` değeri nedir? False ise R11-05'teki iki kalıcı critical alert bugün zaten firing durumda olur.
- Rendered `alertmanager.yml`'deki `external-watchdog` receiver'ı gerçekten repo/altyapı dışı bağımsız bir dead-man's-switch servisine mi (Healthchecks.io, Dead Man's Snitch, PagerDuty heartbeat vb.) bağlı, yoksa operator-critical ile aynı webhook altyapısına mı? Substring kapısı bunu ayırt edemiyor (R11-02).
- Production release manifest'indeki `runtimeImages.prometheus` / `alertmanager` / `otel` / `tempo` / `loki` digest'leri, `validate-production-assets.sh` ve `release-images.yml` içinde sabitlenmiş digest'lerle aynı mı (R11-10)?
- `SAYDIN_PROMETHEUS_RETENTION`, `SAYDIN_TEMPO_RETENTION`, `SAYDIN_LOKI_RETENTION` production değerleri nedir? Prometheus retention'ı R11-01'deki zaman-sınırsız `/api/v1/series` kapısının ne kadar geriye kadar 'geçerli' seri kabul ettiğini doğrudan belirliyor.

---

## R12 — Release supply chain, CI workflow, kapılar

**Bulgu:** 1 Critical · 0 High · 6 Medium · 8 Low

**Kapsam.** R12.files listesindeki 33 dosyanın tamamı okundu (infrastructure/release/* tam gövde, .github/workflows/ci.yml 1004 satır + release-images/promote-production/restore-drill, tüm .github/scripts kapı scriptleri ve iki yeni self-test). Karşı taraf olarak deploy-staging.yml, rollback-production.yml, docker-compose.yml (tests/migrator/post-bootstrap servisleri), .github/compose.integration.yml, infrastructure/postgres/Dockerfile.migrator, src/Saydin.DatabaseMigrator/{MigrationRunner,MigrationManifest,MigrationTrustRoot,Program}.cs, migration dosya envanteri, IngestionDatabaseFixture/RepairDatabaseFixture ve önceki review'in 01/03/06/07 dokümanları okundu; validate-release.py, validate-workflows.py, release-manifest-self-test.py, rollback-admission-self-test.py ve test-verify-release-ci-admission.py lokal olarak çalıştırıldı (hepsi exit 0). Okunmayan: infrastructure/deployment/validate-production*.py gövdeleri ve backup entrypoint iç mantığı (başka hatların kapsamı) — bunlara yalnız çağrı sözleşmesi düzeyinde bakıldı.

**Reddedilen iddialar.**

- *`run-backup-auth-tests.sh`'ten HBA-red kanıtı çıkarıldı — 'sql-deny' kapısı psql'in herhangi bir nedenle başarısız olmasını kabul ediyor* — Diff doğru: iki assertion gerçekten silinmiş. Ama finder karşı tarafı okumamış ve üç şeyi kaçırmış. (1) Kaldırma kasıtlı ve sözleşmeye bağlı: `infrastructure/backup/tests/backup-static-self-test.py:229-235` `locale_independent_backup_auth` kontrolü ile bu grep'lerin script'te BULUNMAMASINI zorunlu kılıyor (server `lc_messages` İngilizce olmayabilir). Finder'ın önerisi ('grep'i geri koy') required CI'ı kırar. (2) Korunan asıl güvenlik özelliği — backup rolünün SQL yapamaması — hâlâ doğrudan test ediliyor: HBA'daki reject kuralı kaybolup yerini g
- *`SAYDIN_CI_MIGRATOR_*_ROLE_PREFIX` GITHUB_ENV'e iki farklı değerle yazılıyor* — Çifte yazım gerçek (ci.yml:262,265 placeholder; :382-395 `create_hba_fixture_contract` türetmesi) — ama finder neden'i araştırmamış. `.github/compose.integration.yml:62,103,348,537,567` bu değişkenleri `${VAR:?...}` (required) sözdizimiyle kullanıyor; Compose bu sözdizimini TÜM dosya için interpolasyon anında değerlendirir. ci.yml:311-319'daki ara adım (`docker compose ... up --detach --wait postgres postgres-migrator-secondary redis`) gerçek değerler henüz üretilmeden koşuyor, dolayısıyla placeholder'lar bu adımın çalışabilmesi için ZORUNLU. F

**Güçlü kararlar.**

- Önceki Critical'ın kök nedeni gerçekten kapatılmış: deploy-release.sh'teki 9-anahtarlı inline runtime sözlüğü tamamen silindi, bağlama `render-deployment-env.py --verify-existing` üzerinden tek Python otoritesine (`release_manifest.RUNTIME_IMAGE_ENV_KEYS`) taşındı ve `validate-release.py:117-118` sözlüğün geri gelmesini `deployment_runtime_image_mapping_duplicated` ile yasakladı.
- `release-manifest-self-test.py` artık gerçek env binder'ı uçtan uca koşturuyor: render → verify-existing pozitifi, `SAYDIN_LOKI_IMAGE` eksik, fazladan `SAYDIN_UNEXPECTED_IMAGE`, `SAYDIN_TEMPO_IMAGE` mismatch ve `data_repair` türetme uyuşmazlığı negatifleri dahil — yani KeyError'ı üreten anahtar (`loki`) artık açıkça mühürlü.
- `run-development-compose-smoke.sh` gerçekten kök compose'u ayağa kaldırıyor (secret bootstrap → HBA → pre-bootstrap → migrator → post-bootstrap → api/exporter health), yalnız `config` çalıştırmıyor; üstelik run-scoped project adı, `.env.database-runtime` var-olma guard'ı, mode/uid kontrollü metadata silme ve `residual=0:0:0:0` container/volume/network/image sızıntı kapısıyla temizliği kanıtlıyor.
- Tüm integration runner'larında `dotnet restore --force-evaluate` → `--locked-mode` geçişi yapıldı; kilitli bağımlılık grafiği artık CI'da fiilen zorunlu.
- Unit proje envanteri sabit sayı yerine dosya sisteminden türetiliyor: `run-unit-coverage.sh:44-56` `unit_project_inventory_mismatch` ile diff basıyor, `ci.yml:76-78` ve `coverage-admission` `expected_reports`'u `find` ile hesaplıyor, `validate-workflows.py:123-133` sln↔repo↔runner üçlüsünü karşılıklı doğruluyor.
- Monitoring admission sırası doğru kurgulanmış: promtool/amtool/otelcol/tempo/loki config'leri aday binary'lerle `run --rm` ile doğrulanıyor, ancak ondan sonra `up -d --force-recreate` yapılıyor ve arkasından canlı `/api/v1/rules|targets|series` + `validate-prometheus-runtime.py` kapısı geliyor; `validate-release.py:103-110` bu sıralamayı statik olarak da mühürlüyor.
- Workflow güvenlik hijyeni birinci sınıf: tüm release/deploy workflow'larında top-level `permissions: {}`, job düzeyinde en az yetki, istisnasız 40-hex SHA action pinleri (`validate-workflows.py` + `validate-release.py` çift kapı), `persist-credentials: false`, promote ve rollback'in aynı `saydin-production` concurrency grubunu paylaşması ve `::add-mask::` ile keyring secret'ının log'dan gizlenmesi.
- Restore drill artık zamanlanmış (`17 2 1,15 * *`) ve tüm receipt/artefakt adları `run_id-run_attempt` ile ayrıştırıldı; aynı run'ın yeniden denemesi eski kanıtı ezmiyor ve `promote-production.yml` receipt'i `runAttempt` alanıyla birlikte doğruluyor.

**Repo dışı bilgi gerektiren sorular.**

- `production-assurance` job'ı yeni compose smoke ile birlikte gerçek bir GitHub-hosted runner'da ne kadar sürüyor? 20 dakikalık timeout ölçülmüş bir değere mi dayanıyor, yoksa smoke eklenmeden önceki süreye mi (R12-09 severity'si buna bağlı)?
- `staging`, `production`, `production-rollback` ve `restore-drill` GitHub environment'larında gerçekten required reviewer / wait timer tanımlı mı? Repo içinden yalnız `environment:` referansı görülebiliyor; production onay kapısının varlığı repo dışı konfigürasyona bağlı.
- `vars.SAYDIN_RESTORE_SCHEDULE_RELEASE_TAG` ve `vars.SAYDIN_RUNTIME_IMAGE_LOCK_FILE` kim tarafından, hangi kadansla güncelleniyor? Zamanlanmış restore drill'in başarısızlığı için repo dışında (Actions bildirimi, e-posta, on-call) bir alarm yolu var mı?
- Self-hosted `saydin-release`/`saydin-staging`/`saydin-production` runner'ları ephemeral mi? deploy-release.sh `/tmp` altında mktemp kullanıyor ve `$RUNNER_TEMP` receipt dizinlerinin var olmamasını şart koşuyor; kalıcı runner'da önceki koşu artıkları `deployment_receipt_target_exists` (73) ile deploy'u bloke edebilir.

---

## R13 — Saydin.Api test kalitesi

**Bulgu:** 0 Critical · 0 High · 8 Medium · 9 Low

**Kapsam.** R13.files listesindeki 33 dosyanın tamamı okundu: `tests/Saydin.Api.Tests/**` ve `tests/Saydin.Api.IntegrationTests/**` için `git diff`, yeni (untracked) `MetricsTestCollection.cs`, `ActivityPrincipalPseudonymizerTests.cs`, `InstallationCredentialRehashIntegrationTests.cs` tam metin. İddiaları doğrulamak için karşı taraf üretim kodu da okundu: `ApiPortBoundaryMiddleware`/`ApiEndpointSurface`/`ApiRuntimeContract`, `EndpointExtensions`, `ActivityLogWriter`/`ActivityLogBatchStore`, `ActivityPrincipalPseudonymizer`, `SecurityAdmissionTelemetry`, `LinuxSecretFile`, `DcaCalculator`, `ActivityLogConfiguration`, `Resources/ErrorMessages*.resx`, `Program.cs` endpoint haritası ve migration 021/023/024. Testler çalıştırılmadı (lokal .NET yok); iddialar kod okuması ve `docs/analysis/pr-review/07-remediation-progress.md` kanıt tablosuna dayanıyor.

**Reddedilen iddialar.**

- *Yeni admission fail-closed yolları (untrusted client address, pending-rotation filtresi) test edilmiyor* — İddianın her iki yarısı da karşı tarafta korunuyor. (1) Untrusted address: TryGetTrustedClientAddress ve fail-closed 503 davranışı DistributedSecurityLimiterTests.cs:282 `Middleware_UnknownAddress_ReturnsStable503AndDoesNotInvokeNext` ve 307 `Middleware_UnconsumedForwardedAddress_Returns503` ile doğrulanıyor; üstelik Program.cs:348-349 limiter middleware'ini admission-exempt olmayan tüm isteklerde çalıştırdığı için güvenilmez adres endpoint filtresine ulaşmadan 503'e dönüşür — finder'ın 'yanlış proxy → registration/calculation 503' tetiklemesi
- *Test isimlendirme sözleşmesi (MethodName_Scenario_ExpectedResult) yeni testlerde sistematik olarak terk edilmiş* — `git diff -- tests/Saydin.Api.Tests | grep '^+.*public.*Task\|void'` çıktısını inceledim: bu commit'in eklediği testlerin büyük kısmı konvansiyona UYUYOR — CalculateAsync_HighUnitPriceAtBreakeven_..., CalculateAsync_ExplicitFutureEndDate_IsRejectedBeforeQuota, GetStorageUtf8Size_ControlCharacters_UsesPostgresEscapes, GetStorageUtf8Size_DecimalAndExponent_..., ReadAsync_Utf8Bom_..., Validate_NumberOutsideBoundedDecimalDomain_..., Load_ProductionSecretReader_..., ObserveDcaAsync_.... Tüm Saydin.Api.Tests'te üç parçalı isim sayısı 182, iki parçalı

**Güçlü kararlar.**

- Tautolojik assertion'ların literal oracle'a çevrilmesi: `WhatIfCalculatorTests` artık `Math.Round(10_000m/5.95m, 6, AwayFromZero)` gibi üretim formülünü tekrarlamak yerine `1680.672269m`, `14_285.71m`, `42.86m` sabitlerini doğruluyor; reverse yolda da `11_764.705882m`/`100_000m`/`70_000m` forward-consistency kilidi literal. Bu, hesap mantığı ile testin aynı anda bozulmasını imkânsız kılıyor.
- Plan testinden `SET enable_seqscan=off` / `enable_sort=off` kaldırılıp yerine 2.500 satırlık gerçek planner gürültüsü + `ANALYZE` konması: `idx_saved_scenarios_user_created_id_desc` seçimi artık zorlanmış değil, planner'ın gerçek kararı. Bu, sahte güvence veren bir testin gerçek bir teste dönüştürülmesinin ders niteliğinde örneği.
- Mutable static test hook'unun (`LinuxSecretFile.AfterOpenBeforeReadForTests`) üretim tipinden kaldırılıp açık bir `ReadForTests(..., LinuxSecretFileTestProbe)` parametresine çevrilmesi; üstelik davranış kapsamı kaybolmamış, `SecretFileTests`'e taşınmış ve Api tarafında 'yazılabilir statik state kalmadı' reflection guard'ı bırakılmış.
- `if (!OperatingSystem.IsLinux()) return;` sessiz-geçen desenlerinin `RequireLinux()` → `PlatformNotSupportedException` ile değiştirilmesi: artık yanlış platformda test 'yeşil' görünmüyor, fail ediyor. Zero-skip CI kapısıyla tutarlı ve güvenlik testleri için doğru varsayılan.
- `MetricsTestCollection`'ın `DisableParallelization = true` ile tanımlanması ve MeterListener sahibi dört sınıfın (`ActivityLogWriterTests`, `CalculationTelemetryTests`, `ExceptionHandlerContractTests`, `ChannelActivityLoggerTests`) tamamına uygulanması; xunit 2.9.2'de bu bayrak koleksiyonu diğer tüm koleksiyonlara karşı da serileştirdiği için process-global sayaç çakışması gerçekten kapanıyor.
- Determinizm düzeltmeleri: `WhatIfCalculatorTests`/`DcaCalculatorTests` ctor'larında `FakeTimeProvider` sabit tarihe kuruluyor ve `DateOnly.FromDateTime(DateTime.UtcNow)` bağımlılıkları (ve ona bağlı koşullu stub kurulumu) tamamen kaldırılıyor — gün-dönümü flake'i yapısal olarak eleniyor.
- Gerçek altyapıya karşı oracle kullanımı: `pg_column_size(...::jsonb)` ile `JsonbStorageSize.UpperBound` karşılaştırması ve `pg_terminate_backend` ile gerçek 57P01 retry senaryosu — mock yerine gerçek PostgreSQL davranışının kanıt olarak kullanılması CLAUDE.md test politikasıyla tam uyumlu.
- Redis admission integration testinde atomikliğin dolaylı ama gerçek kanıtı: reddedilen dört-bucket transaction sonrası `server.Keys(prefix*)` sayısının 6'da kalması ve anahtarların üçüncü exact-IP'yi içermemesi — 'kısmi yazma yok' iddiasını gözlemlenebilir bir yan etkiyle doğruluyor.

**Repo dışı bilgi gerektiren sorular.**

- Kestrel'in `KestrelServerOptions.ListenHandle` yoluyla verilen fd'yi `SafeSocketHandle(..., ownsHandle: true)` ile sahiplenip sahiplenmediği (R13-03) pinlenmiş ASP.NET Core sürümünün `SocketConnectionListener.Bind()` kaynağından teyit edilmeli; sahiplenmiyorsa bulgu düşer, sahipleniyorsa çift-close kesindir.
- `chk_activity_action`'ın EF modelinde bilinçli olarak mı bırakıldığı (ör. gelecekte geri eklenmesi planlanıyor mu, yoksa 023 sırasında güncellenmesi unutuldu mu) — 023'ün ADR/tasarım notunda bu karar yazılı mı?
- Gerçek koşuda `Saydin.Api.Tests` paketinin toplam süresi ve `ActivityLogWriterTests`'in gerçek-saat backoff'larının CI runner yükü altında 5 sn `WaitAsync` sınırına ne kadar yaklaştığı (R13-15 severity'sini netleştirir).
- PostgreSQL'in `'1E+3'::jsonb::text` ve `'1.2300e2'::jsonb::text` çıktısının hedef sürümde (TimescaleDB imajının PG minor'ı) gerçekten `1000` / `123.00` olup olmadığı — R13-10'un unit beklentileri bu davranışa dayanıyor.

---

## R15 — Dokümantasyon, ADR, runbook

**Bulgu:** 0 Critical · 2 High · 5 Medium · 10 Low

**Kapsam.** R15.files listesindeki 30 dosyanın tamamı okundu (CLAUDE.md, README.md, CONTRIBUTING.md, docs/README.md, architecture*.md, cache-strategy.md, ADR-008/ADR-011, decisions/README, deployment/README, development-guide.md, high-traffic-checklist.md, 06-remediation-progress.md ve 24 runbook'un tamamı; beş yeni runbook satır satır). Her iddia karşı tarafından doğrulandı: docker-compose.yml + bootstrap-dev-database.sh + run-local-tests.sh/run-unit-coverage.sh, .github/workflows/ci.yml şema kapısı, infrastructure/postgres/migrations/018 & 022, compose.production.yml + deploy-release.sh + validate-private-material.py, RepairOptions/BootstrapOptions/RoleContract CLI sözleşmeleri, prometheus rules alert adları ve runbook_url'leri, Program.cs handler zinciri, WhatIfCalculator/DcaCalculator cache key literalleri, HttpResilienceExtensions sıralaması. Kapsam dışı bırakılan: docs/analysis/pr-review/** ve pr-review2/** (review girdisi) ile diğer hatlara ait kod/CI dosyaları.

**Güçlü kararlar.**

- Yeni runbook'ların sayısal ve sözleşmesel iddiaları koda birebir doğrulanıyor: backup-login-renewal.md'deki 45–93 gün penceresi deploy-release.sh:244'teki 3888000/8035200 saniyeye, 30 günlük uyarı host-backup.yml:71'deki 2592000'e, restore-drill.md'deki 'observation - 300 saniye rotation budget, en fazla 900 saniye yaş' cümlesi wal-recovery-evidence.py:135-137'ye tam oturuyor. Bu düzeyde doküman-kod hizası nadir.
- scenario-integrity-migration.md, migration 018'in dört preflight koşulunu (jsonb_typeof object/null, octet_length > 8192, dca→quantity_unit='try', kullanıcı başına > 100) SQL düzeyinde birebir yeniden üretiyor ve arşiv/apply akışını SERIALIZABLE + SHARE ROW EXCLUSIVE lock + iki yönlü EXCEPT set karşılaştırması + satır bazlı `to_jsonb(live) IS DISTINCT FROM approved.document` kontrolüyle DELETE öncesi fail-closed yapıyor; yıkıcı adım ikinci operatör onayına ve dış şifreli arşive bağlanmış.
- data-repair.md fiilen çalıştırılabilir: her dosya yolu (release_manifest.py, render-deployment-env.py, validate-production.py, validate-runtime-volume.py, validate-private-material.py), her volume adı (data_repair_secret/input/receipts) ve her CLI bayrağı (--receipt-signer-mode oci-kms-instance-principal, --kms-key-version-id, --kms-timeout-seconds) RepairOptions.cs:72-109 ve compose.production.yml:743-783 ile doğrulandı; dormant profil + `operator-command-required` sentinel bağlaması runbook'un kendi Python kontrolüyle kanıtlanıyor.
- Cache dokümantasyonu gerçek koda göre güncellenmiş: whatif:v4 (WhatIfCalculator.cs:428), dca:v3 (DcaCalculator.cs:147), usage:assets: (AssetsEndpoints.cs:13), security:rate:v1: (DistributedSecurityLimiterOptions.cs:42) ve cashflow_cpi_lkv_terminal_v1 (DcaCalculator.cs:29) — beşi de literal olarak eşleşiyor. Cache sürüm artışının gerekçesi (finansal yöntem değişikliği) de doğru şekilde 'response shape' yerine 'yöntem' olarak yeniden yazılmış.
- architecture.md'deki resilience Mermaid diyagramı, HttpResilienceExtensions.cs:26-77'deki gerçek strateji sırasını (CircuitBreaker → TotalRequestTimeout → Retry → AttemptTimeout) yansıtacak şekilde düzeltilmiş; eski diyagram sırayı ters gösteriyordu. ResponseHeadersRead nedeniyle gövde okumanın attempt timeout dışında kalması ve worker'ın aynı 3 dk'yı mutlak bütçe olarak uygulaması gibi ince davranış da hem CLAUDE.md hem architecture.md'de aynı şekilde belgelenmiş.
- SDK pin'i tek digest'e indirilmiş ve tutarlı: `sdk@sha256:e1ffd2a...` repo genelinde 19 yerde (Dockerfile'lar, CI, CLAUDE.md, CONTRIBUTING, development-guide, docker-compose tests servisi) tek bir değer; eski hareketli `mcr.microsoft.com/dotnet/sdk:10.0` tag'i dokümanlardan temizlenmiş.
- ADR-011 gerçek bir karar kaydı: seçenekler (undocumented beta / auto-latest beta / hemen OTLP geçişi / bounded istisna) gerekçeleriyle listelenmiş, istisnanın geçerli kalması için beş somut kontrol sayılmış, çıkış kanıtı (iki eşdeğer staging koşusu + paketin ve Directory.Packages.props:50'deki yorumun kaldırılması) tanımlanmış ve süresiz erteleme açıkça reddedilmiş.
- Alert→runbook bağı veri düzeyinde eksiksiz: prometheus rules'taki 40 alert'in tamamı `runbook_url` taşıyor ve referans verilen 13 runbook dosyasının hepsi mevcut; check-doc-links.py 88 dosyada 183 yerel linki kırık link olmadan geçiyor. observability-game-day tablosu da eski tek `SaydinIngestionStale` satırını gerçek üç alarma (FreshnessMetricMissing / Daily / Monthly) ayırarak düzeltmiş.

**Repo dışı bilgi gerektiren sorular.**

- Production'da `bootstrap_secret` volümündeki login versiyonlarını v1'in ötesine taşımak için onaylı bir yol var mı? validate-private-material.py'nin exact file-set kuralı bilinçli bir 'v1-only' donması mı, yoksa rotasyon runbook'u yazılırken gözden mi kaçtı? (R15-02'nin doğru düzeltmesi bu karara bağlı.)
- Hedef TimescaleDB 2.16.1 sürümünde `timescaledb_information.hypertable_compression_stats` gerçekten hypertable başına tek satır mı döndürüyor? Canlı bir cluster'da `SELECT count(*) ... WHERE hypertable_name='activity_logs'` çıktısı R15-04'ü kesinleştirir/çürütür.
- docs/runbooks/ altındaki tüm dosyaların İngilizce olması bilinçli bir on-call/dış ekip kararı mı, yoksa tesadüfi mi? Ekibin nöbet dili repo dışı bir bilgi.
- Backup validity için 'non-secret production configuration' fiilen hangi dosya adıyla ve hangi sistemde (git'te mi, operatör host'unda mı) tutuluyor? R15-10'un önerdiği base↔rendered eşitlik kontrolünün nereye ekleneceği buna bağlı.
- `pr-review2/` dizini bu review dalgasının çıktısı mı, yoksa üçüncü bir dalga mı? docs/analysis/README.md'nin nasıl indeksleneceği (R15-12) buna göre değişir.

---

## R16 — Compose, solution, build konfigürasyonu

**Bulgu:** 0 Critical · 1 High · 4 Medium · 5 Low

> ⚠️ Bu hattın doğrulayıcısı oturum limiti nedeniyle koşamadı. Kayıtlar yalnız üreten
> agent'a dayanır; High'ları ana agent elle denetlemiştir.

**Kapsam.** Hattın üç dosyası (`docker-compose.yml`, `Saydin.Services.sln`, `Directory.Packages.props`) tam diff olarak okundu; ayrıca `global.json`, `Directory.Build.props` ve karşı taraflar doğrulandı: `infrastructure/secrets/bootstrap-dev-database.sh`, `.github/scripts/validate-development-compose.{py,sh}`, yeni `.github/scripts/run-local-tests.sh` + `run-development-compose-smoke.sh`, `run-unit-coverage.sh`, `.github/workflows/ci.yml`, `infrastructure/backup/manage_backup_hba.py`, `src/Saydin.DatabaseRoleBootstrap/*` (Runner + DatabaseOperations + BootstrapOptions), `src/Saydin.Api/Services/ActivityPrincipalPseudonymizer.cs`, 27 `packages.lock.json` ve CLAUDE.md/README/development-guide komut blokları. `docker compose config` (default + `--profile "*"`) gerçekten çalıştırıldı, timescaledb imajında `python3` varlığı doğrulandı; ancak tam stack ayağa kaldırılmadı, migration SQL gövdeleri ve backup runtime'ı bu hattın dışında bırakıldı.

**Güçlü kararlar.**

- Önceki review'in Critical'ı gerçekten kök nedende kapatılmış: `secret-source-generator` `backup-v1` üretiyor (docker-compose.yml:34), `secret-materializer` onu 1001:1001 0400 olarak `/out-bootstrap/private/backup-v1`e kuruyor (satır 67), `database-role-bootstrap` hem `--backup-v1-valid-until` hem `--backup-password-file` alıyor (satır 279-297) ve `database-migrator` `SAYDIN_BACKUP_V1_VALID_UNTIL` env'ini taşıyor (satır 325). `docker compose --env-file <env> config` gerçekten çalıştırıldı: exit 0, `extends` ile üretilen post-migration servisinin çözülmüş command'inde her iki backup argümanı da doğru sırada mevcut.
- Semptom örtmek yerine eksik olan kontrol düzlemi adımı eklenmiş: pre-bootstrap → migrator → `database-role-bootstrap-post-migration` sıralaması, 022 terminal olmadan backup rolünün materyalize edilemeyeceği gerçeğini doğru modelliyor ve `IsBackupPhaseReadyAsync` (RoleBootstrapDatabaseOperations.cs:530+) fresh DB'de zarif şekilde `false` dönüyor — yani ilk koşu kırılmıyor.
- İddia CI'da davranışsal olarak kanıtlanıyor: `.github/workflows/ci.yml:113` her koşuda `run-development-compose-smoke.sh` çalıştırıyor; script izole `COMPOSE_PROJECT_NAME`, `SAYDIN_*_PORT=0`, tam container/volume/network/image residual doğrulaması ve `backup_postbootstrap_required=false` çıktısı üzerinde grep ile fresh-stack + idempotent verify-only kanıtı üretiyor. Statik doğrulama tek başına bırakılmamış.
- `validate-development-compose.py` sadece pozitif kontrol değil, 12 yeni *mutasyon* fixture'ı ile kapının gerçekten kapandığını kanıtlıyor (`missing_post_bootstrap`, `post_bootstrap_bypasses_hba`, `api_bypasses_post_bootstrap`, `broad_backup_hba`, `broad_local_test_scope`, ...) — testi iddiaya uydurma değil, kapıyı mutasyona karşı sertleştirme yaklaşımı.
- Tüm downstream tüketiciler (`saydin-api`, `saydin-price-ingestion`, `postgres-exporter`, `calendar-release`, `pgadmin`, `tests`) tek tek `database-role-bootstrap-post-migration: service_completed_successfully`e taşınmış ve bu, `POST_BOOTSTRAP_CONSUMERS` seti üzerinden bypass mutasyonuyla kilitlenmiş; hiçbir servis eski `database-migrator` kapısında unutulmamış (docker compose config ile doğrulandı).
- Solution envanteri artık tam: `Saydin.DatabaseRoleBootstrap`, `Saydin.DatabaseSecurity` ve iki test projesi eklenmiş; diskteki 24 `.csproj` ile `Saydin.Services.sln` içeriği birebir eşleşiyor (diff boş), GUID çakışması yok, projeler doğru solution klasörlerine yerleştirilmiş.
- Merkezi paket disiplini sağlam: 27 `packages.lock.json`'ın tamamı `Directory.Packages.props` sürümleriyle birebir uyumlu (script ile doğrulandı), lock dosyası olmayan proje yok, floating sürüm yok ve tek prerelease istisnası (`OpenTelemetry.Exporter.Prometheus.AspNetCore 1.15.3-beta.1`) hem inline yorumla hem ADR-011 ile çıkış kanıtı tanımlanarak gerekçelendirilmiş.
- Yeni `database-backup-hba` servisi minimum yetkiyle tasarlanmış: `user: 70:70`, `read_only: true`, `cap_drop: [ALL]`, `no-new-privileges`, `pids_limit: 64`, tmpfs `/tmp`, Docker socket yok; subnet container içinden `/proc/net/route`'tan türetiliyor ve `manage_backup_hba.py` install→reload→verify sırası ile HBA sözleşmesi kurulduktan sonra ayrıca doğrulanıyor.
- `bootstrap-dev-database.sh` artık `--env-file .env --env-file .env.database-runtime` ikilisini hem doğrulama adımında hem de bastığı kullanım talimatında kullanıyor; Compose'un `--env-file` verildiğinde `.env`'i otomatik yüklemeyi bırakması tuzağı doğru şekilde kapatılmış.

**Repo dışı bilgi gerektiren sorular.**

- `docs/runbooks/backup-login-renewal.md` production kabul aralığını "45 ila 93 gün" olarak tanımlıyor; kod tarafındaki `ValidateNewBackupValidityAsync` sınırı ise [now+24h, now+93d] (RoleBootstrapDatabaseOperations.cs:878). 45 günlük alt sınırı uygulayan production admission kontrolünün nerede olduğu (deploy-release.sh / imzalı manifest) bu hattın dosya kapsamı dışında kaldı — repo dışı deployment yapılandırmasıyla teyit edilmeli.
- `database-backup-hba`'nın subnet keşfi, ekibin fiilen kullandığı Docker Desktop / Colima / Linux dağıtımlarında her zaman tek bir `/16-/28` private eşleşme üretiyor mu? CI (ubuntu-latest) kanıtı var; geliştirici makinelerinde özel `default-address-pool` veya ek network kullanımı olup olmadığı repo'dan görülemiyor.
- `docker compose run --rm tests` komutunun ekipte fiilen nasıl çağrıldığı (CLAUDE.md formuyla mı, development-guide formuyla mı) bilinmiyor; R16-01'in gerçek etkisi bu kullanım alışkanlığına bağlı.

---

## R17 — REMEDIATION DENETİMİ

**Bulgu:** 0 Critical · 1 High · 6 Medium · 4 Low

**Kapsam.** `docs/analysis/pr-review/01-findings-critical-high.md` içindeki 16 bulgunun her biri için hem düzeltme kodunu hem karşı tarafını (endpoint↔service↔repository↔SQL fonksiyonu, compose↔validator↔CI, script↔workflow, alert↔rules.test/inventory.test, test↔ratchet) okudum; `07-remediation-progress.md`'nin "Verified" iddialarını bu kanıtla karşılaştırdım. Okunanlar: docker-compose.yml + validate-development-compose.py, deploy-release.sh + release_manifest.py/render-deployment-env.py/validate-release.py, ApiPortBoundaryMiddleware + Caddyfile + iki-port Kestrel testi, EndpointExtensions/DistributedSecurityLimiter(+Options/appsettings), DcaCalculator + testleri, ActivityLogBatchStore/Writer + classifier testleri, RoleContract/RoleBootstrapDatabaseOperations + backup-login-renewal runbook + host-backup alertleri, BaseAssetWorker/IngestionWindowRepository/IngestionFreshnessTelemetry, HttpResilienceExtensions, prometheus rules+testleri, restore-drill.sh + backup-static-self-test.py, backup-entrypoint.sh staging kapıları, ingestion fixture/CI schema gate, DataRepair guard test matrisi, 021/023/024 migration'ları ve InstallationRepository. Okunmayan: tam test/CI koşusu (lokal .NET yok — davranış statik olarak doğrulandı), calendar-data ve DQA'nın bu hatta ait olmayan bölümleri.

**Güçlü kararlar.**

- PR1 bulgusu #1 (kök compose role-bootstrap backup argümanları) → **FIXED** ve sınıfı kapatılmış: `docker-compose.yml:36,67,282-297,325` secret üretim/mount/argüman zincirini tamamlıyor, `validate-development-compose.py:94-102` argüman sözleşmesini statik kapıya bağlıyor ve satır 180-183'teki mutation fixture'ları (`missing_backup_argument`, `missing_post_bootstrap`, `post_bootstrap_bypasses_migrator/hba`) kapının gerçekten reddettiğini kanıtlıyor — semptom değil kök neden kapanmış.
- PR1 bulgusu #2 (deploy-release.sh KeyError) → **FIXED** ve tek-kaynak ilkesiyle: inline `runtime` sözlüğü tamamen kaldırılıp `render-deployment-env.py --verify-existing`'e devredildi; `release_manifest.py:31 EXPECTED_RUNTIME_IMAGES = tuple(sorted(RUNTIME_IMAGE_ENV_KEYS))` iki eşlemeyi tek kaynaktan türetiyor ve `validate-release.py:117` sözlüğün geri gelmesini (`runtime={` / `RUNTIME_IMAGE_ENV_KEYS` metni) statik olarak yasaklıyor.
- PR1 bulgusu #3 (management port trailing-slash bypass) → **FIXED** ve savunma derinliğiyle: `ApiPortBoundaryMiddleware.NormalizePath` segment tabanlı normalizasyon + OrdinalIgnoreCase karşılaştırma yapıyor, `ApiPortEndpointSelectorPolicy` + `ApiEndpointSurfaceMetadata` ile sınıflandırma hatası endpoint eşleşmesini etkileyemiyor, Caddyfile `path_regexp` ile `//metrics//`-tipi varyantları da kapatıyor ve `ApiManagementBoundaryHttpTests` gerçek iki Kestrel listener'ı üzerinde `/HEALTH/READY/`, `//health//ready//`, `/METRICS/`, `//metrics//` ile birlikte X-Forwarded-Host/Port spoof senaryolarını da kilitliyor.
- PR1 bulgusu #7 (backup login VALID UNTIL kilidi) → **FIXED**: `RoleBootstrapDatabaseOperations.cs:195-230` forward-only `ALTER ROLE ... VALID UNTIL` uzatma yolunu marker güncellemesiyle birlikte açıyor, satır 219-220 geriye alma girişimini `backup_valid_until_regression` ile reddediyor; `saydin_backup_login_valid_until_timestamp_seconds` metriği + `host-backup.yml:62-80`'deki missing/expiring/expired alert üçlüsü + `rules.test.yml:111-159`'daki üç promtool senaryosu + `docs/runbooks/backup-login-renewal.md` ile prosedür, alarm ve test birlikte geldi.
- PR1 bulgusu #10 (provider gövde timeout'u) → **FIXED** ve iki katmanlı: `HttpResilienceExtensions.cs:45-47` 3 dk `TotalRequestTimeout`'u pipeline'a geri koydu, ayrıca `BaseAssetWorker.WithLeaseRenewalAsync` (satır 384-428) adapter çağrısını `ProviderDeadline` ile mutlak bütçeye bağlayıp aşımda durable `RetryableFailure("provider_deadline")` üretiyor ve askıda kalan task'ı `ObserveDetachedAsync` ile sızdırmadan gözlemliyor — CLAUDE.md'nin timeout sözleşmesi tekrar geçerli.
- PR1 bulgusu #11 (SaydinActivityLogLoss ölü alert) → **FIXED** ve testle kilitlenmiş: `api.yml:42` önerilen `sum(increase({__name__=~...}[10m])) > 0` formuna geçti; `inventory.test.yml:18-40` tek sayacın (yalnız `queue_drops`) arttığı pozitif senaryoyu ve satır 283'teki sabit seri ile hiçbir sayacın artmadığı negatif senaryoyu birlikte doğruluyor — bulgu raporundaki tam öneri uygulanmış.
- PR1 bulguları #8 ve #9 (permanent window izolasyonu + next_attempt_at) → **FIXED**: `BaseAssetWorker.BackfillAsync` asset listesini `OrderBy(Symbol, Ordinal).ThenBy(Id)` ile deterministik sıralıyor, permanent scope artık süreci düşürmeden `DrainResult.PermanentBlocked` ile izole ediliyor ve sibling'lar devam ediyor; `WorkerPass`/`GetDelayUntilNextRun` (satır 372-382) uyanmayı `min(en yakın next_attempt_at, sonraki planlı koşu)` semantiğine çevirdi. Freshness sorgusundaki source başına `min(last_success_at)` sayesinde izole edilen asset yine de staleness alarmı üretiyor.
- PR1 bulgusu #16 (DataRepair guard test boşluğu) → **FIXED** ve ratchet yükseltilmiş: `RepairGuardIntegrationTests.cs` gerçek PG üzerinde bulgunun (a)-(g) önerilerinin tamamını karşılıyor (`repair_window_missing`, `repair_running_job_rejected`, `repair_newer_terminal_window_rejected`, `repair_guard_row_budget_exceeded`, `repair_guard_changed_inside_transaction`, `repair_cas_failed`, `repair_target_lock_lost`, tamperlanmış final receipt); integration test sayısı 7'den 27'ye çıkmış ve CI `--minimum-executed` 7'den 32'ye yükseltilmiş.
- Coverage kapıları gevşetilmemiş: `coverage-thresholds.json` / `-unit.json` global `overall` eşikleri `f9f608d` ile birebir aynı kalmış, yalnız `Saydin.Api.Services` için yeni critical-namespace eşiği eklenmiş — remediation sırasında ratchet düşürme (kapı gevşetme) yapılmamış.

**Repo dışı bilgi gerektiren sorular.**

- `07-remediation-progress.md`'de referans verilen merkezi gerçek-infrastructure koşusu `eb03bf08631d4517841b5faba65c203a` ve TRX sayıları (CalendarData 92, API integration 66, Ingestion ledger 44, DQA 96/106, DataRepair 32, RoleBootstrap 98/13, Migrator 184) repo içinden doğrulanamıyor — bu koşuya ait TRX/coverage artefaktları arşivlenmiş mi ve hangi commit'e karşı çalıştırıldı?
- Production PostgreSQL'de `installation_credentials` tablosunun beklenen büyüklüğü ve p95 auth latency hedefi nedir? R17-01'in etkisini severity olarak kesinleştirmek için mevcut/öngörülen installation sayısı gerekiyor.
- Türk mobil operatörlerinde tek bir CGNAT public IP'yi kaç abone paylaşıyor? R17-03'teki `RegistrationExactDailyLimit=5` ve `CalculationNetworkDailyLimit=500` değerlerinin meşru kullanıcıları ne oranda etkileyeceği bu veriye bağlı.
- CI runner'larında (GitHub-hosted ubuntu-latest dışında self-hosted kullanılıyor mu?) Docker daemon her zaman erişilebilir mi? R17-04'ün gerçek tetiklenme olasılığı buna bağlı.
- `installation_verifier_matches` PL/pgSQL karşılaştırıcısını getiren güvenlik kararının arkasında somut bir tehdit modeli/pentest bulgusu var mı, yoksa savunma derinliği amaçlı mı eklendi? R17-01'in önerdiği hibrit (index eşitliği + tek satır üzerinde constant-time doğrulama) bu tehdit modelini karşılıyor mu?

---

## R18 — ÜRÜN VE GELİŞTİRİCİ DENEYİMİ

**Bulgu:** 0 Critical · 1 High · 11 Medium · 10 Low

> ⚠️ Bu hattın doğrulayıcısı oturum limiti nedeniyle koşamadı. Kayıtlar yalnız üreten
> agent'a dayanır; High'ları ana agent elle denetlemiştir.

**Kapsam.** Diff'in tamamına ürün/DX gözüyle çapraz baktım: API endpoint'leri ve error contract (Endpoints/, Exceptions/, Security/, her iki resx), hesaplama servisleri (DcaCalculator, WhatIfCalculator, CalculationTelemetry), gözlemlenebilirlik (SaydinMetrics, prometheus rules + inventory.test.yml, validate-prometheus-runtime.py), operatör araçları (DataRepair/DQA CLI ve README'leri), yeni ve değişen script'ler (.github/scripts/*, infrastructure/**/self-test|smoke), onboarding dokümanları (CLAUDE.md, CONTRIBUTING.md, README.md, development-guide.md, cache-strategy.md, ADR-008, yeni beş runbook) ve OpenAPI/exception sözleşme testleri. Compose komut iddiasını yerelde `docker compose config` ile ampirik doğruladım. Derinlemesine okumadıklarım: migration 023/024 SQL gövdeleri (yalnız başlık/doküman uyumu), backup/restore entrypoint shell mantığı, release imzalama/manifest zinciri ve calendar-data acquisition iç akışı — bunlar diğer hatların kapsamında.

**Güçlü kararlar.**

- Lokalizasyon eksiksiz ve simetrik: TR ve EN resx anahtar setleri birebir aynı (88/88, `diff` ile doğrulandı) ve bu diff'te eklenen her yeni hata ailesi hem başlık hem detay anahtarı almış (`SecurityLimiterUnavailable`, `QuotaUnavailable`, `RouteNotFound`, `CalculationEndDateCannotBeInFuture`). Hardcoded İngilizce başlıklar (`"Quota service unavailable."`, `"Too many requests."`) middleware ve handler'lardan tamamen kaldırılmış.
- Hata kodları merkezileşti: `QuotaUnavailableException.ErrorCode` artık `ApiErrorCodes.QuotaUnavailable`'a delege ediyor ve limiter middleware'indeki dört kopyalanmış string literal (`"security_rate_limited"`, `"security_limiter_unavailable"` ve type URI'ları) `ApiErrorCodes` + `SecurityAdmissionProblem` altında tek kaynağa çekilmiş.
- `SecurityAdmissionTelemetry` gizliliği yapısal olarak garanti ediyor: `bucket`/`outcome`/`reason` üçlüsü sabit bir allowlist'e karşı doğrulanıyor, dolayısıyla IP, ağ pseudonym'i, principal id veya Redis key'inin Prometheus label'ına sızması kod düzeyinde imkânsız — yorum satırıyla değil, kontrolle.
- `ProviderValueParser` beş mapper'a dağılmış invariant finansal parse mantığını tek yerde topladı ve binlik ayracı/parantez kabulünün neden reddedildiğini (`locale-formatted value into a different amount`) kodun içinde açıkladı — CLAUDE.md'nin finansal hassasiyet kuralının doğru soyutlanmış hâli.
- Dokümantasyon kodla aynı adımda ilerledi: `dca:v2 → dca:v3` cache namespace bump'ı `docs/cache-strategy.md`'ye, `cashflow_cpi_lkv_terminal_v1` yöntem token'ı `docs/architecture.md`'ye, migration 023/024 satırları `docs/architecture/database-schema.md`'ye işlendi; ADR-008 durumu güncellendi ve beş yeni runbook'un tamamı `docs/runbooks/README.md`'den linklendi (link denetimi ile doğruladım).
- `backup-static-self-test.py`'ye eklenen `docker_commands()` ve `function_body()` çıkarıcıları shell script sözleşmelerini grep yerine yapılandırılmış, assert edilebilir nesnelere çeviriyor — kırılgan metin eşlemesinden davranış sözleşmesine anlamlı bir yükseltme.
- `bootstrap-dev-database.sh` artık hem `.env` hem `.env.database-runtime` ile `compose config` doğrulaması yapıyor ve çalışan tam komutu (`docker compose --env-file .env --env-file .env.database-runtime up --build`) ekrana basıyor — iki-env-file gereksiniminin doğru ele alındığı tek yer.
- DCA'da eksik fiyat artık sessizce düşmüyor: atlanan günler `SkippedPurchaseDates` ile ve `purchase_price_unavailable` warning'iyle makine-okunur biçimde bildiriliyor; ayrıca DCA hesaplaması WhatIf ile aynı seviyede (`DcaCalculations` + `DcaCalculationDuration`) ölçülür hale geldi.

**Repo dışı bilgi gerektiren sorular.**

- Saydın meta repo `docs/architecture/api-contract.md` bu diff'le birlikte güncellendi mi? Bu değişiklik seti en az üç sözleşme değişikliği içeriyor: (a) `DcaResponse.SkippedPurchaseDates` + `purchase_price_unavailable` warning'i, (b) `RealReturnMethod` değerinin `cashflow_cpi_terminal_v1` → `cashflow_cpi_lkv_terminal_v1` olarak değişmesi (istemci bu değere göre dallanıyorsa breaking), (c) yeni `security_rate_limited`/`security_limiter_unavailable`/`quota_unavailable`/`route_not_found` hata kodları. Bu repo dışında olduğu için doğrulayamadım.
- Türk mobil operatörlerinin (Turkcell/Vodafone/Türk Telekom) CGNAT egress bloklarında tek bir /24 arkasında kaç eşzamanlı abone bulunuyor? R18-10'daki `CalculationNetworkDailyLimit=500` ve `RegistrationNetworkDailyLimit=100` değerlerinin gerçek risk büyüklüğünü ölçmek için üretim/beta telemetrisi veya operatör IP tahsis verisi gerekiyor.
- Flutter istemcisi bugün `ProblemDetails.extensions.field` değerini nasıl tüketiyor? R18-03'teki PascalCase/camelCase karışımı zaten elle bir eşleme tablosuyla mı çözülmüş, yoksa alan hiç kullanılmıyor mu? Yanıt, düzeltmenin breaking olup olmadığını belirliyor.
- `docs/analysis/pr-review/07-remediation-progress.md`'nin 'açık kod, test, CI, doküman veya statik-konfigürasyon kusuru kalmadı' iddiası hangi kanıt setine dayanıyor — bu hattın bulgularının (özellikle R18-01 kırık Compose komutları ve R18-02 kaybolan OpenAPI parametreleri) hiçbir mekanik kapı tarafından yakalanmamış olması, iddianın kapsamının kapı-tabanlı mı yoksa manuel mi olduğunu netleştirmeyi gerektiriyor.

---

## R14a — PriceIngestion + calendar test kalitesi

**Bulgu:** 0 Critical · 1 High · 8 Medium · 8 Low

**Kapsam.** R14a dosya listesindeki tüm test dosyaları okundu: `tests/Saydin.PriceIngestion.Tests/**` (Workers, Adapters, HttpResilienceExtensionsTests), `tests/Saydin.PriceIngestion.IntegrationTests/**` (fixture, window repo, write fence, authority migration, worker ledger, freshness hydration) ve `tools/calendar-data/tests/**` (dört yeni dosya dahil). İddiaları doğrulamak için karşı taraf da okundu: `BaseAssetWorker.cs`, `EvdsInflationWorker.cs`, `IngestionOrchestrator.cs`, `IngestionFreshnessHydrationService.cs`, `HttpResilienceExtensions.cs`, `ProviderPayload.cs`, `ProviderValueParser.cs`, `IngestionWindowRepository.cs`, `CalendarPlanMaterializer.cs`, `CalendarDataGenerator.cs`, `CalendarAcquisition.cs`, `infrastructure/calendar/verify-candidate.sh`, `.github/scripts/run-calendar-data-tests.sh`, `.github/scripts/run-ingestion-ledger-tests.sh`, `.github/workflows/ci.yml` TRX kapıları ve `07-remediation-progress.md`. Testler çalıştırılmadı (lokal .NET yok, salt-okunur review); xunit dinamik-skip davranışı NuGet cache'indeki assembly'ler üzerinden statik olarak doğrulandı.

**Güçlü kararlar.**

- Provider deadline testi gerçek fault injection yapıyor: `StalledReadStream` ile gövdeyi süresiz askıda tutan bir `HttpContent` kurulup `HttpCompletionOption.ResponseHeadersRead` sonrası stall senaryosu üretiliyor; `CancellationObserved` TCS'i ile iptalin gerçekten stream'e ulaştığı da doğrulanıyor. Bu, 10 numaralı High'ın kök nedenine (gövde okuma attempt-timeout dışında) doğrudan isabet eden birinci sınıf bir test.
- Ledger `next_attempt_at` sözleşmesi gerçekten davranışsal olarak test edilmiş: `LedgerDueTime_WakesAtFiveMinutes_WithoutStarvingOrderedSibling` (NotDue ve Busy için Theory) ve `LedgerNotDue_WakesAtExactThirtyMinuteRetryInsteadOfMonthlySchedule`, `RunAsync` üzerinden FakeTimeProvider ile 4dk59sn/29dk59sn sınırını ve deterministik asset sıralamasını (A-FIRST önce) birlikte kanıtlıyor — 9 numaralı High'ın hem gecikme hem starvation boyutunu kapsıyor.
- Write fence integration testi sahte-guard'dan gerçek veritabanı davranışına taşınmış: `LegacyRepositories_RejectTokenlessPriceAndInflationWrites` yerine `RuntimeIngestionRole_RealDatabaseFenceRejectsTokenlessPriceAndInflationWrites`, gerçek ingestion login'iyle authority window'u sağlayıp lease token'ı vermeyerek PostgreSQL trigger'ının `InsufficientPrivilege` döndüğünü doğruluyor. Mock politikası kuralına (DB testlerinde mock yasak) tam uyum.
- Advisory-lock claim yarışı belirsiz `Task.WhenAll` yarışından deterministik fault injection'a çevrilmiş: `BlockingClaimFault.BeforeClaimCommitAsync` ilk transaction'ı açık tutarken ikinci replica'nın `Busy` aldığı kanıtlanıyor ve `finally { fault.Release(); }` ile assertion hatasında bile transaction strand edilmiyor.
- `PriceAuthorityMigrationIntegrationTests` içindeki `count(*)=23` sabiti, sayıya değil invariant'a dayanan `NOT EXISTS (... state NOT IN ('succeeded','skipped_optional'))` + hedeflenen versiyonun checksum'ı biçimine çevrilmiş — High #15'in doğru şekilde kök nedeninden kapatılması.
- Provider payload sınırı hem `Content-Length` hem chunked yol için test edilmiş: `ProviderPayloadTests.Chunked64KiBPlusOne_IsRejectedWithoutContentLength`, `TryComputeLength=false` dönen özel bir `HttpContent` ile header'sız akışta da 64 KiB sınırının uygulandığını kanıtlıyor.
- Locale/thousands formatlı finansal değerlerin fail-closed reddi üç mapper'da (TCMB, TwelveData, EVDS) aynı `[InlineData("2115,19")] [InlineData("2.115,19")] [InlineData("(30.5)")]` Theory setiyle simetrik olarak test edilmiş; `ProviderValueParser.FinancialNumberStyles`'ın `AllowThousands`/parantez kabul etmemesi kasıtlı ve yorumla gerekçelendirilmiş.
- `.github/scripts/run-calendar-data-tests.sh` ve `run-ingestion-ledger-tests.sh` `dotnet restore --force-evaluate`'ten `--locked-mode`'a geçirilmiş; required test koşusu artık lock dosyasını sessizce yeniden yazamıyor (tedarik zinciri determinizmi).

**Repo dışı bilgi gerektiren sorular.**

- `07-remediation-progress.md`'deki otoritatif koşu (`eb03bf08631d4517841b5faba65c203a`) bu çalışma ağacının tam hâliyle mi alındı? Calendar TRX'i 92/92/92 ve 0 skipped raporluyor; `VerifyCandidateBehaviorTests` 5 Theory case'i eklediğine göre bu sayının bu ağaçtan üretildiği ancak CI çıktısına erişilerek doğrulanabilir.
- TCMB aylık arşiv sayfası (`https://www.tcmb.gov.tr/kurlar/YYYYMM/Mon_tr.html`) publication linkini aynı gün mü yayınlıyor, yoksa bir iş günü gecikme var mı? R14a-03'teki hafta sonu bypass senaryosunun gerçekçi tetiklenme sıklığı bu operasyonel bilgiye bağlı.
- Calendar candidate promotion akışı üretimde hangi kullanıcı/uid ile koşuyor? `verify-candidate.sh` `SAYDIN_CALENDAR_RUNTIME_UID` varsayılanı 1001; testler bunu runner uid'siyle override ettiği için gerçek operatör ortamındaki owner eşleşmesi repo içinden doğrulanamıyor.

---

## R14b — Migrator/RoleBootstrap/DQA/DataRepair test kalitesi

**Bulgu:** 0 Critical · 1 High · 3 Medium · 12 Low

**Kapsam.** R14b dosya listesindeki 44 dosyanın tamamı okundu (7 yeni test dosyası satır satır; değişenler `git diff` ile; silinen iki DQA contract testi `git show f9f608d:` ile). İddiaları doğrulamak için hat dışı karşı taraflar da okundu: `src/Saydin.DataRepair/{DqaEvidenceVerifier,RepairExecutor,CanonicalJson,Program,ReceiptStore,RepairDatabase}.cs`, `src/Saydin.DataQualityAudit/{CanonicalJson,AuditAccumulator}.cs`, `src/Saydin.DatabaseSecurity/{PostgresScramSha256Verifier,SecureSecretFile,LinuxSecretFile}.cs`, `src/Saydin.DatabaseRoleBootstrap/RoleBootstrapRunner.cs`, `.github/workflows/ci.yml`, `.github/scripts/{run-unit-coverage.sh,verify-integration-trx.py,run-migrator-tests.sh}`, `.github/compose.integration.yml`, `docker-compose.yml`, `Directory.Packages.props` + lock dosyaları ve `docs/analysis/pr-review/{03,07}`. Testler çalıştırılmadı (lokal .NET yok, salt-okunur review); bulgular kod/script/lock okumasına ve xunit 2.9.2 assembly'sinin sembol denetimine dayanıyor.

**Güçlü kararlar.**

- Silinen iki DQA 'contract' testinin (`ApiTrustAuditContractTests`, `PrincipalRetentionAuditContractTests`) kapsamı gerçekten kaybolmadı: substring self-assertion'lar yerine gerçek-PG'de fonksiyon/trigger mutasyonu enjekte eden `EveryAuditedFunctionMutation_IsDetectedAndRestored` (13 vaka) ve `EveryAuditedTriggerMutation_IsDetectedAndRestored` (6 vaka) geldi; ACL/FK/compression drift'i ise zaten mevcut `ApiTrustSchemaFunctionTriggerAndAclDrift` (9 vaka) ve `PrincipalRetentionFunctionTriggerFkAndAclDrift` (7 vaka) theory'lerinde gerçek-PG ile kapsanıyor. Bu, semptom örtme değil gerçek bir yükseltme.
- L143'ün kökü doğru kapatıldı: `AuditDatabaseFixture.CapturePinnedConstraintsAsync` artık constraint tanımlarını `pg_get_constraintdef` ile canlı katalogdan yakalıyor; elle transkripsiyon kaynaklı yanıltıcı kırmızı riski ortadan kalktı.
- Acceptance assertion'ları ham JSON substring'inden tipli sözleşmeye taşındı: artık `check.CheckId` + `check.Severity` + `sample.ViolationCode` üçlüsü ve preflight/budget redlerinde `code=<exact>` doğrulanıyor; redaksiyon testi de tek dosya yerine bundle'daki tüm `.sig` olmayan dosyaları tarıyor.
- `run-isolated.sh` gerçekten fail-closed hale getirildi: `TEST_FILTER` artık `test_filter_forbidden` ile reddediliyor, TRX üretiliyor ve `verify-integration-trx.py --minimum-executed 32` ile kapı konuyor; ayrıca `readonly` atamaları SC2155'e uygun ikiye bölünmüş ve cleanup trap'i build'den önce kurulmuş.
- `run-unit-coverage.sh`'taki kırılgan `grep -Eo 'total="..."'` ratchet'i, integration tarafıyla aynı `verify-integration-trx.py` zero-skip kapısına birleştirildi ve proje envanteri `find` ile diff'lenerek 'yeni test projesi sessizce kapının dışında kalır' sınıfı kapatıldı.
- L135 ve L138 tam olarak kapatıldı: privilege-separation drift theory'si artık `code=schema_fingerprint_mismatch` ve control-state mutasyonsuzluğunu assert ediyor, `pg_parameter_acl` vakası (L133) eklendi; child-process migrator testi sabit `bin/Release/net10.0` yerine `AppContext.BaseDirectory` kullanıyor.
- `RepairGuardIntegrationTests` gerçek negatif matrisi kurdu: transaction ortasında checkpoint fault enjeksiyonuyla `repair_cas_failed`, `repair_guard_changed_inside_transaction`, `rollback_preimage_restore_failed`, `receipt_publish_after_commit_failed` + pending reconciliation ve lease kaybı (`pg_terminate_backend`) yolları exact reject kodu ve DB son durumu ile doğrulanıyor.
- `RoleCredentialLifecycleIntegrationTests` client-side SCRAM verifier'ı için gerçek bir dış oracle kuruyor: verifier client'ta hesaplanıp PostgreSQL'e yazılıyor, ardından aynı parolayla gerçek auth yapılıyor; ayrıca `pg_authid.rolpassword` ve `pg_stat_activity.query` içinde plaintext aranarak sızıntı yokluğu kanıtlanıyor. Bu sayede unit vektör tautoloji değil, regresyon pini oluyor.

**Repo dışı bilgi gerektiren sorular.**

- GitHub-hosted `ubuntu-latest` runner'ında bu commit'le `build-and-test` job'ı gerçekten kırmızıya düşüyor mu? Bulgu R14b-01 statik analize dayanıyor; `07-remediation-progress.md`'de atıf verilen `eb03bf08631d4517841b5faba65c203a` numaralı otoritatif koşunun hangi bağlamda (root Docker mu, çıplak runner mı) yapıldığı ve o koşuda `run-unit-coverage.sh` adımının yer alıp almadığı repo dışı bilgi.
- Bootstrap secret dosyalarına yazılan üretim parolaları kurumsal olarak ASCII graphic ile mi sınırlı üretiliyor (parola üreticisi/vault politikası)? R14b-05'in gerçekleşme olasılığı buna bağlı; repoda yazılı bir parola karakter politikası bulunamadı.
- `Xunit.Sdk.SkipException` ile dinamik skip, kullanılan `xunit.runner.visualstudio 2.8.2` + `Microsoft.NET.Test.Sdk 17.12.0` kombinasyonunda TRX'e `outcome="NotExecuted"` olarak mı yansıyor, yoksa `Skipped` olarak sayaç dışında mı kalıyor? Kesin davranış runner sürümüne bağlı ve lokalde .NET olmadığı için doğrulanamadı (her iki durumda da `--minimum-executed 98` kapısı düşer).

---
