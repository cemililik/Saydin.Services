# Hat Bazlı Review Kaydı

> 22 uzman hattının her biri için: kapsam beyanı, doğrulanmış bulgu dağılımı, **reddedilen**
> bulgular (false-positive izi) ve kayda değer güçlü kararlar.
> Reddedilen bulgular bilinçli olarak kayıtta tutulur — bir iddianın neden yanlış olduğunu
> bilmek, iddianın kendisi kadar değerlidir.

## Dağılım tablosu

| Hat | Kapsam | Critical | High | Medium | Low | Reddedilen |
|---|---|---:|---:|---:|---:|---:|
| L01 | API kimlik ve güvenlik yüzeyi |  | 2 | 3 | 4 |  |
| L02 | Scenario payload, pagination ve gövde sınırları |  |  | 1 | 8 |  |
| L03 | Finansal hesaplama, cache, kota ve authority |  | 1 | 1 | 7 |  |
| L04 | API runtime, activity logging, exception zinciri |  | 1 | 2 | 9 | 1 |
| L05 | Shared entity/EF ↔ SQL şema paritesi |  |  | 1 | 7 |  |
| L06 | SQL migration 015–022 ve online protokol |  |  | 1 | 3 | 2 |
| L07 | Saydin.DatabaseMigrator |  |  | 1 | 11 |  |
| L08 | DatabaseRoleBootstrap + DatabaseSecurity | 1 | 1 | 2 | 5 |  |
| L09 | Ingestion window ledger, write fence, supervision |  | 2 | 4 | 3 |  |
| L10 | Provider adapter/mapper ve observation authority |  | 1 | 4 | 6 |  |
| L11 | Saydin.DataQualityAudit |  |  | 1 | 5 |  |
| L12 | Saydin.DataRepair |  |  | 2 | 8 |  |
| L13 | calendar-data aracı ve calendar infrastructure |  |  | 4 | 11 |  |
| L14 | CI/CD workflow ve kapı script'leri |  |  |  | 10 | 1 |
| L15 | Production deployment ve observability |  | 2 | 6 | 8 |  |
| L16 | Backup/restore ve release supply chain | 1 | 2 | 5 | 7 | 1 |
| L17 | Build, compose ve paketleme |  |  | 2 | 3 |  |
| L19 | Dokümantasyon, ADR ve runbook tutarlılığı |  |  | 7 |  |  |
| L18a | Saydin.Api test kalitesi |  |  | 4 | 8 |  |
| L18b | PriceIngestion + calendar-data test kalitesi |  | 1 |  | 10 | 1 |
| L18c | DatabaseMigrator + RoleBootstrap test kalitesi |  |  | 3 | 7 |  |
| L18e | DataQualityAudit + DataRepair test kalitesi |  | 1 | 2 | 9 |  |

---

## L01 — API kimlik ve güvenlik yüzeyi

**Doğrulanmış bulgu:** 0 Critical · 2 High · 3 Medium · 4 Low

**Kapsam.** L01.files listesindeki 18 dosyanın tamamı (yeni olanlar baştan sona, `EndpointExtensions.cs` hem tam hem `a274c62..f9f608d` diff'i olarak) okundu; karşı taraf olarak `Program.cs` DI/pipeline kaydı, `ApiRuntimeContract`, `021_api_trust_expand.sql` (register/resolve/begin/commit/revoke fonksiyonları), `SecureSecretFile`, `DailyLimitGuard`, `SavedScenarioService`/`AssetsEndpoints`/`WhatIfCalculator` çağıranları, `ActivityLogBuilder`, `ErrorMessages.resx`, ilgili unit + integration testleri ve ADR-003/ADR-010 ile `docs/analysis/06-remediation-progress.md` kabul iddiaları denetlendi. Okunmayanlar: 022 migration'ın retention/trigger gövdesi (başka hat), PriceIngestion/DQA/RoleBootstrap tarafı, cache içerik mantığı. Bir bulgu (L01-01) `mcr.microsoft.com/dotnet/sdk:10.0` içinde minimal ASP.NET Core 10 probe uygulaması çalıştırılarak deneysel olarak doğrulandı.

**Güçlü kararlar.**

- Ham credential hiçbir zaman veritabanına gitmiyor: keyring in-process 32 byte CSPRNG üretiyor, yalnız HMAC-SHA256 verifier + key version DB'ye yazılıyor; API rolü tabloya doğrudan yetkili değil, yalnız `SET search_path=pg_catalog,pg_temp` ile kilitlenmiş `SECURITY DEFINER` fonksiyonlarını çağırıyor ve tüm çağrılar `$1..$n` parametreli (InstallationRepository.cs:14-61) — SQL interpolation yok.
- Base64url çözümü kanonik: 43 karakter uzunluk, alfabe kontrolü ve son karakterin kullanılmayan 2 bitinin sıfır olması zorunlu (`InstallationCredentialKeyring.cs:166-189`). Bu, aynı 32 bayta çözülen birden fazla token varyantını (credential aliasing) engelliyor ve testle mühürlenmiş.
- İki fazlı rotation gerçek anlamda güvenli kurgulanmış: principal başına `pg_advisory_xact_lock` alındıktan SONRA yeniden okuma (021:317-341), commit'te eski aktif credential'ın atomik revoke'u + pending'in activate'i, idempotent commit ve pending credential'ın iş API'sinde geçersizliği. Bunların hepsi gerçek PostgreSQL'e karşı uçtan uca doğrulanıyor (InstallationHttpIntegrationTests.cs:54-91).
- Enumeration'a kapalı tek tip 401 sözleşmesi: malformed / bilinmeyen / süresi geçmiş / revoked durumlarının hepsi aynı gövdeye düşüyor; endpoint'lerdeki `catch (PostgresException) when (SqlState == 28000)` döngüleri hangi key version'ın eşleştiğini sızdırmıyor; integration testi gövdede "hash", "version", "revoked" geçmediğini de assert ediyor.
- Dağıtık limiter kararı tek bir atomik Lua script'inde: pencere otoritesi olarak Redis `TIME`, önce tüm bucket'ları kontrol edip sonra hepsini artırma, `{security-rate-v1}` hash-tag'i ile slot tutarlılığı, anahtarlarda yalnız private-file HMAC pseudonym'i, ve her Redis hatasında fail-closed `Unavailable`. İki replica + gerçek Redis ile paylaşılan cap'ler ve anahtarlarda ham IP/principal bulunmadığı test ediliyor.
- Secret hijyeni titiz: keyring yüklemesinde JSON payload'ından doğrudan base64 decode (ara `string` bırakmamak için), her hata yolunda `CryptographicOperations.ZeroMemory`, filtre ve endpoint'lerde çözülmüş secret ile aday hash'lerin uygulama kodu çalışmadan önce sıfırlanması; bunu gözlemleyen gerçek bir unit test var (InstallationAuthenticationFilterTests). Response DTO'larında `ToString()` override ile `[REDACTED]`.
- Sahiplik tek kaynaktan: `IDeviceContext` tamamen kaldırılmış, tüm iş servisleri yalnız `IInstallationPrincipalContext.PrincipalId` kullanıyor; `InstallationPrincipalContext` scoped kayıtlı ve interface aynı somut örneğe factory ile bağlanmış (Program.cs:244-246) — cihazlar arası sızıntı yaratacak singleton hatası yok. Senaryo listeleme/silme `GetByIdAndUserIdAsync(scenarioId, user.Id)` ile principal'a kırpılmış.
- Keyring dosyası `SecureSecretFile` üzerinden Linux openat2/statx sözleşmesiyle okunuyor: mutlak yol zorunlu, symlink/hardlink/world-readable/private olmayan üst dizin reddediliyor, hata mesajı yolu veya değeri sızdırmıyor — hepsi ayrı testlerle kapatılmış.

**Repo dışı bilgi gerektiren açık sorular.**

- Public port'un önündeki Caddy/ingress `/metrics*` ve `/health/*` yollarını uygulamadan bağımsız olarak blokluyor mu? Blokluyorsa lane-01'in gerçek maruziyeti azalır; blokla­mıyorsa bulgu doğrudan internete açıktır.
- Üretimdeki `SAYDIN_PROXY_NETWORK_CIDR` değeri gerçek reverse proxy ağıyla birebir eşleşiyor mu, ve gerçek kullanıcı popülasyonunun ne kadarı kendi `X-Forwarded-For` başlığını enjekte eden kurumsal/operatör proxy'leri arkasından geliyor?
- Üretimde gözlenen `POST /v1/installations` hacmi ve `users` tablosu büyüme eğrisi nedir? lane-02'nin fiilen istismar edilip edilmediği ancak canlı veriyle görülebilir.
- App Attest / Play Integrity gibi bir attestation kontrolü ürün yol haritasında var mı? Plan bunu açık ürün kararı olarak listeliyor; kararın yönü lane-02 için önerilen çözümü belirler.
- Installation keyring HMAC anahtarının üretimdeki rotasyon periyodu ve eski sürümü düşürmeden önceki drain prosedürü nedir? Repo'da bu prosedür yok; repo dışı bir operasyon runbook'u varsa lane-06'nın etkisi azalır.

---

## L02 — Scenario payload, pagination ve gövde sınırları

**Doğrulanmış bulgu:** 0 Critical · 0 High · 1 Medium · 8 Low

**Kapsam.** L02 dosya listesindeki 14 dosyanın tamamı (ScenariosEndpoints, ScenarioRequestBodyReader/ExtraDataValidator/Limits/CursorCodec, ScenarioCursor, ScenarioPageResponse, RequestBodyTooLarge exception+handler, ISavedScenarioService/SavedScenarioService, ISavedScenarioRepository/SavedScenarioRepository, SavedScenarioConfiguration) baştan sona okundu. Karşı taraf olarak `018_scenario_integrity.sql`, `019_privilege_separation.sql` ilgili grant/trigger blokları, `Program.cs` DI/pipeline kayıtları, `EndpointExtensions.RequireInstallationCredential`, `ActivityLogBuilder`, `ErrorMessages(.en).resx` anahtarları, ADR-008, `docs/analysis/06-remediation-progress.md` kabul iddiaları ve ilgili tüm unit/integration testleri (SavedScenarioRepositoryQueryTests, SavedScenarioServiceTests, ScenarioCursorCodecTests, ScenarioExtraDataValidatorTests, ScenarioRequestBodyReaderTests, SavedScenarioRepositoryIntegrationTests, OpenApiSemanticContractTests) incelendi. Testler çalıştırılmadı; yalnız izole bir System.Text.Json BOM davranışı SDK 10 konteynerinde doğrulandı. Rate limiter, installation credential doğrulama iç mantığı ve cache katmanı bu hattın kapsamı dışında bırakıldı.

**Güçlü kararlar.**

- Cursor tasarımı doğru sınırı çiziyor: `ScenarioCursorCodec` sabit uzunluk + version byte + epoch/Guid.Empty reddi + canonical yeniden-encode karşılaştırması yapıyor, ve `BuildPageQuery` tek noktada zorunlu `s.UserId == userId` predicate'ini uyguluyor. Tamper edilmiş bir cursor yalnız kendi verisinde başlangıç noktası seçebiliyor — testler (ScenarioCursorCodecTests + integration'daki cross-user sızıntı assert'i) bunu açıkça kilitliyor.
- Limit ve insert gerçekten atomik: `CreateWithinLimitAsync` transaction içinde `pg_advisory_xact_lock(hashtextextended('saydin.saved_scenarios:'||user_id::text,0))` alıyor ve migration 018 trigger'ı BİREBİR aynı anahtar formunu kullanıyor. Integration testleri hem 2 hem 20 eşzamanlı contender için tam olarak bir kazanan olduğunu, farklı kullanıcının bloklanmadığını ve harici bir session'ın aynı literal anahtarla API yolunu bloklayabildiğini gerçek PostgreSQL'de kanıtlıyor — bu, anahtar eşleşmesinin sağlam kanıtıdır.
- Gövde sınırı gerçekten uygulanabilir konumda: `Content-Length` varsa okumadan reddediliyor, chunked'da 32 KiB + 1 byte'ta akış kesiliyor, buffer ArrayPool'dan alınıp `clearArray: true` ile iade ediliyor. Pipeline'da başka hiçbir yerde `Request.Body` okunmuyor / `EnableBuffering` çağrılmıyor, yani limit bypass edilebilir bir çift-okuma yolu yok.
- ExtraData bütçesi çok eksenli ve fail-closed: derinlik, toplam property, toplam node (property adları dahil), toplam array item, tek string UTF-8 byte ve jsonb-canonical toplam boyut ayrı ayrı sınırlanıyor; tüm kontroller user upsert/last_seen/count/insert yazmalarından ÖNCE çalışıyor. `CountPostgresJsonbFormattingSpaces` PostgreSQL'in `": "`/`", "` biçimlendirmesini doğru modelliyor (elle doğruladım) ve tip allowlist'i legacy-v1 sözleşmesini kapatıyor.
- Duplicate property kontrolü doğru denklik bağıntısını kullanıyor: web serializer case-insensitive bağladığı için `EnsureNoDuplicateProperties` de `OrdinalIgnoreCase` kullanıyor — `extraData` + `ExtraData` gibi ambiguous last-wins payload'lar reddediliyor ve bu testlerle kilitlenmiş.
- Defense-in-depth doğru katmanlanmış: uygulama sınırları + 018'deki named CHECK'ler + hard-cap trigger birlikte çalışıyor, `TryMapExpectedConstraint` yalnız BEKLENEN constraint adlarını domain exception'a çeviriyor, beklenmeyen `DbUpdateException` maskelenmeden yükseliyor (integration testi bunu ayrıca doğruluyor). Migration 018 mevcut ihlalleri silmeden/normalize etmeden fail-closed duruyor ve SHA-256'sı migrator + DQA trust root'unda pinli (hash'i yeniden hesaplayıp doğruladım).
- Sayfalama sözleşmesi doğru: servis `limit+1` okuyup `nextCursor`'ı DÖNEN sayfanın son elemanından üretiyor (look-ahead satırından değil), böylece kayıt atlama/tekrar oluşmuyor; `(created_at, id) DESC` sıralaması ve keyset predicate'i ikisi de SQL'e çevrildiği için PostgreSQL uuid sıralamasıyla tutarlı, ve integration testi 37 satır / aynı timestamp üzerinde tam DB sırasıyla eşleşmeyi + tekrarsızlığı + cross-user sızıntısızlığı gerçek DB'de doğruluyor.

**Repo dışı bilgi gerektiren açık sorular.**

- Üretim PostgreSQL'inde temsili veri (kullanıcı başına 100'e yakın satır, tabloda milyonlarca satır) ile `EXPLAIN (ANALYZE, BUFFERS)` alındığında page sorgusu gerçekten `idx_saved_scenarios_user_created_id_desc` üzerinde sort'suz index scan mi yapıyor? Mevcut test planner knob'larını kapattığı için bu repo içinden yanıtlanamıyor (bkz. L02-02).
- Canlı `saved_scenarios` verisinde comparison/portfolio tipli satırların `asset_symbol` değerleri fiilen ne kadar serbest metin içeriyor (yani L02-01'in geçmişe dönük veri temizliği gerektirip gerektirmediği)? Bu ancak üretim veritabanı sorgusuyla ölçülebilir.
- Mevcut Flutter istemcisi `GET /v1/scenarios/page` çağrısında null cursor'ı query'den tamamen çıkarıyor mu, yoksa boş değer (`?cursor=`) olarak mı serialize ediyor? L02-06'nın ikinci yarısının gerçek etkisi buna bağlı ve istemci reposu bu repoda değil.
- Mobil istemci `assetDisplayName`'i kendi gönderdiği hâliyle mi bekliyor? Server bu alanı doğruluyor ama sonra atıyor (portfolio/comparison'da display name = uppercase sembol); bu davranış commit'ten önce de aynıydı, fakat ürün tarafında kabul edilmiş bir sözleşme mi olduğu repo dışı bilgi.

---

## L03 — Finansal hesaplama, cache, kota ve authority

**Doğrulanmış bulgu:** 0 Critical · 1 High · 1 Medium · 7 Low

**Kapsam.** L03 dosya listesinin tamamı (38 dosya) okundu: `WhatIfCalculator`, `DcaCalculator`, `AssetService`/`AssetSymbolIndex`, `AppConfigService`, `PlanLimitResolver`, `DailyLimitGuard`+`QuotaLease`+`QuotaUnavailableException`, `RedisCacheHelper`, `AuthorityCacheEntries`/`AuthorityCacheNamespace`/`CalculationCacheEntries`, `FinalObservationAuthority`, `PriceRepository`/`InflationRepository`, `CalculationTelemetry` ve ilgili endpoint/DTO'lar. Karşı taraflar da doğrulandı: `Saydin.Shared` entity + EF configuration, migration 020/021 (authority tuple, `source_raw` sözleşmesi, `get_asset_catalog_state`), `EvdsInflationWorker` (CPI ingestion penceresi), `docs/cache-strategy.md`, `docs/analysis/06-remediation-progress.md` ve API unit/integration testleri (`DcaCalculatorTests`, `WhatIfCalculatorTests`, `DailyLimitGuardTests`, `AuthorityCacheIntegrationTests`, `InflationRepositoryIntegrationTests`). Okunmayanlar: senaryo/installation/güvenlik hatları (L03 kapsamı dışı), PriceIngestion adapter iç detayları (yalnız EVDS pencere hesabı okundu).

**Güçlü kararlar.**

- Cache envelope tasarımı gerçekten sağlam: `PriceCacheEntry`/`PriceRangeCacheEntry` hit'i kabul etmeden önce PostgreSQL'den okunmuş trusted `AssetReadIdentity` (id/symbol/source) ile eşleştiriyor, exact tarih ve nearest ±maxDays sınırını, range'te monoton artan tarih sırasını ve her noktanın `IsCompleteFinal` olduğunu doğruluyor. Redis'e yazma yetkisi ele geçse bile yanlış provider/tarih taşıyan envelope hit sayılmıyor (AuthorityCacheEntries.cs:101-208).
- Hesaplama cache'i request'e exact bağlanmış: `WhatIfCacheEntry`/`DcaCacheEntry` sembol, tüm tarihler, tutar, tip, inflation bayrağı, dil ve catalog revision+SHA'yı hem envelope'ta hem response içeriğinde doğruluyor; `CalculationCacheContract.IsComplete` yalnız warning'siz, `complete`+`final` basis taşıyan sonucun cache'lenmesine izin veriyor (CalculationCacheEntries.cs:48-238).
- `FinalObservationAuthority` tek trust-boundary olarak hem IQueryable predicate'i hem in-memory kontrolü sunuyor ve provider↔price_kind matrisini (`tcmb/official_reference`, `coingecko/daily_utc_reference`, …) `provider_source = asset.source` eşitliğiyle birlikte uyguluyor; `PriceRepository.GetNearestPricesAsync` içindeki raw SQL bu predicate'i satır satır yansıtıyor, böylece bulk yol ile EF yolu ayrışmıyor.
- Kota kiralama fail-closed ve idempotent: gün kararı Redis `TIME`'dan alınıyor, Lua script `HEXISTS lease:<nonce>` ile aynı nonce'un replay'ini artırmadan başarı döndürüyor, ambiguous `RedisConnectionException`/`RedisTimeoutException` tek sefer aynı nonce ile tekrarlanıyor, `QuotaLease` release'i acquire anındaki exact key+nonce'a bağlıyor (gün dönümü güvenli) ve sonlu kotada her Redis arızası 503 üretiyor. Gerçek Redis'te atomiklik/idempotency/48h retention integration testleriyle kilitlenmiş (DailyLimitGuardIntegrationTests).
- `OptionalDataFailure` dar bir taksonomi ile yalnız `TimeoutException`, `HttpRequestException` ve `NpgsqlException { IsTransient: true }`'i 'beklenen degrade' sayıyor; cancellation, auth, EF state ve programlama hataları propagate ediyor. Bu, eski `catch (Exception ex) when (ex is not OperationCanceledException)` geniş yutmasına göre net bir iyileşme.
- `GetNearestPricesAsync` bulk yolu `unnest(...) WITH ORDINALITY` + `LATERAL` ile duplicate istek tarihlerinin mantıksal pozisyonunu koruyor, cardinality'yi hem repository hem service katmanında doğruluyor ve DCA'nın 601 noktalık N+1 fiyat sorgusunu tek komuta indiriyor; trusted asset identity per-scope semaphore ile coalesce edilip başarısız/cancelled loader memo'ya yazılmıyor (AssetService.cs:301-334).
- API-06 veri minimizasyonu tutarlı uygulanmış: hem `ILogger` mesajlarında hem activity log payload'larında ham tutar yerine `AmountBucket.Coarse` ve exact yüzde yerine `TelemetryOutcome` (`profit`/`loss`/`flat`/`unavailable`) kullanılıyor; kota loglarında key, nonce, subject veya exception metni yer almıyor.
- `docs/cache-strategy.md` yeni `authority-final-v1` namespace'i, `whatif:v3`/`dca:v2` sürümleri, kota key şeması ve fail-closed hata politikasıyla koda birebir uyumlu güncellenmiş — key formatları ve TTL'ler tek tek doğrulandı.

