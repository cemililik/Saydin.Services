# Saydin.Services — Doğrulama ve Çapraz-Kesit Review Raporu

> **Tarih:** 2026-08-18  
> **Branch / commit:** `main` / `9067dd2`  
> **İncelenen kaynak:** Başlangıç anındaki 233 tracked dosya, yaklaşık 22,5 bin fiziksel satır  
> **Çalıştırma ilkesi:** Docker-first; host üzerinde `dotnet` kullanılmadı

## 1. Karar özeti

Kodun davranışsal tabanı beklenenden güçlüdür: güvenlik denetimi davranış testinden bilinçli olarak
ayrıldığında Release solution build'i sıfır warning/error ile tamamlandı; gerçek PostgreSQL ve Redis
üzerinde **380/380 test geçti, skip yok**; boş veritabanında 16 migration başarıyla uygulandı. Temel
mimari yasak taramalarında da ihlal bulunmadı.

Bununla birlikte mevcut commit **yayına hazır değildir**. Normal API Docker build'i, transitive
`Microsoft.OpenApi 2.0.0` paketindeki High advisory nedeniyle restore aşamasında `NU1903` ile duruyor.
Ayrıca GitHub Actions gerçek PostgreSQL/Redis sağlamadığı için sekiz entegrasyon testini sessizce
atlayabiliyor ve activity-log kanalının taşma telemetrisi .NET channel davranışı yanlış varsayıldığı
için üretimde hiç çalışmıyor. İlk iki konu release/CI kapısını, üçüncü konu audit/observability
güvenilirliğini doğrudan etkiliyor.

### Mekanik kapı durumu

| Kapı | Sonuç | Kanıt / yorum |
|---|---|---|
| Normal API image build, NuGet audit açık | **FAIL** | Restore `Microsoft.OpenApi 2.0.0` / `GHSA-v5pm-xwqc-g5wc` için `NU1903`; warning-as-error politikası build'i doğru biçimde durdurdu |
| Ingestion image build, NuGet audit açık | **PASS** | Release publish ve image oluşturma tamamlandı |
| Solution Release build, audit davranış doğrulaması için kapalı | **PASS** | 0 warning, 0 error; 16,95 saniye |
| Unit + gerçek-infra integration testleri | **PASS** | 286 API unit + 86 ingestion + 8 integration = **380 passed, 0 failed, 0 skipped** |
| Fresh migration zinciri | **PASS** | `schema_migrations` içinde 16 kayıt; `001_initial` → `014_schema_migrations`, `008b` ve `012b` dahil |
| PostgreSQL/Timescale doğrulaması | **PASS** | `activity_logs` hypertable + compression açık; `price_points` hypertable mevcut, compression kapalı |
| Compose config | **PASS/Fail-fast** | Zorunlu parolalar yokken beklenen şekilde fail; yalnız komut ömürlü review değerleriyle config geçerli |
| JSON / YAML / XML / shell syntax | **PASS** | Tracked config dosyaları parse edildi; shell scriptleri `bash -n` ile geçti |
| Markdown link / fence kontrolü | **PASS** | Review öncesindeki 24 Markdown dosyasında kırık yerel link veya dengesiz fence bulunmadı |
| Mimari kural taramaları | **PASS** | Yasak servis referansı, Controller, raw-SQL interpolation, production `new HttpClient`, sync-over-async, `async void`, finansal `float/double` ihlali bulunmadı |
| `dotnet format --verify-no-changes` | **FAIL** | Mutable 10.0 SDK taraması 71/180 dosya raporladı; exact Dockerfile SDK'sı da eşdeğer diagnostic kategorileriyle exit 2 verdi; kaynaklar değiştirilmedi |

`NuGetAudit=false` yalnızca derleme ve test davranışını bağımlılık güvenliği kapısından ayrı ölçmek için
kullanıldı. Bu bir çözüm, suppression önerisi veya yayın komutu değildir. Normal yayın zincirinin audit
açıkken geçmesi zorunludur.

## 2. Çalıştırma ortamı ve izolasyon

- Review için ayrı Compose project adı kullanıldı: `saydin-review-20260818`.
- PostgreSQL ve Redis yalnız review'e ait yeni volume'larla başlatıldı. Mevcut başka Compose
  projelerine dokunulmadı.
