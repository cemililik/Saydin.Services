# Doğrulanmış Medium Bulgular

> 56 bulgu. Tümü bağımsız doğrulayıcı agent tarafından kod okunarak `CONFIRMED` veya
> `PLAUSIBLE` işaretlendi; reddedilenler [`05-lane-summaries.md`](05-lane-summaries.md) içindedir.

## Hat bazlı dağılım

| Hat | Kapsam | Medium |
|---|---|---:|
| L01 | API kimlik ve güvenlik yüzeyi | 3 |
| L02 | Scenario payload/pagination | 1 |
| L03 | Finansal hesaplama, cache, kota | 1 |
| L04 | API runtime ve activity logging | 2 |
| L05 | Shared entity ↔ SQL şema paritesi | 1 |
| L06 | SQL migration 015–022 | 1 |
| L07 | Saydin.DatabaseMigrator | 1 |
| L08 | RoleBootstrap + DatabaseSecurity | 2 |
| L09 | Ingestion ledger ve write fence | 4 |
| L10 | Provider adapter/mapper | 4 |
| L11 | Saydin.DataQualityAudit | 1 |
| L12 | Saydin.DataRepair | 2 |
| L13 | calendar-data ve calendar infra | 4 |
| L15 | Production deployment ve observability | 6 |
| L16 | Backup/restore ve supply chain | 5 |
| L17 | Build/compose/paketleme | 2 |
| L18a | Saydin.Api test kalitesi | 4 |
| L18c | Migrator/RoleBootstrap test kalitesi | 3 |
| L18e | DQA/DataRepair test kalitesi | 2 |
| L19 | Dokümantasyon, ADR, runbook | 7 |

---

### 1. Limiter 429/503 yanıtlarında hardcoded İngilizce metin — IStringLocalizer zorunluluğu ihlali

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L01 — API kimlik ve güvenlik yüzeyi |
| **Kategori** | correctness |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Endpoints/EndpointExtensions.cs:64-69, 79-84, 138-151; src/Saydin.Api/Security/DistributedSecurityLimiterMiddleware.cs:48-53, 65-70` |

**Bulgu.** Varsayılan olarak açık olan dağıtık limiter'ın 429 ve 503 ProblemDetails başlıkları iki dosyada hardcoded İngilizce'dir ve `IStringLocalizer<ErrorMessages>` üzerinden geçmez; mevcut TR/EN `RateLimited`/`RateLimitedDetail` kaynak anahtarları artık üretim kodundan hiç referanslanmadığı hâlde localization testi yalnız varlıklarını doğruladığı için bu regresyonu yakalamaz.

**Etki.** Türk kullanıcı tabanına yönelik uygulamada en sık görülecek iki hata (429/503) `Accept-Language: tr` gönderilse bile İngilizce döner; ayrıca mimari sözleşme ihlali ve localization test kapısının yanlış güvence vermesi. Detail alanı hiç doldurulmadığı için kullanıcıya eylem bilgisi de verilmiyor.

**Öneri.** `SecurityLimiterProblem` ve `DistributedSecurityLimiterMiddleware.WriteProblemAsync` çağrılarını localizer üzerinden besle; mevcut `RateLimited`/`RateLimitedDetail` anahtarlarını kullan, 503 için `SecurityLimiterUnavailable`/`...Detail` anahtarlarını TR/EN resx'e ekle. Localization testini 'anahtar var mı' yerine HTTP seviyesinde `Accept-Language: tr` ile 429 gövdesinin Türkçe olduğunu doğrulayacak şekilde mühürle.

---

### 2. Security admission kararları için ayrık metrik/log nedeni yok — iki farklı kök neden ayırt edilemez 503 üretiyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L01 — API kimlik ve güvenlik yüzeyi |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Security/DistributedSecurityLimiterMiddleware.cs:26-35, 60-71; src/Saydin.Api/Endpoints/EndpointExtensions.cs:72-85; src/Saydin.Shared/Diagnostics/SaydinMetrics.cs; docs/decisions/ADR-003-rate-limiting.md:41-42` |

**Bulgu.** 429/503 oranı genel ASP.NET Core HTTP metrikleriyle görülebilir; ancak 'güvenilmeyen/tüketilmemiş X-Forwarded-For' ile 'Redis kararı başarısız' kök nedenleri hem logda hem gövdede birebir aynı sabit kodla temsil edildiğinden ayırt edilemez ve ADR-003'ün istediği neden-bazlı availability alarmı kurulamaz.

**Etki.** Tüm public isteklerin 503'e düştüğü tam-kapanma sınıfı bir arızada nöbetçi ekip Redis'i sağlıklı görüp yanlış kök nedene yönlenir; SAYDIN_PROXY_NETWORK_CIDR drift'i veya kendi X-Forwarded-For'unu ekleyen istemciler kalıcı 503 alır ve bunun hacmi ölçülemez.

**Öneri.** SaydinMetrics'te düşük kardinaliteli bir Counter tanımla (ör. `saydin.security.admission.decisions.total{bucket,outcome,reason}`; ADR-003 gereği ham IP/principal etiketlenmeden) ve hem middleware'de hem filtrede artır. Log mesajlarında iki nedeni ayrı sabit kodla ayır (`security_client_address_untrusted` vs `security_limiter_unavailable`); public gövde kodu aynı kalabilir. redis-unavailable.md ve api-availability.md runbook'larına 'proxy trust drift' teşhis adımını ekle ve production compose'da `ForwardedHeaders__KnownProxies` değerini açıkça set/boşalt.

---

### 3. Installation keyring HMAC anahtar rotasyonunda re-hash yolu yok — eski sürüm düşürüldüğünde toplu kimlik kaybı

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L01 — API kimlik ve güvenlik yüzeyi |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Services/InstallationCredentialKeyring.cs:40, 118-128, 197-215; src/Saydin.Api/Repositories/InstallationRepository.cs:18-33; infrastructure/postgres/migrations/021_api_trust_expand.sql:594-599` |

**Bulgu.** Keyring en fazla 3 sürüm kabul ettiği ve aktif sürüm mutlaka en büyük olmak zorunda olduğu için 4. rotasyonda en eski sürüm zorunlu düşer; kullanım anında verifier'ı aktif sürüme yükselten hiçbir sunucu tarafı yol bulunmadığından o sürümle hash'lenmiş tüm credential'lar tek seferde çözümsüz kalır, ayrıca aktif anahtara bağlı principal pseudonym'i telemetride sessizce kopar.

**Etki.** Etkilenen tüm kurulumlar aynı anda generic 401 alır; ADR-010'da kanıtsız devralma yolu bulunmadığından kullanıcılar kayıtlı senaryolarını kalıcı kaybeder ve yeniden kayıt olmak zorunda kalır. Activity log korelasyonu da rotasyon anında bozulur.

**Öneri.** Ya başarılı `resolve_installation` sonrası verifier'ı aktif sürümle idempotent şekilde yeniden yazan bir SECURITY DEFINER fonksiyonu ekleyip kullanım anında upgrade uygula, ya da keyring rotasyonunu istemci tarafı `POST /v1/installations/rotation` akışını zorunlu kılan zamanlanmış bir drain penceresiyle prosedürleştirip `docs/runbooks/` altına yaz. Activity log pseudonym anahtarını keyring'den ayır (ayrı sabit HMAC anahtarı) ki anahtar rotasyonu telemetri korelasyonunu bozmasın.

---

### 4. Migration 018 hard-cap preflight'ı, 100'den fazla senaryosu olan tek bir kullanıcıda tüm migration zincirini durdurur → API/ingestion hiç başlamaz

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L02 — Scenario payload/pagination |
| **Kategori** | operations |
| **Doğrulama** | CONFIRMED (verifier) |
| **Konum** | `infrastructure/postgres/migrations/018_scenario_integrity.sql:41-50; docker-compose.yml:425-426,473-474; src/Saydin.Api/appsettings.json:49-51; docs/decisions/ADR-008-scenario-payload-pagination.md:69-70` |

**Bulgu.** 018 preflight'ı `SELECT 1 FROM saved_scenarios GROUP BY user_id HAVING count(*) > 100` bulursa `RAISE EXCEPTION ... ERRCODE '23514'` ile transaction'ı iptal eder (satır 41-50). Compose'da `api` ve `price-ingestion` servisleri `database-migrator: condition: service_completed_successfully` ile bağlıdır (docker-compose.yml:425-426, 473-474), yani migrator non-zero dönerse hiçbir servis ayağa kalkmaz. Bu duruma düşmek mümkündür: appsettings.json:51 `Plans:Premium:MaxSavedScenarios = 0` ve eski kod (git show a274c62:src/Saydin.Api/Services/SavedScenarioService.cs:178-186) `scenarioLimit <= 0` için limiti hiç uygulamıyordu → premium kullanıcılar sınırsız senaryo kaydedebiliyordu. ADR-008:69-70 tam tersini vaat ediyor: '100'den fazla mevcut satır silinmez. Yeni save 422 alır; tüm mevcut satırlar additive cursor endpointinden okunabilir.' ADR'nin 018 gate listesi (satır 96-99) preflight'ı yalnız extra_data ihlalleri için tanımlar; hard-cap preflight'ı ADR kapsamı dışında eklenmiştir. docs/ altında bu duruma dair operatör runbook adımı bulunamadı (yalnız docs/architecture/database-schema.md:103'te tek satırlık başlık).

**Etki.** Deploy-blocking tam servis kesintisi ve ADR ile fiili davranış arasında doğrudan çelişki (ADR mevcut fazla satırların korunup okunabileceğini söylerken migration, mevcudiyetlerinde deploy'u tamamen durdurur). Etki mevcut üretim verisine bağlıdır; böyle bir satır yoksa etki yoktur.

**Öneri.** Deploy öncesi `SELECT user_id, count(*) FROM saved_scenarios GROUP BY 1 HAVING count(*)>100` kontrolünü ve varsa kontrollü arşivleme adımını runbook'a ekle; ya da hard-cap zorlamasını yalnız trigger'a bırakıp (preflight'sız) mevcut fazlalığı tolere et ve ADR-008:69-70 ile hizala.

---

### 5. İleri yönlü WhatIf/DCA'da 6 haneli birim yuvarlaması ham yatırım tutarıyla asimetrik → yüksek birim fiyatlı varlıklarda hayali kâr/zarar

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L03 — Finansal hesaplama, cache, kota |
| **Kategori** | financial |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Services/WhatIfCalculator.cs:468-483; src/Saydin.Api/Services/DcaCalculator.cs:185,190,236-243; src/Saydin.Api/Services/WhatIfCalculator.cs:288-289 (karşı örnek)` |

**Bulgu.** İleri yönlü WhatIf ve DCA'da yatırım tarafı ham tutardan, değer tarafı 6 haneye yuvarlanmış birimden türetildiği için birim yuvarlama hatası doğrudan kâr/zarara sızar; hata mutlak olarak ≤5e-7 birim × fiyat ile sınırlıdır ve yalnız çok yüksek birim fiyatlı varlıklarda (BTC) küçük TL tutarlarında (~100 TL) %1'i aşan sapmaya dönüşür — ters hesaplama yolu bu asimetriyi bilinçli olarak kapatmıştır.

**Etki.** BTC gibi yüksek birim fiyatlı varlıklarda küçük tutarlı sorgularda alış=satış günü bile sıfırdan farklı kâr/zarar gösterilir (örn. 100 TL/BTC senaryosunda ~%1,5). DCA'da hata her periyodik alımda tekrarlanır ve `TotalUnitsAcquired` → `CurrentValueTry` → `ProfitLossTry`/`RealProfitLossTry` zincirine taşınır. Tipik tutarlarda (≥5.000 TL) etki kuruş mertebesindedir.

**Öneri.** İleri yolu ters yol ile hizala: `initialValueTry = Math.Round(unitsAcquired * buyPrice, 2, AwayFromZero)` ve DCA'da `cumulativeCost += Math.Round(unitsAcquired * price, 2, AwayFromZero)`; ya da birim hassasiyetini varlık kategorisine göre (kripto için ≥8-12 hane) ölçekle. Testte formülü tekrarlamak yerine değişmezi assert et: `BuyPrice == SellPrice ⇒ ProfitLossTry == 0` ve `InitialValueTry == Math.Round(UnitsAcquired * BuyPrice, 2)`.

---