**Repo dışı bilgi gerektiren açık sorular.**

- Üretimde EVDS/TÜİK CPI verisi gerçekten yalnız `bugün - 1 ay`'a kadar mı mevcut, yoksa manuel/backfill bir yolla içinde bulunulan ayın satırı da yazılıyor mu? L03-01'in etkisi buna bağlı olarak 'her zaman' ya da 'ayın ilk günlerinde' olur.
- Üretimdeki Redis instance'ının `maxmemory`/eviction policy'si nedir? L03-03'ün (source_raw'lı range cache) bellek etkisi, `noeviction` ile `allkeys-lru` arasında çok farklı sonuç doğurur.
- BTC/ETH gibi yüksek birim fiyatlı varlıklarda tipik kullanıcı tutarı nedir (Flutter istemcisinin varsayılan/önerilen tutarları)? L03-02'nin kullanıcıya görünen hata büyüklüğü doğrudan bu dağılıma bağlı.
- Flutter istemcisi `InflationDataAsOf != null` durumunu bir uyarı rozetiyle mi gösteriyor? L03-04'ün kullanıcıya yansıyan etkisi bu istemci davranışına bağlı ve meta repo'da.
- Premium tier'da hem `DailyCalculationLimit = 0` (kota yok) hem `PriceHistoryMonths = 0` (sınırsız geçmiş) birlikte açık — üretimde premium hesap sayısı ve dağıtık IP limiter'ın gerçek eşikleri nedir? Bu, calculator içi sınırsız `GetPriceRangeAsync` aralığının pratik risk seviyesini belirler.

---

## L04 — API runtime, activity logging, exception zinciri

**Doğrulanmış bulgu:** 0 Critical · 1 High · 2 Medium · 9 Low

**Kapsam.** L04.files listesindeki 19 dosyanın tamamı tam metin olarak okundu (Program.cs, ActivityLogWriter/BatchStore, ActivityLogMiddleware/Builder, ChannelActivityLogger, ActivityLogChannelTelemetry, ApiRuntimeContract, ApiServiceVersionContract, ApiErrorCodes, ValidationExceptionHandler, appsettings.json, Dockerfile, csproj, SaydinMetrics, iki resx). Karşı taraf olarak ayrıca ApiPortBoundaryMiddleware, DistributedSecurityLimiterMiddleware/Limiter, kalan 10 exception handler, WhatIf/Dca/Assets/Scenarios/Installation endpoint'leri, AmountBucket/TelemetryOutcome/IpMasker/ActivityActions/ActivityLogLimits, ActivityLogConfiguration, migration 011 CHECK, ilgili unit testler, docker-compose.yml + infrastructure/deployment/compose.production.yml ve docs/architecture/activity-logging.md okundu. Çalıştırma/derleme yapılmadı (lokal SDK yok); bulgular statik okuma ve testlerin kilitlediği davranışa dayanıyor.

**Reddedilen iddialar.**

- *Rate-limit ile reddedilen istekler de activity_logs'a yazılıyor — shed edilen trafik DB yazma yükü üretiyor* — Mekanizma iddiası doğru: Program.cs:330-332'de ActivityLogMiddleware limiter branch'ini sarmalıyor ve routing otomatik olarak kullanıcı middleware'lerinden önce çalıştığı için ResolveAction endpoint adını çözebiliyor; 429 satırı finally'de kuyruğa giriyor. Fakat bu bir kusur değil, mühürlenmiş kasıtlı davranış: tests/Saydin.Api.Tests/Middleware/ActivityLogMiddlewareTests.cs:16-21 `ProductFailuresBeforeHandler_AreAuditedWithStableOutcome` testi 429 ve 503 dahil handler'a ulaşmadan reddedilen isteklerin denetlenmesini AÇIKÇA gerektiriyor — güvenlik açısından rate-limit reddedilmelerinin audit ta

**Güçlü kararlar.**

- API-06 iddiası kodda gerçekten karşılanmış: WhatIf/Reverse/DCA activity payload'ları exact tutarı `AmountBucket.Coarse` ile kabalaştırıyor, exact TL/yüzde sonuçları `TelemetryOutcome.From` ile `profit/loss/flat/unavailable`'a indiriyor, senaryo serbest metin label'ı yerine `hasLabel` boolean'ı tutuluyor (WhatIfEndpoints.cs:70-90, DcaEndpoints.cs:43-59, ScenariosEndpoints.cs:145-152) ve IP `IpMasker` ile maskeleniyor. Serilog Information logları da aynı bucket/outcome sözleşmesini kullanıyor — API tarafında ham finansal değer loglayan tek bir yer bulunamadı.
- Channel drop telemetrisi doğru modellenmiş: `BoundedChannelFullMode.DropWrite` semantiğinde `TryWrite=true` dönmesi nedeniyle gerçek kayıp `itemDropped` callback'ine bağlanmış (Program.cs:278-284), completed-writer reddi ayrı sayaçta (`reason=writer_completed`) tutulmuş, action tag'i `ActivityActions.Lookup` allowlist'iyle sınırlanıp kardinalite patlaması engellenmiş ve warning'ler `Interlocked.CompareExchange` ile lock'suz, dakikada bir'e rate-limit edilmiş.
- Middleware sırası (ActivityLog → ExceptionHandler) hem kodda gerekçelendirilmiş hem de regresyon testiyle kilitlenmiş; `finally` bloğu exception handler'ın çevirdiği NİHAİ status'ü okuyor ve log gönderim hatasını iç try/catch ile sarmalayıp orijinal request exception'ını yutmuyor (ActivityLogMiddleware.cs:38-61).
- Toxic-row bisection tasarımı sağlam: tek bozuk satır tüm 50'lik batch'i düşürmüyor, O(log N) bölme ile sağlam satırlar kurtarılıyor, izole edilen satır `outcome=toxic_row` metriğiyle görünür kılınıyor ve shutdown yolunda retry devre dışı bırakılarak drain timeout'una sığması sağlanıyor.
- Exception handler zincirinin 11 üyesi de RFC 7807 `application/problem+json` üretiyor, hepsi `traceId` (Activity.Current fallback `TraceIdentifier`) ve kararlı `code` taşıyor; `GlobalExceptionHandler` teknik mesaj/stack sızdırmıyor ve `ExceptionHandlerContractTests` bu değişmezleri altyapısız, deterministik biçimde mühürlüyor.
- `ApiRuntimeContract` fail-fast trust sözleşmesi titiz: framework loopback default'ları temizleniyor, `ForwardLimit=1` zorunlu, wildcard/şemalı/duplike AllowedHosts reddediliyor, IPv4 için /24 IPv6 için /64'ten geniş CIDR ve canonical olmayan network prefix'i reddediliyor, proxy'nin network içinde tekrarlanması yakalanıyor — üretimde AllowedHosts boşluğu da başlatmayı durduruyor.
- `ApiServiceVersionContract` production'da servis sürümünü zorunlu kılıyor ve `latest`/`dev`/`1.0.0` gibi placeholder'ları reddediyor; bu sayede OTLP resource attribute'ları ve `infrastructure/release` manifest doğrulaması gerçek bir release kimliğine bağlanıyor.
- Yerelleştirme kaynakları temiz: TR/EN resx dosyaları 80'er key ile birebir örtüşüyor, kodda `localizer["..."]` ile çağrılan hiçbir key eksik değil ve `ResourcesPath` tuzağı Program.cs:113-118'de yorum + regresyon testiyle kilitlenmiş.

**Repo dışı bilgi gerektiren açık sorular.**

- Üretimdeki PostgreSQL `max_connections` / connection-pool boyutlandırması nedir? L04-01'in gerçekleşme sıklığı (53300/57P01 alma olasılığı) buna ve PG failover/restart pratiğine bağlı; repo bu değerleri göstermiyor.
- Kullanılan `AspNetCore.HealthChecks.Redis` sürümü `AddRedis(string)` overload'unda gerçekten kendi ConnectionMultiplexer'ını mı kuruyor (L04-06)? Paket kaynağı bu ortamda doğrulanamadı; sürüm davranışının canlı olarak teyidi gerekiyor.
- Üretimde `activity_logs` retention/erişim politikasının sahibi kim ve `saydin.activity_log.queue.drops.total` / `rejected_writes.total` / `write.failures.total` sayaçları için alarm kurulmuş durumda mı? 06-remediation-progress bunu "repo dışı governance residual" olarak bırakıyor.
- MaxMind GeoLite2 veri setinde `City.Name` uzunluğunun 100 karakteri aşabildiği kayıt var mı? L04-01'in `22001` tetikleyicisinin gerçekçiliği bu veri setine bağlı.

---

## L05 — Shared entity/EF ↔ SQL şema paritesi

**Doğrulanmış bulgu:** 0 Critical · 0 High · 1 Medium · 7 Low

**Kapsam.** L05 dosya listesindeki 10 EF configuration, 10 entity, `SaydinDbContext` ve `SaydinMetrics` tam olarak okundu; karşı taraf olarak `infrastructure/postgres/migrations/001,004,006,007,011,012,015,016,017,018,019,020,021,022` DDL'leri kolon/tip/nullability/default/PK/FK/unique/index/CHECK düzeyinde satır satır karşılaştırıldı. Doğrulama için ayrıca çağıran taraf okundu: `Saydin.Api/Repositories/{PriceRepository,SavedScenarioRepository,FinalObservationAuthority}`, `Saydin.Api/Services/{AssetService,AuthorityCacheEntries}`, `Saydin.PriceIngestion/Repositories/IngestionWindowRepository`, `Saydin.PriceIngestion/Adapters/{ProviderAuthority,ObservationEvidence}`, `Saydin.DatabaseMigrator/MigrationRunner` readiness/fingerprint blokları, `tools/calendar-data` importer'ı ve `tests/Saydin.Api.Tests/Data/*`. 017'nin ~180 bin karakterlik seed payload satırları (mask/evidence jsonb literal'leri) içerik olarak doğrulanmadı — yalnızca DDL ve INSERT hedef kolonları okundu; ayrıca .NET SDK lokal olmadığı için EF model hiç materyalize edilmedi (konvansiyon çıkarımları statik okumaya dayanıyor).

**Güçlü kararlar.**

- Finansal kolonlarda EF↔SQL precision paritesi kusursuz: `price_points` OHLC `numeric(18,6)` ↔ `HasPrecision(18,6)`, `volume` `numeric(24,4)` ↔ `HasPrecision(24,4)` (PricePointConfiguration.cs:15-22, 001_initial.sql:55-59), `inflation_rates.index_value` `numeric(12,4)` (004:10) ve `saved_scenarios.quantity` `numeric(18,8)` (001:142) de birebir. Volume'un 18,6'dan 24,4'e hizalanması yorumda gerekçelendirilmiş — bu tam olarak aranan türden bir yuvarlama/taşma hatasının kapatılması.
- `ingestion_windows` paritesi satır satır tutuyor: 25 kolonun tamamı, 10 CHECK constraint'in tamamı, üç index ve `UNIQUE NULLS NOT DISTINCT` kısıtı EF tarafında `AreNullsDistinct(false)` ile doğru modellenmiş (IngestionWindowConfiguration.cs:53-59 ↔ 015:36-38). Bu, EF Core/Npgsql'de sık atlanan bir ayrıntı ve migrator `ingestion_window_nullsafe_unique_missing` gate'i (MigrationRunner.cs:1340-1346) `indnullsnotdistinct`'i DB tarafında da ayrıca doğruluyor.
- DB'de karşılığı olmayan taşıyıcı alanlar (`PayloadSha256`, `PayloadByteLength`, `IngestionWindowId`) `price_points` ve `inflation_rates` için açıkça `builder.Ignore(...)` edilmiş (PricePointConfiguration.cs:28-30, InflationRateConfiguration.cs:33-35). Bu üç alan gerçekten yalnız `provider_fetch_payloads` / `*_observation_attributions` tablolarına ham SQL ile yazılıyor (IngestionWindowRepository.cs:698-723, 812-837); yani "entity'de var ama kolonu yok" tuzağı bilinçli ve doğru kapatılmış.
- `market_calendar_days → market_calendar_release_sources` composite FK'si EF'te doğru kurulmuş: `HasAlternateKey(ReleaseId, RawSha256)` + `HasPrincipalKey` (MarketCalendarConfiguration.cs:56-58, 79-84), DB'deki `uq_market_calendar_release_sources_hash` ve `fk_market_calendar_days_evidence` (017:60, 76-77) ile eşleşiyor. Aynı şekilde `market_calendar_active_releases`'in (calendar_code, release_id) composite FK'si `HasPrincipalKey<MarketCalendarRelease>` ile modellenmiş.
- CHECK constraint metinleri tek kaynaktan üretiliyor ve gerçekten senkron: `ScenarioTypes.All` ↔ 011:128, `QuantityUnits.All` ↔ 011:119, `UserTiers.All` ↔ 011 users bloğu, `InflationSources` ↔ `chk_inflation_rates_source`, `ActivityActions.All` ↔ `chk_activity_action`. Dört listeyi de karşılaştırdım, sapma yok.
- `installation_credentials`'ın iki partial unique index'inden EF'in coalesce ettiği ikincisi sessizce yutulmamış; `Saydin:DatabaseIndex:uq_installation_credentials_pending_principal` annotation'ı ve yorumla EF'in bilinen sınırı açıkça işaretlenmiş (InstallationCredentialConfiguration.cs:48-53) ve test bunu mühürlüyor.
- `char(64)` hash kolonları (`normalized_sha256`, `source_bundle_sha256`, `raw_sha256`, `evidence_raw_sha256`) EF'te `HasMaxLength(64).IsFixedLength()` ile doğru eşlenmiş — `varchar(64)` ile karıştırılmamış.
- Migrator `--verify-only` DB tarafını gerçekten sert doğruluyor: `VerifyPriceAuthorityAsync` (MigrationRunner.cs:2086-2145) 020'nin 37 kolonunu `format_type` + `attnotnull` + default ifadesi düzeyinde ve constraint'leri `pg_get_constraintdef` SHA-256'sı ile exact karşılaştırıyor; timestamptz/varchar uzunluk drift'i buradan kaçamaz.

**Repo dışı bilgi gerektiren açık sorular.**

- Üretimde `price_points.source_raw` satır başına ortalama kaç bayt ve Redis'te `maxmemory`/eviction politikası nedir? L05-01'in gerçek bellek etkisi bu iki değere bağlı; repo'da Redis için bellek sınırı tanımlı değil (docker-compose.yml:282-300).
- KVKK/erasure kapsamında `users` satırları üretimde hiç silinecek mi ve silme hangi rol/yol üzerinden yapılacak? EF üzerinden silinecekse L05-03'teki `DeleteBehavior.SetNull` drift'i, api_cap'in `activity_logs` UPDATE yetkisi olmadığı için silme akışını 42501 ile kıracaktır.
- Calendar release importer üretimde hangi rolle ve hangi süreçte (offline tool mu, deploy adımı mı) çalışıyor? `market_calendar_*` EF konfigürasyonlarının yazma yolu hiç kullanılmayacaksa L05-06 salt dokümantasyon meselesine iner; kullanılacaksa `DEFAULT NOW()` eksikliği gerçek veri hatası üretir.
- Migration 019 `asset_category` enum tipinde tüm managed rollerden `REVOKE ALL ON TYPE` yapıyor (019:777-778) ve sonrasında USAGE geri verilmiyor. Üretim api_cap login'i ile Npgsql `MapEnum<AssetCategory>` ve enum parametreli sorgular sorunsuz çalışıyor mu? Repo içinde enum'u parametre olarak gönderen bir sorgu göremedim (yalnız `OrderBy(a => a.Category)`), ama bu ileride eklenecek bir filtre için canlı ortam doğrulaması gerektiriyor.

---

## L06 — SQL migration 015–022 ve online protokol

**Doğrulanmış bulgu:** 0 Critical · 0 High · 1 Medium · 3 Low

