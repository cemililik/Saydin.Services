# Doğrulanmış Critical ve High Bulgular

> Hedef: `development` çalışma ağacındaki commit'lenmemiş değişiklik seti (taban `f9f608d`)
> 20 kayıt. Her biri bağımsız doğrulayıcı agent veya ana agent tarafından kod
> okunarak doğrulandı; doğrulanmayanlar açıkça işaretlidir.

## Özet

| # | Önem | Tip | Hat | Bulgu |
|---:|---|---|---|---|
| 1 | **Critical** | `defect` | R11 | Deploy monitoring readiness döngüsü, yalnız loopback'e bağlı OTel Collector health endpoint'ini |
| 2 | **Critical** | `defect` | R12 | Yeni required CI kapısı `run-development-compose-smoke.sh` bayat `already_applied=25` sabiti ne |
| 3 | **High** | `defect` | R01 | IPv4 /24 ağ kovaları ve registration kotaları paylaşılan NAT altında meşru kullanıcıları bloke  |
| 4 | **High** | `defect` | R01 | Calculation-network günlük kovası principal kontrolünden ÖNCE artırılıyor: tek bir installation |
| 5 | **High** | `defect` | R02 | Rehash resolver, verifier lookup'ını indeksten çıkarıyor: her kimlik doğrulanmış istek installa |
| 6 | **High** | `defect` | R02 | Registration ve calculation admission cap'leri paylaşılan-NAT/CGNAT ağlarında meşru kullanıcıyı |
| 7 | **High** | `defect` | R03 | Sınıflandırıcının varsayılan dalı FatalHost: listelenmemiş SQLSTATE'ler ve Postgres-dışı except |
| 8 | **High** | `defect` | R05 | TwelveData aynı-gün hedefi için "henüz yayınlanmadı" kaçış yolu yok: geciken tek bir EOD bar as |
| 9 | **High** | `defect` | R05 | TCMB de aynı-gün/plansız kapanış tuzağında: `unexpected_404` kalıcı blok üretiyor ve pencere ba |
| 10 | **High** | `defect` | R07 | `ensure` ve migrator v1'e pinli: dokümante ve test edilen `retire` sonrası her deploy kalıcı ol |
| 11 | **High** | `defect` | R09 | Günlük TCMB plan materialization ikinci koşuda kalıcı olarak çöküyor (materialized_plan_conflic |
| 12 | **High** | `defect` | R09 | TCMB coverage guard yalnız son günü denetliyor: coverage_through hafta sonuna denk geldiğinde g |
| 13 | **High** | `defect` | R10 | Yeni restic-wal-observation smoke'u Linux CI'da tempdir temizliğinde PermissionError ile ölüyor |
| 14 | **High** | `defect` | R14a | Günlük TCMB acquisition ikinci koşudan itibaren `materialized_plan_conflict` ile kalıcı olarak  |
| 15 | **High** | `defect` | R14b | RequireLinuxRoot dinamik skip'i CI unit kapısını her koşuda kırar (mekanizma: skip değil, FAIL) |
| 16 | **High** | `defect` | R15 | CLAUDE.md ve CONTRIBUTING.md'deki Compose komutları fail-closed: `--env-file .env.database-runt |
| 17 | **High** | `defect` | R15 | database-role-credential-lifecycle.md prosedürü production sözleşmesinde uygulanamaz: yanlış se |
| 18 | **High** | `defect` | R16 | CLAUDE.md'de belgelenen kök compose komutlarının hiçbiri çalışmıyor (--env-file eksik) |
| 19 | **High** | `defect` | R17 | `resolve_installation_and_rehash` her kimlik doğrulamasında index kullanamayan tarama + satır k |
| 20 | **High** | `defect` | R18 | CLAUDE.md ve CONTRIBUTING.md'deki Compose komutları çalışmıyor — build/test/migrate kapılarının |

---

### 1. Deploy monitoring readiness döngüsü, yalnız loopback'e bağlı OTel Collector health endpoint'ini ağ üzerinden proble ediyor — her deploy `deployment_monitoring_readiness_failed` ile ölür

| | |
|---|---|
| **Önem** | Critical |
| **Tip** | `defect` |
| **Boyut** | correctness |
| **Hat** | R11 — Deployment, Prometheus, Alertmanager, OTEL |
| **Doğrulama** | CONFIRMED (doğrulayıcı ek bulgusu) |
| **Konum** | `infrastructure/release/deploy-release.sh:283-296; infrastructure/otel/otel-collector.production.yml:2-3; infrastructure/deployment/compose.production.yml (otel-collector servisi, healthcheck yok)` |

**Bulgu.** deploy-release.sh:283-296'da bu commit'le YENİ eklenen readiness döngüsü, Prometheus container'ının içinden şu uçları `wget -q --spider` ile yokluyor: `http://alertmanager:9093/-/ready`, `http://otel-collector:13133/`, `http://tempo:3200/ready`, `http://loki:3100/ready` ve dört exporter `/metrics`. Ancak infrastructure/otel/otel-collector.production.yml:2-3 şöyle: `extensions:\n  health_check:\n    endpoint: 127.0.0.1:13133` — health_check extension'ı Collector'ın kendi network namespace'inde YALNIZ loopback'e bind ediyor. `grep -rn 13133 infrastructure/` sadece bu iki isabeti veriyor; Collector'ın compose servisinde healthcheck yok ve validate-production.py:597-608'deki `health_contracts` sözlüğü de bilinçli olarak otel-collector'ı dışarıda bırakıyor. Diğer sekiz uç doğrulanabilir şekilde erişilebilir (alertmanager ve tempo/loki 0.0.0.0'a bind ediyor; prometheus alertmanager/tempo/loki/otel-collector ile `monitoring-core`, exporter'larla `monitoring-scrape`/`blackbox-control`/`host-scrape` ağlarını paylaşıyor). Yalnız 13133 portu container dışından TCP reddi verir.

**Etki.** Dokümante edilmiş release/deploy akışı tamamen kırık: hiçbir release staging veya production'a çıkamaz, üstelik başarısızlık monitoring düzlemi zaten force-recreate edilmişken (API ve Caddy henüz yeni sürüme geçmemişken) gerçekleşir — yani ortam yarım güncellenmiş durumda kalır ve manuel müdahale gerektirir. Bu kapı CI'da hiç çalıştırılmadığı için (yalnız self-hosted deploy runner'ında koşar) statik kapıların hiçbiri yakalamaz.

**Öneri.** Ya otel-collector.production.yml'de `health_check.endpoint`i `0.0.0.0:13133` yap (Collector monitoring-core/telemetry-ingest internal ağlarında, host portu yayınlanmıyor) ve validate-observability.py'ye bunu zorlayan bir token kapısı ekle; ya da readiness döngüsünden `http://otel-collector:13133/` satırını çıkarıp Collector canlılığını Prometheus'un `up{job="otel-collector"}` (8888) hedefi üzerinden doğrula — zaten validate-prometheus-runtime.py:63-70 bu job'ın `health == "up"` olmasını istiyor. Hangi yol seçilirse seçilsin, deploy readiness listesindeki her uç için 'container dışından erişilebilir mi' kontrolünü statik bir kapıya bağla.

---

### 2. Yeni required CI kapısı `run-development-compose-smoke.sh` bayat `already_applied=25` sabiti nedeniyle deterministik olarak kırmızı

| | |
|---|---|
| **Önem** | Critical |
| **Tip** | `defect` |
| **Boyut** | correctness |
| **Hat** | R12 — Release supply chain, CI workflow, kapılar |
| **Doğrulama** | CONFIRMED |
| **Konum** | `.github/scripts/run-development-compose-smoke.sh:178-183; tests/Saydin.DataRepair.IntegrationTests/run-isolated.sh:193; karşı taraf: src/Saydin.DatabaseMigrator/MigrationRunner.cs:240, MigrationManifest.cs:53-60, Program.cs:35-38` |

**Bulgu.** Finder'ın iddiası birebir doğru. Tek düzeltme: satır numaraları 179/183 değil 178-183 (iki ayrı grep) ve `MigrationRunResult`'ın 4. parametresi varsayılan `false` olduğu için beklenen tam çıktı `applied=0; already_applied=26; skipped_optional=0; backup_postbootstrap_required=false` — yani grep yalnız sayı yüzünden eşleşmiyor.

**Etki.** Required `production-assurance` job'ı her push/PR'da son adımda başarısız olur; `docker-build` bu job'a bağlı olduğu için tüm CI zinciri bloke olur. `verify-release-ci-admission.py select` exact-commit başarılı CI koşusu bulamayacağı için release workflow'u da açılamaz. Ayrıca lokal DataRepair integration akışı (`run-isolated.sh`) aynı şekilde kırık. `07-remediation-progress.md`'nin 'açık kusur kalmadı' iddiasını doğrudan çürütür.

**Öneri.** Sabiti kaldır ve sayıyı türet. Örn. smoke script'inde `expected=$(find infrastructure/postgres/migrations -maxdepth 1 -type f \( -name '*.sql' -o -name '*.sh' \) | wc -l)` hesaplayıp `grep -q "applied=0; already_applied=${expected}; skipped_optional=0; backup_postbootstrap_required=false"` yap; `run-isolated.sh:193` için aynısını uygula. Kalıcı çözüm için R12-04'teki tek-kaynak türetmesini uygula ve `validate-workflows.py`'ye 'repo'da `already_applied=<literal>` deseni bulunmasın' kontrolü ekle.

---

### 3. IPv4 /24 ağ kovaları ve registration kotaları paylaşılan NAT altında meşru kullanıcıları bloke ediyor

| | |
|---|---|
| **Önem** | High |
| **Tip** | `defect` |
| **Boyut** | product-ux |
| **Hat** | R01 — API güvenlik ve admission yüzeyi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Security/DistributedSecurityLimiterOptions.cs:17-38; DistributedSecurityLimiter.cs:263-277; src/Saydin.Api/appsettings.json:31-44,47; infrastructure/deployment/compose.production.yml:195-200` |

**Bulgu.** IPv4 istemcileri exact IP + /24 ağ kovalarına konuyor ve kovaların günlük tavanları abone-başı plan kotalarıyla uyumsuz. Repo içinden kanıtlanabilir çelişki: free plan principal başına 20 hesaplama/gün verirken CalculationNetworkDailyLimit tüm /24 için 500/gün — yani bir /24'te günde 25 tam-aktif free kullanıcı tavanı doluyor; premium'un 'limitsiz' vaadi de aynı paylaşılan kovayla çelişiyor. Türk mobil operatörlerinin CGNAT havuzlarında tek /24 binlerce aboneyi fronte ettiği için bu tavanlar hem hesaplamayı hem (RegistrationExactDaily=5 / NetworkDaily=100 ile) onboarding'i kesiyor. Repo'nun kendi kodu CGNAT'ı tanıyor (MaxMindGeoIpResolver.cs:101, activity-logging.md:357 '100.64.0.0/10 — Türk mobil operatörlerinde yaygın') ama limiter tasarımı bunu hiç hesaba katmıyor.

**Etki.** Paylaşılan NAT arkasındaki kullanıcılar için: (a) günde 5 kurulumdan sonra yeni kullanıcı installation credential alamıyor → uygulama hiç kullanılamıyor; (b) /24 başına 500 hesaplama tükendikten sonra o bloktaki HERKES (premium dahil) günün geri kalanında 429 alıyor. Telemetride yalnız bucket/outcome/reason var; paylaşılan-NAT tali hasarı ile gerçek abuse ayırt edilemiyor.

**Öneri.** (1) IPv4'te /24 kovasını scarce günlük bütçe için özne olarak kullanmayı bırak; günlük kotayı principal'a bağla, ağ kovasını yalnız kısa pencereli burst tavanı olarak tut. IPv6'da /64 = abone olduğu için mevcut sıkılık korunabilir — ayrımı AddressFamily'ye göre yap. (2) Registration exact/network günlük tavanlarını CGNAT yoğunluğuna göre yeniden boyutlandır ve üretime almadan önce shadow-mode ile ölç. (3) SecurityAdmissionTelemetry'ye düşük kardinaliteli `family` (v4/v6) etiketi ekle, `bucket=registration|calculation_network, outcome=limited` oranına Prometheus warning kuralı yaz (bkz. R01-03). (4) ADR-003 'Sonuçlar' bölümüne paylaşılan-NAT tali hasarını ve plan kotası ↔ ağ kovası ilişkisini açıkça yaz.

---

### 4. Calculation-network günlük kovası principal kontrolünden ÖNCE artırılıyor: tek bir installation, paylaştığı /24'teki tüm kullanıcıların hesaplama hakkını 24 saatliğine tüketebilir

| | |
|---|---|
| **Önem** | High |
| **Tip** | `defect` |
| **Boyut** | security |
| **Hat** | R01 — API güvenlik ve admission yüzeyi |
| **Doğrulama** | CONFIRMED (doğrulayıcı ek bulgusu) |
| **Konum** | `src/Saydin.Api/Endpoints/EndpointExtensions.cs:64-86; src/Saydin.Api/Security/DistributedSecurityLimiter.cs:166-189,64-75; src/Saydin.Api/appsettings.json:38,47` |

**Bulgu.** EndpointExtensions.cs:64-78 önce `TryAcquireCalculationNetworkAsync` çağırıyor; bu çağrı Lua'nın ikinci döngüsünde (DistributedSecurityLimiter.cs:64-75) kovayı koşulsuz artırıyor. Principal kovası (`TryAcquirePrincipalAsync`, 80-86) ancak SONRA kontrol ediliyor — yani principal limitine takılıp 429 alan bir istek bile paylaşılan /24 günlük bütçesini harcamış oluyor. Kova öznesi yalnız ağ pseudonym'i (`BuildKey("calculation-network-day", networkDigest)`, satır 179); principal hiç karışmıyor. Üretim değeri CalculationNetworkDailyLimit=500/gün/​/24 (compose.production.yml:200), ağ middleware'i aynı IP'ye 60 istek/dk veriyor (ExactIpLimit).

**Etki.** Düşük maliyetli ve pseudonymize edildiği için atfedilemeyen bir komşu-DoS: ~500 istek karşılığında bir /24'ün 24 saatlik hesaplama hizmeti düşürülür. Ne principal başına muhasebe ne de kötü principal'ı izole etme mekanizması var; abuse eden principal kendi 120/dk kovasına takılsa bile paylaşılan bütçe zaten harcanmış olur. R01-01 ile kök nedeni paylaşır ama kasıtlı saldırı yüzeyi olarak çok daha keskindir.

**Öneri.** (1) Günlük scarce bütçenin öznesi olarak IPv4 /24'ü kullanmayı bırak; günlük kotayı principal'a, ağ kovasını yalnız kısa pencereli burst tavanına bağla. (2) En azından sırayı tersine çevir: principal admission'ı calculation-network'ten ÖNCE değerlendir ki principal-limitli istekler paylaşılan bütçeyi harcamasın. (3) Ağ kovası korunacaksa principal başına ağ bütçesinden alınabilecek payı sınırla ve `bucket=calculation_network, outcome=limited` için alarm kur.

---

### 5. Rehash resolver, verifier lookup'ını indeksten çıkarıyor: her kimlik doğrulanmış istek installation_credentials'ı taramaya dönüşüyor

| | |
|---|---|
| **Önem** | High |
| **Tip** | `defect` |
| **Boyut** | performance |
| **Hat** | R02 — Installation kimlik yaşam döngüsü + migration 023/024 |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/postgres/migrations/024_installation_credential_rehash.sql:88-127 (karşılaştır: 021_api_trust_expand.sql:93,97-102,249-251; 023:76-104,130-133)` |

**Bulgu.** 024, 021'in `UNIQUE(hash_key_version,secret_hash)` indeks probe'una dayanan auth lookup'ını, indekslenemeyen bir plpgsql karşılaştırma fonksiyonuna çevirerek O(log n)'den aktif key sürümündeki satır sayısıyla lineer bir taramaya düşürüyor. Ek olarak her başarılı auth, rehash gerekmese bile credential+principal satırlarına koşulsuz FOR UPDATE uygular; geçersiz token'da fallback dalı taramayı sürüm başına ikiye katlar. Doğru desenin örneği repoda zaten var (023'ün rotation_id-seçici commit resolver'ı) — 024 elinde seçici bir anahtar olmadığı için onu uygulayamıyor.

**Etki.** Auth yolu (tüm korumalı endpoint'ler) kurulum sayısıyla lineer yavaşlar; koşulsuz FOR UPDATE salt-okunur auth'u WAL üreten yazma yoluna çevirir (heap tuple kirlenmesi, autovacuum yükü, aynı principal'ın isteklerinin serileşmesi). Geçersiz token gönderen kimliği doğrulanmamış trafik sürüm başına iki tam tarama tetiklediği için ucuz bir amplifikasyon vektörüdür. Karşılığında alınan 'constant-time' faydası zayıftır: karşılaştırılan değer istemcinin yönlendiremediği bir HMAC-SHA256 çıktısıdır.

**Öneri.** Satırı 023'ün kendi desenine göre önce indeksle seç: `WHERE credential.hash_key_version=p_key_version AND credential.secret_hash=p_secret_hash` ile unique index'i kullan; `installation_verifier_matches`'i seçilen tek satır üzerinde ikinci (savunma derinliği) doğrulama olarak bırak. FOR UPDATE'i yalnız `p_key_version<>p_active_key_version` iken al. Sürümler eşitken fallback yeniden okumasını atla (birinci sorguyla özdeş). Değişikliği 100k+ satırlık fixture üzerinde EXPLAIN (ANALYZE, BUFFERS) kanıtına bağla ve bu kanıtı migrator integration testine ekle.

---

### 6. Registration ve calculation admission cap'leri paylaşılan-NAT/CGNAT ağlarında meşru kullanıcıyı dışarıda bırakıyor

| | |
|---|---|
| **Önem** | High |
| **Tip** | `defect` |
| **Boyut** | product-ux |
| **Hat** | R02 — Installation kimlik yaşam döngüsü + migration 023/024 |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/appsettings.json:36-41; src/Saydin.Api/Security/DistributedSecurityLimiter.cs:126-182,241-278; src/Saydin.Api/Endpoints/EndpointExtensions.cs:91-115; src/Saydin.Api/Endpoints/InstallationEndpoints.cs:17-20` |

**Bulgu.** Kayıt admission'ı, kullanıcının kontrol edemediği ağ özelliğine (exact IP ve /24) bağlı sabit günlük tavanlar uygular (5/gün exact, 100/gün ağ) ve tavan aşıldığında kurtarma yolu olmadan gün sonuna kadar reddeder. Aynı desen hesaplama endpoint'lerinde /24 başına 500/gün olarak tekrarlanıyor. Paylaşılan NAT/CGNAT altındaki meşru kullanıcı uygulamayı hiç açamaz.

**Etki.** Kötüye kullanım maliyeti saldırgandan meşru kullanıcıya kaydırılmış; onboarding başarısızlığı kalıcı (gün sonuna kadar), kurtarılamaz ve hata mesajı nedeni açıklamadığı için destek tarafında teşhis edilemez. Türkiye mobil operatörlerinde CGNAT yaygın olduğundan mobil trafiğin bir bölümünde sessiz kurulum kaybı beklenir.

**Öneri.** Exact-IP günlük tavanını en az bir mertebe yükselt ve asıl kapıyı ağ pseudonym'i + adaptif eşiğe taşı; ya da tavan aşımında reddetmek yerine maliyet uygula (proof-of-work, App Attest/Play Integrity, veya yeni principal'a düşük ilk-gün kotası). CalculationNetworkDailyLimit'i mutlak sayı yerine ağdaki farklı principal sayısına oranlı bir eşiğe çevir. Varsayılanları production'da ölçülmüş `bucket=registration, outcome=limited` oranı olmadan sabitleme; CGNAT varsayımını ve seçilen takası ADR-003'e yaz.

---

### 7. Sınıflandırıcının varsayılan dalı FatalHost: listelenmemiş SQLSTATE'ler ve Postgres-dışı exception'lar tüm API host'unu düşürüyor

| | |
|---|---|
| **Önem** | High |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R03 — Activity logging ve pseudonymization |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/BackgroundServices/ActivityLogBatchStore.cs:63-71,74-81; src/Saydin.Api/BackgroundServices/ActivityLogWriter.cs:49-55,137-141; src/Saydin.Api/Program.cs (HostOptions yok); tests/Saydin.Api.Tests/BackgroundServices/ActivityLogWriterTests.cs:34` |

**Bulgu.** ActivityLogWriteFailureClassifier'ın hem PostgresException hem de generic dalında varsayılan sonuç FatalHost'tur (satır 71 ve 81); 66-69'daki açık 42xx/3D000/3F000/28xx listesi davranışsal olarak ölü koddur. Writer bu sınıfta exception'ı ExecuteAsync dışına fırlatır, Saydin.Api'de HostOptions ayarlanmadığı için .NET varsayılanı StopHost devreye girer ve tüm API host'u durur (drain de atlanır). Kapsam yalnız 'şema/auth drift'i' değildir: listelenmemiş her SQLSTATE ve Postgres-dışı her beklenmedik exception aynı sonucu verir.

**Etki.** Audit yazma yolundaki tek bir sınıflandırılmamış DB/EF hatası ürün API'sinin tamamını (hesaplama, senaryo, config uçları) kapatır; restart politikası altında koşul sürdüğü sürece crash-loop oluşur ve her restart kuyruktaki 10.000'e kadar activity log'u kanıtsız yok eder. Eski review'in High bulgusu kök neden kapatılmadan 'verified' işaretlenmiş.

**Öneri.** (a) ActivityLogBatchStore.cs:71 ve 81'deki varsayılanı TransientBatch (veya bounded-retry sonrası drop) yap; fatal'i 66-69'daki açık allowlist'e indir — o zaman liste anlam kazanır. (b) Saydin.Api Program.cs'e `Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = Ignore)` ekleyip writer içinde bounded restart/backoff kur; host'u öldürmek yerine readiness'i unhealthy işaretle. (c) ActivityLogWriterTests'e 25006/55006/0A000/58030 InlineData'larını ve XX000'in yeni beklenen sınıfını ekle. (d) 07-remediation-progress.md:55-56'daki daraltılmış iddiayı koda uydur.

---

### 8. TwelveData aynı-gün hedefi için "henüz yayınlanmadı" kaçış yolu yok: geciken tek bir EOD bar asset'i kalıcı olarak kilitliyor

| | |
|---|---|
| **Önem** | High |
| **Tip** | `defect` |
| **Boyut** | data-integrity |
| **Hat** | R05 — PriceIngestion worker/repository |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Workers/TwelveDataWorker.cs:27-44; src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:84-101,246-262; src/Saydin.PriceIngestion/Repositories/IngestionWindowRepository.cs:314-341; src/Saydin.PriceIngestion/Adapters/AdapterCompleteness.cs:38-42; src/Saydin.PriceIngestion/appsettings.json:24` |

**Bulgu.** Doğrulandı, tek düzeltmeyle: finder "TCMB `ResolveLatestExpectedObservationAsync` sayesinde yayınlanmamış günü hiç istemiyor" diyor — bu YANLIŞ. TcmbWorker.cs:37-41 hedefi sealed takvimden seçse de `ProviderCutoff` 16:30'dan sonra **bugünü** notAfter yapıyor ve takvim bugünü `observation_expected` işaretliyorsa hedef yine bugün oluyor; TcmbAdapter.cs:61-63 o gün için 404 gelirse `PermanentFailure("unexpected_404")` üretiyor. Yani evidence-bounded hedef seçimi bu sınıfa karşı koruma sağlamıyor (ayrı bulgu olarak eklendi: R05-V1). TwelveData'ya özgü asıl fark, cutoff'un provider yayın gecikmesine karşı sıfır marjla (18:20 vs 18:20) ayarlanmış olması ve hiçbir "henüz yayınlanmadı" sınıfının bulunmaması.

**Etki.** BIST asset'inin fiyat ingestion'ı ilk geç yayınlanan barda kalıcı olarak durur; sonraki günlerin pencereleri head-of-line nedeniyle hiç işlenmez. Freshness gauge'ı `min(data_through)` üzerinden kaynak seviyesinde lag gösterir ama hangi pencere/asset'in bloke olduğu görünmez (R05-07). Kurtarma yalnız imzalı DataRepair planıyla mümkün.

**Öneri.** TwelveData için "tek eksik gün == aralığın son günü ve daha eskiler tam" durumunda `RetryableFailure("not_published_yet")` sınıfı ekle (EVDS deseni), `provider_error`/boş-values ayrımını da bu kurala tabi tut. Ayrıca `ProviderSettlementDelayMinutes` varsayılanını sıfır marjdan çıkar (ör. 45-60 dk) ve `DailyRunUtcTime`'ı cutoff'tan sonraya al. Uzun vadede `incomplete_observation_set` gibi "eksik ama tekrar denenebilir" sonuçları sınırlı deneme sayısından sonra permanent'a düşen ayrı bir duruma bağla.

---

### 9. TCMB de aynı-gün/plansız kapanış tuzağında: `unexpected_404` kalıcı blok üretiyor ve pencere bayat calendar release'e ömür boyu bağlı kaldığı için requeue bile kurtarmıyor

| | |
|---|---|
| **Önem** | High |
| **Tip** | `defect` |
| **Boyut** | data-integrity |
| **Hat** | R05 — PriceIngestion worker/repository |
| **Doğrulama** | CONFIRMED (doğrulayıcı ek bulgusu) |
| **Konum** | `src/Saydin.PriceIngestion/Adapters/TcmbAdapter.cs:59-63,183-190; src/Saydin.PriceIngestion/Workers/TcmbWorker.cs:37-41,45-53; src/Saydin.PriceIngestion/Repositories/IngestionWindowRepository.cs:355-368,1163-1177` |

**Bulgu.** `TcmbWorker.ResolveBackfillThroughAsync` hedefi sealed takvimden seçiyor ama `notAfter = ProviderCutoff(utcNow)` 16:30 Istanbul'dan sonra **bugün** oluyor; takvim bugünü `observation_expected` işaretliyse hedef bugündür. `TcmbAdapter.FetchDayXmlAsync` (:183-190) 404'ü "yayın yok" olarak `null` döndürüyor, `FetchRangeAsync` (:59-63) ise takvimin kapalı işaretlemediği bir gün için 404 gelirse `AdapterOutcome.PermanentFailure("unexpected_404")` üretiyor → `PersistTypedFailureAsync` (BaseAssetWorker.cs:250-251) `PermanentFailed` → `ClaimNextAsync` (:336-341) `PermanentBlocked` → o asset için head-of-line blok. Daha da kötüsü, pencere ilk claim'de `window.CalendarReleaseId ??= readiness.ReleaseId` (repo:367) ile bir release'e **kalıcı** olarak bağlanıyor ve sonraki readiness kontrolleri `boundReleaseId` üzerinden yürüyor (repo:1163-1177); `GetExpectedNoDataDatesAsync` de o eski release'i okuyor. Düzeltilmiş yeni bir takvim release'i aktive edilse bile bloke pencere eski release'e bağlı kalır. DataRepair'in tek ilgili operasyonu `requeue_permanent_window` (SignedRepairPlan.cs:94-102); release'i yeniden bağlayan bir operasyon yok — yani requeue aynı 404'e tekrar düşer.

**Etki.** O gün için TCMB'ye bağlı **tüm** aktif asset'lerin (USD, EUR vb. çekirdek FX serisi) pencereleri `permanent_failed` olur ve head-of-line blok nedeniyle sonraki günler hiç işlenmez. Kurtarma imzalı requeue ile bile mümkün değil (aynı bayat release'e bağlı kalıp tekrar 404 alır); manuel satır cerrahisi gerekir. Fiyat verisi süresiz olarak bayatlar.

**Öneri.** (a) Takvimin beklediği bir günde 404 gelmesini `PermanentFailure` yerine sınırlı sayıda denemeden sonra permanent'a düşen retryable bir sınıf yap (ör. `provider_publication_pending`). (b) `PermanentFailed` pencereler için `calendar_release_id`'yi sıfırlayıp yeni aktif release'e yeniden bağlayan bir operatör yolu ekle (repository metodu + imzalı DataRepair operasyonu). (c) `docs/runbooks/calendar-release.md` ve `ingestion-stale.md`'ye "sealed release sonrası ilan edilen kapanış" senaryosunu ve kurtarma adımlarını yaz.

---

### 10. `ensure` ve migrator v1'e pinli: dokümante ve test edilen `retire` sonrası her deploy kalıcı olarak kırılıyor

| | |
|---|---|
| **Önem** | High |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R07 — Migrator + RoleBootstrap + DatabaseSecurity |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.DatabaseRoleBootstrap/RoleBootstrapDatabaseOperations.cs:250-252; src/Saydin.DatabaseMigrator/MigrationRunner.cs:478,528; src/Saydin.DatabaseSecurity/RoleContract.cs:145-155` |

**Bulgu.** Managed login yaşam döngüsünün `retire` ayağı, geri kalan control-plane ile uyumsuz. `verify` bilinçli olarak version-agnostik hale getirildi, fakat (a) `EnsureMembershipsAsync` hâlâ `Login(purpose, 1)`'e GRANT deniyor, (b) migrator `AllRolesForVersion(1)`'in tamamının var olmasını şart koşuyor, (c) `RoleContract.ContractMaterial` sözleşme hash'ini v1 rol grafiği üzerinden kuruyor. Bu nedenle runbook'un ve integration testinin gösterdiği `rotate v2 → retire v1` dizisi tamamlandıktan sonra `ensure` 42704 ile, migrator ise `managed_role_contract_mismatch` ile fail-closed olur. Ayrıca drain-timeout sonrası NOLOGIN bırakılan rol, sonraki `ensure`'ü `managed_role_attribute_mismatch` ile durdurur. Test yalnız gevşetilmiş `verify`'ı çağırdığı için bu kapıların hiçbiri test edilmiyor.

**Etki.** Retire (veya timeout'lu retire denemesi) sonrası production deploy zinciri kalıcı olarak başlamaz: role-bootstrap ensure ve database-migrator sırasıyla fail-closed olur, ikisi de api/ingestion/calendar/audit/data-repair için `service_completed_successfully` ön koşuludur. `ensure` düşen v1'i yeniden yaratmaz, compose'da yönetilecek sürümü seçen bir argüman yoktur ve runbook manuel rol/grant müdahalesini yasaklar.

**Öneri.** 1) `EnsureMembershipsAsync`'i `ReadManagedLoginRolesAsync` sonucundan türet; `EnsureAsync`'teki `MinBy` ile rotate/reset'teki `Max` semantiğini tek bir 'current' tanımında birleştir (veya farkı kodda gerekçelendir). 2) Migrator'da `AllRolesForVersion(1)` şartını 'purpose başına marker'ı çözülen en az bir current login' sözleşmesine çevir. 3) `RoleContract.ContractMaterial`'ın v1 rol/membership satırlarını sürümden bağımsız bir gösterime taşı, aksi halde retire her zaman contract hash'ini de bozar. 4) Kapıyı kapatan integration test: gerçek PG üzerinde `ensure → rotate v2 → retire v1 → ensure → database-migrator --verify-only` zincirinin tamamının yeşil olmasını iste; ayrıca drain-timeout sonrası `ensure`'ün geçtiğini doğrulayan ikinci bir vaka ekle.

---

### 11. Günlük TCMB plan materialization ikinci koşuda kalıcı olarak çöküyor (materialized_plan_conflict) — otomasyon 2. günden itibaren ölü

| | |
|---|---|
| **Önem** | High |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R09 — calendar-data ve calendar infrastructure |
| **Doğrulama** | CONFIRMED (ana agent) |
| **Konum** | `tools/calendar-data/src/Saydin.CalendarData/SecureBundleStorage.cs:51-64; tools/calendar-data/src/Saydin.CalendarData/CalendarPlanMaterializer.cs:45; infrastructure/calendar/run-acquisition.sh:43-55` |

**Bulgu.** `WritePrivateFileIdempotent` hedef dosya varsa ve içerik BYTE-EŞİT DEĞİLSE `throw new CalendarDataException(conflictCode, target)` yapar; üzerine yazmaz. `run-acquisition.sh` planı (`/var/lib/saydin/calendar/plans/tcmb-daily.json`) materialize etmeden önce silmez/rotate etmez; script `set -eu` ile çalıştığı için container'ın exit 2'si tüm job'ı düşürür. Repo genelinde planı temizleyen tek bir satır yok (`grep -rn 'plans/' infrastructure/calendar docs/runbooks/calendar-release.md` → yalnız env örneği ve README). Ampirik doğrulama (pinned SDK imajı, scratchpad kopyası): 1. koşu `snapshotSetId: cal-tcmb-2026-08-26` planını yazdı; plan dosyası bir önceki günün içeriğiyle değiştirilip aynı komut tekrar çalıştırıldığında çıktı `materialized_plan_conflict: /src/out/tcmb-daily.json` ve `exit=2` oldu.

**Etki.** Dokümante edilen (README + runbook: "TCMB plan materialization is deterministic: a second run is a verified no-op") günlük otomasyon ilk başarılı koşudan sonra hiç çalışmaz. TCMB coverage ilerlemez; 1-2 gün içinde `saydin_market_calendar_coverage_horizon_days{tcmb_indicative_fx} < 0` olur, `SaydinTcmbCalendarCoverageStale` critical firing'e geçer ve worker `calendar.not_ready` ile TCMB pencerelerini reddetmeye başlar → FX ingestion durur. Önceki review'in "günlük timer statik planla ikinci koşuda kesin başarısız" High'ı kök nedeni kapatılmadan, yalnız aynı-gün idempotency'sine indirgenmiş.

**Öneri.** Materialize adımını gerçekten ilerletilebilir yap: (a) plan dosyasını içerik-adresli/tarihli isimle yaz (`plans/tcmb-<cutoff>.json`) ve script'te en yeni dosyayı seç, ya da (b) `WritePrivateFileIdempotent`'a atomik replace modu ekle (tmp dosyaya yaz + `File.Move(overwrite:true)`), conflict'i yalnız aynı gün/aynı snapshotSetId için sakla. Her iki durumda da `CalendarPlanMaterializerTests`'e farklı `utcNow` ile ikinci koşuyu doğrulayan bir test ekle; `InfrastructureCalendarContractTests` script kontratında plan rotasyonunu assert et.

---

### 12. TCMB coverage guard yalnız son günü denetliyor: coverage_through hafta sonuna denk geldiğinde gerçek işlem günleri hâlâ sessizce `no_publication` oluyor

| | |
|---|---|
| **Önem** | High |
| **Tip** | `defect` |
| **Boyut** | data-integrity |
| **Hat** | R09 — calendar-data ve calendar infrastructure |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `tools/calendar-data/src/Saydin.CalendarData/CalendarDataGenerator.cs:176-181 ve :261-272` |

**Bulgu.** Guard koşulu: `cursor.Year == through.Year && cursor.Month == through.Month && !published.Contains(through) && through.DayOfWeek is not (Saturday or Sunday)` — yalnız `through` gününe bakar, aradaki yayımlanmamış hafta içi günlere bakmaz. `ResolveTcmbCoverageThrough` hafta sonu dalında `_ = ResolveLatestTcmbPublication(...)` ile sonucu ATAR ve `return requestedThrough;` der; yani "herhangi bir tarihte bir yayın var" kanıtı yeterli sayılır, `through`'dan önceki son iş gününün yayınlandığı doğrulanmaz. Ampirik doğrulama: taban bundle'ın `coverageThrough` değeri `2026-08-30` (Pazar) yapıldığında `generate` **exit=0** verdi ve `tcmb_indicative_fx,2026-08-19..2026-08-28` arasındaki 8 hafta içi gün `false,no_publication,official_archive_absence` olarak üretildi — hiçbir uyarı çıkmadı.

**Etki.** Önceki review'in "TCMB coverage yayınlanmamış güne ilerletilebiliyor ve gerçek işlem günü sessizce no_publication oluyor" High'ı tamamen kapanmamıştır; yalnız hafta içi son gün senaryosu kapatılmıştır. `observation_expected=false` yazılan gerçek bir yayın günü için worker hiç fiyat çekmez ve eksiklik "resmî yokluk" olarak mühürlenir — sessiz, kalıcı veri boşluğu. Tek savunma, runbook adım 3'teki insan karşılaştırmasıdır (otomatik kapı değil).

**Öneri.** Guard'ı son güne değil, `through`'a kadar olan tüm hafta içi günlere uygula: `through`'dan geriye doğru ilk hafta-içi günü bul ve o gün `published` kümesinde yoksa fail-closed ol; ayrıca aralıktaki ardışık yayımlanmamış hafta içi gün sayısına bir üst sınır (örn. TCMB'nin en uzun resmî tatili) koy ve aşımda `tcmb_publication_gap_unexplained` fırlat. `ResolveTcmbCoverageThrough` hafta sonu dalında dönen kanıtı atmak yerine `latestPublication >= requestedThrough.AddDays(-3)` gibi bir yakınlık koşulu uygula. Yeni `CalendarCoverageEvidenceTests`'e hafta sonu senaryosu için negatif test ekle.

---

### 13. Yeni restic-wal-observation smoke'u Linux CI'da tempdir temizliğinde PermissionError ile ölüyor; zorunlu `production-assurance` job'ı kalıcı kırmızı

| | |
|---|---|
| **Önem** | High |
| **Tip** | `defect` |
| **Boyut** | test-quality |
| **Hat** | R10 — infrastructure/backup |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `infrastructure/backup/tests/restic-wal-observation-smoke.py:14-21,30-35,59-64 (çağıran: infrastructure/backup/tests/backup-static-self-test.py:296-298; .github/workflows/ci.yml:95-97,120)` |

**Bulgu.** Smoke, host tempdir'ini (`tempfile.TemporaryDirectory`) bind-mount edip `docker run --user 0:0 ... restic init` çalıştırıyor. Restic yerel backend'i dizinleri 0700 ile açar; yerel Docker ile üretildi: `drwx------ 501 0 repository` ve altında `data/`, `index/`, `keys/`, `locks/`, `snapshots/` hepsi 0700. Linux'ta bu dizinler gerçekten uid 0'a ait olur (macOS Docker Desktop sahipliği host kullanıcısına remap ettiği için yerel koşuda maskeleniyor — bu makinede test PASSED verdi). Linux + Python 3.12 ile birebir üretildi: root'a ait 0700 alt ağaç içeren bir tempdir için `tempfile.TemporaryDirectory._rmtree` → `PermissionError [Errno 1] Operation not permitted: '/probe/tmpdir/repository'` (CPython `_resetperms`'in `os.chmod`'u root'a ait dosyada da başarısız oluyor ve yalnız FileNotFoundError yutuluyor). Smoke'un sarmalayıcısı yalnız `except RuntimeError` yakalıyor (satır 62), dolayısıyla istisna kaçıyor ve süreç 1 ile çıkıyor.

**Etki.** `required["restic_wal_observation_behavior"]=False` → `backup_static_failed:restic_wal_observation_behavior` → static self-test exit 2 → zorunlu `production-assurance` job'ı her koşuda kırmızı; tüm merge/release akışı bloke. Ayrıca `07-remediation-progress.md`'nin "açık kusur kalmadı" iddiası CI'ın ilk koşusunda geçersizleşir. Sessiz değil, ama tamamen yeni ve kendi eklediği bir kapı kırığı.

**Öneri.** Diğer iki smoke'un yaptığı gibi bind-mount yerine adlandırılmış Docker volume kullan (`docker volume create` + sonda `docker volume rm`), ya da repository dizinini container içinde `--user "$(id -u):$(id -g)"` ile oluştur, ya da en azından `finally` bloğunda `docker run --rm --user 0:0 -v ...:/fixture busybox rm -rf /fixture/repository` ile root'a ait ağacı sil. Sarmalayıcıyı `except Exception`'a genişlet ki temizlik hatası da anlaşılır bir mesajla raporlansın.

---

### 14. Günlük TCMB acquisition ikinci koşudan itibaren `materialized_plan_conflict` ile kalıcı olarak kırılıyor; test yalnız aynı zaman damgasıyla idempotency ölçüyor

| | |
|---|---|
| **Önem** | High |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R14a — PriceIngestion + calendar test kalitesi |
| **Doğrulama** | CONFIRMED (doğrulayıcı ek bulgusu) |
| **Konum** | `tools/calendar-data/src/Saydin.CalendarData/CalendarPlanMaterializer.cs:16, :36-46; tools/calendar-data/src/Saydin.CalendarData/SecureBundleStorage.cs:51-65; tools/calendar-data/src/Saydin.CalendarData/Program.cs:7-14, :62-66; infrastructure/calendar/run-acquisition.sh:43-55; infrastructure/calendar/calendar-acquisition.env.example:4; infrastructure/calendar/systemd/calendar-acquisition@.service; tools/calendar-data/tests/Saydin.CalendarData.Tests/CalendarPlanMaterializerTests.cs:12-20` |

**Bulgu.** `MaterializeTcmb` planı `SecureBundleStorage.WritePrivateFileIdempotent(outputPath, ManifestJson.Write(plan), "materialized_plan_conflict")` ile yazıyor. WritePrivateFileIdempotent (SecureBundleStorage.cs:58-63): `if (File.Exists(target)) { ...; if (File.ReadAllBytes(target).AsSpan().SequenceEqual(content)) return; throw new CalendarDataException(conflictCode, target); }` — yani var olan dosya ile içerik AYNI değilse fail eder. Plan içeriği her gün değişiyor: `CoverageThrough = cutoff` ve `SnapshotSetId = $"cal-tcmb-{cutoff:yyyy-MM-dd}"` (CalendarPlanMaterializer.cs:22, :39), cutoff ise `TcmbProviderCutoff` ile günlük olarak ilerliyor. Plan yolu kalıcıdır ve hiçbir yerde silinmez: `SAYDIN_CALENDAR_TCMB_PLAN=/var/lib/saydin/calendar/plans/tcmb-daily.json` (env örneği), systemd unit'te `ReadWritePaths=/var/lib/saydin/calendar`; `grep` ile CalendarAcquisition/Program/run-acquisition.sh içinde plan dosyasını silen hiçbir kod yok. run-acquisition.sh:43-54 `materialize-plan` docker çağrısını `set -e` altında, `fail()` sarmalaması olmadan yapıyor ve bu adım noop kontrollerinden (satır 64-78) ÖNCE geliyor. Program.cs:62-66 CalendarDataException'ı exit 2'ye çeviriyor. CalendarPlanMaterializerTests.cs:12-18 `MaterializeTcmb`'yi iki kez AYNI `beforePublication` zaman damgasıyla çağırdığı için yalnız aynı-gün idempotency'sini doğruluyor; gün dönümü senaryosu test edilmiyor.

**Etki.** Otomatik TCMB calendar acquisition ilk başarılı koşudan sonra kalıcı olarak durur; yeni candidate üretilmez, authoritative calendar coverage ilerlemez. TCMB/BIST'e bağlı ingestion hedef günü dondurulur (ResolveLatestExpectedObservationAsync son mühürlü release'in coverage_through'unu aşamaz) ve fiyat verisi bayatlar. Üstelik hata `calendar_acquisition_rejected:` markerını üretmediği için (docker çağrısı fail() ile sarılmamış) log tarafında da tipli bir red kodu görünmez; yalnız ham exit 2 kalır.

**Öneri.** Plan materyalizasyonunu içerik-çakışması yerine "aynı cutoff için idempotent, yeni cutoff için değiştir" semantiğine çevir: ya cutoff'u dosya adına koy (`tcmb-daily-{cutoff}.json`) ve eski planları bounded retention ile temizle, ya da WritePrivateFileIdempotent yerine atomik replace (tmp + rename) kullanıp çakışmayı yalnız aynı-gün eşzamanlı koşu için ayır. Testi de düzelt: `MaterializeTcmb`'yi iki FARKLI güne ait zaman damgasıyla çağıran ve ikincisinin başarılı olduğunu (ya da beklenen tipli davranışı ürettiğini) doğrulayan bir case ekle; ayrıca run-acquisition.sh'in materialize-plan çağrısını `|| fail "plan_materialization_failed"` ile sar.

---

### 15. RequireLinuxRoot dinamik skip'i CI unit kapısını her koşuda kırar (mekanizma: skip değil, FAIL)

| | |
|---|---|
| **Önem** | High |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R14b — Migrator/RoleBootstrap/DQA/DataRepair test kalitesi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.DatabaseRoleBootstrap.Tests/SecretFileTests.cs:182,195,313-327; .github/scripts/run-unit-coverage.sh:22-24,36,83-84; .github/workflows/ci.yml:22-24,71; .github/compose.integration.yml:464-485` |

**Bulgu.** `RequireLinuxRoot()` GitHub-hosted (non-root) Linux runner'da `Xunit.Sdk.SkipException` fırlatır. Ancak dependency graph'te (xunit 2.9.2 / xunit.runner.visualstudio 2.8.2) dinamik skip'i tanıyan hiçbir runner yoktur — `$XunitDynamicSkip$` token'ı yalnız xunit.assert'te vardır, execution/adapter assembly'lerinde yoktur. Bu nedenle iki ownership testi 'skipped/notExecuted' değil, doğrudan **failed** olur ve `dotnet test` non-zero döner; TRX kapısı çalışmaya bile fırsat bulmadan `run-unit-coverage.sh` patlar. minimum_tests=98 değerinin gerçek test sayısına (40 Fact + 58 InlineData) birebir eşit olduğunu bağımsız saydım. Aynı proje compose.integration.yml'de root olarak koştuğu ve lokal `tests` profili de root konteyner olduğu için sorun yalnız çıplak-runner CI yolunda görünür.

**Etki.** Required CI kapısı (`build-and-test`) her push ve PR'da deterministik olarak kırmızı. Kırılma fail-closed olduğu için üretime hatalı kod akıtmaz, ancak dokümante edilmiş CI akışı tamamen bloke olur ve düzeltme baskısı 'kapıyı gevşetme' (minimum düşürme / skip toleransı) yönünde çalışır; bu da remediation'ın ana kazanımını geri alır. `07-remediation-progress.md`'nin zero-skip iddiası yalnız root konteynerde geçerlidir.

**Öneri.** Ownership testlerini root'a bağımlı olmaktan çıkar: `LinuxSecretFileTestProbe` seam'i zaten mevcut olduğundan `statx` sahiplik gözlemini probe ile enjekte ederek testi root gerektirmeyecek şekilde deterministik yap. Bu mümkün değilse iki testi `[Trait("Category","LinuxRoot")]` altına al, `run-unit-coverage.sh`'ta `--filter Category!=LinuxRoot` ile hariç tut ve minimum'u 96'ya indir; root koşan compose kapısında filtresiz koş ve orada 98 zorunlu kıl. HER DURUMDA: xunit v2'de dinamik skip çalışmadığı için `Xunit.Sdk.SkipException` kullanımını tüm repodan kaldır (SecretFileTests.cs:313-327 dahil) — ya `Xunit.SkippableFact` (repo'da zaten yönetiliyor, Directory.Packages.props:76) ya da statik `Skip=` kullan.

---

### 16. CLAUDE.md ve CONTRIBUTING.md'deki Compose komutları fail-closed: `--env-file .env.database-runtime` ve bootstrap adımı eksik

| | |
|---|---|
| **Önem** | High |
| **Tip** | `defect` |
| **Boyut** | developer-experience |
| **Hat** | R15 — Dokümantasyon, ADR, runbook |
| **Doğrulama** | CONFIRMED |
| **Konum** | `CLAUDE.md:25-38,70-76 · CONTRIBUTING.md:44 · docker-compose.yml:249,271-283,316-325 · infrastructure/secrets/bootstrap-dev-database.sh:10-24,son satır` |

**Bulgu.** CLAUDE.md'nin 'Geliştirme Ortamı Kuralı' ve 'Commit Kuralı' bloklarındaki dört Compose komutu ile CONTRIBUTING.md:44, `.env.database-runtime` env-file bayrağı olmadan yazılmıştır ve bootstrap adımı hiç anılmamıştır; bu komutlar temiz bir checkout'ta da bootstrap sonrasında da `required variable ... is missing` ile fail-closed olur. Aynı repodaki README.md ve docs/development-guide.md doğru biçimi kullanır.

**Etki.** Agent sözleşmesi olan CLAUDE.md'nin dokümante ettiği geliştirme ve commit-öncesi doğrulama akışının tamamı çalışmıyor. Her agent oturumu ve CONTRIBUTING'i izleyen her katkıcı aynı duvara çarpar; 'Lokal dotnet bulunamadı diye debelenme' talimatı bu durumda yanlış yönlendirir. Kanonik doğru yol repoda mevcut olduğu için veri riski yok, ancak dokümante edilmiş kapı kırık.

**Öneri.** CLAUDE.md 'Geliştirme Ortamı Kuralı' bloğuna 0. adım olarak `./infrastructure/secrets/bootstrap-dev-database.sh` ekle; blok içindeki dört komutu ve CONTRIBUTING.md:44'ü README/development-guide ile aynı `docker compose --env-file .env --env-file .env.database-runtime ...` biçimine getir. Kalıcı çözüm: bayrakları tek yerde tutan ince bir `.github/scripts/dev-compose.sh` sarmalayıcısı ekleyip üç dokümanı da ona yönlendirmek.

---

### 17. database-role-credential-lifecycle.md prosedürü production sözleşmesinde uygulanamaz: yanlış secret yolu ve dosya-adı allowlist'i sonraki deploy'u kilitler

| | |
|---|---|
| **Önem** | High |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R15 — Dokümantasyon, ADR, runbook |
| **Doğrulama** | CONFIRMED |
| **Konum** | `docs/runbooks/database-role-credential-lifecycle.md:24-33,40-49,60-72 · infrastructure/deployment/compose.production.yml:93-127 · infrastructure/deployment/validate-private-material.py:20-26,174-176 · infrastructure/release/deploy-release.sh:52,95-105 · src/Saydin.DatabaseRoleBootstrap/BootstrapOptions.cs:178-191` |

**Bulgu.** Runbook'un secret yolu (`/run/saydin-secrets/api-v3`) production mount sözleşmesiyle uyumsuzdur (doğrusu `.../private/api-v3`); ayrıca bootstrap volümünün dosya kümesi validate-private-material.py tarafından tam eşitlikle zorlandığı için rotasyon artefaktı bırakıldığında sonraki imzalı deploy exit 78 ile durur. Runbook 12 ortak argümanı somutlaştırmıyor, temizlik/geri alma adımı içermiyor ve production'da bu komutları çalıştıracak bir Compose profili tanımlı değil.

**Etki.** Kimlik-bilgisi sızıntısına yanıt (rotate/reset-password/retire) prosedürü olay anında uygulanabilir değil; operatör yolu düzeltip devam ederse rotasyon başarılı olur ama production deploy hattı fail-closed kilitlenir. '07-remediation-progress.md'nin 'açık operasyon kalmadı' iddiasının aksine bu operasyon kapanmamıştır.

**Öneri.** (1) Tüm yolları `/run/saydin-secrets/private/<purpose>-vN` olarak düzelt. (2) validate-private-material.py EXPECTED['bootstrap'] kümesini versiyon-farkında bir kurala (`^(migrator|api|ingestion|calendar_importer|exporter|audit|backup)-v([1-9]|[12][0-9]|3[0-2])$`) çevir veya cutover sonrası eski dosyanın silinmesini runbook'ta zorunlu adım yapıp deploy öncesi kontrol listesine bağla. (3) data-repair.md tarzında tam çalıştırılabilir bir `docker compose --project-name saydin-production --env-file ... run --rm --no-deps database-role-bootstrap rotate ...` bloğu ve 12 ortak argümanın somut listesini ekle; production compose'a bir `role-credential-operator` profili tanımla. (4) Cutover sonrası `SAYDIN_*_LOGIN` güncellemesi, `ensure` argümanlarındaki `-v1` bağının nasıl ilerletileceği ve eski secret dosyasının kaldırılması adımlarını açıkça yaz.

---

### 18. CLAUDE.md'de belgelenen kök compose komutlarının hiçbiri çalışmıyor (--env-file eksik)

| | |
|---|---|
| **Önem** | High |
| **Tip** | `defect` |
| **Boyut** | developer-experience |
| **Hat** | R16 — Compose, solution, build konfigürasyonu |
| **Doğrulama** | CONFIRMED (ana agent) |
| **Konum** | `CLAUDE.md:27,31-33,72,375,378 ↔ docker-compose.yml:262-300,518-527` |

**Bulgu.** Kök compose artık her runtime servisinde `${SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256:?run infrastructure/secrets/bootstrap-dev-database.sh first}`, `${SAYDIN_DATABASE_ROLE_PREFIX:?...}`, `${SAYDIN_MIGRATOR_LOGIN:?...}` ve bu diff'le eklenen `${SAYDIN_BACKUP_V1_VALID_UNTIL:?...}` gibi zorunlu değişkenler taşıyor. Bunlar `.env` değil `.env.database-runtime` içinde üretiliyor ve Compose `--env-file` verilmediğinde `.env.database-runtime`'ı hiç okumuyor. Repo kökünde doğrulandı: `docker compose config` → `error while interpolating services.saydin-api.environment.SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256: required variable ... is missing a value`. README.md:30, CONTRIBUTING.md:12 ve docs/development-guide.md:22,238 doğru formu (`docker compose --env-file .env --env-file .env.database-runtime ...`) kullanıyor; bu diff'te güncellenen CLAUDE.md ise güncellenmemiş. Ayrıca CLAUDE.md:29,71 "`tests` compose profili" diyor, `docker compose config` çıktısında profil adı `test`.

**Etki.** Projenin agent sözleşmesi olarak ilan edilen dosyadaki kanonik build/test/deploy akışının tamamı kırık. Agent veya yeni geliştirici hatayı gerçek bir altyapı arızası sanıp gereksiz teşhis döngüsüne girer; en kötü durumda commit kuralındaki test kapısı atlanır. `07-remediation-progress.md`'nin "doküman kusuru kalmadı" iddiasıyla doğrudan çelişiyor.

**Öneri.** CLAUDE.md:27,31-33,72,375,378 satırlarındaki tüm komutları `docker compose --env-file .env --env-file .env.database-runtime ...` formuna çevir (README/development-guide ile birebir aynı), profil adını `test` olarak düzelt ve `.github/scripts/check-doc-links.py` benzeri bir doküman kapısına "kök compose komutları --env-file içermeli" kuralını ekle ki aynı drift tekrar etmesin.

---

### 19. `resolve_installation_and_rehash` her kimlik doğrulamasında index kullanamayan tarama + satır kilidi yapıyor

| | |
|---|---|
| **Önem** | High |
| **Tip** | `defect` |
| **Boyut** | performance |
| **Hat** | R17 — REMEDIATION DENETİMİ |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/postgres/migrations/024_installation_credential_rehash.sql:92-102,136-147; src/Saydin.Api/Repositories/InstallationRepository.cs:26-36; src/Saydin.Api/Endpoints/EndpointExtensions.cs:41; infrastructure/postgres/migrations/021_api_trust_expand.sql:93,257` |

**Bulgu.** 024 ile kimlik doğrulama hot-path'i sargable olmayan bir predikata taşındı: her korumalı istek, aktif key version'daki tüm `installation_credentials` satırları için 32 iterasyonluk bir PL/pgSQL karşılaştırması çalıştırabilir ve eşleşen satırda koşulsuz `FOR UPDATE` kilidi alır. Geçerli token'da istek başına 1 tarama, geçersiz/eski token'da 3'e kadar tarama olur (keyring active=max key olduğu için azalan sırada denenir).

**Etki.** Kullanıcı sayısıyla doğrusal büyüyen kimlik doğrulama maliyeti: 100k installation'da her istek milyonlarca PL/pgSQL işlemi tetikler; geçersiz token floodu maliyeti 3x'ler. Ayrıca aynı principal'ın paralel istekleri `FOR UPDATE OF principal` üzerinde serileşir. Ölçekte kendi kendine DoS ve p99 latency çöküşü.

**Öneri.** Eşitlik predikatını geri getir (`credential.secret_hash=p_secret_hash`) ve `installation_verifier_matches`'i yalnız index'ten seçilen tek satır üzerinde guard olarak çalıştır. Steady-state'te (p_key_version=p_active_key_version) hiç yazma/kilit gerekmediği için 021'in `STABLE`/`LANGUAGE sql` yolunu kullan; rehash'i yalnız versiyon farkı varsa ayrı yazma transaction'ında yap. ≥100k credential seed'i ile latency regresyon testi ekle.

---

### 20. CLAUDE.md ve CONTRIBUTING.md'deki Compose komutları çalışmıyor — build/test/migrate kapılarının tamamı kırık

| | |
|---|---|
| **Önem** | High |
| **Tip** | `defect` |
| **Boyut** | developer-experience |
| **Hat** | R18 — ÜRÜN VE GELİŞTİRİCİ DENEYİMİ |
| **Doğrulama** | CONFIRMED (ana agent) |
| **Konum** | `CLAUDE.md:20-33,72; CONTRIBUTING.md:44; docs/development-guide.md:238-242` |

**Bulgu.** CLAUDE.md `docker compose build && docker compose up -d`, `docker compose run --rm tests`, `docker compose run --rm database-migrator --verify-only` diyor; CONTRIBUTING.md `docker compose --profile test run --rm tests` diyor. docker-compose.yml 20+ yerde `${SAYDIN_DEPLOYMENT_ID:?...}` gibi zorunlu interpolasyon kullanıyor ve bu değerler `.env.database-runtime` içinde yaşıyor — Compose bu dosyayı otomatik yüklemiyor. Repo kökünde ampirik doğrulama: `docker compose config --quiet` → `error while interpolating services.saydin-api.environment.SAYDIN_DEPLOYMENT_ID: required variable SAYDIN_DEPLOYMENT_ID is missing a value: run infrastructure/secrets/bootstrap-dev-database.sh first` (exit≠0); `docker compose --env-file .env --env-file .env.database-runtime config --quiet` → exit 0. Yalnız docs/development-guide.md doğru uzun formu kullanıyor.

**Etki.** Dokümante edilmiş yerel build/test/migration akışının tamamı ilk komutta kırılıyor. CLAUDE.md agent sözleşme dosyası olduğu için her otomatik commit-öncesi kapısı da aynı şekilde başarısız oluyor; hata mesajı yanıltıcı olduğu için geliştirici bootstrap'ı gereksizce tekrar çalıştırmaya yönlendiriliyor.

**Öneri.** Compose v2.24+ `COMPOSE_ENV_FILES` desteğini kullan: `.env` içine `COMPOSE_ENV_FILES=.env,.env.database-runtime` koymak yerine (kendi kendine referans) repo köküne kısa bir `saydin` wrapper script'i veya `Makefile` hedefi ekle (`./scripts/compose.sh run --rm tests`), ya da tüm dokümanları development-guide.md'deki iki `--env-file` biçimine hizala. En azından CLAUDE.md:20-33,72 ve CONTRIBUTING.md:44 satırlarını `docker compose --env-file .env --env-file .env.database-runtime ...` haline getir. Ayrıca `check-doc-links.py` benzeri bir kapı ile doküman içindeki `docker compose` komutlarının en az bir `--env-file .env.database-runtime` taşıdığını fail-closed doğrula — 20+ kez tekrarlanan bu prefix zaten kendi başına bir DX borcudur.

---