### 6. QuotaUnavailableExceptionHandler kullanıcıya hardcoded İngilizce metin dönüyor ve contract testinin dışında

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L04 — API runtime ve activity logging |
| **Kategori** | correctness |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Exceptions/QuotaUnavailableExceptionHandler.cs:22, src/Saydin.Api/Security/DistributedSecurityLimiterMiddleware.cs:53,70, src/Saydin.Api/Endpoints/EndpointExtensions.cs:69,84, src/Saydin.Api/Exceptions/ApiErrorCodes.cs:22-34, tests/Saydin.Api.Tests/Exceptions/ExceptionHandlerContractTests.cs` |

**Bulgu.** Kota/limiter yolundaki dört kullanıcı-yüzeyi metni (QuotaUnavailableExceptionHandler.cs:22, DistributedSecurityLimiterMiddleware.cs:53,70, EndpointExtensions.cs:69,84) IStringLocalizer'ı atlayıp hardcoded İngilizce döner; resx'teki hazır RateLimited/RateLimitedDetail key'leri hiçbir yerden çağrılmaz, ApiErrorCodes'ta karşılık sabit yoktur ve QuotaUnavailableExceptionHandler ExceptionHandlerContractTests kapsamında değildir.

**Etki.** CLAUDE.md yasak listesindeki "kullanıcıya dönecek string'lerde hardcoded Türkçe/İngilizce" ihlali; `Accept-Language: tr` istemcisi 429/503 yanıtlarında karışık dilli hata yüzeyi görür. Lokalizasyon regresyonunu yakalayacak contract mührü bu handler için hiç kurulmamış.

**Öneri.** QuotaUnavailableExceptionHandler'a IStringLocalizer<ErrorMessages> enjekte et; limiter middleware ve EndpointExtensions'ta mevcut RateLimited/RateLimitedDetail key'lerini kullan (503 için yeni key ekle); ApiErrorCodes'a QuotaUnavailable ve SecurityRateLimited sabitlerini ekle; ExceptionHandlerContractTests'i kayıtlı IExceptionHandler tipleri üzerinden refleksiyonla enumerate edip "her handler contract testinde temsil edilmeli" mührünü kur.

---

### 7. Distributed security limiter tüm Redis/Lua hatalarını logsuz yutuyor — 503 fırtınasının kök nedeni hiçbir yerde görünmüyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L04 — API runtime ve activity logging |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED (verifier) |
| **Konum** | `src/Saydin.Api/Security/DistributedSecurityLimiter.cs:136-142, src/Saydin.Api/Security/DistributedSecurityLimiterMiddleware.cs:60-71, src/Saydin.Api/Endpoints/EndpointExtensions.cs:72-85` |

**Bulgu.** `TryAcquireBucketsAsync` sonunda `catch (OperationCanceledException) { throw; } catch (Exception) { return SecurityLimiterDecision.Unavailable; }` (satır 136-142) — sınıfın hiç `ILogger` bağımlılığı yok, exception hiçbir yere yazılmıyor. Aynı şekilde satır 123-131'deki parse/aralık doğrulaması başarısız olduğunda da sessizce Unavailable dönülüyor. Çağıran taraflar da exception'ı görmüyor: DistributedSecurityLimiterMiddleware.cs:62-64 yalnız sabit bir string logluyor (`logger.LogWarning("Distributed security limiter unavailable: {Code}", "security_limiter_unavailable")`) ve satır 62'deki yorum exception eklemeyi bilinçli olarak yasaklıyor; EndpointExtensions.cs:74-78 aynı sabit satırı tekrarlıyor. CLAUDE.md: "Exception sessizce yutulmaz — minimum ILogger ile loglanır" ve yasak listesinde "Exception'ı sessizce yutmak — YASAK".

**Etki.** Tüm ürün trafiğini 503'e düşüren bir arızada operasyon ekibi yalnızca sabit "security_limiter_unavailable" satırını görür; exception tipi, mesajı ve stack hiçbir log/trace'e düşmediği için kök neden analizi ancak Redis tarafına canlı erişimle yapılabilir. Ayrıca aynı catch, Redis dışı kod hatalarını (ör. yanlış RedisValue dönüşümü) da kalıcı olarak gizler.

**Öneri.** Limiter'a `ILogger<DistributedSecurityLimiter>` enjekte edip catch bloğunda exception'ı `LogError`/`LogWarning` ile (yalnız exception + sabit kod; adres/principal/Redis key olmadan) logla — gizlilik gereksinimi exception'ın kendisini değil, kimlik/adres tag'lerini yasaklıyor. Ek olarak `saydin.security_limiter.unavailable.total` gibi bir sayaç ile hata sınıfını (`redis_exception` / `malformed_reply`) ayır.

---

### 8. price_points.source_raw EF modeline eklenince tüm API okuma yolları ve Redis cache girdileri ham gözlem JSON'unu taşımaya başladı

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L05 — Shared entity ↔ SQL şema paritesi |
| **Kategori** | performance |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Shared/Entities/PricePoint.cs:22, src/Saydin.Shared/Data/Configurations/PricePointConfiguration.cs:32, src/Saydin.Api/Repositories/PriceRepository.cs:125,215,283-290, src/Saydin.Api/Services/AssetService.cs:280-284, src/Saydin.Api/Services/RedisCacheHelper.cs:54` |

**Bulgu.** Bu commit `PricePoint.SourceRaw`'ı EF modeline ekleyip jsonb olarak mapliyor; API okuma sorguları projeksiyon kullanmadığı için ham gözlem JSON'u her satırda DB'den çekiliyor ve `PriceRangeCacheEntry`/`PriceCacheEntry` üzerinden boyut sınırı olmadan Redis'e serileşiyor — oysa tüketicinin tek ihtiyacı `SourceRaw != null` bilgisidir.

**Etki.** En geniş price-range isteğinde (MaxPriceRangeDays=3650, ~2.500 satır) DB→API ve Redis payload'ı satır başına kabaca 2-3 katına çıkar (~0,5-0,8 MB ek). Üretimde Redis `noeviction` politikasıyla çalıştığı için (validate-private-material.py:92-94) kullanıcı-kontrollü sınırsız key uzayında bu şişme bellek baskısı ve cache yazma hatalarına dönüşebilir. Fonksiyonel yanlışlık yok.

**Öneri.** Okuma yolunda `source_raw`'ı materyalize etme: repository'de projeksiyon (`Select`) ile yalnız gerekli alanlar + `HasSourceRaw` bool'u taşınsın; bulk SQL'de `nearest.source_raw::text` yerine `(nearest.source_raw IS NOT NULL) AS has_source_raw` kullanılsın. Alternatif olarak cache DTO'su domain entity yerine dar bir record olsun. CLAUDE.md cache kuralı gereği `docs/cache-strategy.md`'ye değer boyutu notu eklensin.

---

### 9. Trust-root migration'ları (001–022) DBM-004 impact/bütçe ve online yürütme makinesini atlar; 022 tek transaction'da tüm activity_logs chunk'larını decompress/recompress eder

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L06 — SQL migration 015–022 |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.DatabaseMigrator/MigrationRunner.cs:283-285, :235-262, :2840-2853; src/Saydin.DatabaseMigrator/MigratorOptions.cs:203-208; infrastructure/postgres/migrations/022_principal_retention.sql:70,133-141,165-195` |

**Bulgu.** Migration 022, `activity_logs` hypertable'ının tüm sıkıştırılmış chunk'larını tek bir `DO` bloğu (yani tek statement_timeout penceresi) içinde açıp yeniden sıkıştırırken `public.users` üzerinde ACCESS EXCLUSIVE kilidi tutar; trust-root migration'ı olduğu için DBM-004 impact/bütçe preflight'ı ve resumable-online yolu bu migration'a uygulanmaz (bu kapsam sınırı runbook'ta belgelidir) ve chunk sayısına göre büyüyen süre yalnız genel 1800 s statement / 2100 s toplam timeout ile sınırlıdır.

**Etki.** Aylardır veri toplamış (fresh olmayan) bir veritabanında 022 uygulanırken: (a) users tablosu decompress+recompress süresi boyunca ACCESS EXCLUSIVE altında kalır, principal/scenario yolları bloke olur; (b) süre 1800 s statement veya 2100 s toplam bütçeyi aşarsa transaction geri alınır, ilerleme kaydedilmez (checkpoint/batch yok) ve tekrar denemede aynı duvara çarpılır — deploy yakınsamayabilir. Fresh kurulumda `compressed_chunks` boş olduğu için etki yoktur.

**Öneri.** 022'nin chunk decompress/recompress adımını migration transaction'ından çıkarıp ayrı, resumable bir bootstrap adımına taşıyın; ya da 022 için deploy-özel `--command-timeout-seconds`/`--total-timeout-seconds` değerlerini, gereken disk headroom'unu ve maintenance-window gereksinimini docs/runbooks/ altında bir 022-özel prosedüre yazın. docs/analysis/06-remediation-progress.md'deki DBM-004 satırına 'impact/online koruma yalnız 023+ için geçerlidir' notunu eklemek de faydalı olur.

---

### 10. UnknownTailMigration integration testi ulaşılamayan hata kodunu iddia ediyor; ValidateHistoricalPrefix üretimde çalışmıyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L07 — Saydin.DatabaseMigrator |
| **Kategori** | test-quality |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.DatabaseMigrator.Tests/MigrationRunnerIntegrationTests.cs:1911-1929, 2572-2582; tests/Saydin.DatabaseMigrator.Tests/MigrationManifestTests.cs:26-57, 87-117; src/Saydin.DatabaseMigrator/MigrationRunner.cs:93-99, 3159-3207; src/Saydin.DatabaseMigrator/MigrationImpactManifest.cs:154-157; .github/workflows/ci.yml:753-765` |

**Bulgu.** `UnknownTailMigration_IsRejectedBeforeConnectionOrDdl` senaryosu `ValidateTrustedPrefix`'i geçer ve `migration_impact_configuration_required` ile reddedilir; test `historical_manifest_mismatch` beklediği için çalıştığında kesin başarısız olur. Ayrıca `MigrationRunner.ValidateHistoricalPrefix` üretim yolunda hiç çağrılmaz — `MigrationManifestTests`'in dört testi yalnız bu ölü kopyayı mühürler, üretimdeki `ValidateTrustedPrefix`'i değil.

**Etki.** İki olasılıktan biri geçerli: (a) required CI migrator job'ı kırmızıdır ve docs/analysis/06-remediation-progress.md:643'teki '124 passed, skipped/failed/notExecuted 0' kabul kanıtı bu commit'te geçerli değildir; (b) test çalışmamıştır ve zero-skip kapısı iddiası tutmuyordur. Her iki durumda da trust-root prefix kapısının üretim yolunda korunduğuna dair birim-test kanıtı yoktur (mühür ölü metodun üzerindedir).

**Öneri.** Testin beklentisini `migration_impact_configuration_required` ile hizala veya senaryoyu gerçekten `historical_manifest_mismatch` üretecek şekilde kur (kanonik bir dosyayı silerek). `ValidateHistoricalPrefix`'i sil ve `MigrationManifestTests`'i `ValidateTrustedPrefix` üzerinden yeniden yaz; kuyruklu ve kuyruksuz iki durumu ayrı ayrı mühürle. Kabul kanıtını güncel commit üzerinde yeniden koşup TRX sayısını raporla.

---

### 11. Login parolaları çalıştırılan CREATE ROLE metnine gömülüyor: pg_stat_activity ve hatalı ifade loglarında düz metin ifşası

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L08 — RoleBootstrap + DatabaseSecurity |
| **Kategori** | security |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.DatabaseRoleBootstrap/RoleBootstrapDatabaseOperations.cs:128-139, 1113-1124, 174-177; RoleBootstrapRunner.cs:250-266` |

**Bulgu.** CREATE ROLE ifadesi pg_catalog.format ile güvenli biçimde üretilse de düz parola içeren nihai metin ayrı bir komut olarak çalıştırılıyor; bu metin çalıştığı süre boyunca pg_stat_activity.query'de görünür (exporter capability pg_monitor üyesidir) ve ifade hata alırsa PostgreSQL varsayılan log_min_error_statement=error ile parola sunucu log dosyasına yazılır. Pencere yalnız rol oluşturma anlarıyla (ilk ensure ve rotate) sınırlıdır.

**Etki.** Bileşenin tüm tehdit modeli "parola argv/env'e girmez, yalnız 0400/0600 dosyadan okunur" üzerine kurulu; bu yol parolayı DB sunucusunun gözlemlenebilir yüzeyine (stat view + hata logu) taşıyor. En gerçekçi tetikleme CREATE ROLE'ün herhangi bir nedenle hata alması ve düz parolanın kalıcı sunucu logunda kalmasıdır.

**Öneri.** Parolayı istemci tarafında SCRAM-SHA-256 verifier'a çevirip `PASSWORD '<verifier>'` olarak gönder (sunucu zaten verifier saklıyor); böylece ne stat view ne log düz metin görür. Ara önlem olarak rol oluşturma ifadelerini `SET LOCAL log_min_error_statement=panic` sarmalına al — ama pg_stat_activity penceresini yalnız verifier yaklaşımı kapatır.

---

### 12. Kimlik bilgisi rotasyonu tek yönlü ve terminal: yalnız v1→v2, mevcut sürümün parolası değiştirilemiyor, v1 emekliye ayrılamıyor, runbook yok

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L08 — RoleBootstrap + DatabaseSecurity |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.DatabaseRoleBootstrap/BootstrapOptions.cs:95-98; RoleBootstrapDatabaseOperations.cs:112-150; RoleBootstrapRunner.cs:388-416, 497-500; tests/Saydin.DatabaseRoleBootstrap.IntegrationTests/RoleBootstrapIntegrationTests.cs (rejectedReplacement bloğu)` |

**Bulgu.** Rotate komutu yalnızca v1→v2 geçişini destekler; v2 zaten mevcutken yeni parola yazılmaz (EnsureRoleAsync no-op) ve komut exit 69 `login_authentication_failed` ile başarısız olur, sızan v2 parolası geçerli kalır. v3+ Parse aşamasında reddedilir, v1'i emekliye ayıran komut yoktur ve repoda rotasyon runbook'u bulunmuyor.