**Kapsam.** `infrastructure/postgres/migrations/015`–`022` SQL dosyalarının tamamı (017/019/020/021'in uzun payload satırları kısaltılarak), `apply-migrations.sh`, `Dockerfile.migrator` ve `migration-impact/` (README + JSON Schema) baştan sona okundu; iddiaları doğrulamak için karşı taraf olarak `SqlScriptNormalizer.cs`, `MigrationRunner.cs` (apply/impact/timeout/SET ROLE yolları), `OnlineMigrationExecutor.cs`, `MigrationTrustRoot.cs`, `MigratorOptions.cs`, `RoleBootstrapDatabaseOperations.cs`, `PrincipalRetentionTransitionControlPlane.cs`, `SavedScenarioRepository.cs`, `PriceIngestionRepository`/`IngestionWindowRepository`/`BaseAssetWorker`, `DataQualityAudit/AuditSql*.cs`, `docker-compose.yml` ve `docs/analysis/06-remediation-progress.md` ilgili bölümleri okundu. 001–014 dosyalarının değişmediği `git diff --name-status` ile, trust-root SHA-256 pinlerinin gerçek dosya hash'leriyle birebir tuttuğu `shasum -a 256` ile doğrulandı. Okunmayanlar: 017'nin ~180 KB'lik gömülü JSON takvim payload'unun içeriği (yalnız yapısı ve doğrulama fonksiyonu incelendi), ingestion/API iş mantığının derin davranışı (diğer hatların kapsamı) ve gerçek bir PostgreSQL/TimescaleDB üzerinde çalıştırma.

**Reddedilen iddialar.**

- *SqlScriptNormalizer'ın non-transactional statement kapısı CREATE UNIQUE INDEX CONCURRENTLY varyantını kaçırıyor* — Kod iddiası doğru ama etki iddiası çürük. SqlScriptNormalizer.cs:251-260 `IsNonTransactional` gerçekten yalnız `createindexconcurrently`/`dropindexconcurrently` prefix'lerini ve `reindex...concurrently`'yi yakalıyor; Scan (satır 163-165, 213) boşlukları atıp küçük harfe çevirdiği için `CREATE UNIQUE INDEX CONCURRENTLY` → `createuniqueindexconcurrently...` bu prefix'e uymuyor. ANCAK bir üst katman bunu zaten kapatıyor: MigrationImpactManifest.LoadAndVerify (satır 153-156) trust-root sonrası HER migration için imzalı impact manifesti zorunlu kılar ('migration_impact_configuration_required'), Sql
- *OCI runbook'u şema değişikliği prosedürü olarak emekliye ayrılmış apply-migrations.sh'ı ve 001→014 initdb zincirini gösteriyor* — Alıntılanan satırların hepsi gerçekten var: apply-migrations.sh:11-13 tombstone (`exit 64`); oci-migration-plan.md:176 'initdb.d 001→014 migration zincirini otomatik çalıştırır', satır 190-191 kabul kriteri 'schema_migrations 001–014 dolu', satır 311 bakım tablosunda 'Yeni `.sql` ekle → infrastructure/postgres/apply-migrations.sh'. ANCAK risk iki bağımsız katmanda açıkça kapatılmış: (1) dosyanın en başında (satır 3-10) büyük harfle 'ARŞİVLENMİŞ TASARIM — KOPYALA/ÇALIŞTIR DEĞİLDİR' bandı var ve üretim için yalnız compose.production.yml, validate-production-assets.sh, imzalı infrastructure/relea

**Güçlü kararlar.**

- 001–014 dosyaları bit düzeyinde değişmemiş (`git diff --name-status` yalnız A kaydı gösteriyor) ve 015–022'nin `MigrationTrustRoot.Checksums` içindeki SHA-256 pinleri gerçek dosya hash'leriyle sekizde sekiz birebir tutuyor — immutable migration disiplini gerçekten korunmuş.
- 008b/013 compression sarmalama deseni 022'de doğru şekilde yeniden üretilmiş: 022 compression'ı kapatıp FK'yi değiştiriyor, ancak yeniden açma işi bootstrap'in sahip olduğu `saydin_principal_retention_control.consume_principal_retention_transition()` helper'ına devredilmiş ve orada `compress_segmentby='action'`, `compress_orderby='created_at DESC'` 008/013 ile birebir geri kuruluyor (PrincipalRetentionTransitionControlPlane.cs:209-212) — yani storage layout drift'i önlenmiş. 022'nin terminal sözleşmesi ayrıca compression_enabled ve 7 günlük policy'yi tekrar doğruluyor.
- Hiçbir 015–022 dosyası TimescaleDB hypertable'ında `ALTER COLUMN ... TYPE` kullanmıyor; `price_points` üzerindeki tüm genişletmeler nullable `ADD COLUMN` (020:29-46), `users` üzerindekiler ise PG11+ hızlı-yol `ADD COLUMN ... NOT NULL DEFAULT` (021:35-40) — tablo rewrite'ı yok.
- `CREATE INDEX CONCURRENTLY` transaction içine kaçamıyor: SqlScriptNormalizer non-transactional ifadeleri bağlantı öncesi reddediyor ve 015–022'nin hiçbirinde CONCURRENTLY kullanılmıyor; runner her migration gövdesini kendi transaction'ında `SET LOCAL lock_timeout` (120 s) + `statement_timeout` + `search_path` ile çalıştırıyor (MigrationRunner.cs:2839-2853), yani uzun kilit kuyruğu yerine fail-fast davranış var.
- 019'un least-privilege grant'ları gerçek kod kullanımıyla birebir örtüşüyor — doğrulandı: API yalnız assets/price_points/inflation_rates/users/saved_scenarios SELECT + users kolon-kısıtlı INSERT/UPDATE + saved_scenarios INSERT/DELETE kullanıyor (SavedScenarioRepository'de UPDATE yolu yok, DELETE var); ingestion `market_holidays` okuyor ve grant'ı var, hiç DELETE atmıyor ve grant'ı da yok; DataQualityAudit'in `AuditSql`/`ApiTrustAuditSql` sorgularının tamamı audit_cap'e verilen SELECT listesinin içinde kalıyor, users/saved_scenarios/activity_logs/installation_credentials'a hiç dokunmuyor.
- 019'un `ALTER DEFAULT PRIVILEGES FOR ROLE owner REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC` / `REVOKE USAGE ON TYPES FROM PUBLIC` satırları (819-835), PostgreSQL'in varsayılan "her fonksiyona PUBLIC EXECUTE" davranışını gelecek nesneler için de kapatıyor — 020/021/022'de owner tarafından yaratılan tüm fonksiyonlar bundan yararlanıyor, ayrıca her migration ek olarak açık `REVOKE ALL ... FROM PUBLIC` de yazıyor (savunma derinliği).
- Calendar importer için gerçek capability tasarımı: importer_cap `market_calendar_days` üzerinde yalnız INSERT, `market_calendar_active_releases` üzerinde hiçbir hak alıyor; seal ve activate işlemleri dar ACL'li SECURITY DEFINER fonksiyonlar üzerinden yapılıyor (017:256-271, 390-436 + 019:701-706, 816-817) — importer'ın doğrudan UPDATE/CAS yetkisi hiç doğmuyor.
- 018'in per-user advisory lock namespace'i iddia edildiği gibi API ile birebir uyuşuyor: trigger `hashtextextended('saydin.saved_scenarios:' || NEW.user_id::text, 0)` (018:94-95), repository ise aynı literal + `uuid::text` cast'iyle aynı anahtarı üretiyor (SavedScenarioRepository.cs:18,100); DB hard cap'i 100 ile `ScenarioLimits.SystemSaveHardLimit` ve premium tier'ın 0→100 çözümlemesi tutarlı, yani API limiti ile DB kapısı arasında sessiz uyumsuzluk yok.

**Repo dışı bilgi gerektiren açık sorular.**

- Üretimdeki `activity_logs` hypertable'ının sıkıştırılmış chunk sayısı ve toplam boyutu nedir? 022'nin tek DO bloğu içindeki tam decompress+recompress döngüsünün varsayılan 1800 s statement_timeout / 2100 s toplam bütçeye sığıp sığmadığı ve gereken geçici disk headroom'u yalnız canlı hacim bilgisiyle yanıtlanabilir.
- Üretim deploy akışı (infrastructure/deployment/compose.production.yml + release flow) migrator koşarken API ve PriceIngestion'ı gerçekten durduruyor mu? 019 on yedi tabloyu, 022 ise `users`'ı ACCESS EXCLUSIVE modda kilitliyor; rolling deploy senaryosunda bu kilitler kullanıcıya görünür kesinti anlamına gelir.
- 016 ve 020'nin başlıklarında zorunlu tutulan "eski ingestion replica'larını stop/drain et → migrate/verify → yeni binary başlat" sırası deploy otomasyonunda mekanik olarak zorlanıyor mu, yoksa yalnız migration yorumunda mı yazılı? Yanlış sırada bir deploy, fence trigger'ları nedeniyle ingestion'ın tamamen yazamaz hale gelmesine yol açar.
- P-256 offline imzalama otoritesi (migration-impact signing key) operasyonel olarak mevcut mu? Repoda hiç imzalı `.impact.json`/`.impact.sig` yok — bu doğru, çünkü 001–022 compiled trust-root'ta; ancak 023 eklenmeden önce imzalama süreci hazır değilse migrator o migration'ı hiç uygulayamaz.
- TimescaleDB 2.16.1'de tek transaction içinde çok sayıda `decompress_chunk`/`compress_chunk` çağrısının WAL üretimi ve geçici alan tüketimi üretim disk profiliyle uyumlu mu? Bu, ancak gerçek chunk sayısı/boyutu üzerinde bir prova ile kapatılabilir.

---

## L07 — Saydin.DatabaseMigrator

**Doğrulanmış bulgu:** 0 Critical · 0 High · 1 Medium · 11 Low

**Kapsam.** L07 dosya listesindeki 11 kaynak dosyanın tamamı (MigrationRunner.cs 3295 satır dahil) baştan sona okundu; ayrıca doğrulama için karşı taraflar da okundu: `tests/Saydin.DatabaseMigrator.Tests/*` (5 test dosyası + fixture'lar), `infrastructure/postgres/Dockerfile.migrator`, `docker-compose.yml` (database-identity / role-bootstrap / migrator servisleri), `infrastructure/deployment/compose.production.yml`, `infrastructure/secrets/bootstrap-dev-database.sh`, `infrastructure/postgres/migration-impact/README.md`, `infrastructure/postgres/migrations/` dizin envanteri, `src/Saydin.DatabaseRoleBootstrap/BootstrapOptions.cs` ve `docs/analysis/README.md` + `06-remediation-progress.md` kabul kanıtı iddiaları. Testler ÇALIŞTIRILMADI (lokalde .NET SDK yok, gerçek PostgreSQL/Timescale yok); dinamik davranışa dair iddialar salt kod okumasına dayanır. Migration `.sql` gövdelerinin semantik içeriği (019/020/021/022 SQL'i) bu hattın kapsamı dışında bırakıldı, yalnız runner'ın onları nasıl çalıştırdığı incelendi.

**Güçlü kararlar.**

- Trust-root modeli doğru kurgulanmış: checksum'lar NORMALIZE EDİLMİŞ değil HAM byte üzerinden (`SHA256.HashData(rawBytes)`) hesaplanıyor, 001–022 pinleri tek bir derlenmiş `Saydin.Migrations.MigrationTrustRoot` sınıfında yaşıyor ve hem migrator hem DQA aynı kaynağı tüketiyor; kuyruk (tail) migration'ları ise offline P-256 imzalı, kanonik JSON'lu impact manifesti olmadan kesinlikle çalışamıyor (`migration_impact_configuration_required`).
- Crash-safety tasarımı sağlam: `MarkRunningAsync` autocommit ile kalıcı 'running' işareti bırakıyor, gövde + `MarkTerminalAsync` tek runner-owned transaction'da atomik commit ediliyor, `commitAttempted` bayrağı + `TryReconcileCommittedAsync` commit-ACK kaybını idempotent şekilde uzlaştırıyor, `TryMarkFailedAsync` ise `WHERE version=$ AND checksum=$` ile başka bir runner'ın satırını ezmiyor. `ValidateManagedStateAsync`'in prefix kontrolü (`migration_history_not_prefix`) delikli geçmişi reddediyor.
- `--verify-only` gerçekten salt-okunur: Blank ve LegacyComplete014 durumları kontrol düzlemi yazılmadan ÖNCE reddediliyor (`blank_database_not_ready` / `legacy_baseline_required`), `VerifyImpactPostconditionsAsync` `SET TRANSACTION READ ONLY` ile RepeatableRead içinde koşuyor ve her iki catch bloğunda `if (!options.VerifyOnly)` koşulu `TrySetControlFailedAsync`'i engelliyor.
- Sır sızıntısına karşı disiplinli: `MigratorOptions` `DATABASE_URL`/`PGPASSWORD`/`POSTGRES_EXPORTER_PASSWORD` env'lerini fail-fast reddediyor, `Harden()` `IncludeErrorDetail=false`/`LogParameters=false`/`Passfile=null`/`Options=null` set ediyor, `ToString()` ve `SafeTarget` parolasız, `Program`'ın catch'leri yalnız `ex.Code` veya `ex.GetType().Name` yazdırıyor — exception mesajı (dolayısıyla Detail/inner mesaj) hiçbir çıktı akışına düşmüyor.
- SQL enjeksiyon yüzeyi kapatılmış: tüm parametreler `$n` ile geçiriliyor, dinamik rol/identifier gereken yerlerde `SELECT pg_catalog.format('SET ROLE %I',$1)` / `format('SET LOCAL ROLE %I',$1)` sunucu tarafı quoting'i kullanılıyor, `MigrationImpactPreflight.Quote` yalnız regex ile doğrulanmış (`^[a-z][a-z0-9_]{0,62}$`) ve imzalı manifestten gelen identifier'lara uygulanıyor.
- Schema fingerprint doğrulaması olağanüstü ayrıntılı ve fail-closed: tablo/kolon/constraint/index'in yanı sıra trigger `tgtype`/`tgenabled`, fonksiyon `prosrc` SHA-256'sı, `proconfig` search_path'i, `prosecdef`, ACL'lerin `aclexplode` ile EXCEPT ALL karşılaştırması, Timescale chunk ve compressed-chunk sahipliği ve compression job sahibi/aralığı dahil ediliyor; `database_scalar_missing` bile `schema_fingerprint_mismatch`'e dönüştürülüyor.
- search_path determinizmi hem beyan hem etki düzeyinde assert ediliyor: `AssertHistoricalTransactionSearchPathAsync` `current_setting`, `current_schemas(true)` ve `current_schemas(false)` üçlüsünü birlikte kontrol ederek 'pg_catalog implicit ilk, public explicit ilk' invariant'ını mühürlüyor; 019 gövdesi için ayrıca `pg_catalog,pg_temp`'e daraltılıp doğrulanıyor.
- Online (resumable) yürütücü çift-sayım ve atlama riskini kapatıyor: batch CAS'ı `last_key IS NOT DISTINCT FROM $ AND processed_rows=$` üzerine kurulu, `batch.Selected != batch.Updated` durumu reddediliyor, checkpoint tablosunun kolon/constraint/ACL şeması tam metin karşılaştırmasıyla doğrulanıyor ve compression policy hem transaction içinde hem crash yolunda geri yükleniyor.

**Repo dışı bilgi gerektiren açık sorular.**

- Required CI'da `Saydin.DatabaseMigrator.Tests` gerçekten 124 test executed / 0 skipped ile mi koşuyor? L07-02'deki `UnknownTailMigration_IsRejectedBeforeConnectionOrDdl` beklentisi üretim yolunda üretilemeyecek bir hata kodunu iddia ediyor; en son yeşil TRX artifact'ı incelenmeden bu testin kırmızı mı yoksa hiç mi koşmadığı repo içinden ayırt edilemiyor.
- Kuyruk (tail) migration imzasını üreten offline P-256 release authority'sinin anahtarı nerede tutuluyor, rotasyon/iptal prosedürü nedir ve `--migration-impact-public-key-sha256` değeri hangi bağımsız kanaldan promote ediliyor? Repoda ne anahtar ne de bir wiring örneği (`SAYDIN_MIGRATION_IMPACT_*` hiçbir compose/CI dosyasında set edilmiyor) var.
- Üretimde bir migration 'failed' durumuna düştükten sonra ileri-düzeltme (forward-fix) prosedürü nedir? Kod, satır checksum'ı değişmiş bir migration'ı `migration_checksum_mismatch` ile kalıcı olarak reddediyor; kurtarma yalnız `schema_migrations` satırının elle silinmesiyle mümkün görünüyor ve bu runbook repo içinde belgelenmemiş (06'da 'clone audit/forward-fix runbook' residual olarak açık bırakılmış).
- Üretim PostgreSQL'inde `pg_stat_replication` / `pg_replication_slots` görünürlüğü migrator login'i için gerçekten var mı? `ReadReplicationMetricsAsync` ve `ReadSlotMetricsAsync` boş sonuç kümesini `Visible=true` sayıyor (`reader.IsDBNull(2) || reader.GetBoolean(2)`); yetkisi olmayan bir rol sıfır satır görürse replica/slot bütçeleri sessizce geçer — bunun canlı ortamda test edilmesi gerekiyor.
- Kök `docker-compose.yml`'in bu commit'te hiç ayağa kaldırılıp kaldırılmadığı (L07-01) belirsiz; eğer geliştirme fiilen `.github/compose.integration.yml` ile yapılıyorsa CLAUDE.md'nin lokal iş akışı bölümünün kanonik olup olmadığı ürün ekibince netleştirilmeli.

---

## L08 — DatabaseRoleBootstrap + DatabaseSecurity

**Doğrulanmış bulgu:** 1 Critical · 1 High · 2 Medium · 5 Low

**Kapsam.** L08.files listesindeki 17 dosyanın tamamı satır satır okundu (RoleBootstrapRunner, RoleBootstrapDatabaseOperations 1139 satır, iki transition control-plane, BootstrapOptions/Program/BootstrapFailure, RoleContract/RuntimeDatabase/SecureSecretFile/LinuxSecretFile/DatabaseSecurityRejectedException, iki csproj, bootstrap-dev-database.sh). Karşı taraf olarak docker-compose.yml (secret generator/materializer, database-identity, database-role-bootstrap, migrator, tests profili), infrastructure/deployment/compose.production.yml, infrastructure/release/deploy-release.sh, infrastructure/postgres/Dockerfile.migrator, .github/workflows/ci.yml, .github/compose.integration.yml, .github/scripts/run-role-bootstrap-tests.sh + run-unit-coverage.sh, Saydin.Services.sln, tests/Saydin.DatabaseRoleBootstrap.{Tests,IntegrationTests} ve docs/analysis/06-remediation-progress.md'nin RP-07/SEC-001 kabul iddiaları okundu. Okunmayan: 019/022 migration SQL'lerinin gövdesi ve diğer servislerin runtime kodu (başka hatların kapsamı); bu nedenle ACL sözleşmesinin migration tarafı yalnız bootstrap'ın doğrulama sorguları üzerinden değerlendirildi.

**Güçlü kararlar.**

- SQL enjeksiyonu yüzeyi gerçekten kapatılmış: rol adı ve parola içeren tüm DDL sunucu tarafında `pg_catalog.format('%I','%L', $1, $2)` ile bind parametrelerinden üretiliyor (RoleBootstrapDatabaseOperations.cs:129-137, 158-161); istemci tarafı `QuoteIdentifier`/`QuoteLiteral` yalnız regex ile doğrulanmış extension sürümü ve contract'tan türetilen (regex'e uyan) rol adları için kullanılıyor. Tüm inceleme sorguları parametreli.
- Secret dosya okuyucusu (LinuxSecretFile) fail-closed ve derinlemesine: openat2 + RESOLVE_NO_SYMLINKS, O_NOFOLLOW/O_CLOEXEC, parent dizin 0700 + euid sahipliği, dosya 0400/0600 exact mod, link-count=1, boyut sınırı, ve okuma öncesi/sonrası dev/mount-id/inode/mode/uid/ctime/mtime kimliğinin karşılaştırılması ile TOCTOU/rewrite/mount-swap senaryolarını reddediyor; statx maskesi eksik alan bildirirse de reddediyor.
- Hedef doğrulaması gerçek fiziksel kimliğe bağlı: `pg_control_system().system_identifier`'ın SHA-256'sı contract ile sabit-zamanlı karşılaştırılıyor, admin'in OID 10 bootstrap superuser olması zorunlu, rol prefix'i deployment+database+system hash'inden yeniden türetilip verilenle karşılaştırılıyor (RoleContract.Create), ve tüm claim'ler `TargetLockSha256`'dan türetilen advisory lock ile serileştiriliyor. Bu, yanlış cluster'a bootstrap atmayı yapısal olarak engelliyor.
- Yabancı/işaretsiz roller asla adopt/alter/drop edilmiyor: prefix altındaki her rolün `shobj_description` marker'ı exact eşleşmek zorunda, aksi halde `managed_role_name_collision` ile mutasyondan önce duruluyor; `ensure` mevcut parolayı değiştirmiyor ve tüm işlem tek transaction içinde son bir `VerifyContractAsync` ile mühürleniyor.
- Least-privilege doğrulaması sadece 'grant edildi mi' değil, exact ACL multiset'i olarak yapılıyor: database/schema/pg_control_system ACL kümeleri `aclexplode` ile grantee+grantor+privilege+grantable dörtlüsü bazında karşılaştırılıyor; capability'lerin CREATE/TEMP hakkı, scheduler'ın normal login yeteneği ve backup rolünün herhangi bir doğrudan ACL/ownership'i negatif olarak test ediliyor.
- Bootstrap sonrası pozitif ve negatif kimlik probe'ları var: her login için gerçek SCRAM bağlantısı + aynı sunucu adresi/portu/system identifier doğrulaması, backup için fiziksel replikasyon `IDENTIFY_SYSTEM`, ve `timescale_scheduler` için 'giriş yapabiliyorsa hata' negatif probe'u (RejectSchedulerAuthenticationAsync).
- Hata çıktısı sızıntıya karşı disiplinli: Program.cs Npgsql/IO exception metinlerini bilinçli olarak bastırıp yalnız stabil kod yazıyor, `IncludeErrorDetail=false`/`LogParameters=false`/`PersistSecurityInfo=false` her bağlantıda set ediliyor ve testler (`Stable_error_output_never_contains_secret_or_path`) bunu mühürlüyor.
- Dev bootstrap script'i sırları host'a hiç indirmiyor: yalnız nonsecret kimlik metadata'sını üretiyor, çıktıyı satır sayısı/anahtar tekilliği/secret-shape/regex filtrelerinden geçiriyor, symlink hedefini reddediyor ve 0600 temp dosya + atomik `mv` ile yazıyor; `.env.database-runtime` gitignore kapsamında. Compose tarafında prefix türetimi C# `RoleContract.DerivePrefix` ile birebir aynı algoritmayı uyguluyor.

**Repo dışı bilgi gerektiren açık sorular.**

- Production PostgreSQL örneğinde `log_statement`, `log_min_error_statement` ve `log_min_duration_statement` hangi değerlerde? L08-02'nin log tarafındaki etkisi tamamen buna bağlı (repo'da bu ayarlar hiç tanımlı değil).
- postgres_exporter'ın gerçek scrape sorgu kümesi `pg_stat_activity.query` alanını okuyor mu ve exporter kimlik bilgisi hangi ekiplerle paylaşılıyor? L08-02'nin istismar edilebilirliği bu iki bilgiye bağlı.
- Production'da secret backend (ör. OCI Vault/KMS) parola dosyalarını hangi UID/mode/mount ile materialize ediyor? LinuxSecretFile parent dizin için 0700 + euid sahipliği, dosya için 0400/0600 ve link-count=1 şartı koyuyor; bu şartların gerçek backend tarafından karşılandığı repo içinden doğrulanamıyor.
- Çalışan production kernel'ı STATX_MNT_ID'yi (Linux 5.8+) destekliyor mu? Desteklemezse `HasRequiredFields` her secret okumasını reddeder ve tüm servisler fail-closed başlamaz.
- v1→v2 rotasyonu bugüne kadar herhangi bir ortamda uygulandı mı; uygulandıysa v1 login'i canlı sistemde nasıl emekliye ayrıldı? L08-04'ün gerçek operasyonel etkisi buna bağlı.

---

## L09 — Ingestion window ledger, write fence, supervision

**Doğrulanmış bulgu:** 0 Critical · 2 High · 4 Medium · 3 Low

**Kapsam.** L09.files listesindeki 20 dosyanın tamamı okundu (IngestionWindowRepository 1206 satır dahil), ayrıca karşı taraflar: migration 015/016/020, IngestionWindow entity + EF configuration, AdapterOutcome/AdapterCompleteness/ProviderFailureClassifier ve dört price adapter + EVDS adapter'ın outcome sınıflandırması, docker-compose ingestion servisi, LivenessHeartbeatService, PriceIngestion unit ve gerçek-DB integration test setleri (Workers/*, IngestionWindowRepositoryIntegrationTests, IngestionWriteFenceIntegrationTests, WorkerLedgerIntegrationTests) ve docs/analysis 06 içindeki ING-001/SUP-001 kabul kanıtı iddiaları. Okunmayan: DataRepair/DataQualityAudit iç detayları (yalnız `requeue_permanent_window` operasyonunun varlığı doğrulandı), calendar importer iç akışı ve mapper'ların alan-alan normalizasyonu.

**Güçlü kararlar.**

- DB sınırındaki yazma çiti gerçekten kapalı: 016'daki BEFORE INSERT/UPDATE trigger'ları `SET LOCAL` ile sunulan window/lease token'ını canlı `ingestion_windows` satırına karşı yeniden doğruluyor (state='running', lease_until > clock_timestamp, asset/source/job_type/tarih aralığı eşleşmesi) — yani fence yalnız uygulama seviyesinde değil; gerçek TimescaleDB üzerinde forged token, yanlış asset/tarih/source/job, expired ve reclaim edilmiş token negatifleriyle test edilmiş.
- Veri UPSERT'i, window terminal state'i ve ingestion_jobs terminal state'i tek DbContext/connection/transaction'da commit ediliyor; `IIngestionPersistenceFaultInjector` ile before/after-commit fault enjeksiyonu ve commit-ACK kaybı sonrası `GetTerminalStateAsync` üzerinden idempotent yakınsama gerçek DB testleriyle kanıtlanmış.
- Lease/fencing tasarımı sağlam: UUID lease token + attempt_count + `FOR UPDATE` + scope advisory lock; tüm zaman kararları uygulama saatinden değil `clock_timestamp()`'ten alınıyor (uygulama saat kayması testi mevcut), expired lease reclaim edilirken yetim `running` job'lar `lease_expired` ile terminalize ediliyor.
- Checkpoint artık `MAX(date)` değil: PlanWindowsAsync mevcut window aralıklarını okuyup interior/başlangıç boşluklarını yeniden planlıyor ve claim `ORDER BY range_start LIMIT 1` ile en eski çözülmemiş window'u seçtiği için başarısız chunk'ın üzerinden atlanamıyor.
- Completeness kararı worker'a bırakılmamış: repository, tamamlama transaction'ı içinde authoritative calendar günlerini ve depolanan asset/source/tarih key-set'ini yeniden okuyup adapter'ın verdiği sayaçlarla birebir karşılaştırıyor; affected-row sayısı beklenen accepted sayısına eşit değilse transaction rollback oluyor.
- Freshness telemetrisi takvim tatilini gerçek boşluktan ayırıyor: `expected_no_data` window'ları da `range_end`'i ilerlettiği için hafta sonu/tatil yapay lag üretmiyor; expected scope aktif asset'lerden türetildiği için yeni bir asset sağlıklı kardeşinin arkasına saklanamıyor ve tek bir eksik asset grubun `data_through`'unu NULL'a çekiyor.
- Migration 015'teki CHECK constraint seti (lease tutarlılığı, terminal completeness, outcome/error kod zorunlulukları, NULLS NOT DISTINCT logical uniqueness) EF configuration'da birebir aynalanmış; şema seviyesinde yanlış terminal state yazmak mümkün değil.
- Typed `AdapterOutcome` 'boş liste = başarı' belirsizliğini kaldırıyor; CoinGecko adaptörünün kısa aralıkları 90 güne genişletip provider'ı günlük granülariteye zorlaması ve saat-bazlı noktaları reddetmesi gibi provider semantiği detayları kod içinde gerekçelendirilmiş.

**Repo dışı bilgi gerektiren açık sorular.**

- TwelveData/CoinGecko/TCMB geçmiş bir günün değerini sonradan revize ediyor mu (özellikle BIST'te bedelsiz/split sonrası düzeltilmiş kapanış)? lane-03'teki immutability-trigger crash-loop'unun gerçekleşme olasılığı tamamen buna bağlı ve repo dışı provider davranışı gerektiriyor.
- Üretimde supervisor compose `restart: unless-stopped` mi, yoksa systemd/k8s `Restart=on-failure` mı? lane-04'teki exit-0 kapanışının etkisi buna göre 'gürültü' ile 'süreç hiç geri gelmiyor' arasında değişiyor.
- Süreç öldüğünde OTLP metrik akışı da durduğu için crash-loop'u yakalayan bir alarm var mı (container restart count, log-based Critical alarmı)? Yoksa lane-01'deki toplam ingestion outage'ı sessiz kalabilir.
- TCMB authoritative calendar'ın aktif horizon'u 2026-08-17'de bitiyor (docs/analysis/06 residual). Üretimde yeni resmî calendar release'lerini üretip promote eden operasyonel süreç kuruldu mu — kurulmadıysa TCMB window'ları kalıcı olarak `calendar_not_ready` kalır.
- Mevcut CoinGecko/TwelveData asset'lerinin tamamı ilgili worker'ın BackfillStartDate'i (2024-01-01) itibarıyla tam geçmişe sahip mi? Değilse lane-01 daha ilk backfill turunda tetiklenir.

---

## L10 — Provider adapter/mapper ve observation authority

**Doğrulanmış bulgu:** 0 Critical · 1 High · 4 Medium · 6 Low

**Kapsam.** L10 dosya listesindeki 21 dosyanın tamamı (5 adapter, 5 mapper, ProviderAuthority/ProviderPayload/ObservationEvidence/AdapterCompleteness/AdapterOutcome/ProviderFailureClassifier/ProviderStartupValidator, HttpResilienceExtensions, appsettings.json) satır satır okundu. Karşı taraf olarak `Program.cs` HTTP client kayıtları, `BaseAssetWorker`/`TcmbWorker`/`CoinGeckoWorker`/`TwelveDataWorker`/`OpenExchangeRatesWorker`/`IngestionOrchestrator`, migration `020_price_authority_expand.sql` (authority CHECK + trigger evidence sözleşmesi), `ObservationAuthority.cs` sabitleri ve `tests/Saydin.PriceIngestion.Tests/Adapters/*` + `HttpResilienceExtensionsTests` okundu; `docs/analysis/06-remediation-progress.md` ING-001/PRV-001 kabul iddiaları ve `CLAUDE.md`/`docs/architecture.md` resilience sözleşmesi kodla karşılaştırıldı. Repository/persistence katmanı, calendar acquisition (CAL-001) ve API tarafı okunmadı (başka hatlar); canlı provider yanıt formatları doğrulanamadı.

**Güçlü kararlar.**

- Typed `AdapterOutcome` + `AdapterCompleteness` sözleşmesi 'boş liste = başarı' belirsizliğini gerçekten kapatıyor: requested/expected/no-data küme aritmetiği, duplicate ve out-of-range sayımıyla birlikte yapılıyor ve `BaseAssetWorker.TryValidateSuccess` adapter'ın iddiasını bağımsız olarak yeniden doğruluyor (adapter'a güvenilmiyor).
- Mapper evidence'ları migration 020 trigger'ındaki provider-başına exact key dizisi ve observation_id grameriyle birebir örtüşüyor (TCMB 7, CoinGecko 8, OXR 9, TwelveData 16, EVDS 6 anahtar); C# ve SQL sözleşmeleri birbirini karşılıklı zorluyor, tek taraflı sessiz kayma mümkün değil.
- Dört keyed provider'ın tamamında secret yalnız HTTP header'da taşınıyor ve testler URL/query'de secret olmadığını, provider gövdesi/exception mesajının outcome `Detail`'ine veya log'a sızmadığını canary ile doğruluyor (`ForbiddenBodySecret_IsNotLoggedOrReturned`, `ProviderCodeAndMessage_AreNotReflectedIntoDetailOrLogs`).
- `ObservationAuthorityCultureTests` beş mapper'ı en-US/tr-TR/th-TH/ar-SA altında çalıştırıp observation id + evidence metninin byte-özdeş olduğunu mühürlüyor; tarih/sayı biçimlendirmesinin her yerinde `CultureInfo.InvariantCulture` ve exact-format parse kullanılmış.
- `BoundedHttpContent` Content-Length ön kontrolü + streaming okuma sırasında artımlı SHA-256 ile payload'ı sınırlıyor; ham gövde saklanmadan hash/uzunluk kanıtı üretiliyor ve `ArrayPool` buffer'ı `clearArray: true` ile iade ediliyor.
- Fiyat semantiği fail-closed: CoinGecko yalnız exact 00:00:00.000 UTC observation'ı kabul ediyor ('nearest' yok), OXR yalnız tamamlanmış UTC günü hedefliyor ve `current_day_provisional` ile erken çağrıyı HTTP'siz reddediyor, TCMB takvimde kapalı olmayan bir gün için 404 aldığında `unexpected_404` ile kalıcı hata veriyor.
- `ProviderStartupValidator` etkin worker'ların secret'larını herhangi bir DB kimlik doğrulaması, window planlaması veya dış HTTP çağrısından ÖNCE doğruluyor; worker adı anahtarları orchestrator'ın `IngestionWorkers:{Name}:Enabled` anahtarlarıyla tutarlı.
- TCMB gün-cache'i `Lazy<Task<>>` ile race-free single-flight yapıyor (kaybeden fetch task'ları sızmıyor), hata sonucu cache'lenmiyor; OXR eviction'ı `Interlocked` ile tek koşucuya indiriliyor ve gerçek wire çağrıları arasındaki 200 ms pacing `FakeTimeProvider` ile deterministik test edilmiş.

