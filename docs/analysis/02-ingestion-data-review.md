# Ingestion ve Veri Katmanı İnceleme Raporu

**Tarih:** 18 Ağustos 2026  
**Kapsam:** `src/Saydin.PriceIngestion`, `tests/Saydin.PriceIngestion.Tests`, `infrastructure/postgres`, `src/Saydin.Shared/Data`, `src/Saydin.Shared/Entities` ve veri bütünlüğü için gerekli ortak yapılandırma/diagnostic dosyaları  
**Yöntem:** Kaynak kod ve SQL/shell statik incelemesi, odaklı Docker SDK testleri, temiz TimescaleDB kurulumu ve şema sorguları. Üretim kodu değiştirilmedi.

## Yönetici özeti

İnceleme sonucunda **25 bulgu** kaydedildi: **2 Critical, 13 High, 9 Medium, 1 Low**. En önemli risk, dış kaynak veya veritabanı hatasının iç katmanlarda yakalanıp normal dönüşe çevrilmesi ve backfill imlecinin yine de ilerletilmesidir. Bu davranış hem fiyatlarda hem TÜFE serisinde kalıcı tarih boşluğu yaratabilir; günlük akışta ise job'ı `success / 0 records` gösterebilir. İkinci kritik risk, ilk PostgreSQL kurulumundaki erken migration'ların transaction dışı olması ve Docker init zincirinin yalnız boş data dizininde çalışması nedeniyle, yarım kurulmuş bir volume'ün sonraki açılışlarda yalnız `pg_isready` ile sağlıklı kabul edilebilmesidir.

OpenExchangeRates adaptörü ve mapper'ı ile concrete worker/repository/orchestrator sınıflarının review sırasında üretilen coverage ölçümünde satır kapsamı **%0**'dır; kalıcı özet `docs/analysis/04-validation-and-cross-cutting-review.md:121-139` içindedir. Odaklı test projesindeki 86 test geçmesine rağmen gerçek resilience pipeline'ı, kalıcı boşluk senaryosu, worker supervision, DB upsert/transaction davranışı ve migration runner yarışları test edilmemektedir.

## Kapsam matrisi

| Alan | İncelenen kapsam | Sonuç |
|---|---:|---|
| `src/Saydin.PriceIngestion` | 32/32 dosya | Program, 5 worker, orchestrator, heartbeat, 5 adapter, 5 mapper, repository'ler, resilience ve config incelendi |
| `tests/Saydin.PriceIngestion.Tests` | 12/12 tracked dosya | 11 C# test dosyası + proje dosyasının tamamı incelendi. Review sırasında üretilen, tracked olmayan Cobertura `TestResults` artifact'ı yalnız ölçüm için ayrıca okundu; tracked kapsam sayısına dahil edilmedi. `bin/obj` üretilmiş artifact olarak dışlandı |
| `infrastructure/postgres` | 17/17 dosya | 14 SQL, 2 shell migration/runner ve klasördeki tüm sürümler incelendi |
| `src/Saydin.Shared/Data` | 8/8 dosya | DbContext ve tüm entity configuration'ları incelendi |
| `src/Saydin.Shared/Entities` | 8/8 dosya | Tüm entity'ler; özellikle `Asset`, `PricePoint`, `InflationRate`, `IngestionJob` incelendi |
| Bağlamsal dosyalar | Seçili | `CLAUDE.md`, mimari/veritabanı/observability dokümanları, ADR-001/005/006, Compose, Dockerfile, `.env.example`, metrics ve CI/test talimatları |

## Doğrulama sonuçları

- Docker içindeki .NET SDK 10 ile `Saydin.PriceIngestion.Tests` proje dizininden restore/test: **86 passed, 0 failed, 0 skipped**.
- Aynı SDK ile dokümante edilen `dotnet test tests/Saydin.PriceIngestion.Tests` dizin argümanı: **exit 0**, ancak test discovery/output yok; test çalışmadı.
- Kök çapraz-validasyonda NuGet güvenlik audit'i yalnız davranışsal ölçümü ayırmak için kapatıldığında Solution Release build **0 warning / 0 error** tamamlandı; 286 API unit + 86 ingestion + gerçek PostgreSQL/Redis kullanan 8 integration testi = **380 passed, 0 failed, 0 skipped**. Gerçek-infra alt kümesi **8/8** geçti.
- Normal, NuGet audit'i açık API image build'i transitive `Microsoft.OpenApi 2.0.0` high advisory'si nedeniyle restore aşamasında **NU1903 ile fail** oldu. Bu release engeli ingestion test sonucundan ayrıdır; audit açık PriceIngestion image build'i geçti (`docs/analysis/04-validation-and-cross-cutting-review.md:22-40`).
- İzole `timescale/timescaledb:2.16.1-pg16` üzerinde boş volume + dummy exporter secret ile `001`–`014` zinciri: **başarılı**. Bu, mutlu yolu doğrular; C-02'deki arıza sonrası yeniden başlatma riskini ortadan kaldırmaz.
- Temiz şemada `schema_migrations` sorgusu: kayıtlı **16 sürümün tamamında `checksum IS NULL`**.
- Temiz şema constraint sorgusu: kaynak/job/FK kontrolleri var; fiyat pozitifliği, OHLC sıralaması, hacim ve aylık TÜFE bütünlük kontrolleri yok.
- `bash -n infrastructure/postgres/apply-migrations.sh` ve `bash -n infrastructure/postgres/migrations/012b_create_exporter_role.sh`: başarılı. `shellcheck` ortamda yoktu.

## Critical bulgular

### C-01 — Hata yutma + MAX anchor kalıcı fiyat/TÜFE boşlukları yaratıyor

- **Dosya/satır:** `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:124-133`, `251-262`, `289-303`, `345-372`, `375-394`; `src/Saydin.PriceIngestion/Repositories/PriceIngestionRepository.cs:108-113`; `src/Saydin.PriceIngestion/Workers/EvdsInflationWorker.cs:83-103`, `138-159`; `src/Saydin.PriceIngestion/Repositories/InflationIngestionRepository.cs:14-21`.
- **Kanıt:** `FetchAndUpsertAsync` adapter, mapper, job ve repository hatalarının tamamını yakalayıp normal döner. `BackfillChunkedAsync` bu normal dönüşten sonra `chunkFrom = chunkTo + 1` ile ilerler. Böylece `RunAsync` içindeki transient retry filtresi hatayı hiç görmez. Sonraki başlangıçta anchor, fiyat için `MAX(price_date)`, TÜFE için kaynağın `MAX(period_date)` değeridir. EVDS worker da chunk hatasını `RunInflationJobAsync` içinde yutup sonraki chunk'a geçer.
- **Etki:** Ortadaki bir chunk başarısız, daha yeni chunk başarılı olduğunda eksik dönem anchor'ın gerisinde kalır ve otomatik olarak bir daha istenmez. Kullanıcı getirisi eksik/yanlış fiyat veya TÜFE ile hesaplanabilir; job logları sonraki başarılı chunk'lar nedeniyle sağlıklı görünebilir.
- **Tetiklenme:** Örneğin 2018–2022 fiyat chunk'ı DB timeout ile başarısız olur, 2023–2025 chunk'ı yazılır; restart sonrası `MAX=2025` olduğundan 2018–2022 kalıcı boşluk olur. Aynı zincir EVDS'nin dört 60 aylık parçasından biri başarısız olduğunda oluşur. `ingestion_jobs.StartAsync` hatası da fiyat chunk'ını sessizce atlatır.
- **Öneri:** Fetch/upsert hatasını sonuç tipiyle yukarı taşı; başarısız chunk'ta ilerlemeyi durdur veya persisted checkpoint'i yalnız doğrulanmış tam pencere sonrası ilerlet. Trading calendar/expected-observation tabanlı completeness kontrolü ve tüm kaynaklar için gap reconciliation ekle. Retry yalnız başarısız operasyonu yeniden yürütmeli; `StartAsync` hatası ingestion'ı atlatmamalı ya da açıkça degraded/auditsiz mod olmalıdır.
- **Regresyon testi:** Üç chunk'lı backfill'de ikinci adapter/repository çağrısını fail ettir, üçüncünün çalışmadığını ve restart'ta ikinci chunk'ın tekrar hedeflendiğini doğrula. Ayrı testte eski chunk fail/yeni chunk success simülasyonundan sonra gap query'nin eksik tarihleri geri aldığını kanıtla.