**Etki.** Parola rotasyonu bir güvenlik kontrolü olarak ömür boyu yalnız bir kez kullanılabilir. İkinci bir kompromizasyonda desteklenen kurtarma yolu yok; elle DROP/ALTER ROLE gerekiyor ve bu da exact-marker/ACL fail-closed doğrulamalarıyla çakışma riski taşıyor.

**Öneri.** (a) rotate'i N. sürüme genişlet ve izin verilen sürüm kümesini contract'tan türet; (b) mevcut sürümün parolasını bilinçli yeniden yazan `reset-password` komutu (veya `rotate --force-password`) ekle; (c) v1'i düşüren `retire` komutu + eski oturum drenajı runbook'unu docs/deployment/ altına yaz. Asgari olarak mevcut sınırı ve elle kurtarma prosedürünü belgele.

---

### 13. daily_update ve historical_backfill scope'ları her gün aynı günü iki kez çekip iki kez yazıyor; ikinci yazım değer değişirse süreç kurtarılamaz döngüye giriyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L09 — Ingestion ledger ve write fence |
| **Kategori** | architecture-rule |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:33-34,74-84,105-117,211-216; Workers/TcmbWorker.cs:28-32; Workers/TwelveDataWorker.cs:43-44; Repositories/IngestionWindowRepository.cs:666-694; infrastructure/postgres/migrations/020_price_authority_expand.sql:299-311` |

**Bulgu.** Her price worker'da backfill horizonu ile daily_update hedef tarihi aynı günü kapsadığından her günlük koşuda aynı (asset, gün) için iki ayrı window, iki provider çağrısı ve iki yazım gerçekleşir; değerler birebir aynı olduğu sürece zararsızdır, ancak provider aynı günü revize ederse ikinci yazım migration 020'nin authority-immutability trigger'ından 23514 alır ve window 'running' kaldığı için operator requeue ile bile açılamayan bir crash-loop oluşur.

**Etki.** Normal işleyişte asset başına günde bir gereksiz provider çağrısı, gereksiz window ve attribution satırı (OXR'de gün cache'i HTTP tekrarını önler — OpenExchangeRatesAdapter.cs:116-125 doğrulandı; TCMB/CoinGecko/TwelveData'da önlemez). Revizyon durumunda ise imzalı repair yolu olmayan kalıcı ingestion kilidi.

**Öneri.** daily_update scope'unu kaldırıp backfill horizonunu tek otorite yap (veya FetchDailyAsync'i backfill horizonunun kapsadığı tarihlerde no-op yap). Kalması gerekiyorsa aynı (asset, gün) için değer farkını typed `provider_revision_conflict` permanent outcome'una çevirip süreci düşürmek yerine alarm üret.

---

### 14. Telemetri amaçlı freshness hydration servisinin tek bir geçici DB hatası tüm ingestion sürecini durduruyor ve süreç exit code 0 ile kapanıyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L09 — Ingestion ledger ve write fence |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Workers/IngestionFreshnessHydrationService.cs:27-34,36-48; src/Saydin.PriceIngestion/Program.cs:132-135,157,160-161; src/Saydin.PriceIngestion/Workers/IngestionOrchestrator.cs:104-113` |

**Bulgu.** IngestionFreshnessHydrationService'in periyodik DB sorgusu hiçbir try/catch ile korunmadığı ve host `StopHost` davranışında yapılandırıldığı için tek bir geçici PostgreSQL hatası sağlıklı beş worker'ı da durdurur; orchestrator shutdown dalında ExitCode'u koşulsuz 0 yaptığından bu fatal kapanış temiz çıkış gibi raporlanır (LivenessHeartbeatService ise aynı zafiyete sahip değildir, kendi exception'larını yutar).

**Etki.** Veri düzlemi sağlıklıyken salt-gözlemlenebilirlik bileşeni yüzünden süreç düşüyor; in-flight window'lar lease süresi (30 dk) boyunca Busy kalıyor. Ayrıca SUP-001'in 'fatal → non-zero exit' kabul kanıtı yalnız worker fault'ları için geçerli; hosted-service fault'unda exit 0 dönerek non-zero exit'e dayalı alarm/restart politikalarını sessizce bozar.

**Öneri.** RefreshAsync çağrısını try/catch ile sarıp LogWarning + `ingestion_freshness_hydration_failures` sayacına dönüştür ve bir sonraki tick'te tekrar dene; orchestrator'ın shutdown dalında `if (_exitCode.ExitCode == 0)` guard'ı ile başka bir bileşenin işaretlediği fatal durumu silme.

---

### 15. Beklenmeyen adapter exception'larında kök neden hiçbir yerde kaydedilmiyor (exception nesnesi loglanmıyor, EVDS'te hiç log yok)

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L09 — Ingestion ledger ve write fence |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:172-186; src/Saydin.PriceIngestion/Workers/EvdsInflationWorker.cs:139-152; src/Saydin.PriceIngestion/Repositories/IngestionWindowRepository.cs:1197-1205` |

**Bulgu.** Beklenmeyen adapter exception'larında BaseAssetWorker exception nesnesini LogError'a geçirmediği (yalnız tip adını string parametre olarak yazdığı), EvdsInflationWorker ise hiç log yazmadığı için message ve stack trace hem log'dan hem ledger'dan tamamen kayboluyor.

**Etki.** Permanent window'lar tüm hattı durdurduğundan (bkz. lane-01) kök-neden analizi kritik; olay çözüm süresi doğrudan uzuyor. Adapter'ların kendi typed failure'larında `Detail` dolduğu için gözlemlenebilirlik de tutarsız.

**Öneri.** Her iki catch bloğunda `logger.LogError(ex, ...)` kullan; secret sızıntısı endişesi için mevcut `Truncate` redaction regex'ini exception mesajına uygulayıp redakte edilmiş mesajı hem log'a hem `detail` alanına yaz.

---

### 16. TcmbWorker hedef tarihi bu commit'te 'bugün'den 'dün'e çevrildi: TCMB kurları artık her zaman en az bir gün geç ingest ediliyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L09 — Ingestion ledger ve write fence |
| **Kategori** | correctness |
| **Doğrulama** | CONFIRMED (verifier) |
| **Konum** | `src/Saydin.PriceIngestion/Workers/TcmbWorker.cs:26-32 (diff a274c62..f9f608d)` |

**Bulgu.** `git diff a274c62 f9f608d -- src/Saydin.PriceIngestion/Workers/TcmbWorker.cs` gösteriyor ki commit öncesi TcmbWorker `TargetDate`'i override etmiyordu; BaseAssetWorker default'u `DateOnly.FromDateTime(utcNow.Date)` yani 'bugün'dü. Commit sonrası hem `TargetDate` hem `BackfillThrough` `IstanbulDate(utcNow).AddDays(-1)` döndürüyor. Worker 13:30 UTC = 16:30 İstanbul'da çalışıyor (DefaultDailyRunUtcTime, :26) ve dosyadaki yorum bu saatin TCMB'nin 15:30 yayınından sonra olması için seçildiğini söylüyor — ancak artık o gün yayımlanan kur aynı gün değil, ertesi gün 16:30'da yazılıyor.

**Etki.** FX/TCMB tabanlı varlıklar için en güncel fiyat sürekli ~1 gün (16:30 öncesinde ~2 gün) eskidir; kullanıcıya gösterilen güncel değer/kar-zarar TRY'nin oynak günlerinde gerçek durumu yansıtmaz. Not: IngestionFreshnessTelemetry.RecordCalendarHorizon 'tcmb_indicative_fx' için coverage gereksinimini 'yesterday' olarak tanımladığından bu tercihin kasıtlı olma ihtimali var, ancak ne kod yorumunda ne docs/analysis'te bu 24 saatlik freshness kaybı gerekçelendirilmiş.

**Öneri.** TCMB için hedef tarihi tekrar 'bugün' yap (yayın saati 15:30'dan sonra çalıştığı ve gün içi kur bülteni o gün için final olduğu için güvenli), ya da 24 saatlik gecikmenin bilinçli olduğunu kod yorumu + docs/architecture'da açıkça belgele ve calendar horizon gereksinimiyle birlikte gerekçelendir.

---

### 17. Retry zincirinde hiç backoff yok; Retry-After'sız 429'da provider'a arka arkaya 4 istek atılıyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L10 — Provider adapter/mapper |
| **Kategori** | correctness |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Extensions/HttpResilienceExtensions.cs:35-43,61-70; tests/Saydin.PriceIngestion.Tests/HttpResilienceExtensionsTests.cs:14-22,24-46` |

**Bulgu.** Retry stratejisi DelayGenerator üzerinden 429 dışı tüm hatalarda ve Retry-After başlığı olmayan 429'larda sıfır gecikme uygular; exponential backoff ve jitter fiilen devre dışıdır ve mevcut testler bu davranışı mühürlemektedir.

**Etki.** Geçici 5xx/ağ hatası veya Retry-After'sız 429'da provider'a milisaniyeler içinde 4 istek gider; retry işlevsizleşir ve ücretsiz plan provider'larında daha uzun/agresif kısıtlama (geçici ban) riski artar. CLAUDE.md 'retry (3 deneme, exponential backoff + jitter)' ve '429'da exponential backoff' maddeleriyle çelişir.

**Öneri.** 429/5xx için sıfırdan büyük taban + jitter'a dön (`Delay = 2s`, `UseJitter = true`), DelayGenerator'ı yalnız Retry-After varsa `max(backoff, retryAfter)` biçiminde devreye sok ve Retry-After'sız 429'da gecikmenin sıfır olmadığını doğrulayan bir test ekle.

---

### 18. CLAUDE.md ve docs/architecture.md resilience sözleşmesi kodla uyuşmuyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L10 — Provider adapter/mapper |
| **Kategori** | docs |
| **Doğrulama** | CONFIRMED |
| **Konum** | `CLAUDE.md:327-331; docs/architecture.md:88-101; src/Saydin.PriceIngestion/Extensions/HttpResilienceExtensions.cs:14-50` |

**Bulgu.** Bu commit CLAUDE.md ve docs/architecture.md'yi değiştirdiği hâlde resilience bölümlerini güncellememiştir; her iki doküman da artık var olmayan bir 3 dk TotalRequestTimeout'u, jitter'lı exponential backoff'u ve MinimumThroughput=2'yi tarif etmektedir.

**Etki.** Mimari sözleşme dokümanı üretim davranışını yanlış tarif ediyor; özellikle 'TotalRequestTimeout 3 dk' iddiası L10-01'deki süresiz askıda kalma senaryosunun teşhisini doğrudan yanıltır ve sonraki değişikliklerin yanlış varsayımla yapılmasına yol açar.

**Öneri.** CLAUDE.md 'Dış API Adaptörleri' maddelerini ve docs/architecture.md resilience diyagramını gerçek pipeline ile hizala; tercihen L10-01/L10-02 düzeltmeleriyle kodu belgelenen sözleşmeye geri getir. Parametreleri kod sabitlerinden okuyan bir doküman-drift testi düşünülebilir.

---

### 19. Finansal mapper'larda `NumberStyles.Any` binlik ayırıcıya izin veriyor: virgüllü bir provider değeri sessizce yanlış fiyat üretir

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L10 — Provider adapter/mapper |
| **Kategori** | financial |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Mappers/TcmbMapper.cs:162-169; src/Saydin.PriceIngestion/Mappers/TwelveDataMapper.cs:77-80,127-132; src/Saydin.PriceIngestion/Mappers/EvdsInflationMapper.cs:53` |

**Bulgu.** Üç finansal mapper'da `NumberStyles.Any` kullanıldığı için virgülü ondalık ayırıcı olarak yollayan bir provider yanıtı reddedilmek yerine binlik ayırıcı sayılıp 100x–10.000x büyütülmüş pozitif bir değere dönüşür ve ne mapper, ne CHECK constraint'leri, ne de evidence trigger'ı bunu yakalar.

**Etki.** Yanlış fiyat/TÜFE değeri final authority olarak kalıcı yazılır (is_final=TRUE, immutability trigger'ı nedeniyle sonradan UPDATE edilemez) ve doğrudan what-if/DCA hesaplarına girer. Fail-closed tasarımdaki tek sessiz veri bozulma yoludur; tetikleyicisi provider tarafında bir format/locale değişikliğidir.

**Öneri.** Üç mapper'da `NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign` (gerekirse leading/trailing white) kullan, `Any`'yi kaldır; '2115,19', '2.115,19' ve '(30.5)' girdilerinin reddedilip rejectedCount'a düştüğünü doğrulayan testler ekle.

---

### 20. Typed AdapterOutcome sınırı eksiksiz değil: gerçekçi payload şekilleri ham exception olarak kaçıyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L10 — Provider adapter/mapper |
| **Kategori** | correctness |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Adapters/ProviderPayload.cs:19,32; src/Saydin.PriceIngestion/Adapters/CoinGeckoAdapter.cs:90-115; src/Saydin.PriceIngestion/Adapters/TwelveDataAdapter.cs:67,94-115; src/Saydin.PriceIngestion/Mappers/EvdsInflationMapper.cs:43,49; src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:170-185` |