**Repo dışı bilgi gerektiren açık sorular.**

- EVDS3 `igmevdsms-dis/series=...&type=json&frequency=5` endpoint'inin gerçek yanıt şekli nedir? `EvdsInflationMapper` `Tarih` alanının "YYYY-M" (ör. "2025-1") ve `TP_FG_J0` değerinin JSON *string* olduğunu varsayıyor; klasik EVDS API'lerinde aylık `Tarih` "MM-YYYY" biçiminde de görülebiliyor. Format farklıysa `TryParseDate` her satırı atar ve TÜFE ingestion'ı hiç kayıt üretmez (fail-closed ama kalıcı işlevsizlik). Canlı bir yanıt örneğiyle doğrulanmalı.
- TCMB XML, TwelveData ve EVDS herhangi bir koşulda (bölgesel endpoint, Accept-Language, plan farkı) ondalık ayırıcı olarak virgül döndürüyor mu? L10-05'in gerçek tetiklenme olasılığı buna bağlı.
- CoinGecko `market_chart/range` mevcut plan altında 91 günlük aralıkta gerçekten günlük granülerlik ve exact 00:00:00.000 UTC damgası veriyor mu; aralık sonunda 'güncel fiyat' noktası ekliyor mu? Kod `To.AddDays(1)` ile 91 güne çıkarıp bunu varsayıyor.
- Provider'lar (CoinGecko demo, TwelveData free, OXR free) 429 yanıtlarında `Retry-After` başlığı gönderiyor mu? Göndermiyorlarsa L10-02'deki sıfır gecikmeli 4 istek her rate-limit olayında gerçekleşir.
- Üretim ağ katmanında (reverse proxy, OCI güvenlik listesi, egress gateway) askıda kalan bir HTTP gövdesini koparacak bir idle/read timeout var mı? Varsa L10-01'in gerçek etkisi 'süresiz'den 'proxy timeout kadar'a iner.

---

## L11 — Saydin.DataQualityAudit

**Doğrulanmış bulgu:** 0 Critical · 0 High · 1 Medium · 5 Low

**Kapsam.** `src/Saydin.DataQualityAudit/` altındaki 16 kaynak dosyanın tamamı (AuditRunner 1166, AuditSql 692, EvidenceBundle 413, ApiTrustAuditSql 315, EvidenceSigning 306, AuditCryptography 290, AuditOptions 234, PrincipalRetentionAuditSql 193, AuditModels/SignedAuditInput/Program/AuditAccumulator/CanonicalJson/EmbeddedMigrations/AuditFileLimits/LedgerContinuity), csproj + `infrastructure/release/Dockerfile.dqa` satır satır okundu. Karşı taraf olarak `src/Saydin.DatabaseMigrator/MigrationTrustRoot.cs`, `src/Saydin.DatabaseSecurity/SecureSecretFile.cs`, `src/Saydin.DataRepair/DqaEvidenceVerifier.cs`, `infrastructure/postgres/migrations/` (001–022 checksum doğrulaması dahil), `tests/Saydin.DataQualityAudit.Tests/*` (8 dosya) ve `tests/Saydin.DataQualityAudit.IntegrationTests/*` (2779 satır fixture+acceptance), `.github/workflows/ci.yml` ratchet'i ile `docs/analysis/06-remediation-progress.md` Paket 6 kabul iddiaları okundu. Okunmayan: DataRepair'in geri kalanı, DatabaseSecurity'nin Linux openat2 katmanı ve migration SQL gövdelerinin iç semantiği (yalnız checksum düzeyinde doğrulandı).

**Güçlü kararlar.**