- Repository'deki sabit `container_name` ve host `5432/6379` bind'ları, başka bir yerel projenin aynı
  portları kullanması nedeniyle ilk denemeyi engelledi. Testi güvenli biçimde izole etmek için yalnız
  review komutuna bağlı bir Compose override ile dependency host port yayınları kaldırıldı.
- Zorunlu parolalar dosyaya veya `.env`'e yazılmadı; yalnız ilgili komut ortamında review-only değerler
  kullanıldı.
- Dockerfile'ın digest-pinned SDK image'ı bu hostta `.NET SDK 10.0.300`; mutable Compose test image'ı
  inceleme anında `10.0.400` çözüldü. Bu drift aşağıda ayrıca bulgudur.

## 3. Doğrulanmış çapraz-kesit bulguları

### XVR-H01 — High zafiyet normal API build ve release artifact üretimini engelliyor

- **Severity:** High
- **Konum:** `Directory.Packages.props:21-29`, `src/Saydin.Api/Saydin.Api.csproj:14-26`,
  `src/Saydin.Api/Dockerfile:9-15`, `Directory.Build.props:1-9`
- **Yeniden üretim:** Audit açık `docker compose build`, API restore adımında transitive
  `Microsoft.OpenApi 2.0.0` için `NU1903` verip durdu. `dotnet package list --include-transitive
  --vulnerable` aynı advisory'yi API ve onu referanslayan iki test projesinde gösterdi; Shared ve
  PriceIngestion grafikleri temizdi.
- **Bağımlılık zinciri:** `Microsoft.AspNetCore.OpenApi 10.0.8 → Microsoft.OpenApi 2.0.0`.
- **Etki:** Mevcut güvenli warning-as-error politikası altında API image üretilemiyor. Advisory,
  circular OpenAPI schema işlenirken process termination / availability etkisi bildiriyor. Uygulama
  şu anda kendi OpenAPI tanımını üretiyor ve untrusted OpenAPI dokümanı parse eden bir akış
  görülmediği için doğrudan uzaktan istismar edilebilirlik düşük görünüyor; release-blocker olduğu ve
  zafiyetli paket grafikte bulunduğu gerçeği değişmiyor.
- **Düzeltme:** `Microsoft.OpenApi` için **en az 2.7.5** güvenli 2.x sürümünü merkezi ve açık biçimde
  pinle; yalnız `Microsoft.AspNetCore.OpenApi` patch bump'ına güvenme, çünkü güncel 10.0.x paketleri
  `Microsoft.OpenApi >= 2.0.0` alt sınırıyla en düşük vulnerable sürümü çözebilir. Restore/build,
  runtime OpenAPI smoke ve transitive vulnerability audit'i yeniden çalıştır. CI'a High/Critical'da
  fail-closed NuGet audit kapısı ekle.