**Bulgu.** `ProviderPayloadTooLargeException` ve `JsonElement.GetString()` kaynaklı `InvalidOperationException` hiçbir adapter tarafından yakalanmadığı için typed outcome üretilmez; bu hatalar ledger'a stabil provider kodu yerine `adapter_exception_permanent`/`adapter_unhandled` olarak yazılır ve tek bir pencere hatası tüm ingestion sürecini fatal olarak düşürür.

**Etki.** Teşhis kaybı (provider'a özgü kod yerine exception tip adı) ve tek pencerede fatal süreç sonlanması; aynı payload her denemede tekrar edeceği ve pencere PermanentFailed kalacağı için ilgili kaynak kalıcı olarak durur ve crash-loop oluşur. 06-remediation-progress.md ING-001'deki 'parse/mapping ... artık başarılı 0 kaydı değildir' iddiası bu yollar için tam kapanmamıştır.

**Öneri.** Adapter catch zincirlerine `ProviderPayloadTooLargeException` (→ `PermanentFailure("payload_too_large")`) ve `InvalidOperationException` (→ `PermanentFailure("contract_value_kind_invalid")`) ekle; mapper'larda `GetString()` yerine ValueKind kontrolü + `TryGetDecimal` ile ham JSON sayı formunu da kabul eden savunmalı okuma kullan. CoinGecko chunk payload boyutunu ölç/metrikleştir veya ChunkDays'i düşür.

---

### 21. `full_window` yüklemi aşırı dar: ledger sayaç kontrolleri çok-pencereli lane'lerde hiç değerlendirilmiyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L11 — Saydin.DataQualityAudit |
| **Kategori** | correctness |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.DataQualityAudit/AuditSql.cs:10, :57-75, :88, :125-130 (çağıran: AuditRunner.cs:1060-1073; fixture: tests/Saydin.DataQualityAudit.IntegrationTests/AuditDatabaseFixture.cs:1846-1864)` |

**Bulgu.** DQ-001'in ledger sayaç karşılaştırmaları, `full_window` yüklemi pencere-lane birebir eşitliği istediği için birden fazla ingestion window kapsayan her lane'de (üretimdeki normal kullanım) hiç değerlendirilmiyor ve bu kod yolları hiçbir testle mühürlenmemiş; etki, DB CHECK kısıtları ve veri düzeyi kontrolleri sayesinde ledger sayaç metadata'sının doğrulanmamasıyla sınırlı.

**Etki.** ingestion_windows'un requested_calendar_count / expected_observation_count / accepted_distinct_count / expected_no_data_count kolonları takvim ve gerçek veriye karşı üretimde hiç doğrulanmıyor; bu kolonlar tutarlı biçimde kaydırılmış (DB CHECK'i sağlayan) bozuk değerler taşısa audit temiz `0` döner ve imzalı kanıt paketi 'clean' yayımlanır. Kanıt paketinin/dokümanın ima ettiği kapsama ile gerçek kapsama arasında sessiz bir fark oluşur; veri eksiksizliği ve finansal invariant kontrolleri etkilenmez.

**Öneri.** Her iki SQL'de bayrağı `base.range_start >= $2::date AND base.range_end <= $3::date` yapın. Ardından entegrasyon süitine lane'in >1 pencere kapsadığı bir fixture ekleyip accepted_distinct_count/requested_calendar_count'u (session_replication_role=replica ile CHECK'i sağlayacak şekilde) bozarak ledger_requested_count_mismatch ve ledger_success_actual_count_mismatch kodlarının gerçekten tetiklendiğini mühürleyin; CI ratchet'ini (84/72) buna göre yükseltin.

---

### 22. Rollback commit sonrası publish hatasında pending receipt uzlaştırma bloğu erişilemez; araç kalıcı olarak `rollback_apply_postimage_changed` verir

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L12 — Saydin.DataRepair |
| **Kategori** | correctness |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.DataRepair/RepairExecutor.cs:70-80 (bağlam 49-90, 120-133)` |

**Bulgu.** Rollback transaction'ı commit olduktan sonra receipt terfisi başarısız olursa, aynı imzalı planla yapılan her rollback (ve apply) yeniden çalıştırması erken postimage kontrolüne takılır; pending rollback receipt'ini uzlaştıran blok (RepairExecutor.cs:75-80) bu senaryoda hiçbir zaman çalıştırılamaz ve committed mutasyonun imzalı kanıtı `.pending-*` dizininde yayımlanmamış kalır.

**Etki.** Gerçekleşmiş (committed) bir rollback mutasyonunun denetim izi araç tarafından yayımlanamaz; dönen hata kodu da olgusal olarak yanlıştır ('apply postimage'ı değişti' der, oysa değişikliği aracın kendisi yapmıştır) ve operatörü README'nin açıkça yasakladığı elle receipt taşımaya iter. Veri kaybı veya yanlış finansal sonuç yok; etki denetlenebilirlik ve olay müdahalesiyle sınırlı olduğu için Medium.

**Öneri.** `PendingExists(plan.NonceSha256, "rollback")` bloğunu 70-72'deki apply-postimage assert'inin ÖNÜNE al (apply receipt'i 55. satırda okunmuş durumda, yorumdaki ön koşul sağlanıyor); RecoverPendingAsync null dönerse mevcut postimage kontrolüne devam et. `CommitThenThrow`/promote-failure enjeksiyonuyla rollback modunu kapsayan bir entegrasyon testi ekle.

---

### 23. Guard/CAS koruma dalları hiçbir testte tetiklenmiyor: fixture yalnız tek bir boş ingestion_windows satırı üretiyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L12 — Saydin.DataRepair |
| **Kategori** | test-quality |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.DataRepair.IntegrationTests/RepairDatabaseFixture.cs:54-119,228-235; src/Saydin.DataRepair/RepairDatabase.cs:110-118,187-203,266-284,366-401,411-450,520-528` |

**Bulgu.** Entegrasyon fixture'ı ilişkili hiçbir job/attribution/fiyat satırı üretmediği için guard boş küme üzerinde hesaplanıyor; guard/running-job/newer-window/row-budget red dalları ve guard'ın sıralama determinizmi hiçbir testle kanıtlanmıyor (SQL geçerliliği ve tablo ACL'leri ise boş sonuçla da yürütüldüğü için zaten kapsanıyor).

**Etki.** Aracın en karmaşık güvenlik mekanizmasının davranışsal doğruluğu (mutasyon penceresi boyunca ilişkili durumun değişmediğinin kanıtı ve değiştiğinde reddetme) otomatik kanıt olmadan kalıyor; ileride guard sorgularında yapılacak bir sıralama/semantik değişikliği CI'da fark edilmez. Test/bakım açığı olduğu için Medium.

**Öneri.** Fixture'a en az bir `ingestion_jobs` satırı (ayrıca `status='running'` varyantı), bir `inflation_observation_attributions` + `inflation_rates` çifti ekle; (a) apply sonrası ilişkili satırı değiştirip `rollback_related_state_changed`, (b) running job ile `repair_running_job_rejected`, (c) daha yeni terminal pencere ile `repair_newer_terminal_window_rejected`, (d) düşürülmüş budget ile `repair_guard_row_budget_exceeded` dallarını doğrulayan testler yaz ve CI TRX ratchet'ini (şu an 7) yükselt.

---

### 24. TCMB coverage_through yayınlanmamış güne ilerletilebiliyor; gerçek işlem günü sessizce "no_publication" olarak mühürlenebiliyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L13 — calendar-data ve calendar infra |
| **Kategori** | data-integrity |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tools/calendar-data/src/Saydin.CalendarData/CalendarDataGenerator.cs:156-173; tools/calendar-data/README.md:28-29; infrastructure/postgres/migrations/017_authoritative_market_calendars.sql:555-563` |

**Bulgu.** Generator, coverage_through gününün aylık TCMB arşivinde gerçekten yayınlanmış olmasını zorunlu kılmaz; plan yayınlanmamış bir güne kadar ilerletilirse o gün immutable release'e `observation_expected=false, no_publication` olarak girer ve README'nin iddia ettiği 'yayınlanmamışsa coverage ilerlemez' kapısı kodda mevcut değildir.

**Etki.** Operatör coverageThrough'u TCMB'nin XML yayınını yapmadığı bir güne ayarlarsa gerçek bir FX işlem günü sahte tatil olarak mühürlenir: DQ audit completeness o günü beklemez, market_holidays contract-v1 projeksiyonuna sahte satır girer ve release immutable olduğu için düzeltme yeni release + CAS activate gerektirir. Doküman ile kod arasındaki bu fark, operatörün var olmayan bir korumaya güvenmesine yol açar.

**Öneri.** GenerateTcmb sonunda fail-closed bir kapı ekle: coverage_through günü `published` setinde olmalı ya da o gün için resmî kapalılık kanıtı bulunmalı; aksi halde `tcmb_coverage_beyond_last_publication` fırlat. Ek olarak plan doğrulamasında coverageThrough <= Istanbul-yesterday sınırını zorla ve FailClosedParserTests'e regresyon testi ekle. Kapı eklenmeyecekse README:28-29 ifadesi düzeltilmelidir.

---

### 25. Günlük TCMB timer'ı statik planla ikinci koşuda kesin başarısız; plan materialization otomasyonu yok

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L13 — calendar-data ve calendar infra |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/calendar/systemd/calendar-acquisition-tcmb.timer:5-8; infrastructure/calendar/run-acquisition.sh:34-37,45-73; tools/calendar-data/src/Saydin.CalendarData/CalendarAcquisition.cs:67-75` |

**Bulgu.** Shipped timer/unit/env kombinasyonu sabit bir plan dosyasına bağlıdır; plan her koşu için elle yeni `snapshotSetId` (ve ilerletilmiş `coverageThrough`) ile yeniden yazılmadıkça ilk başarılı koşudan sonraki her koşu `acquisition_output_exists` veya `snapshot_set_id_not_advanced` ile deterministik olarak başarısız olur ve repo bu materialization'ı otomatikleştiren hiçbir artefakt içermez.

**Etki.** 'Günlük otomatik acquisition' pratikte elle plan üretimine bağımlıdır. Yapılmazsa TCMB active coverage Istanbul-yesterday'in gerisine düşer, SaydinTcmbCalendarCoverageStale (infrastructure/prometheus/rules/ingestion.yml:64-70) critical yanar, contract-v2 worker'lar CalendarNotReady kalır (veri uydurulmaz ama güncellik durur) ve her gün failed bir systemd unit'i alarm gürültüsü üretir.

**Öneri.** Koşu anında planı üreten bounded bir materializer ekle (tarih → snapshotSetId, coverageThrough=Istanbul-yesterday, gerekli yıllık/aylık kaynak listesi) veya output-name'i tarih/UUID'den türet; `acquisition_output_exists` / `snapshot_set_id_not_advanced` için ayrı exit kodu ile 'yapılacak iş yok' durumunu gerçek hatadan ayır. Timer enable edilmeden önce bu boşluk runbook'ta yazılmalı.

---

### 26. Gerçek verify-candidate.sh hiçbir testte çalıştırılmıyor; infra contract testleri substring assert ve non-Linux'ta sessiz PASS

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L13 — calendar-data ve calendar infra |
| **Kategori** | test-quality |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tools/calendar-data/tests/Saydin.CalendarData.Tests/InfrastructureCalendarContractTests.cs:22-46,51-56,71-84` |

**Bulgu.** Promotion güvenlik kapısı (verify-candidate.sh) hiçbir testte gerçekten çalıştırılmaz; onunla ilgili tüm iddialar script metnindeki substring varlığına dayanır ve tek davranışsal test hem doğrulayıcıyı stub'lar hem de non-Linux'ta assert çalıştırmadan PASS raporlar.

**Etki.** İmza doğrulama zincirinde, envelope hash kontrolünde veya exact-inventory karşılaştırmasında yapılacak bir regresyon hiçbir testi kırmaz; CI yeşil kalır. Lokal (macOS) koşuda tek davranışsal test Skipped olarak bile görünmez.

**Öneri.** Geçici OpenSSL anahtar çifti üretip gerçek verify-candidate.sh'i uçtan uca çalıştıran testler ekle: geçerli imza + temiz bundle → exit 0; bozulmuş source-manifest.json → manifest_hash_mismatch; yabancı anahtar → signature_invalid; fazladan dosya → candidate_contains_untracked_file. Non-Linux dalını SkippableFact ile açık Skipped yap.

---

### 27. Promotion offline replay'i candidate'ı okuyamaz: doğrulayıcı container uid 1001 ile çalışırken candidate root'a ait 0700'dür — belgelenen promotion akışı olduğu gibi başarısız olur

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L13 — calendar-data ve calendar infra |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED (verifier) |
| **Konum** | `infrastructure/calendar/verify-candidate.sh:65-70; infrastructure/calendar/run-acquisition.sh:47-61; tools/calendar-data/Dockerfile:22-23; tools/calendar-data/src/Saydin.CalendarData/SecureBundleStorage.cs:5-14,29-49` |

**Bulgu.** Acquisition, systemd unit'inde `User=` olmadığı için root çalışır ve run-acquisition.sh:53 `--user "$(id -u):$(id -g)"` ile container'ı 0:0 yapar; CalendarAcquisition çıktısını SecureBundleStorage.EnsurePrivateDirectory (dizin 0700, satır 11-12) ve WriteNewPrivateFile (dosya 0600, satır 47-48) ile owner-only yazar → candidate root:root 0700/0600 olur. verify-candidate.sh:65-70 ise `docker run --rm --network none ... --mount type=bind,src=$candidate,dst=/candidate,readonly "$image" verify --data-root /candidate` çağrısını `--user` GEÇMEDEN yapar; Dockerfile:22-23 `USER appuser` (uid 1001) olduğu için container süreci uid 1001'dir ve root'a ait 0700 bir dizini okuyamaz. Aynı sorun promote-reviewed-bundle.sh:52-60'taki ikinci pass için de geçerlidir (pending dizini 0700, dosyalar 0600, promotion kimliğine ait). Karşılaştırma: tools/calendar-data/README.md:52-62'deki dokümante edilen `docker run` örneği de `--user` geçmez, yani beklenen uid 1001'dir — script ile unit birbirine ters düşer.

**Etki.** Belgelenen uçtan uca akışın son kapısı (offline `--network none` parser replay) izin hatasıyla düşer; üstelik UnauthorizedAccessException Program.cs'in catch zincirinde (CalendarDataException / DatabaseSecurityRejectedException / NpgsqlException) yakalanmadığı için fail-closed hata kodu kontratı yerine handled edilmemiş bir stack trace ile çıkılır. Sonuç: promotion hiç tamamlanamaz veya operatör her şeyi root'a çekerek 'acquisition ≠ promotion kimliği' ayrımını çöker.

**Öneri.** Ya acquisition'ı image'ın uid'siyle (ör. `User=saydin-calendar` + `--user 1001:1001`) çalıştır, ya da verify-candidate.sh'in `docker run` çağrısına candidate sahibinin uid/gid'sini geçen bir `--user` ekle; ek olarak Program.cs'e IOException/UnauthorizedAccessException için tipli bir fail-closed kod (`bundle_unreadable`) ekle ve bu akışı gerçek dosya izinleriyle test et.

---

### 28. SaydinProcessRestarted, saydin-api ve otel-collector için hiç var olmayan bir metriğe dayanıyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L15 — Production deployment ve observability |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/prometheus/rules/tls-runtime.yml:61-66; src/Saydin.Api/Program.cs:100-110; infrastructure/otel/otel-collector.production.yml:78-90` |

**Bulgu.** SaydinProcessRestarted kuralının hedeflediği dört job'dan ikisi (saydin-api, otel-collector) process_start_time_seconds serisini hiç yayınlamaz — API'de AddProcessInstrumentation kayıtlı değil, collector self-telemetry yalnız otelcol_* serileri veriyor (imaj çalıştırılarak doğrulandı); alert bu iki servis için hiç tetiklenemez.

**Etki.** İki birinci-sınıf servis için restart/crash-loop görünürlüğü yok. Uzun kesintiler SaydinApiUnavailable ile yakalansa da hızlı restart döngüleri görünmez; observability-game-day.md:16 bu alert'i enjeksiyon senaryosu olarak listelediği için game-day yanlış güvence üretir.

**Öneri.** Restart tespitini gerçekten yayınlanan bir sinyale bağla: collector için `changes(otelcol_process_uptime[15m]) < 0` benzeri bir uptime-reset ifadesi veya .NET tarafında AddProcessInstrumentation/özel uptime gauge; ya da container düzeyinde restart sayacı. Düzeltmeyi rules.test.yml'e taşı.

---

### 29. Alert kural testleri 35 alert'in yalnız 15'ini kapsıyor, tamamı pozitif ve serileri kendi tanımlıyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L15 — Production deployment ve observability |
| **Kategori** | test-quality |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/prometheus/tests/rules.test.yml:1-226; infrastructure/deployment/validate-observability.py:27-44` |

**Bulgu.** 35 alert kuralından 20'si promtool test setinde hiç yer almıyor, mevcut 16 test bloğunun tamamı pozitif senaryodur (hiç `exp_alerts: []` yok) ve validate-observability.py kapsam için fail-closed bir kapı uygulamaz.

**Etki.** lane-01 ve lane-03 tam olarak bu boşluktan geçmiştir; 'promtool test rules CI'da yeşil' kanıtı kuralların gerçekten tetiklenebildiğini göstermez. Alert regresyonları CI tarafından mühürlenmemiştir.

**Öneri.** validate-observability.py'ye 'her `- alert:` adı rules.test.yml'de en az bir alertname olarak geçmeli' kapısı ekle (fail-closed, observability-self-test.py'ye mutation ile). Her alert için en az bir negatif test ekle ve kritik metrikler (saydin_activity_log_*, saydin_market_calendar_coverage_horizon_days, http_server_request_duration_seconds_*) için gerçek scrape çıktısından üretilmiş fixture ile ad/label doğrulaması yap.

---

### 30. Hardening validator'ı privileged / cap_add / host namespace kaçışlarını hiç reddetmiyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L15 — Production deployment ve observability |
| **Kategori** | security |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/deployment/validate-production.py:127-181; infrastructure/deployment/validation-self-test.py:34-67` |

**Bulgu.** Fail-closed üretim manifest validator'ı privileged, cap_add, devices, sysctls, group_add ve host namespace (pid/ipc/uts/userns) alanlarını hiç denetlemez; network_mode host yalnız ağ-scope kapısı bulunan birkaç servis için dolaylı yakalanır, diğerlerinde (redis, exporter'lar) sessizce geçer.

**Etki.** '29/29 mutation' kanıtı container-escape yüzeyi sınıfı için sahte güvence verir; debug amaçlı eklenen `cap_add: [SYS_PTRACE]`, `privileged: true` veya redis'te `network_mode: host` gibi bir değişiklik production-assurance job'ından ve release öncesi validate-production.sh'ten yeşil geçerek üretime çıkar.

**Öneri.** Servis başına şu reddetmeleri ekle: privileged truthy, cap_add boş değil, network_mode tanımlı, pid/ipc/uts/userns_mode host, devices/sysctls/group_add boş değil ve /var/run/docker.sock bind mount. Her biri için validation-self-test.py'ye mutation ekle.

---

### 31. Tek düz `management` ağı: uygulama container'ları kimlik doğrulamasız izleme kontrol düzlemiyle aynı segmentte

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L15 — Production deployment ve observability |
| **Kategori** | security |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/deployment/compose.production.yml:225,278,347,368,394,419,441,466,489,506,526,794; infrastructure/otel/loki.production.yml:1; infrastructure/deployment/validate-production.py:311-316,369-377` |

**Bulgu.** Uygulama container'ları, kimlik doğrulaması olmayan Alertmanager, Loki, Tempo, Prometheus ve OTLP alıcısıyla aynı düz `management` bridge ağındadır; bir container kompromizi doğrudan 'alarmı sustur + 30 günlük logu oku' yeteneğine dönüşür (post-exploitation izolasyon açığı, ön koşul RCE/SSRF).

**Etki.** saydin-api veya bir exporter'da RCE/SSRF durumunda saldırgan alertmanager:9093/api/v2/silences ile alarmları susturabilir, loki:3100 üzerinden 30 günlük uygulama loglarını sorgulayabilir, collector'a sahte telemetri enjekte edebilir. Uygulama katmanı ile denetim/alarm düzlemi arasında ayrıcalık sınırı yoktur.

**Öneri.** En azından uygulama container'larını alertmanager/loki/tempo ile aynı ağdan ayır (telemetry-ingest: api+ingestion+collector; monitoring-core: collector+prometheus+alertmanager+tempo+loki; scrape: prometheus+api/exporter'lar). Prometheus ve Alertmanager'a --web.config.file ile basic-auth/mTLS ekle; validate-production.py ağ-scope kapılarını yeni topolojiye göre güncelle.

---

### 32. Dead-man's-switch / watchdog alert'i ve harici heartbeat yok — susan Prometheus tespit edilemez

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L15 — Production deployment ve observability |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/prometheus/rules/*.yml; infrastructure/alertmanager/alertmanager.template.yml:4-25; infrastructure/deployment/compose.production.yml:444-490` |

**Bulgu.** Alert zincirinin sessiz arızasını tespit edecek hiçbir mekanizma yok: sürekli-firing bir watchdog kuralı, harici heartbeat receiver'ı ve monitoring servislerinde compose healthcheck'i bulunmuyor; Prometheus hang ederse 'alarm gelmiyor' ile 'her şey yolunda' ayırt edilemez.

**Etki.** Prometheus hang'i veya prometheus_data yazamama durumunda tüm kural değerlendirmesi durur ve operatör tarafında hiçbir sinyal değişmez; backup failure gibi kritik alarmlar sessizce kaybolur. lane-02 ile birleştiğinde bu zaten deploy sonrası varsayılan durumdur.

**Öneri.** `expr: vector(1)`, `labels: {severity: watchdog}` olan sürekli-firing bir SaydinWatchdog kuralı ve Alertmanager'da ayrı watchdog route/receiver ekleyip repo dışı bir dead-man's-switch servisine bağla. Ayrıca prometheus/alertmanager/tempo/loki/caddy servislerine compose healthcheck ekle.

---

### 33. Prometheus ve Alertmanager deploy'da hiç yeniden yaratılmıyor/reload edilmiyor — yeni release'in kural ve route değişiklikleri çalışan instance'a hiç ulaşmaz

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L15 — Production deployment ve observability |
| **Kategori** | ci-cd |
| **Doğrulama** | CONFIRMED (verifier) |
| **Konum** | `infrastructure/release/deploy-release.sh (tamamı); infrastructure/deployment/compose.production.yml:455-486` |

**Bulgu.** deploy-release.sh saydin-api ve caddy'yi açıkça `up -d --no-deps` ile yeniden yaratıyor (156, 165) ve bootstrap/migrator için `--force-recreate` kullanıyor; prometheus/alertmanager için ne `up`, ne `--force-recreate`, ne SIGHUP/`/-/reload` çağrısı var. `grep -rn 'enable-lifecycle|SIGHUP|reload'` deploy/promote/staging hattında yalnız PostgreSQL pg_reload_conf eşleşmesi veriyor; prometheus command'ında `--web.enable-lifecycle` yok (455-458), yani API ile reload da mümkün değil. Prometheus kural dosyalarını yalnız başlangıçta ve SIGHUP'ta yükler. Ayrıca prometheus.yml bir DOSYA bind mount'u (`../prometheus/prometheus.production.yml`), kaynağı self-hosted runner'ın `$GITHUB_WORKSPACE` checkout'u; checkout dosyayı yeniden yazdığında (yeni inode) çalışan container eski inode'u görmeye devam eder. Alertmanager config'i de bir kez okunan secret volume dosyası.

**Etki.** Alert kuralı, scrape config ve Alertmanager route düzeltmeleri üretime hiç ulaşmaz — observability düzleminde release süreci fiilen etkisizdir. lane-02 ile birlikte, monitoring düzlemi ya hiç çalışmıyor ya da sonsuza dek ilk kez başlatıldığı andaki konfigürasyonla çalışıyordur.

**Öneri.** deploy-release.sh'e `compose up -d --force-recreate prometheus alertmanager` (veya `--web.enable-lifecycle` + `/-/reload` çağrısı ve amtool ile config doğrulaması) ekle ve ardından Prometheus `/api/v1/rules` çıktısındaki kural sayısını/adlarını repo'daki kural setiyle karşılaştıran bir kapı koy.

---

### 34. WAL yalnız segment sınırında off-host'a gidiyor (`*.partial` hariç, `archive_timeout` yok); ilan edilen 15 dk RPO garanti edilmiyor ve WAL freshness metriği bunu ölçmüyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L16 — Backup/restore ve supply chain |
| **Kategori** | data-integrity |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/backup/backup-entrypoint.sh:163-176; infrastructure/deployment/compose.production.yml:13-24,774; infrastructure/prometheus/rules/host-backup.yml:38-44` |

**Bulgu.** Off-host WAL yalnız tamamlanmış segmentlerle sınırlıdır (`--exclude='*.partial'`) ve `archive_timeout` ayarlanmadığı için düşük yazma hacminde segment saatlerce dönmeyebilir; `write_metric wal` her turda koşulsuz yazıldığından WAL tazelik alarmı gerçek recovery-point gerilemesini değil yalnız döngü canlılığını ölçer.

**Etki.** Manifest'in ve README'nin 15 dakikalık RPO taahhüdü düşük-trafik pencerelerinde karşılanmaz; kaybolan veri son segment döndüğünden beri işlenen yazımlarla sınırlıdır. Daha önemlisi, `SaydinWalBackupStale` alarmı bu ihlali hiçbir zaman göremez — yeşil ama anlamsız bir gözlemlenebilirlik kapısıdır.

**Öneri.** PostgreSQL'e `archive_timeout` (ör. 300s) ekleyerek segment dönüşünü garanti et; `write_metric wal`'ı yalnız spool'da yeni tamamlanmış segment tespit edildiğinde yaz ve gerçek recovery-point tazeliğini ölçen ayrı bir metrik (`saydin_backup_wal_last_segment_timestamp_seconds`) + alarm ekle.

---

### 35. `base-backup-loop` önce 24 saat uyuyor ve tek hatada süreç ölüyor → her restart/hata sonrası ≥24 saatlik base yedeği boşluğu

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L16 — Backup/restore ve supply chain |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/backup/backup-entrypoint.sh:142-148 (karşı taraf: infrastructure/deployment/compose.production.yml:704, infrastructure/release/deploy-release.sh:144)` |

**Bulgu.** `base_backup_loop` yedek almadan önce 24 saat uyur ve `base_backup` içindeki herhangi bir hata `set -eu` nedeniyle tüm süreci sonlandırdığından, container her restart veya geçici hatadan sonra bir sonraki base denemesine kadar 24 saat daha bekler.

**Etki.** Base yedekleri 24 saatlik adımlarla starve olabilir; tekrarlayan bir hata sınıfında base hiç üretilmez. host-backup.yml:46-52 alarmı 26 saat sonra tetiklendiği için tamamen sessiz değildir, ancak DR zinciri gereksiz yere kırılgandır.

**Öneri.** Döngüyü önce-yedek-sonra-uyu sırasına çevir; `base_backup`'ı döngüyü öldürmeyecek şekilde sar (`if ! base_backup; then write_failure_metric base; sleep <kısa backoff>; continue; fi`) ve ardışık hata sayacına bağlı ayrı bir kritik alarm ekle.

---

### 36. Restic exclusive lock çakışması: günlük `forget --prune` ile 15 dakikalık WAL `backup` aynı repository'de yarışıyor, `--retry-lock` verilmemiş

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L16 — Backup/restore ve supply chain |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/backup/backup-entrypoint.sh:136-137,171-172` |

**Bulgu.** Base turundaki `restic forget --prune` repository üzerinde exclusive lock alır, 15 dakikalık WAL turundaki `restic backup` ise aynı repository'ye yazar ve hiçbir restic çağrısında `--retry-lock` verilmediğinden çakışan taraf beklemeden hata verip `set -eu` altında container'ı öldürür.

**Etki.** Günlük tekrarlayan yanlış-pozitif `SaydinBackupFailure` alarmı (host-backup.yml:54-59), WAL alıcı oturumunun kopup yeniden başlaması veya günlük base yedeğinin düşmesi (lane-04 ile birleşince bir sonraki deneme 24 saat sonra). Replication slot kalıcı olduğu için WAL verisi kaybolmaz.

**Öneri.** Tüm restic çağrılarına `--retry-lock 15m` ekle; `forget --prune`'u her base turundan ayırıp haftalık ayrı bir adıma taşı ve WAL turuyla zamanlamayı ayrıştır.

---

### 37. Restore drill'in `restore_rpo_exceeded` kapısı RPO'yu değil işlem yoğunluğunu ölçüyor; sessiz dönemlerde drill sahte başarısız olur

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L16 — Backup/restore ve supply chain |
| **Kategori** | test-quality |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/backup/restore-drill.sh:142-155; .github/workflows/promote-production.yml:80-86 (karşı taraf: infrastructure/backup/prepare-recovery.sh:16)` |

**Bulgu.** `recoveryLagSeconds` yedek tazeliğini değil, seçilen hedef ana en yakın commit'in yaşını ölçer; yazma trafiğinin olmadığı bir hedef seçilirse yedekler kusursuz olsa dahi drill `restore_rpo_exceeded` (veya hiç işlem yoksa `restore_recovery_timestamp_missing`) ile düşer ve aynı değer üretim promotion'ında sert kapı olarak kullanılır.

**Etki.** Aylık DR drill'i deterministik olmayan biçimde kırmızıya döner ve üretim promotion'ını bloke eder; ters yönde ise kapı gerçek bir RPO ihlalini ölçemez, çünkü off-host WAL tazeliğine hiç bakmaz. Kanıt üretimi güvenilmez.

**Öneri.** Sert kapıyı gerçek recovery-point sinyaline taşı: restore edilen kümedeki en son erişilebilir WAL segmentinin zaman damgası (veya off-host'a gönderilmiş en son segmentin yaşı) ile hedef arasındaki farkı kullan. Mevcut alanı korumak isteniyorsa adını `lastReplayedTransactionLagSeconds` yap ve gevşek/bilgilendirici bir eşik ver.

---

### 38. Drill sabit bir DQA çıktı dizinini mount ediyor ama DQA `--output` yolunun hiç var olmamasını şart koşuyor → ikinci ve sonraki drill'ler başarısız

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L16 — Backup/restore ve supply chain |
| **Kategori** | operability |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/backup/restore-drill.sh:79,86,173-195; src/Saydin.DataQualityAudit/EvidenceBundle.cs:231-236` |

**Bulgu.** Drill sabit ve kalıcı bir contract dizinini `/run/output` olarak mount edip DQA'yı `--output /run/output/evidence` ile çalıştırır, ancak DQA bu yolun hiç var olmamasını şart koşar ve drill hiçbir yerde temizlemez; aynı runner'daki ikinci koşu `evidence_output_must_be_absent` ile düşer.

**Etki.** Runbook'un 'en az aylık' taahhüt ettiği drill belgelenmemiş manuel temizlik olmadan tekrarlanamaz; üretim promotion'ı ≤31 günlük drill receipt'ine bağlı olduğundan release akışı da etkilenir.

**Öneri.** `--output` yolunu koşuya özel yap (`/run/output/$run_id/evidence`) veya `$audit_output` altında run-id'li alt dizini script içinde oluştur; alternatif olarak script başında çıktı dizininin boş olduğunu doğrula ve runbook/`restore-contract.env.example`'a bu gereksinimi yaz.

---

### 39. Saydin.Services.sln, DatabaseRoleBootstrap/DatabaseSecurity güvenlik-sınırı test projelerini içermiyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L17 — Build/compose/paketleme |
| **Kategori** | test-coverage |
| **Doğrulama** | CONFIRMED |
| **Konum** | `Saydin.Services.sln (proje listesi satır 6-44), docker-compose.yml:388, CLAUDE.md:27` |

**Bulgu.** Saydin.Services.sln, DatabaseRoleBootstrap.Tests ve DatabaseRoleBootstrap.IntegrationTests projelerini içermediği için `docker compose run --rm tests` (sln bazlı) bu güvenlik-sınırı davranış testlerini keşfedip çalıştırmaz — ancak DatabaseRoleBootstrap KAYNAK KODU, sln-üyesi Saydin.DatabaseMigrator.Tests.csproj'un ona verdiği ProjectReference sayesinde sln build'inde dolaylı olarak derlenir (compile hatası görünür kalır); yalnızca test-çalıştırma (davranış doğrulaması) eksik, derleme kapsamı eksik değil.

**Etki.** Yerel/IDE 'tüm solution' test komutu role/privilege-separation güvenlik sınırı testlerini sessizce atlar ve yanlış güven verebilir; ancak required CI (ci.yml) bu projeleri sln dışında elle restore/test ettiği için PR merge öncesi gerçek regresyonlar hâlâ yakalanır — üretim ve merge-gate riski yok.

**Öneri.** Saydin.DatabaseRoleBootstrap, Saydin.DatabaseSecurity, Saydin.DatabaseRoleBootstrap.Tests ve Saydin.DatabaseRoleBootstrap.IntegrationTests projelerini Saydin.Services.sln'e ekle (dotnet sln add); CLAUDE.md'deki 'tüm solution' iddiasını gerçek kapsamla uyumlu hale getir.

---

### 40. CLAUDE.md, tests compose servisinin yerelde gerçek Postgres'e asla bağlanamayacağını gizliyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L17 — Build/compose/paketleme |
| **Kategori** | docs |
| **Doğrulama** | CONFIRMED |
| **Konum** | `docker-compose.yml:364-388 (tests servisi + F2.6-21 üst yorumu), CLAUDE.md:29, tests/Saydin.Api.IntegrationTests/Fixtures/DatabaseFixture.cs:515-522` |

**Bulgu.** Bu commit, DatabaseFixture.cs'in PGHOST okumasına geçmesiyle BİRLİKTE docker-compose.yml tests servisinden ConnectionStrings__Postgres'i kaldırarak önceden yerelde çalışan gerçek-Postgres entegrasyon test yolunu kasıtlı olarak her-zaman-skip haline getirdi, ama hem CLAUDE.md:29 hem de docker-compose.yml'nin kendi F2.6-21 üst yorumu (satır 364-368) bu değişikliği yansıtmayacak şekilde eski/çelişkili haliyle bırakıldı.

**Etki.** Geliştirici CLAUDE.md:29'u veya docker-compose.yml'nin kendi üst yorumunu okuyup `docker compose run --rm tests test tests/Saydin.Api.IntegrationTests` çalıştırdığında testler PGHOST hiç set edilmediği için sessizce Skipped döner (kırmızıya dönmeden); geliştirici yanlışlıkla gerçek DB'ye karşı doğruladığını sanabilir.

**Öneri.** CLAUDE.md:29'u ve docker-compose.yml'nin F2.6-21 üst yorumunu (satır 364-368) güncel tasarımla uyumlu hale getir, veya tests servisine PGHOST/gerçek bağlantı wiring'ini geri ekleyip iddiayı gerçek kıl.

---

### 41. CalculationTelemetryTests global MeterListener kullanıyor ve aynı metriği üreten WhatIfCalculatorTests ile paralel koşuyor → yapısal flaky

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L18a — Saydin.Api test kalitesi |
| **Kategori** | test-quality |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.Api.Tests/Helpers/CalculationTelemetryTests.cs:15-50; src/Saydin.Api/Helpers/CalculationTelemetry.cs:36-37; src/Saydin.Api/Services/WhatIfCalculator.cs:38,78,167` |

**Bulgu.** CalculationTelemetryTests, süreç-genel bir MeterListener ile `saydin.whatif.*` enstrümanlarının TAM 3 ölçüm üretmesini bekliyor; aynı enstrümanlara yazan WhatIfCalculatorTests ayrı bir xUnit collection'ı olduğu için paralel koşabilir ve listener penceresinde fazladan ölçüm enjekte ederek testi sözleşme bozulmadan kırabilir.

**Etki.** Yanlış-pozitif suite kırmızısı (flake). 'API unit 545/545, 0 failed' kabul kanıtının tekrarlanabilirliği zayıflar; .github/scripts/run-unit-coverage.sh:81 `passed -eq total` kapısı tek bir flake'te CI'ı düşürür.

**Öneri.** Testin dinlediği ölçümleri benzersizleştir (ör. operation adına test-özgü bir sentinel ekle ve yalnız onu topla) ya da CalculationTelemetry'ye enjekte edilebilir `Meter`/`IMeterFactory` ver. Alternatif olarak CalculationTelemetryTests ve WhatIfCalculatorTests'i aynı `[Collection]` altına al (assembly geneli paralelliği kapatmak gereksiz maliyetli).

---

### 42. Installation credential filtresinin 429/503 admission dalları hiçbir testle kapsanmıyor; HTTP integration factory limiter'ı tamamen kapatıyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L18a — Saydin.Api test kalitesi |
| **Kategori** | test-coverage |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.Api.Tests/Endpoints/InstallationAuthenticationFilterTests.cs:16-45,128-134; src/Saydin.Api/Endpoints/EndpointExtensions.cs:56-85,138-151; tests/Saydin.Api.IntegrationTests/ErrorContractHttpTests.cs:162,174` |

**Bulgu.** `RequireInstallationCredential` filtresinin principal-admission 429 ve 503 dalları hiçbir unit veya integration testiyle yürütülmüyor (401 dalı kapsanıyor); tek unit test limiter'ı hep Allowed döndürüyor, tüm HTTP integration ise limiter'ı Enabled=false ile kapatıyor — buna karşılık OpenAPI sözleşmesi bu iki status'u her korumalı rotada kilitliyor.

**Etki.** Kimlik doğrulama sonrası dağıtık abuse koruması (status, Retry-After, `code`, next'in çağrılmaması) üretimde sessizce bozulabilir ve OpenAPI sözleşmesiyle runtime ayrışabilir. Aynı kör nokta EndpointExtensions.cs:69/:84'teki hardcoded İngilizce metinleri de gizliyor.

**Öneri.** InstallationAuthenticationFilterTests'e `SecurityLimiterDecision.Limited(...)` ve `.Unavailable` döndüren sahte limiter ile iki test ekle (status, `code`, `application/problem+json`, Retry-After tavanı, `next` çağrılmaması). Ek olarak en az bir HTTP integration testinde `DistributedSecurityLimiter:Enabled=true` + düşük `PrincipalLimit` ile gerçek Redis üzerinden uçtan uca 429 doğrula.

---

### 43. Hesaplama endpoint'lerinin activity-log payload'ı test edilmiyor; TelemetryPrivacyTests'in finansal redaksiyon testi kendi girdisini redakte ediyor (tautolojik)

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L18a — Saydin.Api test kalitesi |
| **Kategori** | test-quality |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.Api.Tests/Endpoints/TelemetryPrivacyTests.cs:14-26; src/Saydin.Api/Endpoints/WhatIfEndpoints.cs:70-85,101-120,135-147; src/Saydin.Api/Endpoints/DcaEndpoints.cs:43-56` |

**Bulgu.** WhatIf (calculate/compare/reverse) ve DCA endpoint'lerinin activity-log payload'ları inline anonim nesnelerdir ve hiçbir test onları yürütmez; TelemetryPrivacyTests'in tek finansal redaksiyon testi payload'ı kendisi redakte ederek kurduğu için tautolojiktir (Assets payload'ları ham finansal değer taşımadığından bu kapsamın dışındadır).

**Etki.** Bir geliştirici WhatIf/DCA payload'ına `request.Amount` veya `result.ProfitLossTry` gibi ham finansal değeri eklerse ya da `AmountBucket.Coarse`/`TelemetryOutcome.From` sarmalayıcısını kaldırırsa hiçbir test kırılmaz; ADR-006 finansal activity-log politikası ve API-06 bucketing kuralı sessizce ihlal edilir.

**Öneri.** WhatIf ve DCA için de `ScenariosEndpoints.CreateSaveActivityData` desenindeki `internal static` payload factory'lerini çıkar ve TelemetryPrivacyTests'te sentinel tutar/kâr değerleriyle (ör. 8_765_432m) `GetRawText()` içinde bulunmadığını, buna karşılık `amountBucket`/`outcome` alanlarının bulunduğunu doğrula.

---

### 44. Dağıtık security limiter'ın 429/503 yanıtları hardcoded İngilizce; karşılık gelen lokalize resx key'leri mevcut ama hiçbir yerde kullanılmıyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L18a — Saydin.Api test kalitesi |
| **Kategori** | localization |
| **Doğrulama** | CONFIRMED (verifier) |
| **Konum** | `src/Saydin.Api/Endpoints/EndpointExtensions.cs:69,84,138-151; src/Saydin.Api/Security/DistributedSecurityLimiterMiddleware.cs:53,70,72-93; src/Saydin.Api/Resources/ErrorMessages.resx:49-50` |

**Bulgu.** Kullanıcıya dönen iki yanıt yolu da `title`'ı hardcoded İngilizce yazıyor: EndpointExtensions.cs:69 `"Too many requests."`, :84 `"Request admission is temporarily unavailable."` ve DistributedSecurityLimiterMiddleware.cs:53/:70 aynı iki metin. Her ikisi de `IStringLocalizer<ErrorMessages>` KULLANMIYOR (EndpointExtensions.cs:114'te 401 yolu localizer kullanıyor, yani sınıf içinde bile tutarsız). Buna karşılık src/Saydin.Api/Resources/ErrorMessages.resx:49-50 ve ErrorMessages.en.resx:49-50 `RateLimited` ("Çok fazla istek") ve `RateLimitedDetail` key'lerini tanımlıyor; `grep -rn "RateLimited" src/ --include=*.cs` yalnız ApiErrorCodes.cs:31'deki sabit kodu döndürüyor — resx key'lerini TÜKETEN hiçbir kod yok. Program.cs'te `AddRateLimiter`/`OnRejected` de yok. Yine de tests/Saydin.Api.Tests/Localization/ErrorMessagesLocalizationTests.cs bu iki key'i `[InlineData("RateLimited")]`/`[InlineData("RateLimitedDetail")]` ile pinliyor ve tr/en ayrımını doğruluyor → 429 yolunun lokalize olduğu yanılsaması yaratıyor. Ayrıca her iki yanıt da `detail` alanı hiç göndermiyor.

**Etki.** Türk kullanıcılara yönelik üründe kullanıcıya dönen iki hata başlığı her dilde İngilizce döner. CLAUDE.md'nin 'kullanıcıya dönecek string'lerde hardcoded Türkçe/İngilizce YASAK, IStringLocalizer<ErrorMessages> kullan' kuralının doğrudan ihlali; lokalizasyon testi bu ihlali maskeliyor çünkü kullanılmayan key'lerin varlığını doğruluyor.

**Öneri.** Her iki yolda da `IStringLocalizer<ErrorMessages>` üzerinden `RateLimited`/`RateLimitedDetail` (ve 503 için yeni bir `LimiterUnavailable`/`LimiterUnavailableDetail` çifti) kullan; ErrorMessagesLocalizationTests'e bu key'leri ekleyip ayrıca 429/503 yanıt gövdesinin `Accept-Language: tr` altında Türkçe döndüğünü doğrulayan bir handler/filter testi yaz.

---

### 45. CLAUDE.md'nin kanonik commit kapısı `docker compose run --rm tests` gerçek-PG integration testleri yüzünden zorunlu olarak kırmızı

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L18c — Migrator/RoleBootstrap test kalitesi |
| **Kategori** | ci-cd |
| **Doğrulama** | CONFIRMED |
| **Konum** | `docker-compose.yml:368-387; tests/Saydin.DatabaseMigrator.Tests/IntegrationEnvironment.cs:12-22; tests/Saydin.DataQualityAudit.IntegrationTests/IntegrationEnvironment.cs:22-26; tests/Saydin.DataRepair.IntegrationTests/RepairIntegrationEnvironment.cs:25-28; Saydin.Services.sln:28; CLAUDE.md:27` |

**Bulgu.** CLAUDE.md:27 ve :66'da kanonik commit kapısı olarak gösterilen `docker compose run --rm tests` komutu tüm solution'ı Debug'da koşturur; ancak migrator (124 vaka), DQA ve DataRepair integration projeleri env değişkeni yokken `Skip` değil `InvalidOperationException` fırlatacak biçimde yazılmıştır, dolayısıyla bu kapı lokalde deterministik olarak yüzlerce hata verir.

**Etki.** Dokümante edilmiş tek lokal test kapısı hiçbir zaman yeşil olamaz; ekip ya kapıyı terk eder ya da hataları 'beklenen' sayarak görmezden gelir, bu da gerçek regresyonların commit öncesi görünmez kalmasına yol açar. CI kapıları sağlam olduğu için üretim riski yok; etki geliştirme akışı ve dokümantasyon-gerçek uyumsuzluğu düzeyinde.

**Öneri.** `tests` compose servisinin komutunu gerçek-PG gerektiren projeleri dışlayacak biçimde daralt (proje listesi veya `--filter`), ya da bu üç projede `SAYDIN_*_TEST_REQUIRED` bayrağı yokken `Skip.If*` ile zarifçe skip et ve CI'da bayrak zorunlu olduğu için fail-closed davranışı koru (run-migrator-tests.sh:11-14 zaten bayrağı zorunlu kılıyor). CLAUDE.md'deki komut açıklamasını gerçek davranışla hizala.

---

### 46. Impact manifest imza zincirinin kökü olan pinned public-key SHA-256 kontrolü hiç test edilmiyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L18c — Migrator/RoleBootstrap test kalitesi |
| **Kategori** | test-quality |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.DatabaseMigrator/MigrationImpactManifest.cs:385-404; tests/Saydin.DatabaseMigrator.Tests/MigrationImpactManifestTests.cs:76-88,236-252; tests/Saydin.DatabaseMigrator.Tests/ImpactTestPackage.cs:118-133` |

**Bulgu.** `migration_impact_public_key_mismatch` ve `migration_impact_public_key_invalid` kodlarını tetikleyen hiçbir test yoktur; her iki test fixture'ı da imza anahtarını üretip pin'i aynı anahtardan türettiği için pin karşılaştırması testler tarafından hiç zorlanmaz.

**Etki.** DBM-004'ün güven kökü (release'in impact dizinindeki public key'in env'deki pin ile eşleşmesi) test tarafından mühürlenmemiştir. Bu karşılaştırma gevşetilirse/kaldırılırsa mevcut 7 saf-unit ve 7 gerçek-PG impact testi yeşil kalır; impact dizinine yazabilen bir aktör kendi anahtarıyla imzaladığı manifestle bütçe/hedef/sınıflandırma kısıtlarını yeniden yazabilir. Bugün kod doğrudur; risk regresyonun görünmez olmasıdır.

**Öneri.** İki negatif unit test ekle: (1) doğru imzalı manifest + `PublicKeySha256` başka bir anahtarın hash'i → `migration_impact_public_key_mismatch`; (2) manifest+imza+PEM tamamen ikinci anahtarla üretilip pin eski anahtarda bırakılır → yine `migration_impact_public_key_mismatch`. Ayrıca PEM'i bozup `migration_impact_public_key_invalid` yolunu da kapat.

---

### 47. Impact manifest reddetme kodlarının 9/13'ü hiçbir testte doğrulanmıyor — manifest↔migration bağlayıcı `migration_impact_identity_mismatch` dahil

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L18c — Migrator/RoleBootstrap test kalitesi |
| **Kategori** | test-quality |
| **Doğrulama** | CONFIRMED (verifier) |
| **Konum** | `src/Saydin.DatabaseMigrator/MigrationImpactManifest.cs:230,245,254,264,276,313,359,392,404,726,753,768; tests/Saydin.DatabaseMigrator.Tests/MigrationImpactManifestTests.cs; tests/Saydin.DatabaseMigrator.Tests/MigrationRunnerIntegrationTests.cs` |

**Bulgu.** MigrationImpactManifest.cs içindeki 13 ayrı `MigratorRejectedException` kodu çıkarıldı ve `tests/` altında arandı. Yalnız 4'ü test ediliyor: `migration_impact_budget_invalid` (MigrationImpactManifestTests.cs:135), `migration_impact_configuration_invalid`, `migration_impact_configuration_required` (MigrationRunnerIntegrationTests.cs:54) ve dolaylı olarak `migration_impact_signature_invalid` / `migration_impact_static_classification_mismatch`. Hiç testi olmayanlar: `migration_impact_file_set_mismatch` (impact dizininde beklenmeyen/eksik dosya seti), `migration_impact_identity_mismatch` (manifest'in `migrationSha256`/`migrationVersion` alanının gerçek migration checksum'ına `CryptographicEquals` ile bağlanması — satır 241-245), `migration_impact_predecessor_invalid` (satır 254 ve 264; `requiredSchemaManifestSha256` = `manifest.ChecksumThrough(index)` bağı), `migration_impact_relation_invalid`, `migration_impact_postcondition_invalid`, `migration_online_plan_contract_invalid` (satır 313 ve 359), `migration_impact_manifest_missing`, `migration_impact_public_key_invalid`, `migration_impact_public_key_mismatch`, `migration_sql_lexically_invalid`. Mevcut testlerin tamamı fixture'ın ürettiği tutarlı manifest'i kullandığı için bu kapıların hiçbiri zorlanmıyor.

**Etki.** Impact manifest'i migration dosyasına ve şema manifest zincirine bağlayan kriptografik bağların hiçbiri regresyon testiyle mühürlenmemiştir. Bu bağlar koparsa, bir migration'a ait imzalı manifest başka bir migration için yeniden kullanılabilir veya predecessor kısıtı atlatılabilir — yani bütçe/kilit/timeout kısıtları yanlış migration'a uygulanır. Bugün kod doğrudur; risk sessiz regresyondur.

**Öneri.** SignedImpactFixture'a manifest alanlarını bozmaya izin veren bir `mutateDocument` kancası ekle (mevcut `mutateBudgets` deseni gibi) ve en az şu negatifleri mühürle: yanlış `migrationSha256` → `migration_impact_identity_mismatch`; yanlış `requiredSchemaManifestSha256`/`requiredPredecessorVersion` → `migration_impact_predecessor_invalid`; impact dizinine fazladan bir `.impact.json` → `migration_impact_file_set_mismatch`; manifest dosyasını sil → `migration_impact_manifest_missing`. Public key pinini de lane-03'teki iki testle kapat.

---

### 48. DQA'da 13 ihlal/preflight kodu için hiç bozuk-veri enjeksiyonu yok; enflasyon fence'i fiyat tarafıyla simetrik test edilmemiş

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L18e — DQA/DataRepair test kalitesi |
| **Kategori** | test-quality |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.DataQualityAudit/AuditRunner.cs:153,159,181,217,224,286,332,549,551,647,999,1028,1050; tests/Saydin.DataQualityAudit.IntegrationTests/AuditDatabaseFixture.cs:1588,1606; tests/Saydin.DataQualityAudit.IntegrationTests/DataQualityAuditAcceptanceTests.cs:390-406` |

**Bulgu.** AuditRunner'daki 13 preflight/ihlal kodu (target_or_read_only_mismatch, target_system_identifier_mismatch, required_schema_object_missing, migration_set_mismatch, migration_checksum_or_state_mismatch, query_budget_exceeded, window_budget_exceeded, price/inflation_primary_key_drift, backup_role_version_set_drift, inflation_fence_trigger_drift, calendar_payload_invalid, calendar_release_missing) hiçbir unit veya gerçek-PG testinde tetiklenmiyor; özellikle enflasyon write-fence'i, fiyat tarafındaki iki drift varyantına rağmen simetrik olarak test edilmemiş.

**Etki.** Bu detektörlerin dedektif gücü yalnız temiz DB üzerinde 'ihlal üretmeme' ile gözleniyor; negatif yön kanıt değil. Migration kurcalaması, eksik şema nesnesi, enflasyon write-fence'inin devre dışı bırakılması veya hedef sistem kimliği uyuşmazlığı için karşılaştırma mantığı sessizce bozulursa audit yeşil raporlar ve operatör yanlış güvence alır.

**Öneri.** Suite'te zaten kullanılan try/finally + 'cleanup sonrası clean' desenini bu kodlara da uygulayın: schema_migrations checksum'ını bozun, bir satır silin, gerekli bir tabloyu geçici yeniden adlandırın, manifest'e yanlış systemIdentifierSha256 koyun, `trg_inflation_rates_ingestion_fence`'i fiyat tarafındaki iki varyantla bozun, PK'ları yeniden tanımlayın, düşük satır/pencere bütçesiyle window_budget_exceeded tetikleyin. Ardından CI'daki `--minimum-executed 72` ratchet'ini yükseltin.

---

### 49. 'target/system/deployment/role mismatch kapsanır' kabul iddiası testlerle örtüşmüyor; canlı trust doğrulamaları test edilmiyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L18e — DQA/DataRepair test kalitesi |
| **Kategori** | test-quality |
| **Doğrulama** | CONFIRMED |
| **Konum** | `docs/analysis/06-remediation-progress.md:615-617; src/Saydin.DataRepair/RepairTrustLease.cs:113,146,163,174,176,211; src/Saydin.DataRepair/RepairDatabase.cs:55; tests/Saydin.DataRepair.IntegrationTests/RepairExecutorIntegrationTests.cs:31-51` |

**Bulgu.** Kabul dokümanının 'target/system/deployment/role mismatch kapsanır' iddiasının karşılığı yalnız CLI tarafındaki `ValidateTarget`'ın environment alanına yapılan tek testtir; canlı-DB trust doğrulamalarının (physical target, role contract, migration control state/set, audit read-only, database ACL) hiçbiri negatif yönde tetiklenmiyor.

**Etki.** Yanlış küme/veritabanı üzerinde ya da yazma yetkisi olan bir audit kimliğiyle repair çalıştırılmasını engelleyen en kritik canlı kontroller regresyona tamamen açık; kabul dokümanı bunları doğrulanmış gibi kaydediyor.

**Öneri.** Her canlı doğrulama için negatif test ekleyin: planda yanlış systemIdentifierSha256/deploymentId/rolePrefix; `saydin_role_contract` satırında geçici drift; `saydin_migration_control.state`'i 'ready' dışına alma; `schema_migrations`'tan satır silme (set mismatch); audit login'ine geçici UPDATE grant verip repair_audit_role_not_read_only bekleme. Bunlar eklenmeyecekse 06 dokümanındaki iddiayı gerçek kapsama daraltın.

---

### 50. CLAUDE.md aynı commit'in kendi kod değişikliklerini yansıtmıyor (IDeviceContext, exception handler zinciri, migration aralığı)

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L19 — Dokümantasyon, ADR, runbook |
| **Kategori** | docs |
| **Doğrulama** | CONFIRMED |
| **Konum** | `CLAUDE.md:187-193,227-232,364` |

**Bulgu.** CLAUDE.md aynı commit'te kaldırılan IDeviceContext/RequireDeviceId modelini hâlâ anlatıyor, yeni eklenen RequestBodyTooLargeExceptionHandler(413)/QuotaUnavailableExceptionHandler(503)'ü exception handler zincirine eklemiyor ve migration aralığını hâlâ '001..014' olarak veriyor.

**Etki.** CLAUDE.md'yi otoritatif referans alan bir ajan/geliştirici artık var olmayan bir API'yi enjekte etmeye çalışır veya yeni exception handler eklerken güncel kontrat setini (413/503 dahil) eksik anlar.

**Öneri.** İstek Bağlamı bölümünü IInstallationPrincipalContext'e güncelle; handler listesine 413/503'ü ekle; migration yorumunu güncel terminal sayıya göre düzelt.

---

### 51. docs/high-traffic-checklist.md hâlâ Redis allkeys-lru öneriyor; aynı commit'in noeviction zorunluluğuyla çelişiyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L19 — Dokümantasyon, ADR, runbook |
| **Kategori** | docs |
| **Doğrulama** | CONFIRMED |
| **Konum** | `docs/high-traffic-checklist.md:13; infrastructure/deployment/README.md:64-65; infrastructure/deployment/validate-private-material.py:92-94; docs/runbooks/redis-unavailable.md:11-12` |

**Bulgu.** high-traffic-checklist.md hâlâ Redis için allkeys-lru öneriyor; bu, aynı commit'in production deployment doğrulayıcısı ve runbook'unun zorunlu tuttuğu noeviction politikasıyla doğrudan çelişiyor, ancak resmi deploy akışı otomatik doğrulayıcı ile korunduğundan doğrudan istismar riski sınırlı.

**Etki.** Checklist'i literal takip eden bir operatör resmi deploy dışı bir Redis'i allkeys-lru ile kurarsa kota/security-limiter key'leri bellek baskısında evict edilebilir; resmi deploy akışında validate-private-material.py bunu reddeder.

**Öneri.** high-traffic-checklist.md:13'ü 'maxmemory-policy: noeviction (sabit)' olarak düzelt; maxmemory boyutlandırmasını ayrı madde yap.

---

### 52. docs/architecture/database-schema.md 'Doğruluk kaynağı' ve 'Tam Şema' bölümü migration 020-022'yi kapsamıyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L19 — Dokümantasyon, ADR, runbook |
| **Kategori** | docs |
| **Doğrulama** | CONFIRMED |
| **Konum** | `docs/architecture/database-schema.md:3-5,63,108-402,415,516-568` |

**Bulgu.** database-schema.md'nin başlık/migration-zinciri özeti hâlâ 001-019 sınırında ve 'tam 21 version' diyor, ama dosyanın kendisi 020-022'yi prose olarak anlatıyor; 'Tam Şema' CREATE TABLE listesi bu üç migration'ın eklediği tabloları SQL düzeyinde içermiyor.

**Etki.** Kanonik-iddialı şema referansı son 8 migration'ın gerçek SQL şemasını belgelemiyor; okuyucu ham SQL dosyalarına gitmek zorunda kalıyor.

**Öneri.** Mermaid/tablo özetini ve 'Tam Şema' CREATE TABLE listesini 020-022'yi kapsayacak şekilde güncelle; 'tam 21 version' ifadesini güncel sayıya çevir.

---

### 53. ADR-008 durumu 'migration 018 bekliyor' diyor ama migration 018 zaten mevcut ve tamamlanmış

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L19 — Dokümantasyon, ADR, runbook |
| **Kategori** | docs |
| **Doğrulama** | CONFIRMED |
| **Konum** | `docs/decisions/ADR-008-scenario-payload-pagination.md:3,91-107; infrastructure/postgres/migrations/018_scenario_integrity.sql:1-80; docs/analysis/06-remediation-progress.md:30` |

**Bulgu.** ADR-008'in durum satırı ve 'Migration 018 Release Gate'i' bölümü migration 018'in henüz yazılmadığını ima ediyor, ama dosya zaten mevcut ve ADR'nin 1-3. maddelerini tam olarak uyguluyor; yalnız 4-5. maddedeki (fixture testleri, EXPLAIN kanıtı) durum belirsiz kalmış olabilir.

**Etki.** ADR statüsü ile gerçek repo durumu arasındaki tutarsızlık karar kaydının güvenilirliğini zedeliyor; remediation-progress.md ile ADR-008 birbirini doğrulamıyor.

**Öneri.** ADR-008 durum satırını 'migration 018 uygulandı; 4-5. madde kanıtı açık/kapalı' şeklinde netleştir.

---

### 54. docs/cache-strategy.md 'Kullanım Sayacı' prefix listesi kodda var olan usage:assets: prefix'ini içermiyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L19 — Dokümantasyon, ADR, runbook |
| **Kategori** | docs |
| **Doğrulama** | CONFIRMED |
| **Konum** | `docs/cache-strategy.md:129-133; src/Saydin.Api/Endpoints/AssetsEndpoints.cs:13,89-90,120-121,174-175` |

**Bulgu.** cache-strategy.md'nin prefix listesi yalnız usage:whatif: ve usage:dca:'yı sayıyor; AssetsEndpoints.cs'in tanımladığı ve üç asset endpoint'inde aktif kullanılan usage:assets: prefix'i dokümante edilmemiş.

**Etki.** Kota mimarisinin üçüncü bir yüzeyi dokümante değil; operasyonel debug/kapasite planlamasında usage:assets:* key'leri gözden kaçabilir.

**Öneri.** Prefix listesine usage:assets: (AssetsEndpoints, ResolveDailyAssetQueryLimitAsync) satırını ekle.

---

### 55. observability-game-day.md gerçekte var olmayan SaydinIngestionStale alert'ini referans veriyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L19 — Dokümantasyon, ADR, runbook |
| **Kategori** | docs |
| **Doğrulama** | CONFIRMED |
| **Konum** | `docs/runbooks/observability-game-day.md:11; infrastructure/prometheus/rules/ingestion.yml:4,12,23,33` |

**Bulgu.** observability-game-day.md, ingestion freshness satırında var olmayan tekil bir alert adı (SaydinIngestionStale) kullanıyor; gerçek kurallar SaydinDailyIngestionStale ve SaydinMonthlyIngestionStale (ayrıca SaydinIngestionFreshnessMetricMissing) olarak tanımlı.

**Etki.** Game-day tatbikatının bir satırı gerçek alerting davranışını doğrulayamaz; operatör yanlış alert adı arayarak zaman kaybeder veya maddeyi hatalı şekilde başarısız sayar.

**Öneri.** Tabloyu SaydinDailyIngestionStale / SaydinMonthlyIngestionStale referans verecek şekilde düzelt.

---

### 56. CONTRIBUTING.md 'Zorunlu kontroller' bölümü CLAUDE.md'nin Docker-only kuralıyla çelişiyor

| | |
|---|---|
| **Önem** | Medium |
| **Hat** | L19 — Dokümantasyon, ADR, runbook |
| **Kategori** | docs |
| **Doğrulama** | CONFIRMED |
| **Konum** | `CONTRIBUTING.md:30,33-40; CLAUDE.md:8-15,25-27; .github/scripts/run-unit-coverage.sh:54-66; docs/development-guide.md:236-246` |

**Bulgu.** Bu commit'te yeni eklenen CONTRIBUTING.md, 'Zorunlu kontroller' bölümünde dotnet komutlarını doğrudan host'ta çalıştırmayı öneriyor; bu, CLAUDE.md'nin KRİTİK 'SDK yok, her zaman Docker Compose kullan' kuralıyla çelişiyor ve development-guide.md'deki telafi edici uyarı CONTRIBUTING.md'de yok.

**Etki.** SDK kurulu olmayan bir katkıda bulunan CONTRIBUTING.md'yi izlerse komutlar başarısız olur; iki üst düzey doküman aynı konuda çelişkili talimat veriyor.

**Öneri.** CONTRIBUTING.md'ye CLAUDE.md'nin Docker-only kuralına referans/uyarı ekle veya komutları SDK imajı + mount ile göster.

---

---

## Ana agent ek bulgusu (hat kapsamı dışında kalan çapraz gözlem)

### MA-A. Fail-closed limiter tüm API'yi Redis'e bağlı tek arıza noktası yapıyor

| | |
|---|---|
| **Önem** | Medium (bilinçli tasarım kararı — belgelenmesi ve alarmlanması eksik) |
| **Hat** | Çapraz (L01 + L04 + L15 kesişimi; hiçbir hat bu haliyle kaydetmedi) |
| **Kategori** | operability |
| **Doğrulama** | Ana agent, kod okunarak |
| **Konum** | `src/Saydin.Api/Security/DistributedSecurityLimiterMiddleware.cs:36-57`, `src/Saydin.Api/Program.cs` (pipeline sırası) |

**Bulgu.** `DistributedSecurityLimiterMiddleware` istek hattının önünde çalışıyor ve limiter
`Unavailable` döndüğünde 503 üretiyor. Limiter `SecurityLimiterDecision.Unavailable`'ı her Redis
istisnasında döndürdüğü için (`DistributedSecurityLimiter.cs:143-146`), Redis erişilemez olduğunda
`/health/live` dışındaki **her endpoint** 503 döner. Bu bilinçli ve doğru gerekçelendirilmiş bir
fail-closed tercihidir; ancak availability açısından yeni bir tek arıza noktası yaratır.

**Etki.** Redis kesintisi artık yalnız cache kaybı değil, tam API kesintisi anlamına geliyor.
`docs/runbooks/redis-unavailable.md` bu davranışı açıkça yazmıyor; on-call bu durumu
"cache düştü, degrade çalışıyoruz" diye yorumlayıp yanlış öncelik verebilir.

**Öneri.** Runbook'a "Redis down ⇒ API tamamen 503" satırını ekle, `SaydinRedisUnavailable`
alert severity'sini bu gerçeğe göre yükselt, ve `security_limiter_unavailable` kodunu ayrı bir
sayaç/etiketle ölçülebilir kıl (bkz. L04 Medium bulgusu — logsuz yutma).