- Salt-okunurluk iddiası gerçekten tutuyor: `grep -inE '\b(insert|update|delete|truncate|create |alter |drop |grant |revoke)\b'` DQA kaynaklarında tek bir DML/DDL üretmiyor (yalnız `Directory.Delete(staging)` dosya sistemi temizliği). Üstüne `REPEATABLE READ` + `SET TRANSACTION READ ONLY` + `SHOW transaction_read_only='on'` doğrulaması ve `VerifyReadOnlyRoleAsync`'te audit login'inin denetlenen 23 tabloda INSERT/UPDATE/DELETE/TRUNCATE, DB TEMP, schema CREATE veya calendar seal/activate EXECUTE yetkisi taşımasını reddeden preflight var. `EmbeddedMigrations` de yalnız gömülü kaynakları hash'liyor, hiçbir migration çalıştırmıyor.
- İmza tasarımı sağlam: imzalanan bayt dizisi her iki uçta da kanonik JSON (`SignedAuditInput` girdiyi kanonikleştirip *onun üzerinde* doğruluyor; `EvidenceBundle.VerifyAsync` manifest'in zaten kanonik olduğunu `canonical.SequenceEqual(manifestBytes)` ile mühürlüyor). `EnsureP256` hem `KeySize != 256` hem eğri OID'sini kontrol ediyor; `ImportPublicP256Pem` yalnız `PUBLIC KEY` etiketli SPKI kabul ediyor, PEM bloğu dışındaki baytları ve artık SPKI baytlarını reddediyor, tamponu sıfırlıyor. P-384 anahtarın reddi testle mühürlü.
- KMS yolu doğru kurulmuş: `OciKmsEvidenceSigner` KMS'in döndürdüğü imzayı asla körlemesine kabul etmiyor — base64 round-trip'i kanonikliğe zorluyor, `NormalizeP256Signature` ile katı DER'e (r/s uzunluk sınırları + byte-eşitlik) indirgiyor ve `VerifyHashWithSubjectPublicKeyInfo` ile *yerel, allowlist'e alınmış* public key'e karşı yeniden doğruluyor. Böylece KMS key OCID'si ile public key dosyası kriptografik olarak bağlanmış oluyor; `RetryConfiguration = null` ile sessiz yeniden denemeler de kapatılmış.
- Kanıt paketi bütünlüğü uçtan uca bağlı: tüm dosyaların path/bayt/SHA-256 listesi imzalanan manifest'in içinde; `VerifyAsync` hem beklenen dosyaların varlığını hem *fazladan* dosya olmadığını (`InventoryMatches` → `remaining.Count == 0` + `!remaining.Remove(relative)`), symlink/reparse'ı, path escape'i (`ResolveContainedPath`), derinlik/dizin sayısını ve toplam boyut taşmasını reddediyor. Yayın, rastgele isimli 0700 kardeş staging dizininden atomik `Directory.Move` ile yapılıyor ve hata halinde staging siliniyor — KMS hatası testlerinde "ne bundle ne staging kalır" olarak mühürlenmiş.
- Migration trust root migrator ile tek kaynaktan derleniyor (`<Compile Include="..\Saydin.DatabaseMigrator\MigrationTrustRoot.cs">`) ve gerçek dosya SHA-256'ları doğrulandı (018/021/022 birebir tutuyor). Preflight, `saydin_migration_control.state='ready'` + manifest checksum eşitliği + `schema_migrations`'ın gömülü kümeyle *tam* eşleşmesini (sayı, checksum ve `succeeded` durumu; yalnız `012b_create_exporter_role` için `skipped_optional`) sabit-zamanlı karşılaştırmayla zorunlu kılıyor.
- Yapısal drift, isim varlığıyla değil katalog parmak iziyle ölçülüyor: constraint tanımlarının, `pg_proc.prosrc` ve `proconfig`'in SHA-256'sı, `convalidated`/`confdeltype`, index `indnullsnotdistinct`, trigger `tgtype` tamsayısı ve ACL kümeleri çift yönlü `EXCEPT ALL` ile karşılaştırılıyor. Kritik olarak bu yollar sadece string assertion'la değil, gerçek PostgreSQL üzerinde drift enjekte eden kabul testleriyle kapsanıyor (`price_authority_structure_drift`, `api_trust_structure_drift`, `principal_retention_structure_drift`, `asset_catalog_state_drift`, `price_fence_trigger_drift`).
- Kanıt paketine ham veri sızmıyor: business key'ler ayrı bir secret HMAC anahtarıyla pseudonymize ediliyor, `ApiTrustAuditSql.Structure` bilinçli olarak tek bir `boolean` döndürüyor (kod içi yorumla gerekçelendirilmiş) ve `AssetCatalogState` katalog digest'ini SECURITY DEFINER publisher'dan bağımsız yeniden hesaplıyor. `SecretCanary_IsCountedButNeverEmitted` ve `ForgedProviderEvidence_...` testleri paketteki her dosyada canary'nin bulunmadığını fiilen doğruluyor.
- Girdi manifest doğrulaması derin ve fail-closed: `UnmappedMemberHandling.Disallow` + `CanonicalJson`'ın duplicate-property reddi + tam sayı-dışı sayı reddi; lifetime/clock-skew (±5 dk), lane tekilliği, aynı boyutta örtüşen lane reddi, source↔cadence↔assetId tutarlılığı (evds↔month↔assetId null), bütçe alt/üst sınırları ve `keyId`/`evidenceKeyId`'nin SPKI SHA-256 fingerprint'ine sabit-zamanlı bağlanması. Tüm finansal karşılaştırmalar SQL `numeric` üzerinde; C# tarafında hiç `double`/`float` yok ve tüm sorgular parametreli.

**Repo dışı bilgi gerektiren açık sorular.**

- Üretim veritabanındaki `market_calendar_releases` ve `provider_fetch_payloads` satır sayıları nedir? lane-04'ün gerçek etkisi (audit'in `TotalTimeoutSeconds` içinde bitip bitmediği) yalnız canlı hacim ölçüsüyle kapatılabilir.
- Migration 004'ün 2010'dan itibaren yazdığı `source='seed-approximation'` CPI satırları ilk production scan'inden önce `tuik` ile backfill edilecek mi? Edilmezse tarihsel her EVDS lane'i `InflationProvenance`'tan binlerce `seed_without_tuik` (DQ-005 High) ve `InflationLegacyAuthority`'den `legacy_authority_unknown` (DQ-009 High) üretecek; bu, doküman residual'ında "beklenen kapsamdaki legacy/partial/invalid authority sayısının sıfır olması" olarak koşullanmış ama repo içinden doğrulanamıyor.
- Production runbook'ları imzalı manifest'te `Target.Environment` alanına tam olarak hangi değeri yazıyor? lane-02'nin gerçek sömürülebilirliği bu operasyonel sözleşmeye bağlı.
- Audit container'ının OCI instance-principal IAM policy'si yalnız allowlist'teki key/key-version üzerinde `Sign` yetkisine mi sahip, yoksa daha geniş bir KMS erişimi mi var? Kod tarafı imzayı yerel public key'e karşı doğruladığı için güvenli, ancak yetki genişliği repo dışı.
- `--hmac-key-file` ile gösterilen dosyanın üretimdeki sahibi/mode'u ve bulunduğu mount'a kimlerin yazabildiği nedir? lane-03'ün symlink tetikleyicisinin gerçekçiliği buna bağlı.

---

## L12 — Saydin.DataRepair

**Doğrulanmış bulgu:** 0 Critical · 0 High · 2 Medium · 8 Low

**Kapsam.** `src/Saydin.DataRepair/` altındaki 17 dosyanın tamamı (Program, RepairOptions, RepairModels, SignedRepairPlan, CanonicalJson, RepairCryptography, RepairFiles, DqaEvidenceVerifier, RepairTrustLease, RepairMigrationTrust, RepairDatabase, RepairExecutor, ReceiptSigning, ReceiptStore, README, csproj) satır satır okundu. Karşı taraf olarak `tests/Saydin.DataRepair.Tests` + `tests/Saydin.DataRepair.IntegrationTests`, `src/Saydin.DataQualityAudit/CanonicalJson.cs`, `src/Saydin.DatabaseSecurity/RuntimeDatabase.cs` + `RoleContract`, `src/Saydin.DatabaseMigrator/{MigrationTrustRoot,MigrationManifest,MigrationRunner}.cs`, `infrastructure/postgres/migrations/{015,016,017}`, `.github/workflows/ci.yml` + `run-unit-coverage.sh` + `compose.integration.yml`, `docs/analysis/06-remediation-progress.md` (Paket 7) ve `docs/runbooks/` incelendi. Okunmayan: `packages.lock.json`, OCI SDK'nın kendi davranışı, `run-isolated.sh`/`run-data-repair-tests.sh` içerikleri (yalnız CI'daki çağrı noktaları doğrulandı).

**Güçlü kararlar.**