### C-02 — İlk PostgreSQL init hatası yarım şemayı kalıcı ve “healthy” bırakabilir

- **Dosya/satır:** `infrastructure/postgres/migrations/001_initial.sql:10-109`, `infrastructure/postgres/migrations/004_add_inflation_rates.sql:8-17`, `docker-compose.yml:27-38`.
- **Kanıt:** `001_initial.sql` ve `004_add_inflation_rates.sql` transaction ile sarılı değildir; çok sayıda DDL/DML ifadesi autocommit olur. Compose açıkça init klasörünün yalnız boş data dizininde çalıştığını belirtir. PostgreSQL healthcheck yalnız `pg_isready` çağırır; migration seviyesi veya zorunlu tablo/constraint kontrol etmez.
- **Etki:** İlk init sırasında geçici disk/SQL/process hatası meydana gelirse önceden commit olmuş nesneler kalır. PostgreSQL cluster'ı artık boş olmadığı için sonraki container başlangıcı init scriptlerini tekrar çalıştırmayabilir; eksik tablolu/constraint'siz veritabanı `healthy` olup API/ingestion'a açılır. Onarımın otomatik ve güvenilir yolu yoktur.
- **Tetiklenme:** `001` içinde `assets` yaratıldıktan sonra Timescale hypertable/index adımı fail eder veya `004` seed insert'i yarıda kesilir; volume korunup container yeniden başlatılır.
- **Öneri:** SQL migration'larını mümkün olan yerde tek transaction'a al; init sonunda beklenen son sürümü atomik yaz. Health/readiness'i `schema_migrations` son sürümü ve kritik şema invariants ile doğrula. Başarısız fresh-init volume için açık, güvenli ve veri kaybını onaylatan recovery runbook'u oluştur. Docker init semantics için [resmî postgres image dokümanı](https://hub.docker.com/_/postgres) esas alınmalıdır.
- **Regresyon testi:** Geçici bir migration'a kontrollü `RAISE EXCEPTION` eklenen ephemeral fixture ile ilk init'i fail ettir, aynı volume ile restart sonrası health'in yeşile dönmediğini ve eksik şemanın servis bağımlılıklarını açmadığını doğrula.

## High bulgular

### H-01 — OpenExchangeRates sistemik hataları başarılı boş run'a dönüşüyor

- **Dosya/satır:** `src/Saydin.PriceIngestion/Adapters/OpenExchangeRatesAdapter.cs:38-43`, `48-65`, `72-84`, `103-117`; `src/Saydin.PriceIngestion/Mappers/OpenExchangeRatesMapper.cs:19-42`; `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:405-413`.
- **Kanıt:** Eksik AppId doğrudan `[]`; 401/403 `null`; `FetchDayAsync` cancellation dışındaki bütün exception'ları yakalayıp `null` döndürüyor. Bu catch 5xx sonrası tükenen HTTP hatasını, bozuk JSON'u, schema drift'i ve mapper bug'ını aynı biçimde yumuşatır. Range kısmi/boş listeyle tamamlanır; base worker boş listeyi `success`, `records_upserted=0` yazar. OXR adapter/mapper coverage'ı saklanan raporda %0'dır.
- **Etki:** Geçersiz/limit aşmış key, provider outage veya contract değişimi alarm ve retry üretmeden “o gün veri yoktu” gibi kaydedilir. Backfill'de C-01 ile birleşerek kalıcı kayıp üretir.
- **Tetiklenme:** OXR 401/403/500 döndürür, payload'da `rates` kaybolur veya JSON bozulur.
- **Öneri:** 404 gibi semantik olarak beklenen “yayın yok” sonucu ile auth/rate-limit/5xx/parse/validation hatasını typed result/exception ile ayır. Auth ve payload schema hatalarını fail-fast, 429/5xx/network'ü retryable yap; expected-count/completeness doğrulamasından geçmeyen range'i success işaretleme.
- **Regresyon testi:** Eksik key, 401, 403, 429, 500, malformed JSON, eksik `rates`, eksik XAU/TRY ve kısmi çok-gün yanıtlarının `failed` job ürettiğini; yalnız tanımlı no-data durumunun başarılı boş sayıldığını doğrula.

### H-02 — `close` alanı provider'lar arasında final olmayan ve farklı anlamdaki fiyatları karıştırıyor

- **Dosya/satır:** `src/Saydin.PriceIngestion/Workers/OpenExchangeRatesWorker.cs:18-24`, `src/Saydin.PriceIngestion/Workers/TwelveDataWorker.cs:15-21`, `src/Saydin.PriceIngestion/Workers/CoinGeckoWorker.cs:20-26`; `src/Saydin.PriceIngestion/Mappers/OpenExchangeRatesMapper.cs:38-49`, `CoinGeckoMapper.cs:28-54`, `TcmbMapper.cs:21-26`; `src/Saydin.PriceIngestion/appsettings.json:21-25`; `infrastructure/postgres/migrations/001_initial.sql:52-69`.
- **Kanıt:** OXR 22:00 UTC'de UTC “bugün” historical endpoint'ini çağırıp değeri `Close` yazar; provider historical gün değeri günün sonunda kesinleşir. TwelveData BIST worker'ı 15:00 UTC, yani Türkiye saatiyle 18:00'de çalışır; Borsa İstanbul normal seans işlemleri 18:10'a kadar sürer. CoinGecko günlük aralıkta dönen saatlik gözlemlerden günün **00:00'una en yakın** olanı seçip o tarihin `Close` değeri yapar; bu gün sonu değil gün başı snapshot'ıdır. TCMB de gerçek OHLC sağlamadığı halde referans alış kurunu aynı kolonda tutar.
- **Etki:** Tek `close` kolonu BIST partial candle, kripto gün başı, metal provider snapshot'ı ve TCMB referans/bid değerini aynı finansal semantik gibi sunar. “Ya alsaydım” sonuçları kaynaklar arasında karşılaştırılamaz ve aynı gün daha sonra final veriyle düzeltilmez.
- **Tetiklenme:** Günlük worker normal saatinde çalışır; özellikle gün içi volatilite veya kapanışa yakın sert hareket vardır.
- **Öneri:** Her kaynak için kanonik `as_of_at`, `price_kind` (`official_reference`, `daily_close`, `snapshot`) ve final/provisional bayrağı tanımla. OXR'ı tamamlanmış önceki UTC güne, TwelveData'yı BIST kapanışı + provider gecikme payına zamanla; CoinGecko'da gün sonu gözlemini veya provider'ın günlük candle sözleşmesini kullan. Provisional kayıtlar final veri geldiğinde yeniden çekilmelidir. Referanslar: [OXR historical](https://docs.openexchangerates.org/reference/historical-json), [CoinGecko range granularity](https://docs.coingecko.com/reference/coins-id-market-chart-range), [Borsa İstanbul piyasa işleyişi](https://www.borsaistanbul.com/en/markets/equity-market/market-functioning).
- **Regresyon testi:** Gün başı ve gün sonu fiyatları farklı fixture'larda seçilen timestamp'i assert et; BIST worker clock testinde hedef zamanın 18:10 Türkiye sonrasına düştüğünü; OXR daily run'ın tamamlanmış günü istediğini doğrula.

### H-03 — EVDS backfill başlangıcı adapter sözleşmesini ve belgelenen tarih ufkunu ihlal ediyor

- **Dosya/satır:** `src/Saydin.PriceIngestion/Adapters/IInflationAdapter.cs:15-24`; `src/Saydin.PriceIngestion/Workers/EvdsInflationWorker.cs:9-12`, `34-36`, `83-89`, `110-123`.
- **Kanıt:** Interface `from`/`to` değerlerinin ayın ilk günü olmasını şart koşar. Worker `DateTime.UtcNow.AddYears(-20)` değerini gün numarasını koruyarak `DateOnly`'e çevirir; 18 Ağustos 2026'da başlangıç `2006-08-18` olur. Chunk'lar aynı gün-of-month değerini taşır. Yorum ise serinin 2003-01-01'den backfill edildiğini söyler, kod yalnız 20 yıl gider.
- **Etki:** API sınır semantiğine göre ilk/son aylar atlanabilir; 2003–yaklaşık 2006 arasındaki gerçek TÜFE hiçbir zaman alınmaz. Bu dönemlerde reel getiri hesapları yok veya yaklaşık seed'e bağımlı kalır.
- **Tetiklenme:** İlk kez ayın 1'i dışındaki herhangi bir günde başlayan EVDS worker.
- **Öneri:** Sabit ve ay-başı `new DateOnly(2003, 1, 1)` kullan veya desteklenen başlangıcı config/metadata'dan oku. Her chunk'ın iki sınırında `Day == 1` invariant'ı uygula.
- **Regresyon testi:** Clock'u ayın 18'ine sabitleyip ilk ve bütün chunk sınırlarının ayın 1'i olduğunu, ilk tarihin 2003-01-01 olduğunu doğrula.

### H-04 — Varsayılan EVDS kurulumu key olmadan açık ve sessizce güncel veri üretmiyor

- **Dosya/satır:** `.env.example:30-35`, `56-62`; `docs/development-guide.md:15-19`, `182-191`; `src/Saydin.PriceIngestion/Adapters/EvdsInflationAdapter.cs:11-16`, `32-37`; `src/Saydin.PriceIngestion/Workers/EvdsInflationWorker.cs:138-148`; `infrastructure/postgres/migrations/004_add_inflation_rates.sql:109-120`.
- **Kanıt:** `.env.example` EVDS worker'ını `true`, key'i boş verir; kılavuz üç yerde EVDS'yi “key gerektirmez/key-free” diye tanımlar. Adapter kendi sözleşmesinde key gerektiğini belirtir ve key yoksa `[]` döner. Worker bu sonucu `success / 0` yapar. Seed yalnız Mart 2025'e kadardır.
- **Etki:** Dokümana göre kurulan sistem sağlıklı görünürken gerçek TÜİK serisi hiç gelmez; Ağustos 2026 itibarıyla fallback seed en az 17 ay geridedir. Reel getiri ya hesaplanamaz ya da eski yaklaşık veri kullanır.
- **Tetiklenme:** Fresh checkout, örnek `.env` kopyalama ve EVDS key alınmadan Compose başlatma.
- **Öneri:** EVDS enabled ise startup validation ile key'i zorunlu kıl; örnekte default'u false yap veya açıkça placeholder/fail-fast sun. Kılavuzu resmi key gereksinimiyle düzelt. Resmi sözleşme: [TCMB EVDS web servis kılavuzu](https://evds2.tcmb.gov.tr/help/videos/EVDS_Web_Servis_Kullanim_Kilavuzu.pdf).
- **Regresyon testi:** Compose config smoke testinde `WORKER_EVDS_ENABLED=true` + boş key'in non-zero startup ile reddedildiğini; disabled durumda servis başlangıcını engellemediğini doğrula.

### H-05 — Circuit breaker ayarı yorumlandığı gibi çalışmıyor ve kendi retry zincirini kesebiliyor

- **Dosya/satır:** `src/Saydin.PriceIngestion/Extensions/HttpResilienceExtensions.cs:24-50`; `CLAUDE.md:318-324`; `docs/architecture.md:86-101`.
- **Kanıt:** Standard handler sırası retry dışta, circuit breaker attempt seviyesinde olacak şekildedir. `MinimumThroughput=2`, `FailureRatio=1` olduğundan tek mantıksal çağrının ilk iki başarısız attempt'i devreyi açabilir; kalan retry'lar `BrokenCircuitException` görebilir. Kod `SamplingDuration=120s` ayarlıyor fakat `BreakDuration` ayarlamıyor; varsayılan açık kalma süresi 5 saniyedir. Kod ve doküman “devre 120s açık” diye yazıyor.
- **Etki:** Beklenen 4 toplam attempt gerçekleşmeyebilir; düşük trafikte devre koruması yanlış süreyle açılır/kapanır. Operasyon runbook'u ve alarmlar gerçek davranıştan sapar.
- **Tetiklenme:** Aynı request'in art arda iki 5xx/timeout attempt'i.
- **Öneri:** İstenen semantiği açıkça seç: attempt bazlı breaker ise `BreakDuration`'ı ayrıca ayarla ve retry bütçesiyle uyumlandır; logical-call bazlı ise pipeline sırasını/ayrı handler'ı değiştir. “5 ardışık” hedefini test edilebilir state-machine tanımına çevir. Referans: [.NET standard resilience handler](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience).
- **Regresyon testi:** Kontrollü zaman sağlayıcısı ve sırayla 500 dönen handler ile gerçek DI pipeline'ını kur; attempt sayısı, breaker açılış eşiği ve 5/120 saniyelik açık kalma süresini assert et.

### H-06 — Tek worker'ın ölmesi process'i düşürmüyor; bağımsız heartbeat false-green kalıyor

- **Dosya/satır:** `src/Saydin.PriceIngestion/Workers/IngestionOrchestrator.cs:24-42`, `57-72`, `76-91`; `src/Saydin.PriceIngestion/BackgroundServices/LivenessHeartbeatService.cs:39-50`; `docker-compose.yml:247-256`; `src/Saydin.PriceIngestion/Dockerfile:32-38`.
- **Kanıt:** `RunSafelyAsync` fatal exception'ı `LogCritical` sonrası yutar ve task normal tamamlanır. `Task.WhenAll`, diğer worker'ların sonsuz loop'larını beklediği için “tüm worker'lar öldü” kontrolüne ulaşmaz. Ölen worker yeniden başlatılmaz. Heartbeat tamamen bağımsız bir `PeriodicTimer` ile dosyayı günceller; worker/orchestrator progress'ini gözlemlemez. Docker health yalnız bu dosyanın mtime'ına bakar.
- **Etki:** Tek veri kaynağı günlerce kalıcı olarak durmuşken container `healthy` kalabilir. Örneğin yalnız BIST worker'ı ölür, TCMB çalıştığı için process devam eder ve otomatik restart olmaz.
- **Tetiklenme:** Concrete worker'da transient listesine girmeyen mapper/config/programlama exception'ı veya worker'ın beklenmedik normal dönüşü.
- **Öneri:** Worker başına supervisor/restart policy ve last-progress state'i ekle veya kritik worker ölümünü host'a propagate et. Health/readiness provider bazında `last_success`, `last_attempt`, `lag`, `fatal_state` kontrol etmelidir; genel process heartbeat tek başına liveness olabilir ama readiness sayılmamalıdır.
- **Regresyon testi:** İki fake worker'dan birini fatal exception ile sonlandır; host'un seçilen politikaya göre non-zero çıktığını veya worker'ı backoff ile yeniden başlattığını, health'in ilgili provider için unhealthy/degraded olduğunu doğrula.

### H-07 — Çoklu replica/deploy için dağıtık lease yok; aynı pencere eşzamanlı işleniyor

- **Dosya/satır:** `src/Saydin.PriceIngestion/Workers/IngestionOrchestrator.cs:24-60`; `src/Saydin.PriceIngestion/Repositories/IngestionJobRepository.cs:18-40`; `src/Saydin.PriceIngestion/Repositories/PriceIngestionRepository.cs:57-79`; `infrastructure/postgres/migrations/001_initial.sql:89-109`.
- **Kanıt:** Scheduler ve job başlangıcında PostgreSQL advisory lock, lease, leader election veya “aynı source/asset/range için tek running job” unique constraint yoktur. Her replica ayrı `running` kaydı açar. Fiyat UPSERT'i satır çoğalmasını engellese de sonuçlar farklıysa son commit kazanır.
- **Etki:** Rolling deploy/replica artışında provider kotası iki kat tüketilir, 429/circuit breaker tetiklenir, job geçmişi çoğalır ve aynı günün fiyatı nondeterministik last-writer-wins olur.
- **Tetiklenme:** İki ingestion container'ının aynı anda çalışması veya eski instance bitmeden yenisinin başlaması.
- **Öneri:** `(source, asset_id, job_type, range)` için süreli DB lease/advisory lock kullan; stale lease recovery ekle. DB tarafında uygun partial unique constraint ile aynı logical job'ın eşzamanlı `running` olmasını engelle.
- **Regresyon testi:** Aynı DB'ye bağlı iki worker fixture'ını barrier ile eşzamanlı başlat; yalnız birinin provider çağrısı ve running job oluşturduğunu doğrula.

### H-08 — Migration runner eşzamanlı ve crash-safe değil; DDL ile kayıt atomik değil

- **Dosya/satır:** `infrastructure/postgres/apply-migrations.sh:11-23`, `55-78`.
- **Kanıt:** Script kendi yorumunda SELECT–INSERT TOCTOU penceresini kabul eder. “Uygulandı mı?” sorgusu, migration body ve `schema_migrations` insert'i ayrı `psql` süreçleridir. SQL migration commit olduktan sonra process ölürse version kaydı yoktur; rerun non-idempotent body'yi tekrar çalıştırır. İki runner aynı eksik sürümü eşzamanlı uygulayabilir; tracking insert'indeki `ON CONFLICT` yalnız kayıt satırını korur.
- **Etki:** Paralel deploy veya crash migration'ı iki kez uygulayabilir, deployment'ı kilitleyebilir ya da şemayı uygulanmış ama kayıtsız durumda bırakabilir. `.sh` migration'larda etkiler transactional olmayabilir.
- **Tetiklenme:** İki CI job, overlapping rollout veya DDL commit ile version insert'i arasındaki process/network kaybı.
- **Öneri:** Tek persistent DB oturumunda global advisory lock al; SQL migration body + version insert'i mümkünse aynı transaction'da çalıştır. Shell migration'lar için `started/succeeded/failed` durumu, idempotent body ve açık recovery protokolü kullan. CI concurrency guard yalnız ikinci savunma olmalıdır.
- **Regresyon testi:** İki runner'ı aynı ephemeral DB'de barrier ile başlat; migration body'ye sayaç ekleyip bir kez çalıştığını doğrula. Ayrı fault-injection testinde body sonrası tracking öncesi kill yapıp güvenli recovery'yi kanıtla.

### H-09 — Migration checksum alanı kullanılmıyor; 014 eksik geçmişi uygulanmış sayabilir

- **Dosya/satır:** `infrastructure/postgres/migrations/014_schema_migrations.sql:26-30`, `35-73`; `infrastructure/postgres/migrations/008_add_activity_logs.sql:80-86`; `infrastructure/postgres/apply-migrations.sh:64-77`.
- **Kanıt:** `checksum` kolonu yaratılıyor ancak 014 back-register ve runner insert'leri değer yazmıyor/karşılaştırmıyor; temiz kurulum sorgusunda 16/16 checksum null çıktı. 014 guard yalnız `activity_logs` compression'ın açık olmasını kontrol eder. Oysa 008'in kendisi compression'ı açar; 009–012 eksik bir DB bu guard'ı geçip 014 tarafından 001–014'ün tamamı, koşullu olarak hiç yaratılmamış `012b` rolü dahil, uygulanmış kaydedilebilir.
- **Etki:** Uygulanmış migration dosyalarının sonradan değişmesi tespit edilmez. Ara sürüm/yarım şema eksik migration'ları sonsuza kadar atlayabilir ve sürüm tablosu gerçeğe aykırı güven üretir.
- **Tetiklenme:** 008 sonrasında fakat 008b/009–013 öncesinde kalmış DB'ye 014'ün manuel uygulanması; geçmiş SQL'in değiştirilmesi; 012b sırasında exporter secret'ın bulunmaması.
- **Öneri:** Yeni migration'larda SHA-256 zorunlu yaz/validate et; geçmiş için doğrulanmış baseline manifest üret. 014 bootstrap guard'ı her kritik kolon/constraint/policy/role için doğrulama yapmalı veya tek onaylı schema fingerprint istemelidir. Koşullu shell adımını “uygulandı” yerine “skipped” olarak kaydet.
- **Regresyon testi:** 008 seviyesinde sentetik DB'de 014'ün fail ettiğini; migration dosyası değiştirildiğinde runner'ın checksum mismatch ile non-zero çıktığını; exporter rolü atlandığında kaydın `succeeded` olmadığını doğrula.

### H-10 — Finansal domain invariant'ları ne mapper'da ne DB'de yeterince korunuyor

- **Dosya/satır:** `infrastructure/postgres/migrations/001_initial.sql:52-65`; `infrastructure/postgres/migrations/004_add_inflation_rates.sql:8-15`; `src/Saydin.PriceIngestion/Mappers/CoinGeckoMapper.cs:28-54`, `TwelveDataMapper.cs:45-70`, `TcmbMapper.cs:77-89`, `EvdsInflationMapper.cs:38-52`.
- **Kanıt:** DB'de `close > 0`, `volume >= 0`, `high >= low`, mevcut OHLC'nin high/low aralığında olması, `index_value > 0` veya `period_date` ayın ilk günü constraint'i yoktur. CoinGecko negatif/sıfır fiyatı; TwelveData negatif veya tutarsız OHLCV'yi; TCMB negatif kur değerini; EVDS sıfır/negatif endeksi kabul edebilir.
- **Etki:** Provider bozulması, birim hatası veya malformed ama parse edilebilir veri kalıcı olarak finansal hesaplara girer. UPSERT doğru kayıtları da bozuk değerle overwrite edebilir.
- **Tetiklenme:** `close=-1`, `high<low`, `volume=-10`, `index=0` gibi syntactically valid payload.
- **Öneri:** Mapper seviyesinde kaynak bağlamlı validation + DB'de son savunma CHECK constraint'leri ekle. Precision/range ve OHLC null kurallarını belgeleyip constraint'leri önce `NOT VALID`, audit, sonra `VALIDATE` ile dağıt.
- **Regresyon testi:** Her mapper için boundary/property-based test; gerçek PostgreSQL entegrasyonunda her ihlal örneğinin constraint tarafından reddedildiği ve mevcut veri audit sorgusunun temiz olduğu doğrulanmalı.

### H-11 — Kısmi/schema-drift payload'lar satır bazında atlanıp tam başarı sayılıyor

- **Dosya/satır:** `src/Saydin.PriceIngestion/Mappers/TwelveDataMapper.cs:40-59`; `EvdsInflationMapper.cs:24-46`; `src/Saydin.PriceIngestion/Adapters/TcmbAdapter.cs:105-123`; `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:405-413`; `EvdsInflationWorker.cs:143-148`.
- **Kanıt:** TwelveData `values` yoksa boş döner ve tarih/close parse edilemeyen her satırı sessizce atlar. EVDS `items` yoksa boş döner; tarih/alan/değer hatalarını sessizce atlar. TCMB malformed XML veya format hatasını `null` ile “yayın yok”a eşitler. Worker beklenen gün/ay sayısı veya dropped-row sayısı kontrol etmeden success yazar.
- **Etki:** Provider alan adı/format değiştirdiğinde tüm seri veya yalnız bazı tarihler kaybolur; kısmi response anchor'ı ileri taşıyabilir. Hata metrik/job status üretmediği için tespit gecikir.
- **Tetiklenme:** TwelveData `datetime` format değişimi, EVDS `items`/`TP_FG_J0` alan değişimi, TCMB malformed 200 response.
- **Öneri:** Mapper sonucu `records + rejectedRows + diagnostics + completeness` taşımalı. Top-level zorunlu alan yokluğu exception olmalı; satır reddi eşik üstünde whole-job failure/degraded yapmalı. Tatil/`ND` gibi beklenen no-data açık kodla temsil edilmeli.
- **Regresyon testi:** 10 satırdan 1 ve 10 satırdan 10 malformed fixture; dropped-row metric/status ve threshold davranışını doğrula. 200-malformed TCMB yanıtının 404 tatilden ayrıldığını test et.

### H-12 — Fiyat kökeni ve ham kanıt saklanmıyor; eski GoldAPI verisi ayırt edilemiyor

- **Dosya/satır:** `infrastructure/postgres/migrations/001_initial.sql:52-69`; `src/Saydin.Shared/Entities/PricePoint.cs:3-15`; `src/Saydin.PriceIngestion/Repositories/PriceIngestionRepository.cs:56-77`; `infrastructure/postgres/migrations/012_faz3_schema.sql:20-34`; `src/Saydin.PriceIngestion/Workers/OpenExchangeRatesWorker.cs:15-20`.
- **Kanıt:** Şemadaki `source_raw` “kalite kontrolü ve yeniden işleme” için tanımlı fakat entity'de alan yok, repository insert/upsert'i bu kolonu hiç yazmıyor. `price_points` satırında provider/source alanı da yok. Migration 012 eski GoldAPI satırlarının seçilemediğini açıkça kabul eder. OXR normal backfill'i yalnız bir yıldır; daha eski metal satırları overwrite edilmeden kalabilir.
- **Etki:** Bir fiyatın hangi provider/payload/revizyondan geldiği kanıtlanamaz; hatalı provider verisi seçici düzeltilemez. Tarih serisi sessizce GoldAPI + OXR karışımı olabilir.
- **Tetiklenme:** Migration 003 öncesi metal verisi bulunan DB, provider değişimi, sonradan reprocessing/audit ihtiyacı.
- **Öneri:** `source`, `source_observation_id/as_of_at`, payload hash ve kontrollü raw/archive referansı ekle. Büyük/secret içerebilen raw payload'ı doğrudan sınırsız saklamak yerine redaction, boyut limiti ve retention uygula. Provider geçişi için doğrulanabilir rebaseline/backfill planı oluştur.
- **Regresyon testi:** Aynı asset/tarihe iki provider yazımında provenance'ın son yazanla atomik güncellendiğini; eski provider satırlarının seçilip yeniden işlendiğini; raw veride secret redaction/size limitini doğrula.

### H-13 — En riskli concrete akışlar ve gerçek resilience/DB davranışı test kapsamı dışında

- **Dosya/satır:** `docs/analysis/04-validation-and-cross-cutting-review.md:121-139`; `tests/Saydin.PriceIngestion.Tests/Adapters/StubHttpMessageHandler.cs:12-36`; `tests/Saydin.PriceIngestion.Tests/Adapters/EvdsInflationAdapterTests.cs:26-45`; `tests/Saydin.PriceIngestion.Tests/Saydin.PriceIngestion.Tests.csproj:10-25`; `src/Saydin.PriceIngestion/Adapters/OpenExchangeRatesAdapter.cs:14-156`; `src/Saydin.PriceIngestion/Mappers/OpenExchangeRatesMapper.cs:6-52`; `src/Saydin.PriceIngestion/Workers/IngestionOrchestrator.cs:15-98`; `src/Saydin.PriceIngestion/Repositories/PriceIngestionRepository.cs:12-114`.
- **Kanıt:** Review sırasında üretilen Cobertura toplam line-rate `%29.04`, PriceIngestion package `%32.7` gösterdi; kalıcı ölçüm özeti `04-validation-and-cross-cutting-review.md` içindedir, silinebilir/ignored `TestResults` yolu bu bulgunun kanıtı değildir. Ölçümde OXR adapter/mapper; CoinGecko/TCMB/Twelve/OXR concrete worker'ları; orchestrator; üç repository ve Shared DbContext/configuration sınıfları `%0` satır kapsamındadır. Tracked test envanterinde OXR adapter/mapper testi yoktur. Mevcut adapter testleri doğrudan `new HttpClient(stub)` oluşturur; `Program`/DI içindeki `AddSaydinResilience` zincirinden geçmez. Stub cancellation token'ı kullanmaz ve senkron tamamlanır. Repository/migration entegrasyon testi yoktur.
- **Etki:** H-01/H-05/H-06/H-07 ve transaction/schema drift regresyonları yeşil unit suite içinde görünmez. Bazı testler boş dönüş gibi tehlikeli mevcut davranışı beklenti olarak sabitler.
- **Tetiklenme:** Provider/error semantics, scheduler, resilience ayarı, SQL veya DI wiring değişikliği.
- **Öneri:** OXR adapter+mapper testlerini; concrete worker clock/schedule testlerini; orchestrator supervision testini; gerçek DI resilience testini; ephemeral PostgreSQL repository/migration contract testlerini ekle. Cancellation-aware async handler ve fake time kullan. Risk bazlı minimum coverage gate'i sınıf/namespace düzeyinde uygula.
- **Regresyon testi:** Bu madde önerilen test paketinin kendisidir; özellikle 401/500/malformed/timeout/cancel, duplicate/concurrent upsert, job atomicity ve fresh/existing migration senaryolarını CI'da zorunlu kıl.

## Medium bulgular

### M-01 — Veri commit'i ile job finalizasyonu atomik değil

- **Dosya/satır:** `src/Saydin.PriceIngestion/Repositories/PriceIngestionRepository.cs:49-84`; `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:402-427`; `EvdsInflationWorker.cs:138-190`; `src/Saydin.PriceIngestion/Repositories/IngestionJobRepository.cs:65-86`.
- **Kanıt:** Fiyat UPSERT transaction'ı commit edildikten sonra farklı DbContext üzerinden `MarkSuccess` çağrılır. Bu çağrı fail ederse dış catch aynı job'ı failed yapmaya çalışır; veri commit edilmişken status `failed` olabilir. EVDS success güncellemesini açıkça best-effort sayar. Status UPDATE etkilenen satır sayısını kontrol etmez.
- **Etki:** Job audit'i veri gerçeğiyle çelişir; retry/reconciliation yanlış karar verebilir. Silinmiş/yanlış job ID update'i sessizce başarı sanılır.
- **Tetiklenme:** Veri commit'inden hemen sonra DB bağlantı kesintisi veya job satırının bulunmaması.
- **Öneri:** Data write ve job terminal state'i aynı DB transaction/connection içinde atomik yap veya outbox/checkpoint tasarla. Update affected-row `==1` invariant'ını zorunlu kıl.
- **Regresyon testi:** UPSERT commit sonrası status update fault injection; gözlenen durumun ya ikisinin commit'i ya ikisinin rollback'i olduğunu doğrula.

### M-02 — Cancellation/shutdown job'ları süresiz `running` bırakıyor

- **Dosya/satır:** `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:361-365`, `419-433`; `EvdsInflationWorker.cs:150-158`; `src/Saydin.Shared/Entities/IngestionJob.cs:34-50`.
- **Kanıt:** Base worker cancellation'da job'ı finalize etmeden rethrow eder; yorum bunun bilinçli olduğunu söyler. EVDS cancellation da failed/cancelled state yazmadan çıkar. Status enum'unda `cancelled/abandoned` yok; stale-running reaper/lease alanı yok. Base failure finalizasyonu aynı iptal edilmiş token'ı kullanabilir.
- **Etki:** Her deploy/shutdown gözlemleme tablolarında sahte aktif işler biriktirir; gerçek stuck job ile normal kapanış ayırt edilemez.
- **Tetiklenme:** HTTP/DB işlemi sürerken SIGTERM, deployment veya host timeout.
- **Öneri:** `cancelled/abandoned` terminal status ve shutdown-safe kısa finalizasyon bütçesi ekle; startup'ta lease süresi geçmiş running job'ları reconcile et.
- **Regresyon testi:** İş ortasında cancellation verip job'ın belirli süre içinde `cancelled/abandoned` olduğunu ve yeni run'ın lease'i devralabildiğini doğrula.

### M-03 — EF ilişki davranışları SQL `ON DELETE RESTRICT` ile aynı değil; kolon drift'i var

- **Dosya/satır:** `src/Saydin.Shared/Data/Configurations/PricePointConfiguration.cs:22-25`, `IngestionJobConfiguration.cs:34-39`; `infrastructure/postgres/migrations/011_phase2_schema_hardening.sql:190-212`; `src/Saydin.Shared/Entities/PricePoint.cs:3-15`; `Asset.cs:5-24`.
- **Kanıt:** SQL iki FK'yi `ON DELETE RESTRICT` yapar, fakat EF configuration `.OnDelete(DeleteBehavior.Restrict)` belirtmez. Required PricePoint ilişkisi EF convention ile Cascade; optional IngestionJob ilişkisi ClientSetNull davranışına gidebilir. Tracking durumuna göre EF dependents'ı silip/null'layarak DB restrict niyetini aşabilir. Ayrıca DB `price_points.source_raw/ingested_at` ve `assets.created_at` kolonları entity modelinde yoktur.
- **Etki:** Asset silme davranışı loaded/unloaded graph'a göre değişebilir; fiyat geçmişi istemeden silinebilir veya job provenance'ı null olabilir. Model bazlı migration üretimi kolonları drop etmeye yönelebilir.
- **Tetiklenme:** EF üzerinden Asset delete veya gelecekte `Add-Migration`/model diff kullanımı.
- **Öneri:** DeleteBehavior'ı açıkça SQL ile eşleştir; shadow/property mapping ile tüm kalıcı kolonları modelle veya deliberate exclusion'ı contract testinde sabitle.
- **Regresyon testi:** Loaded ve unloaded dependent senaryolarında Asset delete'in aynı RESTRICT sonucunu verdiğini; EF modelinin beklenen kolon/delete action fingerprint'iyle eşleştiğini doğrula.

### M-04 — Secret'lar bazı process/URL yüzeylerine taşınıyor; SSRF bulunmadı

- **Dosya/satır:** `src/Saydin.PriceIngestion/Adapters/OpenExchangeRatesAdapter.cs:100-103`; `infrastructure/postgres/apply-migrations.sh:34-42`; `migrations/012b_create_exporter_role.sh:31-34`; `src/Saydin.PriceIngestion/Program.cs:75-119`.
- **Kanıt:** OXR AppId query string'e eklenir; OXR header token authentication da destekler. Varsayılan .NET HTTP instrumentation query değerlerini redakte etse de reverse proxy, custom logging veya exception yüzeyleri tam URL'yi kaydedebilir. Migration runner parola içerebilen `DATABASE_URL`'yi `psql` argv'sine; 012b exporter parolasını `--set=...` argv'sine koyar, böylece process listing'e çıkabilir. Öte yandan BaseAddress'ler sabit ve dinamik source ID'ler URI-escape edildiği için incelenen yolda kullanıcı kontrollü SSRF bulunmadı.
- **Etki:** Aynı hosttaki process gözlemi, debug dump veya ara log katmanı credential sızdırabilir.
- **Tetiklenme:** Process list erişimi veya URL'yi redaksiyonsuz kaydeden proxy/instrumentation.
- **Öneri:** OXR token'ını `Authorization: Token ...` header'ına taşı ([resmî auth](https://docs.openexchangerates.org/reference/authentication)); psql için `PGPASSFILE`/güvenli env/secret file ve stdin mekanizması kullan. Redaction entegrasyon testi ekle.
- **Regresyon testi:** Capturing handler/span exporter ile URI ve loglarda key olmadığını; process argv fixture'ında password bulunmadığını doğrula.

### M-05 — Telemetri provider ilerlemesini/veri tazeliğini ölçmüyor

- **Dosya/satır:** `src/Saydin.PriceIngestion/Program.cs:53-73`; `src/Saydin.Shared/Diagnostics/SaydinMetrics.cs:36-44`; `src/Saydin.PriceIngestion/BackgroundServices/LivenessHeartbeatService.cs:39-50`; `docker-compose.yml:247-256`.
- **Kanıt:** ActivitySource kaydediliyor fakat ingestion akışında custom span başlatılmıyor. Business metriği yalnız EVDS failure counter'ıdır; fiyat provider success/failure, records, duration, lag, last-success, rejected rows, circuit state veya stuck jobs metriği yoktur. Health yalnız process heartbeat'tir.
- **Etki:** Sessiz boş sonuç, kısmi veri, ölü worker ve gecikmiş seri log taraması/son kullanıcı şikâyeti olmadan fark edilmez.
- **Tetiklenme:** H-01/H-06/H-11'deki bütün sessiz arızalar.
- **Öneri:** Provider/asset sınırlı-cardinality etiketleriyle attempt, outcome, duration, accepted/rejected count ve freshness gauge üret; job yaşını ve son başarılı observation tarihini readiness/alert'e bağla. Trace'e job/range/source correlation ekle.
- **Regresyon testi:** Fake exporter ile success, empty, partial, auth, retry-exhausted ve dead worker senaryolarında beklenen metric/span/health durumunu assert et.

### M-06 — Dokümante edilen ingestion test komutu test çalıştırmadan exit 0 verebiliyor

- **Dosya/satır:** `docs/development-guide.md:205-218`; `docker-compose.yml:164-185`.
- **Kanıt:** Compose service entrypoint'i `dotnet`, working directory `/src`. Dokümandaki `docker compose run --rm tests test tests/Saydin.PriceIngestion.Tests` komutunun SDK 10 eşdeğeri exit 0 verdi fakat hiçbir test discovery/summary üretmedi. `.csproj` yolu veya proje dizini çalışma biçiminde 86 test gerçekten çalıştı.
- **Etki:** Geliştirici/CI “yeşil” exit code'u test başarısı sanabilir; kritik regression suite hiç koşmaz.
- **Tetiklenme:** Dokümandaki dizin argümanını aynen kullanma.
- **Öneri:** Tam `.csproj` yolu kullan: `dotnet test tests/Saydin.PriceIngestion.Tests/Saydin.PriceIngestion.Tests.csproj`; CI'da TRX oluşturup minimum test sayısını doğrula.
- **Regresyon testi:** Dokümantasyon smoke job'u komutları çalıştırmalı ve test summary'de beklenen minimum sayıyı parse/assert etmelidir.

### M-07 — Büyük mevcut DB migration'larında lock/disk bütçesi ve otomatik rollback planı yetersiz

- **Dosya/satır:** `infrastructure/postgres/migrations/008b_disable_activity_log_compression.sql:33-58`; `011_phase2_schema_hardening.sql:45-58`, `104-110`, `165-263`; `013_enable_activity_log_compression.sql:26-37`.
- **Kanıt:** 008b tek transaction içinde bütün sıkıştırılmış chunk'ları decompress eder; disk kapasitesi/timeout/batch sınırı yoktur. 011 tek uzun transaction'da non-concurrent index, constraint drop/add/validate, FK ve column type değişimi yapar; `lock_timeout`/`statement_timeout` yoktur. 008b başarı, sonraki migration hata durumunda compression/policy 013'e kadar kapalı kalır.
- **Etki:** Büyük production tablolarında uzun lock, disk dolması, deploy timeout'u ve uzun süre compression'sız çalışma oluşabilir.
- **Tetiklenme:** Dolu activity log hypertable'ında existing-DB migration veya trafik altında 011.
- **Öneri:** Data-size preflight, disk headroom, lock/statement timeout, chunk-batch ve resumable adımlar tanımla; concurrent index'i transaction dışına böl. Her adım için forward-recovery/rollback runbook ve postcondition check ekle.
- **Regresyon testi:** Temsili hacimli TimescaleDB fixture'ında lock bekleme, disk artışı, timeout ve 008b sonrası kontrollü failure/013 recovery senaryosunu ölç.

### M-08 — Top-level fatal exception process exit code'una yansımıyor

- **Dosya/satır:** `src/Saydin.PriceIngestion/Program.cs:149-160`.
- **Kanıt:** `host.RunAsync()` exception'ı top-level catch'te loglanır fakat rethrow veya `Environment.ExitCode = 1` yoktur. Normal top-level sona erme exit code 0 üretir.
- **Etki:** Docker restart policy bunu çoğunlukla yeniden başlatsa da Kubernetes Job, systemd wrapper, deployment hook veya CI fatal startup'ı başarı sayabilir.
- **Tetiklenme:** DB/config/DI/host startup exception'ı veya tüm worker'ların sonlanmasıyla orchestrator exception'ı.
- **Öneri:** Fatal log/flush sonrası non-zero exit sağla; tercih edilen host lifetime davranışını entegrasyon testiyle sabitle.
- **Regresyon testi:** Eksik connection string/no-enabled-worker ile process başlatıp exit code'un non-zero olduğunu doğrula.

### M-09 — `.sh` migration, runner'ın `DATABASE_URL` hedefini kullanmıyor

- **Dosya/satır:** `infrastructure/postgres/apply-migrations.sh:25-42`, `70-77`; `infrastructure/postgres/migrations/012b_create_exporter_role.sh:31-34`.
- **Kanıt:** Runner SQL dosyalarını `DATABASE_URL` üzerinden `run_psql` ile çalıştırır; `.sh` dosyasında yalnız `bash path` çağırır. 012b ise `DATABASE_URL` kullanmaz, doğrudan `POSTGRES_USER`/`POSTGRES_DB` bekler. Dokümante edilen yalnız-`DATABASE_URL` kullanımında unset-variable ile fail eder; ortamda farklı PG/default değerleri varsa ana runner'dan farklı hedefe gidebilir.
- **Etki:** Deploy ortasında kesilme, exporter rolünün yanlış cluster/database'te yaratılması veya sürüm kaydının ana DB'ye yazılmasına rağmen rol adımının başka hedefte olması mümkündür.
- **Tetiklenme:** `DATABASE_URL=... ./apply-migrations.sh` ve POSTGRES_* değişkenlerinin yok/farklı olması.
- **Öneri:** Tüm migration tiplerine tek normalize edilmiş bağlantı sözleşmesi geçir; target fingerprint'i (`server_addr`, database, user) body öncesi/sonrası doğrula. Shell migration aynı runner helper/oturumunu kullanmalı.
- **Regresyon testi:** Yalnız DATABASE_URL ile iki ayrı ephemeral DB kur; rol ve migration kaydının aynı hedefte olduğunu, diğer DB'nin değişmediğini doğrula.

## Low bulgu

### L-01 — Gereksiz fetch/delay kota tüketiyor ve kayıt sayısı gerçeği aşabiliyor

- **Dosya/satır:** `src/Saydin.PriceIngestion/Adapters/OpenExchangeRatesAdapter.cs:48-59`, `91-101`; `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:218-231`, `402-416`; `src/Saydin.PriceIngestion/Repositories/PriceIngestionRepository.cs:29-36`.
- **Kanıt:** OXR her gün için cache hit olsa bile 200 ms bekler; ikinci metalin 365 günlük cache-hit turu yaklaşık 73 saniye gereksiz bekler. Schedule saati geçmişse `IsImmediateFetchNeededAsync` DB'de target zaten var mı bakmadan true döner; her restart aynı günü provider'dan yeniden çeker. Job `points.Count` yazar, repository ise önce duplicate'leri dedupe eder; `records_upserted` gerçek etkilenen satır sayısı değildir.
- **Etki:** Startup/backfill uzar, free-tier kota ve provider yükü gereksiz artar; operasyon metrikleri upsert sayısını yüksek gösterir.
- **Tetiklenme:** İki metal backfill'i, schedule sonrası sık restart, duplicate mapper sonucu.
- **Öneri:** Delay'i yalnız gerçek HTTP request sonrası uygula; schedule sonrası da persisted target kontrolü yap. Repository gerçek distinct/affected sayıyı döndürsün ve inserted/updated/unchanged ayrımı mümkünse ölçülsün.
- **Regresyon testi:** İki metal aynı range testinde HTTP call sayısı ve fake-time delay'i; restart testinde target mevcutsa sıfır fetch; duplicate input'ta reported count'un distinct count'a eşitliği.

## İyi tasarım kararları

- Finansal değerler C# `decimal` ve PostgreSQL `NUMERIC` ile tutuluyor; mapper'ların çoğunda para dönüşümleri `MidpointRounding.AwayFromZero` ile deterministik.
- `IDbContextFactory<SaydinDbContext>` singleton background worker/repository yaşam süresiyle uyumlu; scoped DbContext'in singleton'a sızması engellenmiş.
- Batch `UNNEST ... ON CONFLICT DO UPDATE`, composite price PK ve pre-upsert dedupe normal retry/replay durumunda satır çoğalmasını önlüyor ve round-trip sayısını düşürüyor.
- HTTP client'lar merkezi factory/resilience registration kullanıyor; BaseAddress'ler sabit, dinamik source id'ler URI-escape ediliyor. İncelenen akışta kullanılabilir SSRF yolu görülmedi.
- TwelveData, CoinGecko ve EVDS key'leri header'da taşınıyor; gerçek secret `appsettings.json` içine yazılmamış. OXR ve shell process-argv istisnaları M-04'te ele alındı.
- TCMB aynı gün XML'ini parse edilmiş `XDocument` olarak single-flight cache'liyor; OXR gün cache'i bounded/TTL'li. TCMB Unit normalizasyonu ve OXR troy-ounce → gram/TRY formülü doğru yönde kurulmuş.
- Worker'lar cancellation token'ı HTTP, EF, delay ve timer çağrılarına genel olarak geçiriyor; shutdown cancellation çoğu yerde ayrı ele alınıyor.
- Enflasyon tablosundaki `(period_date, source)` composite PK gerçek TÜİK ile `seed-approximation` kayıtlarını audit amacıyla yan yana tutuyor; okuma önceliği için kaynak ayrımı korunuyor.
- Migration 008b/013, TimescaleDB 2.16 compression ile kolon tipi değişiklik sırasını fresh init mutlu yolunda doğru yönetiyor. Temiz kurulum testi tüm zincirin bugün çalıştığını doğruladı.
- Docker runtime non-root ve image sürümleri pinli; worker'ların config'te disabled-by-default olması istenmeyen dış API/kota kullanımını azaltıyor. Hiç worker yoksa orchestrator fail-fast deniyor.
- Ingestion job status/type ve inflation source için DB CHECK constraint'leri mevcut; 011'de FK delete action'ları SQL tarafında açıkça belirtilmiş.

## Öncelikli düzeltme sırası

1. **Veri kaybını durdur:** C-01, H-01 ve H-11'i birlikte ele al; typed fetch outcome, completeness ve durable checkpoint/gap reconciliation ekle.
2. **Kurulum güvenini kur:** C-02, H-08 ve H-09 için transactional init, schema readiness, advisory lock ve checksum doğrulaması uygula.
3. **Fiyat semantiğini düzelt:** H-02, H-10 ve H-12 ile final/reference ayrımı, domain constraint ve provenance ekle; mevcut veriyi audit/rebaseline et.
4. **Operasyonel görünürlüğü sağla:** H-06, M-02 ve M-05 ile worker supervision, stale-job recovery ve provider freshness health/metric'leri oluştur.
5. **Koruyucu testleri ekle:** H-13 ve M-06; gerçek DI resilience + PostgreSQL/migration entegrasyon testlerini test-count gate ile CI'a bağla.

## Residual risk ve inceleme sınırları

- Provider endpoint'lerine canlı credential ile istek atılmadı; fiyat anlamı, yayın gecikmesi ve granularity değerlendirmesi kod sözleşmesi ile resmi provider dokümanına dayanır. Plan/tier'e özgü davranış staging'de kontrollü doğrulanmalıdır.
- Production veri hacmi, gerçek query planları, Timescale chunk boyutu, lock süresi, disk headroom ve mevcut migration seviyesi görülmedi. M-07 için production-benzeri restore üzerinde rehearsal gerekir.
- Mevcut production verisinde fiyat/TÜFE boşlukları, negatif/tutarsız OHLC, GoldAPI/OXR karışımı veya stale `running` job olup olmadığı sorgulanmadı. Salt-okunur veri kalite audit'i ayrıca çalıştırılmalıdır.
- PostgreSQL bağlantısı üzerinde gerçek paralel worker/race ve process-kill fault injection uygulanmadı; H-07/H-08 kod ve şema garantilerinin yokluğundan türetilmiştir.
- NuGet audit'i davranışsal validasyon için kapatıldığında full solution **380/380** ve gerçek-infra alt kümesi **8/8** geçti; ingestion alt kümesi **86/86** kaldı. Buna karşın normal audit-açık API build'i `NU1903` nedeniyle halen release-blocked durumdadır. Bu ayrım test başarısının bağımlılık güvenliği kapısını geçtiği anlamına gelmesini önler; repository/migration ingestion entegrasyon coverage boşluğu da sürmektedir.
- Bu rapor yalnız inceleme yapar; production kodu, migration veya test dosyası değiştirilmemiştir.
