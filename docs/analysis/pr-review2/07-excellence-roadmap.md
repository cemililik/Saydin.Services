# Birinci Sınıfa Giden Yol Haritası

> Bu review'in asıl katma değeri. 191 `excellence-gap` kaydı: **yanlış değil ama birinci
> sınıf da değil**. Kod çalışıyor, testler geçiyor — ama bir sonraki okuyucu, bir sonraki
> operatör veya bir istemci geliştiricisi burada sürtünme yaşayacak.
>
> Bu kayıtlar defect değildir; yayın kararını etkilemezler. Öncelik sırası **boyut** bazlıdır:
> aynı boyuttaki birden fazla kaydı tek bir çalışmada kapatmak en verimli yoldur.

## Boyut dağılımı

| Boyut | Kayıt | Ağırlık |
|---|---:|---|
| Test kalitesi | 48 | ████████████████ |
| İşletilebilirlik | 45 | ███████████████ |
| Geliştirici deneyimi | 25 | ████████ |
| Ürün deneyimi | 15 | █████ |
| Sadelik ve tekrar | 13 | ████ |
| Dokümantasyon | 11 | ████ |
| Güvenlik derinliği | 9 | ███ |
| Performans | 9 | ███ |
| Mimari kural | 7 | ██ |
| Veri bütünlüğü | 5 | ██ |
| Doğruluk | 3 | █ |
| Finansal | 1 | █ |

---

## Test kalitesi (48 kayıt)

### [Medium] Kanal yaşam döngüsünün tüm garantisi test edilmeyen bir DI kayıt sırası varsayımına dayanıyor

`src/Saydin.Api/Program.cs:301-305; src/Saydin.Api/BackgroundServices/ActivityLogChannelLifetime.cs:6-12; tests/Saydin.Api.Tests/BackgroundServices/ActivityLogWriterTests.cs:125-141` · R03 · CONFIRMED

ActivityLogChannelLifetime'ın sağladığı 'ingress önce kapanır, writer sonra durur' garantisi yalnızca DI kayıt sırası + Host'un ters durdurma semantiği + Kestrel'in en sonda kaydedilmesi varsayımlarına dayanıyor ve hiçbir test bu üç varsayımdan hiçbirini doğrulamıyor. Var olan test bileşenleri elle doğru sırayla sürüyor, yani varsayımı test etmek yerine yeniden üretiyor.

**Neden birinci sınıf değil.** Program.cs'de AddHostedService<ActivityLogChannelLifetime>() çağrısından sonra yeni bir hosted service eklenirse veya .NET sürümü sırayı değiştirirse drain garantisi sessizce bozulur: writer, ingress kapanmadan durdurulur ve kapanışta üretilen satırlar (tam da bu değişikliğin çözdüğü sorun) yeniden kaybolmaya başlar; hiçbir test kırılmaz.

**Nasıl kapanır.** WebApplication'ı ayağa kaldırıp `services.GetServices<IHostedService>()` sırasında ActivityLogWriter'ın ActivityLogChannelLifetime'dan önce ve GenericWebHostService'in en sonda olduğunu doğrulayan bir sözleşme testi ekle; Program.cs'e yorum düşmek yerine kapı kur.

### [Medium] Yeni davranışların kritik negatif senaryoları test edilmiyor; "idempotent" testi günlük tekrarı doğruluyormuş izlenimi veriyor

`tools/calendar-data/tests/Saydin.CalendarData.Tests/CalendarPlanMaterializerTests.cs:8-32; tools/calendar-data/tests/Saydin.CalendarData.Tests/CalendarCoverageEvidenceTests.cs:8-40` · R09 · DOĞRULANMADI (yalnız üreten agent)

`TcmbPlan_IsDeterministicIdempotentAndUsesOfficialPublicationCutoff` materializer'ı AYNI `beforePublication` timestamp'iyle iki kez çağırıp bayt eşitliği assert ediyor; farklı gün senaryosu (`materialized_plan_conflict`) hiç çalıştırılmıyor — R09-01 bu boşluk yüzünden kaçtı. `CalendarCoverageEvidenceTests` yalnız hafta içi (`2026-08-19`) durumunu kapsıyor; hafta sonu muafiyeti (R09-02) test edilmiyor. `VerifyCandidateBehaviorTests`'te `docker` stub'ı `case " $* " in *' --network none '*) exit 0` — testin adı "OfflineReplay" iddia etse de `--read-only`, `--user`, `--memory`, mount readonly gibi hiçbir hardening bayrağı doğrulanmıyor.

**Neden birinci sınıf değil.** CI, bu hattın iki High'ını da yakalayamaz. Test isimleri kapsamı olduğundan geniş gösteriyor (tautoloji riski), bu da ikinci bir okuyucuyu yanıltır.

**Nasıl kapanır.** (1) Farklı `utcNow` ile ikinci materialize çağrısı için test ekle ve beklenen davranışı (rotasyon/atomik replace) sabitle. (2) `coverageThrough` hafta sonu + aradaki hafta içi günlerin yayımlanmamış olduğu bir fixture ile fail-closed testi ekle. (3) `docker` stub'ını `--read-only`, `--user`, `--network none`, `dst=/candidate,readonly` bayraklarının hepsini isteyecek şekilde sıkılaştır veya testi `EnforcesSignatureHashesAndInventory` olarak yeniden adlandır.

### [Medium] `recoveryTargetReached` receipt alanı sabit `True` yazılıyor ve üretim promotion kapısı bu sabiti kanıt sayıyor; statik test de sabiti şart koşuyor

`infrastructure/backup/restore-drill.sh:357-363 (`"recoveryTargetReached":True`); .github/workflows/promote-production.yml:109 (`assert receipt["recoveryTargetReached"] is True`); infrastructure/backup/tests/backup-static-self-test.py:137-146,272-273` · R10 · DOĞRULANMADI (yalnız üreten agent)

Drill, receipt'e `"recoveryTargetReached":True` sabitini gömüyor; hiçbir ölçümden türetilmiyor. Promotion kapısı `assert receipt["recoveryTargetReached"] is True` diyor — yani sabiti doğruluyor. Statik self-test `restore_recovery_contract` içinde literal `'"recoveryTargetReached":True'` metninin varlığını zorunlu kılıyor (satır 140,145), mutasyon testi ise yalnız `SELECT NOT pg_is_in_recovery();` ifadesinin `SELECT true;` ile değiştirilmesini yakalıyor. Gerçek kanıt yalnız dolaylı: `recovery_target_action=promote` altında hedefe ulaşılamazsa PostgreSQL FATAL verir. Buna ek olarak `lastReplayedTransactionAt` boş olabiliyor (restore-drill.sh:280 `""` kabul ediliyor) ve promotion `transaction is None` durumunu sınırsız kabul ediyor (satır 110-114), yani "restore edilen küme gerçekten hedefe kadar replay etti" için hiçbir sayısal kanıt yok.

**Neden birinci sınıf değil.** İmzalı DR kanıtının başlık alanı ölçüm değil sabit; kapı yeşil ama bilgi taşımıyor. Drill hiçbir veri düzeyi değişmezi (ör. `price_points` içinde hedef zamandan önceki bir satırın varlığı, `max(price_date)`) doğrulamadığı için "şema/kimlik doğru ama içerik boş/eksik" bir restore hâlâ geçebilir.

**Nasıl kapanır.** Alanı ölçümden türet: PostgreSQL log'undaki `recovery stopping before/after ...` satırını veya `pg_last_xact_replay_timestamp()`/`pg_last_wal_replay_lsn()` ile `targetTime` ilişkisini receipt'e yaz ve promotion'da `lastReplayedTransactionAt <= targetTime` + üst sınır kontrolü yap (boş olması yalnız gerçekten sessiz bir hedef için gerekçelendirilsin, ayrı bir `quietTarget: true` bayrağıyla). Ek olarak drill'e küçük bir veri değişmezi sorgusu ekle (ör. `SELECT count(*)>0 FROM price_points WHERE price_date <= <target>::date`) ve sonucunu receipt'e/kapıya bağla.

### [Medium] Docker yokken üç davranış smoke'u sessizce "pass" sayılıyor; statik self-test hem skip'i hem tutarsız skip kodlarını gizliyor

`infrastructure/backup/tests/backup-static-self-test.py:296-308,365-385; infrastructure/backup/tests/restic-wal-observation-smoke.py:25-29; infrastructure/backup/tests/archive-timeout-receiver-smoke.py:58-61; infrastructure/backup/tests/base-backup-behavior-smoke.py:127-130; infrastructure/backup/tests/restore-volume-init-smoke.py:44-46` · R10 · DOĞRULANMADI (yalnız üreten agent)

`restic_wal_observation_behavior`, `restore_cleanup_behavior` ve `archive_timeout_receiver_behavior` koşulsuz çağrılıyor ve yalnız `returncode == 0` bakılıyor. Bu smoke'ların ikisi docker yoksa `print("..._skipped:docker_unavailable"); return 0` yapıyor — yani docker'sız bir ortamda üç davranış kapısı da vakumsal olarak geçiyor ve script yine `backup_static_self_test_passed:N` basıyor. Skip sözleşmesi tutarsız: `restore-volume-init-smoke.py` skip'te 77, diğer üçü 0 döndürüyor; 77 zaten hiç kullanılmıyor çünkü o smoke yalnız docker varken çağrılıyor. CLAUDE.md ise integration kabul için açıkça "zero-skip gate, total=executed=passed" istiyor.

**Neden birinci sınıf değil.** "Backup statik + davranış kapısı geçti" çıktısı, gerçek PostgreSQL/restic davranışının hiç doğrulanmadığı bir koşuda da üretilir; bu, tam olarak bu review'in aradığı "yeşil ama anlamsız" sinyaldir. Ayrıca gerçek bir CI hatasında (bkz. R10-01) hangi smoke'un skip'lendiği çıktıdan anlaşılamıyor.

**Nasıl kapanır.** Tek bir skip sözleşmesi belirle (ör. 77) ve `backup-static-self-test.py`'de skip'i açıkça ele al: docker beklenen ortamda (CI) skip'i HATA say (`SAYDIN_REQUIRE_DOCKER_SMOKES=1` env ile), yerelde açıkça "skipped" olarak listele ve final satırında `passed=N skipped=M` yayınla. `.github/workflows/ci.yml`'de `SAYDIN_REQUIRE_DOCKER_SMOKES=1` ver.

### [Medium] Yeni high-water probe'unun gerçek PostgreSQL/HBA yolu (replication modunda psql) hiçbir kapıda koşmuyor; tüm off-host WAL akışı bu yola bağlı

`infrastructure/backup/backup-entrypoint.sh:587-601; .github/scripts/run-backup-auth-tests.sh:41-74; infrastructure/backup/tests/base-backup-behavior-smoke.py:385-392 (sahte psql); infrastructure/backup/manage_backup_hba.py:74-83` · R10 · DOĞRULANMADI (yalnız üreten agent)

WAL yüklemesi artık `psql --dbname="host=... user=... dbname=postgres replication=true" -c IDENTIFY_SYSTEM` ve `-c 'SHOW wal_segment_size'` başarısına koşulsuz bağlı (başarısızlıkta `continue` → hiç yükleme yok). `run-backup-auth-tests.sh` — "Required physical-protocol acceptance" gate'i — yalnız `pg_receivewal` ve `pg_basebackup`'ı gerçek sunucuya karşı koşuyor; replication modunda psql'i hiç denemiyor. `base-backup-behavior-smoke.py`'deki psql sahte bir shell script (satır 385-392) ve `saydin-wal-highwater` de sabit çıktı veren sahte bir script (satır 394-396). `manage_backup_hba.py:80-82` kuralları (`hostssl replication <role> <cidr> scram-sha-256` ardından `host all <role> ... reject`) okundu — teoride physical walsender `replication` anahtar sözcüğüyle eşleşiyor, ama bu hiçbir yerde gerçek sunucuya karşı doğrulanmıyor.

**Neden birinci sınıf değil.** Yükleme zinciri sessizce durur: her döngü `backup_wal_highwater_probe_unavailable` yazıp `continue` eder, `write_failure_metric` hiç çalışmaz, `SaydinBackupFailure` tetiklenmez; tek sinyal 45 dakika sonra gelen `SaydinWalBackupStale`'dir. Deploy kapısı bunu önceden yakalayamaz çünkü kabul testi bu yolu hiç koşmuyor.

**Nasıl kapanır.** `run-backup-auth-tests.sh`'ye üçüncü bir pozitif adım ekle: aynı credential ile `psql --dbname="host=$PGHOST port=$PGPORT user=$PGUSER dbname=postgres replication=true" -c IDENTIFY_SYSTEM` ve `SHOW wal_segment_size` çalıştır, çıktıyı `saydin-wal-highwater` ile gerçek segment adına çevirip spool'daki gerçek segmentle karşılaştır. Bunu `deploy-release.sh`'in `verify-auth` adımına da bağla (`verify_auth` şu an yalnız pg_receivewal + SQL-deny doğruluyor).

### [Medium] `Saydin.PriceIngestion.IntegrationTests` coverage üretmiyor; `coverage-admission`'ın kardinalite kontrolü tesadüfen tutuyor

`.github/workflows/ci.yml:894-899; .github/scripts/run-ingestion-ledger-tests.sh:44-49; karşı taraf: .github/scripts/run-unit-coverage.sh:45-56` · R12 · CONFIRMED

İddia birebir doğru; ek kanıt olarak `run-unit-coverage.sh` unit tarafında tam olarak önerilen envanter-eşitliği kontrolünü zaten yapıyor, yani integration tarafındaki sayı-kontrolü bilinçli bir tercih değil, eksik uygulama.

**Neden birinci sınıf değil.** Ingestion ledger yollarının (lease fence, next_attempt_at, permanent-window) integration kapsamı birleşik changed-line coverage kapısına hiç girmiyor; `Saydin.PriceIngestion.Adapters` gibi kritik namespace eşikleri olduğundan düşük hesaplanabilir. Kardinalite kontrolü bu boşluğu maskeliyor.

**Nasıl kapanır.** `run-ingestion-ledger-tests.sh`'e migrator runner'daki gibi `--settings .github/scripts/coverage.settings.xml --collect "XPlat Code Coverage"` + tek-rapor kardinalite kontrolü + `mv ... ingestion-ledger-integration.coverage.cobertura.xml` ekle. `coverage-admission`'daki sayı kontrolünü beklenen dosya adları kümesiyle eşitlik kontrolüne çevir (unit taraftaki `diff -u` deseni gibi).

### [Medium] Management-port HTTP testinin yeniden yazımı ApiRuntimeContract.Configure(KestrelServerOptions) kapsamını tamamen düşürdü

`tests/Saydin.Api.Tests/Middleware/ApiManagementBoundaryHttpTests.cs:33-38 (git diff: `-builder.WebHost.ConfigureKestrel(runtime.Configure)`); src/Saydin.Api/Runtime/ApiRuntimeContract.cs:70-74` · R13 · CONFIRMED

Doğru; ancak bu bir 'yanlış davranış' değil, bu commit'in getirdiği bir kapsam kaybıdır: `Configure(KestrelServerOptions)` (public+management ListenAnyIP) artık hiçbir birim/entegrasyon testi tarafından çağrılmıyor. Regresyon tamamen sessiz kalmaz (Docker health check / Prometheus scrape deploy'da patlar), ama L01 port-izolasyon remediation'ının en dış katmanı CI kapısında kanıtsız.

**Neden birinci sınıf değil.** ListenAnyIP→ListenLocalhost veya yanlış port gibi bir değişiklik tüm test paketini yeşil bırakır; hata ancak deploy sonrası (management scrape ölümü ya da management yüzeyinin yanlış arayüze açılması) görülür.

**Nasıl kapanır.** ApiRuntimeContractTests'e `KestrelServerOptions` overload'ı için bir Fact ekle: `options.CodeBackedListenOptions` üzerinden iki endpoint'in IPAddress.Any + doğru portlar olduğunu doğrula. Bu, mevcut HTTP testinin ListenHandle kurgusunu bozmadan kapsamı geri getirir.

### [Medium] Yeni SecurityAdmissionTelemetry ve saydin.security admission sayacı için hiç test yok — üstelik istek yolunda fırlatan bir doğrulama içeriyor

`src/Saydin.Api/Security/SecurityAdmissionTelemetry.cs:17-49; src/Saydin.Shared/Diagnostics/SaydinMetrics.cs:176-179 (test yok)` · R13 · CONFIRMED

İddia doğru. Mevcut 5 enum değeri dolaylı olarak DistributedSecurityLimiterTests ve InstallationAuthenticationFilterTests akışlarında uyandırılıyor; eksik olan (a) metrik adı/etiket şeması, (b) enum genişlemesine karşı fail-fast davranışı, (c) allowlist dışı değerlerin reddi için tek bir assertion bulunmamasıdır.

**Neden birinci sınıf değil.** İki runbook'un teşhis adımı doğrulanmamış bir metrik sözleşmesine dayanıyor; SecurityLimiterReason/Outcome genişlemesi korumalı endpoint'lerde toplu 500'e dönüşebilir ve CI bunu yakalamaz.

**Nasıl kapanır.** `[Collection(MetricsTestCollection.Name)]` altında MeterListener ile (a) saydin.security.admission.decisions.total etiketlerini, (b) Enum.GetValues üzerinden theory ile Record'un hiçbir enum değerinde fırlatmadığını, (c) allowlist dışı değerlerin ArgumentOutOfRangeException verdiğini doğrulayan bir suite ekle.

### [Medium] Bu commit'te eklenen lokalizasyon anahtarları resx key-varlığı regresyon kilidine eklenmedi

`tests/Saydin.Api.Tests/Localization/ErrorMessagesLocalizationTests.cs:31-71` · R13 · CONFIRMED

Doğru; sayılar biraz farklı: 50 kullanılan anahtardan 35'i listede, 23'ü değil (finder 49/35/14 demiş). InstallationCredentialInvalid dolaylı olarak `GetString_WithDifferentCultures...` Fact'i tarafından korunuyor, Detail varyantı korunmuyor.

**Neden birinci sınıf değil.** Bir anahtar resx'ten silinir veya .en.resx'e eklenmezse IStringLocalizer ham anahtarı döndürür ve RFC 7807 gövdesinde kullanıcıya 'SecurityRateLimitedDetail' gibi bir string gider; tüm test paketi yeşil kalır.

**Nasıl kapanır.** InlineData listesini elle bakımlı olmaktan çıkar: merkezi bir ErrorMessageKeys sabit sınıfı tanımlayıp hem üretim kodu hem test ondan beslensin. Kısa vadede 23 eksik anahtarı ekle ve QuotaUnavailableExceptionHandlerTests'e title'ın ham anahtar olmadığı assertion'ını koy.

### [Medium] 'Her endpoint bir yüzey bildirir' invariant'ı ve port==0 kaçış kapısı test edilmiyor; selector policy metadata'sız endpoint'lerde fail-open

`src/Saydin.Api/Runtime/ApiEndpointSurface.cs:31-40,44-53; src/Saydin.Api/Program.cs:354-378; tests/Saydin.Api.Tests/Middleware/ApiPortBoundaryMiddlewareTests.cs:17-33` · R13 · CONFIRMED

Büyük ölçüde doğru, tetikleme düzeltilmeli: middleware Classify (ApiPortBoundaryMiddleware.cs:50-56) bilinmeyen path'i management portunda zaten Rejected yapıyor; metadata unutulan endpoint public portta servis edilir, dolayısıyla asıl risk 'management niyetli' bir endpoint'in grup dışında map edilip public yüzeye düşmesidir. port==0 kaçış kapısının tamamen kaldırılması tüm WebApplicationFactory testlerini kırar (dolaylı koruma); test edilmeyen yön yalnızca `!environment.IsProduction()` koşulunun düşürülmesidir ve gerçek Kestrel'de LocalPort 0 olmaz.

**Neden birinci sınıf değil.** L01 remediation'ının çekirdek savunması (surface metadata) sözleşme testi olmadan duruyor; en olası regresyon yolu (metadata'sız yeni endpoint) kapıda yakalanmıyor.

**Nasıl kapanır.** Gerçek Program/MapXEndpoints grafiğini ayağa kaldırıp EndpointDataSource.Endpoints üzerinden 'her RouteEndpoint tam olarak bir ApiEndpointSurfaceMetadata taşır' Fact'i ekle; Matrix'e Production + port==0 satırlarını ve ayrı bir Development testinde kaçış kapısının bilinçli davranışını ekle.

### [Medium] ActivityLog yazıcı sınıflandırıcısının Postgres-dışı dalı (SocketException/IOException/TimeoutException/IsTransient) hiç test edilmiyor

`tests/Saydin.Api.Tests/BackgroundServices/ActivityLogWriterTests.cs:16-40; src/Saydin.Api/BackgroundServices/ActivityLogBatchStore.cs:74-81; src/Saydin.Api/BackgroundServices/ActivityLogWriter.cs:143-147` · R13 · CONFIRMED

İddia doğrulandı. Ek nüans: sınıflandırıcının varsayılanı FatalHost olduğu için bu dalın bozulması sessiz yanlış sınıflandırma değil doğrudan host sonlanması (crash-loop) demektir; integration tarafı yalnız pg_terminate_backend (57P01, PostgresException) senaryosunu kapsıyor, gerçek soket kopmasını değil.

**Neden birinci sınıf değil.** Ağ kopması / PG host erişilemezliği yolunda regresyon koruması yok; önceki High bulgu #6'nın hedeflediği 'geçici PG koşulu tüm host'u düşürüyor' senaryosu kanıtsız kalıyor.

**Nasıl kapanır.** Theory'yi `Exception -> beklenen kind` biçimine genişlet: DbUpdateException(IOException), DbUpdateException(SocketException), DbUpdateException(TimeoutException), InvalidOperationException (→ FatalHost) ve iç içe sarmalanmış bir vaka. FatalHost'un host'u düşürmesi bilinçliyse HostOptions'ı Saydin.Api'de de açıkça set edip testle belgele.

### [Medium] jsonb sayısal normalizasyon beklentileri gerçek PostgreSQL'e karşı doğrulanmıyor; parity integration testinin korpusu exponent vakalarını dışarıda bırakıyor

`tests/Saydin.Api.Tests/Services/ScenarioExtraDataValidatorTests.cs:101-117; tests/Saydin.Api.IntegrationTests/SavedScenarioRepositoryIntegrationTests.cs:22-40` · R13 · CONFIRMED

Doğru. Unit beklentileri PostgreSQL numeric semantiğine göre bugün doğru görünüyor; eksik olan, gerçek-DB oracle eldeyken en riskli dalın (exponent/ölçek normalizasyonu) o oracle'a karşı hiç koşulmamasıdır.

**Neden birinci sınıf değil.** Tahmin edici ile gerçek jsonb boyutu arasındaki bir sapma 8192 baytlık uygulama sınırını DB CHECK'inden (chk_saved_scenarios_extra_data_size) ayırır: ya erken red ya da 23514 → kullanıcıya beklenmedik hata.

**Nasıl kapanır.** Unit theory'nin InlineData korpusunu integration parity testine taşı (aynı dizi octet_length ile karşılaştırılsın), unit tarafı hızlı geri besleme için bırak; aynı yaklaşımı JsonbStorageSize.UpperBound için pg_column_size karşılaştırmasını exponent/derin nesne/uzun string ile genişleterek uygula.

### [Medium] SharedEfParityTests'in elle bakımlı prefix allowlist'i EF-modellenmiş üç tablonun 18 CHECK constraint'ini sessizce kapsam dışı bırakıyor

`tests/Saydin.Api.Tests/Data/SharedEfParityTests.cs:58-68` · R13 · CONFIRMED (doğrulayıcı ek bulgusu)

`ownedPrefixes` yalnız sekiz prefix içeriyor: chk_activity_, chk_asset_catalog_, chk_asset_market_, chk_inflation_rates_, chk_installation_credentials_, chk_market_calendar, chk_price_points_, chk_users_. Testin kendi regex'ini python ile emüle ettim: migration'larda 63 chk_ adı var, allowlist bunların yalnız 38'ini kapsıyor. Kapsam dışı kalan 25 addan 18'i EF tarafından FİİLEN modelleniyor — chk_saved_scenarios_type/unit/dates/type_unit/extra_data_object/extra_data_size (SavedScenarioConfiguration.cs:19-30), chk_ingestion_jobs_type/status (IngestionJobConfiguration.cs:16-20) ve chk_ingestion_windows_* (10 adet, IngestionWindowConfiguration.cs). Bunları allowlist'e eklemek testi kırmıyor: migration−EF farkı yalnız EF'de hiç modellenmeyen chk_price_attribution_*, chk_inflation_attribution_*, chk_provider_fetch_payloads_* (7 ad).

**Neden birinci sınıf değil.** 'EF modeli ile checked-in migration'lar parity' iddiası adının düşündürdüğünün ~%60'ı kadar yüzey kapsıyor; en önemlisi kullanıcı verisi tutan saved_scenarios (extra_data boyut/şekil CHECK'leri dahil) korumasız. Allowlist elle bakımlı olduğu için her yeni tablo sessizce dışarıda kalır.

**Nasıl kapanır.** Kapsamı tersine çevir: 'EF modelinde tablosu bulunan her constraint' olarak hesapla ve yalnız EF'de hiç modellenmeyen üç tabloyu (price_attribution, inflation_attribution, provider_fetch_payloads) gerekçeli istisna listesine al. Ayrıca Should().Contain(expected) yerine çift yönlü karşılaştırma kullan ki EF'de olup migration'ın son durumunda olmayan nesneler (R13-01'deki chk_activity_action drift'i) de yakalansın.

### [Medium] VerifyCandidateBehaviorTests gerçek script'i çalıştırıyor ama docker stub'ı offline replay'i tamamen atlıyor; sandbox bayrakları hiçbir testte doğrulanmıyor

`tools/calendar-data/tests/Saydin.CalendarData.Tests/VerifyCandidateBehaviorTests.cs:9-13, :57-58 ↔ infrastructure/calendar/verify-candidate.sh:31,35,66,74,81-97; tools/calendar-data/tests/Saydin.CalendarData.Tests/InfrastructureCalendarContractTests.cs:43-47` · R14a · CONFIRMED

Yeni davranışsal verifier testi imza/manifest-hash/envanter/owner kapılarını gerçekten koşturuyor (önceki review'in isteği bu yönde karşılanmış), ancak admission'ın ikinci güvencesi olan hardened offline replay davranışsal olarak test edilmiyor: stub docker `--network none` gördüğü an başarı döndüğü için sandbox bayraklarının kaybı, yanlış alt-komut veya replay divergence hiçbir testte kırmızıya dönmez. Ek olarak stub `jq` `select(...)` guard'larını yok saydığından envelope schema/snapshotSetId doğrulama semantiği de gerçekte koşmuyor (bkz. ek bulgu R14a-A3).

**Neden birinci sınıf değil.** Calendar candidate admission zincirinin en kritik kapısı (deterministik offline replay + sandbox hardening) regresyona açık; bir refactor `--read-only`/`--cap-drop ALL`/`--user`/`readonly` mount'u düşürse veya replay'i etkisizleştirse beş test case'i de contract testi de yeşil kalır.

**Nasıl kapanır.** Stub docker'ı `--read-only`, `--cap-drop ALL`, `--security-opt no-new-privileges`, `--user <uid>:<gid>`, `dst=/candidate,readonly` ve `verify --data-root /candidate` argümanlarını doğrulayıp eksikte non-zero dönen bir script'e çevir. En az bir case'te stub'ı non-zero döndürüp replay divergence'ının reddedildiğini kanıtla; `expected_output_hash_mismatch`, `snapshot_set_mismatch`, `candidate_contains_symlink` ve post-replay `manifest_changed` için InlineData ekle. İdeali: required Linux Docker gate'inde gerçek imajla uçtan uca en az bir replay case'i.

### [Medium] "Bir asset'in permanent window'u diğerlerini etkilemiyor" iddiasının worker-pass düzeyinde regresyon testi yok

`tests/Saydin.PriceIngestion.Tests/Workers/BaseAssetWorkerTests.cs:78-96, :288-338, :666-672 ↔ src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:88-100, :141-144, :202-203, :494-500` · R14a · CONFIRMED

Permanent-window izolasyonunun yalnız tek-asset, tek-chunk seviyesindeki "throw etmiyor" davranışı test edilmiştir. DrainAsync'in `PermanentBlocked` claim dalı unit testlerde hiç çalışmaz ve BackfillAsync'in permanent bir asset'ten sonra sibling asset'lere devam ettiğini doğrulayan bir test yoktur; "tek asset tüm hattı kilitlemiyor" iddiası davranışsal kanıta değil kod okumasına dayanmaktadır.

**Neden birinci sınıf değil.** Envanterdeki en yüksek etkili High'ın (tek asset tüm hattı kilitliyor + crash-loop) regresyon koruması yoktur; `case PermanentBlocked:` dalı yeniden throw'a dönse veya BackfillAsync döngüsü break'e çevrilse mevcut 20+ worker testinin hiçbiri kırmızıya dönmez.

**Nasıl kapanır.** RunAsync üzerinden iki asset'li bir test ekle: A-FIRST için ClaimNextAsync `PermanentBlocked`, Z-LAST için `Claimed`→`Complete`. Assert: (1) Z-LAST adapter çağrısı yapıldı, (2) RunAsync exception fırlatmadı ve döngüde kaldı, (3) scope kimlikli LogCritical bir kez yazıldı, (4) permanent + retryable sibling karışımında NextWakeAt sibling'in next_attempt_at'i oldu. IngestionOrchestratorTests'e "permanent window fatal değildir" case'ini ekle.

### [Medium] "Hiçbir şey olmadı" negatif assert'leri tek bir `await Task.Yield()` ile senkronize ediliyor; wait-loop'lar duvar saatine bağlı

`tests/Saydin.PriceIngestion.Tests/Workers/BaseAssetWorkerTests.cs:244-248, :329-331, :536-545; tests/Saydin.PriceIngestion.Tests/Workers/EvdsInflationWorkerTests.cs:157-160, :202-211; tests/Saydin.PriceIngestion.Tests/HttpResilienceExtensionsTests.cs:32-35, :131-140` · R14a · CONFIRMED

Yeni eklenen en değerli davranışların (mutlak provider deadline sonrası lease renewal'ın durması, ledger next_attempt_at uyanması, retry gecikmesinin alt sınırı) negatif assert'leri gözlemlenebilir bir sinyale değil tek bir `await Task.Yield()`'e bağlanmıştır; WaitUntilAsync ise duvar saatine dayalı 1 saniyelik bir spin bütçesi kullanır. Bu, zero-skip/zero-fail kapısı olan bir repoda hem regresyon kaçırma hem merge-bloker flake riski üretir.

**Neden birinci sınıf değil.** Deadline sonrası renewal'ı durdurmayan bir regresyon, devam thread-pool'a geç kuyruklandığı için fark edilmeyebilir; ters yönde yüklü CI runner'da 1 sn bütçesi aşılırsa required job TimeoutException ile düşer.

**Nasıl kapanır.** Negatif assert'leri gözlemlenebilir sinyale bağla: fake repository'ye TaskCompletionSource tabanlı "renewal çağrıldı" sinyali ekleyip `await Task.WhenAny(signal, Task.Delay(...))` ile bekle veya FakeTimeProvider ile deterministik bir TaskScheduler kullan. WaitUntilAsync bütçesini en az 30 sn'ye çıkar, `DateTime.UtcNow` yerine `Stopwatch` kullan ve spin yerine kısa TCS/`Task.Delay` beklemesine geç. HttpResilienceExtensionsTests'teki await'siz `CallCount`/`IsCompleted` assert'lerini bir sinyal beklemesinin arkasına al.

### [Medium] 3 dakikalık pipeline bütçesi yalnız sabit eşitliğiyle test ediliyor; total-timeout'un gerçekten kestiği davranışsal olarak kanıtlanmıyor

`tests/Saydin.PriceIngestion.Tests/HttpResilienceExtensionsTests.cs:106-113 ↔ tests/Saydin.PriceIngestion.Tests/Workers/BaseAssetWorkerTests.cs:250-286 ↔ src/Saydin.PriceIngestion/Extensions/HttpResilienceExtensions.cs:25, :41-48` · R14a · CONFIRMED

Pipeline'ın "tek 3 dakikalık sözleşme" güvencesi yalnız sabit karşılaştırmasıyla korunmaktadır; ne total-timeout stratejisinin askıda kalan bir isteği kestiği ne de HttpClient.Timeout'un sonsuza alınmasıyla birlikte doğru davrandığı davranışsal olarak test edilir. Worker'ın stall testi üretim pipeline'ını hiç kullanmaz.

**Neden birinci sınıf değil.** Header acquisition'da askıda kalan bir provider için pipeline seviyesindeki savunma katmanı sessizce kaybolabilir; geriye yalnız worker deadline'ı kalır ve bu regresyon hiçbir kapıda görünmez.

**Nasıl kapanır.** Build(...) yardımcısına yanıt dönmeyen (TCS ile askıda) bir handler seçeneği ekle; FakeTimeProvider ile 3 dk ilerletip `client.GetAsync(...)`'in TimeoutRejectedException/TaskCanceledException ile bittiğini ve handler.CallCount'un beklenen değerde olduğunu doğrula. StalledHttpResponseBody_... testinde HttpClient'ı elle kurmak yerine AddSaydinResilience ile kurulmuş named client kullan.

### [Medium] Mega-test deseni yeni migrator integration testinde tekrarlanıyor; 'concurrent' iddiası kanıtlanmıyor

`tests/Saydin.DatabaseMigrator.Tests/InstallationCredentialRehashMigrationIntegrationTests.cs:11-77; .github/workflows/ci.yml:810-812` · R14b · CONFIRMED

Finder'ın tespiti birebir doğru. Ek doğrulama: migrator TRX ratchet'i (184) projenin gerçek test sayısına birebir eşit, dolayısıyla altı sözleşmenin beşinin sessizce çalışmaması sayıyı hiç etkilemez. Ayrıca eşzamanlılık kurgusunda senkronizasyon primitifi yok; test adı ('IsConcurrent...') kanıtlanmamış bir özellik iddia ediyor.

**Neden birinci sınıf değil.** Ratchet ve TRX kapıları assertion kaybına karşı koruma sağlamıyor; ilk hata sonrası diğer beş sözleşmenin durumu CI çıktısından bilinemez. 'Eşzamanlılık kanıtlandı' izlenimi determinizmi olmayan bir kurguya dayanıyor — `resolve_installation_and_rehash` içinde gerçek bir yarış (eksik FOR UPDATE/CAS) olsa test yeşil kalır.

**Nasıl kapanır.** Altı senaryoyu ayrı `[SkippableFact]`/`[SkippableTheory]` vakalarına böl (collection fixture paylaşıldığı için maliyet artmaz) ve ci.yml minimum'unu buna göre yükselt. Eşzamanlılık için iki bağlantıyı `SemaphoreSlim`/`Barrier` ile aynı anda serbest bırak, ardından rehash'in tam olarak bir kez uygulandığını `hash_key_version` ve `xmin`/`updated_at` üzerinden assert et.

### [Medium] En ağır iki backup davranış smoke'u docker yoksa sessizce atlanıyor; geçiş satırı bunu bildirmiyor

`infrastructure/backup/tests/backup-static-self-test.py:365-389; .github/workflows/ci.yml:120; tests/Saydin.Api.IntegrationTests/Fixtures/IntegrationTestEnvironment.cs:15` · R18 · DOĞRULANMADI (yalnız üreten agent)

`if shutil.which("docker") is not None:` + `docker info` başarılıysa `restore-volume-init-smoke.py` (136 satır) ve `base-backup-behavior-smoke.py` (552 satır) çalıştırılıyor; docker yoksa `required` sözlüğüne bu iki anahtar hiç eklenmiyor ve script `backup_static_self_test_passed:{len(required)}` yazıp exit 0 dönüyor — atlandıklarına dair tek kelime yok, sadece sayı değişiyor. Buna karşılık repo'nun kendi idiomu fail-closed: integration testleri `SAYDIN_INTEGRATION_REQUIRED=true` ile 'skip yasak' kapısı kullanıyor ve CI 'sıfır failed/skipped/notExecuted' şartı arıyor.

**Neden birinci sınıf değil.** DR/backup güvencesinin en davranışsal kısmı opsiyonel hale gelmiş durumda ve kaybı gözlemlenemiyor. Bu, repo'nun her yerde uyguladığı zero-skip disiplininin tek istisnası.

**Nasıl kapanır.** Repo idiomunu uygula: `BACKUP_DOCKER_SMOKE_REQUIRED` ortam değişkeni ekle, CI'da (`ci.yml:120` adımında) `true` olarak set et. Değişken `true` iken docker bulunamazsa fail-closed çık (`backup_static_failed:docker_smoke_unavailable`). Değişken set değilse mevcut atlama davranışı sürsün ama çıktı satırı açıkça `backup_static_self_test_passed:{N} skipped:restore_volume_init_docker_smoke,base_backup_docker_behavior_smoke` yazsın, böylece atlama hiçbir zaman sessiz olmasın.

### [Low] Boundary 404'ün tek unit testi yeni ProblemDetails gövdesini hiç doğrulamıyor; test adı hâlâ "Empty404" diyor

`tests/Saydin.Api.Tests/Middleware/ApiPortBoundaryMiddlewareTests.cs:93-111; src/Saydin.Api/Middleware/ApiPortBoundaryMiddleware.cs:20-34; ApiErrorCodes.cs:38` · R01 · CONFIRMED

Doğru. Bu diff'in eklediği hata-zarfı sözleşmesi (ProblemDetails + kararlı `code` + traceId + problem+json content-type) bu yol için regresyon kilidi olmadan duruyor ve testin adı gövdenin boş olduğunu söyleyerek yanlış yönlendiriyor.

**Neden birinci sınıf değil.** Biri WriteAsJsonAsync çağrısını, contentType argümanını veya code extension'ını kaldırırsa test yeşil kalır; CLAUDE.md'nin ProblemDetails/traceId kuralı bu yol için doğrulanmamış olur.

**Nasıl kapanır.** Testi `Invoke_RejectedRoute_WritesRouteNotFoundProblemDetails` olarak yeniden adlandır, `context.Response.Body = new MemoryStream()` ata ve Content-Type == application/problem+json, code == "route_not_found", traceId varlığını assert et. ApiManagementBoundaryHttpTests'teki gerçek-listener testinde de public port'tan /metrics yanıtının gövdesini doğrula.

### [Low] Registration cap'i ve rehash yolu iddiaya uydurulmuş testlerle mühürlenmiş

`tests/Saydin.Api.Tests/Endpoints/InstallationAuthenticationFilterTests.cs:99-120; tests/Saydin.Api.IntegrationTests/ErrorContractHttpTests.cs:232-243,277-303; tests/Saydin.Api.IntegrationTests/DistributedSecurityLimiterIntegrationTests.cs:149-176; tests/Saydin.Api.IntegrationTests/InstallationCredentialRehashIntegrationTests.cs:22-46` · R02 · CONFIRMED

Registration cap'inin Redis semantiği (dört pencere, atomiklik) ve filtrenin HTTP sözleşmesi (429/Retry-After/code) testlerle korunuyor; limiter'ı gerçek Redis'le açan bir HTTP factory de mevcut. Korunmayan tek şey, canlı `POST /v1/installations` route'unun bu filtreye gerçekten bağlı olduğudur. Rehash testindeki 'aynı bearer'dan türemiş verifier' yorumu testin kanıtladığından fazlasını söylüyor, ama iddia edilen invariant başka testlerde kapsanıyor.

**Neden birinci sınıf değil.** Bir refactor `.RequireRegistrationAdmission()` çağrısını route'tan düşürürse tüm testler yeşil kalır ve kimlik doğrulamasız principal üretimine karşı tek kapı sessizce açılır.

**Nasıl kapanır.** SecurityAdmissionWebAppFactory'de `RegistrationExactDailyLimit=2` gibi küçük bir değerle tek bir uçtan uca test ekle: 3. `POST /v1/installations` → 429, `Retry-After` pozitif ve bounded, `code=security_rate_limited`, ve DB'de 3. bir `users` satırı yaratılmamış. Rehash testindeki yanıltıcı yorumu düzelt (test DB sözleşmesini kanıtlıyor, keyring türetimini değil).

### [Low] JsonbStorageSize üst sınırı yalnız tek yönde test ediliyor; tahminin gevşekliği ölçülmüyor

`src/Saydin.Api/Helpers/JsonbStorageSize.cs:33-55,74-149; src/Saydin.Api/Helpers/ActivityLogBuilder.cs:106-128; tests/Saydin.Api.IntegrationTests/ActivityLogWriterIntegrationTests.cs:118-149` · R03 · CONFIRMED

Üst sınır fonksiyonu bilinçli olarak muhafazakârdır ve testler yalnız 'asla eksik tahmin etmez' yönünü kilitler; 'ne kadar fazla tahmin edebilir' için hiçbir kapı yoktur. Gerçek bir üretim payload'ının bu gevşeklik yüzünden eşiği aştığı gösterilemedi — risk teoriktir, ama ölçüm boşluğu gerçektir.

**Neden birinci sınıf değil.** Aşırı muhafazakâr tahmin, DB'nin kabul edeceği bir payload'ı tamamen placeholder'a çevirebilir; kayıp yalnız `saydin_activity_log_data_truncations_total` ile görünür ve bu sayaç hiçbir alarm kuralına bağlı değil. Geliştirici tahminin gerçek boyuta oranını ölçemez.

**Nasıl kapanır.** Gerçek-PG testine gerçek payload factory çıktılarından oluşan bir korpus ekleyip `upperBound <= 2 * pg_column_size` ve `upperBound < DataMaxBytes` kapısını kur; eşiği aşan payload'da tüm data'yı atmak yerine alan-bazlı kırpma (dizileri kısaltma) uygula; ActivityLogDataTruncations için ayrı bir warning alarmı tanımla.

### [Low] Determinizm enjeksiyonu yarım: jitter Random.Shared, TcmbAdapter'da TimeProvider opsiyonel, EvdsInflationMapper DateTimeOffset.UtcNow

`src/Saydin.PriceIngestion/Extensions/HttpResilienceExtensions.cs:106-112; src/Saydin.PriceIngestion/Adapters/TcmbAdapter.cs:22-25; src/Saydin.PriceIngestion/Mappers/EvdsInflationMapper.cs:38` · R06 · CONFIRMED

Determinizm enjeksiyonu üç noktada yarım: `JitteredExponentialDelay` `Random.Shared` kullandığı için 429 + Retry-After yolunun üstel tabanı testte sürülemiyor (test yalnız `ResolveRetryDelay`'i doğrudan çağırıyor); `TcmbAdapter` bu diff'te TimeProvider'ı OPSİYONEL parametre olarak aldı, kardeş adapter'lar zorunlu alıyor; `EvdsInflationMapper.cs:38` hâlâ `DateTimeOffset.UtcNow` ile entity zaman damgalıyor. CLAUDE.md PriceIngestion'ı TimeProvider zorunluluğundan muaf tuttuğu için sert ihlal değil, tutarlılık kaybıdır.

**Neden birinci sınıf değil.** 429 backoff'un üstel tabanı ve jitter aralığı test edilemez durumda; bir regresyon sessizce geçer. Opsiyonel `TimeProvider`, yeni testlerin farkında olmadan duvar saatine bağlanmasına izin verir (gün-dönümü flaky'liği). Adapter'lar arasında farklı bağımlılık sözleşmesi ikinci okuyucu için gereksiz sürpriz.

**Nasıl kapanır.** `JitteredExponentialDelay`'e `Func<double> randomizer` parametresi ver ve `AddSaydinResilience` üzerinden enjekte edilebilir yap; testte sabit değerle üstel tabanı doğrula. `TcmbAdapter`'daki `TimeProvider`'ı zorunlu parametreye çevir (DI'da zaten kayıtlı). `EvdsInflationMapper`'a saat parametresi ekleyip adapter'ın enjekte edilmiş `timeProvider`'ını geçir.

### [Low] Yeni `ProviderValueParser` beş mapper'ın ortak finansal ayrıştırma noktası olmasına rağmen kendi testi yok; kasıtlı `NumberStyles` daraltmaları hiçbir testle korunmuyor

`src/Saydin.PriceIngestion/Mappers/ProviderValueParser.cs:11-16 (yeni dosya); tests/Saydin.PriceIngestion.Tests/Adapters/ (ProviderValueParserTests yok)` · R06 · CONFIRMED (doğrulayıcı ek bulgusu)

`ProviderValueParser` bu diff'te eklenen untracked yeni dosya ve `EvdsInflationMapper`, `TwelveDataMapper`, `OpenExchangeRatesMapper`, `TcmbMapper` (satır 166-167) hepsi `NumberStyles.Any`'den buna geçirildi. `FinancialNumberStyles` yorumu (satır 8-10) thousands separator ve parantez dışlamasını gerekçelendiriyor; ancak `grep -rln "FinancialNumberStyles" tests/` hiçbir sonuç vermiyor ve `tests/Saydin.PriceIngestion.Tests/` altında `ProviderValueParserTests.cs` yok. Ayrıca geçiş `NumberStyles.AllowExponent`'i de sessizce düşürüyor — yorum bunu hiç anmıyor.

**Neden birinci sınıf değil.** Finansal ayrıştırmanın güvenlik daraltması ("1,234.56" veya "(5)" reddedilmeli) hiçbir testle korunmuyor; geri alınırsa CI sessiz kalır ve locale-formatlı bir değer farklı bir tutara dönüşebilir. `AllowExponent` kaybı belgesiz olduğu için sağlayıcı "1.2E+3" gibi bir string dönerse satır sessizce düşer ve nedeni koddan anlaşılmaz.

**Nasıl kapanır.** `ProviderValueParserTests.cs` ekle: `"1,234.56"`, `"(5)"`, `"$5"` reddedilmeli; `" -1.5 "` kabul edilmeli; Number/String/Null/Object her ValueKind için beklenen davranış assert edilmeli. `AllowExponent` dışlamasını yoruma ekle veya bilinçli değilse geri koy.

### [Low] Yeni `RepairRecommendationPolicyTests` totolojik: switch'i kendisine karşı doğruluyor, wire sözleşmesini hiç sınamıyor

`tests/Saydin.DataQualityAudit.Tests/RepairRecommendationPolicyTests.cs:6-24; src/Saydin.DataQualityAudit/AuditAccumulator.cs:78-98` · R08 · CONFIRMED (doğrulayıcı ek bulgusu)

Test üç InlineData ile `PolicyFor("DQ-003") == RestoreSchemaContract` gibi eşlemeleri ve saf bir switch için önemsiz biçimde doğru olan determinizmi (`first.Should().Be(second)`) doğruluyor. `ToWireAction`'ın ürettiği string'ler (`restore_schema_contract` vb.), bunların `repair-recommendations.json`'a serileştirilmesi ve DataRepair'in kabul ettiği operasyon tipleriyle ilişkisi hiçbir testte geçmiyor (grep: bu string'ler yalnız AuditAccumulator.cs'te var).

**Neden birinci sınıf değil.** Kapatma kanıtı iddiaya uydurulmuş bir test; gerçek sözleşme (wire string'leri ve DataRepair'in kabul kümesi) korumasız kalıyor.

**Nasıl kapanır.** Testi wire string'leri üzerinden yaz ve DataRepair'in kabul ettiği operasyon tipleri kümesiyle (paylaşılan sabit) karşılaştıran bir cross-project sözleşme testine dönüştür; yürütülebilir karşılığı olmayan aksiyonların bilinçli olarak `manual_review`'a düştüğünü assert et.

### [Low] `backup-static-self-test.py` ağırlıklı olarak metin-varlığı kontrolü; mutasyonlar yalnız aynı grep'in tetiklendiğini kanıtlıyor, davranışı değil

`infrastructure/backup/tests/backup-static-self-test.py:90-146,148-260,264-273` · R10 · DOĞRULANMADI (yalnız üreten agent)

`required` sözlüğünün ~40 anahtarının büyük çoğunluğu `"<literal>" in entry` / `entry.count(...) >= N` biçiminde. Örnekler: `"restic_lock_retry_and_weekly_prune"` yalnız `entry.count('restic --retry-lock "$restic_retry_lock"') >= 6` sayıyor; `"bounded_physical_slot"` yalnız `'--slot="$slot"' in entry` bakıyor; `"restore_completed_wal_rpo"` alan adlarının metin olarak geçmesini istiyor. Dosyanın kendi yorumu (satır 262-263) mutasyonların "yalnız pozitif token'ların varlığını kanıtlamak yerine eski üretim hatalarına bağlandığını" söylüyor, ama dört mutasyonun her biri (satır 264-273) yine aynı grep fonksiyonunu ters yönde çağırıyor — yani grep'in seçiciliğini ölçüyor, kodun davranışını değil.

**Neden birinci sınıf değil.** Kapı bakım maliyeti yüksek (her metin değişikliği testi kırar) ama regresyon yakalama gücü düşük; okuyucu için hangi güvencenin gerçekten kanıtlandığı ile hangisinin sadece "metin var" olduğu ayırt edilemiyor. Gerçek davranış kanıtı (base-backup ve archive-timeout smoke'ları) mükemmel; statik katman onların yanında gürültü yaratıyor.

**Nasıl kapanır.** Metin kontrollerini iki kümeye ayır ve isimlendir: `contract_text_*` (bilinçli olarak metin) ve `behavior_*` (gerçekten koşan). Davranışla kanıtlanabilen anahtarları (retry-lock, slot, prune periyodu, RPO alanları) `base-backup-behavior-smoke.py`'ye taşı — orada zaten `/state/calls` üzerinden gerçek argümanlar gözleniyor. Kalan metin kontrollerini tek bir "drift guard" listesine indir.

### [Low] release-images.yml promtool testini yalnız rules.test.yml üzerinde koşuyor; inventory.test.yml release policy kapısında yok

`.github/workflows/release-images.yml:80-84; infrastructure/deployment/validate-production-assets.sh:69-73; .github/workflows/ci.yml:115` · R11 · CONFIRMED

Kapsam kaybı yok — inventory.test.yml CI'da (ci.yml:115 → validate-production-assets.sh) koşuyor ve release policy job'ı o CI run'ının başarısını zorunlu kılıyor. Kusur, aynı işi iki yerde farklı kapsamda yapan tekrarın ileride ayrışabilecek olmasıdır.

**Neden birinci sınıf değil.** release-images.yml'yi okuyan biri 'alert kuralları release'de tam test ediliyor' sonucunu çıkarır; ileride biri güncellenip diğeri unutulursa tekrar gerçek boşluğa döner.

**Nasıl kapanır.** En sade çözüm: release-images.yml'deki iki `docker run ... promtool` bloğunu kaldırıp yalnız `verify-release-ci-admission.py` kapısına güven. İkinci seçenek: promtool çağrısına `inventory.test.yml`'i ekleyerek iki kopyayı hizala.

### [Low] volume-contract-self-test yalnız 2 mutasyon içeriyor ve geçici dizinleri repo çalışma ağacında oluşturuyor

`infrastructure/deployment/volume-contract-self-test.py:26-53; infrastructure/deployment/validate-blackbox-targets.py:18-42; .github/workflows/release-images.yml:55` · R11 · CONFIRMED

İddia doğru. `dir=directory` seçimi savunulabilir olabilir ama gerekçesiz ve `.gitignore` deseni yok; asıl eksik mutasyon disiplinidir.

**Neden birinci sınıf değil.** SSRF-benzeri arbitrary-probe savunması ve volume sahiplik sözleşmesi tek birer mutasyonla mühürlenmiş; validator ileride gevşetilirse hiçbir test bunu yakalamaz.

**Nasıl kapanır.** Yukarıdaki sekiz reddi (symlink root, yanlış uid, ek dosya, nlink>1, 0644 mode, >16 KiB, bozuk `labels`, `http://` şeması) kapsayan mutasyonları ekle. Geçici dizin için sistem tmp'sini kullan; `dir=directory` gerekliyse gerekçeyi yorumda yaz ve `.gitignore`'a `saydin-volume-contract-*` deseni ekle.

### [Low] `verify-release-ci-admission.py` REQUIRED_JOBS'u ci.yml'deki gerçek job adlarıyla hiçbir kapı karşılaştırmıyor

`.github/scripts/verify-release-ci-admission.py:14-22; .github/scripts/validate-workflows.py:59-69` · R12 · CONFIRMED

İddia doğru. Önemli nüans: (a) yönü fail-closed (bir `name:` değişirse release admission reddeder — geç ama güvenli); (b) yönü fail-open (ci.yml'e yeni bir required job eklenip REQUIRED_JOBS güncellenmezse o job release kabulünde hiç aranmaz) — asıl risk budur.

**Neden birinci sınıf değil.** 'Exact-commit required CI kabulü' garantisi elle senkronize iki listeye bağlı; yeni bir güvenlik/kalite job'ı release kapısına sessizce dahil olmaz.

**Nasıl kapanır.** `validate-workflows.py`'ye ci.yml'i parse edip her top-level job için `name:` (yoksa id) çıkaran ve bu kümenin `verify_release_ci_admission.REQUIRED_JOBS` ile birebir eşit olduğunu doğrulayan bir kontrol ekle (modülü `importlib` ile yükle; `test-verify-release-ci-admission.py` bu tekniği zaten kullanıyor). Bu kontrolü R12-02 ile birlikte `production-assurance`'a bağla.

### [Low] Migrator unit filtresi `Category=Unit` trait'ine geçti ama trait'in yeni saf-unit sınıflara uygulandığını doğrulayan kapı yok

`.github/scripts/run-unit-coverage.sh:28-37; tests/Saydin.DatabaseMigrator.Tests/` · R12 · CONFIRMED

İddia doğru. Not: değişiklik yine de bir iyileşme (allowlist bakımı kalktı) ve `run-unit-coverage.sh:45-56` proje envanteri için zaten bir eşitlik kontrolü uyguluyor — aynı disiplin sınıf/trait düzeyinde uygulanmamış.

**Neden birinci sınıf değil.** Migrator'da yeni saf-unit sınıflar hızlı unit kapısına ve kritik namespace eşiğine katkı vermeden geçebilir; 'yeni unit sınıf sessizce düşer' semantiği mekanizma değişse de sürüyor.

**Nasıl kapanır.** Migrator test projesine bir sözleşme testi ekle: assembly'deki, adı `*IntegrationTests` ile bitmeyen tüm test sınıflarının `Trait("Category","Unit")` taşıdığını doğrula. Alternatif ve daha temizi: integration sınıflarını ayrı bir `Saydin.DatabaseMigrator.IntegrationTests` projesine taşı — o zaman filtreye hiç gerek kalmaz ve `run-unit-coverage.sh`'nin envanter kontrolü tek otorite olur (bu R12-07'deki kardinalite sorununu da düzeltir).

### [Low] ActivityPrincipalPseudonymizer'ın private-secret sözleşmesi için negatif test yok; Linux-guard konvansiyonu iki suite arasında tutarsız

`tests/Saydin.Api.Tests/Security/ActivityPrincipalPseudonymizerTests.cs:12-58; tests/Saydin.Api.Tests/Security/InstallationCredentialKeyringTests.cs:15-21,94-186,302-307` · R13 · CONFIRMED

Doğru, ancak etki finder'ın söylediğinden düşük: SecureSecretFile.ReadBytes'ın world-readable/symlink/hardlink/parent-mode sözleşmesi repo'da başka giriş noktalarında test ediliyor (tests/Saydin.DataQualityAudit.Tests/SecretFileContractTests.cs, tests/Saydin.DatabaseRoleBootstrap.Tests/SecretFileTests.cs). Boşluk paylaşılan helper değil, pseudonymizer'ın o helper'ı kullanmaya devam ettiğinin ve 32-bayt sınırının kilitlenmemiş olmasıdır.

**Neden birinci sınıf değil.** Load'daki ReadBytes çağrısı File.ReadAllBytes ile değiştirilir veya min/max gevşetilirse hiçbir test kırılmaz; audit korelasyon anahtarı zayıf izinli bir dosyadan okunmaya başlayabilir.

**Nasıl kapanır.** Keyring suite'indeki dosya-hardening testlerini ortak bir SecretFileContractAssertions yardımcısına çıkarıp her iki Load giriş noktası için theory olarak çalıştır (0644, symlink, hardlink, 0755 parent, 31/33 bayt) ve RequireLinux() helper'ını ortaklaştır.

### [Low] Planner-noise ekleyen scenario index testi paylaşılan integration DB'sinde bayat ANALYZE istatistiği bırakıyor

`tests/Saydin.Api.IntegrationTests/SavedScenarioRepositoryIntegrationTests.cs:133-143 ve InsertPlannerNoiseAsync/DeletePlannerNoiseAsync yardımcıları` · R13 · CONFIRMED

Olgu doğru, tetiklenme senaryosu bugün mevcut değil: `grep -rn EXPLAIN tests/` tüm repoda tek kullanım (bu testin kendisi) buluyor, yani bayat istatistikten etkilenecek başka bir plan assertion'ı yok; ayrıca CI integration DB'si her koşuda UUID'li ve ephemeral olduğundan kalıntı sonraki koşulara taşınmaz. Bu bir hijyen/determinizm açığıdır, aktif flake kaynağı değil.

**Neden birinci sınıf değil.** İleride ikinci bir plan/performans assertion'ı eklenirse sıra bağımlı, açıklaması zor bir başarısızlık doğabilir; test izolasyonu iddiası zayıflar.

**Nasıl kapanır.** DeletePlannerNoiseAsync sonunda `ANALYZE saved_scenarios; ANALYZE users;` çalıştır ve fixture disposal'ında `device_id LIKE 'scenario-plan-%'` kalıntılarını süpüren savunma temizliği ekle.

### [Low] Rehash integration testi tek bir mega-Fact ve 'Atomically/concurrent' iddiasını gerçekte doğrulamıyor

`tests/Saydin.Api.IntegrationTests/InstallationCredentialRehashIntegrationTests.cs:13-82` · R13 · CONFIRMED

Doğru: test adının vaat ettiği atomiklik/eşzamanlılık garantisi ile fiilen doğrulanan davranış ayrışıyor; Task.WhenAll çakışmayı zorlamıyor.

**Neden birinci sınıf değil.** resolve_installation_and_rehash içindeki atomiklik kaldırılsa test yine geçer; ayrıca beş senaryodan biri kırıldığında hangi lifecycle davranışının bozulduğu anlaşılmaz ve kalanlar hiç çalışmaz.

**Nasıl kapanır.** Senaryoları ayrı SkippableFact'lere böl; eşzamanlılık için N=20 paralel resolve çalıştırıp tam olarak bir generation artışı ve tek nihai verifier olduğunu doğrula veya advisory lock ile örtüşmeyi zorla.

### [Low] Finansal redaksiyon testinde sentinel tekrarı — 'her sentinel' iddiasına rağmen alanların çoğu 1m ile dolduruluyor

`tests/Saydin.Api.Tests/Endpoints/TelemetryPrivacyTests.cs:13-65,148-155; src/Saydin.Api/Models/Responses/WhatIfResponse.cs:5-16` · R13 · CONFIRMED

Doğru: test adı 'RedactEveryFinancialSentinel' derken redaksiyon kanıtı yalnız sentinel taşıyan birkaç alan için geçerli; BuyPrice/SellPrice/UnitsAcquired gibi alanların sızması testi kırmaz.

**Neden birinci sınıf değil.** ADR-006 audit izi için verilen 'finansal veri sızmaz' güvencesi kısmi; activity data factory'sine yeni alan eklendiğinde test otomatik kapsamaz.

**Nasıl kapanır.** Her decimal alana benzersiz artan sentinel ata (reflection ile response record'unu gezen bir yardımcı idealdir) ve 'üretilen JSON hiçbir sentinel'i içermez' + 'beklenen bucket/outcome alanları mevcut' şeklinde kur.

### [Low] Cache-envelope mutasyon theory'lerinde 4096 karakterlik yapay değerler gerçekçi locale/catalog mutasyonunu test etmiyor

`tests/Saydin.Api.Tests/Services/WhatIfCalculatorTests.cs:511-536; tests/Saydin.Api.Tests/Services/DcaCalculatorTests.cs:892-915; src/Saydin.Api/Services/CalculationCacheEntries.cs:55-71,225-235` · R13 · CONFIRMED

Mutasyonlar yalnızca envelope'un shape guard'larını (IsLanguage/IsCatalogHash) uyandırıyor; gerçek eşitlik karşılaştırması (Language ordinal eşitliği ve CatalogCacheContract hash eşitliği) test edilmiyor — o satırlar silinse theory yine yeşil kalır.

**Neden birinci sınıf değil.** Dil/katalog bazlı cache karışmasına karşı savunma derinliği katmanının eşitlik kontrolü kanıtsız. Language cache key'inin de parçası olduğu için gerçek sızıntı ikinci bir hata gerektirir; bu yüzden Low.

**Nasıl kapanır.** Mutasyonları gerçek alan uzayından seç: Language = "en" (entry "tr" iken), CatalogHash = 64 hex'in tek karakteri değiştirilmiş hali; aşırı uzun/bozuk değerler için amacı açık ayrı bir test (MalformedEnvelopeField_IsRejectedByShapeGuard) yaz.

### [Low] ActivityLogWriter retry backoff'u TimeProvider'a bağlı değil; yeni testler gerçek duvar saatiyle uyuyor

`src/Saydin.Api/BackgroundServices/ActivityLogWriter.cs:20,200-206; tests/Saydin.Api.Tests/BackgroundServices/ActivityLogWriterTests.cs:73-105` · R13 · CONFIRMED

Doğru, ancak CLAUDE.md alıntısı yerinde değil: kural 'DateTime.UtcNow yerine TimeProvider.GetUtcNow()' diyor; writer saat OKUMUYOR, yalnız Task.Delay ile bekliyor. Asıl argüman kural ihlali değil, determinizm ve backoff politikasının (200ms/400ms) hiçbir testte doğrulanamamasıdır; 600ms ile 5s arasındaki marj geniş olduğundan pratik flake riski düşüktür.

**Neden birinci sınıf değil.** Unit paketi gereksiz uzuyor; backoff süresi ileride büyütülürse aynı testler sessizce timeout'a girer ve gecikme sözleşmesi hiçbir yerde kilitli değil.

**Nasıl kapanır.** ActivityLogWriter'a TimeProvider enjekte edip `Task.Delay(delay, timeProvider, ct)` kullan; testlerde FakeTimeProvider.Advance ile hem süreyi hem beklenen 200ms/400ms aralıklarını doğrula.

### [Low] Gerçek-Redis admission HTTP testi, admission dışı bir ürün konfigürasyonuna (free-tier feature flag → 403) bağlı

`tests/Saydin.Api.IntegrationTests/ErrorContractHttpTests.cs:276-305 (SecurityAdmissionHttpTests.PrincipalLimit_WithRealRedis_ReturnsLocalized429BeforeHandler)` · R13 · CONFIRMED

Doğru: testin kırılma nedeni (free-tier feature flag durumu) ile adının vaat ettiği davranış (admission'ın handler'dan önce 429 vermesi) birbirinden bağımsız; 'localized' iddiası tek dilde doğrulanıyor.

**Neden birinci sınıf değil.** What-if free tier'a açılırsa admission davranışı hiç değişmese de test kırılır; tersine dil müzakeresi bozulup her yanıt Türkçe dönse test yine geçer. Bakım maliyeti yüksek, teşhis değeri düşük.

**Nasıl kapanır.** İlk isteğin handler'a ulaştığını feature-flag'e bağlı olmayan bir işaretle doğrula (yanıtın 429 OLMAMASI + SecurityAdmissionDecisions sayacının outcome=allowed ile bir kez artması). 429 gövdesini hem en hem tr ile isteyip iki farklı title döndüğünü assert et.

### [Low] ApiTrustSchemaModelTests, 'testte sabit yok' iddiasına rağmen index adını elle enjekte ediyor ve tek migration dosyasını otorite sayıyor

`tests/Saydin.Api.Tests/Data/ApiTrustSchemaModelTests.cs:22-29,54-70,118-121` · R13 · CONFIRMED

Doğru ama ileriye dönük bir bakım borcu: bugün kırık değil. 025+ bir migration installation_credentials üzerine yeni bir chk_/uq_ nesnesi eklerse BeEquivalentTo 021'i tek kaynak saydığı için yanlış yerde kırılır; ayrıca assertion mesajının 'test sabiti yok' iddiası kısmen yanlıştır.

**Neden birinci sınıf değil.** Şema-parity kanıtının iki farklı, kısmen çelişen implementasyonu var; testin kendi gerekçe metni yanıltıcı.

**Nasıl kapanır.** Tüm migration dosyalarını numara sırasıyla tarayıp ADD/DROP oynatan ortak bir MigrationSchemaSnapshot yardımcısı kur; hem ApiTrustSchemaModelTests hem SharedEfParityTests ondan beslensin. Index adı literal'ini annotation anahtarından türet.

### [Low] Calendar acquisition/promotion script sözleşmesi hâlâ substring grep'i ile doğrulanıyor

`tools/calendar-data/tests/Saydin.CalendarData.Tests/InfrastructureCalendarContractTests.cs:24-71 ↔ infrastructure/calendar/run-acquisition.sh:45-53, :64-78, :80-108; infrastructure/calendar/promote-reviewed-bundle.sh:58-82` · R14a · CONFIRMED

Acquisition ve promotion script'lerinin sözleşmeleri (sandbox bayrakları, noop yolları, quarantine temizliği, import/activate yasağı) yalnız metin düzeyinde korunmaktadır; `--memory 256m` gibi bayraklar birden çok docker çağrısında geçtiği için substring assert bir bayrağın yanlış çağrıya taşınmasını yakalayamaz. verify-candidate.sh için kısmen davranışsal test eklenmiş, acquire/promote için hiç yoktur.

**Neden birinci sınıf değil.** Offline/hardened acquisition ve quarantine promotion sözleşmelerinin regresyon koruması etkisiz kalabilir; önceki review'in "substring assert" eleştirisinin kapatılmayan kısmıdır.

**Nasıl kapanır.** Promotion testindeki stub yaklaşımını genişlet: stub `docker`/`jq` ile (a) noop yolunun gerçekten exit 0 + `calendar_acquisition_noop=` yazdığını, (b) quarantine temizliğinin dosyayı gerçekten kaldırdığını, (c) her `docker run` çağrısının beklenen bayrak setini taşıdığını (bayrağı hangi çağrının aldığını ayırt ederek) doğrulayan davranışsal case'ler ekle.

### [Low] En riskli ingestion namespace'leri (`Workers`, `Mappers`) için coverage tabanı yok

`.github/scripts/coverage-thresholds.json:6-15; .github/scripts/coverage-thresholds-unit.json:6-15; src/Saydin.PriceIngestion/Workers/, src/Saydin.PriceIngestion/Mappers/` · R14a · CONFIRMED

Yeni sertleştirilen en kritik dallar (worker deadline, lease renewal, permanent izolasyon, provider değer parse'ı) namespace bazlı coverage ratchet'i tarafından korunmuyor; yalnız global `overall` ve `changed_line: 80` kapıları geçerli.

**Neden birinci sınıf değil.** Bu dalları silen veya kısaltan bir refactor, değişen satır sayısı az olduğu için changed_line kapısını da global tabanı da geçebilir; yeşil CI davranışın hâlâ test edildiğine dair kanıt üretmez.

**Nasıl kapanır.** `Saydin.PriceIngestion.Workers` ve `Saydin.PriceIngestion.Mappers` için ölçülen mevcut değerin hemen altına line/branch tabanı ekle (Adapters'takine benzer), böylece dal silinmesi coverage düşüşü olarak yakalanır.

### [Low] `ResolveLatestExpectedObservationAsync` negatif dalları test edilmiyor; pozitif test üretim sorgusunun sadeleştirilmiş kopyasını oracle olarak kullanıyor

`tests/Saydin.PriceIngestion.IntegrationTests/IngestionWindowRepositoryIntegrationTests.cs:14-47 ↔ src/Saydin.PriceIngestion/Repositories/IngestionWindowRepository.cs:134-171; infrastructure/postgres/migrations/017_authoritative_market_calendars.sql:206-246, :364-385` · R14a · CONFIRMED

Test oracle'ının üretim sorgusunun sadeleştirilmiş kopyası olması ve iki negatif dalın (`calendar_active_release_missing`, `calendar_eligible_day_missing`) hiç test edilmemesi doğrudur; ancak finder'ın "SealedAt filtresi düşerse mühürlenmemiş release aktif kabul edilir" tetiği geçersizdir — 017 numaralı migration'daki trigger ve payload doğrulama fonksiyonu bunu veritabanı düzeyinde engeller. Kalan gerçek kusur, testin oracle'ının üretim mantığını aynalaması (tautolojiye yakın) ve fail-closed dalların kapsanmamasıdır.

**Neden birinci sınıf değil.** Fail-closed dallarda bir regresyon (ör. `calendar_eligible_day_missing` yerine sessizce `Ready=true` dönmek) hiçbir testte yakalanmaz; veri bütünlüğü etkisi ise DB invariant'ları sayesinde sınırlıdır.

**Nasıl kapanır.** Oracle'ı üretim SQL'inin kopyası yerine fixture verisinden türetilmiş sabit beklenen tarihe bağla. `calendar_active_release_missing` (desteklenen ama aktif release'i olmayan calendar_code) ve `calendar_eligible_day_missing` (notAfter'ı CoverageFrom'un öncesine veren cutoff) için birer negatif case ekle.

### [Low] VerifyCandidateBehaviorTests'in stub `jq`'su `select(...)` guard'larını yok sayıyor; envelope schema/snapshotSetId doğrulaması gerçekte hiç koşmuyor

`tools/calendar-data/tests/Saydin.CalendarData.Tests/VerifyCandidateBehaviorTests.cs:61-83 ↔ infrastructure/calendar/verify-candidate.sh:57-70` · R14a · CONFIRMED (doğrulayıcı ek bulgusu)

Test PATH'in başına kendi `jq` stub'ını koyuyor; stub sadece `.schemaVersion`, `.snapshotSetId`, `.sourceManifestSha256`, `.expectedOutputSha256` anahtarlarını sed ile çekiyor ve ifadedeki `select(...)` kısmını tamamen yok sayıyor (`case "$expression" in .schemaVersion*) key=schemaVersion ;; ...`). Oysa script bu guard'lara güveniyor: `jq -er '.schemaVersion | select(. == 1)'` → envelope_schema_invalid, `jq -er '.snapshotSetId | select(test("^[a-z0-9][a-z0-9._-]{0,79}$"))'` → envelope_snapshot_set_invalid, `jq -er '.sourceManifestSha256 | select(test("^[0-9a-f]{64}$"))'` → envelope_manifest_hash_invalid. Stub ile bu üç fail-closed kodun hiçbiri tetiklenemez ve `jq -er`'ın gerçek boş-çıktı/null semantiği de doğrulanmaz.

**Neden birinci sınıf değil.** "Gerçek script'i çalıştırıyoruz" iddiası envelope doğrulama semantiği için geçerli değildir; verifier'ın schema/snapshot-set/hash-format kapıları davranışsal koruma altında değildir.

**Nasıl kapanır.** Stub jq yerine test imajında gerçek `jq`'yu kullan (Linux CI gate'inde mevcut ya da kolayca kurulabilir); mümkün değilse stub'a `select` semantiğini gerçekten uygula ve `envelope_schema_invalid` ile `envelope_snapshot_set_invalid` için birer negatif InlineData ekle.

### [Low] CanonicalJsonParityTests bayt-eşitliğini kanıtlamıyor: MaxDepth ayrışması vektörlerin dışında, ret testi zayıf

`tests/Saydin.DataRepair.Tests/CanonicalJsonParityTests.cs:10-32; src/Saydin.DataRepair/CanonicalJson.cs:10-15; src/Saydin.DataQualityAudit/CanonicalJson.cs:12` · R14b · CONFIRMED

İki kanonikleştirici arasındaki tek gerçek ayrışma `MaxDepth` (32 vs 64) ve parity testi bunu görmüyor; ret testi ise iki tarafın exception tipini/kodunu assert etmediği için 'aynı sözleşmeyi reddediyorlar' iddiasını kanıtlamıyor. Escaping/sıralama ayrışması iddiası ise yanlış — her iki taraf da aynı Utf8JsonWriter varsayılanlarını ve aynı ordinal sıralamayı kullanıyor. Manifest şeması sığ olduğu için MaxDepth farkının üretimde tetiklenme senaryosu yok; bu bir kanıt-değeri boşluğu, işleyen bir kusur değil.

**Neden birinci sınıf değil.** İki (fiilen üç, `MigrationImpactManifest` dahil) implementasyonun bayt eşitliği kanıt değil örnek düzeyinde. İmza doğrulaması tam olarak kanonik bayt eşitliğine dayandığı için ileride sessiz bir ayrışma imzalı kanıtın reddine (kullanılabilirlik kaybı) yol açabilir ve mevcut test bunu yakalamaz.

**Nasıl kapanır.** (1) İki tarafın `JsonDocumentOptions`'ını tek bir paylaşılan sabitte birleştir; birleştirilemiyorsa MaxDepth farkını bilinçli sözleşme olarak pinle (33 seviyeli vektör: DQA kabul eder, Repair `evidence_bundle_invalid` ile reddeder → kod adıyla assert). (2) Ret testinde her iki tarafın exception tipini VE kodunu ayrı ayrı assert et. (3) Vektörlere boş `{}`/`[]`, kontrol karakteri, surrogate çifti, non-ASCII property adı sıralaması ve `Int64.MaxValue` ekle.

### [Low] Migrator hızlı unit kapısı opt-in trait allowlist'ine taşındı; trait envanteri doğrulanmıyor

`.github/scripts/run-unit-coverage.sh:32,36,44-56; tests/Saydin.DatabaseMigrator.Tests/{MigrationManifestTests.cs:6,MigrationImpactManifestTests.cs:8,MigratorOptionsTests.cs:6,SqlScriptNormalizerTests.cs:7}` · R14b · CONFIRMED

Filtre opt-in trait allowlist'ine dönüştü ve trait taşıyan sınıf kümesi eski isim allowlist'iyle (artı MigrationImpactManifestTests) aynı. Aynı script proje envanteri için fail-closed bir diff kontrolü ekliyor, fakat trait kapsamı için eşdeğer kontrol yok — yeni bir saf-unit sınıfı `[Trait]` unutursa hızlı kapıdan ve o kapının coverage ölçümünden sessizce düşer. minimum_tests=77 mevcut trait kapsamına birebir eşit olduğu için düşme anında ratchet de kırılmaz (yeni sınıf zaten sayılmaz).

**Neden birinci sınıf değil.** Hızlı geri bildirim kapısı ve unit coverage ratchet'i eksik ölçebilir; yeni bir saf-unit sınıfı yalnız 30+ dakikalık gerçek-PG job'ında koşar. L137'nin kök nedeni yeni bir kılıkta korunur.

**Nasıl kapanır.** Filtreyi tersine çevir (`Category!=Integration`) ve iki integration sınıfını (`MigrationRunnerIntegrationTests`, `InstallationCredentialRehashMigrationIntegrationTests`) işaretle — böylece varsayılan davranış 'dahil' olur. Alternatif: `dotnet test --list-tests` çıktısını script içinde diff'leyerek trait kapsamının bilinen sınıf kümesiyle eşleştiğini fail-closed doğrula (proje envanteri deseninin aynısı).

### [Low] Platform kapısı için aynı değişiklik setinde üç farklı deyim: sessiz return, SkipException, PlatformNotSupportedException

`tests/Saydin.DataRepair.Tests/OptionsAndReceiptTests.cs:49,83,108,133,159; tests/Saydin.DataRepair.Tests/SignedRepairPlanTests.cs:122; tests/Saydin.DataQualityAudit.Tests/SecretFileContractTests.cs:10-11,30-31; tests/Saydin.DatabaseRoleBootstrap.Tests/SecretFileTests.cs:313-327` · R14b · CONFIRMED

Üç farklı platform-kapısı deyimi aynı değişiklik setinde yan yana yaşıyor ve bu doğrulandı. Fakat etki finder'ın yazdığından dar: dokümante edilmiş tüm koşu yolları (docker compose `tests` profili, CI) Linux olduğundan sessiz-return dalları pratikte hiç çalışmaz — sorun yalancı yeşil riskinden çok okunabilirlik ve sözleşme tutarlılığıdır. R14b-01 ışığında ayrıca kritik bir ek not: `SkipException` bu xunit v2 grafiğinde skip üretmez, fail üretir; yani 'doğru' sanılan deyim aslında üçünün en kırılganı.

**Neden birinci sınıf değil.** Test sonucunun anlamı dosyaya göre değişiyor; 'zero-skip' sözleşmesi ile 'sessiz pass' ve 'platform fail' aynı repoda çelişiyor. Bakımcı, bir testin yeşil olmasının gerçekten bir şey kanıtladığını okuyarak anlayamıyor.

**Nasıl kapanır.** Tek bir `LinuxOnly.Require()` yardımcısı tanımla ve tüm test projelerinde kullan. xunit v2'de dinamik skip desteklenmediği için semantik `throw new PlatformNotSupportedException(...)` (fail-closed) veya statik `[SkippableFact]` olmalı; sessiz `return` hiçbir yerde kalmamalı.

### [Low] RepairRecommendationPolicyTests sözdizimsel garanti bir özelliği 'kanıtlıyor' ve 9 check id'nin 3'ünü kapsıyor

`tests/Saydin.DataQualityAudit.Tests/RepairRecommendationPolicyTests.cs:7-22; src/Saydin.DataQualityAudit/AuditAccumulator.cs:78-99` · R14b · CONFIRMED

`code` parametresi kullanılmadığı için testin 'determinizm' assertion'ı sözdizimsel garantidir; 9 check id'den 3'ü ve varsayılan kol kapsanmıyor; `ToWireAction` string'leri hiçbir testte pinlenmemiş. Düzeltme: bu string'ler DataRepair'in plan operation-type uzayına değil, imzalı kanıt paketindeki öneri alanına aittir — yani ayrışma DataRepair'i kırmaz, kanıtı tüketen tarafları sessizce etkiler.

**Neden birinci sınıf değil.** Test kendi başına yanlış bir güven duygusu veriyor; imzalı kanıt paketinin öneri alanı tel sözleşmesi regresyona karşı korumasız (ör. `restore_calendar_release` yeniden adlandırılsa hiçbir test kırılmaz).

**Nasıl kapanır.** `code` parametresini ya kaldır ya da gerçekten kullan. Testi 9 check id + bilinmeyen id'yi kapsayan bir `[Theory]`'ye çevir, tautolojik `first.Should().Be(second)` yerine beklenen (Action, RequiresProviderEvidence) çiftini assert et ve `ToWireAction` string'lerini birebir pinle.

### [Low] FixedTimeProvider(DateTimeOffset.UtcNow) determinizm sağlamıyor; iki projede üç elle yazılmış kopya

`tests/Saydin.DataQualityAudit.Tests/CanonicalAndSignatureTests.cs:60,66,84,105,124,150,167,192; tests/Saydin.DataQualityAudit.Tests/TestFiles.cs:85,88-89,124-126; tests/Saydin.DataRepair.Tests/OptionsAndReceiptTests.cs:226-229; tests/Saydin.DataRepair.Tests/SignedRepairPlanTests.cs:212-215` · R14b · CONFIRMED

DQA testlerinde saat 'donduruluyor' gibi görünüyor ama fixture zaman damgalarını ayrı bir `DateTimeOffset.UtcNow` okumasından üretiyor; yani determinizm kozmetik. Flakilik riski finder'ın ima ettiğinden çok düşük (pencere ±1dk/1sa). Kopya sayısı iki projede üç (üç projede üç değil). DataRepair tarafı zaten `files.Now` sabit değerini kullandığı için doğru davranıyor — sorun yalnız DQA tarafında.

**Neden birinci sınıf değil.** Determinizm iddiası gerçekte karşılanmıyor; CLAUDE.md'nin 'FakeTimeProvider ile saat dondurulur, gün-dönümü flaky'liği önlenir' gerekçesi DQA testlerinde sağlanmamış. Ayrıca aynı yardımcı sınıfın üç kopyası bakım yükü yaratıyor.

**Nasıl kapanır.** DQA testlerinde sabit bir enstantane kullan (ör. `new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero)`) ve aynı değeri hem `TestFiles.ValidManifest(now)`'a hem doğrulayıcıya ver — DataRepair.Tests'in `files.Now` deseninin aynısı. Elle yazılmış üç kopyayı kaldırıp yönetilen `FakeTimeProvider` üzerinde birleş.

### [Low] SCRAM unit vektörünün kaynağı belgelenmemiş ve IsCanonical negatif vakası iki reddi tek girdide birleştiriyor

`tests/Saydin.DatabaseRoleBootstrap.Tests/PostgresScramSha256VerifierTests.cs:8-32; src/Saydin.DatabaseSecurity/PostgresScramSha256Verifier.cs:22-38,83-90` · R14b · CONFIRMED

Vektörün kaynağı belgelenmemiş ve `IsCanonical` negatif vakası iki ayrı reddi tek girdide birleştiriyor — her ikisi de doğru. Ancak 'ikinci bir okuyucu doğrulayamaz' iddiası fazla: vektörü RFC 5802 türetimiyle bağımsız olarak yeniden hesapladım ve birebir tuttu, dolayısıyla vektör doğrudur ve doğrulanabilirdir. Kalan boşluk okunabilirlik (provenance yorumu) ve tek-değişken negatif ayrıştırmasıdır.

**Neden birinci sınıf değil.** Kriptografik regresyon pini olarak değeri gerçek (gerçek-PG auth oracle'ı da destekliyor), ancak `IsCanonical`'daki bir kontrol (ör. iterasyon eşitliği) silinse mevcut theory yine geçer; okuyucu vektörün nereden geldiğini kaynaktan anlayamaz.

**Nasıl kapanır.** Vektörün üretim yöntemini tek satırlık yorumla belirt (RFC 5802 §3 türetimi, salt=0x00..0x0F) ve ek olarak RFC 7677 §3 vektörünü ayrı bir `[Fact]` olarak ekle. `IsCanonical` negatiflerini tek-değişken vakalara ayır: yanlış iterasyon, base64 olmayan salt, 31/33 baytlık StoredKey, 2 bölümlü string, 256 karakter üstü verifier.

---

## İşletilebilirlik (45 kayıt)

### [Medium] `saydin_security_admission_decisions_total` üzerinde hiçbir Prometheus kuralı yok

`infrastructure/prometheus/rules/api.yml:1-46; redis.yml:12-18; docs/decisions/ADR-003-rate-limiting.md:41-42` · R01 · CONFIRMED

ADR-003'ün açıkça istediği 'availability alarmı' toplam Redis kaybı için SaydinRedisUnavailable + SaydinApiErrorBudgetBurn ile karşılanıyor; ADR ihlali değil. Gerçek boşluk daha dar ve yine önemli: admission kararı metriğinin hiçbir kuralı yok, dolayısıyla error-budget eşiğinin (%5) altında kalan KISMİ ve KALICI admission reddi (özellikle normal işleyişte sıfır olması gereken client_address_untrusted serisi) hiç alarm üretmiyor; nöbetçi ancak elle metrik gruplayarak (runbook api-availability.md:13-19) fark edebiliyor.

**Neden birinci sınıf değil.** Reverse-proxy trust yapılandırması kısmen kayarsa veya belirli bir taşıyıcı/VPN grubunun istekleri XFF taşırsa, kullanıcıların bir kısmı süresiz 503 alır ve bu durum hiçbir alarm üretmez. Fail-closed tasarımın maliyeti ölçülebilir ama alarmlanabilir değil.

**Nasıl kapanır.** api.yml'e iki kural ekle: (a) `sum(rate(saydin_security_admission_decisions_total{outcome="unavailable",reason="client_address_untrusted"}[10m])) > 0` → warning, runbook_url api-availability.md; (b) `...{outcome="unavailable",reason=~"redis_failure|malformed_reply"}[5m] > 0` → critical, runbook_url redis-unavailable.md. Üçüncü olarak `outcome="limited", bucket=~"registration|calculation_network"` oranı için warning (bkz. R01-01). Kuralları infrastructure/prometheus/tests/rules.test.yml'e promtool testiyle birlikte ekle.

### [Medium] Rate-limit ayar sabitleri üç yerde tekrarlanıyor ve release kapısı tam-eşitlikle kilitliyor — operasyonel yeniden kalibrasyon yolu yok

`infrastructure/deployment/validate-production.py:222-232; infrastructure/deployment/compose.production.yml:194-200; src/Saydin.Api/appsettings.json:31-44` · R01 · CONFIRMED (doğrulayıcı ek bulgusu)

validate-production.py:224-231 `security_limits` sözlüğünde "3"/"5"/"20"/"100"/"500" değerlerini string olarak tam eşitlikle karşılaştırıyor: `if any(api_env.get(key) != expected ...): reject(errors, "security_limiter_production_limits_invalid")`. Aynı değerler compose.production.yml:196-200 ve appsettings.json:37-41'de de yazılı. Sınır kontrolü değil birebir sabit eşitliği yapılıyor. Buna karşılık ExactIpLimit/NetworkLimit/PrincipalLimit/WindowSeconds compose'da hiç pinlenmiyor ve validator'da hiç kontrol edilmiyor — asimetrik.

**Neden birinci sınıf değil.** Gerçek trafikle kalibre edilmesi gereken tavanlar bir CI kapısında magic literal olarak donduruldu; üretimde yaygın 429 gözlemlendiğinde tepki süresi bir release döngüsü kadar. Ayrıca aynı sabitin üç kopyası sessizce sapabilir (appsettings.json ile compose farklı olursa yalnız compose geçerlidir, validator appsettings'i hiç görmez).

**Nasıl kapanır.** Validator'ı tam-eşitlikten bounded-range + tutarlılık kontrolüne çevir (exactHourly ≤ exactDaily ≤ networkDaily vb. — DistributedSecurityLimiterOptions.HasValidShape bu değişmezleri zaten kodluyor, aynı kuralı tek kaynaktan türet). Böylece ayar değişikliği imzalı manifest üzerinden yapılabilir ama güvenlik değişmezleri korunur. ExactIpLimit/NetworkLimit/PrincipalLimit/WindowSeconds'ı da üretim env'ine taşıyıp aynı aralık kontrolüne bağla.

### [Medium] Pseudonym anahtarının sürüm/rotasyon hikâyesi yok: değer anahtar sürümü taşımıyor, runbook ve dual-key kabulü yok

`src/Saydin.Api/Services/ActivityPrincipalPseudonymizer.cs:20-31,59-75; src/Saydin.Api/Options/ActivityPrincipalPseudonymOptions.cs:9-10; docs/runbooks/ (activity-principal-hmac hiç geçmiyor)` · R03 · CONFIRMED

Pseudonym anahtarı tek dosyalı, sürümsüz ve rotasyon prosedürsüzdür; üretilen `p1:` öneki yalnız şema sürümünü kodlar, anahtar sürümünü değil. Aynı repoda installation credential'ları için sürümlü keyring + dual-key kabul + rotasyon runbook'u varken bu materyal için hiçbiri yoktur.

**Neden birinci sınıf değil.** Anahtar sızar veya bir ortam yeniden materyalize edilirken dosya yeniden üretilirse aynı principal öncesi/sonrası iki farklı pseudonym üretir; denetim izi sessizce ikiye bölünür — M-KEYRING remediation'ının tam olarak önlemek istediği durum. KVKK açısından pseudonymization anahtarının yaşam döngüsü ve kompromis prosedürü belgesizdir.

**Nasıl kapanır.** Pseudonym'e anahtar sürümünü göm (`p1.2:<hex>`) veya keyring dosyası + ActiveKeyVersion modeline geç; docs/runbooks/activity-pseudonym-key-rotation.md ekleyip 'eski satırlar yeniden hesaplanmaz, kesme tarihi şu şekilde kaydedilir' adımını yaz; ADR-006/activity-logging.md KVKK bölümüne anahtar sahipliği ve saklama sınırını ekle.

### [Medium] Kalıcı olarak bloke olmuş scope için metrik/alarm yok; operatör hangi asset'in neden takıldığını tek sorguyla göremiyor

`src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:257-261,321-325; src/Saydin.PriceIngestion/Repositories/IngestionFreshnessTelemetry.cs:87-125; src/Saydin.PriceIngestion/Repositories/IngestionWindowRepository.cs:70-78; docs/runbooks/ingestion-stale.md:1-27` · R05 · CONFIRMED

Doğrulandı, küçük bir nüansla: hata anında `SaydinMetrics.IngestionAttempts` sayacı `outcome="permanent_failure"/"partial_rejected"` etiketiyle bir kez artıyor (IngestionFreshnessTelemetry.cs:67-69), yani tamamen sessiz değil. Ancak bu tek atışlık bir counter; bloke durumu **süregelen bir durum** olarak yayınlayan hiçbir gauge yok ve alarmlanabilir bir "şu anda N pencere bloke" sinyali mevcut değil.

**Neden birinci sınıf değil.** En kritik arıza modu (sessiz kalıcı durma) gözlemlenebilir değil; nöbetçi hangi asset/pencere olduğunu bulmak için elle SQL yazmak zorunda ve runbook kurtarma yolunu hiç anlatmıyor → MTTR uzar.

**Nasıl kapanır.** (a) Hydration sorgusuna `state='permanent_failed'` pencere sayısını ekleyip `saydin_ingestion_scope_blocked` gauge'u olarak (`source`, `job_type`, `outcome_code` etiketli) yayınla. (b) Prometheus'a Critical alert + runbook linki ekle. (c) `docs/runbooks/ingestion-stale.md`'ye bloke pencereyi bulan SQL'i ve imzalı `requeue_permanent_window` adımını (ve v2 pencerelerde `calendar_release_id` bağının sabit kaldığı uyarısını) ekle.

### [Medium] `ProviderExceptionSanitizer.ForLog` exception zincirini ve stack'i yok ediyor; beklenmeyen adapter hatasının kök nedeni loglara ulaşmıyor

`src/Saydin.PriceIngestion/Workers/ProviderExceptionSanitizer.cs:9-26; src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:185-199,432-441; src/Saydin.PriceIngestion/Workers/EvdsInflationWorker.cs:137-156` · R05 · CONFIRMED

Doğrulandı, bir düzeltmeyle: finder "orijinal **tip** de kayboluyor" diyor — bu kısmen yanlış. BaseAssetWorker.cs:190-191 ve EvdsInflationWorker.cs:142-143 log şablonunda `{ExceptionType}` = `ex.GetType().Name` structured property olarak taşınıyor, ayrıca tip `ForLog`/`Detail` mesajının içinde de var (aslında iki kez, bkz. R05-V3). Gerçekten kaybolan şey **inner exception zinciri** ve **stack trace'in ilk satırı dışındaki her şey**.

**Neden birinci sınıf değil.** Bu tam da bir asset'i kalıcı bloke eden yol (`adapter_exception_permanent` → PermanentBlocked). Kök neden analizi için tek kanıt tek satırlık bir mesaj; olayı yeniden üretmek gerekiyor. Gizlilik/tanılanabilirlik dengesi tanılanabilirlik aleyhine fazla kaymış.

**Nasıl kapanır.** Sanitize edilmiş **zincir** üret: her `InnerException` seviyesi için `Type: sanitize(Message)` (derinlik 3-4 sınırlı) ve stack'in ilk N (ör. 5) karesi. Serilog'a sahte exception nesnesi vermek yerine `sanitized_chain` structured property'si kullan.

### [Medium] Bloke pencerelerin kurtarma yolu asset başına tek tek imzalı plan gerektiriyor; toplu kurtarma için ne API ne runbook var

`src/Saydin.PriceIngestion/Repositories/IngestionWindowRepository.cs:556-569; src/Saydin.DataRepair/SignedRepairPlan.cs:92-102; docs/runbooks/ingestion-stale.md:21-22` · R05 · CONFIRMED (doğrulayıcı ek bulgusu)

`RequeuePermanentAsync` imzası `(Guid windowId, DateTimeOffset nextAttemptAt, ct)` — tek pencere. `SignedRepairPlan` doğrulaması her `requeue_permanent_window` operasyonu için ayrı `windowId` + `preimage_sha256` + `next_attempt_at_utc` istiyor ve `windows.Add(id)` ile tekrarı reddediyor. Kaynak-geneli bir arıza (R05-01/R05-V1: takvim ya da provider kaynaklı, tüm asset'leri aynı anda vuran hata) 30 sembolde 30 ayrı operasyon demek; her birinin window id'sini bulmak için de dokümante edilmiş bir sorgu yok (`ingestion-stale.md` yalnız "reviewed provenance workflow" diyor).

**Neden birinci sınıf değil.** Kurtarma insan hatasına açık ve yavaş; kanıt-temelli onarım tasarımının doğru olan katı sözleşmesi, toplu senaryoda pratik bir operasyon yolu bırakmıyor → uzun MTTR.

**Nasıl kapanır.** (a) Bloke pencereleri listeleyen salt-okunur bir komut/sorgu ekle (DataQualityAudit çıktısı veya runbook'ta hazır SQL) ki plan hazırlığı otomatikleşsin. (b) Plan üretimini kolaylaştıran bir yardımcı (window id + preimage hash listesini üreten script) ekle. (c) Alternatif olarak `requeue_permanent_window`'a scope-tabanlı (source + job_type + tarih aralığı) ve budget-sınırlı bir varyant tanımla.

### [Medium] İmzalı onarım planını üretecek araç veya belgelenmiş prosedür yok

`docs/runbooks/data-repair.md:60-66,138-176; src/Saydin.DataRepair/RepairOptions.cs:47-64; src/Saydin.DataRepair/README.md:8-30` · R08 · CONFIRMED

İddia doğrulandı: yürütme tarafı (compose bağlama, release attestation, volume doğrulaması, receipt saklama) çok ayrıntılı belgelenmişken girdi üretimi tamamen belgesiz ve araçsız.

**Neden birinci sınıf değil.** Gerçek bir olayda operatör planı üretemez; yanlış hesaplanmış tek bir preimage `repair_preimage_rejected` ile döner ve döngü elle tekrarlanır. Bu, alt sistemin birinci sınıf olmasını engelleyen en büyük tek boşluktur.

**Nasıl kapanır.** (1) Salt-okunur bir `plan-template`/`--emit-preimages` alt komutu ekle (mevcut audit login'i ve trust lease'i zaten var); (2) `docs/runbooks/data-repair.md`'ye plan şeması, alan-alan türetme kuralları, nonce/approval-token üretimi ve offline imzalama seremonisi bölümü yaz; (3) uçtan uca bir integration kabul testiyle mühürle.

### [Medium] Dry-run gerçek bir önizleme değil; apply yolundaki dört fail-closed kapısını denemiyor ve README bunu yanlış anlatıyor

`src/Saydin.DataRepair/Program.cs:57-66; src/Saydin.DataRepair/RepairDatabase.cs:110-131; src/Saydin.DataRepair/RepairOptions.cs:68-82; src/Saydin.DataRepair/README.md:68-70` · R08 · CONFIRMED

Tüm iddialar doğrulandı; ek olarak README'nin 'validates every preimage and safety guard' cümlesi doğrudan bir doküman-kod uyumsuzluğudur ve runbook (satır 155-157) bağımsız gözden geçiricinin kararını bu dry-run kanıtına dayandırmasını şart koşar.

**Neden birinci sınıf değil.** Onay ritüeli, gözden geçiricinin karar veremeyeceği iki sayıya dayanıyor; destructive adım ise dry-run'ın kanıtlamadığı dört kapıyı (guard bütçesi, approval token, receipt root, KMS erişimi) değişim penceresi açıldıktan ve advisory lock alındıktan sonra ilk kez deniyor.

**Nasıl kapanır.** (1) Dry-run'da her `requeue_permanent_window` için makine-okunur tek satır bas (op index, window, scope, state, outcome, next_attempt, preimage, guard); (2) `ComputeGuardAsync`'i dry-run'da `lockRows:false` ile çalıştırıp bütçeyi gerçekten sına; (3) receipt/approval/KMS argümanlarını dry-run'da isteğe bağlı kabul edip verildiklerinde yalnız doğrula; (4) README'deki guard iddiasını düzelt.

### [Medium] Yeni genel `IOException` catch'i tanılamayı yok ediyor; `DeletePrivateTree` catch bloğunda fırlarsa asıl acquisition hatası kayboluyor

`tools/calendar-data/src/Saydin.CalendarData/Program.cs:67-71; tools/calendar-data/src/Saydin.CalendarData/CalendarAcquisition.cs:195-200` · R09 · DOĞRULANMADI (yalnız üreten agent)

`catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"bundle_unreadable:{ex.GetType().Name}"); }` — hangi dosya, hangi errno, hangi aşama olduğu tamamen düşürülür; tüm IO hataları (disk dolu, mount readonly, izin) tek ve yanıltıcı `bundle_unreadable` kodu altında toplanır. Ayrıca `catch { SecureBundleStorage.DeletePrivateTree(pendingRoot, stagingRoot); throw; }` — `Directory.Delete(recursive:true)` IOException fırlatırsa (ör. dolu disk, NFS meşgul dosya) orijinal `CalendarDataException` (`source_http_status_invalid`, `tcmb_publication_evidence_regressed` vb.) atılır ve operatöre yalnız `bundle_unreadable:IOException` ulaşır.

**Neden birinci sınıf değil.** Bu CLI'nin tek gözlemlenebilirlik yüzeyi bounded stdout/stderr sözleşmesidir (README'de belgeli istisna). Sözleşmenin ayırt ediciliğinin düşmesi, fail-closed davranışın teşhis edilebilirliğini doğrudan azaltır; runbook "acquisition/promotion fails" trigger'ından sonraki ilk adımı imkânsızlaştırır.

**Nasıl kapanır.** `bundle_unreadable` satırına en azından ex.Message'ın path içermeyen kısaltmasını veya sabit bir aşama etiketi (`stage=copy_base|stage=write_manifest`) ekle. Temizliği `catch { try { DeletePrivateTree(...); } catch (IOException cleanup) { Console.Error.WriteLine($"staging_cleanup_failed:{cleanup.GetType().Name}"); } throw; }` biçiminde sararak orijinal hatayı koru (sessiz yutma değil, ayrı kod ile raporlama).

### [Medium] `restore` alt komutu yalnız drill düzenine sabitlenmiş; gerçek felaket kurtarma için ne script ne de adım adım runbook var (RTO 120 dk iddiasına rağmen)

`infrastructure/backup/backup-entrypoint.sh:680-720 (`/restore-drill/wal-recovery-evidence.json`, `canonical=/restore-drill/pgdata`, `DISPOSABLE_RESTORE_ONLY`); infrastructure/backup/prepare-recovery.sh:7; docs/runbooks/backup-failure.md:1-24` · R10 · DOĞRULANMADI (yalnız üreten agent)

`restore_snapshot` kanıt dosyasını sabit `/restore-drill/wal-recovery-evidence.json`'a, PGDATA'yı sabit `/restore-drill/pgdata`'ya yazıyor; `prepare-recovery.sh:7` `[ "$data" = /restore-drill/pgdata ]` dışındaki her yolu 78 ile reddediyor; `SAYDIN_RESTORE_CONFIRM` yalnız `DISPOSABLE_RESTORE_ONLY` kabul ediyor ve `restore_target_guard.py` hedefi `<root>/work` leaf'ine kısıtlıyor. `docs/runbooks/backup-failure.md` gerçek kurtarma için yalnız düzyazı ("restore the base, replay WAL to the selected target timestamp") veriyor; tek bir komut, tek bir dosya yolu, tek bir hedef-volume adı yok. Bu runbook bu değişiklik setinde hiç güncellenmemiş (git status'ta yok), oysa yeni hata kodları (`restore_wal_recovery_point_stale`, `backup_wal_receiver_not_caught_up`, `backup_wal_highwater_probe_unavailable`, `backup_base_staging_capacity_insufficient`, `backup_physical_probe_lock_timeout`, `backup_repository_prune_deferred`) `SaydinBackupFailure`'ın runbook_url'i olan bu dosyada hiç geçmiyor.

**Neden birinci sınıf değil.** Aylık drill mükemmel otomatize edilmiş, ama gerçek kurtarma tamamen prova edilmemiş elle bir iş olarak kalıyor; 120 dakikalık RTO taahhüdü hiçbir zaman ölçülmüyor. Ayrıca alarm→runbook zinciri kopuk: en olası yeni hata kodlarının hiçbiri işaret edilen runbook'ta yok.

**Nasıl kapanır.** (a) `restore` alt komutunu parametrik hale getir: kanıt/PGDATA hedeflerini `SAYDIN_RESTORE_TARGET` altına taşı ve `SAYDIN_RESTORE_CONFIRM` için ikinci bir onay değeri (`PRODUCTION_RECOVERY_APPROVED_<incident-id>`) + ayrı bir hedef guard'ı tanımla; `prepare-recovery.sh`'in sabit yol kontrolünü de buna göre gevşet (yine de üretim volume'ünü reddederek). (b) `docs/runbooks/backup-failure.md`'ye yeni hata kodları tablosu (kod → anlam → ilk aksiyon) ve gerçek kurtarma için birebir kopyalanabilir komut dizisi ekle. (c) drill'de ölçülen uçtan uca süreyi receipt'e `elapsedSeconds` olarak yaz ve RTO'ya karşı raporla.

### [Medium] Deploy, rule/target envanterini doğruluyor ama watchdog alert'inin Alertmanager'a ulaştığını hiç kontrol etmiyor

`infrastructure/release/deploy-release.sh:277-296, 336-349; infrastructure/prometheus/rules/tls-runtime.yml:73-78` · R11 · CONFIRMED

Kural değerlendirme tarafı (rule health + envanter eşitliği) artık kanıtlanıyor; Prometheus→Alertmanager teslimatı ve route eşleşmesi kanıtlanmıyor ve tamamen manuel promotion prosedürüne bırakılmış. Otomatikleştirilebilir bir kontrol insan adımı olarak kalmış.

**Neden birinci sınıf değil.** Watchdog matcher'ı yanlış yazılırsa veya notification yolu bozulursa deploy 'passed' imzalanır ama hiçbir heartbeat dışarı çıkmaz; dead-man's-switch'in kendisi sessizce ölü olur.

**Nasıl kapanır.** Monitoring runtime kapısına ekle: (a) `wget -qO- 'http://alertmanager:9093/api/v2/alerts?filter=alertname%3D%22SaydinWatchdog%22'` yanıtında tam bir aktif alert; (b) `/api/v2/alerts/groups?receiver=external-watchdog` içinde SaydinWatchdog'un görünmesi; (c) `prometheus_notifications_dropped_total` / `prometheus_notifications_errors_total` değerlerinin deploy penceresinde artmamış olması. Üçü de mevcut `until` döngüsüne oturur.

### [Medium] Ingestion kapalı ortamlarda iki critical alert kalıcı olarak firing kalıyor

`infrastructure/prometheus/rules/ingestion.yml:4-10, 56-62; infrastructure/release/deploy-release.sh:317-334; infrastructure/alertmanager/alertmanager.template.yml:18-20; .github/workflows/deploy-staging.yml:58` · R11 · CONFIRMED

İddia doğru: ingestion kapalıyken tam olarak iki critical alert kalıcı firing olur ve operator-critical receiver'ına yarım saatte bir gider.

**Neden birinci sınıf değil.** Critical route'un sinyal/gürültü oranı bozulur; ekip critical bildirimleri susturmayı öğrenirse gerçek backup/API/activity-log critical'ları kaçırılır. Alternatif, sona erdiğinde kimsenin fark etmeyeceği kalıcı bir silence'tır.

**Nasıl kapanır.** Kuralları ortama göre yükle (ör. `rules/optional-ingestion/` dizinini yalnız ingestion açıkken mount et ve validate-prometheus-runtime'ın beklenen alert envanterini aynı koşula bağla) ya da ifadeleri bir 'ingestion beklenir mi' sinyaline bağla. En azından deploy-release.sh ingestion kapalıyken bu iki alert için süreli bir Alertmanager silence açsın ve runbook bunu belgelesin.

### [Medium] Monitoring düzleminin kendi sağlığı hiçbir alert tarafından izlenmiyor

`infrastructure/prometheus/rules/*.yml (40 alert); infrastructure/deployment/validate-prometheus-runtime.py:39-52; infrastructure/prometheus/prometheus.production.yml:66-72` · R11 · CONFIRMED

İddia doğru. SaydinWatchdog yalnız 'tüm zincir ölü' durumunu yakalar; 'tek kural unhealthy' veya 'tek receiver 5xx' durumunda watchdog kendi route'undan teslim edilmeye devam ettiği için hiçbir sinyal üretilmez.

**Neden birinci sınıf değil.** Backup, activity-log ve API critical'ları, monitoring düzlemi kısmen bozukken sessizce kaybolabilir; deploy anındaki tek seferlik rule-health kontrolü bunu kapatmaz.

**Nasıl kapanır.** `rules/monitoring-self.yml` ekle: `increase(prometheus_rule_evaluation_failures_total[10m]) > 0`, `increase(prometheus_notifications_errors_total[10m]) > 0`, `increase(prometheus_notifications_dropped_total[10m]) > 0`, `increase(alertmanager_notifications_failed_total[15m]) > 0`, `increase(prometheus_tsdb_compactions_failed_total[1h]) > 0`. Her biri için inventory.test.yml'e pozitif + negatif test ve telemetry-pipeline.md'ye bir bölüm ekle.

### [Medium] Denetim bütünlüğü sinyali üreten `saydin_activity_log_data_truncations_total` hiçbir alert tarafından tüketilmiyor

`src/Saydin.Shared/Diagnostics/SaydinMetrics.cs:161-179; src/Saydin.Api/Helpers/ActivityLogBuilder.cs:114; infrastructure/prometheus/rules/api.yml:38-45` · R11 · CONFIRMED

Bulgunun asıl geçerli yarısı truncation sayacıdır: kodun kendi yorumu bu sayacı 'sessiz kaybın tek göstergesi' olarak tanımlıyor ama hiçbir kural onu tüketmiyor. Security admission oranı için alert yokluğu bir kusur değil, savunulabilir bir tercihtir; öneriden çıkarılmalı ya da açık bir muafiyet olarak yazılmalıdır.

**Neden birinci sınıf değil.** JSONB boyut sınırını aşan bir payload sürümü activity_logs `data` alanını sessizce placeholder'a düşürür; ADR-006 denetim izi eksik yazılır ve bu yalnız log arkeolojisiyle fark edilir.

**Nasıl kapanır.** `increase(saydin_activity_log_data_truncations_total{job="saydin-api"}[30m]) > 0` (warning, activity-logging runbook'una bağlı) kuralını ekle ve inventory.test.yml'e pozitif+negatif test yaz. Daha kalıcısı: validate-observability.py'ye 'SaydinMetrics.cs'deki her metrik ya bir alert ifadesinde ya da yazılı bir muafiyet listesinde geçmeli' kapısı — security admission metriği o listeye gerekçesiyle girsin.

### [Medium] Release tedarik zincirinin statik kapısı (`validate-release.py`) ve CI-admission self-test'i yalnız release workflow'unda koşuyor

`.github/workflows/release-images.yml:56-57 (tek çağrı yeri); karşı taraf: .github/workflows/ci.yml:96-131` · R12 · CONFIRMED

Olgular doğru ama sınıflandırma fazla ağır. Bu bir 'defect/High' değil, shift-left eksikliği: regresyon release anında fail-closed yakalanıyor (release-images.yml:46 'Fail-closed static and mutation gates' adımı build/imza öncesi koşuyor), yani yanlış bir deploy üretilmiyor. Kayıp, geri bildirimin PR'dan release penceresine kaymasıdır.

**Neden birinci sınıf değil.** deploy-release.sh'e tekrar inline runtime-image sözlüğü konması veya monitoring admission sırasının bozulması PR CI'ında görünmez; hata ancak self-hosted release runner'da release başlatıldığında ortaya çıkar ve release penceresini yakar. Orijinal Critical'ın tekrar oluşma yolu 'merge edilebilir' kalır.

**Nasıl kapanır.** `production-assurance` job'ına iki satır ekle: `python3 infrastructure/release/validate-release.py` ve `python3 .github/scripts/test-verify-release-ci-admission.py`. Ardından `validate-workflows.py`'nin mevcut token kontrolü listesine (satır 70-77) bu iki komutu ekle ki adım geri çıkarılamasın.

### [Medium] Yeni release image'ı `src/Saydin.DataRepair/Dockerfile` required CI'da hiç build edilmiyor

`.github/workflows/release-images.yml:124; karşı taraf: .github/workflows/ci.yml:944-1004` · R12 · CONFIRMED

Kaydın DataRepair yarısı doğru, Caddy yarısı YANLIŞ: `infrastructure/deployment/Dockerfile.caddy` required `production-assurance` job'ında `validate-production-assets.sh:93-95` tarafından gerçekten build ediliyor. Sekiz release Dockerfile'ından yalnız biri (DataRepair) required CI'da hiç derlenmiyor.

**Neden birinci sınıf değil.** Yeni first-party release artefaktı için build-doğruluğu geri bildirimi PR'dan release penceresine kayıyor; DataRepair Dockerfile'ındaki bir COPY/lock/publish kırılması ancak self-hosted release runner'da, imzalama/SBOM öncesi ortaya çıkar ve release'i yarıda keser.

**Nasıl kapanır.** `docker-build` job'ına `src/Saydin.DataRepair/Dockerfile` için bir `docker/build-push-action` adımı ekle (push:false, load:true, `type=gha` cache scope'u ile). Daha kalıcısı: `validate-workflows.py`'ye 'release matrisindeki her Dockerfile, ci.yml docker-build'de veya validate-production-assets.sh'de build ediliyor' kontrolü koy.

### [Medium] Permanent-blocked scope yalnız Critical log üretiyor; metrik/alarm yok, dolayısıyla testlenebilir bir gözlemlenebilirlik sözleşmesi de yok

`src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:256-262, :296-310; src/Saydin.Shared/Diagnostics/SaydinMetrics.cs:83; infrastructure/prometheus/rules/ingestion.yml:49-50` · R14a · CONFIRMED

Kalıcı olarak izole edilmiş bir ingestion scope'u için yayılan tek sinyal LogCritical'dır; calendar-not-ready yolunun aksine sayaç ve Prometheus alert kuralı yoktur. Bu hem operasyonel tespiti log aggregation'a bağımlı kılar hem de davranışın metrik tabanlı bir regresyon testinin yazılmasını imkânsızlaştırır.

**Neden birinci sınıf değil.** Bir asset'in provider credential'ı kalıcı bozulduğunda süreç ayakta, health yeşil ve calendar_not_ready sayacı 0 kalır; durmuş lane yalnız dolaylı olarak (saydin_ingestion_lag büyümesi) ve gecikmeli fark edilir. Kurtarma imzalı DataRepair `requeue_permanent_window` gerektirdiğinden erken tespit değerlidir.

**Nasıl kapanır.** SaydinMetrics'e `saydin_ingestion_permanent_blocked_total{source,scope,code}` sayacı (tercihen aktif blocked scope sayısı için bir gauge) ekle ve RecordPermanentBlocked ile PersistTypedFailureAsync'in permanent dalında artır. infrastructure/prometheus/rules/ingestion.yml'a alert, tests/inventory.test.yml'a unit-test kuralı ekle. Worker testinde MeterListener ile etiketleri doğrula.

### [Medium] data-repair.md, deploy-release.sh ve validate-production.sh'in güvenlik-kritik preflight'ını markdown içine kopyalıyor

`docs/runbooks/data-repair.md:16-110 · infrastructure/deployment/validate-production.sh:19-25 · infrastructure/release/deploy-release.sh:84-121` · R15 · CONFIRMED

data-repair.md yaklaşık 90 satır güvenlik-kritik shell'i (production config render + validate-production + helper-image çıkarımı + üç volume kontratı doğrulaması) script'lerden kopyalayarak inline taşıyor; bu kopya hiçbir self-test tarafından kapsanmıyor ve script'lerle senkron kalacağını garanti eden bir kapı yok.

**Neden birinci sınıf değil.** Güvenlik preflight'ının ikinci, test edilmeyen bir kopyası var; deploy-release.sh'teki sandbox bayrakları ya da validator argümanları güncellendiğinde sapma fark edilmeden production repair admission'ını zayıflatabilir.

**Nasıl kapanır.** `infrastructure/release/verify-repair-admission.sh` adında tek bir script çıkar (manifest verify + env verify-existing + config render + validate-production + üç volume kontratı + release-binding kontrolü), diğer doğrulayıcılar gibi bir self-test ekle ve runbook'u `verify-repair-admission.sh "$RELEASE_DIR" "$ENV_FILE"` tek satırına indir.

### [Medium] Dev backup login'i 60 günde sessizce sona eriyor; dev tarafı yenileme prosedürü belgelenmemiş

`docker-compose.yml:166-167 (database-identity), 279-280 (--backup-v1-valid-until), 325 (SAYDIN_BACKUP_V1_VALID_UNTIL)` · R16 · DOĞRULANMADI (yalnız üreten agent)

`database-identity` `SAYDIN_BACKUP_V1_VALID_UNTIL` değerini `((clock_timestamp() AT TIME ZONE 'UTC')::date + 60)` olarak üretip `.env.database-runtime`'a yazıyor; bu dosya kalıcı ve gitignored. RoleBootstrapDatabaseOperations.cs:795-801 `VerifyBackupIsolationAndAvailabilityAsync` içinde `if (backups.All(role => role.ValidUntilUtc!.Value < now.AddHours(24))) throw TopologyRejected("backup_role_rotation_horizon_insufficient")`. Yani metadata üretiminden ~59 gün sonra her `docker compose up` post-migration bootstrap'ta bu kodla durur ve `saydin-api` dahil hiçbir downstream servis başlamaz. `docs/runbooks/backup-login-renewal.md` yalnız production imzalı deployment akışını anlatıyor ("Update SAYDIN_BACKUP_V1_VALID_UNTIL in the non-secret production configuration and run the normal signed deployment"); `docs/development-guide.md` ve README'de dev için "bootstrap-dev-database.sh'i yeniden çalıştır" adımı hiç yok. Ek karışıklık: `database-identity` profilsiz olduğu için her `docker compose up`'ta yeniden çalışıp log'a *kullanılmayan* yeni bir tarih basıyor; log'daki değer fiilen kullanılan değerden farklı.

**Neden birinci sınıf değil.** Zaman bombası niteliğinde dev ortam arızası; teşhis edilmesi zor, çünkü hata mesajı yenileme yolunu göstermiyor ve doküman araması boş dönüyor. Uzun süreli dallarda / yeni katılan geliştiricide tekrarlanabilir.

**Nasıl kapanır.** (1) `docs/development-guide.md`'ye "Backup login süresi doldu" başlığı ekle: `backup_role_rotation_horizon_insufficient` görüldüğünde `./infrastructure/secrets/bootstrap-dev-database.sh` yeniden çalıştırılır (bootstrap tarihi ileriye taşır, `ExtendManagedBackupValidityAsync` forward-only uzatmayı destekler). (2) `bootstrap-dev-database.sh`'a mevcut `.env.database-runtime` içindeki tarih 30 günden yakınsa uyarı bas. (3) `database-identity` servisini `devtools`/dedicated bir profile al ya da adını `database-identity-oneshot` yapıp `docker compose up`'ın default setinden çıkar — böylece log'daki yanıltıcı ikinci tarih üretilmez.

### [Medium] Permanent-blocked ingestion lane'i için operatör kurtarma yolu alarm→runbook zincirinde yok

`src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs (RecordPermanentBlocked); docs/runbooks/ingestion-stale.md; docs/runbooks/data-repair.md; infrastructure/prometheus/rules/ingestion.yml (runbook_url)` · R17 · CONFIRMED

İddia doğru ve aslında bir adım daha kötü: yalnız `ingestion-stale.md` permanent-window kurtarmasını anlatmıyor değil, `data-repair.md` de `requeue_permanent_window` planını hiç anmıyor. Alarmdan kurtarma prosedürüne giden hiçbir doküman yolu yok; plan tipi yalnız kaynak kodda (SignedRepairPlan/RepairDatabase) ve testlerde adlandırılmış.

**Neden birinci sınıf değil.** Tek bir asset'in permanent window izolasyonu nedeniyle çalan SaydinDailyIngestionStale alarmında nöbetçi operatör, durumun ne olduğunu ve tek çıkış yolunun imzalı DataRepair planı olduğunu runbook zincirinden öğrenemez; MTTR uzar ve runbook adım 2'nin yasakladığı worker restart'ı denenmeye açıktır.

**Nasıl kapanır.** (a) SaydinMetrics'e bounded label'lı (`source`,`job_type`,`outcome_code`) `saydin_ingestion_permanent_blocked` sayacı ekle ve ayrı critical alert tanımla. (b) ingestion-stale.md'ye `ingestion_windows.state='permanent_failed'` teşhis sorgusu ve data-repair.md'ye link içeren bir adım ekle. (c) data-repair.md'de `requeue_permanent_window` planını açıkça belgele. (d) check-doc-links.py'a alert runbook'unun ilgili kurtarma runbook'una link verdiğini doğrulayan kural ekle.

### [Medium] Yeni security admission metriği ölçülüyor ama alarma, zero-init'e ve runtime kontrat doğrulamasına bağlanmadı

`src/Saydin.Shared/Diagnostics/SaydinMetrics.cs:172-195; infrastructure/prometheus/rules/api.yml (tamamı); infrastructure/deployment/validate-prometheus-runtime.py:81-88; docs/runbooks/api-availability.md:14; docs/runbooks/redis-unavailable.md:11` · R18 · DOĞRULANMADI (yalnız üreten agent)

`SaydinMetrics.SecurityAdmissionDecisions` eklendi ve iki runbook operatöre `saydin_security_admission_decisions_total{outcome="unavailable"}` serisini `bucket,reason` ile gruplamasını söylüyor. Ancak: (1) `infrastructure/prometheus/rules/api.yml` içinde bu metriğe dayanan tek bir alert yok — 5 alert'in hiçbiri admission'a bakmıyor; (2) aynı commit'te eklenen `SaydinMetrics.InitializeActivityLogContractSeries()` activity-log sayaçlarını sıfır değerle materyalize ediyor ama admission sayacını etmiyor; (3) `validate-prometheus-runtime.py:81-84` zorunlu seri/label listesinde `saydin_activity_log_*` ve `saydin_process_start_time_seconds` var, `saydin_security_admission_decisions_total` yok.

**Neden birinci sınıf değil.** Yeni fail-closed kapı için 'bu tetiklenirse ne yapılır' cevabı yazıldı ama operatörü oraya götürecek sinyal yok. Metriğin adı/label şeması canlı scrape admission'ında doğrulanmadığı için sessizce yanlış adla yayınlansa fark edilmez.

**Nasıl kapanır.** (1) `api.yml`'ye iki alert ekle: `SaydinSecurityAdmissionUnavailable` (`sum(increase(saydin_security_admission_decisions_total{outcome="unavailable"}[10m])) > 0`, severity critical, runbook_url=api-availability.md) ve `SaydinSecurityAdmissionLimitedSpike` (warning, runbook_url=api-availability.md); `inventory.test.yml`'ye her ikisi için pozitif ve negatif fixture ekle. (2) `InitializeActivityLogContractSeries`'i `InitializeMetricContractSeries` olarak genelleştirip `SecurityAdmissionDecisions.Add(0, bucket=network, outcome=allowed, reason=allowed)` sıfır serisini de yayınla. (3) `validate-prometheus-runtime.py:81` sözlüğüne `"saydin_security_admission_decisions_total": {"job","bucket","outcome","reason"}` ekle. (4) `SaydinApiErrorBudgetBurn` runbook_url'ini admission bölümüne çapa ile yönlendir.

### [Medium] Production limiter değerleri validate-production.py'de tam eşitlikle sabitlenmiş — kullanılabilirlik ayarı operatöre kapalı

`infrastructure/deployment/validate-production.py:224-232; infrastructure/deployment/compose.production.yml:196-200; src/Saydin.Api/Security/DistributedSecurityLimiterOptions.cs:25-38` · R18 · DOĞRULANMADI (yalnız üreten agent)

`validate-production.py:224-232` `security_limits` sözlüğünde beş limiti string olarak birebir pinliyor (`"3"`,`"5"`,`"20"`,`"100"`,`"500"`) ve `any(api_env.get(key) != expected ...)` ise `security_limiter_production_limits_invalid` ile reddediyor. Buna karşılık `DistributedSecurityLimiterOptions.IsValid` zaten aralık ve sıralama (hourly ≤ daily, exact ≤ network) invariant'larını doğruluyor — yani kod tarafında güvenli bir aralık sözleşmesi mevcut.

**Neden birinci sınıf değil.** Bir kullanılabilirlik/kapasite ayarı, statik bir eşitlik assert'iyle donduruldu. Fail-closed niyet doğru ama araç yanlış: acil durumda limitin yükseltilmesi bir doğrulama script'i düzenlemesini gerektiriyor.

**Nasıl kapanır.** Eşitlik yerine bounded aralık + invariant doğrula: `3 <= RegistrationExactHourlyLimit <= RegistrationExactDailyLimit <= 100`, `RegistrationNetworkHourlyLimit <= RegistrationNetworkDailyLimit`, `100 <= CalculationNetworkDailyLimit <= 100000` gibi. Böylece 'limiter kapatılamaz / anlamsız değere çekilemez' güvencesi korunurken operatör olay sırasında tavanı yükseltebilir. Değişikliğin izlenebilir kalması için değerleri `saydin_security_limit_configured{bucket=...}` gauge'u olarak yayınla ve runbook'ta 'limit değiştirildiyse metrikten doğrula' adımı ekle.

### [Low] Kayıt olayları activity_logs'ta principal pseudonym'i taşımıyor

`src/Saydin.Api/Endpoints/InstallationEndpoints.cs:53-60; src/Saydin.Api/Helpers/ActivityLogBuilder.cs:92-94; karşılaştır EndpointExtensions.cs:56-58,153-155; infrastructure/postgres/migrations/022_principal_retention.sql:81-82` · R02 · CONFIRMED

RegisterAsync, filtrelerin yaptığı `http.Items[PrincipalActivityIdItemKey]` yazımını atlıyor; sonuçta `installation_register` satırları `device_id='unknown'` ile yazılan tek action tipi oluyor. Ancak bu satırlarda `user_id` dolu ve pseudonym zaten principal id'den türediği için korelasyon kaybı yok; silme sonrasında da her iki alan redakte edildiğinden 'kalıcı kimliksizleşme' iddiası geçerli değil. Bulgu bir tutarlılık/ergonomi kusuru, veri kaybı değil.

**Neden birinci sınıf değil.** Audit sorgularında action tipleri arasında tutarsız kolon değeri; pseudonym üzerinden filtre kuran her sorgu registration olaylarını sessizce dışarıda bırakır ve üç yerde tekrarlanan aynı iki satırlık atama, dördüncü bir giriş noktasında yine unutulabilir.

**Nasıl kapanır.** RegisterAsync'te principal context set edildiği yerde `http.Items[EndpointExtensions.PrincipalActivityIdItemKey] = pseudonymizer.Pseudonymize(registered.PrincipalId)` yaz ve bu tekrarı tek bir `SetResolvedPrincipal(http, principal)` yardımcısına çek (iki filtre + register). Aynı metottaki artık gereksiz `.WithUserId(...)` çağrısını ve kullanılmayan `IInstallationPrincipalContext principalContext` parametresini de kaldır.

### [Low] `action` metric etiketi için üç farklı kelime dağarcığı dolaşıyor (unknown / other / calculation)

`src/Saydin.Api/Services/ActivityLogChannelTelemetry.cs:70-71; src/Saydin.Shared/Diagnostics/SaydinMetrics.cs:186-195; infrastructure/prometheus/tests/inventory.test.yml:18; src/Saydin.Api/Helpers/ActivityLogBuilder.cs:17` · R03 · CONFIRMED

Allowlist dışı action için runtime `unknown`, sözleşme serisi `other`, promtool fixture'ı ise `calculation` etiketi kullanıyor; ayrıca 'unknown' sabiti iki ayrı dosyada bağımsız tanımlanmış. Fixture gerçek etiket uzayını temsil etmiyor ve sözleşme serisi gerçek üretimi doğrulamıyor.

**Neden birinci sınıf değil.** Operatörün `action="other"` üzerine kurduğu pano gerçek drop'ları (`action="unknown"`) göstermez; label admission testi de üretimde hiç oluşmayacak bir seriyi doğrular.

**Nasıl kapanır.** Tek bir paylaşılan sabit tanımla (ör. `ActivityActions.UnknownTag = "unknown"`); NormalizeAction, ActivityLogBuilder fallback'i, InitializeActivityLogContractSeries ve prometheus fixture'ları bu tek değeri kullansın.

### [Low] Cache dil damgası ihlali graceful cache-skip yerine 500 üretiyor

`src/Saydin.Api/Services/CalculationCacheEntries.cs:41-44, 64-65, 105-108, 130-131, 170-175, 198-199, 224-227; src/Saydin.Api/Program.cs:323-328` · R04 · CONFIRMED

Aynen iddia edildiği gibi: savunma amaçlı bir invariant ihlali cache'i atlamak yerine hesaplama isteğini 500'e düşürüyor. Bugün `SupportedCultures` tr/en olduğu için tetiklenmiyor; `IsLanguage` ile `Program.cs`'teki liste arasında hiçbir derleme/başlangıç bağı yok.

**Neden birinci sınıf değil.** Dil listesine iki harfli ISO kodu olmayan tek bir kültür eklemek (ör. `fil`) tüm hesaplama endpoint'lerini, hesap doğru yapılmış olmasına rağmen, cache yazımında 500'e düşürebilir.

**Nasıl kapanır.** Damga doğrulamasını iki katmana ayır: okuma tarafında fail-closed (cache miss) kalsın; yazma tarafında geçersiz dil için exception yerine null döndürüp `TrySetAsync`'i atla. Ya da `IsLanguage`'i `Program.cs`'teki desteklenen kültür listesinden türeterek tek sabit üzerinden bağla.

### [Low] SaydinMetrics static initializer'ı Process.GetCurrentProcess()'e bağlı; başarısız olursa tüm metrik tipi kullanılamaz hale gelir

`src/Saydin.Shared/Diagnostics/SaydinMetrics.cs:17-35, 186-195; src/Saydin.Api/Program.cs (RunAsync öncesi InitializeActivityLogContractSeries çağrısı)` · R04 · PLAUSIBLE

Salt gözlemlenebilirlik amaçlı bir gauge için tip static initializer'ında korumasız process I/O yapılıyor. Saydin.Api'de bu initializer başlangıçta açıkça tetiklendiği için sonuç fail-fast başlatma hatası olur; Saydin.PriceIngestion'da böyle bir açık tetikleyici olmadığı için ilk metrik dokunuşunda `TypeInitializationException` üretebilir. Tetikleyici container profiline bağlı ve repo içinden doğrulanamaz.

**Neden birinci sınıf değil.** Düşük olasılıklı ama yüksek maliyetli bir başarısızlık modu: gözlemlenebilirlik detayı, ürün yolunun (veya başlatmanın) hard-fail nedeni olabiliyor ve hata `TypeInitializationException` olarak göründüğü için teşhisi zor.

**Nasıl kapanır.** `GetProcessStartTimeUnixSeconds`'i try/catch ile sar, başarısızlıkta 0 veya `Environment.TickCount64` türevi bir yaklaşık değere düş; veya `Lazy<long>` kullanarak tip initializer'ının hiç I/O yapmamasını sağla.

### [Low] Bulk fiyat yolunda authority-eksik gözlem sessizce 'veri yok'a düşüyor; tekil yol aynı durumda 404 fırlatıyor

`src/Saydin.Api/Services/AssetService.cs:187-217 (özellikle 208-213) ve karşılaştırma için 156-185 (206-207 satırları)` · R04 · CONFIRMED (doğrulayıcı ek bulgusu)

`GetNearestPriceAsync` (tekil): `if (!FinalObservationAuthority.IsCompleteFinal(price)) throw new PriceNotFoundException(symbol, date);` — savunma derinliği ihlali hata olarak yüzeye çıkıyor. `GetNearestPricesAsync` (bulk) ise git diff ile bu değişiklikte şu hale getirildi: `result[index] = point is not null && FinalObservationAuthority.IsCompleteFinal(point) ? point : null;` — hiçbir log, metrik veya ayrım yok. `AssetService`'e ILogger enjekte edilmiş olmasına rağmen bu dalda kullanılmıyor. Repository zaten SQL tarafında aynı authority predicate'ini uyguladığı (PriceRepository.cs:165-186) için buradan null dönen bir 'complete olmayan' satır ancak repository/SQL predicate'i ile C# predicate'inin ayrışması demektir — yani gerçek bir kontrat bug'ı.

**Neden birinci sınıf değil.** Savunma-derinliği ihlali telemetriye hiç yansımıyor; sessiz veri kaybı 'kullanıcının seçtiği tarihte fiyat yok' gibi görünen normal bir duruma karışıyor. Aynı zamanda `/calculate` ile `/dca` arasında tutarsız arıza modu.

**Nasıl kapanır.** Bulk döngüde `point is not null && !IsCompleteFinal(point)` durumunu ayır: `logger.LogWarning("Bulk nearest-price authority ihlali: {Symbol} {Date}", ...)` ile logla ve `SaydinMetrics`'e ayrı bir sayaç ekle (mevcut `PriceNotFoundCount`'tan ayrı). SQL ve C# predicate'lerinin ayrışmadığını doğrulayan bir integration testi ekle.

### [Low] TwelveData'da ayrıştırılamayan String değer sessizce satır düşürüyor, aynı alanın Object/Null olması typed contract exception fırlatıyor — eşdeğer bozukluklar iki farklı terminal koda gidiyor

`src/Saydin.PriceIngestion/Mappers/TwelveDataMapper.cs:66-68,115-119; src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:333-347` · R06 · CONFIRMED (doğrulayıcı ek bulgusu)

`"close": "N/A"` → `ProviderValueParser.TryReadDecimal` String dalında `decimal.TryParse` false döner → `continue` (satır 67-68), satır sessizce düşer. `"close": {"bad":true}` → aynı metot `contract_value_kind_invalid` fırlatır → adapter `PermanentFailure(ex.Code)` (TwelveDataAdapter.cs:102-107). Sessizce düşen satır ise yalnız `TryValidateSuccess` (BaseAssetWorker.cs:333-347) `distinct.SetEquals(expected)` kontrolüne takılıp jenerik `worker_completeness_rejected` kodu üretir ve hangi alanın/hangi günün bozuk olduğu hiçbir yere yazılmaz.

**Neden birinci sınıf değil.** Terminal durum her iki yolda da PermanentFailed olduğu için veri riski yok; kayıp teşhiste: operatör `worker_completeness_rejected` kodundan sağlayıcı payload'ının hangi noktada bozulduğunu göremez ve payload'ı elle incelemek zorunda kalır. Bu diff value-kind taksonomisini tipleştirirken bu yolu tipsiz bıraktı.

**Nasıl kapanır.** Satır-düzeyi reddi de tipleştir: mapper reddedilen satır sayısını ve ilk reddedilen alan/tarihi bir sonuç nesnesiyle döndürsün, adapter bunu `PartialRejected(..., "contract_unparseable_value", $"field=close;date=…")` olarak yansıtsın. Alternatif olarak `TryReadDecimal` başarısızlığında da typed exception fırlat ve tek bir taksonomi kullan.

### [Low] Preflight timeout geri-yükleme hatası sessizce yutuluyor ve gerekçe yorumu gerçeği yansıtmıyor

`src/Saydin.DatabaseMigrator/MigrationImpactPreflight.cs:183-187,153-165; src/Saydin.DatabaseMigrator/MigrationRunner.cs:257,266,290` · R07 · CONFIRMED

Geri-yükleme hatası gerçekten sessizce yutuluyor ve yorumdaki gerekçe ('session close is the authoritative reset fallback') yanlış, çünkü preflight sonrası bağlantı kapanmıyor; aynı oturum tüm migration uygulama döngüsünde kullanılıyor. Fakat iddia edilen etki gerçekleşmez: her migration gövdesi kendi transaction'ında `SET LOCAL lock_timeout/statement_timeout` uyguluyor (MigrationRunner.cs:3040-3050, OnlineMigrationExecutor.cs:634-648) ve impact bütçeleri zaten runner bütçelerinden gevşek olamaz. Sızan değerler yalnız transaction dışı kontrol-düzlemi sorgularını etkiler ve daima daha sıkıdır.

**Neden birinci sınıf değil.** Gözlemlenebilirlik kaybı: geri-yükleme başarısızlığı hiçbir yerde iz bırakmaz ve kod okuyan biri yorumdan yanlış bir yaşam döngüsü modeli çıkarır. Fonksiyonel etki, transaction dışı kontrol sorgularının sızmış (daha sıkı) bütçeyle çalışmasıyla sınırlı.

**Nasıl kapanır.** Yutmayı koru ama sessiz bırakma: `output`/`error` TextWriter'ına `impact preflight timeout restore failed: sqlstate=...` gibi bounded bir satır yaz. Yorumu gerçek davranışa göre düzelt — bağlantı paylaşılıyor ve asıl telafi mekanizması her transaction'daki `SET LOCAL` yeniden yazımıdır; bunu yorumda açıkça belirt.

### [Low] Blank DB'de ertelenen impact preflight, aynı reddi managed DB'den farklı bir control state ile sonuçlandırıyor

`src/Saydin.DatabaseMigrator/MigrationRunner.cs:163,243,260,285-292,334,340` · R07 · CONFIRMED

`impactPreflightInProgress`, preflight reddinin control state'i kirletmesini önlemek için eklenmiş bir invariant'tır ve ertelenen (blank-DB) preflight çağrısı bu invariant'ın dışında kalıyor: :288-292'deki çağrı bayrak false iken ve `SetControlStateAsync("bootstrapping")` sonrasında çalıştığı için bütçe reddi `state='failed'` bırakır. Etki finder'ın ima ettiğinden küçüktür — `failed` durumu yeniden denemeyi bloklamaz (ValidateManagedStateAsync:1225 yalnız `ready` ile çelişkiyi reddeder) ve blank yolda state zaten `bootstrapping`'e taşınmıştır.

**Neden birinci sınıf değil.** Aynı bütçe reddi iki hedef sınıfında farklı control state izi bırakır; runbook/CI çıktısında ve operatör teşhisinde tutarsız görünür. Fonksiyonel kurtarma etkilenmez.

**Nasıl kapanır.** Ertelenen çağrıyı da `impactPreflightInProgress` true/false sarmalına al — ya da daha temizi, bayrak yönetimini `VerifyImpactPreflightAsync`'in kendi içine taşı ki her iki çağrı yeri otomatik olarak invariant'a uysun. Alternatif olarak ertelenen yolda `failed` bırakmanın kasıtlı olduğunu koda yorum ve runbook satırı olarak yaz.

### [Low] `rotate` idempotent-tekrar davranışı yeni secret ile denendiğinde opak `login_authentication_failed` üretiyor

`src/Saydin.DatabaseRoleBootstrap/RoleBootstrapRunner.cs:451-458; src/Saydin.DatabaseRoleBootstrap/RoleBootstrapDatabaseOperations.cs:126-148; docs/runbooks/database-role-credential-lifecycle.md:20-22` · R07 · CONFIRMED

Idempotent-tekrar yolunda `EnsureRoleAsync` mevcut rolü marker eşleştiği için hiç değiştirmiyor (parola yeniden yazılmıyor), ardından auth probe yeni dosyadaki parolayla çalışıyor. Rol commit edildikten sonra secret yeniden üretilirse aynı `rotate` komutu kalıcı olarak `login_authentication_failed` döndürür ve doğru kurtarma (`reset-password --login-version <n>`) ne stderr'da ne de bu hata koduna eşlenmiş bir runbook satırında geçiyor. Runbook idempotent davranışı belgeliyor (:20-22) ama tanılamayı kurtarmaya bağlamıyor.

**Neden birinci sınıf değil.** Operatör parola dosyasının bozulduğunu sanarak aynı komutu tekrarlar ve döngüye girer; olay anında zaman kaybı. Fail-closed olduğu için güvenlik veya veri riski yok.

**Nasıl kapanır.** Idempotent-tekrar yolunda auth probe başarısız olduğunda ayrı bir kod döndür (`rotate_repeat_password_mismatch`) ve stderr'da tek satırlık `reset-password` yönlendirmesi ver. `database-role-credential-lifecycle.md`'nin 'Rotate to the next version' bölümüne, reset-password bölümündekine (:48-52) denk bir 'probe başarısız olursa' kurtarma dalı ekle.

### [Low] Promote edilmeyen günlük candidate'ler için retention/temizlik yok; her koşu 245 kaynaklı tam bundle'ı yeniden replay ediyor

`infrastructure/calendar/run-acquisition.sh:56-110; tools/calendar-data/src/Saydin.CalendarData/CalendarPlanMaterializer.cs:18` · R09 · DOĞRULANMADI (yalnız üreten agent)

Başarılı her koşu `$staging/candidate-cal-tcmb-<gün>` dizini bırakır; yalnız `promote-reviewed-bundle.sh` promote EDİLEN candidate'ı siler. İnsan imzası gerektiren bir akışta günlük candidate birikimi sınırsızdır ve her candidate ~245 snapshot içerir (base'den kopyalanan tüm HTML/PDF'ler dahil). Ayrıca `MaterializeTcmb` yalnız plan üretmek için `CalendarDataGenerator.LoadVerified(baseDataRoot)` çağırarak 20 yıllık TCMB arşivinin + 3 PDF'in tam replay'ini yapar; bu, `--memory 256m` sınırlı bir container'da her gün tekrarlanır.

**Neden birinci sınıf değil.** Disk dolması sessizce acquisition'ı düşürebilir (ve R09-05 nedeniyle `bundle_unreadable` olarak görünür); tam replay CPU/bellek bütçesini gereksiz tüketir. Runbook'ta retention politikası yok.

**Nasıl kapanır.** `run-acquisition.sh`'a sınırlı retention ekle (ör. en yeni N candidate'ı tut, daha eskisini `rm -rf` ile temizle veya en azından sayıyı stdout kontratına yaz) ve runbook'ta retention/temizlik adımını belgele. Materialize adımında tam replay yerine yalnız manifest okuma + hash doğrulama yeterliyse hafif bir yol ekle, aksi halde replay maliyetini README'de bilinçli bir güvence olarak gerekçelendir.

### [Low] `ValidateTcmbPolicy` literal metin eşleşmelerine dayanıyor, politika kaynağı hiçbir planla yenilenmiyor ve 16:30 cutoff sabiti bu kanıta bağlanmamış

`tools/calendar-data/src/Saydin.CalendarData/CalendarDataGenerator.cs:276-283; tools/calendar-data/src/Saydin.CalendarData/CalendarPlanMaterializer.cs:50-57` · R09 · DOĞRULANMADI (yalnız üreten agent)

`text.Contains("15.30")`, `text.Contains("16.00-16.30")` ve `text.Contains("resmi tatiller, hafta sonları ve yarım gün", OrdinalIgnoreCase)` — TCMB SSS sayfasının birebir ifadelerine bağlı. Bu kontrol HER `Generate`/`verify` çağrısında (yani her materialize, her acquire, her promotion replay'inde) çalışır. Materializer ise planına `tcmb-policy-faq` kaynağını hiç eklemez; snapshot taban bundle'dan taşınır ve asla tazelenmez. Ayrı olarak `TcmbProviderCutoff` 16:30'u hardcode eder ve doğrulanan politika metniyle programatik bir bağı yoktur.

**Neden birinci sınıf değil.** Fail-closed tarafı doğru ama kırılgan; "kanıta bağlı cutoff" iddiası pratikte hiç yenilenmeyen bir snapshot'a ve elle senkronize edilmesi gereken bir sabite dayanıyor.

**Nasıl kapanır.** Politika sayfasını da (örn. haftalık/aylık) plan üzerinden tazele ve değiştiğinde fail-closed ol; cutoff saatini metinden türetilmiş bir değere ya da en azından politika snapshot hash'ine bağlı bir sabite dönüştür (hash değişirse `tcmb_policy_review_required` üret). Bu kontratı `tools/calendar-data/README.md`'de açıkça yaz.

### [Low] `saydin_backup_wal_last_segment_timestamp_seconds` yayınlanıyor ama hiçbir alarm/dashboard/runbook onu kullanmıyor; tek kural testi "alarm ÜRETMEMELİ" diyor

`infrastructure/backup/backup-entrypoint.sh:264; infrastructure/prometheus/rules/host-backup.yml (metriğe hiç referans yok); infrastructure/prometheus/tests/rules.test.yml:96-106` · R10 · DOĞRULANMADI (yalnız üreten agent)

Metrik `write_wal_recovery_metric` içinde yazılıyor. `grep -rn saydin_backup_wal_last_segment_timestamp_seconds` sonucu: yalnız entrypoint, README, static self-test, base-backup smoke ve `rules.test.yml`'deki `quiet_database_caught_up_wal_observation_stays_fresh` grubu — o grup da metriği sabit `0x40` verip `SaydinWalBackupStale`'in BOŞ kalmasını doğruluyor. `host-backup.yml`'de metriğe dayanan tek bir kural yok. Önceki review'in M34 önerisi metrik + alarm istiyordu; metrik geldi, alarm gelmedi.

**Neden birinci sınıf değil.** Sessiz-veritabanı tasarımı (`recovery_timestamp = max(source, observed-300)`) savunulabilir ama denetlenemez: gerçek segment yaşı hiçbir yerde görünmüyor, yalnız tazelik iddiası görünüyor. Grafana/rehber olmadan metrik ölü ağırlık.

**Nasıl kapanır.** Bilgilendirici (warning, uzun `for`) bir kural ekle: ör. `time() - saydin_backup_wal_last_segment_timestamp_seconds > 86400` → "WAL segmenti 24 saattir dönmedi — beklenen sessizlik mi, yoksa archive_timeout/checkpointer sorunu mu?" ve `docs/runbooks/backup-failure.md`'de nasıl ayırt edileceğini yaz. Alternatif olarak metriği bir Grafana panelinde `kind="wal"` ile yan yana göster.

### [Low] Restore yolunda restic cache hâlâ tmpfs'te (`/tmp` 512 MiB) — base tarafında tmpfs'ten çıkarılan aynı risk restore'da duruyor

`infrastructure/backup/backup-entrypoint.sh:682 (`configure_cache /tmp/restic-cache`); infrastructure/backup/restore-drill.sh:228-238 (`--tmpfs /tmp:...size=512m`)` · R10 · DOĞRULANMADI (yalnız üreten agent)

Base backup yolunda cache `configure_cache "$base_staging_root/restic-cache"` ile diske taşındı (satır 386) ve `/tmp` 2 GiB'dan 64 MiB'a indirildi — CH-14'ün düzeltmesi. Restore yolunda ise `restore_snapshot` hâlâ `configure_cache /tmp/restic-cache` diyor ve fetch container'ı `--tmpfs /tmp:uid=1001,gid=1001,mode=0700,size=512m` ile koşuyor; restore edilen veri disk-backed volume'e gitse de restic index cache'i RAM'de. Fetch container'ında `mem_limit` de yok, yani sınır yalnız tmpfs boyutu.

**Neden birinci sınıf değil.** Restore/drill, base backup'ın az önce düzeltilen aynı kök nedeniyle (tmpfs'te sınırlı alan) başarısız olabilir; asimetri, düzeltmenin kök nedeni değil tek bir çağrı yerini kapattığını gösteriyor.

**Nasıl kapanır.** `restore_snapshot`'ta cache'i `$SAYDIN_RESTORE_TARGET`'ın bulunduğu disk-backed volume altına al (ör. `/restore-drill/restic-cache`, guard'ın izin verdiği bir leaf) ve `restore-drill.sh`'te `/tmp` tmpfs'ini 64 MiB'a indir. `backup-static-self-test.py`'ye "restore yolunda `/tmp` altında cache yok" mutasyon kontrolü ekle.

### [Low] Her release `SaydinProcessRestarted` üretiyor; deploy otomasyonu silence açmıyor, runbook deploy'u beklenen neden saymıyor

`infrastructure/prometheus/rules/tls-runtime.yml:61-70; infrastructure/release/deploy-release.sh:277-279, 300; docs/runbooks/container-restart.md:3-14` · R11 · CONFIRMED

İddia olgusal olarak doğru ancak etkisi finder'ın çizdiğinden dar: deploy başına tek bir warning grubu, 15 dakika sonra otomatik resolve. Asıl kusur alarm hacmi değil, runbook'un bu beklenen olayı 'release blocker' diliyle karşılaması.

**Neden birinci sınıf değil.** Operatör her deploy'da runbook'un incident adımlarını işletmeye davet ediliyor; rutin olarak yok saymayı öğrendiğinde gerçek crash-loop restart'ları da görünmez olur.

**Nasıl kapanır.** deploy-release.sh ve rollback-release.sh, monitoring/API recreate'inden hemen önce `amtool silence add alertname=SaydinProcessRestarted --duration=20m --comment="deploy $SAYDIN_DEPLOYMENT_ID"` açsın (alertmanager container'ından, `--alertmanager.url=http://127.0.0.1:9093`) ve silence id'sini receipt'e yazsın. container-restart.md'ye 'aktif bir deployment_id silence'ı varsa bu beklenen bir olaydır' adımını ekle.

### [Low] Observability game-day matrisi 40 alert'in 11'ini kapsıyor ve dead-man's-switch yokluk provası içermiyor

`docs/runbooks/observability-game-day.md:6-18; infrastructure/alertmanager/README.md:14-16` · R11 · CONFIRMED

Eleştiri 'matris 40'ın 11'ini kapsıyor' genel iddiası olarak zayıf; asıl geçerli nokta, bu commit'in eklediği yeni garantilerin (watchdog yokluk provası ve backup login expiry) matriste hiç yer almamasıdır.

**Neden birinci sınıf değil.** Yeni eklenen dead-man's-switch'in uçtan uca değeri kanıtlanmamış kalır; dış DMS servisinin eşiği yanlış ayarlanmış veya entegrasyonu susturulmuş olabilir ve bu ancak gerçek bir kesintide anlaşılır.

**Nasıl kapanır.** Tabloya en az şunları ekle: 'Prometheus'u durdur → dış watchdog servisi operatör penceresinde sayfalar', 'Activity log writer'ı toxic-row moduna zorla → SaydinActivityLogLoss', 'Backup login VALID UNTIL fixture'ını yaklaştır → SaydinBackupLoginExpiring', 'Collector export'unu bozup kuyruğu doldur → TelemetryQueueNearCapacity'.

### [Low] Zamanlanmış restore drill elle bakılan bir repo değişkenine bağlı ve başarısızlığında bildirim yolu yok

`.github/workflows/restore-drill.yml:3-5,27-47; karşı taraf: .github/workflows/promote-production.yml:116-118` · R12 · CONFIRMED

Olgular doğru, ama Medium değil Low: (1) GitHub zamanlanmış workflow başarısızlıklarında workflow dosyasının son commit sahibine e-posta gönderir, yani 'hiçbir bildirim yolu yok' tam doğru değil — eksik olan repo içinde tanımlı, sahiplenilmiş bir alarm yoludur; (2) sonuç fail-closed: bayat/eksik değişken drill'i patlatır, promotion ise 31 gün sonra kendiliğinden bloke olur, yani sessiz bir DR yanılsaması oluşmaz.

**Neden birinci sınıf değil.** DR kanıt üretimi 'otomatik' görünürken elle bakım gerektiriyor; drill'in ölmesi ancak bir sonraki promotion denemesinde (≤31 gün) fark edilir ve o an release penceresini bloke eder.

**Nasıl kapanır.** (a) Scheduled dalda tag'i `gh release list --limit 1 --json tagName` ile en son imzalı release'ten türet, `vars`'ı yalnız override bırak; (b) job'a `if: failure()` bir adım ekleyip `gh issue create` ile etiketli issue aç; (c) `docs/runbooks/restore-drill.md`'ye '31 gün eşiği ve promotion blokajı' notunu yaz.

### [Low] `IngestionFreshnessHydrationService.KnownStreams` orchestrator worker kaydını elle tekrarlıyor; yeni unit test dosyası yalnız exception yolunu kapsıyor

`src/Saydin.PriceIngestion/Workers/IngestionFreshnessHydrationService.cs:12-19, :53-65 ↔ src/Saydin.PriceIngestion/Workers/IngestionOrchestrator.cs:34-40; tests/Saydin.PriceIngestion.Tests/Workers/IngestionFreshnessHydrationServiceTests.cs:1-46` · R14a · CONFIRMED

Beklenen freshness stream kataloğu ile orchestrator worker kaydı iki ayrı elle bakımlı listedir ve eşitlikleri hiçbir testle korunmaz; ayrıca yeni test dosyası RefreshAsync'in asıl işi olan enabled-bayrağa göre stream seçimini hiç kapsamaz.

**Neden birinci sınıf değil.** Yeni bir provider worker'ı orchestrator'a eklenip KnownStreams'e eklenmezse o kaynak için `saydin_ingestion_last_success_timestamp_seconds` / `saydin_ingestion_lag_seconds` serileri hiç yayılmaz; kaynak sessizce alarm kapsamı dışında kalır ve ingestion durduğunda stale-data alarmı tetiklenmez.

**Nasıl kapanır.** İki listeyi tek bir `IngestionStreamCatalog` sabitinden türet, ya da isim kümelerinin eşit olduğunu assert eden bir unit test ekle. Ayrıca RefreshAsync için (a) yalnız `Enabled=true` worker'ların expected stream'e dönüştüğünü, (b) disabled worker'ın listeden düştüğünü doğrulayan iki test yaz.

### [Low] `docker run` başarısızlıkları script'lerin machine-readable red sözleşmesini atlıyor; replay divergence ile Docker altyapı hatası ayırt edilemiyor

`infrastructure/calendar/verify-candidate.sh:4-7, :81-90; infrastructure/calendar/run-acquisition.sh:4-7, :43-54, :69-78` · R14a · CONFIRMED (doğrulayıcı ek bulgusu)

verify-candidate.sh'te her red yolu `fail()` üzerinden `calendar_candidate_rejected:<code>` yazıp exit 65 döner (satır 4-7). Buna karşılık script'in TEK gerçek kanıt üretme adımı olan `docker run ... verify --data-root /candidate` (satır 81-90) `fail()` ile sarılmamıştır; `set -e` nedeniyle script docker'ın ham exit kodunu döndürür ve hiçbir `calendar_candidate_rejected:` satırı yazılmaz. Aynı desen run-acquisition.sh:45-53 (materialize-plan) ve :70-75 (candidate yeniden doğrulama) için de geçerlidir. Sonuç: offline replay divergence (imaj exit 2), imajın çekilememesi (docker exit 125), docker binary'sinin bulunmaması (127) ve OOM-kill (137) çağıran açısından ayırt edilemez ve hiçbiri tipli red kodu üretmez.

**Neden birinci sınıf değil.** Calendar admission zincirinin en önemli red gerekçesi (deterministik replay uyuşmazlığı) log aggregation'da tipli bir kodla görünmez; operatör altyapı arızası ile kanıt reddini ayıramaz ve runbook'un marker tabanlı teşhis akışı bu tek noktada çalışmaz.

**Nasıl kapanır.** `docker run` çağrılarını `|| fail "offline_replay_divergent"` benzeri sarmalayıcılarla koru ve docker'ın 125/126/127 gibi altyapı kodlarını ayrı bir koda (`container_runtime_unavailable`) eşle; promotion tarafında da verifier çağrısının exit 65 (kanıt reddi) ile diğer kodları ayırıp uygun `calendar_promotion_rejected:` kodunu yaz.

### [Low] Fault-injection seam'leri imzalı repair binary'sine derleniyor; maximumGuardRows operatöre açık değil

`src/Saydin.DataRepair/Program.cs:8-27; src/Saydin.DataRepair/RepairDatabase.cs:31-51,54,628-635; src/Saydin.DataRepair/README.md` · R14b · PLAUSIBLE

Seam'ler gerçekten shipped assembly'de duruyor, fakat hepsi `internal` + `InternalsVisibleTo` ile sınırlı ve `Main` yolundan erişilemez; 'imzalı araç transaction ortasında keyfi SQL çalıştırma noktası taşıyor' çerçevesi savunma-derinliği açısından anlamlı bir kayıp değildir ve tek başına bulgu sayılmaz. Kaydın ayakta kalan kısmı operasyoneldir: `maximumGuardRows` 100.000 sabit, CLI'dan ayarlanamıyor ve aşım kodu `repair_guard_row_budget_exceeded` hiçbir dokümanda/runbook'ta açıklanmıyor — operatör bu hatayla karşılaştığında ne yapacağını bilmiyor.

**Neden birinci sınıf değil.** Guard satır bütçesi aşıldığında operatörün elinde ne belgelenmiş bir prosedür ne de bir ayar düğmesi var; tek çıkış kaynağı değiştirip yeniden derlemek. Seam'lerin varlığı ise yalnız 'yalnız test' garantisinin sözleşmeye değil konvansiyona dayanması bakımından not edilebilir.

**Nasıl kapanır.** src/Saydin.DataRepair/README.md'ye `repair_guard_row_budget_exceeded` için bir operatör bölümü ekle: bütçenin ne olduğu, neden var olduğu ve aşıldığında planın nasıl bölüneceği. Bütçeyi imzalı plan alanına taşımak (böylece imza kapsamında kalır) CLI bayrağından daha güvenlidir. Seam'ler için ayrı bir aksiyon şart değil; istenirse `#if REPAIR_TEST_SEAMS` ile koşullu derleme değerlendirilebilir.

### [Low] docs/runbooks/README.md bir indeks değil: runbook'ların çoğu linkli değil, alert→runbook eşlemesi yok

`docs/runbooks/README.md (tamamı) · docs/README.md:20 · infrastructure/prometheus/rules/*.yml` · R15 · CONFIRMED

docs/runbooks/README.md 23 runbook'un yalnız 5'ini (bu changeset'te eklenenleri) düz metin içinde linkliyor ve alarm→runbook eşleme tablosu içermiyor; alarmların kendi `runbook_url` alanları doğru olduğundan olay anındaki yol çalışır durumda, eksik olan indeks/envanter işlevidir.

**Neden birinci sınıf değil.** Operasyonel yüzeyin keşfedilebilirliği zayıf; yeni bir on-call kişisi kapsamı ancak dosya listesine bakarak öğrenir. Yeni runbook eklendiğinde indeks güncellenmediği için sessizce sapıyor (bu changeset'te de öyle oldu).

**Nasıl kapanır.** README'ye `| Runbook | Tetikleyen alarm(lar) | Kapsam |` tablosu ekle (23 satır) ve `.github/scripts/check-doc-links.py` yanına küçük bir kapı koy: her `docs/runbooks/*.md` README'de linklenmiş olmalı ve her alert kuralının `runbook_url`'i o tabloda geçmeli.

### [Low] backup-login-renewal.md hangi dosyanın düzenleneceğini söylemiyor

`docs/runbooks/backup-login-renewal.md adım 2 · infrastructure/release/render-deployment-env.py:87-93,113-118 · .github/workflows/promote-production.yml:121-128 · infrastructure/release/deploy-release.sh:243-244` · R15 · PLAUSIBLE

Runbook adım 2 hangi dosyanın düzenleneceğini adıyla söylemiyor ('non-secret production configuration'); doğru hedef operatör sahipli taban env (`vars.SAYDIN_PRODUCTION_ENV_FILE`) olup rendered dosya her promosyonda ondan yeniden üretilir. render-deployment-env.py bu anahtarı ne yeniden yazar ne de doğrular, dolayısıyla yanlış dosyanın düzenlenmesini yakalayan bir kapı yoktur — ancak kanonik akışta kalıcı bir rendered dosya bulunmadığı için pratik risk finder'ın anlattığından düşüktür.

**Neden birinci sınıf değil.** Belirsiz talimat, backup login geçerlilik uzatmasının yanlış dosyada yapılmasına ve tekrar edilmesi gereken bir deploy'a yol açabilir; kalıcı rendered env kullanılan manuel akışlarda değer sessizce eski hâline dönebilir.

**Nasıl kapanır.** Adım 2'de dosyayı adıyla yaz (operatör tabanı = `vars.SAYDIN_PRODUCTION_ENV_FILE`; rendered env manuel akışta `render-deployment-env.py --base ... --manifest ... --output ...` ile yeniden üretilir). İstersen deploy-release.sh'e base ile rendered arasındaki `SAYDIN_BACKUP_V1_VALID_UNTIL` eşitliğini doğrulayan ucuz bir kontrol ekle.

### [Low] `database-backup-hba` opak hata kodlarıyla fail-closed oluyor ve dev'de prod `hostssl` yolunu hiç denemiyor

`docker-compose.yml:193-259; infrastructure/backup/manage_backup_hba.py:163-170` · R16 · DOĞRULANMADI (yalnız üreten agent)

Gömülü Python `raise SystemExit('development_backup_subnet_invalid')` / `'development_backup_subnet_mask_invalid'` ile çıkıyor; ne tespit edilen rotalar ne container IP'si loglanıyor, ne de düzeltme ipucu var. Kabul aralığı `network.is_private and 16 <= network.prefixlen <= 28` ve `len(matches) != 1` şartı, geliştirici kendi Docker `default-address-pool`'unu değiştirdiğinde veya container ek bir network'e bağlandığında sessizce ihlal edilebilir. Ayrıca `--fixture-cleartext` bayrağı `manage_backup_hba.py:170`'te kural türünü `hostssl` yerine `host`'a çeviriyor; yani dev, production'ın SSL zorunlu replication yolunu hiçbir zaman çalıştırmıyor.

**Neden birinci sınıf değil.** Kurtarılabilir bir ortam farkı, teşhis edilemeyen bir başlangıç arızasına dönüşür. `--fixture-cleartext` farkı ise HBA sözleşmesindeki bir prod regresyonunun lokalde yakalanamayacağı anlamına gelir (kapsam yalnız `backup-hba-self-test.py`'a kalır).

**Nasıl kapanır.** Hata yollarında bulunan rotaları/adresi stderr'e bas (`print(address, matches, file=sys.stderr)`) ve mesaja "SAYDIN dev ağını tek bir /16-/28 private subnet'e sabitleyin" ipucunu ekle. `--fixture-cleartext` ile prod arasındaki farkı `infrastructure/backup/README.md` içinde açık bir tablo olarak belgeleyip `backup-hba-self-test.py`'ın her iki kural türünü de fixture'ladığını doğrula.

### [Low] ActivityPrincipalPseudonymizer, gizli dosya reddedilme nedenini yutup tek bir genel mesaja indirgiyor

`src/Saydin.Api/Services/ActivityPrincipalPseudonymizer.cs:32-56,77-78` · R18 · DOĞRULANMADI (yalnız üreten agent)

`SecureSecretFile.ReadBytes(..., rejectionCode: "activity_principal_pseudonym_secret_invalid")` çağrısının fırlattığı `DatabaseSecurityRejectedException` (ve `IOException`/`UnauthorizedAccessException`) yakalanıp hepsi tek bir `InvalidOperationException("Activity principal pseudonym secret file is invalid.")`'a çevriliyor. Ne özgün rejectionCode, ne iç exception, ne dosya yolu korunuyor; `catch` blokları hiçbir şey loglamıyor.

**Neden birinci sınıf değil.** Başlatma-zamanı fail-closed davranışı doğru ama teşhis edilemez. Deploy sırasında dakikalar 'hangi invariant kırıldı' sorusuna gidiyor; benzer secret dosyaları (installation-keyring.json, security-limiter-hmac) için de aynı sınıf hata olduğundan yanlış dosyaya bakma riski var.

**Nasıl kapanır.** `SecureSecretFile`'ın `rejectionCode`'unu koru: `catch (DatabaseSecurityRejectedException exception) => throw InvalidSecret(exception.Code)` ve mesajı `$"Activity principal pseudonym secret rejected: code={code}"` yap (kod stabil bir enum-benzeri string, değer sızdırmaz). `IOException`/`UnauthorizedAccessException` için de ayrı kodlar (`secret_io_error`, `secret_access_denied`) üret. Başlatma yolunda `ILogger` mevcutsa aynı kodu `LogCritical` ile parametreli olarak yaz. Aynı düzeltmeyi diğer secret yükleyicilerde de uygula ve `docs/deployment/README.md`'ye secret rejection kodu → aksiyon tablosu ekle.

---

## Geliştirici deneyimi (25 kayıt)

### [Medium] Action allowlist üç yerde tekrarlanıyor, aralarında parite kapısı yok; EF modeli DB'de artık var olmayan bir CHECK'i tarif ediyor

`src/Saydin.Shared/Constants/ActivityActions.cs:33-48; src/Saydin.Shared/Data/Configurations/ActivityLogConfiguration.cs:12-22; infrastructure/postgres/migrations/023_installation_lifecycle_admission.sql:157-162,278; tests/Saydin.DatabaseMigrator.Tests/MigrationRunnerIntegrationTests.cs:1758-1790` · R03 · CONFIRMED

Action allowlist artık üç bağımsız yerde tekrarlanıyor (C# sabiti, 023'teki plpgsql trigger dizisi, EF'in artık var olmayan chk_activity_action modeli) ve hiçbir otomatik parite kapısı yok. EF'teki HasCheckConstraint runtime'ı etkilemez (EF migration kullanılmıyor), dolayısıyla doğrudan bir hata değil; asıl kusur, 'ActivityActions.All ile birebir aynı kalmalı' yorumunun hiçbir mekanizmayla desteklenmemesi ve modelin gerçek şemayla çelişmesidir.

**Neden birinci sınıf değil.** Bir geliştirici ActivityActions'a 16. action ekleyip trigger'ı güncellemeyi unutursa derleme ve unit testler geçer; üretimde o action'ın her satırı 23514 alır, ToxicRow olarak bisection'a girer (50'lik batch için ~2N ek DB round-trip'i), satır satır düşürülür ve yalnız `toxic_row` sayacı + critical alarm ile geriye dönük fark edilir. Denetim izinde o özellik hiç görünmez.

**Nasıl kapanır.** Gerçek-PG integration testinde `ActivityActions.All`'ı döngüyle INSERT edip hepsinin kabul edildiğini, listede olmayan bir değerin reddedildiğini doğrula (literal listeler yerine sabitten türet). ActivityLogConfiguration'daki chk_activity_action modelini kaldır veya 'artık trigger ile enforce ediliyor' yorumuyla açıkla; chk_activity_data_size predicate'indeki 10000 literalini ActivityLogLimits.DataMaxBytes'tan üret.

### [Medium] Program.cs'teki 30 saniyelik client.Timeout ayarları ölü konfigürasyon

`src/Saydin.PriceIngestion/Program.cs:84,92,101,111,121; src/Saydin.PriceIngestion/Extensions/HttpResilienceExtensions.cs:25` · R06 · CONFIRMED

Beş HTTP client kaydındaki `client.Timeout = TimeSpan.FromSeconds(30)` ölü konfigürasyondur: `AddSaydinResilience` sonradan `ConfigureHttpClient` ile `Timeout.InfiniteTimeSpan` yazar ve HttpClientActions kayıt sırasıyla uygulandığı için Infinite kazanır. Ezme hiçbir yorumla belgelenmemiş.

**Neden birinci sınıf değil.** Etkin davranış kayıt sırasına bağlı ve okunan dosyada görünmüyor. Bir okuyucu 30 s'yi geçerli sanıp CLAUDE.md'nin timeout zorunluluğunu karşılanmış sayar; gerçekte bütçe tamamen Polly pipeline'ındadır. Sıra tersine dönecek bir refactor'da `ResponseHeadersRead` ile okunan gövde 30 s'lik `HttpClient.Timeout`'a takılır, attempt/total timeout'larla çakışır ve `provider_deadline` sözleşmesi bozulur.

**Nasıl kapanır.** `Program.cs`'teki beş `client.Timeout = TimeSpan.FromSeconds(30)` satırını sil; `HttpResilienceExtensions.cs:25`'in yanına "bütçe Polly pipeline'ındadır; `HttpClient.Timeout` bilinçli olarak devre dışı" yorumunu ekle. Ezmenin uygulandığını doğrulayan bir test yaz (`CreateClient("tcmb").Timeout == Timeout.InfiniteTimeSpan`).

### [Medium] Dev backup login validity bootstrap anında donduruluyor; 60 gün sonra tüm dev stack başlamaz ve çözüm hiçbir yerde yazmıyor

`infrastructure/secrets/bootstrap-dev-database.sh:66,78-83; docker-compose.yml:165-167; src/Saydin.DatabaseRoleBootstrap/RoleBootstrapDatabaseOperations.cs:210-231,1183-1186; docs/development-guide.md:446,455` · R07 · CONFIRMED

Dev bootstrap, backup login'in 60 günlük geçerlilik damgasını `.env.database-runtime`'a bir kez yazar ve bu dosya yalnız `bootstrap-dev-database.sh` yeniden çalıştırıldığında tazelenir. Değer değişmediği sürece `ExtendManagedBackupValidityAsync` hiç tetiklenmez ve `VerifyRoleAttributes` süre dolumunu görmez; kırılma `AuthenticateBackupAsync`'te `backup_authentication_failed` olarak yüzeye çıkar ve `service_completed_successfully` zinciri yüzünden api/ingestion/migrator hiç başlamaz. development-guide.md §10 bu senaryoyu anmıyor ve aynı bölümdeki `-U saydin` örnekleri de artık geçersiz (`POSTGRES_USER: saydin_admin`).

**Neden birinci sınıf değil.** Depoya 60+ gün sonra dönen bir geliştirici için dokümante edilmiş dev akışı kendiliğinden bozulur; hata kodu (`backup_authentication_failed`) ile çözüm (`bootstrap-dev-database.sh`'i tekrar çalıştır) arasında hiçbir bağ yoktur. Yeni bir katılımcı bunu secret bozulması sanıp volume silmeye yönelebilir; sorun giderme bölümündeki komutlar da yanlış kullanıcı adıyla ikinci bir yanlış ize götürür.

**Nasıl kapanır.** 1) `bootstrap-dev-database.sh`'e mevcut `.env.database-runtime`'daki `SAYDIN_BACKUP_V1_VALID_UNTIL` 30 günden yakınsa uyaran veya otomatik tazeleyen bir kontrol ekle (üretim runbook'undaki 30 günlük uyarı eşiğiyle aynı sabit). 2) `development-guide.md` §10'a 'role-bootstrap `backup_authentication_failed` ile çıkıyor → `./infrastructure/secrets/bootstrap-dev-database.sh` tekrar çalıştır' maddesini ekle. 3) Aynı bölümdeki `pg_isready -U saydin` ve `psql -U saydin` örneklerini `-U saydin_admin` olarak düzelt.

### [Medium] 11 target job envanteri yalnız Python sabitinde; prometheus.production.yml ile statik olarak bağlanmamış

`infrastructure/deployment/validate-prometheus-runtime.py:13-16; infrastructure/prometheus/prometheus.production.yml:17,23,27,31,35,39,43,47,51,66,70; infrastructure/deployment/validate-observability.py:184-200; infrastructure/deployment/monitoring-runtime-self-test.py:32-42` · R11 · CONFIRMED

İddia doğru. Ayrıca hatanın deploy'un en sonunda — monitoring, API ve Caddy zaten force-recreate/başlatılmışken — ortaya çıkması etkiyi keskinleştiriyor: yarım uygulanmış bir release ve manuel rollback gerektiriyor.

**Neden birinci sınıf değil.** Ucuz bir statik kontrolle CI'da yakalanabilecek bir tutarsızlık, en pahalı noktada (canlı deploy'un sonunda) ortaya çıkıyor.

**Nasıl kapanır.** validate-observability.py'ye prometheus.production.yml'deki `job_name` kümesinin `validate-prometheus-runtime.EXPECTED_JOBS` ile eşit olduğunu doğrulayan bir kapı ekle (kopyalayarak değil import ederek) ve observability-self-test.py'ye 'job eklendi/silindi → reddedilmeli' mutasyonu koy. `required_labels` anahtarlarından deploy-release.sh'in `match[]` regex'ini türet ya da regex'i validator'a taşı.

### [Medium] Migration sayısı hâlâ türetilmiyor: `26` sabiti 9 ayrı yerde elle tutuluyor ve doğrulayıcı literal'i literal ile karşılaştırıyor

`.github/workflows/ci.yml:615,622,629,636,640; .github/scripts/validate-workflows.py:92-95; tests/Saydin.PriceIngestion.IntegrationTests/IngestionDatabaseFixture.cs:65; tests/Saydin.DataRepair.IntegrationTests/RepairDatabaseFixture.cs:37; .github/scripts/run-development-compose-smoke.sh:178,183; tests/Saydin.DataRepair.IntegrationTests/run-isolated.sh:193` · R12 · CONFIRMED

İddia doğru ve R12-01 ile kanıtlanmış durumda. Not: `run-unit-coverage.sh:45-56` zaten doğru deseni gösteriyor (`find ... | sort` ile keşfedilen envanteri konfigüre edilmiş liste ile `diff` karşılaştırması) — yani repo bu türetmeyi başka yerde yapmayı biliyor, migration sayısında yapmıyor.

**Neden birinci sınıf değil.** Her yeni migration 9 dosyalık mekanik bir edit turu gerektiriyor; doğrulayıcı literal-literal karşılaştırdığı için birlikte bayatlamayı yakalayamıyor ve atlanan yerler (smoke, run-isolated) sessizce yanlış sayı bekliyor.

**Nasıl kapanır.** `validate-workflows.py` içinde sayıyı envanterden hesapla (`len([p for p in (root/'infrastructure/postgres/migrations').iterdir() if p.suffix in ('.sql','.sh')])`) ve ci.yml'deki literal'i bu değere göre üret/doğrula; ci.yml'de sayıyı job-level `env: SAYDIN_EXPECTED_MIGRATIONS` değişkenine taşı; test fixture'larını `MigrationTrustRoot.Versions.Count` üzerinden bağla; shell kapılarını `find`-türetmeli yap. `run-unit-coverage.sh`'deki envanter-diff desenini örnek al.

### [Medium] `production-assurance` 20 dakikalık timeout içinde soğuk tam .NET stack build'i + DB bootstrap smoke'u da koşuyor

`.github/workflows/ci.yml:96-99,112-113; .github/scripts/run-development-compose-smoke.sh:120-176` · R12 · PLAUSIBLE

Olgular doğrulandı (cache yok, aynı job'da ağır statik kapılar var, timeout 20 dk). Gerçek sürenin 20 dakikayı aşıp aşmadığı ancak koşuda ölçülebilir; bu nedenle kesin değil ama risk somut.

**Neden birinci sınıf değil.** Cache miss veya yavaş runner'da job 'timeout' ile iptal olur ve gerçek neden görünmez; her PR bu maliyeti öder. Kararsızlaşan yeni kapının tipik akıbeti `continue-on-error` veya timeout şişirmedir — kapının değeri erozyona uğrar.

**Nasıl kapanır.** Smoke'u ayrı bir job'a taşı (hızlı statik kapılar — validate-workflows, check-doc-links, release self-test'leri — smoke süresine bağlı kalmasın) ve o job'a ölçülmüş süreye göre timeout ver; ayrıca `COMPOSE_BAKE=true` + `type=gha` cache ile smoke build'ini `docker-build` scope'larıyla paylaştır.

### [Medium] Lokal unit kapısı (`tests` compose servisi) hiç ihtiyaç duymadığı tam DB bootstrap zincirine ve Redis'e bağlı — ve bu bağımlılık artık sözleşmeyle sabitlendi

`docker-compose.yml:477-500 (tests servisi); .github/scripts/validate-development-compose.py:13-16,153-157 (POST_BOOTSTRAP_CONSUMERS); .github/scripts/run-local-tests.sh:14-21; CLAUDE.md:31-32` · R12 · CONFIRMED (doğrulayıcı ek bulgusu)

`tests` servisi `depends_on: {database-role-bootstrap-post-migration: service_completed_successfully, redis: service_healthy}` ve `ConnectionStrings__Redis` env'i taşıyor. Ama aynı servisin entrypoint'i `run-local-tests.sh` ve o script satır 14-21'de `*Saydin.Services.sln*|*IntegrationTests*|*Saydin.DatabaseMigrator.Tests*` argümanlarını fail-closed reddediyor — yani bu servis içinde gerçek PG/Redis kullanan hiçbir suite koşamaz. `grep -rn 'ConnectionStrings__Redis' tests/` yalnız `tests/Saydin.Api.IntegrationTests/**` içinde eşleşiyor (RedisFixture.cs:19, ErrorContractHttpTests.cs:56, IntegrationTestEnvironment.cs:56); `ErrorContractWebAppFactory` de yalnız IntegrationTests projesinde. Kesin kanıt: CI'ın `build-and-test` job'ı (ci.yml:22-95) tam olarak aynı `run-unit-coverage.sh`'yi hiçbir PostgreSQL/Redis servisi olmadan, çıplak ubuntu runner'da koşuyor ve geçiyor — yani yedi unit projesinin hiçbiri DB/Redis'e ihtiyaç duymuyor. Buna rağmen `validate-development-compose.py:13-16` `tests`'i `POST_BOOTSTRAP_CONSUMERS` içine koyup post-bootstrap bağımlılığını required CI'da zorunlu kılıyor (mutasyon testi: `api_bypasses_post_bootstrap`).

**Neden birinci sınıf değil.** Repo'nun en sık çalıştırılan lokal kapısı, hiç kullanmadığı bir altyapıyı ayağa kaldırmak için dakikalar harcıyor ve `REDIS_PASSWORD` gibi ek ön koşullar dayatıyor; DB stack'i sağlıksızsa saf unit testleri hiç koşturulamıyor. Bağımlılık artık bir CI sözleşmesiyle sabitlendiği için kaldırılması ek bir kapı değişikliği gerektiriyor — yani yanlış varsayım kalıcılaştırılmış durumda.

**Nasıl kapanır.** `tests` servisinden `depends_on` ve `ConnectionStrings__Redis`'i kaldır (unit suite'inin hiçbiri kullanmıyor; CI bunu zaten kanıtlıyor) ve `validate-development-compose.py`'deki `POST_BOOTSTRAP_CONSUMERS` kümesinden `tests`'i çıkar. Gerçek PG/Redis gerektiren suite'ler zaten `run-local-tests.sh` tarafından reddediliyor ve kanonik yolları `.github/compose.integration.yml`. Böylece lokal unit döngüsü saniyeler mertebesine iner ve CLAUDE.md:31'deki vaat gerçeğe uyar.

### [Medium] Migration sayısı `26` fixture'larda ve CI'da 10+ yerde sabit; High #15'i üreten bayatlama sınıfı korunuyor

`tests/Saydin.PriceIngestion.IntegrationTests/IngestionDatabaseFixture.cs:64-69; tests/Saydin.DataRepair.IntegrationTests/RepairDatabaseFixture.cs:37; .github/workflows/ci.yml:615,622,629,636,640; .github/scripts/validate-workflows.py:92-95; karşılaştırma: tests/Saydin.PriceIngestion.IntegrationTests/PriceAuthorityMigrationIntegrationTests.cs:119-124` · R14a · CONFIRMED

Mevcut `26` değeri doğrudur; kusur değerin yanlışlığı değil, tek otoriteden türetilmemiş olmasıdır. Aynı sayı iki fixture, dört CI kapısı, bir CI özet satırı ve bir validate-workflows.py kuralı arasında elle senkron tutulmaktadır; 025 numaralı migration eklendiğinde required integration-test job'ı ingestion, DataRepair, DQA ve fresh-schema adımlarında aynı anda düşer ve düzeltme 10 ayrı satırın elle güncellenmesini gerektirir.

**Neden birinci sınıf değil.** Her migration eklemesi öngörülebilir bir "tüm CI kırmızı" turu üretir; bu, envanterdeki High #15'in (hard-coded `=23`) kök nedeninin aynısıdır ve remediation dokümanındaki kanıt tablosu bir sonraki migration'da yeniden bayatlar.

**Nasıl kapanır.** Fixture readiness probe'larını PriceAuthorityMigrationIntegrationTests'teki gibi "terminal olmayan migration yok + gerekli versiyonlar succeeded" invariant'ına çevir. CI tarafındaki bilinçli ratchet'i korumak isteniyorsa sayıyı tek bir `.github/scripts/schema-expectations.json` kaynağından oku ve hem ci.yml hem validate-workflows.py bu tek değeri kullansın; böylece migration eklemesi tek satırlık bir güncelleme olur.

### [Medium] 'Hafif lokal unit kapısı' olarak belgelenen `tests` servisi postgres + migrator + iki bootstrap + HBA + redis zincirini ayağa kaldırıyor

`docker-compose.yml:477-496 · .github/scripts/run-local-tests.sh:6-9 · .github/scripts/run-unit-coverage.sh:19-27,44-47 · CLAUDE.md:29-31 · docs/development-guide.md:231-242` · R15 · CONFIRMED

`tests` servisi varsayılan yolunda hiçbir şekilde PostgreSQL veya Redis kullanmadığı hâlde `depends_on` ile post-migration bootstrap zincirinin tamamına ve zorunlu `REDIS_PASSWORD` interpolasyonuna bağlanmıştır; dokümanların 'DB gerekmez' etiketi komutun gerçek maliyetiyle çelişir.

**Neden birinci sınıf değil.** Belirtilen ergonomi ile gerçek maliyet uyuşmuyor: tek bir unit test projesini çalıştırmak dakikalarca süren bir data-plane kurulumu tetikler ve bootstrap yapılmamış temiz bir checkout'ta hiç başlamaz. Commit kapısı gereksiz yere kırılgan hale gelmiş.

**Nasıl kapanır.** `tests` servisinden `depends_on` ve `ConnectionStrings__Redis`'i kaldır (varsayılan yol bunları kullanmıyor); Redis gerektiren bir senaryo kalırsa ayrı bir `tests-with-infra` servisi tanımla. En azından dokümanlarda `--no-deps` bayrağını göster ve development-guide.md:240'taki 'DB gerekmez' etiketini komutla tutarlı hale getir.

### [Medium] Salt-unit hâline gelen `tests` servisi hâlâ tüm veritabanı zincirine ve Redis'e bağlı

`docker-compose.yml:477-494` · R16 · DOĞRULANMADI (yalnız üreten agent)

Servisin entrypoint'i artık `run-local-tests.sh` ve argümansız çalışmada yalnız `run-unit-coverage.sh` (7 unit projesi, DB/Redis kullanmaz) koşuyor; servis yorumu da "purpose-specific DB credential taşımaz" diyor. Buna rağmen `depends_on: database-role-bootstrap-post-migration: service_completed_successfully` + `redis: service_healthy` korunmuş (satır 489-493) ve `ConnectionStrings__Redis` env'i (satır 485) hiçbir unit testte kullanılmıyor. Sonuç: `docker compose run --rm tests` postgres + secret-source-generator + secret-materializer + database-identity + database-backup-hba (pg_hba.conf mutasyonu + `pg_reload_conf()`) + iki role-bootstrap + migrator zincirini ayağa kaldırıyor.

**Neden birinci sınıf değil.** Commit öncesi kapı dakikalar sürüyor, DB stack'i sağlıklı değilse hiç çalışmıyor ve bir unit test koşusu üretim benzeri bir kontrol düzlemini mutasyona uğratıyor — bu üç etki de kapının kullanılmama olasılığını artırıyor. `--no-deps` çıkış yolu hiçbir dokümanda yazmıyor.

**Nasıl kapanır.** `tests` servisinden `depends_on` ve `ConnectionStrings__Redis`'i kaldır (integration kabulü zaten `.github/compose.integration.yml`'de). Gerçekten DB'li bir yerel deneme isteniyorsa bunu ayrı bir `tests-integration` servisine taşı. Geçici çözüm olarak CLAUDE.md/development-guide'a `--no-deps` bayrağını ekle ve `validate-development-compose.py`'a "unit test servisi DB'ye bağlı olmamalı" kuralını yaz.

### [Medium] Operatör CLI'ları `argument_required` gibi hangi argüman olduğunu söylemeyen kodlar döndürüyor; --help yok, kod→aksiyon tablosu yok

`src/Saydin.DataRepair/RepairOptions.cs:49,80,151,191-192; src/Saydin.DataRepair/Program.cs:85; src/Saydin.DataQualityAudit/AuditOptions.cs:75,144,167; src/Saydin.DataRepair/README.md; docs/runbooks/data-repair.md` · R18 · DOĞRULANMADI (yalnız üreten agent)

`RepairOptions.Parse` yedi zorunlu ortak anahtardan hangisi eksikse aynı `Invalid("argument_required")`'ı fırlatıyor (satır 80: `foreach (var key in CommonKeys) if (!values.ContainsKey(key)) throw Invalid("argument_required")`). `Program.cs:85` bunu `repair rejected: code=argument_required` olarak stderr'e yazıyor — başka hiçbir bağlam yok. `args.Length == 0` da aynı kodu üretiyor, yani `--help` yok. AuditOptions aynı deseni tekrarlıyor. Repo genelinde hiçbir `.sh`/CLI'da `--help`/`usage()` yok. `src/Saydin.DataRepair/README.md` ve `docs/runbooks/data-repair.md` çok detaylı ama hiçbirinde kod→anlam→aksiyon tablosu yok.

**Neden birinci sınıf değil.** En yüksek riskli, en az sık çalıştırılan araç (imzalı operatör onarımı) aynı zamanda en zayıf hata ergonomisine sahip. Olay sırasında dakikalar kaynak kod okumaya gider ve yanlış denemeler ekstra deneme-yanılma turu üretir.

**Nasıl kapanır.** (1) Kodu argüman adıyla zenginleştir — anahtar adları gizli değil: `Invalid($"argument_required:{key}")`. Aynısını `argument_invalid`, `signer_argument_mismatch` için de yap. (2) `args.Length == 0` veya `--help`/`-h` gelirse moda göre izin verilen anahtar listesini ve exit kod tablosunu stdout'a bas, exit 0 dön (calendar-data CLI'daki belgeli `Console` istisnasıyla aynı bounded sözleşme). (3) `src/Saydin.DataRepair/README.md`'ye ve `docs/runbooks/data-repair.md`'ye `code` → anlam → operatör aksiyonu tablosu ekle (`argument_required`, `command_unknown`, `repair_target_mismatch`, `repair_audit_identity_mismatch`, `postgres_*`, `database_transport`, `cancelled`). (4) Aynı iyileştirmeyi `Saydin.DataQualityAudit`'e uygula; iki araç aynı `Invalid(code)` desenini paylaştığı için ortak bir `CliRejection` helper'ına çıkarılabilir.

### [Low] Calculation-network admission opt-in bir bool parametre — yeni bir hesaplama endpoint'i sessizce kotasız kalır

`src/Saydin.Api/Endpoints/EndpointExtensions.cs:17-19,64-78; WhatIfEndpoints.cs:32,43,54; DcaEndpoints.cs:26` · R01 · CONFIRMED

Doğru ama etkisi sınırlı: bayrağı unutan yeni bir hesaplama endpoint'i tamamen korumasız kalmıyor — ağ ve principal kovaları uygulanıyor; yalnız günlük calculation-network bütçesinin dışında kalıyor. Asıl kusur, 'hesaplama endpoint'i' kümesinin tek tanımının bir bool bayrağın çağrı sitesi olması ve hiçbir test/yorumun bu kümeyi ActivityLogMiddleware.ResolveAction'daki aynı kümeye bağlamaması.

**Neden birinci sınıf değil.** Yeni bir pahalı hesaplama endpoint'i günlük ağ bütçesinden muaf kalır ve bu ne derlemede ne testte yakalanır.

**Nasıl kapanır.** Bayrağı adı kendini anlatan ayrı bir extension'a çıkar: `.RequireInstallationCredential().RequireCalculationAdmission()`. Ek olarak R01-05'te önerilen endpoint-enumerasyon testine bir kural ekle: ResolveAction'da WhatIf*/Dca action'ına eşlenen her endpoint calculation admission filtresini taşımalı.

### [Low] `login_password_missing` tanılama kodu ölü koda dönüştü

`src/Saydin.DatabaseRoleBootstrap/RoleBootstrapDatabaseOperations.cs:122-128` · R07 · CONFIRMED

Yeni eklenen canonical-verifier guard'ı (:122-124) `IsCanonical(null) == false` olduğu için null verifier durumunu da yakalıyor; hemen altındaki `login_password_missing` guard'ı (:126-128) artık erişilemez ölü koddur. Mevcut çağıranlarda gözlemlenebilir bir fark yoktur (null verifier yalnız non-Login veya passwordless scheduler rolleri için geçiliyor); kusur ileride bir login purpose'u eklenirken secret mapping unutulursa yüzeye çıkar.

**Neden birinci sınıf değil.** Spesifik ve doğrudan aksiyon aldıran kod ('secret dosyası verilmemiş') yerine genel `password_verifier_invalid` döner; operatör dosyanın var olduğunu ama içeriğinin bozuk olduğunu sanır. Ayrıca ölü kod, sözleşmenin iki ayrı hata kodu vaat ettiği izlenimini sürdürür.

**Nasıl kapanır.** Null kontrolünü canonical kontrolünün önüne al: `passwordVerifier is null` → `login_password_missing`, aksi halde `!IsCanonical(...)` → `password_verifier_invalid`. İki kodu ayrı ayrı doğrulayan birer unit test ekle (mevcut `DatabaseFailureCodeTests` bunun için doğal yer).

### [Low] `Single`/`SingleOrDefault` çağrıları bozuk girdide tipli CalendarDataException yerine yakalanmayan InvalidOperationException üretiyor

`tools/calendar-data/src/Saydin.CalendarData/CalendarDataGenerator.cs:135,194; tools/calendar-data/src/Saydin.CalendarData/CalendarAcquisition.cs:130,133` · R09 · DOĞRULANMADI (yalnız üreten agent)

`sources.SingleOrDefault(s => s.Kind == "tcmbPolicyFaq")` ve `"bistHolidayIndex"` — birden fazla eşleşmede `SingleOrDefault` `InvalidOperationException` fırlatır; `ValidateManifestShape` (CalendarDataGenerator.cs:319-334) kind bazında tekillik doğrulamaz. Benzer şekilde `plan.Calendars.Single(c => c.Code == TcmbCode)`: `ValidatePlan` yalnız `Calendars.Count != 2` kontrolü yapar, iki kaydın da `bist_pay_xist` olmasını engellemez → eşleşme yok → `InvalidOperationException`. Program.cs'in catch zinciri yalnız `CalendarDataException`, IO/Unauthorized, `DatabaseSecurityRejectedException` ve `NpgsqlException` yakalar.

**Neden birinci sınıf değil.** Bounded machine-readable stderr sözleşmesi kırılır: operatöre stabil kod yerine tam .NET stack trace (dosya yolları dahil) ve farklı bir exit kodu döner.

**Nasıl kapanır.** `SingleOrDefault` yerine `UniqueDictionary` benzeri kind-bazlı tekillik doğrulaması kullan (`tcmb_policy_source_duplicate`, `bist_index_source_duplicate`); `ValidatePlan`'a `plan.Calendars` kodlarının tam `{tcmb_indicative_fx, bist_pay_xist}` kümesi olduğu kontrolünü ekle.

### [Low] Konfigürasyonu doğrulayan monitoring imaj digest'leri elle sabit; release manifest'in runtimeImages'ıyla bağlı değil

`infrastructure/deployment/validate-production-assets.sh:8-12, 69-73; .github/workflows/release-images.yml:76-84; infrastructure/release/release_manifest.py:17-31; infrastructure/deployment/compose.production.yml:478` · R11 · CONFIRMED

İddia doğru. Kural birim testleri (promtool test rules) üretimde çalışacak Prometheus sürümüyle hiç koşmuyor; digest üç ayrı yerde elle senkron tutuluyor.

**Neden birinci sınıf değil.** Sessiz sürüm drift'i için açık kapı; kural testlerinin doğruladığı davranış ile üretimde çalışan binary'nin davranışı ayrışabilir.

**Nasıl kapanır.** Digest'leri tek kaynaktan türet (release_manifest.py'de export edilen bir sabit veya `infrastructure/release/runtime-images.lock.example.json`) ve validate-release.py'ye 'validation digest == manifest runtimeImages digest' kapısı ekle. Ek olarak deploy-release.sh'in monitoring aşamasına `compose run --rm --no-deps --entrypoint promtool prometheus test rules /etc/prometheus/tests/*.test.yml` adımını (test dosyalarını mount ederek) koy.

### [Low] TestDatabase.EnsureRolesAsync() artık yalnız backup login'ini kuruyor ama adı hâlâ 'roller' diyor

`tests/Saydin.DatabaseMigrator.Tests/IntegrationEnvironment.cs:344-375; çağıranlar: MigrationRunnerIntegrationTests.cs:344,380,676,677,2944; InstallationCredentialRehashMigrationIntegrationTests.cs:19` · R14b · CONFIRMED

Finder'ın tespiti doğru; çağıran dağılımını düzeltiyorum: `EnsureRolesAsync()` altı yerde, `EnsureRolesThroughApplicationAsync()` üç yerde çağrılıyor. `EnsureRolesAsync` artık yalnız backup v1 login'ini kuruyor (boş `PasswordFiles`), isim ise tüm managed rol grafiğini ima ediyor.

**Neden birinci sınıf değil.** İkinci okuyucu ve yeni test yazan geliştirici `EnsureRolesAsync()`'in tüm managed rolleri kurduğunu varsayar; gerçekte yalnız backup rolü kurulur ve test kurgusu sessizce eksik kalır. Ayrıca post-migration bootstrap zincirinin CLI/uygulama yolu daha az testte kapsanır.

**Nasıl kapanır.** Metodu yaptığı işe göre adlandır (`EnsureBackupLoginOnlyAsync`) ve `EnsureRolesThroughApplicationAsync`'i `EnsureRolesAsync` yap; her iki metoda hangi testin neden hangi varyanta ihtiyaç duyduğunu açıklayan tek satırlık XML doc ekle.

### [Low] Unit ratchet sayıları üç ayrı yerde elle senkron tutuluyor ve validator hatası eski değerleri gösteriyor

`.github/scripts/run-unit-coverage.sh:36; .github/scripts/validate-workflows.py:118-122; .github/workflows/ci.yml:652-812` · R14b · CONFIRMED (doğrulayıcı ek bulgusu)

`run-unit-coverage.sh:36` `minimum_tests=(641 179 96 77 98 28 92)` tanımlıyor; `validate-workflows.py:121` bu diziyi LİTERAL STRING olarak eşleştiriyor (`if "minimum_tests=(641 179 96 77 98 28 92)" not in unit_runner`), ci.yml ise integration TRX minimumlarını ayrıca taşıyor ve validate-workflows.py:77-91 onları da ayrı bir sözlükte pinliyor. Her yedi değerin de projelerin GERÇEK test sayısına birebir eşit olduğunu bağımsız saydım (RoleBootstrap 40+58=98, DQA 42+54=96, DataRepair 20+8=28, Calendar 31+61=92, Migrator unit 31+46=77, Migrator toplam 80+104=184). Ratchet'i meşru şekilde yükseltmek isteyen geliştirici scripti güncellediğinde `production-assurance` job'ı `unit_test_ratchet_missing:641,179,96,77,98,28,92` hatasıyla düşer — hata mesajı ESKİ değerleri sayar ve 'validator'ı da güncelle' demez.

**Neden birinci sınıf değil.** Test ekleme/çıkarma gibi rutin bir işlem iki-üç dosyada elle senkronizasyon gerektiriyor ve başarısızlık mesajı yanlış yöne işaret ediyor. Tek doğruluk kaynağı yok; ratchet'in kendisi bir kontrol olduğu halde kontrolün kontrolü kırılgan.

**Nasıl kapanır.** Ratchet değerlerini tek bir JSON'a (`.github/scripts/unit-test-ratchet.json`) taşı; `run-unit-coverage.sh` ve ci.yml adımları onu okusun, `validate-workflows.py` ise literal string eşleştirmek yerine bu JSON'u parse edip ci.yml/script ile tutarlılığını doğrulasın. En azından `unit_test_ratchet_missing` hata mesajını 'run-unit-coverage.sh'taki minimum_tests dizisi değişti; validate-workflows.py:121'deki beklenen değeri de güncelle' diyecek şekilde açıklayıcı yap.

### [Low] Host .NET SDK hikâyesi üç üst-seviye dokümanda üç farklı

`CLAUDE.md:20-22 · CONTRIBUTING.md:30,38 · README.md:21,60-65,127` · R15 · CONFIRMED

Üç üst-seviye doküman host SDK konusunda üç farklı şey söylüyor ('kurulu değildir' / 'kullanılmaz' ama 'normal dotnet test akışı' / 'opsiyonel, yerel .NET bölümü'), ayrıca README.md:127'deki 'user-secrets' ifadesi README.md:62'nin kendi cümlesiyle çelişiyor.

**Neden birinci sınıf değil.** Sıfırdan ortam kuran biri için tek ve net bir yol yok; katkıcı README'ye göre SDK kurup `dotnet test` denerken CLAUDE.md'yi okuyan agent aynı komutu yasak sayıyor. Bayat user-secrets referansı artık geçersiz bir akışa işaret ediyor.

**Nasıl kapanır.** Tek karar ver ve üç dosyada aynı cümleyi kullan: 'Kanonik yol pinned SDK digest'i ile Docker; host SDK desteklenmez.' README ön koşulundan '.NET 10 SDK (opsiyonel)' maddesini kaldır veya 'desteklenmiyor' notu ekle; CONTRIBUTING.md:30'daki 'normal `dotnet test` akışı' ifadesini Compose komutuyla değiştir; README.md:127'deki 'user-secrets' kelimesini kaldır.

### [Low] Aynı Dockerfile üç ayrı compose servisi için üç ayrı image olarak build ediliyor

`docker-compose.yml:261-266, 306-311, 338-347` · R16 · DOĞRULANMADI (yalnız üreten agent)

`database-role-bootstrap`, `database-migrator` ve yeni `database-role-bootstrap-post-migration` üçü de `infrastructure/postgres/Dockerfile.migrator`'ı build ediyor, hiçbirinde `image:` anahtarı yok. Compose bu durumda servis adına göre ayrı tag üretiyor; `run-development-compose-smoke.sh`'in temizlik listesi bunu doğruluyor: `$run_project-database-migrator:latest`, `$run_project-database-role-bootstrap:latest`, `$run_project-database-role-bootstrap-post-migration:latest`. `extends` kullanılmasına rağmen post-migration servisi ayrı bir image tag'i alıyor.

**Neden birinci sınıf değil.** Gereksiz build/export süresi ve disk kullanımı; image envanterinde kavramsal gürültü. İşlevsel bir hata değil ama üçüncü kopyanın eklenmesiyle maliyet görünür hale geldi.

**Nasıl kapanır.** Üç servise de aynı açık `image: saydin/database-control-plane:dev` (veya `${COMPOSE_PROJECT_NAME}-database-control-plane`) etiketini ver; Compose tek build yapıp aynı image'ı paylaştırır. Smoke script'in temizlik listesi de tek tag'e iner.

### [Low] PdfPig merkezi paket sürümü yanlış ItemGroup'a (Saydin.Shared) eklendi

`Directory.Packages.props:19` · R16 · DOĞRULANMADI (yalnız üreten agent)

`<ItemGroup Label="Saydin.Shared">` içine `<PackageVersion Include="PdfPig" Version="0.1.15" />` eklenmiş. Repo genelinde tek tüketici `tools/calendar-data/src/Saydin.CalendarData/Saydin.CalendarData.csproj:9` (`<PackageReference Include="PdfPig" />`); `src/Saydin.Shared` hiçbir yerde `UglyToad` namespace'ini kullanmıyor (tek kullanım `tools/calendar-data/src/Saydin.CalendarData/BistPayCalendarParser.cs`). Dosyanın diğer grupları (`Saydin.Api`, `Saydin.PriceIngestion`, `Tests`) tüketiciye göre etiketlenmiş; `tools/calendar-data` için grup yok.

**Neden birinci sınıf değil.** Bağımlılık haritası yanıltıcı. `CentralPackageTransitivePinningEnabled=true` olduğu için etiket fiilen sadece dokümantasyon; ama bu dosya repo'nun bağımlılık politikasının tek kaynağı olduğundan yanlış gruplama gerçek karar hatasına yol açabilir.

**Nasıl kapanır.** `<ItemGroup Label="tools/calendar-data (offline one-shot)">` grubu aç ve PdfPig'i oraya taşı; grup etiketlerinin "kim tüketiyor" anlamına geldiğini dosya başındaki yorumda açıkça belirt.

### [Low] Yerel unit kapısının coverage çıktısı container içi geçici dizine yazılıp atılıyor

`.github/scripts/run-local-tests.sh:6-9 ↔ docker-compose.yml:477-494` · R16 · DOĞRULANMADI (yalnız üreten agent)

`output_root="$(mktemp -d /tmp/saydin-unit-coverage.XXXXXX)"; exec "$repo_root/.github/scripts/run-unit-coverage.sh" "$output_root"`. `/tmp` container'ın kendi katmanında; `--rm` ile container silinince `coverage.cobertura.xml` ve ReportGenerator çıktıları kayboluyor. CI tarafında aynı script `${runner.temp}/unit-coverage` kullanıp artefakt olarak yüklüyor (.github/workflows/ci.yml:86-94); yerelde eşdeğeri yok. Repo'nun bind mount'u (`.:/src`) zaten mevcut.

**Neden birinci sınıf değil.** Ratchet ihlalinin teşhisi için tek yol testleri elle yeniden koşturmak. Kapının caydırıcılığı yüksek, yol göstericiliği düşük.

**Nasıl kapanır.** `run-local-tests.sh` çıktı kökünü `/src/artifacts/unit-coverage/<timestamp>` yap (gitignore'a ekle) veya compose'da `./artifacts:/artifacts` mount'u tanımlayıp oraya yaz; başarısızlık mesajında rapor yolunu bas.

### [Low] Migration sayısı `26` sekiz ayrı noktada elle senkronize ediliyor; tek kaynak yok

`tests/Saydin.PriceIngestion.IntegrationTests/IngestionDatabaseFixture.cs (schema_migrations sayacı); .github/workflows/ci.yml:612-640; .github/scripts/validate-workflows.py:92-95` · R17 · CONFIRMED

`26` sabiti gerçekten sekiz noktada tekrar ediyor ve tek kaynaktan türetilmiyor; ancak eksik güncelleme sessiz değil, isimlendirilmiş ve deterministik bir CI kırmızısı üretir. Sorun yanlış kapı riski değil, migration eklemenin sekiz noktalı elle senkronizasyon gerektirmesi ve bu noktaların hiçbir yerde listelenmemiş olmasıdır.

**Neden birinci sınıf değil.** Rutin olması gereken migration ekleme işlemi kırılgan ve keşfedilmesi zor bir çoklu-dosya güncellemesine dönüşüyor; geliştirici hangi dosyaları güncelleyeceğini yalnız CI kırmızıya döndükten sonra arayarak öğreniyor.

**Nasıl kapanır.** Kapıların sabit kalması iyi; eksik olan keşfedilebilirlik. `docs/development-guide.md`'ye "yeni migration eklerken güncellenecek kapı listesi" ekle veya `validate-workflows.py`'a beklenen sayıyı `infrastructure/postgres/migrations/` dizin sayısıyla karşılaştıran tek bir tutarlılık kontrolü koy; böylece tek bir hata mesajı bütün noktaları isimlendirir. Alternatif olarak fixture probe'unu sayıdan kurtarıp MigrationTrustRoot.Versions kümesinin tamamının terminal olduğunu doğrula.

### [Low] CLAUDE.md zorunlu hale gelen `bootstrap-dev-database.sh` ön koşulunu belgelemiyor

`CLAUDE.md (Geliştirme Ortamı Kuralı bloğu); docker-compose.yml:249,271-283,316-325; README.md:27; docs/development-guide.md:21; CONTRIBUTING.md:10` · R17 · CONFIRMED

İddia doğru. Compose'un `:?` mesajı doğru script'i adlandırdığı için kurtarma anlıktır; sorun, agent'lara "varsayılan davranışı OVERRIDE eder" diye sunulan CLAUDE.md'nin dört dokümandan tek uyumsuz olanı olmasıdır.

**Neden birinci sınıf değil.** Temiz checkout'ta CLAUDE.md'yi otoritatif kabul eden agent/geliştirici ilk komutta compose hatası alır; düşük etkili ama sözleşme dokümanının kendisiyle çelişen bir tutarsızlıktır.

**Nasıl kapanır.** CLAUDE.md'nin ilgili kod bloğunun ilk satırına `./infrastructure/secrets/bootstrap-dev-database.sh   # tek sefer: purpose-specific dev secret'ları üretir` ekle. Kalıcı çözüm: validate-development-compose.py'a, compose'daki her `:?` zorunlu değişkeni için CLAUDE.md/README/development-guide'da bootstrap adımının bulunduğunu doğrulayan kontrol ekle.

### [Low] Yeni script'lerin reddetme mesajları ne yapılacağını söylemiyor; smoke script'i çalışan bir dev ortamında hiç çalışmıyor

`.github/scripts/run-local-tests.sh:13-21; .github/scripts/run-development-compose-smoke.sh:7-14` · R18 · DOĞRULANMADI (yalnız üreten agent)

`run-local-tests.sh` yasak kapsam için `printf '%s\n' "local_test_scope_rejected:use_required_integration_stack:$argument" >&2; exit 64` yazıyor — 'required integration stack'in ne olduğu, hangi dosya/komut olduğu söylenmiyor. `run-development-compose-smoke.sh:11-14` ise `.env.database-runtime` mevcut ya da symlink ise `development_compose_smoke_failed:runtime_metadata_already_exists` ile exit 78 dönüyor; yani bootstrap'ı çalıştırmış (çalışan bir dev ortamı olan) her geliştirici bu smoke'u yerelde hiç çalıştıramıyor ve mesaj ne yapılacağını (dosyayı taşı/yedekle) söylemiyor. Repo genelinde hiçbir `.sh` script'inde `--help`/`usage()` yok.

**Neden birinci sınıf değil.** Fail-closed kapılar doğru ama yönlendirmesiz. Makine-okunur kod satırları CI için ideal, insan için yetersiz — her ret bir doküman avına dönüşüyor.

**Nasıl kapanır.** Her ret satırının yanına ikinci bir insan satırı ekle: `run-local-tests.sh` için `>&2 echo 'Integration için: .github/compose.integration.yml (bkz. docs/development-guide.md §5) — kanonik akış .github/workflows/ci.yml integration-test job'ıdır.'`. `run-development-compose-smoke.sh` için mesajı `runtime_metadata_already_exists: mevcut .env.database-runtime'ı yedekleyip taşıyın (mv .env.database-runtime .env.database-runtime.bak) ve smoke sonrası geri alın` haline getir — ya da daha iyisi, smoke'u kendi geçici metadata yoluna yönlendirip mevcut dosyaya hiç dokunmasın. Dört yeni script'e `--help` ekle (repo genelinde bir konvansiyon olarak).

### [Low] InstallationEndpoints.RegisterAsync'e kullanılmayan bir servis parametresi eklendi; aynı metotta hem DI hem service-locator kullanılıyor

`src/Saydin.Api/Endpoints/InstallationEndpoints.cs:42-67 (özellikle 46 ve 54)` · R18 · DOĞRULANMADI (yalnız üreten agent)

Diff `RegisterAsync` imzasına `IInstallationPrincipalContext principalContext` ekliyor (satır 46) ama gövde bu parametreyi hiç kullanmıyor; onun yerine satır 54'te `http.RequestServices.GetRequiredService<InstallationPrincipalContext>().Set(registered)` ile concrete tipi service-locator üzerinden çözüyor. Karşılaştırma: aynı diff'te `CommitRotationAsync` (satır 123) aynı parametreyi ekliyor ve gerçekten kullanıyor (satır 143-146 principal eşleşme kontrolü). `EndpointExtensions` da aynı deseni (`http.RequestServices.GetRequiredService<InstallationPrincipalContext>().Set(...)`) tekrarlıyor.

**Neden birinci sınıf değil.** Düşük etkili ama net bir okunabilirlik borcu: ölü parametre + aynı metot içinde iki farklı bağımlılık çözme stili. Minimal API bu parametreyi DI'dan bind etmeye çalıştığı için ayrıca gereksiz bir kayıt bağımlılığı yaratıyor.

**Nasıl kapanır.** `RegisterAsync`'ten kullanılmayan `IInstallationPrincipalContext principalContext` parametresini kaldır. `InstallationPrincipalContext`'i (concrete) doğrudan handler imzasına al — `InstallationPrincipalContext principalContext` — ve `principalContext.Set(registered)` yaz; böylece service-locator kullanımı kalkar ve `CommitRotationAsync` ile aynı stil olur. Aynı temizliği `EndpointExtensions`'daki üç `GetRequiredService<InstallationPrincipalContext>()` çağrısı için de düşün (filter içinde `ctx.HttpContext.RequestServices` kaçınılmaz olabilir; o durumda tek bir `SetPrincipal(http, principal)` yardımcısına indirge).

---

## Ürün deneyimi (15 kayıt)

### [Medium] DCA yanıt sözleşmesi istemci için mutabakat kurulamaz: TotalPurchases ↔ SkippedPurchaseDates ↔ PeriodicAmount uyuşmuyor, opsiyonel alanların null semantiği belgesiz

`src/Saydin.Api/Models/Responses/DcaResponse.cs:18-50; src/Saydin.Api/Services/DcaCalculator.cs:226-236, 294, 351-356, 388-411` · R04 · CONFIRMED

Aynen iddia edildiği gibi. Ek olarak `RealReturnMethod`'un dolu kalması bir test tarafından da sabitlenmiş durumda (DcaCalculatorTests.cs:734-765), yani sözleşme kusuru kasıtlı olarak kilitlenmiş.

**Neden birinci sınıf değil.** Flutter istemcisi planlanan alım sayısını hiçbir alandan türetemez, `PeriodicAmount * TotalPurchases` ile `TotalInvestedTry`'ı bağdaştıramaz ve dolu `RealReturnMethod`'a bakıp reel getiri beklerken null alanlarla karşılaşır. WhatIf ve DCA ekranları aynı kavram için farklı null semantiği taşıyor.

**Nasıl kapanır.** `PlannedPurchases` ve `RequestedInvestedTry` alanlarını ekle; `SkippedPurchaseDates`'i non-nullable `IReadOnlyList<DateOnly>` yap (boş dizi = atlanan yok); `RealReturnMethod`'u yalnız reel hesap tamamlandığında doldur veya ayrı bir 'unavailable' değeri kullan; `InflationDataAsOf`'u deprecate edip tek `InflationTerminalMonth` üzerinden yürü. Meta repo `docs/architecture/api-contract.md`'yi null/boş semantiğiyle güncelle.

### [Medium] Audit'in onarım öneri sözlüğü ile DataRepair'in kabul ettiği operasyon sözlüğü birbirini tutmuyor

`src/Saydin.DataQualityAudit/AuditAccumulator.cs:78-98; src/Saydin.DataRepair/SignedRepairPlan.cs:94-113` · R08 · CONFIRMED

Sözlük uyumsuzluğu ve dokümantasyon boşluğu doğrulandı. Yumuşatıcı iki nokta: (1) `manual_review` plan operasyonu bir 'catch-all' iş emri olarak mevcut olduğundan yürütülemeyen aksiyonlar teknik olarak ifade edilebilir; (2) operatör window id'lerini HMAC'i kırarak değil doğrudan `ingestion_windows` sorgusuyla bulabilir. Dolayısıyla akış 'kopuk' değil, tipli ve belgesiz bir eşleme eksikliğidir — kind 'defect' değil excellence-gap.

**Neden birinci sınıf değil.** Olay anında operatör DQ-003/006/009 önerilerini plana çevirmeye çalışırken tahmin yürütmek ve `plan_operation_type_invalid` ile deneme-yanılma yapmak zorunda; iyileştirme kâğıt üzerinde tipli, uçta yürütülemez.

**Nasıl kapanır.** `RepairAction` → plan operasyon tipi eşlemesini tek yerde (kodda paylaşılan sabit + `docs/runbooks/data-repair.md`'de tablo) yaz; yürütülebilir karşılığı olmayan aksiyonlar için açıkça `manual_review` iş emrine düşürüldüğünü belgele. `RepairRecommendationPolicyTests`'i wire string'leri ve DataRepair'in kabul ettiği tip kümesiyle karşılaştıran bir teste dönüştür.

### [Medium] DCA reel getirisi ara katkı ayları için exact-only kaldığından, her ayın ilk günlerinde tüm reel getiri null'a düşüyor ve /calculate ile çelişiyor

`src/Saydin.Api/Services/DcaCalculator.cs (requiredExactMonths / missingMonths dalı / cache koşulu); src/Saydin.Api/Repositories/InflationRepository.cs (GetExactIndexValuesAsync vs GetIndexValuesAsync/GetNearestRowAsync); src/Saydin.Api/Services/WhatIfCalculator.cs` · R17 · CONFIRMED

Terminal ay LKV ile çözüldüğü için #5 kapandı; ancak M-1 gibi ara katkı ayları hâlâ exact-only. TÜİK M-1 TÜFE'sini tipik olarak ayın 3'ünde yayınladığından, ayın 1-3'ü arasında M-1'de katkısı olan (yani neredeyse tüm) aylık DCA planlarında tüm reel getiri alanları null döner; aynı anda /calculate LKV kullandığı için reel getiri gösterir. Bu istekler ayrıca hiç cache'lenmez.

**Neden birinci sınıf değil.** Enflasyona göre düzeltilmiş getiri özelliği her ay birkaç gün için kapanıyor, aynı üründe iki ekran çelişiyor ve kullanıcıya tek sinyal jenerik bir uyarı kodu oluyor; cache'lenmeme nedeniyle bu pencerede DB yükü de artıyor.

**Nasıl kapanır.** Ara aylar için de kademeli sözleşme uygula: eksik ara ay için `period_date <= o ay` en son final gözlemi deflatör kabul et, kullanılan ayı `InflationDataAsOf` ile bildir ve `RealReturnMethod`'u ayırt edici bir değere çevir (örn. `cashflow_cpi_lkv_v1`); yalnız hiç gözlem yoksa null'a düş. `inflationCalculationComplete=false` yolunu kısa TTL ile cache'le. FakeTimeProvider ile "ayın 2'si, M-1 CPI'ı yok" senaryosunu kilitleyen test ekle.

### [Medium] ProblemDetails `field` uzantısı bazen C# property adı (PascalCase), bazen wire adı (camelCase) — istemci tek bir eşleme kuralı kuramıyor

`src/Saydin.Api/Endpoints/ScenariosEndpoints.cs:177,187; src/Saydin.Api/Services/DcaCalculator.cs:84,123; src/Saydin.Api/Services/WhatIfCalculator.cs:50,629; src/Saydin.Api/Repositories/SavedScenarioRepository.cs:152-161; src/Saydin.Api/Endpoints/AssetsEndpoints.cs:168` · R18 · DOĞRULANMADI (yalnız üreten agent)

Bu diff aynı anda iki konvansiyon ekliyor: `field: "limit"` / `field: "cursor"` (ScenariosEndpoints.cs:177,187 — wire adı) ve `field: nameof(request.EndDate)` → `"EndDate"` (DcaCalculator.cs:84,123 — PascalCase). Mevcut kod tabanında da her iki taraf var: `"dateRange"`, `"request"` vs. `"ExtraData"`, `"Type"`, `"AmountType"`, `"SellDate"`. JSON request property'leri camelCase serialize edildiği için `"SellDate"` değeri istemcinin gönderdiği `sellDate` alanıyla birebir eşleşmiyor. ValidationExceptionHandler.cs:47-48 değeri olduğu gibi `extensions["field"]` içine yazıyor.

**Neden birinci sınıf değil.** Hata deneyimi eyleme dönüştürülemiyor: istemci hangi girdinin hatalı olduğunu güvenilir şekilde işaretleyemiyor. Bir endpoint'i öğrenen geliştirici diğerini tahmin edemiyor.

**Nasıl kapanır.** Tek kural belirle ve zorla: `field` her zaman **wire adı** (JSON property adı, camelCase; query parametresi için query adı) olsun. `nameof(request.EndDate)` kullanımlarını `"endDate"` gibi sabitlerle veya `JsonNamingPolicy.CamelCase.ConvertName(nameof(...))` ile değiştir. Mevcut PascalCase değerleri (`ExtraData`, `Type`, `AmountType`, `SellDate`) tek seferde çevir ve `ExceptionHandlerContractTests`'e 'her `field` değeri `^[a-z][A-Za-z0-9.]*$` desenine uyar' fail-closed assert'i ekle. Bu bir breaking change olduğu için meta repo api-contract.md'de ve release note'ta bildir.

### [Medium] İki farklı 404 şekli: port-boundary reddi RFC 7807 gövdesi döner, eşleşmeyen normal route boş gövde döner

`src/Saydin.Api/Middleware/ApiPortBoundaryMiddleware.cs:18-35; src/Saydin.Api/Program.cs (MapFallback/UseStatusCodePages yok)` · R18 · DOĞRULANMADI (yalnız üreten agent)

Bu diff `ApiPortRequestKind.Rejected` yoluna tam ProblemDetails gövdesi ekliyor: `Type=https://saydin.app/errors/route-not-found`, `code=route_not_found`, `traceId` ve lokalize `RouteNotFound`/`RouteNotFoundDetail`. Buna karşılık `Program.cs` içinde ne `MapFallback` ne `UseStatusCodePages` var; public port'ta eşleşmeyen bir yol (`GET /v1/scenariso`) routing tarafından gövdesiz 404 alır — `code` yok, `traceId` yok, `Content-Type` problem+json değil.

**Neden birinci sınıf değil.** Hata sözleşmesi kısmen tutarlı: yeni eklenen 404 birinci sınıf, mevcut 404 hiç sözleşmeye uymuyor. Ayrıca `traceId` olmadığı için bir istemci hata raporunu sunucu trace'iyle ilişkilendirmek imkânsız.

**Nasıl kapanır.** `Program.cs`'e `app.MapFallback(...)` veya `app.UseStatusCodePages(async ctx => ...)` ekleyip aynı `route_not_found` problem gövdesini üret; gövde oluşturmayı `ApiPortBoundaryMiddleware`'den ortak bir `RouteNotFoundProblem.WriteAsync(context, localizer)` helper'ına çıkar ve her iki yol da onu çağırsın. `ExceptionHandlerContractTests`'e 'public port'ta tanımsız bir yol problem+json ve `code=route_not_found` döner' testi ekle. Aynı kuralı 405 için de düşün.

### [Medium] 503 admission ve quota yanıtları `Retry-After` taşımıyor — istemciye ne zaman deneyeceği söylenmiyor

`src/Saydin.Api/Security/SecurityAdmissionProblem.cs:27-35,39-46; src/Saydin.Api/Exceptions/QuotaUnavailableExceptionHandler.cs:19-33` · R18 · DOĞRULANMADI (yalnız üreten agent)

`SecurityAdmissionProblem.SetRetryAfter` yalnız `SecurityLimiterOutcome.Limited` (429) yolunda çağrılıyor; `Unavailable` (503) yolunda hiç çağrılmıyor. `QuotaUnavailableExceptionHandler` de 503 dönerken Retry-After yazmıyor. Detail metinleri ise 'Lütfen daha sonra tekrar deneyin' / 'Please try again later' diyor — 'daha sonra'nın ne kadar olduğu belirtilmiyor.

**Neden birinci sınıf değil.** Fail-closed davranış doğru ama istemci sözleşmesi eksik: 503'ler eyleme dönüştürülebilir değil. Retry davranışı istemci tahminine bırakılıyor, bu da kesinti sırasında thundering-herd riski yaratıyor.

**Nasıl kapanır.** 503 admission ve quota yanıtlarına bounded, jitter'lı bir `Retry-After` (ör. 5-15 sn) ekle ve aynı değeri ProblemDetails `extensions["retryAfterSeconds"]` olarak da yaz (header'a erişimi kısıtlı istemciler için). `SetRetryAfter`'ı `SecurityAdmissionProblem` içinde her iki yolda da çağır; `QuotaUnavailableExceptionHandler`'a aynı helper'ı ver. `ExceptionHandlerContractTests`'e 'her 429/503 yanıtı Retry-After taşır' assert'i ekle.

### [Medium] /24 ağ bucket'ları CGNAT gerçeğiyle çatışıyor; günlük pencerede 429 mesajı dakikalık limitten ayırt edilemiyor

`src/Saydin.Api/Security/DistributedSecurityLimiter.cs:251-280 (TryNormalizeAddress), 133-197; src/Saydin.Api/Security/DistributedSecurityLimiterOptions.cs:25-38; docs/cache-strategy.md:181-187` · R18 · DOĞRULANMADI (yalnız üreten agent)

`TryNormalizeAddress` IPv4 için `network[3]=0` (yani /24), IPv6 için ilk 8 bayt (/64) kullanıyor. Yeni bucket'lar: `RegistrationNetworkDailyLimit=100`, `CalculationNetworkDailyLimit=500` — her ikisi de **sabit takvim penceresi** (`floor(now_ms / 86400000)`), dolayısıyla `Retry-After` 24 saate kadar çıkabiliyor. 429 gövdesi ise dakikalık limiterle **aynı** başlığı (`RateLimited`) ve detay metnini (`SecurityRateLimitedDetail` — 'belirtilen süre sonunda tekrar deneyin') ve aynı `code=security_rate_limited` değerini taşıyor. docs/cache-strategy.md:184-185 'aynı NAT'taki farklı istemciler ise tekil IP registration bucket'larını paylaşmaz' diyerek asıl bağlayıcı olan **ağ** bucket'ının paylaşıldığını gölgeliyor; repo'da CGNAT ölçeği hiçbir yerde bu limitler bağlamında ele alınmamış (CGNAT yalnız GeoIP tarafında, MaxMindGeoIpResolver.cs:101 ve activity-logging.md:357'de kabul ediliyor).

**Neden birinci sınıf değil.** Lansmanda toplu kullanılamazlık riski; kullanıcıya gösterilen mesaj ('bu istemciden çok fazla istek alındı') yanlış — istek onun değil, ağını paylaşan başkalarının. Ürün kotası (`DailyLimitExceeded`, lokalize ve anlamlı) ile güvenlik limiti aynı 429 altında ayırt edilemiyor.

**Nasıl kapanır.** (1) Ağ bucket'ı için ayrı bir kod ve mesaj ayır: `code=security_network_rate_limited` + yeni resx anahtarları (`SecurityNetworkRateLimited`/`Detail`: 'Paylaşılan ağınızdan gelen istek sayısı sınıra ulaştı'), böylece istemci 'bir dakika bekle' ile 'paylaşılan ağ, yarın tekrar dene'yi ayırt edip doğru UX gösterebilsin. (2) `retryAfterSeconds`'ı ProblemDetails'a da koy (bkz. R18-06). (3) Sabit takvim penceresi yerine sliding/rolling pencere kullanarak 24 saatlik ceza yerine kademeli açılma sağla. (4) CGNAT'ı `docs/high-traffic-checklist.md` ve `docs/cache-strategy.md`'de açıkça yaz, ağ limitlerini 'lansman sonrası ilk telemetriye göre kalibre edilecek' olarak işaretle ve `saydin_security_admission_decisions_total{bucket="calculation_network",outcome="limited"}` üzerine bir warning alert'i koy. (5) cache-strategy.md:184-185'teki yanıltıcı NAT cümlesini düzelt.

### [Low] Kimlik doğrulama şeması büyük/küçük harfe duyarlı karşılaştırılıyor

`src/Saydin.Api/Endpoints/EndpointExtensions.cs:169-189` · R01 · CONFIRMED

Doğru ve standart dışı; güvenlik açığı değil (kesinlik fail-closed yönde) ama RFC 7235'e aykırı ve teşhis edilmesi zor bir entegrasyon tuzağı. 401 gövdesi (InvalidInstallationCredential, 191-205) sebebi ayırt etmiyor.

**Neden birinci sınıf değil.** Şemayı normalize eden bir istemci kütüphanesi veya ara katman ('installation <token>') 401 alır ve sebebi harf durumu olduğu anlaşılmaz.

**Nasıl kapanır.** Şema karşılaştırmasını StringComparison.OrdinalIgnoreCase yap; token kısmının uzunluk/format kontrolü Ordinal kalabilir. Davranışı bir Theory testiyle kilitle ("installation ", "INSTALLATION ", "Installation ").

### [Low] Registration reddi sıradan bir hız limitinden ayırt edilemiyor: aynı code/başlık, 24 saatlik Retry-After, alarm yok

`src/Saydin.Api/Security/SecurityAdmissionProblem.cs:16-25,43-67,76-82; src/Saydin.Api/Security/SecurityAdmissionTelemetry.cs:7-31; infrastructure/prometheus/rules/api.yml` · R02 · CONFIRMED

Registration, calculation-network, principal ve network bucket'ları tek bir ayırt edilemez 429 sözleşmesi paylaşıyor; Retry-After tavansız olduğu için günlük registration bucket'ında 24 saate kadar çıkabiliyor ve bucket'a göre etiketlenmiş SecurityAdmissionDecisions metriği hiçbir Prometheus kuralı tarafından izlenmiyor.

**Neden birinci sınıf değil.** Kullanıcı hiç istek göndermediği hâlde 'çok fazla istek gönderdiniz' mesajı alıp bir gün bekletilir; operatör tarafında registration lockout'ları hiçbir proaktif sinyal üretmediği için R02-02'deki tasarım riskinin production'da ne sıklıkta gerçekleştiği ölçülemez.

**Nasıl kapanır.** Bucket'a özgü bir hata kodu ve lokalize metin ekle (ör. `code=installation_registration_limited` + ErrorMessages(.en).resx'te 'bu ağdan bugün yeni kurulum yapılamıyor' anlamında bir metin) ki istemci doğru şeyi söyleyip başka bir ağ önerebilsin. Prometheus'a `bucket="registration", outcome="limited"` oranı ve `outcome="unavailable"` için iki kural ekleyip docs/runbooks/api-availability.md'ye teşhis adımlarını bağla. Retry-After'ı makul bir tavana (ör. 3600) kırp.

### [Low] installation_register satırları principal pseudonym'i taşımıyor; korelasyon anahtarı yaşam döngüsünün başında kopuyor

`src/Saydin.Api/Endpoints/InstallationEndpoints.cs:43-58; src/Saydin.Api/Endpoints/EndpointExtensions.cs:56-58,153-155; src/Saydin.Api/Helpers/ActivityLogBuilder.cs:92-94` · R03 · CONFIRMED

PrincipalActivityIdItemKey yalnız iki credential filtresinde yazılıyor; principal'ın handler içinde doğduğu registration yolunda yazılmadığı için `installation_register` satırları device_id='unknown' ile kaydediliyor. (Not: 022 redaction'ı bu satırı da diğer satırlarla aynı şekilde yakalar; register'a özgü ek bir redaction kaybı yoktur.)

**Neden birinci sınıf değil.** Bir principal'ın yaşam döngüsü (kayıt → rotasyon → iptal) tek anahtarla, pseudonym üzerinden izlenemez: `installation_register` satırı 'unknown' havuzunda kaybolur ve yalnız user_id ile bulunabilir; bu da pseudonym-temelli (user_id'siz) analitik ihracatını eksik bırakır.

**Nasıl kapanır.** RegisterAsync içinde `Set(registered)` hemen ardından `http.Items[PrincipalActivityIdItemKey] = pseudonymizer.Pseudonymize(registered.PrincipalId)` yaz — daha iyisi, pseudonym yazımını InstallationPrincipalContext.Set üzerinden merkezîleştirip üç yoldaki tekrarı kaldır; ActivityLogMiddlewareTests'in ilgili beklentisini 'p1:' ile güncelle.

### [Low] DCA yanıtı terminal değerlemenin gerçekte hangi tarihe ait olduğunu açıkça bildirmiyor (WhatIf'te ActualSellDate var)

`src/Saydin.Api/Services/DcaCalculator.cs:263-268, 388-392; karşılaştırma: src/Saydin.Api/Services/WhatIfCalculator.cs:450-452, 566-567` · R04 · CONFIRMED (doğrulayıcı ek bulgusu)

DCA `currentUnitPrice = latestPricePoint.Close` ve `currentValueTry` `latestPricePoint.PriceDate` gününe aittir; `latestPricePoint` `GetNearestPricesAsync` ile maxDays=7 penceresinde çözülür, yani istenen `endDate`'ten 7 güne kadar geride olabilir. Yanıt ise `EndDate: endDate` (kullanıcının istediği/çözülen takvim günü) döndürüyor ve `latestPricePoint.PriceDate`'i açıkça bildiren bir alan yok. WhatIfCalculator aynı durumu `ActualBuyDate`/`ActualSellDate` alanlarıyla açıkça bildiriyor (`sellPricePoint.PriceDate != sellDate ? ... : null`).

**Neden birinci sınıf değil.** İki ekran aynı olguyu farklı şekilde raporluyor; DCA'da terminal değerin as-of tarihi yalnız dolaylı olarak (`Data.PriceBasis.AsOfThrough` veya `Purchases` listesinin son satırı) türetilebiliyor. Finansal bir sonucun as-of tarihinin örtük kalması ürün açısından birinci sınıf değil.

**Nasıl kapanır.** `DcaResponse`'a `ActualEndDate` (yalnız `latestPricePoint.PriceDate != endDate` iken dolu) alanını ekle ve WhatIf ile aynı null semantiğini kullan; meta repo api-contract.md'de her iki endpoint için tek bir 'actual vs requested date' kuralı yaz.

### [Low] `retire --login backup` alakasız bir hata koduyla reddediliyor

`src/Saydin.DatabaseRoleBootstrap/BootstrapOptions.cs:105-109` · R07 · CONFIRMED

`retire --login backup` (ve `reset-password --login backup`) çağrıları, sürüm değerinden bağımsız olarak `backup_rotate_version_must_be_v2` koduyla reddediliyor. Doğru mesaj 'backup login'i için bu komut desteklenmiyor' olmalıydı; runbook'lar sınırı doğru anlatıyor, kusur yalnızca CLI hata kodunda.

**Neden birinci sınıf değil.** Yanlış yönlendiren tanılama: operatör `--login-version 2` deneyerek yanlış komutu tekrarlar ve aynı reddi alır. Fail-closed olduğu için güvenlik riski yok, yalnız ergonomi ve olay-anı zaman kaybı.

**Nasıl kapanır.** Backup için `Rotate` dışındaki komutları ayrı bir kodla reddet (`backup_login_command_unsupported`) ve `--login-version != 2` durumunu mevcut kodda bırak. stderr'a `backup-login-renewal.md` referansı veren tek satırlık bir ipucu ekle; `DatabaseFailureCodeTests`'e iki kodu ayıran birer vaka koy.

### [Low] README servis tablosu ve mimari diyagramı gerçek topolojiyi göstermiyor

`README.md:7-15,48-56,97-111 · CLAUDE.md:7-15` · R15 · CONFIRMED

README servis tablosu 6 proje listeliyor; repo 9 taşıyor (DataQualityAudit, DataRepair, CalendarData eksik) ve README'nin Mermaid diyagramı, aynı sayfanın metninde anlatılan pre-bootstrap → migrator → HBA → post-bootstrap kapı zincirini göstermiyor.

**Neden birinci sınıf değil.** Giriş dokümanı sistemin gerçek yüzeyini eksik temsil ediyor; ilk bakışta 'iki servis + migrator' izlenimi veriyor ve onboarding'de bileşen envanteri ile başlangıç diyagramı arasında kopukluk oluşuyor.

**Nasıl kapanır.** README servis tablosuna DataQualityAudit, DataRepair ve CalendarData satırlarını ekle (CLAUDE.md:7-15 ile aynı tanımlarla) ve Mermaid diyagramına role-bootstrap (pre/post) ile backup düğümlerini `service_completed_successfully` kapı okları olarak işle.

### [Low] İstemci kaynaklı `X-Forwarded-*` durumunda kalıcı 503 üretiliyor ve Redis kesintisinden ayırt edilemiyor

`src/Saydin.Api/Security/DistributedSecurityLimiterMiddleware.cs (InvokeAsync untrusted dalı, TryGetTrustedClientAddress); src/Saydin.Api/Security/SecurityAdmissionProblem.cs (WriteAsync); src/Saydin.Api/Runtime/ApiRuntimeContract.cs (ForwardLimit/RequireHeaderSymmetry); infrastructure/deployment/Caddyfile` · R17 · CONFIRMED

Fail-closed karar doğru ve güvenlik açığı değil; sorun sözleşmenin şekli: istemci kaynaklı, kalıcı ve istemci tarafından düzeltilebilir bir red, sunucu tarafı geçici arıza olan Redis kesintisiyle aynı 503/`security-limiter-unavailable`/`code` üçlüsüyle raporlanıyor. Metrikte reason ayrı (`ClientAddressUntrustedReason`) ama yanıt sözleşmesi ayırt etmiyor.

**Neden birinci sınıf değil.** Kendi `X-Forwarded-*` header'ını ekleyen bir istemci/proxy arkasındaki kullanıcı kalıcı 503 alır; hem kullanıcı hem destek bunu backend arızası sanar ve alarm/metriklerde gerçek Redis kesintisiyle karışabilir.

**Nasıl kapanır.** Untrusted-address durumunu ayrı bir ProblemDetails sözleşmesine bağla (örn. 400 `https://saydin.app/errors/forwarded-header-rejected` + ayrı `code`), lokalize `Detail`'de istemcinin `X-Forwarded-*` göndermemesi gerektiğini belirt ve meta repo api-contract.md'ye ekle. Alert tarafında bu reason'ı Redis kesintisinden ayıran ayrı seri tanımla.

### [Low] DcaResponse.SkippedPurchaseDates şemada nullable ama runtime'da hiç null olmuyor — istemci iki durum için de kod yazmak zorunda

`src/Saydin.Api/Models/Responses/DcaResponse.cs:40-49; src/Saydin.Api/Services/DcaCalculator.cs:411` · R18 · DOĞRULANMADI (yalnız üreten agent)

Record `IReadOnlyList<DateOnly>? SkippedPurchaseDates = null` olarak tanımlı (opsiyonel parametre sırası gereği), fakat `DcaCalculator.cs:411` her yolda `SkippedPurchaseDates: skippedPurchaseDates` ile dolu bir liste geçiriyor — atlanan gün yoksa boş liste. Aynı kayıtta `Purchases` ve `ChartData` non-nullable. Yani aynı yanıt içinde üç koleksiyondan ikisi 'her zaman var', biri şemaya göre 'olmayabilir'. Aynı desen `InflationAdjustedInvestedTry`, `RealProfitLossTry`, `RealReturnMethod`, `InflationTerminalMonth`, `Data` için de geçerli — bunlar gerçekten koşullu null ama şemada `SkippedPurchaseDates` ile ayırt edilemiyorlar.

**Neden birinci sınıf değil.** Null semantiği belirsiz: aynı yanıtta 'gerçekten koşullu null' alanlar ile 'hiç null olmayan ama nullable işaretli' alan ayırt edilemiyor. Küçük ama istemci tarafında tekrar eden bir sürtünme.

**Nasıl kapanır.** Ya kayıt sırasını değiştirip `SkippedPurchaseDates`'i non-nullable zorunlu parametre yap (breaking, ama şemayı dürüst kılar), ya da alanı nullable bırakıp `[Required]`/OpenAPI şema override'ı ile 'her zaman mevcut, atlanan yoksa boş dizi' olarak işaretle ve `docs/architecture.md`'deki DCA yanıt açıklamasına bu cümleyi ekle. Genel olarak: bu yanıtta 'koşullu null' alanların hepsinin ne zaman null olduğunu tek bir tabloda docs/architecture.md'ye yaz — istemci geliştiricisi bugün bunu DcaCalculator kaynağından çıkarmak zorunda.

---

## Sadelik ve tekrar (13 kayıt)

### [Medium] EvdsInflationWorker, BaseAssetWorker'ın ~150 satırlık lease/deadline/scheduling mantığını kopyalıyor

`src/Saydin.PriceIngestion/Workers/EvdsInflationWorker.cs:86-112,213-228,279-393; src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:122-156,264-279,384-503` · R05 · CONFIRMED

Doğrulandı; bu bir üslup tercihi değil, iki kopyada bağımsız evrilme riski taşıyan somut bir tekrar (aynı isimli `WorkerPass` tipinin farklı sözleşmeye sahip olması sapmanın başladığının kanıtı).

**Neden birinci sınıf değil.** Bir sonraki lease/deadline düzeltmesi tek kopyaya uygulanırsa davranış sessizce ayrışır; okuyucu "aynı ama farklı" iki worker sözleşmesini kafasında tutmak zorunda; test yükü iki katına çıkıyor.

**Nasıl kapanır.** Lease yenileme + mutlak deadline + terminalization + `min(next_attempt_at, scheduled)` uyanma mantığını asset-agnostik bir `IngestionWindowDrainer` (veya `LeasedProviderCall<T>`) yardımcısına çıkar; `BaseAssetWorker` ve `EvdsInflationWorker` yalnız scope/plan/validate farklarını sağlasın.

### [Medium] BaseAssetWorker ve EvdsInflationWorker arasında deadline/lease/drain durum makinesi kopyalanmış

`src/Saydin.PriceIngestion/Workers/EvdsInflationWorker.cs:279-393 ile src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:384-502` · R06 · CONFIRMED

`WithLeaseRenewalAsync`, `ObserveDetachedAsync`, `RenewUntilCancelledAsync`, `DrainDisposition` ve `DrainResult` iki worker'da birebir kopya; `ProviderDeadline` iki yerde ayrı tanımlı. Ayrışma başlamış: `WorkerPass` base'de `Include` metotlarına sahipken EVDS'te yalnız `Empty` taşıyor. Bulgudaki "RecordPermanentBlocked yalnız base'de" alt-iddiası yanlıştır — EvdsInflationWorker.cs:207-210'da mevcuttur. `GetDelayUntilNextRun` farkı ise meşru (günlük vs aylık zamanlama).

**Neden birinci sınıf değil.** Deadline semantiğindeki (ör. R06-03 düzeltmesi) veya lease yenileme aralığındaki her değişiklik iki yerde yapılmak zorunda. Birinin unutulması EVDS (aylık TÜFE) yolunda sessiz davranış farkı yaratır ve bu yol ayda bir çalıştığı için en geç fark edilen yoldur. İkinci bir okuyucu için hangi kopyanın kanonik olduğu belirsiz.

**Nasıl kapanır.** Lease + deadline + detached-observation sarmalayıcısını ortak bir tipe çıkar (ör. `ProviderExecution.RunWithLeaseAsync<T>(claim, operation, deadline, leaseRenewer, logger, ct)`); `DrainDisposition`/`DrainResult`/`WorkerPass` paylaşılan internal tipler olsun. Zamanlama farkını (`GetDelayUntilNextRun`) abstract bırak.

### [Medium] Aynı admission problem gövdesi iki kez, aynı reason→string eşlemesi üç kez yazılmış; kopyalar şimdiden ayrışmış

`src/Saydin.Api/Security/SecurityAdmissionProblem.cs:11-66; src/Saydin.Api/Security/DistributedSecurityLimiterMiddleware.cs:77-84; src/Saydin.Api/Endpoints/EndpointExtensions.cs:259-267; src/Saydin.Api/Security/SecurityAdmissionTelemetry.cs:42-49` · R18 · DOĞRULANMADI (yalnız üreten agent)

`SecurityAdmissionProblem.Result()` (IResult) ve `SecurityAdmissionProblem.WriteAsync()` (HttpResponse) aynı iki problemi bağımsız iki kod yoluyla üretiyor — ve davranışları ayrışmış: `Result()` `Allowed` gelirse `ArgumentOutOfRangeException` fırlatıyor, `WriteAsync()` aynı girdiyi sessizce 503 'limiter unavailable' yapıyor. `SecurityLimiterReason` → stabil string eşlemesi üç yerde: `DistributedSecurityLimiterMiddleware.StableReason` (yalnız 3 case + `_ => "unexpected"`), `EndpointExtensions.StableReason` (5 case + `_ => "unexpected"`), `SecurityAdmissionTelemetry.Reason` (5 case + throw). Üç kopya zaten farklı davranıyor.

**Neden birinci sınıf değil.** Yeni bir okuyucu için güvenlik admission hata yolunun tek bir otoritesi yok. Kopyalar arasındaki mevcut ayrışma (Allowed davranışı, eksik case'ler) zaten bir bakım tuzağı; telemetrinin sıcak yolda exception fırlatması ise admission reddini 500'e çevirebilir.

**Nasıl kapanır.** Tek bir `SecurityAdmissionProblem.Build(HttpContext, IStringLocalizer, SecurityLimiterDecision) → (int status, ProblemDetails body, int? retryAfterSeconds)` fonksiyonu yaz; `Result()` ve `WriteAsync()` ikisi de bunu sarsın ve `Allowed` için ikisi de aynı şekilde (fırlatarak) davransın. `StableReason`'ı sil, tek otorite olarak `SecurityAdmissionTelemetry.Reason`'ı `internal static string Stable(SecurityLimiterReason)` olarak public'e çıkar ve üç çağıran da onu kullansın. `SecurityAdmissionTelemetry.Record`'daki `ArgumentOutOfRangeException`'ları istek yolunda fırlatmak yerine `Debug.Assert` + `"unexpected"` fallback'e çevir; kontratı bir unit testle (enum'un her değeri için eşleme var mı) kilitle.

### [Low] Aynı admission zarfı iki, reason→string eşlemesi üç yerde tekrarlanıyor ve `Allowed` girdisinde davranışlar ayrışıyor

`src/Saydin.Api/Security/SecurityAdmissionProblem.cs:11-69; SecurityAdmissionTelemetry.cs:41-49; DistributedSecurityLimiterMiddleware.cs:77-83; Endpoints/EndpointExtensions.cs:260-268` · R01 · CONFIRMED

Doğru. Bugün ulaşılamayan ama gerçek bir tuzak: enum'a yeni bir SecurityLimiterReason eklenip üç eşlemeden yalnız biri güncellenirse metrik ArgumentOutOfRangeException fırlatır, log 'unexpected' yazar, filtre yolu doğru değeri üretir.

**Neden birinci sınıf değil.** Gözlemlenebilirlik sözleşmesi (`reason` etiket kümesi) tek kaynak-doğruya bağlı değil; ikinci bir okuyucu hangi yolun hangi davranışa sahip olduğunu okuyamıyor. Bir refactor WriteAsync'i Allowed kararıyla çağırırsa istemci sessizce 503 alır.

**Nasıl kapanır.** Reason→string eşlemesini tek bir `internal static string SecurityLimiterReasons.Stable(SecurityLimiterReason)` fonksiyonunda topla ve üç yeri ona bağla. Result/WriteAsync ikilisini ortak bir `BuildProblemDetails(decision)` üzerinden üret; WriteAsync da Allowed girdisinde Result ile aynı şekilde fırlatsın.

### [Low] Rotation commit yolu, filtrede zaten çözülmüş kimliği el yordamıyla ikinci kez doğruluyor; 024 ilgisiz bir katalog yorumu taşıyor

`src/Saydin.Api/Endpoints/InstallationEndpoints.cs:119-170; src/Saydin.Api/Endpoints/EndpointExtensions.cs:118-165; infrastructure/postgres/migrations/024_installation_credential_rehash.sql:200-207` · R02 · CONFIRMED

Commit yolunda kimlik iki kez çözülüyor (filtre + handler), aday hash'ler iki kez üretiliyor ve 1-3 ek DB roundtrip yapılıyor; hash sıfırlama helper'ı bu yolda kullanılmıyor; principal uyuşmazlığı savunma kontrolü mutasyondan sonra çalışıyor. Ayrı olarak 024, adıyla ilgisiz bir market_holidays katalog yorumu güncellemesini aynı imzalı artefakta katıyor.

**Neden birinci sınıf değil.** Sıcak olmayan bir yolda ölçülebilir performans sorunu değil; ancak 'kimlik nerede doğrulanıyor' sorusunun cevabı ikiye bölünmüş ve mutasyon-sonrası exception yolu istemciyi kullanılamaz token'la 500'e bırakabiliyor. İlgisiz migration içeriği ise checksum/imza review'ini gürültülendiriyor.

**Nasıl kapanır.** Filtrenin çözdüğü key sürüm numarasını (hash'i değil) HttpContext.Items'a koy; handler tek bir `commit_installation_rotation` çağrısı yapsın. `ZeroCandidateHashes`'i dört yolda da kullan. Principal uyuşmazlığı kontrolünü mutasyondan önceye taşı veya tetiklendiğinde idempotent bir yanıt üretip olayı ayrı bir metrikle işaretle. market_holidays yorumunu 024'ten çıkarıp kendi migration'ına al.

### [Low] CalculationTelemetry'nin iki Observe metodu birebir kopya

`src/Saydin.Api/Helpers/CalculationTelemetry.cs:9-73` · R03 · CONFIRMED

İki metot yalnızca kullandıkları Counter/Histogram çiftinde farklı; 30 satırlık outcome sınıflandırma ve tag kurulum bloğu birebir tekrarlanıyor.

**Neden birinci sınıf değil.** Üçüncü bir hesaplama ailesi eklendiğinde blok üçüncü kez kopyalanır; bir dalda outcome sınıflandırması güncellenip diğeri unutulursa metric semantiği sessizce ayrışır.

**Nasıl kapanır.** `ObserveAsync<T>(Counter<long> counter, Histogram<double> duration, string operation, Func<Task<T>> action)` tek metoduna indir; ObserveWhatIfAsync/ObserveDcaAsync tek satırlık sarmalayıcı delegeler olarak kalsın.

### [Low] ObserveWhatIfAsync ve ObserveDcaAsync birebir kopya; outcome="error" etiketi kullanıcı kaynaklı 4xx'leri de kapsıyor

`src/Saydin.Api/Helpers/CalculationTelemetry.cs:9-72; src/Saydin.Api/Services/DcaCalculator.cs:35-38; src/Saydin.Shared/Diagnostics/SaydinMetrics.cs:50-60` · R04 · CONFIRMED

Aynen iddia edildiği gibi. R04-03'teki UTC gelecek-tarih reddi de bu sayaca `error` olarak düşer, yani iki bulgu birbirini besliyor.

**Neden birinci sınıf değil.** Hata oranına dayalı SLO alarmı kullanıcı hatasıyla altyapı hatasını ayırt edemez; ayrıca üçüncü bir hesaplama türü eklendiğinde 30 satırlık gövde yeniden kopyalanır.

**Nasıl kapanır.** Tek bir `ObserveAsync<T>(Counter<long> counter, Histogram<double> duration, string operation, Func<Task<T>> action)` metoduna indir ve `outcome`'ı üçe ayır: `success` / `rejected` (domain exception, 4xx) / `error` (beklenmeyen) — alarm yalnız `error`'a baksın.

### [Low] Aktif asset listesi iki kez, iki farklı sıralama semantiğiyle sıralanıyor

`src/Saydin.PriceIngestion/Repositories/PriceIngestionRepository.cs:18-21; src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:78-81` · R05 · CONFIRMED

Doğrulandı. Davranışsal risk yok (ordinal sıralama son sözü söylüyor); sınırda bir bulgu ama iki farklı determinizm otoritesinin yan yana durması somut bir tutarsızlıktır.

**Neden birinci sınıf değil.** Gereksiz DB `ORDER BY` ve kod okuyucusu için belirsizlik: determinizmin nerede garanti edildiği net değil; ileride biri kaldırılırsa hangisinin kritik olduğu belli değil.

**Nasıl kapanır.** Determinizmi tek yerde tut: ya repository'de ordinal-eşdeğer sıralama (`ORDER BY symbol COLLATE "C", id`) kullanıp worker'daki tekrarı sil, ya da repository `OrderBy`'ını kaldırıp worker'daki ordinal sıralamayı tek otorite yap ve nedenini yorumla belirt.

### [Low] TCMB ay kodu tablosu ve URI şablonu iki ayrı yerde kopyalandı — materializer ile allowlist arasında sessiz drift riski

`tools/calendar-data/src/Saydin.CalendarData/CalendarPlanMaterializer.cs:6-7,31-36; tools/calendar-data/src/Saydin.CalendarData/SourceSnapshotStore.cs:8-9,124-128` · R09 · DOĞRULANMADI (yalnız üreten agent)

`private static readonly string[] MonthCodes = ["Jan",...,"Dec"]` her iki sınıfta ayrı ayrı tanımlı; `/kurlar/{yyyyMM}/{Mon}_tr.html` ve `/kurlar/kur{yıl}_tr.html` şablonları da bir kez üretim (materializer), bir kez doğrulama (allowlist) tarafında elle tekrarlanıyor.

**Neden birinci sınıf değil.** Bakım maliyeti ve teşhis gürültüsü; tek bir otoriter URI üreticisi olmadığı için "exact URI pinning" güvencesi iki bağımsız gerçeğe dayanıyor.

**Nasıl kapanır.** URI şablonlarını tek bir yerde (örn. `OfficialSourcePolicy` içinde `static string TcmbMonthlyUri(int year, int month)` / `TcmbAnnualUri(int year)`) topla ve hem materializer hem `RequireUri` bu fonksiyonları çağırsın; `MonthCodes`'ı da oraya taşı.

### [Low] CalculationTelemetry.ObserveDcaAsync, ObserveWhatIfAsync'in iki statik alan dışında birebir kopyası

`src/Saydin.Api/Helpers/CalculationTelemetry.cs:9-38 ve 40-71; src/Saydin.Shared/Diagnostics/SaydinMetrics.cs:50-60` · R18 · DOĞRULANMADI (yalnız üreten agent)

İki metot 30 satır boyunca karakter karakter aynı; tek fark son iki satırdaki `SaydinMetrics.WhatIfCalculations`/`CalculationDuration` yerine `DcaCalculations`/`DcaCalculationDuration`. Ayrıca DCA çağrısı `ObserveDcaAsync("dca", ...)` ile tek sabit değerli bir `operation` tag'i üretiyor (WhatIf tarafı `calculate`/`compare`/`reverse` ile anlamlı), yani metrik adı ve tag aynı bilgiyi iki kez taşıyor. Alan adı `SaydinMetrics.CalculationDuration` de artık belirsiz (DCA da bir calculation).

**Neden birinci sınıf değil.** Aynı davranışın üç ayrı yerde bakımını gerektiren, kaçırılmış bir sadeleştirme. Metrik şeması da tutarsız: bir aile operation tag'ini anlamlı kullanıyor, diğeri sabit.

**Nasıl kapanır.** Tek bir `ObserveAsync<T>(Counter<long> counter, Histogram<double> duration, string operation, Func<Task<T>> action)` bırak; `ObserveWhatIfAsync`/`ObserveDcaAsync` ince sarmalayıcılar olsun ya da tamamen kalksın. Daha temizi: `SaydinMetrics.WhatIfCalculations`/`DcaCalculations`'ı tek bir `saydin.calculations.total{kind="whatif"|"dca", operation=..., outcome=...}` ailesine indirge (Prometheus tarafında `kind` ile ayrıştırılabilir), `CalculationDuration` alanını `WhatIfCalculationDuration` olarak netleştir ve `docs/architecture.md` metrik tablosunu güncelle.

### [Low] 'Gelecek tarih olamaz' kuralı WhatIf'te helper, DCA'da iki ayrı satır içi kopya

`src/Saydin.Api/Services/WhatIfCalculator.cs:622-630 (ve 50,95,181,228,416 çağrıları); src/Saydin.Api/Services/DcaCalculator.cs:79-86 ve 119-123` · R18 · DOĞRULANMADI (yalnız üreten agent)

WhatIfCalculator temiz bir `private void EnsureNotFuture(DateOnly? date, string field)` helper'ı tanımlayıp beş yerden çağırıyor. Aynı commit'te DcaCalculator aynı üç satırlık kontrolü iki ayrı yerde satır içi tekrarlıyor (`if (request.EndDate is { } requestedEndDate && requestedEndDate > DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime))` ve `if (endDate > DateOnly.FromDateTime(...))`), ikisinde de aynı resx anahtarını ve aynı `field` değerini elle yazıyor.

**Neden birinci sınıf değil.** Aynı iş kuralının iki farklı kalitede uygulanması. İkinci bir okuyucu için 'neden burada helper var, orada yok' sorusu bırakıyor ve iki endpoint'in davranışının aynı kalması testlere bağımlı hale geliyor.

**Nasıl kapanır.** Ortak bir `internal static class CalculationDateGuards { static void EnsureNotFuture(TimeProvider, IStringLocalizer<ErrorMessages>, DateOnly? date, string field) }` çıkar ve hem WhatIfCalculator hem DcaCalculator onu kullansın. `field` değerini R18-03'teki wire-adı konvansiyonuyla birlikte tek yerde belirle. İki servisin aynı gelecek-tarih girdisine aynı yanıtı verdiğini kilitleyen ortak bir parametrik unit test ekle (FakeTimeProvider ile).

### [Low] Port→surface kabul kuralı iki bağımsız uygulamada yaşıyor (selector policy ve middleware)

`src/Saydin.Api/Runtime/ApiEndpointSurface.cs:43-55; src/Saydin.Api/Middleware/ApiPortBoundaryMiddleware.cs:42-58` · R18 · DOĞRULANMADI (yalnız üreten agent)

`ApiPortEndpointSelectorPolicy.IsAccepted` şu kuralı uyguluyor: `port==0 && !IsProduction()` → test server public; PublicProduct/PublicLiveness → `port==runtime.PublicPort || testServerPublic`; Management → `port==runtime.ManagementPort`. `ApiPortBoundaryMiddleware.Classify` aynı üç ifadeyi (`testServerPublic`, `isPublicPort`, `isManagementPort`) kelimesi kelimesine tekrar hesaplayıp path'e göre aynı kararı veriyor.

**Neden birinci sınıf değil.** Savunma derinliği bilinçli olarak iki katmanlı ama kural iki kez yazılmış; niyet (bağımsız doğrulama) ile risk (sessiz drift) arasında yanlış denge.

**Nasıl kapanır.** Kuralı `ApiPortBoundary`'ye tek statik fonksiyon olarak taşı: `internal static bool IsAccepted(int localPort, ApiEndpointSurface surface, ApiRuntimeContract runtime, IHostEnvironment environment)`. Selector policy doğrudan çağırsın; middleware önce path'ten surface türetip aynı fonksiyonu çağırsın. Bağımsızlık iddiası korunmak isteniyorsa bunu koda değil teste taşı: 'her (port, surface) kombinasyonu için selector ve middleware aynı kararı verir' parametrik testi ekle (`ApiPortBoundaryMiddlewareTests` içinde).

### [Low] Python self-test'lerde `load_validator` modül yükleme helper'ı beş dosyada kopyalanmış

`infrastructure/deployment/volume-contract-self-test.py:13-20; infrastructure/deployment/private-material-self-test.py:13-19; infrastructure/deployment/monitoring-runtime-self-test.py:13-19; infrastructure/deployment/observability-self-test.py; infrastructure/deployment/validation-self-test.py` · R18 · DOĞRULANMADI (yalnız üreten agent)

Aynı 7 satırlık `importlib.util.spec_from_file_location(...)` + `RuntimeError("validator_load_failed")` + `exec_module` bloğu her dosyada tekrar ediliyor; tek fark modül adı ('validator_load_failed' hata kodu bile aynı). `volume-contract-self-test.py` bunu `load(name, path)` olarak, diğerleri `load_validator(path)` olarak adlandırmış — kopyalar isimlendirmede zaten ayrışmış.

**Neden birinci sınıf değil.** Yeni bir doğrulama script'i eklemenin maliyeti gereksizce yüksek; küçük ama tekrar eden bir bakım borcu ve isimlendirme tutarsızlığı.

**Nasıl kapanır.** `infrastructure/deployment/_selftest.py` (veya `infrastructure/_pyutil/loader.py`) altında tek bir `load_module(path: Path, name: str | None = None)` bırak; hata mesajına dosya yolunu ekle (`validator_load_failed:{path.name}`). Beş self-test bunu import etsin. Aynı fırsatta `main() -> int` + `required: dict[str, bool]` + `failed` deseninin de tekrar ettiğini göz önüne alarak küçük bir `assert_all(required, prefix)` yardımcısı ekle — böylece hem çıktı formatı hem exit kodu tek yerden garanti edilir.

---

## Dokümantasyon (11 kayıt)

### [Medium] 023/024 ve yeni admission bucket'ları sahibi olan ADR'lerde yok; remediation raporu 'kusur kalmadı' diyor

`docs/decisions/ADR-010-installation-principal.md:3,43-58; docs/decisions/ADR-003-rate-limiting.md:19-31; docs/architecture.md; docs/high-traffic-checklist.md; docs/analysis/pr-review/07-remediation-progress.md:15` · R02 · CONFIRMED

023/024'ün kararları ve dört yeni admission bucket'ı yalnız kodda ve migration SQL'inde yaşıyor; sahibi olan ADR-010 ve ADR-003 ile architecture.md/high-traffic-checklist.md güncellenmemiş, buna karşın remediation raporu tüm doküman kusurlarının kapandığını iddia ediyor.

**Neden birinci sınıf değil.** 'Neden 5/gün', 'neden /24', 'neden lazy rehash', 'rotasyon neden geri alınamaz' sorularının cevabı hiçbir yerde yazılı olmadığı için ikinci bir okuyucu limitleri veya key rotation planını güvenle değiştiremez; kapanış iddiası bir sonraki reviewer'ı da yanıltır.

**Nasıl kapanır.** ADR-010'u 023/024 ile güncelle (durum satırı, immutable SHA'lar, pending-commit admission, rehash'in tek yönlülüğü). ADR-003'e registration ve calculation-network bucket'larını, subject seçimini (exact IP vs /24) ve CGNAT varsayımını ekle. architecture.md ve high-traffic-checklist.md'deki bucket listelerini beş bucket'a genişlet. 07-remediation-progress.md'nin kapanış iddiasını bu hattaki açık kalemlerle düzelt.

### [Low] docs/architecture.md middleware zinciri diyagramı bu diff'in eklediği iki katmanı hiç göstermiyor

`docs/architecture.md:129,132; src/Saydin.Api/Program.cs:318-350` · R01 · CONFIRMED (doğrulayıcı ek bulgusu)

architecture.md:129 mermaid'i `İstek → ResponseCompression → RequestLocalization → Serilog → ActivityLog → ExceptionHandler → Endpoint` diyor. Gerçek pipeline (Program.cs:318-350): UseForwardedHeaders → ApiPortBoundaryMiddleware → ResponseCompression → RequestLocalization → Serilog → ActivityLog → UseWhen(!IsAdmissionExempt → DistributedSecurityLimiterMiddleware) → ExceptionHandler. Port sınırı ve security limiter — bu değişiklik setinin iki ana güvenlik katmanı — diyagramda ve satır 132'deki açıklamada hiç geçmiyor. `git diff -- docs/architecture.md` bu bölümün hiç dokunulmadığını gösteriyor, oysa aynı dosyanın resilience, DB erişim, DCA ve reel getiri bölümleri kapsamlıca yeniden yazılmış.

**Neden birinci sınıf değil.** Bu diff'in en kritik sıralama kararları (localization'dan önce boundary, ActivityLog'un içinde limiter, ExceptionHandler'ın dışında her ikisi) yalnız Program.cs yorumlarında yaşıyor. CLAUDE.md'nin 'dokümanlar kod değişikliğiyle aynı commit'te güncellenir' kuralı ihlal ediliyor.

**Nasıl kapanır.** architecture.md:129 mermaid'ini gerçek zincirle güncelle (ForwardedHeaders ve ApiPortBoundary dahil, limiter'ın UseWhen koşuluyla) ve satır 132'deki paragrafa limiter'ın neden ActivityLog'un içinde/ExceptionHandler'ın dışında olduğunu ekle. R01-06/R01-07/R01-08 uygulanırsa aynı commit'te diyagramı da düzelt.

### [Low] Doküman ve runbook, writer'ın API host'unu bilerek sonlandırabildiğini ve `fatal_contract` sayacını hiç anlatmıyor

`docs/architecture/activity-logging.md:541-553,919; docs/runbooks/api-errors.md; docs/runbooks/container-restart.md:8; src/Saydin.Shared/Diagnostics/SaydinMetrics.cs:131-141,186-195` · R03 · CONFIRMED

Yeni eklenen `fatal_contract` outcome etiketi ve writer'ın host'u kasıtlı sonlandırma davranışı hiçbir dokümanda veya runbook'ta yer almıyor; sözleşme serisi de yalnız retry_exhausted için materyalize ediliyor, dolayısıyla en riskli davranışın label admission'ı ilk gerçek olaya kadar doğrulanmıyor.

**Neden birinci sınıf değil.** Gece nöbetindeki operatör API crash-loop'unu gördüğünde nedeni yazılı sözleşmede bulamaz; MTTR uzar ve en riskli davranış (ürün API'sinin audit yüzünden ölmesi) belgesiz kalır.

**Nasıl kapanır.** activity-logging.md §4.3'e fatal sınıflandırma tablosunu (hangi SQLSTATE/exception → hangi davranış) ve `fatal_contract` etiketini ekle; api-errors.md'ye 'outcome=fatal_contract görülürse şema/rol drift'i kontrol et, database-migrator --verify-only çalıştır' adımını, container-restart.md'ye de audit writer fail-fast'ini restart nedeni olarak koy; InitializeActivityLogContractSeries'e fatal_contract/cancelled/toxic_row sıfır serilerini ekle.

### [Low] Yeni eklenen kod yorumları İngilizce, çevreledikleri kod Türkçe

`src/Saydin.PriceIngestion/Extensions/HttpResilienceExtensions.cs:30-32,41-44,63,67-69; src/Saydin.PriceIngestion/Mappers/ProviderValueParser.cs:8-10; src/Saydin.PriceIngestion/Mappers/TwelveDataMapper.cs:47-49 (TR) vs :81-83 (EN)` · R06 · CONFIRMED

Bu diff'te eklenen yorumların bir kısmı İngilizce, çevreledikleri mevcut yorumlar ve log mesajları Türkçe; `TwelveDataMapper.cs` gibi tek bir dosyada iki dil yan yana duruyor. CLAUDE.md kod yorumu dili için bir kural içermiyor, dolayısıyla ihlal değil ama proje genelindeki tutarlılığın bozulması.

**Neden birinci sınıf değil.** En kritik sözleşme kararlarının gerekçesi (bütçe nerede uygulanıyor, breaker neden MinimumThroughput=2) projenin ana dilinden farklı bir dilde; CLAUDE.md ve `docs/architecture.md`'nin Türkçe resilience paragraflarıyla çapraz referans ve arama zorlaşıyor. Doğruluk sorunu yok.

**Nasıl kapanır.** CLAUDE.md'ye kod yorumu dili için tek satırlık bir kural ekle ve bu diff'te eklenen yorumları o dile getir. Hangi dil seçilirse seçilsin `HttpResilienceExtensions` yorumları ile `docs/architecture.md` Resilience bölümü aynı terimleri kullanmalı.

### [Low] Control-plane CLI sözleşmesi ve migration numarası dokümanda güncellenmemiş

`CLAUDE.md:434; docs/development-guide.md:116,446,455; src/Saydin.DatabaseMigrator/Program.cs:16; src/Saydin.DatabaseRoleBootstrap/Program.cs:8` · R07 · CONFIRMED

CLAUDE.md:434'ün literal `Console.WriteLine` yasağı ihlal EDİLMİYOR — migrator/role-bootstrap/DQA/DataRepair composition root'ları `Console.Out`/`Console.Error`'ı TextWriter olarak enjekte ediyor, literal `Console.WriteLine` yalnız calendar-data'da. Gerçek boşluk, kuralın 'bounded machine-readable stdout process contract' istisnasını tek bir araca özgü gibi anlatması; oysa aynı desen artık dört control-plane CLI'sında standart ve gerekçe hiçbir yerde tek merkezde toplanmamış. İkinci alt iddia tam olarak doğru: development-guide.md:116 yeni migration için hâlâ `023_*.sql` diyor, oysa 023 ve 024 bu sette eklendi; aynı dosyanın :446/:455 satırlarındaki `-U saydin` örnekleri de artık geçersiz.

**Neden birinci sınıf değil.** Mimari sözleşmenin otoritesi zayıflar: `/check-architecture` veya yeni bir katılımcı yasak listesini birebir uygularsa control-plane composition root'larını gri alanda görür. Daha somutu, development-guide.md migration ekleyecek birine kullanılmış bir numarayı öneriyor; o numarayla ilerlemek trust-root checksum çatışması üretir.

**Nasıl kapanır.** CLAUDE.md'deki console istisnasını 'tüm one-shot control-plane CLI composition root'ları (calendar-data, database-migrator, database-role-bootstrap, data-quality-audit, data-repair) `Console.Out`/`Console.Error`'ı bounded, machine-readable process contract için TextWriter olarak enjekte edebilir' biçiminde genelleştir ve gerekçeyi tek yerde topla. `development-guide.md:116`'yı numaraya bağımlı olmayan bir ifadeyle değiştir ('sıradaki kullanılmamış numara; mevcut en yüksek migration için `infrastructure/postgres/migrations/` dizinine bak'). :446/:455'teki `-U saydin` örneklerini `-U saydin_admin` yap.

### [Low] DQA `docs/` haritasında yok; CLAUDE.md'nin `Console` istisnası control-plane executable'ları kapsamıyor

`docs/README.md:19; docs/architecture.md; docs/runbooks/README.md:18; CLAUDE.md 'Yasak Listesi'; src/Saydin.DataQualityAudit/Program.cs:8` · R08 · CONFIRMED

Finder'ın 'docs/README.md'de hiç geçmiyor' ifadesi DataRepair için yanlıştır (satır 19 runbook'a atıf yapar). Doğrulanan gerçek boşluk: DQA'nın kalıcı dokümantasyonda hiç yer almaması (mimari haritada da, operatör prosedürü olarak da) ve CLAUDE.md `Console` istisnasının control-plane composition root'larını kapsamaması.

**Neden birinci sınıf değil.** Üretimde imzalı kanıt üreten bir servis dokümantasyon haritasında görünmüyor; CLAUDE.md'ye bakan bir agent mevcut control-plane `Console` kullanımını kural ihlali sanıp yanlış 'düzeltme' yapabilir. `07-remediation-progress.md`'nin 'açık doküman kusuru kalmadı' iddiasıyla çelişiyor.

**Nasıl kapanır.** (1) docs/README.md ve docs/architecture.md'ye DQA/DataRepair satırları ekle (servis sınırı: DQA salt-okunur audit login, DataRepair yalnız ingestion login + `ingestion_windows` mutasyonu). (2) `docs/runbooks/data-quality-audit.md` yaz: signed input manifest sözleşmesi, `--production-target-authority-file` dahil argümanlar, `AuditExitCodes` tablosu ve kanıt arşivleme. (3) CLAUDE.md'deki `Console.WriteLine` istisnasını one-shot control-plane composition root'larını kapsayacak biçimde genişlet.

### [Low] `infrastructure/release/README.md` terminal migration konusunda bayat: 'Migration 022 ... frozen'

`infrastructure/release/README.md:37; karşı taraf: src/Saydin.DatabaseMigrator/MigrationTrustRoot.cs:18-19, .github/workflows/release-images.yml:370-371,402` · R12 · CONFIRMED

İddia doğru; fail-closed olduğu için veri/güvenlik riski yok, kayıp yalnız release operatörünün zaman/deneyimidir.

**Neden birinci sınıf değil.** Release operatörünün en çok başvuracağı belge, workflow'un elle girilen iki parametresi hakkında yanlış bilgi veriyor; release yarıda kesilir.

**Nasıl kapanır.** Cümleyi 'terminal migration release anında `infrastructure/postgres/migrations` envanterinden türetilir; `maximum_migration` girdisi bununla eşleşmek zorundadır' şeklinde değiştir ve sabit numara yazma. `check-doc-links.py`'ye 'dokümanda donmuş migration numarası' taraması eklemeyi değerlendir.

### [Low] Otoritatif ilan edilen pr-review kaydı, CONTRIBUTING'in işaret ettiği analysis indeksinden görünmüyor

`docs/analysis/06-remediation-progress.md:6-9 · docs/analysis/README.md:1-25 (değiştirilmemiş) · docs/README.md:12-22 · CONTRIBUTING.md:57` · R15 · CONFIRMED

Otoritatif ilan edilen `pr-review/07-remediation-progress.md` (ve untracked `pr-review2/`) hiçbir dokümantasyon giriş noktasından linkli değil; CONTRIBUTING.md:57'nin işaret ettiği docs/analysis/README.md hâlâ yalnız 2026-08-18 dalgasını 'Tamamlandı' olarak tanıtıyor ve docs/README.md tablosunda `analysis/` satırı yok.

**Neden birinci sınıf değil.** Değişiklik setinin en güncel ve otoritatif ilan edilen kanıt kaydı dokümantasyon giriş yollarından erişilemiyor; 'hangi review geçerli' sorusu okuyucuya kalıyor.

**Nasıl kapanır.** docs/analysis/README.md'ye `pr-review/` ve `pr-review2/` için satır ekle ve üst tarafa 'Bu rapor 2026-08-18 dalgasıdır; güncel kapanış `pr-review/07-remediation-progress.md`'dir' notunu koy; docs/README.md'nin 'Bu repoda ne var?' tablosuna `analysis/` satırını ekle.

### [Low] Dil sözleşmesi belirsiz: runbook'lar ve ADR-009/011 İngilizce, geri kalan doküman gövdesi Türkçe; ADR indeksi İngilizce dosyayı Türkçe başlıkla listeliyor

`docs/runbooks/*.md · docs/decisions/ADR-009-provider-observation-authority.md:1-4 · docs/decisions/ADR-011-prometheus-exporter-prerelease.md:1-6 · docs/decisions/README.md:46 · CLAUDE.md 'Dokümantasyon Standardı'` · R15 · CONFIRMED

Repo iki dilli: runbook'ların tamamı ve ADR-009/ADR-011 İngilizce, diğer üst-seviye dokümanlar ve ADR-001..008/010 Türkçe; CLAUDE.md'nin dokümantasyon standardı bölümü dil için hiçbir kural içermiyor ve decisions/README.md:46 İngilizce ADR-011'i Türkçe bir başlıkla listeliyor.

**Neden birinci sınıf değil.** Yeni runbook/ADR yazan katkıcı hangi dili seçeceğini bilemiyor; indeks başlıkları ile dosya başlıkları uyuşmuyor ve gelecekteki katkılarda dil sapması kaçınılmaz.

**Nasıl kapanır.** CLAUDE.md 'Dokümantasyon Standardı' bölümüne açık bir kural ekle (ör. 'runbook'lar ve operasyonel ADR'lar İngilizce; ürün/mimari dokümanlar Türkçe') ve gerekçesini bir cümleyle yaz. decisions/README.md:46'daki ADR-011 satırının başlığını dosyanın gerçek başlığıyla eşleştir.

### [Low] cache-strategy.md'de Redis Cluster hash tag'i, placeholder gibi okunuyor

`docs/cache-strategy.md:32-34 · src/Saydin.Api/Security/DistributedSecurityLimiter.cs:248-249` · R15 · CONFIRMED

`{security-rate-v1}` doküman placeholder'ı değil, kodun ürettiği literal Redis Cluster hash tag'idir; aynı satırdaki `{hmac}` ve `{feature}` ise gerçek placeholder'lardır ve bu ayrım dokümanda hiç açıklanmamıştır.

**Neden birinci sınıf değil.** Tarama/temizlik komutları yanlış yazılır (operatör hash tag'i gerçek bir değerle değiştirmeye çalışır ve hiçbir key bulamaz); daha kötüsü hash tag'in fonksiyonel olduğu — tek atomik Lua kararındaki tüm bucket key'lerinin aynı slot'a düşmesini sağladığı — anlaşılmadığı için 'temizlik' amacıyla kaldırılabilir.

**Nasıl kapanır.** Tabloya bir dipnot ekle: '`{security-rate-v1}` bir placeholder değildir; süslü parantezleriyle birlikte literal Redis Cluster hash tag'idir ve tek atomik Lua kararındaki tüm bucket key'lerinin aynı slot'a düşmesini sağlar — kaldırılamaz.'

### [Low] Kanonik analiz indeksi güncellenmedi: docs/analysis/README.md hâlâ 'Tamamlandı' diyor ve yeni pr-review setini listelemiyor

`docs/analysis/README.md:1-40; docs/analysis/06-remediation-progress.md:6-9; .github/scripts/check-doc-links.py:14-24` · R18 · DOĞRULANMADI (yalnız üreten agent)

`check-doc-links.py:20-21` `docs/analysis/README.md` ve `docs/analysis/06-remediation-progress.md`'yi REQUIRED_CANONICAL olarak zorluyor. Ancak `docs/analysis/README.md` değişmedi: hâlâ 'Durum: Tamamlandı', 'Review tabanı: main / 9067dd2' diyor, rapor tablosunda yalnız 00-04 var ve 9 dosyalık yeni `docs/analysis/pr-review/` setine (07-remediation-progress.md dahil — 'açık kusur kalmadı' iddiasının bulunduğu belge) hiç atıf yok. `grep -rn "pr-review" docs/analysis/README.md docs/README.md CLAUDE.md CONTRIBUTING.md` boş dönüyor. Yeni set yalnız `06-remediation-progress.md`'nin başlığına eklenen bir satır üzerinden erişilebiliyor.

**Neden birinci sınıf değil.** Kanonik index ile gerçek durum arasında bir tur gecikme; en güncel remediation kaydı indeksten görünmüyor. `check-doc-links.py` kırık link arıyor ama erişilebilirlik/güncellik aramıyor, dolayısıyla kapı bunu yakalamıyor.

**Nasıl kapanır.** `docs/analysis/README.md`'ye 'Review dalgaları' bölümü ekle: dalga 1 (00-04, taban 9067dd2, Tamamlandı) ve dalga 2 ([`pr-review/`](../pr-review/README.md), taban f9f608d) — durum satırını da güncelle. `docs/README.md` tablosuna `analysis/` satırını ekle. `check-doc-links.py`'ye hafif bir erişilebilirlik kapısı ekle: `docs/**/*.md` içinde `docs/README.md`'den link grafiği üzerinden erişilemeyen dosyaları raporla (fail-closed olması gerekmez, warning yeterli) — böylece yeni doküman setleri indekse bağlanmadan kalmaz.

---

## Güvenlik derinliği (9 kayıt)

### [Medium] Edge katmanı istemci kaynaklı X-Forwarded-For'u temizlemiyor; trust sözleşmesinin edge yarısı sürüm kontrolünde tanımlı değil

`infrastructure/deployment/Caddyfile:14; src/Saydin.Api/Security/DistributedSecurityLimiterMiddleware.cs:70-71; src/Saydin.Api/Runtime/ApiRuntimeContract.cs:59-60; infrastructure/deployment/compose.production.yml:301` · R01 · CONFIRMED

Güvenlik açığı yok — zincir her yönde fail-closed (spoof edilen XFF admission'ı geçemez). Gerçek kusur availability ve doğrulanabilirlik tarafında: public API'nin 'istek kabul edilir mi' davranışı, repo'da hiçbir yerde tanımlanmayan ve sürümü bile görünmeyen (digest env değişkeni) bir Caddy varsayılanına bağlı. Somut 503 sonucu Caddy sürümüne göre değişir ve hiçbir uçtan uca test bunu kilitlemiyor.

**Neden birinci sınıf değil.** Caddy imajı yükseltildiğinde davranış iki yönde de sessizce değişebilir: kalıntı bırakan yönde → meşru kurumsal proxy/VPN arkasındaki kullanıcılar kalıcı 503 (üstelik R01-02 nedeniyle sebebi yanıttan anlaşılamaz); ezen yönde → her şey çalışır ama trust sözleşmesi hiç sınanmamış kalır. Regresyon ancak üretimde fark edilir.

**Nasıl kapanır.** Caddyfile'da güveni açık yaz: `reverse_proxy saydin-api:8080 { header_up X-Forwarded-For {remote_host} }` (append değil replace) ve gerekiyorsa global `servers { trusted_proxies static private_ranges }`. Ardından sözleşmeyi gerçek Caddy + API ile bir kabul testine bağla (.github/compose.integration.yml): istemci `X-Forwarded-For: 1.2.3.4` ve `1.2.3.4, 5.6.7.8` gönderdiğinde yanıt 200 olmalı ve limiter gerçek istemci IP'sini saymalı. Caddy digest'ini validate-production.py'nin görebileceği şekilde sürüm kontrolüne al.

### [Low] Limiter HMAC anahtarı, zeroize edilebilir ReadPasswordBytes API'si mevcutken silinemeyen managed string üzerinden yükleniyor

`src/Saydin.Api/Security/DistributedSecurityLimiter.cs:287-292,320; DistributedSecurityLimiterOptions.cs:74-82; src/Saydin.DatabaseSecurity/SecureSecretFile.cs:17-37,74-105` · R01 · CONFIRMED

Doğru. Kodun geri kalanı (candidate hash zeroization EndpointExtensions.cs:207-214, stackalloc + ZeroMemory principal buffer DistributedSecurityLimiter.cs:117-122, Hash içi input zeroization 315) çok yüksek bir hijyen çıtası koyarken HMAC anahtarının kendisi iki adet zeroize edilemez UTF-8 string kopyası olarak heap'te kalıyor.

**Neden birinci sınıf değil.** Savunma derinliği kaybı. Process memory dump (crash dump, /proc/<pid>/mem, hipervizör snapshot) alınırsa byte dizisi Dispose sonrası temiz olsa bile string kopyaları okunabilir kalır.

**Nasıl kapanır.** SecurityLimiterPseudonymizer public ctor'unu `SecureSecretFile.ReadPasswordBytes(options.Value.HmacKeyFile)` kullanacak şekilde değiştir (dönen buffer'ı doğrudan _key yap). DistributedSecurityLimiterOptionsValidator de ReadPasswordBytes çağırıp finally'de zeroize etsin; mevcut `_ = ReadPassword(...)` satırı gereksiz bir string sızdırıyor.

### [Low] Constant-time verifier karşılaştırması yalnız iki fonksiyonda; 021'in begin/commit/revoke yolları hâlâ kısa-devre eşitlikte ve ölü resolve_installation API rolüne açık

`infrastructure/postgres/migrations/023_installation_lifecycle_admission.sql:78-104,130-133; infrastructure/postgres/migrations/021_api_trust_expand.sql:310,421,505; infrastructure/postgres/migrations/024_installation_credential_rehash.sql:36-37` · R02 · CONFIRMED

Sabit-iş verifier karşılaştırması beş credential yolundan yalnız ikisinde (023 commit resolver + 024 rehash resolver) uygulanıyor; begin/commit/revoke hâlâ indeksli bytea eşitliğinde. Aynı zamanda 024'ün preflight'ı, artık hiçbir uygulama çağıranı olmayan `resolve_installation(bytea,smallint)` üzerindeki API EXECUTE grant'ini kalıcı olarak zorunlu kılıyor.

**Neden birinci sınıf değil.** Aynı tehdit için iki farklı ve gerekçelendirilmemiş sözleşme yürürlükte; okuyucu 'verifier karşılaştırması sabit-zamanlıdır' sonucunu yanlışlıkla genelleştirir. Ölü resolver ise rehash yapmayan, karşılaştırması kısa-devreli ikinci bir auth yüzeyi olarak kalıcılaşıyor.

**Nasıl kapanır.** Tehdit modelini yaz: karşılaştırılan değer sunucunun ürettiği bir HMAC çıktısı olduğu için memcmp timing'i istismar edilebilir oracle üretmez — sabit-iş karşılaştırıcısını savunma derinliği olarak nitelendir ve R02-01'deki indeks maliyetiyle takasını belgele. Sonra ya beş yolda da tutarlı uygula (indeksli satır seçiminden SONRA) ya da hiçbirinde kullanma. `resolve_installation(bytea,smallint)` grant'ini yeni bir migration'da REVOKE et (DQA/migrator envanterlerini birlikte güncelleyerek) ve 024'ün preflight'ındaki zorunluluğu kaldır.

### [Low] 32 baytlık pseudonym anahtarı için entropi/zayıf-materyal reddi yok

`src/Saydin.Api/Services/ActivityPrincipalPseudonymizer.cs:25-41; infrastructure/deployment/validate-private-material.py:63-68; tests/Saydin.Api.Tests/Security/ActivityPrincipalPseudonymizerTests.cs:18,39-40` · R03 · CONFIRMED

Pseudonym anahtarı için ne uygulama tarafında ne de deployment validator'ında zayıf-materyal (all-zero, tek bayt tekrarı, placeholder) reddi var; yalnız 32 baytlık uzunluk doğrulanıyor. Mevcut birim testleri tam da bu zayıf şekli kullanarak kabulü kilitlemiş durumda.

**Neden birinci sınıf değil.** Operatör dosyayı `head -c 32 /dev/zero` benzeri bir yolla üretirse tüm kapılar geçer ve pseudonym'ler herkesçe yeniden hesaplanabilir hâle gelir; user_id'siz activity_logs ihracatı elindeki biri principal id'lerle eşleştirme yapabilir. Savunma derinliği kaybı.

**Nasıl kapanır.** validate-private-material.py'nin `binary-32` dalına all-zero / tek-bayt-tekrarı / düşük-benzersiz-bayt reddi ekle ve aynı kontrolü ActivityPrincipalPseudonymizer.Load içinde fail-closed uygula (kuralı installation keyring anahtarlarına da genişlet); testlerde rastgele anahtar kullan.

### [Low] İki ayrı ve birbirinden sapmış redaksiyon uygulaması; ikisi de tırnaklı/JSON anahtar biçimini yakalamıyor

`src/Saydin.PriceIngestion/Workers/ProviderExceptionSanitizer.cs:28-32; src/Saydin.PriceIngestion/Repositories/IngestionWindowRepository.cs:1238-1244` · R05 · CONFIRMED

Doğrulandı. Bugün gerçekleşen bir sızıntı yolu gösterilemiyor (TwelveData `Authorization: apikey <key>` header'ı, OXR `Authorization: Token <appId>` header'ı kullanıyor ve `HttpRequestException` mesajları header taşımıyor), bu yüzden savunma-derinliği kategorisinde Low kalıyor.

**Neden birinci sınıf değil.** İki uygulamanın sapması, gelecekte birinin güncellenip diğerinin unutulmasını neredeyse garanti ediyor; JSON gövdesinde echo edilen bir kimlik bilgisi ne logda ne DB'de redakte edilir.

**Nasıl kapanır.** Tek bir `SecretRedactor` tanımla (Shared veya PriceIngestion içinde); hem `ProviderExceptionSanitizer` hem `Truncate` onu kullansın. Desene `app_id`, JSON biçimi (`"key"\s*:\s*"[^"]*"`) ve `Bearer`/`Basic`/`Token`/`apikey` şemalarını ekle; her biçim için pozitif + negatif birim test yaz.

### [Low] `--production-target-authority-file` doğruladığı manifest'ten türetilebiliyor; sır semantiğiyle ele alınması yanıltıcı

`src/Saydin.DataQualityAudit/EvidenceSigning.cs:268-345; tests/Saydin.DataQualityAudit.Tests/EvidenceSigningTests.cs:285-315` · R08 · CONFIRMED

Dosyanın sır olmadığı doğrulandı; ancak finder'ın 'hiçbir şey sağlamıyor' okuması fazla sert: kontrol çift yönlüdür — dosya varken production iddia etmeyen manifest `production_target_manifest_mismatch` ile, farklı DB/system-id'li manifest ise `production_target_authority_mismatch` ile reddedilir; manifest'in system identifier'ı ayrıca canlı DB'ye karşı doğrulanır (AuditRunner.cs:156-159). Yani dosya, çalışma ortamını belirli bir fiziksel hedefe bağlayan bir deploy-tarafı beyanıdır; kusur güvencenin kendisi değil, sır ritüeli ve 'authority'/'cryptographically bound' adlandırmasının taşıdığından fazlasını vaat etmesidir.

**Neden birinci sınıf değil.** İkinci bir okuyucu (ve testin adı) bunu out-of-band bir sır sanıyor; ileride yanlış tehdit modeline dayanan değişikliklere davetiye çıkarıyor.

**Nasıl kapanır.** Ya dosyayı gerçekten bağımsız bir otoriteye dönüştür (release imzalama anahtarıyla imzalı attestation), ya da `--acknowledge-production-target` gibi dürüst bir adla yeniden adlandırıp `SecureSecretFile`/`FixedTimeEquals`/`ZeroMemory` ritüelini sadeleştir ve testin adını gerçek güvenceye göre düzelt.

### [Low] `VerifiedPhysicalRepairTarget.IsProduction` fiziksel bir özellik değil, planın kendi beyanı

`src/Saydin.DataRepair/RepairTrustLease.cs:10-18,64-90; src/Saydin.DataRepair/DqaEvidenceVerifier.cs:95-97; src/Saydin.DataRepair/Program.cs:119-127` · R08 · CONFIRMED

İddia doğru, ancak kalan risk dar: infrastructure/deployment/compose.production.yml:761 `SAYDIN_ENVIRONMENT: production` değerini sabitliyor ve infrastructure/deployment/validate-production.py:328 bunu zorunlu kılıyor. Downgrade için hem plan imzalama anahtarı hem de çalışma anında `-e SAYDIN_ENVIRONMENT` override'ı gerekir; yani ayrıcalık yükselmesi değil, savunma derinliği ve tip adının fazla vaat etmesi meselesidir.

**Neden birinci sınıf değil.** `VerifiedPhysical...` adı canlı DB'den doğrulanmış bir özellik izlenimi veriyor; bir sonraki okuyucu bu tipe dayanarak yanlış varsayım kurabilir.

**Nasıl kapanır.** Üretim kararını fiziksel bir işarete bağla (ör. `saydin_role_contract` satırında imzalı bir `environment` kolonu ve `VerifyLiveTrustAsync` içinde karşılaştırma); yapılamıyorsa tipi `PlanDeclaredRepairTarget` gibi dürüst bir adla yeniden adlandır ve `IsProduction`'ın kaynağını yorumda yaz.

### [Low] DQA release image'ı locked-mode restore kullanmıyor; yeni DataRepair Dockerfile'ı kullanıyor

`infrastructure/release/Dockerfile.dqa:4-7; src/Saydin.DataRepair/Dockerfile:6-8; infrastructure/postgres/Dockerfile.migrator:8-15` · R08 · CONFIRMED

Locked-mode farkı doğrulandı, ancak 'DQA tek istisna' iddiası yanlış: src/Saydin.Api/Dockerfile:16 ve src/Saydin.PriceIngestion/Dockerfile:12 de lock dosyası kopyalamadan düz restore yapıyor. Ayrıca .github/workflows/ci.yml:60 solution'ı `--locked-mode` ile restore ettiğinden lock drift'i CI kapısında yakalanıyor; image build'inde kalan risk yalnız transitive sürüm kaymasıdır.

**Neden birinci sınıf değil.** Tedarik zinciri politikası image build'lerinde tutarsız uygulanıyor; imzalı kanıt üreten executable da bu tutarsızlığın içinde.

**Nasıl kapanır.** Dockerfile.dqa (ve Api/Ingestion) restore adımlarını lock dosyalarını kopyalayıp `--locked-mode` ile çalışacak biçimde hizala; `infrastructure/release/validate-release.py` içine tüm first-party Dockerfile'lar için locked-mode zorunluluğunu doğrulayan statik bir kontrol ekle.

### [Low] 024 ile supersede edilen `resolve_installation` (021) hâlâ API rolüne EXECUTE ile grantlı ve trust contract'ta pinli — ölü ama canlı yüzey

`infrastructure/postgres/migrations/021_api_trust_expand.sql:257 (resolve_installation gövdesi); infrastructure/postgres/migrations/024_installation_credential_rehash.sql (preflight `has_function_privilege(api_cap,'public.resolve_installation(bytea,smallint)','EXECUTE')` şartı); src/Saydin.DatabaseMigrator/MigrationRunner.cs:1888,1908` · R17 · CONFIRMED (doğrulayıcı ek bulgusu)

024'ün preflight'ı, kendi kurulumu için 021'deki `resolve_installation`'ın API capability rolünde EXECUTE grantı olmasını ŞART koşuyor; MigrationRunner.cs:1888 ve 1908 bu grantı kalıcı ACL trust contract'ının parçası olarak pinliyor. Buna karşın `grep -rn 'resolve_installation(' src/ tests/` sonucuna göre uygulama kodunda tek bir çağıran yok: InstallationRepository.ResolveAsync artık yalnız `resolve_installation_and_rehash` çağırıyor. Yani 024'ün kapatmayı hedeflediği ham `credential.secret_hash=p_secret_hash` eşitlik yolu, SECURITY DEFINER bir fonksiyon olarak API login'i için çalıştırılabilir durumda kalıyor.

**Neden birinci sınıf değil.** Savunma derinliği kaybı ve bakım yükü: supersede edilmiş bir kimlik-doğrulama fonksiyonu hem grantlı hem de migrator'ın ACL sözleşmesinde zorunlu tutuluyor; 024'ün gerekçesi (constant-work comparator) bu yol için geçerli değil.

**Nasıl kapanır.** Yeni numaralı bir migration ile `resolve_installation(bytea,smallint)` üzerindeki API grantını REVOKE et (veya fonksiyonu DROP et), MigrationRunner'daki ACL beklentisini buna göre güncelle ve 024'ün preflight şartını kaldır. Mevcut migration dosyalarını değiştirme; değişikliği 025 olarak ekle.

---

## Performans (9 kayıt)

### [Medium] Admission'da reddedilen her istek yine de bir activity_logs satırına mal oluyor

`src/Saydin.Api/Program.cs:346-350; src/Saydin.Api/Middleware/ActivityLogMiddleware.cs:34-68; tests/Saydin.Api.Tests/Middleware/ActivityLogMiddlewareTests.cs:17-48; infrastructure/prometheus/rules/api.yml:40-46` · R01 · CONFIRMED

Ağ seviyesindeki limiter 429/503'leri ActivityLogMiddleware'in İÇİNDE üretildiği için her reddedilen istek kalıcı bir activity_logs satırı yazıyor; bu, saldırgan-kontrollü ve limitle sınırlanmayan bir yazma yolu (60/dk limitine takılan 10.000 istek yine 10.000 satır üretir). Filtre seviyesindeki admission denetimi kasıtlı ve testle kilitli olduğu için KORUNMALI; yalnız en dıştaki ağ limiter'ı ActivityLog'un önüne alınabilir.

**Neden birinci sınıf değil.** Limiter'ın koruması gereken kalıcı depolama yolu korunmuyor: flood sırasında bounded channel (10k, DropWrite) taşarsa SaydinActivityLogLoss critical alarmı (api.yml:40-46) ateşlenir ve nöbetçi kök neden yerine semptom için sayfa alır; aynı pencerede meşru trafiğin denetim izi de kaybolur.

**Nasıl kapanır.** `UseWhen(... DistributedSecurityLimiterMiddleware)` çağrısını `UseMiddleware<ActivityLogMiddleware>()`'ten ÖNCEYE al. Filtre seviyesindeki admission (endpoint filter) ActivityLog'un içinde kalır, dolayısıyla ProductFailuresBeforeHandler_AreAuditedWithStableOutcome ve denetim sözleşmesi bozulmaz; ağ seviyesi reddi ise yalnız SecurityAdmissionDecisions sayacına yazılır. Değişikliği 'ağ limiter reddi activity_logs satırı üretmez' assertion'ı taşıyan bir testle kilitle.

### [Medium] Her ertelenen geçiş tüm asset'ler için tam backfill aralığını yeniden planlıyor ve aynı takvim readiness sorgusunu iki kez çalıştırıyor

`src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:90-101,372-382; src/Saydin.PriceIngestion/Repositories/IngestionWindowRepository.cs:205-251,1183-1188` · R05 · CONFIRMED

Gözlem doğrulandı, ancak finder'ın (a) önerisi HATALI: `BackfillAsync`'teki ön `EnsureCalendarReadyAsync` çağrısı gereksiz değil — `PlanWindowsAsync` hazır değilken `CalendarNotReadyException` **fırlatıyor** (repo:206-207) ve bu exception `RunAsync`'te yakalanmadığı için (R05-02) tüm süreci düşürür. Ön kontrol, fatal exception'ı yumuşak `calendar_not_ready` ertelemesine çeviren kasıtlı bir kapıdır. Doğru düzeltme onu silmek değil, readiness sonucunu `PlanWindowsAsync`'e parametre olarak geçirmek (veya PlanWindows'un readiness'i fırlatmak yerine döndürmesi).

**Neden birinci sınıf değil.** Retryable bir pencere varken her 5 dakikada 30 asset × (2 tam aralık COUNT + advisory lock'lu transaction + pencere listesi) çalışır — günde on binlerce gereksiz sorgu ve `ingestion_windows` üzerinde gereksiz lock trafiği. Asset sayısı ve geçmiş uzadıkça doğrusal kötüleşir.

**Nasıl kapanır.** (a) Readiness'i tek kez hesaplayıp `PlanWindowsAsync`'e parametre olarak geçir (imzayı `MarketCalendarReadiness` alacak şekilde genişlet). (b) Planlamayı yalnız "yeni gün eklendi" veya "ilk geçiş" durumunda yap; retryable uyanmalarda doğrudan `DrainAsync`'e gir. (c) Readiness'i (release id + coverage) worker içinde kısa TTL'li cache'le.

### [Medium] API metrikleri Prometheus'a iki yoldan da giriyor; ikinci kopyanın tüketicisi yok, filtrelenmiyor

`src/Saydin.Api/Program.cs:102-112; infrastructure/otel/otel-collector.production.yml:46-56, 95-98; infrastructure/prometheus/prometheus.production.yml:17-29` · R11 · CONFIRMED

İddia doğru ve nüansı şudur: yolun kendisi ingestion için zorunlu, gereksiz olan yalnız API metriklerinin ikinci kopyasıdır. Ayrıca collector'ın prometheus exporter'ı `job`/`instance` etiketlerini resource attribute'lardan türettiği için Prometheus tarafında `exported_job` ile ayrışırlar — yani karışıklık değil, saf tekrar söz konusudur.

**Neden birinci sınıf değil.** API metriklerinin (http_server_request_duration histogram bucket'ları ve .NET runtime serileri dâhil) seri sayısı ve scrape yükü iki katına çıkıyor; job filtresini unutan ad-hoc sorgular iki kat sonuç veriyor. docs/architecture/observability.md:26 ikinci yolu 'bilinçli' diyor ama ikisinin de metrik taşımasını gerekçelendirmiyor.

**Nasıl kapanır.** API'nin metrics pipeline'ından `AddOtlpExporter`'ı kaldır (trace/log OTLP'de kalsın) veya collector metrics pipeline'ına `service.name=saydin-api` kaynaklı metrikleri düşüren bir `filter` processor'ü ekle. Seçimi observability.md'de netleştir ve validate-observability.py'ye karşılık gelen statik kontrolü koy.

### [Low] Sabit pencere sınırında 2× burst mümkün ve Retry-After jitter'sız pencere sınırını işaret ediyor

`src/Saydin.Api/Security/DistributedSecurityLimiter.cs:39-40,51,58,156,181,218; SecurityAdmissionProblem.cs:77-82; docs/decisions/ADR-003-rate-limiting.md:35-42` · R01 · CONFIRMED

Doğru. Sabit pencerede 2× burst bilinen bir ödünleşme ama hiçbir yerde kabul edilmiş sonuç olarak yazılı değil; ayrıca günlük kovalarda jitter'sız Retry-After UTC gün dönümünde tüm reddedilen istemcileri senkronize eder ve 86400'lük bir Retry-After istemci kütüphanelerinde sağlıksız uyku davranışına yol açar.

**Neden birinci sınıf değil.** (a) İki komşu pencerede 2×limit geçirilebilir; limitlerin kesin tavan olduğunu sanan okuyucu yanılır. (b) Reddedilen trafik pencere başında yapay tepe oluşturur; günlük kovalarda gün dönümünde toplu dalga.

**Nasıl kapanır.** (1) SetRetryAfter'a bounded jitter ekle (%0-10, tavan pencere süresi). (2) Günlük kovalarda Retry-After'ı makul bir üst sınıra kırp (ör. ≤3600). (3) ADR-003 'Sonuçlar'a sabit pencere seçimini ve kabul edilen ~2× burst payını açıkça yaz.

### [Low] Bisection'ın derinlik/süre bütçesi yok ve tek okuyuculu döngüyü bloke ediyor — DropWrite kaybını büyütebilir

`src/Saydin.Api/BackgroundServices/ActivityLogWriter.cs:163-186,194-211,222-236` · R03 · CONFIRMED (doğrulayıcı ek bulgusu)

BisectAndFlushAsync her yarım için FlushAsync'i yeniden çağırıyor ve her çağrı tam retry bütçesini (MaxAttempts=3, 200ms+400ms backoff) yeniden alıyor. 50'lik bir batch'te ağaç ~99 düğüm; toxic + transient hataların karıştığı bir durumda düğüm başına 3 deneme + 600 ms backoff mümkün, yani onlarca saniye. Bu süre boyunca ExecuteAsync'in `await foreach` döngüsü ilerlemiyor (SingleReader) ve 10.000 kapasiteli bounded channel DropWrite ile dolmaya devam ediyor. Hiçbir yerde bisection derinliği, toplam süre veya toplam deneme sayısı sınırlandırılmamış.

**Neden birinci sınıf değil.** Kuyruk dolar, DropWrite gerçek kayıp üretir ve bu kayıp toxic_row ile aynı critical alarma karışır; kök neden teşhisi zorlaşır.

**Nasıl kapanır.** Bisection'a toplam süre/deneme bütçesi (ör. batch başına 5 sn veya 2×BatchSize deneme) ekle; bütçe aşılırsa kalan satırları tek seferde `outcome="bisect_budget_exhausted"` ile düşür. Bisection sırasında yapılan alt-flush'larda retry'ı devre dışı bırakmayı (isShutdown deseninin aynısı) değerlendir.

### [Low] Degraded DCA sonuçları hiç cache'lenmediği için kalıcı eksik veri senaryolarında her istek bulk fiyat sorgusunu yeniden çalıştırıyor

`src/Saydin.Api/Services/DcaCalculator.cs:246-249, 415-422` · R04 · CONFIRMED

Cache politikası geçici (`inflation_incomplete`/`inflation_unavailable`) ile kalıcı (`purchase_price_unavailable`) veri boşluklarını ayırmıyor; fiyat serisi başlangıcından önce başlayan bir DCA sorgusu asla değişmeyecek bir sonucu her seferinde sıfırdan hesaplıyor. Maliyet gerçek katkı sayısı kadar bulk LATERAL sorgusu (601 değil) ve günlük kota ile sınırlı.

**Neden birinci sınıf değil.** Kalıcı veri boşluğu olan (ör. istemcide '10 yıl' preset'i + geçmişi kısa varlık) popüler sorgularda cache tamamen devre dışı; PostgreSQL'e gereksiz tekrar yük.

**Nasıl kapanır.** Uyarı türüne göre ayır: katalog revizyonuna bağlı `purchase_price_unavailable` için kısa TTL'li (ör. 15 dk) cache'e izin ver, zamanla düzelen enflasyon uyarıları için cache'leme. Alternatif olarak `SkippedPurchaseDates`'i cache envelope'una alıp `IsValid` içinde doğrula.

### [Low] PricePoint cache shape'i değişti ama AuthorityCacheNamespace.Revision bump edilmedi

`src/Saydin.Shared/Entities/PricePoint.cs:27-44; src/Saydin.Api/Repositories/FinalObservationAuthority.cs:50-59; src/Saydin.Api/Services/AuthorityCacheEntries.cs:101-108; src/Saydin.Api/Services/AuthorityCacheNamespace.cs:9; src/Saydin.Api/Services/AssetService.cs:166-170` · R04 · CONFIRMED

Cache'lenen entity shape'i değişti ama bu tam olarak bunun için var olan `AuthorityCacheNamespace.Revision` mekanizması kullanılmadı. Etki 'iki kat Redis yükü / thundering herd' değil, soğuk cache + anahtar başına bir fazladan DEL; asıl somut fark rolling deploy penceresinde eski ve yeni replikaların birbirinin yazdığı entry'leri karşılıklı geçersiz kılıp cache'i sürekli çöpe atması.

**Neden birinci sınıf değil.** Deploy penceresinde price/nearest-price/price-range cache'i etkisiz kalıyor ve PostgreSQL tam yük alıyor; `AuthorityCacheNamespace`'in atomik cutover garantisi (dosyanın kendi XML doc'unda yazan gerekçe) kullanılmadığı için sözleşme kendi belgesiyle çelişiyor.

**Nasıl kapanır.** `AuthorityCacheNamespace.Revision`'ı `authority-final-v2`'ye çek ve `docs/cache-strategy.md`'nin key versiyonlama bölümüne 'cache'lenen entity shape'i değişirse namespace bump zorunludur' kuralını ekle.

### [Low] Sağlayıcı yanıtı iki kez parse ediliyor ve her erişimde tam UTF-16 string materyalize ediliyor

`src/Saydin.PriceIngestion/Adapters/ProviderPayload.cs:10; CoinGeckoAdapter.cs:66,83; TwelveDataAdapter.cs:64,92; EvdsInflationAdapter.cs:80,94` · R06 · CONFIRMED

Üç adapter yanıtı `payload.Bytes` üzerinden parse edip ön-kontrol yapıyor, sonra `payload.Utf8Text` ile mapper'a veriyor ve mapper aynı gövdeyi ikinci kez parse ediyor; `Utf8Text` hesaplanan property olduğu için her erişimde yeni bir UTF-16 string ayrılıyor. Gövde 64 KiB ile sınırlı olduğundan performans etkisi küçüktür; asıl maliyet aynı payload'ın iki ayrı yerde iki farklı gramerle doğrulanmasıdır.

**Neden birinci sınıf değil.** Payload başına iki JSON parse + bir tam UTF-16 kopya (≤64 KB). Asıl zarar bakım tarafında: adapter ön-kontrolü ile mapper doğrulaması ayrı ayrı evrilebiliyor ve R06-01'deki `pair[0]` asimetrisi tam olarak bu yüzden ortaya çıktı.

**Nasıl kapanır.** Mapper imzalarını `JsonElement root` (veya `ReadOnlySpan<byte>`) alacak şekilde değiştir; adapter zaten parse ettiği `document`'ı ve önceden hesaplanmış `payload.Sha256`/`Bytes.Length` değerlerini geçsin. Böylece hem çift parse hem de iki ayrı doğrulama grameri ortadan kalkar. `Utf8Text`'i property yerine bir kez hesaplanan lazy alan yap.

### [Low] WAL hattı günde ~288 restic snapshot üretiyor; `forget` her 5 dakikada, `prune --no-cache` ise ~4000 snapshot üzerinde koşuyor — maliyet/gecikme sınırı yok

`infrastructure/backup/backup-entrypoint.sh:632-651 (her döngüde backup + forget), 464-485 (prune_repository_if_due), 690-701 (restore'da snapshot envanteri); infrastructure/backup/wal-recovery-evidence.py:18 (MAX_INVENTORY = 8 MiB)` · R10 · DOĞRULANMADI (yalnız üreten agent)

Her 300 saniyede bir `restic backup "$spool"` + `restic forget --tag wal --keep-within 14d` çalışıyor → 288 snapshot/gün, 14 günde ~4030 snapshot. `forget` her turda tüm snapshot listesini okumak zorunda. Haftalık `prune` `--no-cache` ile çalışıyor (satır 480), yani index'i S3'ten sıfırdan çekiyor. Restore yolunda `restic snapshots --tag wal,wal-observation --json` çıktısı `wal-recovery-evidence.py`'de 8 MiB envanter sınırına tabi; restic 0.18 snapshot kaydı (summary alanları dahil) ~0,7-1 KB olduğundan 4030 kayıt ~3-4 MB — sınırın altında ama yalnız ~2x pay bırakıyor. Retention süresi uzatılırsa ya da `archive_timeout` düşürülürse sınır aşılır ve restore `wal_snapshot_inventory_too_large` ile fail-closed olur.

**Neden birinci sınıf değil.** Object-store istek/maliyet profili (5 dakikada bir liste + yazma, haftada bir cache'siz tam index okuma) belgelenmemiş ve sınırlanmamış; `prune` süresi 15 dakikalık `--retry-lock` penceresini aşarsa WAL turu `backup_wal_off_host_write_failed` ile ölüp container restart'ına düşer. Ayrıca 8 MiB envanter tavanı, bugün görünmeyen ama parametre değişince aniden restore'u kilitleyecek gizli bir sınır.

**Nasıl kapanır.** WAL snapshot'larını gruplayarak azalt (ör. saatlik bir `wal-observation` snapshot'ı + ara turlarda yalnız yeni segmentleri ekleyen etiketsiz snapshot) veya `forget`'i her turda değil saatte bir çalıştır. `prune`'u ayrı bir one-shot job'a taşıyıp süresini ölç ve metrikle (`saydin_backup_prune_duration_seconds`). `MAX_INVENTORY`/snapshot sayısı ilişkisini README'ye yaz ve `restic snapshots --latest N` ile envanteri sınırla.

---

## Mimari kural (7 kayıt)

### [Medium] Endpoint yüzey metadata'sı ile gerçek endpoint kümesi arasında otomatik değişmez yok

`src/Saydin.Api/Runtime/ApiEndpointSurface.cs:28-41; src/Saydin.Api/Middleware/ApiPortBoundaryMiddleware.cs:56,78-80; src/Saydin.Api/Program.cs:360-379; tests/Saydin.Api.Tests/Middleware/ApiManagementBoundaryHttpTests.cs:33-55` · R01 · CONFIRMED

Doğru. Her iki katman da metadata YOKLUĞUNU fail-open ele alıyor; bu diff'in ana savunma kazanımı (port yüzeyi ayrımı) tamamen elle disipline ve `productEndpoints` grubunu kullanma alışkanlığına bağlı. Hiçbir derleme/test aşaması bunu doğrulamıyor.

**Neden birinci sınıf değil.** Gelecekte `app.MapPost("/admin/...")` gibi bir operatör endpoint'i doğrudan `app` üzerine (grup dışına) map edilirse, hem selector policy hem boundary middleware onu PublicProduct sayar ve Caddy'nin @internal regexp'i yalnız metrics/health-ready/openapi/scalar'ı kapattığı için endpoint public internetten erişilebilir olur. Regresyon ancak üretimde fark edilir.

**Nasıl kapanır.** Saydin.Api.Tests'e gerçek uygulamayı ayağa kaldıran bir contract testi ekle (WebApplicationFactory + EndpointDataSource.Endpoints): (a) her RouteEndpoint ApiEndpointSurfaceMetadata taşımalı, (b) Surface==Management olanların NormalizePath sonrası route pattern kümesi tam olarak {ReadyPath, MetricsPath}, PublicLiveness olanlarınki {LivePath} olmalı. Ayrıca ApiPortEndpointSelectorPolicy.ApplyAsync'te metadata'sız adayı — en azından Production'da — geçersiz say (fail-closed varsayılan).

### [Low] users.id ve installation_credentials.id için repo genelindeki UUIDv7 sözleşmesi yerine Guid.NewGuid() kullanılıyor

`src/Saydin.Api/Endpoints/InstallationEndpoints.cs:54,81,93` · R02 · CONFIRMED

users ve installation_credentials primary key'leri ile rotation_id, repo genelinde tutarlı olan `Guid.CreateVersion7()` yerine `Guid.NewGuid()` (v4) ile üretiliyor; bu, src/ içindeki tek DB-anahtarı v4 kullanımıdır ve gerekçesi (v7'nin oluşturulma zamanını istemciye sızdırması) hiçbir yerde yazılı değil.

**Neden birinci sınıf değil.** Ölçekte PK B-tree'sinde rastgele sayfa erişimi ve daha yüksek write amplification; ayrıca kod tabanında aynı amaç için iki okunabilir konvansiyon.

**Nasıl kapanır.** Kararı ver ve yaz: dışa dönmeyen `credential_id` için `Guid.CreateVersion7()`'e geç; istemciye dönen `principal_id`/`rotation_id` için v4'te kalınacaksa gerekçeyi hem kodda bir yorumla hem ADR-010'da belirt.

### [Low] İki ContractVersion=2 worker'ı hedef gününü iki farklı mekanizmayla çözüyor

`src/Saydin.PriceIngestion/Workers/TcmbWorker.cs:37-41; src/Saydin.PriceIngestion/Workers/TwelveDataWorker.cs:19,43-44; src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:40-47; src/Saydin.PriceIngestion/Repositories/IngestionWindowRepository.cs:139` · R05 · CONFIRMED

Asimetri doğrulandı, ancak finder'ın "R05-01'in kök nedeni bu asimetri" cümlesi YANLIŞ: `ResolveLatestExpectedObservationAsync` sealed takvimden `notAfter`'a kadarki son **observation_expected** günü seçer; bugün trading günüyse hedef yine bugün olur ve provider henüz yayınlamamışsa aynı permanent-fail sonucu doğar (TCMB'de bu R05-V1 olarak gerçekleşiyor). Bu madde bağımsız bir tutarlılık boşluğudur, R05-01'in çözümü değildir.

**Neden birinci sınıf değil.** Aynı contract sürümünde iki farklı doğruluk sözleşmesi yaşıyor; `bist_pay_xist` için yazılmış repository yolu tamamen ölü; yeni takvim-bağlı worker eklerken hangi desenin doğru olduğu belirsiz.

**Nasıl kapanır.** Tek desen seç: ya `ResolveBackfillThroughAsync`'i TwelveData'ya da ekle (`bist_pay_xist`, `notAfter = TargetDate(utcNow)`), ya da base implementasyonu `ContractVersion >= 2` için fail-closed yap (varsayılan `calendar_not_required` yalnız v1'e kalsın). `ResolveLatestExpectedObservationAsync`'in `bist_pay_xist` dalı ya kullanılmalı ya kaldırılmalı.

### [Low] `CanonicalJson` iki executable'da kopyalanmış; paylaşılan-dosya deseni elde varken sürdürülüyor

`src/Saydin.DataQualityAudit/CanonicalJson.cs:1-78; src/Saydin.DataRepair/CanonicalJson.cs:1-74; tests/Saydin.DataRepair.Tests/CanonicalJsonParityTests.cs` · R08 · CONFIRMED

Kopyalama ve MaxDepth farkı doğrulandı, ancak finder'ın tetik senaryosu bugün ulaşılamaz: iki canonicalizer'ın ortak gördüğü tek bayt dizisi DQA'nın ürettiği `manifest.json`'dır (DqaEvidenceVerifier.cs:29-36) ve `EvidenceManifest` şeması sabit ve derinliği ~4'tür; 32'yi aşan bir yapı üretilemez. Dolayısıyla bu bir 'defect' değil, gelecekteki sessiz uyumsuzluk riskini taşıyan bir bakım/sadelik boşluğudur (Low).

**Neden birinci sınıf değil.** Önceki review'in 'imza uyumu elle senkronizasyona dayanıyor' bulgusu kök nedeninden değil 5 örneklik bir testle kapatılmış; iki dosyadan birine yapılacak ileriki bir canonicalization değişikliği test kapsamı dışında kalırsa sessizce ayrışır.

**Nasıl kapanır.** `CanonicalJson`'ı tek kaynak dosyaya indirip diğer projeye `MigrationTrustRoot.cs` ile aynı `<Compile Include ... Link=...>` deseniyle bağla (reddetme kodlarını partial/delegate ile projeye özgü tut) ve `Serialize`/`SerializeCanonical` isim ayrışmasını birleştir. Bu yapılmayacaksa parity testini derinlik sınırı, surrogate pair, `-0`, int64 sınırları gibi vakalarla genişlet.

### [Low] Runtime-image anahtar kümesi 5 kopya halinde yaşıyor; `release-manifest.schema.json` hiçbir kod yolundan okunmuyor

`infrastructure/release/release_manifest.py:17-29 (otorite); infrastructure/release/release-manifest.schema.json:46-62 (ölü); .github/workflows/release-images.yml:355; infrastructure/release/tests/release-manifest-self-test.py:16-27` · R12 · CONFIRMED

Kayıt doğru ama Medium değil Low: self-test'in kümeyi bağımsız olarak yeniden yazması test tasarımı gereği kabul edilebilir (bağımsız restatement), `release-images.yml:355`'teki set ise external-lock (11) kümesi olduğu için `EXTERNAL_RUNTIME_IMAGES` ile eşleşmesi gerekir. Asıl somut kusur, hiçbir kod yolundan okunmayan ve sessizce ayrışabilecek `release-manifest.schema.json`'dır — bu, önceki review'in L109'unun kapanmadığı anlamına gelir.

**Neden birinci sınıf değil.** Şema, otorite sanılabilecek ölü bir belge olarak duruyor; 13. bir runtime image eklendiğinde güncellenmezse kimse fark etmez ve dış tüketici/okuyucu yanlış sözleşmeye bakar.

**Nasıl kapanır.** (a) `release-manifest-self-test.py`'ye şemayı okuyup `set(schema['properties']['runtimeImages']['required']) == set(RUNTIME_IMAGE_ENV_KEYS)` ve `images.minItems == len(EXPECTED_IMAGES)` iddialarını ekle; (b) `release-images.yml:355`'teki inline `names` set'ini `release_manifest.py`'ye bir `validate-runtime-lock` alt komutu olarak taşı; (c) şema otorite değilse başına bunu belirten bir `$comment` koy.

### [Low] `rollback-release.sh` manifest→env bağlamasını kendi inline sözlüğüyle yapıyor

`infrastructure/release/rollback-release.sh:68-96; karşı taraf: infrastructure/release/deploy-release.sh:28-33, render-deployment-env.py:87-101, validate-release.py:82-88` · R12 · PLAUSIBLE

Rollback'te ikinci bir eşleme gerçekten var, ama kapsamı rollback'in mutasyon kümesiyle (api/ingestion/caddy) birebir örtüşüyor ve rendered compose ayrıca `validate-production.py`'den geçiyor. Bu nedenle 'yanlış digest ile açılmış servis' senaryosu için somut bir tetikleyici gösterilemiyor; kayıt Medium değil Low ve 'defect' değil bakım/tutarlılık boşluğu.

**Neden birinci sınıf değil.** İlk parti image kümesi veya bir env anahtar adı değişirse deploy yolu tek otoriteden türetirken rollback yolu ayrıca elle güncellenmek zorunda; incident anında çalışan ve en az test edilen yolda ikinci bir bakım noktası.

**Nasıl kapanır.** `render-deployment-env.py`'ye `--scope application` bayrağı ekleyip rollback'te de `--verify-existing` kullan; `validate-release.py`'nin rollback bölümüne deploy'daki gibi bir inline-sözlük yasağı (`keys = {` / `"SAYDIN_API_IMAGE"`) ekle.

### [Low] RepairApplication.RunAsync production imzasına dört test-only seam eklendi

`src/Saydin.DataRepair/Program.cs:24-28; tests/Saydin.DataRepair.IntegrationTests/RepairDatabaseFixture.cs:130-161` · R18 · DOĞRULANMADI (yalnız üreten agent)

`RunAsync(...)` public imzasına `IRepairDatabaseFaultInjector? databaseFaultInjector`, `int maximumGuardRows = 100_000`, `Action<ReceiptStoreCheckpoint>? receiptCheckpoint`, `Func<RepairTrustLease, CancellationToken, Task>? afterLiveTrust` parametreleri eklendi. `grep` sonucu bu dördünün production'da tek bir çağıranı yok — yalnız `tests/Saydin.DataRepair.IntegrationTests/RepairGuardIntegrationTests.cs:132,164,188,244,264,293` üzerinden `RepairDatabaseFixture.RunAsync` ile kullanılıyor. `maximumGuardRows`'un varsayılanı ayrıca iki yerde tekrarlanıyor (Program.cs:26 `100_000` ve RepairDatabase.cs:65 `DefaultMaximumGuardRows`), yani sabit ikiye ayrılmış.

**Neden birinci sınıf değil.** Bir güvenlik kritik one-shot aracın giriş noktası test affordance'larıyla kirletilmiş. İkinci bir okuyucu bunların operatöre açık ayar mı yoksa test kancası mı olduğunu imzadan anlayamıyor.

**Nasıl kapanır.** Dört seam'i tek bir `internal sealed record RepairTestSeams(IRepairDatabaseFaultInjector? FaultInjector, int? MaximumGuardRows, Action<ReceiptStoreCheckpoint>? ReceiptCheckpoint, Func<RepairTrustLease, CancellationToken, Task>? AfterLiveTrust)` içinde topla, `RunAsync`'e tek `RepairTestSeams? seams = null` parametresi olarak ver ve `InternalsVisibleTo("Saydin.DataRepair.IntegrationTests")` ile yalnız teste aç. `maximumGuardRows` varsayılanını tek otorite olarak `RepairDatabase.DefaultMaximumGuardRows`'ta bırak, Program.cs'teki `100_000` literal'ini kaldır.

---

## Veri bütünlüğü (5 kayıt)

### [Medium] DQ-006 kapsamı imzalı lane asset kümesine daraltıldı; kanıt paketi kapsamı belirtmiyor

`src/Saydin.DataQualityAudit/AuditSql.cs:664-679; src/Saydin.DataQualityAudit/AuditRunner.cs:1095-1102; tests/Saydin.DataQualityAudit.IntegrationTests/AuditDatabaseFixture.cs:1998-2033` · R08 · CONFIRMED

Daraltma ve kanıtın kapsamı belirtmemesi doğrulandı. Ancak finder'ın tetik örneği kısmen yanlış: SignedAuditInput.cs:113-120 non-evds lane'ler için `AssetId is null` olmasını REDDEDİYOR, yani 'coingecko global job' lane'i mümkün değil; $2 yalnız evds-only (enflasyon) manifest'lerde boş kalır. Asıl kalıcı boşluk, scope dışındaki aktif tcmb/twelvedata asset'lerinin calendar binding bütünlüğünün hiç bakılmaması ve bunun imzalı kanıttan okunamamasıdır.

**Neden birinci sınıf değil.** Critical bir kontrolün gerçek kapsamı yalnız manifest hash'i üzerinden dolaylı okunabiliyor; 'DQ-006 clean' ifadesi operatör için takvim bağlama bütünlüğünün tamamının doğrulandığı anlamına gelmiyor. Kazanılan maliyet ise küçük (assets/asset_market_calendars/market_calendars yüzler ölçeğinde).

**Nasıl kapanır.** (a) Bu üç kontrolü global bırakıp ayrı ve küçük bir bütçeyle sınırla, ya da (b) daraltmayı koru ama `AuditCheckResult`'a `Scope` (`global`|`lane_assets`) alanı ekleyip EvidenceContent.SchemaVersion/RulesetVersion'ı bump et. Her iki durumda da evds-only (boş `$2`) manifest'i kapsayan bir integration testi ekle.

### [Low] Onarım önerileri örnek limitinde sessizce kesiliyor; `repair-recommendations.json` kesilme sinyali taşımıyor

`src/Saydin.DataQualityAudit/AuditAccumulator.cs:53-75; src/Saydin.DataQualityAudit/EvidenceBundle.cs:54-60; src/Saydin.DataQualityAudit/AuditModels.cs:79-85` · R08 · CONFIRMED

Kesilme sinyalinin bu dosyada bulunmadığı doğrulandı; ancak finder'ın 'operatör bu dosyayı tek kaynak alır' senaryosu zayıf: öneri satırları yalnız `BusinessKeyHmac` taşıyor ve preimage alanları daima null, dolayısıyla plan yazımı zaten `ingestion_windows` sorgusunu gerektiriyor ve orada tüm ihlal kümesi görülür. Bu nedenle severity Medium değil Low.

**Neden birinci sınıf değil.** Kanıt paketinin iki dosyası arasında elle korelasyon gerekiyor; kısmi onarımın tam onarım gibi okunma riski küçük ama gerçek.

**Nasıl kapanır.** `repair-recommendations.json`'ı `{ schemaVersion, checks: [{ checkId, totalCount, truncated, recommendations }] }` biçimine çevir (R08-08 ile birlikte sürüm bump'ı) veya her öneriye `checkTruncated`/`checkTotalCount` ekle; runbook'a 'truncated=true ise plan bu koşunun tamamını kapsamaz' uyarısını yaz.

### [Low] Receipt dizininin kendi dizin girdileri fsync edilmiyor; yalnız receipt root fsync ediliyor

`src/Saydin.DataRepair/ReceiptStore.cs:92-106,300-330,348-380` · R08 · CONFIRMED

İddia teknik olarak doğrudur; pratikte ext4 varsayılan data=ordered ile pencere dardır, ama kod fsync için özel P/Invoke yazacak kadar özen gösterirken ikinci fsync'i atlıyor.

**Neden birinci sınıf değil.** Rename fsync'i ile pending dizin girdilerinin yazılması arasındaki güç kesintisinde final receipt dizini boş görünebilir; `ValidateReceiptInventory` `receipt_inventory_invalid` verir ve commit edilmiş bir mutasyonun imzalı kanıtı kaybolur.

**Nasıl kapanır.** `WriteNewPrivateFileAsync` çağrılarından sonra pending dizinini, `Directory.Move`'dan sonra final dizinini de `FlushDirectoryToDisk` ile fsync et (root fsync'i koru); mevcut `ReceiptStoreCheckpoint` seam'i ile 'dosya yazıldı, dizin fsync edilmedi' noktasına enjeksiyon testi ekle.

### [Low] Manifest'in `database.trustRootSha256` ve `terminalMigration` alanları çalışan sisteme bağlanmıyor

`infrastructure/release/release_manifest.py:101-104,206,241-245; infrastructure/release/deploy-release.sh:222-225; .github/workflows/release-images.yml:370-371,402` · R12 · PLAUSIBLE

'Doğrulanmamış girdilere dayanıyor' fazla güçlü: alanlar release anında repo envanterinden türetilip imzalanıyor ve DB↔migrator-manifest bağı `--verify-only` ile kuruluyor, dolayısıyla terminalMigration geçişli olarak bağlı. Gerçek boşluk daha dar: (a) deploy/rollback anında manifest'teki terminalMigration ile DB'deki en yüksek `schema_migrations.version` arasında doğrudan bir assertion yok; (b) `trustRootSha256` hiçbir yerde tüketilmiyor ve gerçek trust root yerine kaynak dosya hash'i.

**Neden birinci sınıf değil.** README:9-11'in 'terminal migration ve trust-root hash'ini bağlar' iddiası ile mekanizma tam örtüşmüyor; rollback şema-uyumluluk kararı doğrudan bir DB assertion'ıyla desteklenmiyor. Pratik veri riski düşük (imza + digest pinleme + verify-only zinciri).

**Nasıl kapanır.** (a) `deploy-release.sh`'e verify-only sonrası bir kapı ekle: DB'deki en yüksek `schema_migrations.version` == `manifest.database.terminalMigration` ve `saydin_migration_control.state='ready'`; (b) trust root'u `MigrationTrustRoot.Checksums` üzerinden kanonik bir digest olarak üret, migrator'a `--print-trust-root` alt komutu ekleyip deploy'da karşılaştır; (c) yapılmayacaksa README:9-11'i daralt.

### [Low] `make_binary_secret` yazım sonrası boyut doğrulaması yapmıyor; kısmi yazım kalıcı hâle gelir

`docker-compose.yml:28-35, 39` · R16 · DOĞRULANMADI (yalnız üreten agent)

`make_binary_secret() { ... dd if=/dev/urandom of="$$target" bs=32 count=1 2>/dev/null; chmod 0400 ...; }` — `dd` stderr'i bastırılıyor, yazılan byte sayısı doğrulanmıyor. Guard yalnız `[ ! -s "$$target" ]` (boş mu?). Tüketici `ActivityPrincipalPseudonymizer.Load` tam olarak 32 byte istiyor (`SecureSecretFile.ReadBytes(..., minimumBytes: KeyBytes, maximumBytes: KeyBytes, ...)`, src/Saydin.Api/Services/ActivityPrincipalPseudonymizer.cs:35-38) ve aksi halde `activity_principal_pseudonym_secret_invalid` fırlatıyor. Aynı zafiyet mevcut `make_secret`/`make_base64_secret` pipeline'larında da var (`pipefail` yok; `dd | od | tr` yalnız `tr`'nin exit kodunu döndürür).

**Neden birinci sınıf değil.** Nadir ama kalıcı ve kendini onarmayan bir dev-ortam arızası; kök neden (bozuk secret dosyası) API tarafındaki jenerik `_invalid` mesajından okunamaz.

**Nasıl kapanır.** Her üç üreticiye postcondition ekle: yazımdan sonra `[ "$$(wc -c < "$$target")" -eq 32 ]` (hex için 64, base64 için 44) kontrolü yapıp aksi halde dosyayı silip non-zero dön. Alternatif olarak `set -o pipefail` ekleyip geçici dosyaya yazıp `mv` ile atomik yerleştir.

---

## Doğruluk (3 kayıt)

### [Low] Security ve port-boundary middleware'leri UseExceptionHandler'ın dışında kalıyor

`src/Saydin.Api/Program.cs:319,346-350; docs/architecture.md:129,308-317` · R01 · CONFIRMED

Sıra iddiası doğru; ancak bugün ulaşılabilir bir istisna yolu gösterilemiyor. Dolayısıyla bu, gerçekleşen bir hata değil savunma derinliği ve doküman tutarlılığı kusuru: bu iki katmanda ileride ortaya çıkacak herhangi bir istisna RFC 7807/traceId sözleşmesinin dışına düşer ve mimari doküman middleware zincirini eksik anlatır.

**Neden birinci sınıf değil.** Limiter/boundary katmanında ileride eklenecek bir hata yolu gövdesiz, traceId'siz ham 500 üretir ve ActivityLog o isteğe 200 yazar. Ayrıca docs/architecture.md:129 diyagramı pipeline'ın gerçek şeklini yanlış anlatıyor.

**Nasıl kapanır.** `app.UseExceptionHandler()`'ı `UseWhen(... DistributedSecurityLimiterMiddleware)` satırından ÖNCEYE al — Serilog ve ActivityLog dışarıda kalmaya devam eder (EC-5/EC-FU gerekçesi korunur) ama limiter istisnaları RFC 7807 zarfına çevrilir. ApiPortBoundaryMiddleware localization ile taşınırsa (R01-06) o da kapsama girer. docs/architecture.md:129 mermaid'ini ForwardedHeaders → ApiPortBoundary → ResponseCompression → RequestLocalization → Serilog → ActivityLog → SecurityLimiter → ExceptionHandler → Endpoint olarak güncelle.

### [Low] Action fallback değeri "unknown" DB allowlist'inde yok — yorumun iddiasının tam tersi, garantili 23514 üretir

`src/Saydin.Api/Helpers/ActivityLogBuilder.cs:15-17,134-136; infrastructure/postgres/migrations/023_installation_lifecycle_admission.sql:157-162; tests/Saydin.DatabaseMigrator.Tests/MigrationRunnerIntegrationTests.cs:1774-1781` · R03 · CONFIRMED (doğrulayıcı ek bulgusu)

ActivityLogBuilder.cs:134-135'teki yorum 'bilinmeyen action UnknownFallback fallback ile CHECK constraint ihlali engellenir; row CHECK'te düşmez, bisection retry tetiklenmez' diyor. Ancak `UnknownFallback = "unknown"` (satır 17) ActivityActions.All'da YOK ve 023'teki `enforce_activity_action_allowlist()` dizisinde de yok; migrator testi 1774-1781 tam olarak listede olmayan bir action'ın 23514 CheckViolation ürettiğini kanıtlıyor. Yani fallback, ihlali engellemek yerine garantiliyor. Pratikte ulaşılamaz (ActivityLogMiddleware.ResolveAction:75-93 yalnız allowlist sabitleri döndürüyor, eşleşmeyende null dönüp hiç builder kurmuyor) — bu yüzden Low.

**Neden birinci sınıf değil.** Savunma amaçlı yazılmış fallback, koruduğunu iddia ettiği hatayı üretir; yanlış yorum ikinci okuyucuyu yanıltır ve gerçek bir teşhisi geciktirir.

**Nasıl kapanır.** Ya 'unknown'ı DB allowlist'ine (yeni migration + trigger dizisi + ActivityActions) ekleyip yorumu doğru hâle getir, ya da fallback'i kaldırıp allowlist dışı action'da Build()'in InvalidOperationException fırlatmasını sağla (satır 86-88'deki mevcut fail-fast deseniyle tutarlı). Her hâlükârda 134-135'teki yorumu düzelt.

### [Low] Non-transactional statement allow-list'i ad hoc; birkaç PostgreSQL ifadesi kaçıyor

`src/Saydin.DatabaseMigrator/SqlScriptNormalizer.cs:251-261` · R07 · CONFIRMED

`IsNonTransactional` elle bakılan bir prefix allow-list'idir ve bu diff'te bir boşluk (`CREATE UNIQUE INDEX CONCURRENTLY`) kapatılmıştır. Hâlâ kapsanmayan ve PostgreSQL'de transaction bloğunda çalışamayan ifadeler var: `REFRESH MATERIALIZED VIEW CONCURRENTLY`, `ALTER TABLE ... DETACH PARTITION CONCURRENTLY`, `CREATE/DROP TABLESPACE`, `CREATE SUBSCRIPTION`. Bunlar normalize aşamasında `nontransactional_statement_unsupported` üretmez; uygulama anında SQLSTATE 25001 ile `migration_failed` altında patlar.

**Neden birinci sınıf değil.** Veri bozulması yok (fail-closed), ancak net ve erken bir red yerine geç ve genel bir hata alınır; zincir ortada durur, teşhis için sqlstate'e inmek gerekir ve CI geri bildirimi belirgin biçimde kötüleşir.

**Nasıl kapanır.** Listeyi tamamla ve 'burada ne var, neden var' gerekçesini tek bir yorum bloğunda topla (transaction bloğunda çalışamayan ifadeler, PostgreSQL 16 referansı). Listeyi teste bağla: `SqlScriptNormalizer` unit testlerine her giriş için birer pozitif/negatif vaka ekle ki yeni bir prefix eklenmeden testin kırılması beklenebilsin.

---

## Finansal (1 kayıt)

### [Low] Kültür-değişmezliği testi yalnız kimlik/kanıt string'lerini karşılaştırıyor; parse edilen finansal decimal'lar fingerprint'e dahil değil

`tests/Saydin.PriceIngestion.Tests/Adapters/ObservationAuthorityCultureTests.cs:41-52 ↔ src/Saydin.PriceIngestion/Mappers/ProviderValueParser.cs:11-27` · R14a · CONFIRMED

Kültür-değişmezliği testi kimlik ve ham kanıt string'lerini korur; parse edilen finansal decimal'ları kapsamaz. Bu, CLAUDE.md finansal hassasiyet kuralının en kritik boyutunu (tutarın kendisi) testin dışında bırakır — mevcut kod doğru olsa da NumberStyles/culture değişikliği sessizce geçebilir.

**Neden birinci sınıf değil.** `FinancialNumberStyles`'a `AllowThousands` eklenmesi gibi bir değişiklik tr-TR ile en-US arasında farklı tutar üretse bile bu test yeşil kalır.

**Nasıl kapanır.** Fingerprint'e `tcmb.Close`, `oxr.Close`, `twelve.Close`, `coin.Close`, `evds.IndexValue` değerlerini `ToString(CultureInfo.InvariantCulture)` ile ekle ve her biri için literal beklenen değeri (`30.5m`, `100.5m`, `42000.5m`) ayrıca assert et.

---