- Mutasyon yüzeyi radikal biçimde daraltılmış: planda SQL/tablo/predicate/connection string kabul edilmiyor, tek yazan operasyon `requeue_permanent_window`, UPDATE'ler `WHERE id=@id AND state=... AND pg_catalog.to_jsonb(ingestion_windows)=@preimage::jsonb` ile tam-satır CAS'e bağlı (RepairDatabase.cs:542-560). Geniş/WHERE'siz UPDATE-DELETE yok; `WindowSnapshot` 25 alanı ingestion_windows'un tüm kolonlarını birebir kapsıyor, dolayısıyla preimage hash'i gerçekten tam satırı mühürlüyor.
- Receipt sırası doğru kurgulanmış: imzalı pending receipt COMMIT'ten ÖNCE diske yazılıp doğrulanıyor (RepairExecutor.cs:44 → 101), commit sonrası atomik `Directory.Move` ile final'e terfi ediyor; PostgresException'da pending siliniyor, belirsiz commit'te DB pre/postimage ile uzlaştırılıyor. Apply yolunun idempotency/reconciliation matrisi eksiksiz.
- Rollback gerçek bir tersine çevirme: `RollbackState` requeue'nun değiştirdiği tüm alanları (state, next_attempt_at, outcome_code, error_code, updated_at, completed_at, lease üçlüsü) taşıyor ve restore sonrası `restored.SnapshotSha256`'nın hem apply receipt'inin preimage'ına hem de planın imzalı preimage'ına eşit olduğu ayrı ayrı doğrulanıyor (RepairDatabase.cs:194-203).
- Hedef veritabanı guard'ı çok katmanlı ve fiziksel: env↔plan eşlemesi (Program.cs:113-124), canlı `pg_control_system().system_identifier` SHA-256'sı, `saydin_role_contract`'ın 14 alanı, `saydin_migration_control='ready'` + 24 migration'ın checksum/state seti (RepairTrustLease.cs), ve migrator/role-bootstrap ile AYNI advisory lock anahtarı — `ContractLockKey` üç projede birebir aynı (`unchecked((long)Convert.ToUInt64(hash[..16],16))`), yani README'nin karşılıklı dışlama iddiası doğrulanıyor.
- Rol ayrımı fiilen zorlanıyor: mutasyon oturumu ingestion login'i (`schema_migrations` SELECT yetkisi OLMAMALI, ingestion_windows DELETE OLMAMALI — RepairDatabase.cs:49-51), trust doğrulaması ayrı audit login'i ile ve o rolün gerçekten salt-okunur olduğu çalışma anında sınanıyor (RepairTrustLease.cs:193-211). Admin secret'ı yalnız test fixture'ında.
- Dosya/girdi sertleştirmesi tutarlı: tüm gizli girdiler `SecureSecretFile` (mutlak yol, link yok, sahiplik, boyut sınırı) üzerinden; evidence bundle'ında symlink/reparse reddi, path traversal reddi (`ResolveContained`), tam envanter eşlemesi hash'lemeden ÖNCE ve SONRA iki kez, bütçe sınırlı okuma ve okuma sırasında dosya boyutu değişimi tespiti (`evidence_file_changed`).
- Kriptografi disiplinli: yalnız P-256, ECDSA imzalarında kanonik DER zorunluluğu (`IsCanonicalDerSignature` re-encode karşılaştırması — imza malleability'sini kapatıyor), KMS'in IEEE-P1363 ham imzasının normalize edilip yerel public key ile ayrıca doğrulanması, local signer'da self-check, plan/receipt/approval-token belleklerinin `CryptographicOperations.ZeroMemory` ile temizlenmesi.
- Hata çıktısı sızdırmıyor ve bu test edilmiş: `ApplicationErrorOutputDoesNotEchoPathsOrSecretMaterial` stderr'in tam olarak `repair rejected: code=plan_signature_invalid\n` olduğunu ve ne kök dizini ne approval token'ı içermediğini doğruluyor; entegrasyon testi apply çıktısında window id'nin geçmediğini de kontrol ediyor.
- CI kapıları gerçekten mevcut ve iddialarla uyumlu: `run-unit-coverage.sh` DataRepair için `minimum_tests=15` + `passed==total` (sıfır skip) ratchet'i uyguluyor, ci.yml `data-repair-integration.trx --minimum-executed 7` kapısını ve `saydin_data_repair_test_<32hex>` UUID-bound izole veritabanı/rol prefix'ini çalıştırıyor — 06-remediation-progress.md'deki 15+7 iddiası doğrulandı.

**Repo dışı bilgi gerektiren açık sorular.**

- Üretimde `Saydin.DataRepair` hangi artefakttan çalıştırılması planlanıyor — bastion host'ta ad-hoc SDK mı, yoksa henüz commit edilmemiş bir release image mı? `--receipt-root` için 0700 kalıcı volume ve receipt'lerin uzun süreli saklama/yedekleme politikası nerede tanımlı?
- Gerçek OCI KMS `Sign` yanıtı `signedData.signingAlgorithm`'i tam olarak `"EcdsaSha256"` string'i olarak mı döndürüyor (ReceiptSigning.cs:151'deki katı eşitlik) ve imzayı IEEE-P1363 ham formatta mı yoksa DER olarak mı veriyor? Bu yol hiçbir testte gerçek KMS'e karşı çalıştırılmamış.
- Üretim `ingestion_windows` tablolarında tipik bir pencerenin ilişkili satır sayısı (ingestion_jobs + attribution + price/inflation) nedir? `MaximumGuardRows=100_000` bütçesi, guard'ın apply başına iki kez hesaplandığı da düşünüldüğünde gerçek backfill pencereleri için yeterli mi?
- Üretim PostgreSQL topolojisinde repair oturumu aynı özel Docker ağı üzerinden mi bağlanacak (compose.production.yml çoğu servis için `PGSSLMODE: Disable` kullanıyor), yoksa ağ dışından mı? İkinci durumda README'nin `PGSSLMODE=disable` seçeneğini serbest bırakması yeniden değerlendirilmeli.
- Operasyonel olarak, requeue edilen bir pencerede `attempt_count` sıfırlanmadığı için (mevcut `IngestionWindowRepository.RequeuePermanentAsync` ile tutarlı) worker'ın gerçekten yeniden deneme yapacağı garanti mi, yoksa pencere hemen tekrar `permanent_failed` mi olur?

---

## L13 — calendar-data aracı ve calendar infrastructure

**Doğrulanmış bulgu:** 0 Critical · 0 High · 4 Medium · 11 Low

**Kapsam.** `tools/calendar-data/src/Saydin.CalendarData/*` (12 kaynak dosyanın tamamı), `Dockerfile`, `Dockerfile.dockerignore`, `Directory.Packages.props`, `csproj`, tool `README.md` ve `infrastructure/calendar/*` (3 shell script, 3 systemd unit, 2 plan örneği, env örneği, README) satır satır okundu. Karşı taraf kanıtı olarak `infrastructure/postgres/migrations/017_authoritative_market_calendars.sql` projection'ı, `src/Saydin.DataQualityAudit/AuditSql.cs`, `.github/workflows/ci.yml` + `compose.integration.yml` calendar job'ları, `infrastructure/prometheus/rules/ingestion.yml`, kök `Directory.Build.props`/`.dockerignore`, `src/Saydin.Api/Dockerfile` ve `tools/calendar-data/tests/**` (özellikle `InfrastructureCalendarContractTests`, `FailClosedParserTests`, `NormalizedCalendarReplayTests`, `CalendarAcquisitionTests`) okundu. `data/snapshots/**` ve `data/normalized/*.csv` içerik olarak review dışı tutuldu; yalnız manifest yapısı, satır sayıları, partial/closed anchor satırları ve bir TCMB yıllık index snapshot'ının href seti spot-check edildi.

**Güçlü kararlar.**

- Resmî kaynak allowlist'i gerçekten exact ve her redirect hop'unda yeniden uygulanıyor: `AllowAutoRedirect=false` + manuel redirect döngüsünde `SourceSnapshotStore.ValidateOfficialUri(source, current)` hem istekten önce hem de yeni `Location` çözüldükten sonra çağrılıyor; şema/port/userinfo/query/fragment ve host+path kind bazında sabitlenmiş. Test seti (evil host, http, farklı port, farklı ay) bunu doğruluyor.
- İndirme yolu çok katmanlı sınırlıyor: Content-Encoding reddi, Content-Type eşitliği, declared ContentLength sınırı, `ReadBoundedAsync` ile streamed sınır, PDF için `%PDF-` magic byte kontrolü, per-request linked CancellationToken timeout ve bounded retry (429/5xx ayrımıyla). Sertifika doğrulaması hiçbir yerde devre dışı bırakılmamış.
- Content-addressed snapshot modeli tutarlı: `snapshotPath` dosya adının `RawSha256`'ya eşit olması zorlanıyor (`snapshot_path_not_content_addressed`), her okuma SHA-256 ile doğrulanıyor, path escape ve symlink/reparse reddediliyor, aynı path'e farklı hash iddiası `snapshot_path_conflict` ile yakalanıyor.
- Parser'lar gerçekten fail-closed: TCMB href'leri exact regex + yıl/ay tutarlılığı + duplicate reddi + sıfır-link reddi ile; BIST tarafında operasyon metninin tamamının parse edilmiş olması (`bist_operation_unparsed_text`), gün↔hafta günü çapraz doğrulaması, tarih sütunu ile operasyon sütunu tarih kümelerinin `SetEquals` eşitliği ve PDF başlık/kolon geometrisi kontrolü. Format değişikliği sessiz yanlış gün değil, hata üretir.
- TOCTOU sertleştirmesi ciddi: base bundle kopyalandıktan sonra `EnsureInputsUnchanged`, candidate üretildikten sonra tekrar `LoadVerified` + `EnsureInputsUnchanged`, `FileMode.CreateNew` + 0600/0700 modlar, `flock` tabanlı exclusive lock ve aynı dosya sistemi içinde atomik `Directory.Move`.
- Promotion script'i candidate'ı kopyaladıktan SONRA kendi özel kopyası üzerinde tüm doğrulama zincirini tekrar çalıştırıyor (`promote-reviewed-bundle.sh:57-60`) ve bu "ilk doğrulamadan sonra kaynağı değiştir" yarışı gerçek bir bariyer/process testiyle (symlink varyantı dahil) kanıtlanmış; başarısızlıkta pending dizini temizleniyor ve `mv -T -n` ile atomik yayın yapılıyor.
- DB import yolu sağlam: calendar-scoped advisory lock, tek transaction, bounded `lock_timeout`/`statement_timeout`/`idle_in_transaction_session_timeout`, aynı release id için payload+source provenance eşitliği kontrolüyle idempotent yakınsama, seal fonksiyonu ve CAS `activate_market_calendar_release`; tüm kullanıcı/veri girdileri parametreli (`$1..$10`, binary COPY).
- Offline replay varsayılan davranış: image `CMD` `verify --data-root ...`, CI hem `--network none` ile exact satır sayılarını (`rows=7534`, `rows=1096`) hem eş zamanlı çift import'un idempotent yakınsamasını ve rollback'i doğruluyor; `infrastructure/prometheus/rules/ingestion.yml` TCMB coverage < Istanbul-yesterday, BIST horizon < 45 gün ve metrik yokluğu için runbook_url'li critical alarmlar tanımlıyor.

**Repo dışı bilgi gerektiren açık sorular.**

- TCMB'nin canlı yıllık index sayfası (`kurYYYY_tr.html`) hiç başka bir `_tr.html` bağlantısı (ör. önceki yıla navigasyon) içeriyor mu? İçerirse `AnnualMonthHrefRegex` exact eşleşmesi `tcmb_annual_href_invalid` ile günlük job'ı tamamen durdurur; repodaki 2026 snapshot'ında yalnız 8 aylık bağlantı var, ancak bu tüm yıllar/gelecek için garanti değil.
- Üretim hostunda `/var/lock` gerçekten `/run/lock`'a sembolik bağ mı ve orada lock dosyası yazan başka (root olmayan) servis var mı? lane-04'ün gerçek etkisi buna bağlı.
- Üretimde günlük TCMB planını kim/ne materialize ediyor? Repo dışında bir plan generator veya konfigürasyon yönetimi şablonu var mı (lane-02'nin etkisini belirler)?
- Borsa İstanbul, yıllık Pay Piyasası tatil tablosunda yer almayan plansız seans iptallerini hangi kanaldan duyuruyor ve bunların authoritative takvime girmesi için öngörülen operasyonel akış nedir?
- Promotion hostunda reviewer public key'i konfigürasyon yönetimi ile sabit bir yola pin'leniyor mu, yoksa çağrı anında operatörün seçtiği dosya mı kullanılıyor? lane-03'ün kalıntı riski buna bağlı.
- `calendar-release` image'ı üretimde hangi orkestrasyonla ve hangi egress allowlist'i ile çalıştırılıyor? README yalnız PostgreSQL endpoint'i olduğunu söylüyor ancak bunu zorlayan bir artefakt repoda görünmüyor.

---

## L14 — CI/CD workflow ve kapı script'leri

**Doğrulanmış bulgu:** 0 Critical · 0 High · 0 Medium · 10 Low

**Kapsam.** Hat listesindeki 28 dosyanın tamamı okundu: `.github/workflows/*.yml` (ci, deploy-staging, promote-production, release-images, restore-drill, rollback-production), `.github/compose.integration.yml`, `.github/actionlint.yaml`, `.github/pull_request_template.md` ve `.github/scripts/` altındaki 19 dosya (coverage-gate.py, test-coverage-gate.py, verify-integration-trx.py, validate-workflows.py, validate-development-compose.{sh,py}, check-doc-links.py, run-*.sh, coverage-thresholds*.json, coverage.settings.xml). Karşı taraf olarak `CLAUDE.md`, `docs/analysis/README.md`, `docs/analysis/06-remediation-progress.md`, `Saydin.Services.sln`, `Directory.Build.props`, `docker-compose.yml`, `infrastructure/release/validate-release.py` ve shell/Dockerfile envanteri okundu. `infrastructure/**` script'lerinin iç davranışı (deploy-release.sh, restore-drill.sh, manage_backup_hba.py) yalnız CI'dan çağrıldıkları arayüz düzeyinde incelendi; bu dosyaların gövde doğruluğu bu hattın kapsamı dışıdır.

**Reddedilen iddialar.**

- *Release zinciri imzalanan commit'in main'in atası olduğuna dair kanıt aramıyor* — İddia, ata kontrolünün eşdeğerini gözden kaçırıyor. release-images.yml'in ÜÇ job'unda da checkout `ref: ${{ inputs.release_tag }}` (satır 39-41, 106-108, 294-297) ve hemen ardından şu çift kontrol var (satır 49-51, 115-117, 320-321): `test "$GITHUB_WORKFLOW_REF" = "$GITHUB_REPOSITORY/.github/workflows/release-images.yml@refs/heads/main"` ve `test "$(git rev-parse HEAD)" = "$GITHUB_SHA"`. Birinci kontrol workflow'un yalnız refs/heads/main'den dispatch edilebilmesini zorunlu kılar (tag'den dispatch edilirse GITHUB_WORKFLOW_REF `@refs/tags/...` olur ve fail eder). workflow_dispatch için GITHUB_SH

**Güçlü kararlar.**

- Untrusted/dispatch girdileri hiçbir yerde doğrudan `run:` gövdesine interpolate edilmiyor: `inputs.release_tag`, `staging_run_id`, `incident_id` vb. önce `env:` bloğuna alınıp shell içinde `"$RELEASE_TAG"` olarak ve katı regex'lerle (`^v[0-9]+\.[0-9]+\.[0-9]+...$`, `^[0-9]+$`) doğrulanıyor. `pull_request_target` hiç kullanılmıyor; script injection yüzeyi kapalı.
- Tüm üçüncü taraf action'lar 40 karakterlik commit SHA ile pinli ve bu bir kapıyla (`validate-workflows.py`) zorunlu kılınıyor; ayrıca `permissions: contents: read` workflow default'u + job bazlı least-privilege (`security-events: write` yalnız codeql, `packages: write` yalnız release build) ve `persist-credentials: false` her checkout'ta uygulanmış.
- `verify-integration-trx.py` gerçekten fail-closed: total==executed==passed eşitliğini, 13 yasak counter'ın sıfır olmasını, `UnitTestResult` düğüm sayısının `total` ile eşleşmesini ve her sonucun `Passed` olmasını birlikte doğruluyor; ayrıca `ET.ParseError/OSError/ValueError` yakalanıp exit 1'e çevriliyor. Aynı sıkılık unit tarafında `run-unit-coverage.sh`'ın `passed -eq total` + `total -ge minimum` ratchet'iyle sağlanıyor. Hiçbir kapıda `continue-on-error` veya hata yutan `|| true` yok.
- Coverage kapısının kendisi test edilmiş: `test-coverage-gate.py` eksik/malformed/eşik-altı fixture'larının non-zero, geçerli fixture'ın zero döndüğünü ve container mutlak yolunun (`/repo/src/...`) doğru normalize edildiğini fail-closed doğruluyor. Kapı ayrıca `namespace_missing` ile kritik namespace'in raporlardan tamamen kaybolmasını da hata sayıyor — tautolojik assert yok.
- Integration ortamı gerçekten izole: her run için 32-hex UUID ile ayrı Compose project/DB/network/volume, host portu yok, sabit `container_name` yok, tüm image'lar (TimescaleDB, Redis, .NET SDK) manifest digest ile pinli, secret'lar `::add-mask::` ile maskeleniyor ve dosya sahiplik/mod/link-count'ları (`stat -c '%u:%g:%a:%h'` == `1001:1001:400:1` / `0:0:400:1`) ile exact assert ediliyor; cleanup adımı `if: always()` + project adı regex kontrolüyle çalışıyor.
- Calendar release smoke'unda secret sızıntısı için gerçek bir sentinel kontrolü var: importer parolası dosyadan `grep -F -f` ile tüm CLI stdout/stderr loglarında aranıyor ve bulunursa adım non-zero düşüyor — "secret log'a yazılmasın" kuralı iddia değil, test edilmiş bir kapı.
- Release/deploy/promote/rollback zinciri katmanlı ve imza tabanlı: keyless cosign imzası + SPDX/CycloneDX SBOM attestation + build provenance, çok-mimarili digest çözümü, önceki manifest SHA-256 zinciri, staging receipt ≤7 gün ve restore-drill receipt ≤31 gün tazelik kontrolü, `verify-rollback` ile schema-uyumlu adjacent rollback ve her workflow'da `$GITHUB_WORKFLOW_REF ... @refs/heads/main` kimlik kapısı.
- `validate-development-compose.py` yalnız statik kural yazmıyor, 7 mutasyonu (`fixed_name`, `public_api`, `admin_default`, `wrong_health`, `collapsed_api_ports`, `heartbeat_literal`, `mutable_test_sdk`) uygulayıp doğrulayıcının bunları yakaladığını kanıtlıyor — kapının etkisizleşmesine karşı meta-test.

**Repo dışı bilgi gerektiren açık sorular.**

- GitHub branch protection'da `Integration tests (TimescaleDB + Redis)`, `Merged unit and real-integration changed-line coverage`, `Production render, observability and mutation gates`, `Dependency, license, vulnerability, secret and IaC gates`, `CodeQL C# SAST` ve `docker-build` status check'leri gerçekten required olarak işaretli mi? Repo içinde bu kanıtlanamıyor ve `docs/analysis/06-remediation-progress.md:120-124` bunu açıkça repo-dışı governance olarak bırakıyor.
- `staging`, `production`, `production-rollback` ve `restore-drill` GitHub Environment'ları için required reviewer / wait timer / deployment branch kısıtı tanımlı mı? Özellikle rollback'in `production` yerine ayrı `production-rollback` environment'ı kullanması, o environment'ta ayrı bir onay kuralı yoksa production onay kapısının atlanması anlamına gelir.
- Tag protection rule var mı? `release-images.yml` yalnız tag adı regex'ini ve tag→commit eşleşmesini doğruluyor; main'de olmayan bir commit'e tag atılmasını engelleyen tek şey repo ayarları olabilir (bkz. lane-03).
- Workflow'lardaki commit SHA pinleri gerçekten yorumdaki sürümlere mi karşılık geliyor? Örn. `actions/checkout@11d5960a326750d5838078e36cf38b85af677262 # v4` — `validate-workflows.py` yalnız 40-hex biçimini doğruluyor, SHA'nın ilgili action reposuna ve sürüme ait olduğunu doğrulamıyor; bu ancak ağ erişimiyle (GitHub API) teyit edilebilir.
- `saydin-release`, `saydin-staging`, `saydin-production`, `saydin-restore` etiketli self-hosted runner'lar nasıl izole edilmiş (ephemeral mi, iş yükleri arası temizlik var mı, ağ segmentasyonu)? `RUNNER_TEMP`'in her job başında temizlendiği varsayımı bu runner'ların yapılandırmasına bağlı ve rollback workflow'unda `test ! -e "$release_dir"` koruması yok.
- `vars.SAYDIN_*_ENV_FILE`, `vars.SAYDIN_RUNTIME_IMAGE_LOCK_FILE`, `vars.SAYDIN_RESTORE_CONTRACT_FILE` gibi operator dosyalarının runner üzerindeki gerçek içeriği/izinleri nedir? Workflow yalnız mutlak yol olmasını doğruluyor; içeriğin doğruluğu repo dışı.

---

## L15 — Production deployment ve observability

**Doğrulanmış bulgu:** 0 Critical · 2 High · 6 Medium · 8 Low

**Kapsam.** L15 dosya listesinin tamamı okundu: `infrastructure/deployment/*` (compose.production.yml 829 satır, Caddyfile, Dockerfile.caddy, blackbox.yml, production.env.example, 4 validator + 2 self-test, tests/ fixture'ları), `infrastructure/otel/*`, `infrastructure/prometheus/*` (6 rule dosyası + rules.test.yml) ve `infrastructure/alertmanager/*`. Karşı taraflar da doğrulandı: alert ifadelerinin dayandığı metrik adları `src/Saydin.Shared/Diagnostics/SaydinMetrics.cs` ve her iki `Program.cs` OTel kaydından; port sınırı `ApiPortBoundaryMiddleware`/`Program.cs`'ten; validator'ların gerçekten çağrıldığı yer `.github/workflows/ci.yml`, `release-images.yml`, `promote-production.yml` ve `infrastructure/release/deploy-release.sh`'ten. İki bulgu (`SaydinActivityLogLoss`) gerçek `promtool test rules` çalıştırılarak ampirik olarak kanıtlandı. Okunmayanlar: `infrastructure/backup/*` içeriği (yalnız metrik yazan fonksiyonlar), migration SQL'leri ve API iş mantığı — bunlar başka hatların kapsamı.

**Güçlü kararlar.**

- Ağ ve port sınırı örnek: yalnız Caddy 80/443 yayımlıyor (compose.production.yml:308-317), `app`/`data`/`backup-db`/`management` `internal: true`, egress ağları tek tüketiciye kilitli ve `validate-production.py:298-307` her egress ağının tüketici kümesini birebir eşitlikle doğruluyor; `internal_port` ve `telemetry_public_port` mutation'ları bunu mühürlüyor.
- Container hardening tüm servislerde tek tip ve makine-zorunlu: `read_only`, `cap_drop: [ALL]`, `no-new-privileges:true`, non-root uid, `pids_limit`, `cpus`/`mem_limit`, `stop_grace_period`, bounded json-file logging — hepsi validate-production.py:142-162'de kontrol ediliyor ve mutation testleriyle korunuyor.
- Secret hijyeni gerçekten uygulanmış: hiçbir sır environment/argv/label'da değil, hepsi tüketici-başına ayrı read-only external volume + `*_FILE` referansı; `SECRET_KEY`/`SAFE_SECRET_REFERENCE`/`FORBIDDEN_COMMAND` regex'leri ve `raw_secret` mutation'ı bunu fail-closed hâle getiriyor. Redis parolası bile argv yerine `redis.conf` üzerinden veriliyor.
- API yüzey ayrımı katmanlı ve doğru: `/metrics` ve `/health/ready` yalnız management portunda (ApiPortBoundaryMiddleware.cs:33-36), `/health/live` bağımlılık detayı sızdırmıyor (`Predicate = _ => false`), ayrıca Caddy `/metrics`, `/openapi/*`, `/scalar/*` için 404 dönüyor — üç bağımsız katman aynı sınırı koruyor.
- `Dockerfile.caddy` upstream binary'deki `cap_net_bind_service` file capability'sini yorumlu bir gerekçeyle strip ediyor; `cap_drop: ALL` + `no-new-privileges` altında exec'in reddedileceğini bilerek çözmüş — nadiren fark edilen, doğru bir ayrıntı.
- Collector self-telemetry'si `without_type_suffix: true` / `without_units: true` ile yapılandırılmış ve `TelemetryExportFailure`/`TelemetryQueueNearCapacity` kuralları tam olarak bu adlandırmaya göre yazılmış; her ikisi de rules.test.yml'de test edilmiş — konfigürasyon ile alert ifadesinin bilinçli eşleştirilmesi.
- Telemetri dayanıklılığı ciddiye alınmış: trace ve log exporter'larında external volume üzerinde `file_storage` sending queue + sınırlı `retry_on_failure`, Tempo/Loki retention'ı `${...:?}` ile zorunlu env'e bağlı ve `otel_queue`/`tempo_data`/`loki_data` external volume zorunluluğu validator'da mühürlü.
- CI gerçek araçlarla gerçek artifact'ları doğruluyor: `promtool check config` + `promtool test rules`, `amtool check-config`, `otelcol validate`, `tempo -config.verify`, `loki -verify-config` ve `caddy validate` — hepsi digest-pinned imajlarla, read-only/cap-drop container'larda (validate-production-assets.sh:40-74). Ayrıca compose iki farklı project adıyla iki kez render edilip izolasyon kanıtlanıyor.

**Repo dışı bilgi gerektiren açık sorular.**

- Repo dışındaki 'root-only control-plane' yazılabilir external volume'leri (özellikle `otel_queue` → uid 10001, `caddy_data`/`caddy_config` → uid 1000) container uid'sine chown ediyor mu? Repoda bunu yapan veya doğrulayan hiçbir artifact yok (lane-11).
- `deploy-release.sh` sonrası Prometheus/Alertmanager/exporter'ları başlatan bir operatör prosedürü fiilen uygulanıyor mu ve release-promotion.md'nin istediği 'alert game-day' kanıtı gerçekten üretiliyor mu? (lane-02)
- Repo dışında render edilen Alertmanager receiver'ları bir heartbeat / dead-man's-switch entegrasyonu içeriyor mu; içermiyorsa susan bir Prometheus nasıl tespit edilecek? (lane-08)
- Gerçek OCI ortamında `edge` bridge ağındaki blackbox-exporter, host'un kendi public IP'sine NAT hairpin ile ulaşabiliyor mu? Ulaşamazsa `SaydinPublicProbeFailed` kalıcı false-positive üretir.
- README'nin talep ettiği host firewall/DNS politikası `provider-egress`, `alert-egress`, `backup-egress`, `kms-egress` ve `edge` ağlarını gerçekten onaylı hedeflere kısıtlıyor mu? Compose bu ağları yalnız non-internal olarak tanımlıyor, çıkış hedefini sınırlamıyor.
- Canlı bir scrape üzerinde doğrulanması gereken metrik adları: OTel .NET Prometheus exporter'ı `saydin.activity_log.write.failures.total` için gerçekten `saydin_activity_log_write_failures_total` mi üretiyor (çift `_total` yok mu), ve `saydin.market_calendar.coverage.horizon.days` (unit: d) için `saydin_market_calendar_coverage_horizon_days` mi (çift `_days` yok mu)? Repoda bunu mühürleyen hiçbir test yok.
- postgres-exporter kullanılan sürümde, `SAYDIN_EXPORTER_LOGIN` rolünün sahip olduğu yetkilerle `pg_stat_activity_count` ve `pg_up` serileri gerçekten üretiliyor mu? `SaydinPostgresConnectionsHigh` bu metriğe bağlı ve test edilmemiş durumda.

---

## L16 — Backup/restore ve release supply chain

**Doğrulanmış bulgu:** 1 Critical · 2 High · 5 Medium · 7 Low

**Kapsam.** `infrastructure/backup/*` (Dockerfile, backup-entrypoint.sh, prepare-recovery.sh, restore-drill.sh, select-base-snapshot.py, manage_backup_hba.py, restore_target_guard.py, restore-contract.env.example, tests/*) ve `infrastructure/release/*` (deploy-release.sh, rollback-release.sh, verify-signed-release.sh, release_manifest.py, make_image_record.py, render-deployment-env.py, validate-release.py, trivy.yaml, schema, lock example, Dockerfile.dqa, tests/*) dosyalarının tamamı okundu. Karşı taraf olarak `.github/workflows/{release-images,deploy-staging,promote-production,rollback-production,restore-drill}.yml`, `.github/workflows/ci.yml` ilgili adımları, `.github/scripts/run-backup-auth-tests.sh`, `infrastructure/deployment/compose.production.yml` (postgres/database-backup/database-wal-archive), `infrastructure/deployment/validate-production.py`, `infrastructure/prometheus/rules/host-backup.yml`, `src/Saydin.DataQualityAudit/{Program,EvidenceBundle}.cs` ve `docs/runbooks/restore-drill.md` doğrulama amacıyla okundu. Gerçek bir restore/deploy çalıştırılmadı; yalnız `--cap-drop ALL` + `chown` davranışı lokal Docker ile ampirik doğrulandı.

**Reddedilen iddialar.**

- *`deploy-release.sh` manifest bağlama kontrolü Tempo ve Loki image'larını atlıyor — iki üretim image'ı imzalı manifest'e bağlanmadan deploy edilebilir* — Dosya gözlemi doğru (deploy-release.sh:38-41 `runtime` sözlüğünde `tempo`/`loki` yok), ama ÇIKARILAN SONUÇ ters. Satır 43 `expected.update({runtime[name]:reference for name,reference in manifest["runtimeImages"].items()})` — manifest'in TÜM runtimeImages anahtarları üzerinde dönüp `runtime[name]` yapıyor. release_manifest.py:15 `EXPECTED_RUNTIME_IMAGES` 11 anahtar içeriyor (loki ve tempo dahil) ve satır 137 `exact_keys(root["runtimeImages"], set(EXPECTED_RUNTIME_IMAGES), ...)` bunları TAM eşleşme olarak zorunlu kılıyor; ayrıca satır 26'da `release_manifest.py verify` zaten başarıyla çalışmış o

**Güçlü kararlar.**

- İmza doğrulama zinciri gerçekten derin ve atlatılması zor: `verify-signed-release.sh` manifest'i cosign keyless (Rekor dahil, `--insecure-ignore-tlog` yok) doğruluyor, ardından her image için index digest + her iki platform digest'i + iki SBOM tipinin attestation'ını + `gh attestation verify` provenance'ını ve SBOM dosyalarının içerik SHA-256'sını imzalı manifest'e bağlıyor. `image['name']` `EXPECTED_IMAGES` ile sınırlı olduğu için SBOM yol birleştirmesinde traversal da mümkün değil.
- `rollback-admission-self-test.py` tautolojik değil, gerçek bir davranış testi: sahte `cosign`/`gh`/`docker` binary'leri PATH'e koyup target imzasını bozuyor ve exit 78, doğru stderr kodu, `docker`'ın HİÇ çağrılmamış olması ve receipt'in yazılmamış olması koşullarını birlikte mühürlüyor — yani 'ilk mutasyondan önce admission' iddiası kanıtlanmış.
- `release_manifest.py` kanonik JSON'u byte-exact round-trip ile doğruluyor (`verify_file`), duplicate JSON anahtarını `object_pairs_hook` ile reddediyor, `exact_keys` ile fazladan/eksik alanı kapatıyor, `latest`/`CHANGE_ME`/`example` gibi placeholder'ları serileştirilmiş manifest üzerinde tarıyor, ve `verify-rollback` hem bitişik-manifest hash zincirini hem de mevcut terminal migration'ın hedefin [min,max] şema aralığında olmasını şart koşuyor (geriye uyumluluk kapısı gerçekten var).
- `restore_target_guard.py` descriptor-göreli, `O_NOFOLLOW|O_DIRECTORY` ile `mkdir`/`open` yapıp her adımda `fstat` ile uid+0700 doğruluyor ve root'un boş olmasını şart koşuyor; self-test symlink root, symlink leaf, `..` traversal, boş-olmayan root, yanlış mod ve yabancı sahip vakalarını gerçekten çalıştırarak kapatıyor.
- `manage_backup_hba.py` atomik ve idempotent: `mkstemp` + `fchmod`/`fchown` (orijinal moddan) + `fsync` + `os.replace` + parent `fsync`; marker sınırlı blok, blokun ilk generic `host` kuralından önce olmasının doğrulanması, blok DIŞINDA backup rolüne değinen her satırın reddi, symlink/hardlink/kanonik-yol kontrolleri. `deploy-release.sh` kurulumdan sonra `pg_reload_conf()` + `pg_hba_file_rules WHERE error IS NOT NULL = 0` ile yeniden doğruluyor.
- Restore drill gerçekten veri doğruluyor, yeşil-ama-anlamsız değil: DQA `scan` herhangi bir High+ kontrolde ihlal bulursa `AuditExitCodes.Violations` döner (`Program.cs:91-93`) ve drill `docker start -a ... || die` ile bunu hata sayar; ardından kanıt paketi `--network none` ile offline imza doğrulamasından geçer. Buna ek olarak RoleBootstrap verify, Migrator `--verify-only` ve API health smoke'u da restore edilen küme üzerinde koşuyor.
- İzolasyon tasarımı titiz: restore edilen DB/API yalnız `--internal` ağda; kısa ömürlü egress ağı sadece restic fetch ve KMS imzalama container'ına veriliyor; kaynak adları run-id ile benzersiz ve `saydin-restore-` prefix guard'ı var; cleanup container/volume/network'ü tamamen siliyor.
- Kimlik/secret hijyeni güçlü: hiçbir kimlik bilgisi argv veya env'de taşınmıyor (yalnız dosya yolu), `private_file()` sahiplik/mod/hardlink sayısı/uzunluk/sondaki newline kontrolü yapıyor, `:` içeren parola pgpass yazılmadan reddediliyor, tüm base image'lar digest-pinned, `render-deployment-env.py` secret-şekilli anahtarları reddediyor.
- Promotion kapısı DR kanıtını sert şart koşuyor: `promote-production.yml` hem ≤7 günlük imzalı staging receipt'ini hem ≤31 günlük imzalı restore-drill receipt'ini, ikisini de manifest SHA-256'sına ve tam alan setine bağlayarak doğruluyor — DR tatbikatı üretim release'inin ön koşulu.

**Repo dışı bilgi gerektiren açık sorular.**

- Restore drill workflow'u bugüne kadar hiç uçtan uca yeşil koştu mu? (lane-01 bulgusu, `--cap-drop ALL` altında `chown`'un kesin başarısız olduğunu gösteriyor; eğer runner'da userns-remap veya rootless Docker daemon kullanılıyorsa volume zaten map edilmiş uid'e ait olabilir ve davranış farklılaşabilir — runner'ın daemon konfigürasyonu repo dışı.)
- Üretim PostgreSQL veri dizininin güncel boyutu ve büyüme hızı nedir? Bu, 2 GiB tmpfs / 1 GiB `mem_limit` base-backup tavanına (lane-02) ne zaman çarpılacağını belirler.
- Üretimde gerçek yazma hacmi / WAL segment doldurma hızı nedir ve `archive_timeout` compose dışında (ör. `ALTER SYSTEM` veya operatör tarafından yönetilen `postgresql.conf`) ayarlanmış mı? (lane-03)
- Üretim PostgreSQL container'ının `lc_messages` değeri nedir? (lane-11'in tetiklenip tetiklenmeyeceğini belirler.)
- `SAYDIN_RESTORE_AUDIT_OUTPUT_DIR` her drill koşusundan önce runner tarafından taze materyalize ediliyor mu, yoksa kalıcı bir dizin mi? (lane-07)
- Restic repository'si başka bir deployment ile paylaşılıyor mu, ve object-store'un lock/prune davranışı (S3 tutarlılığı, eşzamanlı lock süresi) ne? (lane-05'in gerçek çakışma sıklığı buna bağlı.)
- `SAYDIN_RUNTIME_IMAGE_LOCK_FILE` ile işaret edilen runtime-image lock dosyasının gözden geçirme/onay süreci nedir; tempo/loki digest'leri kim güncelliyor? (lane-08'in operasyonel etkisi buna bağlı.)

---

## L17 — Build, compose ve paketleme

**Doğrulanmış bulgu:** 0 Critical · 0 High · 2 Medium · 3 Low

**Kapsam.** Şu dosyalar tam olarak okundu ve/veya a274c62..f9f608d diff'i incelendi: .env.example, .gitignore, Directory.Build.props, Directory.Packages.props, Saydin.Services.sln, docker-compose.yml, global.json; ayrıca 21 packages.lock.json dosyası otomatik script ile merkezi pin'lerle karşılaştırıldı. Doğrulama için lane dışı ama doğrudan ilişkili dosyalar da (kanıt amaçlı) okundu: .github/workflows/ci.yml, .github/scripts/run-unit-coverage.sh, src/Saydin.DatabaseRoleBootstrap/*.csproj, src/Saydin.DatabaseSecurity/*.csproj, tests/Saydin.DatabaseRoleBootstrap*.csproj, infrastructure/postgres/Dockerfile.migrator, tests/Saydin.Api.IntegrationTests/Fixtures/DatabaseFixture.cs, CLAUDE.md diff. Servis kaynak kodu, endpoint/repository katmanları ve migration SQL'leri bu hattın kapsamı dışıdır.

**Güçlü kararlar.**

- Central Package Management tam disiplinli uygulanmış: ManagePackageVersionsCentrally + CentralPackageTransitivePinningEnabled açık, hiçbir csproj'da floating '*' sürüm veya VersionOverride yok (repo genelinde grep ile doğrulandı).
- Önceki review'de bulunan yüksek-önem transitive zafiyetler gerçekten kapatılmış: Microsoft.OpenApi GHSA-v5pm-xwqc-g5wc (≥2.7.5 gerekiyordu) → 2.11.0'a pinlenmiş ve lock dosyalarında resolved=2.11.0 doğrulandı; OCI.Common'ın getirdiği Newtonsoft.Json 12.0.3 → tüketen projelerde (DataQualityAudit, DataRepair) CentralTransitive ile 13.0.4'e yükseltilmiş.
- NuGetAudit=true + NuGetAuditMode=all + NuGetAuditLevel=high kombinasyonu, NU1903/NU1904'ü WarningsAsErrors'a eklemesiyle birlikte audit bulgularının artık sessizce warning olarak geçmemesini sağlıyor (önceki review'in 'NuGetAudit=false' endişesini kapatan gerçek bir mimari değişiklik).
- FluentAssertions 8.10.0 → 7.2.0'a bilinçli düşürülmüş, gerekçe (v8 Xceed lisans/non-commercial riski) yorum olarak belgelenmiş.
- docker-compose.yml'de dışa açılan tüm portlar (postgres, redis, pgadmin, redis-insight, aspire-dashboard, prometheus, saydin-api) 127.0.0.1'e bind edilmiş — 0.0.0.0/LAN sızıntısı yok.
- Dev secret akışı (secret-source-generator → secret-materializer → servis-özel named volume + UID ayrımı) düşünülmüş bir least-privilege tasarımı; .env.example'da gerçek secret yok, yalnız placeholder/nonsecret topology.
- .gitignore, .env* dosyalarını (.env.example hariç) doğru dışlıyor, packages.lock.json'ı genel *.lock.json kuralından doğru negate ediyor — 21 proje lock dosyasının tamamı gerçekten track edilmiş (06-remediation-progress.md'nin '21/21' iddiasıyla uyumlu, doğrulandı).
- global.json exact SDK 10.0.400 + rollForward:disable, CI'ın global-json-file kullanımı ve Docker SDK image digest pin'iyle tutarlı — reprodüktif build zinciri sağlam.

**Repo dışı bilgi gerektiren açık sorular.**

- CI'daki .github/compose.integration.yml stack'i gerçekten PGHOST/admin-connection'ı DatabaseRoleBootstrap/DatabaseSecurity testlerine doğru wiring ile sağlıyor mu ve bu testler required job'da fiilen 'executed' (skipped değil) mi geçiyor? (Bu dosya L17 kapsamı dışında; CI job loglarına bakmadan doğrulanamaz.)
- OpenTelemetry.Exporter.Prometheus.AspNetCore için upstream'de artık stable bir sürüm yayınlandı mı, yoksa paket hâlâ yalnız prerelease dağıtılıyor mu? (repo dışı NuGet.org bilgisi gerektirir.)

---

## L19 — Dokümantasyon, ADR ve runbook tutarlılığı

**Doğrulanmış bulgu:** 0 Critical · 0 High · 7 Medium · 0 Low

**Kapsam.** docs/** (analysis, architecture, decisions, deployment, runbooks — 19 dosyanın tamamı, development-guide, cache-strategy, high-traffic-checklist, README), kök CLAUDE.md/README.md/CONTRIBUTING.md/SECURITY.md tam okundu; her runbook/ADR/checklist iddiası ilgili gerçek koda (src/), migration dosyalarına, Prometheus alert kurallarına, CI workflow'una ve compose dosyalarına karşı çapraz doğrulandı. docs/analysis/00-04 (orijinal review raporları) ve remediation/*.md (3 plan dosyası) yalnız ilgili iddiaları teyit için taranmış, satır satır okunmamıştır; bunlarda ek sistematik bulgu aranmadı.

**Güçlü kararlar.**

- Runbook'larda referans verilen tüm Prometheus alert isimleri (SaydinIngestionStale hariç) infrastructure/prometheus/rules/*.yml ile birebir eşleşiyor — büyük ölçekli bir doğrulama işi doğru yapılmış.
- Release/rollback/restore-drill runbook'larındaki tüm script ve workflow referansları (deploy-release.sh, rollback-release.sh, promote-reviewed-bundle.sh, restore-drill.yml, rollback-production.yml vb.) gerçekten repoda mevcut ve doğru yollarda.
- docs/cache-strategy.md'deki tüm key formatları ve TTL değerleri (whatif:v3, dca:v2, assets:list/info, price/nearest-price/latest-date) kod ile birebir (TimeSpan.FromHours değerleri dahil) doğrulandı.
- ADR-009/ADR-010'daki migration SHA-256 hash iddiaları (021/022) gerçek dosya checksum'larıyla tam eşleşiyor; ADR-010'un decompress/recompress/role anlatımı migration 022 SQL içeriğiyle tutarlı.
- architecture.md, high-traffic-checklist.md ve activity-logging.md, X-Device-ID'nin artık authorize etmediğini ve installation principal modeline geçildiğini doğru şekilde güncellemiş (CLAUDE.md'nin aksine).
- CI workflow'undaki (--minimum-executed) TRX eşikleri ile docs/analysis/06-remediation-progress.md'deki güncel sayılar (57, 84, 72, 76, 7, 124, 24 migration) tutarlı.
- docs/deployment/README.md kısa ve net şekilde arşiv-plan/kanonik-manifest ayrımını yapıyor, tüm iç linkler ve dosya referansları doğru.

**Repo dışı bilgi gerektiren açık sorular.**

- ADR-008'in 4. ve 5. maddesindeki (gerçek PostgreSQL fixture testleri, EXPLAIN ANALYZE index-scan kanıtı) release-gate kanıtlarının fiilen üretilip üretilmediği bu lane'in dosya kapsamı dışında (test dosyaları başka bir hatta); bu nedenle ADR-008'in 'migration 018 bekliyor' ifadesinin tamamen mi yoksa kısmen mi güncel olmadığı ayrı bir hat tarafından teyit edilmeli.
- docs/analysis/remediation/01-03 plan dosyaları ve docs/analysis/00-04 orijinal review raporları satır satır okunmadı; bu dosyalarda ek 'kabul kanıtı vs kod' çelişkisi olup olmadığı doğrulanmadı.

---

## L18a — Saydin.Api test kalitesi

**Doğrulanmış bulgu:** 0 Critical · 0 High · 4 Medium · 8 Low

**Kapsam.** L18a.files listesindeki 59 dosyanın tamamı okundu (yeni dosyalar baştan sona; değiştirilenlerde tam dosya). İddiaları doğrulamak için hat dışına da bakıldı: WhatIfCalculator/DcaCalculator/SavedScenarioRepository/EndpointExtensions/CalculationCacheEntries/CalculationTelemetry/ActivityLogBuilder üretim kodu, WhatIf-DCA-Assets endpoint'lerinin activity-log payload'ları, .github/workflows/ci.yml integration job'ı ve .github/scripts/verify-integration-trx.py kapısı, docs/analysis/README.md + 06-remediation-progress.md kabul kanıtları. Testler çalıştırılmadı (lokal .NET yok, salt-okunur review); bulgular kod okuması ve xUnit/MeterListener/TRX semantiğine dayanıyor.

**Güçlü kararlar.**

- Required-mod fail-closed zinciri gerçekten kapalı: DatabaseFixture/RedisFixture/ErrorContractWebAppFactory required modda eksik env, güvensiz hedef, bağlantı hatası ve eksik migration'da constructor'da throw ediyor; IntegrationTestEnvironment prod/staging işareti ve run-id bağlı DB/Redis adını bağlantı AÇILMADAN önce doğruluyor ve bu saf doğrulama yüzeyi IntegrationTestEnvironmentTests ile doğrudan sınanıyor. .github/scripts/verify-integration-trx.py `total != executed`, `passed != executed`, `notExecuted/inconclusive != 0` ve `UnitTestResult` sayısı uyuşmazlığında fail ettiği için SkippableFact skip'leri required CI'da gerçekten hataya dönüşüyor.
- DcaCalculatorTests finansal matematiği literal beklenen değerlerle kilitliyor: üç nakit akışlı terminal-CPI reel getirisi (331m / -31m / -9.37m), tek katkıda Fisher paritesi (-16.67m) ve 'yalnız yanıt sınırında yuvarla' testi (ham 1.00600899399 → %0.60; yuvarlanmış değer kullanılsaydı yanlışlıkla %1.00 çıkacağı yorumla belgelenmiş). Bu, mirror-implementation değil gerçek oracle.
- Gerçek Redis kota/limiter testleri anlamlı invariant'ları kanıtlıyor: iki guard instance'ının tek atomik cap'i paylaşması (25 allow / 35 deny), aynı nonce ile replay'in ikinci kez decrement/increment etmemesi, 48 saatlik TTL, /24 ve /64 ağ bucket'ları ve Redis key'lerinde ham IP/principal/anahtar materyalinin bulunmaması.
- Gerçek PostgreSQL keyset testi bağımsız referansla çalışıyor: 37 satır 7'lik sayfalarla gezilip DB'nin kendi `ORDER BY created_at DESC, id DESC` çıktısıyla karşılaştırılıyor, tekrar/eksik olmadığı ve başka kullanıcının satırının gelmediği doğrulanıyor, üstelik `EXPLAIN` ile `idx_saved_scenarios_user_created_id_desc` kullanımı ve advisory-lock altında 20 rakip arasından tam bir kazanan çıkması sınanıyor.
- Hata sözleşmesi iki katmanlı ve mühürlü: ExceptionHandlerContractTests altyapısız olarak her handler'ın status/type/code/traceId/problem+json çıktısını ve sızıntı-sentinel'lerini (stack detayı, upstream 'twelvedata') doğruluyor; ErrorMessagesLocalizationTests 35 resx key'ini `ResourceNotFound == false` ile kilitliyor ve yanlış ResourcesPath'in neden çalışmadığını negatif testle donduruyor.
- NullLogger yerine gerçek log/metrik sink'leri kullanılmış: TestLogger ile structured property assertion'ları (bucket var, ham tutar yok; QuotaUnavailable kodu var, ham IP/device sentinel'i yok), MeterListener ile drop/rejected sayaçlarının tag allowlist'i ('action' değeri attacker-controlled ise 'unknown'a indirgeniyor) doğrulanıyor.
- Eşzamanlılık testleri Thread.Sleep/timing yerine TaskCompletionSource el sıkışmasıyla deterministik kurulmuş (AssetService identity coalescing, iptal edilen loader'ın memo'yu zehirlememesi, scoped memo'nun instance'lar arası sızmaması); zaman bağımlılığı FakeTimeProvider ile donduruluyor (ActivityLogBuilder duration/CreatedAt, ActivityLogChannelTelemetry warning penceresi, DailyLimitExceeded resetAt).
- Integration test izolasyonu bilinçli tasarlanmış: SUT sorguları managed API login'iyle, kurulum/temizlik ayrı admin kimliğiyle yapılıyor; AuthorityObservationScenario her koşuda benzersiz asset/symbol üretip IAsyncDisposable ile temizliyor; DB ve Redis ayrı collection'larda, DB'ye dokunan tüm sınıflar tek collection'da serileştirilmiş.

**Repo dışı bilgi gerektiren açık sorular.**

- Required CI unit job'ı Saydin.Api.Tests'i gerçekten Linux runner/konteynerinde mi koşuyor? Değilse InstallationCredentialKeyringTests'teki 10 `if (!OperatingSystem.IsLinux()) return;` testi hiç çalışmadan 'passed' sayılır ve raporlanan 545 sayısı bu güvenlik yüzeyini içermez.
- Kabul kanıtı olarak raporlanan 'API unit 545/545, 0 failed' koşusu kaç kez ve kaç çekirdekli runner'da tekrarlandı? lane-01'deki MeterListener paralellik girişimi düşük-çekirdekli tek koşuda gizli kalabilir; yüksek paralellikte tekrarlanıp tekrarlanmadığı repo dışı bilgi.
- Branch protection'da `Integration tests (TimescaleDB + Redis)` status check'i gerçekten required olarak işaretlenmiş mi? TRX kapısının fail-closed davranışı ancak job zorunluysa anlam taşıyor (docs/analysis/06-remediation-progress.md bunu repo dışı olarak işaretliyor).
- Üretim ortamında `DistributedSecurityLimiter:Enabled` gerçekten true mu? lane-02'deki kapsam boşluğunun etkisi, bu bileşenin canlıda aktif olup olmamasına göre değişir.

---

## L18b — PriceIngestion + calendar-data test kalitesi

**Doğrulanmış bulgu:** 0 Critical · 1 High · 0 Medium · 10 Low

**Kapsam.** Hattın 41 dosyasının tamamı okundu: `tests/Saydin.PriceIngestion.Tests/**` (145 test case), `tests/Saydin.PriceIngestion.IntegrationTests/**` (40 test case) ve `tools/calendar-data/tests/Saydin.CalendarData.Tests/**` (80 test case). Doğrulama için lane dışı karşı taraflar da okundu: `IIngestionWindowRepository`/`IngestionWindowContracts.cs`, `TcmbAdapter._dayCache`, `TwelveDataMapper.MapContractlessFixture`, `PriceIngestionRepository`/`InflationIngestionRepository`, `MigrationTrustRoot`, `infrastructure/postgres/migrations/` dosya listesi, `.github/workflows/ci.yml` şema/TRX kapıları, `.github/compose.integration.yml`, `.github/scripts/run-*-tests.sh`, `verify-integration-trx.py`, `run-unit-coverage.sh` ve `docs/analysis/06-remediation-progress.md` kabul kanıtı tablosu. Migration SQL gövdeleri ve DQA/API test hatları okunmadı (başka hatlar).

**Reddedilen iddialar.**

- *Write fence'in tek yapısal zayıflığı (price trigger 'O' modunda) testlerde beklenen davranış olarak sabitlenmiş ama runtime ingestion principal'ı için bypass negatif testi yok* — Gözlemlerin ham kısmı doğru: `IngestionWriteFenceIntegrationTests.cs:251-263` price trigger için `tgenabled='O'`, inflation için `'A'` bekliyor; fixture (`IngestionDatabaseFixture.cs:113-130, 194-206, 253-278`) `session_replication_role='replica'` ile fence'i atlıyor; `016_ingestion_write_fence.sql:11-13, 105-108` kısıtı yorumda kabul ediyor. Ancak iddianın kalbi — "regresyon hiçbir test kapısı tarafından yakalanmaz" — yanlış: (1) **Tablo sahipliği** `src/Saydin.DatabaseMigrator/MigrationRunner.cs:1591-1624` `application_relation_security_mismatch` fingerprint'i ile mühürlü — `price_points` da

**Güçlü kararlar.**

- Fault injection gerçek ve anlamlı: `IIngestionPersistenceFaultInjector` ile before-commit / after-commit hataları enjekte edilip `BeforeCommitFault_RollsBackDataWindowAndJobTogether` ve `CommitAckLoss_RerunConvergesWithoutDuplicate` testleri data + `ingestion_windows` + `ingestion_jobs` üçlüsünün birlikte geri alındığını ve yeniden koşuda duplicate üretilmediğini gerçek PostgreSQL üzerinde doğruluyor.
- Write fence negatif matrisi geniş ve DB-seviyesinde: sahte token, yanlış asset/tarih/source/job_type, süresi dolmuş kira, reclaim sonrası bayat token, terminal pencere, token'sız payload/attribution ve timestamp kolonu ACL'i — hepsi exact SQLSTATE (`42501`/`23514`/`23503`) ile, savepoint izolasyonu ve testin sonunda tam `RollbackAsync` ile assert ediliyor.
- `SuppressedBatch_RollsBackDataWindowAndJob_InsteadOfFalseSuccess` gerçek bir `BEFORE INSERT … RETURN NULL` trigger'ı kurup sessiz yazma kaybının "başarı" olarak sonuçlanmadığını (`expected=1, affected=0`) kanıtlıyor — sessiz veri kaybı sınıfının doğrudan testi.
- Adapter testlerinde secret/PII hijyeni canary ile ölçülüyor: `ProviderBodyAndNetworkSentinels_AreAbsentFromOutcomeAndLogs`, `ForbiddenBodySecret_IsNotLoggedOrReturned`, `NetworkException_DoesNotLeakMessageIntoOutcomeOrLog`; ayrıca API key'lerin yalnız header'da olduğu ve URI/query'de görünmediği (`AppId_IsTokenHeader_NotUrlOrQuery`, `Key_IsHeaderOnly…`, `AuthorizationSecret_IsHeaderOnly`) doğrulanıyor.
- `ObservationAuthorityCultureTests` beş mapper'ın observation id / SourceRaw çıktısını `en-US`, `tr-TR`, `th-TH`, `ar-SA` altında byte-eşit olarak pinliyor — authority hash'lerini sessizce bozacak kültür sınıfı hataları için doğru ve nadir bir test.
- `HttpResilienceExtensionsTests` `FakeTimeProvider` ile 1+3 deneme, 5. mantıksal hatada devrenin açılması, açık devrede sıfır wire çağrısı, `BreakDuration` sonrası half-open kapanışı ve `Retry-After` clamp'ini tamamen deterministik doğruluyor (gerçek bekleme yok).
- Calendar acquisition testleri ağ yüzeyini gerçekten fail-closed sınıyor: redirect'te scheme/host/port/exact-path yeniden doğrulama, bounded redirect döngüsü, beyan edilen ve stream edilen oversize, `Content-Encoding` reddi, media type uyuşmazlığı, timeout, snapshot hash mismatch ve symlink reddi — hepsi fake handler ile, ağ erişimi olmadan.
- Offline replay determinizmi güçlü mühürlenmiş: generator iki kez çalıştırılıp byte-karşılaştırılıyor, golden normalized SHA-256 + exact row count (TCMB 7.534 / BIST 1.096) + gün başına tek satır kapsama + anchor semantikleri pinleniyor, `EnsureInputsUnchanged` ile koordineli manifest/expected değişimi reddediliyor.
- Fixture readiness probe'u fail-closed: 016/017/020/022 şeması, `verify_market_calendar_release_payload`, mühürlenmemiş release yokluğu, fence fonksiyonları ve trigger modları tek bir sorguda doğrulanıp eksikse suite hiç çalışmadan patlıyor; `IngestionTestTargetGuard` da bağlantı açılmadan önce saf fonksiyon olarak dört negatif senaryoyla test ediliyor.

**Repo dışı bilgi gerektiren açık sorular.**

- Bu commit'te required `integration-test` job'ı fiilen yeşil koştu mu? Gerçek run log'u, lane-01'deki `count(*)=23` çelişkisinin CI'da fail ettiğini (veya suite'in hiç çalışmadığını) kesinleştirir.
- Üretim kümesinde managed ingestion login'i `SET session_replication_role` yapabiliyor mu (superuser üyeliği veya `GRANT SET ON PARAMETER`), ve `price_points` tablo sahibi kim? Bu bilgi olmadan `tgenabled='O'` price fence'inin operasyonel olarak kapalı olup olmadığı repo içinden kanıtlanamıyor.
- Bootstrap TCMB takvim kapsaması 2026-08-17'de bitiyor; canlı acquisition/promotion hattı o tarihten sonra çalıştırıldı mı ve `saydin.market_calendar.coverage.horizon.days` metriği üretimde ne okuyor? Aksi halde contract-v2 tcmb pencereleri üretimde sürekli `CalendarNotReady` üretir.
- Üretim veritabanındaki bootstrap release GUID'leri (`ca100000-0000-7000-8000-000000000002`) ve `cal-001-2026-08-17` snapshot set kimliği CI'daki ile birebir aynı mı? `ContractV2_PointerChange…` ve `TcmbTestOnlyCoveragePlusOne…` testleri aktif pointer'ı mutasyona uğratıp `finally` ile geri alıyor; bu testlerin yanlışlıkla paylaşılan bir hedefe yönelmesi durumunda etkisi repo dışı bilgiyle değerlendirilebilir.
- Twelve Data ve CoinGecko'nun gerçek canlı payload'ları test fixture'larındaki meta/identity alan kümesiyle (symbol, interval, mic_code, exchange_timezone) hâlâ birebir örtüşüyor mu? Contract testleri pinlenmiş örneklere dayanıyor; sağlayıcı şema değişikliği yalnız üretimde görünür.

---

## L18c — DatabaseMigrator + RoleBootstrap test kalitesi

**Doğrulanmış bulgu:** 0 Critical · 0 High · 3 Medium · 7 Low

**Kapsam.** Hattın tam dosya listesi okundu: `tests/Saydin.DatabaseMigrator.Tests/**` (2836 satırlık MigrationRunnerIntegrationTests dahil tamamı taranıp kritik blokları satır satır), `tests/Saydin.DatabaseRoleBootstrap.Tests/**` ve `tests/Saydin.DatabaseRoleBootstrap.IntegrationTests/**`. Karşı taraf olarak `src/Saydin.DatabaseMigrator/MigrationRunner.cs` + `MigrationImpactManifest.cs`, `src/Saydin.DatabaseSecurity/{SecureSecretFile,LinuxSecretFile}.cs`, `Saydin.Services.sln`, `docker-compose.yml` tests profili, `.github/workflows/ci.yml`, `.github/compose.integration.yml`, `.github/scripts/{run-migrator-tests.sh,run-role-bootstrap-tests.sh,run-unit-coverage.sh,verify-integration-trx.py}` ve `docs/analysis/06-remediation-progress.md` kabul iddiaları okundu. Testler çalıştırılmadı (lokal .NET yok, salt-okunur review); süre/flakiness gibi çalışma-zamanı iddiaları doğrulanmadı.

**Güçlü kararlar.**

- Mock yasağı gerçekten uygulanmış: her iki suite de gerçek PostgreSQL/TimescaleDB kullanıyor, test projelerinde NSubstitute yok ve env eksikse `Skip` yerine exception atılıyor (`IntegrationEnvironment.cs:21`, `RoleBootstrapPgHarness.cs:69`) — CI tarafında `verify-integration-trx.py` `notExecuted/skipped/failed/inconclusive` sayaçlarının hepsini sıfıra zorlayarak bu fail-closed niyeti mühürlüyor.
- `PostgresCommitAckDropProxy` iddia edilen yarışı gerçekten üretiyor: frontend COMMIT backend'e iletiliyor, backend `CommandComplete("COMMIT")` yayınladıktan sonra hem client hem backend soketi kapatılıyor ve ACK asla iletilmiyor. Test bunu üç yönden mühürlüyor — child process exit 3, `proxy.DroppedCommitAcknowledgement == true`, ve `schema_migrations.state='succeeded'` (sunucu commit etmişti). Proxy'nin TLS terminatörü olmadığı bilinerek `PGSSLMODE=Disable` ve `SslMode.Disable` açıkça sabitlenmiş; bu, Npgsql'in `Prefer` varsayılanının SSLRequest paketini proxy'ye sokmasını engelleyen doğru ve yorumlanmış bir karar.
- Fresh-init zinciri gerçekten uçtan uca koşuyor: `BlankDatabase_AppliesTwentyFourVersionsAndCreatesTwoHypertables` 24 migration + 24 checksum + 24 terminal state + 2 hypertable + `control.state='ready'` doğruluyor, ardından managed ingestion login'i ile gerçek bir price_points yazıp chunk üretiyor, propagated fence trigger'ını, chunk SELECT eşdeğerliğini, window'suz direct INSERT/UPDATE'in `42501` ile reddini ve foreign-role deny'ını gerçek veriyle kanıtlıyor.
- Trust-root checksum testleri anlamlı: `Load_OneRawByteChanges_ChangesChecksum` (LF→CRLF), `ValidateHistoricalPrefix_OneChangedHistoricalByte_RejectsPinnedTrustRoot`, `..._ChangedAdditiveByte_RejectsPinnedMigration` (7 dosya için ayrı vaka), `..._OmittedCanonicalMigration_Rejects` (7 vaka) ve `..._UnknownTailMigration` birlikte hem mutasyon hem eksiltme hem ekleme yönünü kapatıyor; ayrıca `RoleContractTests.Contract_hash_binds_versions_roles_and_membership_topology` altın SHA-256 pini ile sözleşmeyi mühürlüyor.
- Test izolasyonu ciddiye alınmış: her test kendi `saydin_migrator_<32hex>` / `saydin_role_<10hex>` veritabanını ve türetilmiş rol prefix'ini alıyor; `ValidateName` guard'ı DROP öncesi ad şeklini doğruluyor, tüm DROP DATABASE/DROP ROLE ifadeleri `pg_catalog.format('%I', $1)` ile parametreli üretiliyor, secret dizini 0700 ve cleanup eksik kalırsa `AggregateException` ile test kırılıyor (sessiz sızıntı yok).
- Kilit/yarış testleri gerçek yarışı üretip 'hâlâ bloke' yönünde mühürleniyor: `CalendarSealAndPayloadDml_SerializeAcrossCommitOrders` ve `PrincipalRetention_ConcurrentActivityInsertAndPrincipalDelete_SerializesFailClosed` iki ayrı bağlantı/transaction ile `IsCompleted.Should().BeFalse()` + commit sonrası beklenen SQLSTATE (`55000`/FK ihlali) doğruluyor; `ConcurrentRunners_ExecutePendingBodyExactlyOnce` iki eşzamanlı runner'da tek gövde çalıştığını (`Applied` kümesi {0,25}) kanıtlıyor; `TransactionSessionKill` gerçek `pg_terminate_backend` kullanıyor.
- Sır sızıntısı her koşuda otomatik denetleniyor: `RoleBootstrapPgHarness.AssertRedacted` her ensure/rotate/verify çıktısında admin ve tüm login parolalarını arar, `RotateBackupV2Async` aynı kontrolü tekrarlar, migrator tarafında `AppliedChecksumMismatch_IsRejected` bağlantı parolasını sentinel olarak stdout+stderr'de arar.

**Repo dışı bilgi gerektiren açık sorular.**

- Migrator suite'inin gerçek CI süresi ve `SchedulerBridgeIsConsumed_...` testindeki 30 saniyelik BGW compression polling'inin yüklü GitHub runner'larında flaky olup olmadığı ancak canlı CI koşu geçmişiyle görülebilir (repoda koşu logu yok).
- `--minimum-executed 124` (migrator), `76` (role-bootstrap unit) ve `7` (role-bootstrap real-PG) eşiklerinin bugünkü gerçek vaka sayılarıyla tam örtüşüp örtüşmediği — yani ratchet'in gerçekten sıkı mı yoksa halihazırda gevşek mi olduğu — ancak suite'lerin gerçek bir koşusuyla doğrulanabilir; lokal ortamda .NET 10 SDK olmadığı için sayım yapılamadı.
- Production impact-manifest imzalama anahtarının nerede tutulduğu, `SAYDIN_MIGRATION_IMPACT_PUBLIC_KEY_SHA256` pininin deploy'a nasıl bağlandığı ve rotasyon prosedürü repo dışıdır; lane-03'teki test boşluğunun operasyonel etkisi bu bilgiye bağlıdır.
- `Integration tests (TimescaleDB + Redis)` job'ının GitHub branch protection'da gerçekten required seçili olup olmadığı repo içinden görülemez; seçili değilse bu hattaki tüm gerçek-PG kapıları (migrator 124, role-bootstrap 76+7) pratikte isteğe bağlıdır.

---

## L18e — DataQualityAudit + DataRepair test kalitesi

**Doğrulanmış bulgu:** 0 Critical · 1 High · 2 Medium · 9 Low

**Kapsam.** L18e dosya listesindeki 31 dosyanın tamamı okundu (DQA unit 8 test dosyası + TestFiles, DQA integration 4 dosya dahil 1982 satırlık AuditDatabaseFixture, DataRepair unit 3 dosya, DataRepair integration 4 dosya + run-isolated.sh, csproj/lock dosyaları). İddiaları doğrulamak için hat dışı karşı taraflar da okundu: src/Saydin.DataQualityAudit (AuditRunner, EvidenceSigning, SignedAuditInput, ApiTrust/PrincipalRetention SQL), src/Saydin.DataRepair (Program, RepairDatabase, RepairTrustLease, RepairExecutor, ReceiptStore, README), infrastructure/postgres/migrations/016+020, .github/workflows/ci.yml, .github/scripts/run-unit-coverage.sh ve run-data-repair-tests.sh, docs/analysis/06-remediation-progress.md Paket 6-7. Testler çalıştırılmadı (lokal .NET yok, salt-okunur review); bulgular kod/SQL/script okumasına ve kod-kapsama karşılaştırmasına dayanıyor.

**Güçlü kararlar.**

- Gerçek altyapı zorunluluğu sahte değil: `IntegrationEnvironment.Require()` ve `RepairIntegrationEnvironment.Require()` eksik/yanlış ortamda SkippableFact yerine exception atıyor, ayrıca beklenen host/veritabanı adı UUID kalıbına ve 'prod'/'staging' içermeme kuralına karşı doğrulanıyor — suite sessizce skip'e düşemiyor.
- Audit'in false-negative üretmediği geniş ölçüde kasıtlı bozuk veri enjeksiyonuyla kanıtlanmış: DQ-001/002/003/006/007/009 için 60'tan fazla drift senaryosu (fonksiyon gövdesi, overload, ACL, trigger disable, constraint kind/relation/FK action, katalog hash'i, forged attribution, orphan payload, overlapping window) try/finally ile geri alınıp ardından `RunAuditAsync` yeniden 'Clean' beklenerek mühürleniyor — bu desen fixture'ın kendi geri yüklemesini de doğruluyor.
- Gizli veri sızıntısına karşı çok noktalı canary testleri: ham `source_raw` içindeki api_key/access_token/client-secret/credential, `installation_credentials.raw_secret`, provider evidence içindeki Authorization başlığı ve ham business key (`AssetId`) kanıt paketinin hiçbir dosyasında (imza dışı) görünmemek üzere assert ediliyor; `HmacBusinessKey` testi de HMAC'in girdiyi sızdırmadığını doğruluyor.
- Evidence bundle negatif matrisi olağanüstü kapsamlı: geçerli imzalı ama bilinmeyen/null üye içeren manifest, bildirilen boyutun hard cap'i aşması, symlink ve ata-dizin symlink'i, atomik publish anında final yolunun symlink ile takas edilmesi, envanter cap+1, cancellation ve her hata yolunda staging dizininin sızmadığının doğrulanması.
- DataRepair'in mutasyon yolu gerçek PG üzerinde tam CAS semantiğiyle test ediliyor: dry-run→apply→idempotent→plan-binding çakışması→rollback→idempotent zinciri, tam satır jsonb preimage/postimage SHA eşitliğiyle; commit-ACK kaybı (`CommitThenThrow`) için 'reconciled' yolu ve rollback'te sonradan değişen postimage'ın reddi ayrıca kanıtlanıyor.
- Audit rolünün gerçekten salt-okunur olduğu doğrudan PostgreSQL'e sorularak kanıtlanıyor: 23/18/0 tablo ayrıcalık özeti, users/activity_logs/saved_scenarios/installation_credentials üzerinde SELECT'in reddi, pg_monitor üyeliği olmadan pg_control_system() erişimi, INSERT/TRUNCATE/DISABLE TRIGGER/session_replication_role denemelerinin 42501 ile reddi ve daha önce atlanmış her tabloya yazma yetkisi verildiğinde preflight'ın fail-closed olması.
- CI ratchet'leri gerçek test sayılarıyla birebir örtüşüyor (DQA unit 84, DQA integration 72, DataRepair unit 15, DataRepair integration 7) ve runner'lar TRX yokluğu, passed<total, Cobertura kardinalitesi gibi durumlarda non-zero dönüyor — sayı düşüşü sessizce geçemiyor.
- Sır ve dosya hijyeni testlerle mühürlenmiş: receipt/plan/anahtar dosyaları 0600, dizinler 0700 olarak yazılıyor ve `File.GetUnixFileMode` ile assert ediliyor; hata çıktısının yol veya approval token sızdırmadığı `error.ToString().Should().Be("repair rejected: code=plan_signature_invalid\n")` gibi exact eşitliklerle doğrulanıyor.

**Repo dışı bilgi gerektiren açık sorular.**

- Gerçek OCI KMS'in hata yüzeyi (403/404/429/throttling, devre dışı anahtar, anahtar sürümü rotasyonu) `OciSdkKmsSigningClient` üzerinden hangi exception tiplerine dönüşüyor? Repo içinden doğrulanamıyor; `catch (Exception) => kms_sign_denied` catch-all'unun production'da teşhis edilebilir olup olmadığı ancak canlı bir KMS denemesiyle bilinebilir.
- Production TimescaleDB kümesinde `activity_logs` chunk/compression düzeni, fixture'ın `compressed_chunk_acl` drift senaryosundaki `_timescaledb_catalog.chunk` join varsayımlarıyla aynı mı? Farklı compression policy veya chunk boyutu bu drift testinin production'daki karşılığını geçersiz kılabilir.
- Production audit ve repair login'lerinin gerçek ACL/rol üyelikleri, `saydin_role_contract` tablosundaki sözleşmeyle birebir eşleşiyor mu? Fixture'lar sözleşmeyi disposable DB'de kendisi kuruyor; production'daki fiili grant seti repo dışı bilgi.
- Gerçek production ingestion ledger'ında `requeue_permanent_window` dışında hangi repair operasyon türlerinin (refetch, manual_review) fiilen kullanılacağı ve bunların DB yan etkileri neler? Mevcut testler yalnız requeue + manual_review (yalnızca work-order sayımı) senaryosunu içeriyor.

---