- **Kaynaklar:** [GitHub Advisory GHSA-v5pm-xwqc-g5wc](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc),
  [Microsoft.OpenApi 2.7.5](https://www.nuget.org/packages/Microsoft.OpenApi/2.7.5)

### XVR-H02 — Required CI, sekiz gerçek-infra testini çalıştırmadan yeşil olabilir

- **Severity:** High
- **Konum:** `.github/workflows/ci.yml:54-61`,
  `tests/Saydin.Api.IntegrationTests/Saydin.Api.IntegrationTests.csproj:10-24`,
  `tests/Saydin.Api.IntegrationTests/Fixtures/DatabaseFixture.cs:28-35`,
  `tests/Saydin.Api.IntegrationTests/Fixtures/RedisFixture.cs:15-21`,
  `tests/Saydin.Api.IntegrationTests/ErrorContractHttpTests.cs:16-22`
- **Kanıt:** Workflow `dotnet test` çalıştırıyor fakat PostgreSQL/Redis service container veya
  connection string tanımlamıyor. Projedeki sekiz integration testinin tamamı `SkippableFact`; infra
  yokluğu failure yerine skip. Review ortamında aynı sekiz test gerçek TimescaleDB/Redis ile geçti;
  dolayısıyla testlerin çalışabilir olduğu, CI'ın ise bunları zorunlu kılmadığı doğrulandı.
- **Etki:** Gerçek migration/EF mapping/constraint, Redis Lua atomikliği, HTTP middleware ve problem
  contract regresyonları required PR kapısından kaçabilir.
- **Düzeltme:** Digest-pinned PostgreSQL/TimescaleDB ve Redis CI service'leri ekle, fresh migration
  uygula, benzersiz test DB'si kullan ve integration job'ında `skipped != 0` durumunu failure yap.
  Yereldeki skippable kolaylık required CI'ın fail-closed davranışını etkilememeli.

### XVR-H03 — Activity-log queue drop metriği ve uyarısı hiçbir zaman tetiklenmiyor

- **Severity:** High
- **Konum:** `src/Saydin.Api/Program.cs:249-259`,
  `src/Saydin.Api/Services/ChannelActivityLogger.cs:8-29`,
  `tests/Saydin.Api.Tests/Services/ChannelActivityLoggerTests.cs:30-50`
- **Kanıt:** Production channel `BoundedChannelFullMode.DropWrite` ve callback'siz
  `Channel.CreateBounded(options)` kullanıyor. Kod, dolu kanalda `TryWrite` false dönecek varsayımıyla
  yalnız `!TryWrite` dalında counter ve warning üretiyor. .NET runtime implementasyonu ise
  `DropWrite` modunda öğeyi düşürüp `TryWrite` için **true** döndürüyor; düşen öğeyi gözlemek için
  `itemDropped` callback overload'u kullanılıyor. Mevcut test yalnız exception atılmadığını ve channel
  count'un 1 kaldığını doğruladığından metric/warning körlüğünü yakalamıyor; test yorumları da yanlış
  varsayımı tekrar ediyor.
- **Etki:** 10.000 öğelik queue doygunluğunda activity/audit kayıtları sessizce kaybolur; tam da veri
  kaybını ölçmesi gereken `ActivityLogQueueDrops` metriği sıfır kalır. Alarm/SLO kurulsa bile yanlış
  güven verir.
- **Düzeltme:** `Channel.CreateBounded(options, itemDropped => ...)` callback overload'una geç veya
  gözlenebilir backpressure stratejisi kullan. Counter/logging'i callback'e bağla. Capacity=1 testinde
  ikinci kaydın callback, metric ve kontrollü warning ürettiğini doğrula; burst ve graceful-drain
  testlerini ekle.
- **Kaynaklar:** [.NET bounded channel runtime implementation](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Threading.Channels/src/System/Threading/Channels/BoundedChannel.cs),
  [Microsoft Channels full-mode behavior](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels#full-mode-behavior)

### XVR-M01 — Coverage raporu kritik I/O yollarını korumuyor ve CI ortalaması yanıltıcı

- **Severity:** Medium
- **Konum:** `.github/workflows/ci.yml:60-117`
- **Kanıt:** Ayrı Cobertura sonuçları:

  | Test projesi | Line | Branch | Covered / valid line |
  |---|---:|---:|---:|
  | `Saydin.Api.Tests` | 43,97% | 38,56% | 1.495 / 3.400 |
  | `Saydin.Api.IntegrationTests` | 28,55% | 12,35% | 971 / 3.400 |
  | `Saydin.PriceIngestion.Tests` | 29,04% | 35,75% | 481 / 1.656 |

  Aynı API assembly'sini ölçen unit ve integration raporlarını aritmetik ortalamak kodu iki kez
  sayıyor. Dosya+satır anahtarıyla üç raporun union'ı yaklaşık **%60,90 (2.604 / 4.276)** unique
  executable line gösterdi; ayrı Cobertura'lardan güvenilir branch union üretilemedi. CI'da eşik yok,
  coverage dosyası bulunmazsa step başarıyla çıkıyor.
- **Kritik boşluk örnekleri:** concrete ingestion worker/repository'leri, OXR adapter/mapper,
  ingestion orchestrator ve process bootstrap sıfır direct coverage; `PriceRepository` %1,75,
  `RedisCacheHelper` %8,33, `SavedScenarioRepository` %8,51 civarında.
- **Düzeltme:** Raporları tek coverage modelinde merge et; genel ve changed-lines eşikleri koy;
  coverage artifact yokluğunu failure yap. Önce repository/cache, tüm provider adapter/worker,
  orchestration/shutdown ve endpoint happy-path'lerine risk ağırlıklı test ekle.

### XVR-M02 — Compose adları ve host portları paralel/izole test çalıştırmayı engelliyor

- **Severity:** Medium
- **Konum:** `docker-compose.yml:7-24`, `docker-compose.yml:81-96`,
  `docker-compose.yml:169-185`, `docker-compose.yml:191-226`
- **Kanıt:** Ayrı Compose project adı kullanılmasına rağmen sabit `container_name` değerleri ve
  `127.0.0.1:5432/6379` bind'ları hosttaki bağımsız bir projeyle çakıştı. Review testleri ancak host
  portlarını kaldıran geçici override ile başlayabildi.
- **Etki:** İki checkout/branch/CI shard paralel çalışamaz; onboarding ve otomasyon çevredeki ilgisiz
  container'lara bağlı, nondeterministic olur.
- **Düzeltme:** `container_name` kullanma; test profile/override'ında dependency portlarını hosta
  publish etme. Gerekirse dev portlarını `${POSTGRES_PORT:-5432}` / `${REDIS_PORT:-6379}` yap ve test
  ağında yalnız service DNS kullan.

### XVR-M03 — SDK ve transitive dependency çözümü aynı commit için drift edebiliyor

- **Severity:** Medium
- **Konum:** `global.json:1-5`, `docker-compose.yml:165-185`,
  `src/Saydin.Api/Dockerfile:1-6`, `Directory.Packages.props:1-13`, `.github/workflows/ci.yml:39-44`
- **Kanıt:** `global.json` 10.0.100 belirtse de `rollForward: latestFeature`; digest-pinned Dockerfile
  SDK'sı 10.0.300, mutable `sdk:10.0` test image'ı inceleme anında 10.0.400 idi. `packages.lock.json`
  yok; CI locked restore kullanmıyor. Workflow yorumu buna rağmen ortamların “birebir aynı SDK”
  olduğunu söylüyor.
- **Etki:** Local/container/CI derleyici, analyzer, NuGet audit ve test davranışı farklılaşabilir; aynı
  commit zamanla başka transitive paket çözebilir.
- **Düzeltme:** Tek SDK feature band/digest politikasını seçip Dockerfile, Compose ve CI'da eşle;
  yorumu gerçek davranışa getir. Lock file üret, commit et ve CI restore'u `--locked-mode` ile çalıştır.

### XVR-M04 — FluentAssertions 8.10 kullanımı lisans kararı gerektiriyor

- **Severity:** Medium (project ticari/kapalı kaynak ise); aksi halde bilgi notu
- **Konum:** `Directory.Packages.props:54-63` ve üç test projesindeki `FluentAssertions` referansları
- **Kanıt:** Test çalıştırmalarında v8.10 için lisans uyarısı üretildi. FluentAssertions v8+, açık
  kaynak/non-commercial kullanım dışında ücretli commercial license gerektiriyor. Repository'nin
  hukuki/organizasyonel statüsü bu teknik review kapsamında doğrulanamadı; bu nedenle lisans ihlali
  olduğu iddia edilmiyor.
- **Etki:** Ticari kullanımda onaysız bağımlılık hukuki ve tedarik riski oluşturur; CI warning'i de
  sürekli gürültü üretir.
- **Düzeltme:** Kullanım statüsünü ve lisans envanterini doğrula. Uygunsa lisansla ve kaydını tut;
  değilse v7'de kalma veya assertion kütüphanesi migration'ını ayrı PR'da değerlendir.
- **Kaynak:** [FluentAssertions licensing](https://fluentassertions.com/licensing/)

### XVR-M05 — Bootstrap fatal exception'ları loglandıktan sonra process exit code'u başarı olabilir

- **Severity:** Medium
- **Konum:** `src/Saydin.Api/Program.cs:426-432`,
  `src/Saydin.PriceIngestion/Program.cs:154-160`
- **Kanıt:** Her iki top-level program da `catch (Exception)` içinde fatal log yazıyor fakat exception'ı
  yeniden fırlatmıyor ve non-zero `Environment.ExitCode` atamıyor.
- **Etki:** Bootstrap/configuration/runtime host exception'ı process düzeyinde başarılı çıkış olarak
  yorumlanabilir; orchestrator, deployment ve smoke scriptleri crash ile clean termination'ı güvenilir
  ayıramaz. Mevcut Docker restart policy bazı durumlarda yeniden başlatsa da exit semantiği yanlıştır.
- **Düzeltme:** Flush garantisini koruyarak exception'ı rethrow et veya açıkça non-zero exit code dön;
  bozuk config ile container/process exit code integration testi ekle.

### XVR-L01 — Repository exact SDK formatter standardını karşılamıyor

- **Severity:** Low
- **Konum:** Çapraz; current 10.0 SDK taraması `dotnet format --verify-no-changes` için 71/180
  kaynak dosyayı işaretledi
- **Kanıt:** Mutable 10.0 test SDK'sı 71/180 özetini verdi; Dockerfile ile aynı SDK 10.0.300
  kullanılarak yapılan ikinci verify çalışması da eşdeğer diagnostic kategorileriyle exit code 2 verdi.
  Büyük bölüm alignment/whitespace ve analyzer formatting kategorisinde; build failure veya davranış
  hatası değildir. Review kaynakları otomatik formatlamadı.
- **Düzeltme:** Formatter sürümünü/politikasını sabitle; kontrollü tek mechanical PR ile normalize et;
  sonrasında CI verify kapısı ekle. İşlevsel değişikliklerle toplu format diff'ini karıştırma.

## 4. Paket ve bakım snapshot'ı

Bu kayıtlar tek başına bug değildir; düzenli dependency bakım kuyruğu için snapshot'tır:

- Microsoft .NET paketleri `10.0.8 → 10.0.11`, Npgsql EF `10.0.2 → 10.0.3`,
  `Microsoft.Extensions.Http.Resilience 10.6 → 10.9`, OpenTelemetry 1.15.x → 1.17.x,
  Scalar 2.14.14 → 2.16.20, coverlet 6.0.2 → 6.0.4 ve Test SDK 17.12 → 17.14.1 için
  patch/minor güncellemeler mevcut.
- `xunit 2.9.2` NuGet metadata'sında legacy/deprecated olarak işaretli; kısa vadeli 2.9.3 patch'i ve
  ayrı bir xUnit v3 migration değerlendirmesi gerekir.
- Audit çıktısında Microsoft.OpenApi dışında vulnerable paket görülmedi.

Toplu, doğrulamasız upgrade yerine paketleri risk ve compatibility testleriyle küçük gruplar halinde
güncellemek daha güvenlidir.

## 5. Olumlu güvence bulguları

- Tüm DTO'lar `record`; endpoint katmanında doğrudan repository, service katmanında doğrudan
  `DbContext` erişimi bulunmadı.
- Üretim kodunda ad-hoc `new HttpClient()`, `async void`, `.Result/.Wait()`, `Thread.Sleep`, raw SQL
  interpolation veya string-interpolated log mesajı bulunmadı.
- Finansal değer yollarında `decimal` disiplini korunuyor; taranan production kodunda finansal
  `float/double` bulunmadı.
- Minimal API yaklaşımı korunmuş; Controller ve servisler arası yasak proje referansı yok.
- Zorunlu Compose secret'ları değer yokken fail-fast; Dockerfile runtime kullanıcıları non-root ve
  uygulama base image'ları digest-pinned.
- Fresh migration, enum mapping, hypertable ve gerçek PostgreSQL/Redis contract testleri aynı izole
  review ortamında birlikte geçti.

## 6. Önerilen doğrulama sırası

1. **Release unblock:** `Microsoft.OpenApi >= 2.7.5` pin'i, audit açık restore/build ve OpenAPI smoke.
2. **CI güvenilirliği:** gerçek PostgreSQL/Redis integration job, migration fresh-init ve zero-skip
   zorunluluğu.
3. **Audit-log doğruluğu:** `itemDropped` callback, metric/log regression ve saturation/drain testi.
4. **Operational correctness:** worker supervision/freshness, backup/restore ve alert/SLO kapıları
   (alan raporlarındaki ayrıntılarla).
5. **Quality floor:** merged coverage + risk bazlı eşikler; formatter ve dependency/license policy.

Bu sıra ilk üç High bulgunun aynı anda build güvenliği, CI sinyali ve üretim gözlemlenebilirliğini
düzeltmesini; sonraki iyileştirmelerin güvenilir bir test tabanı üzerinde ilerlemesini sağlar.
