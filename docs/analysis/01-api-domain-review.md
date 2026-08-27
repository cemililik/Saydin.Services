# Saydin.Api / Saydin.Shared kapsamlı API ve domain incelemesi

**Tarih:** 18 Ağustos 2026  
**Kapsam:** `src/Saydin.Api`, `src/Saydin.Shared`, `tests/Saydin.Api.Tests`, `tests/Saydin.Api.IntegrationTests`  
**Sonuç:** 26 bulgu — **0 Critical, 5 High, 17 Medium, 4 Low**

## Yönetici özeti

API'nin katman ayrımı, ProblemDetails hata sözleşmesi, cache-aside yaklaşımı ve test edilebilir saat kullanımı genel olarak iyi kurulmuş. Buna karşılık üretim öncesinde çözülmesi gereken beş yüksek risk var: temiz build'i durduran zafiyetli transitive OpenAPI bağımlılığı, istemcinin kendi kendine ürettiği cihaz kimliğiyle kota/kimlik modelinin aşılabilmesi, sınırsız `ExtraData` ile depolama/yanıt büyütme, DCA reel getirisinin nakit akışlarını yanlış modellemesi ve merkezi loglara tam finansal tutar yazılması.

Özellikle `ChannelActivityLogger` için kaynak yorumları ve test, .NET runtime davranışının tersini varsayıyor. `BoundedChannelFullMode.DropWrite` dolu kanalda yeni öğeyi atsa da `TryWrite` `true` döner; mevcut drop metriği ve uyarı logu bu nedenle üretimde çalışmaz. Bu davranış .NET runtime kaynağı ve resmi Channels dokümantasyonu ile çapraz doğrulandı.

## İnceleme yöntemi ve kapsam

- Önce `CLAUDE.md`, `.editorconfig`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, cache stratejisi, API/aktivite loglama sözleşmeleri ve ilgili ADR'ler okundu.
- `bin/` ve `obj/` üretilmiş çıktıları hariç **132 dosya** incelendi: API 70, Shared 35, birim test 21, entegrasyon test 6. Kategoriler; endpoint ve DTO'lar, middleware/exception handler'lar, servisler, repository'ler, EF entity/configuration'ları, Redis/cache/kota kodu, gözlemlenebilirlik, yerelleştirme kaynakları, proje/config dosyaları ve tüm test kaynaklarıdır.
- Statik olarak giriş doğrulama, hata kodu/status akışları, cihaz kimliği/ownership, hassas veri logları, EF ve Redis atomikliği, cache anahtarları, `DateOnly` sınırları, cancellation, concurrency, sorgu sayıları, lokalizasyon ve test kapsama boşlukları izlendi.
- Temiz Docker SDK ortamındaki normal solution build, `Microsoft.OpenApi 2.0.0` için `NU1903` nedeniyle 0 warning/3 error ile durdu. Yalnız teşhis amacıyla `NuGetAudit=false` verilince solution 0 warning/0 error ile derlendi.
- Aynı container içinde restore+test yapıldığında birim testler **286 passed, 0 failed, 0 skipped**. İki entegrasyon koşusu birbirinden ayrılmalıdır: bu incelemenin altyapı değişkenleri verilmeyen koşusunda **0 passed, 8 skipped, exit 0** görüldü; bu yalnız CI prerequisite eksikliğinin fail-open kalabildiğini yeniden üretir. Kök doğrulamada ayrı, izole Compose projesi ve gerçek PostgreSQL/Redis ile aynı suite **8 passed, 0 failed, 0 skipped** tamamlandı; entegrasyon testlerinin kendisi başarısız değildir.
- İlk `--no-restore` denemelerindeki host/container NuGet-cache uyuşmazlığı ve `POSTGRES_PASSWORD` eksikliği kod hatası olarak sınıflandırılmadı. Testlerin oluşturduğu coverage artefaktları inceleme sonunda temizlendi.

## Bulgular

### API-01 — Temiz build zafiyetli transitive OpenAPI bağımlılığı nedeniyle duruyor — High

- **Kanıt:** `src/Saydin.Api/Saydin.Api.csproj:25` `Microsoft.AspNetCore.OpenApi` paketini alıyor; merkezi sürüm `Directory.Packages.props:24` üzerinde `10.0.8`. Restore grafiği bunun `Microsoft.OpenApi 2.0.0` getirdiğini gösterdi. Temiz build, [GHSA-v5pm-xwqc-g5wc](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc) için `NU1903` ile API ve iki API test projesini durdurdu. Advisory, dairesel şema referansları işlenirken stack overflow/servis sonlanması riskini ve düzeltmenin `2.7.5` olduğunu belirtiyor.
- **Etki / tetiklenme:** Release pipeline normal güvenlik ayarlarıyla build üretemez. Mevcut API yalnız kendi OpenAPI belgesini üretiyor; inceleme kapsamında güvenilmeyen bir OpenAPI belgesi parse eden yol bulunmadığından uzaktan exploit edilebilirlik kanıtlanmış değildir. Yine de audit kapısı ve zafiyetli tedarik zinciri gerçektir.
- **Öneri:** Uyumlu üst `Microsoft.AspNetCore.OpenApi` sürümüne geçin veya `Microsoft.OpenApi >= 2.7.5` için doğrulanmış explicit pin kullanın; NuGet audit'i kapatmayın. Üretilen OpenAPI/Scalar sayfasını smoke test edin.
- **Regresyon testi:** Temiz, boş NuGet cache'li container'da `dotnet restore` ve `dotnet build Saydin.Services.sln -c Release` audit açıkken başarılı olmalı; `dotnet list package --vulnerable --include-transitive` boş dönmeli.

### API-02 — İstemci kontrollü cihaz kimliği kimlik, ownership ve kotanın tek güven kökü — High

- **Kanıt:** `src/Saydin.Api/Endpoints/EndpointExtensions.cs:42-84` herhangi bir imza veya sunucu doğrulaması olmadan uygun karakterli header'ı kimlik kabul ediyor. `src/Saydin.Api/Services/SavedScenarioService.cs:28-48` listeyi, `:112-128` silmeyi yalnız bu ID'den çözülen kullanıcıya bağlıyor. `src/Saydin.Api/appsettings.json:15-18` IP rate limiter'ı varsayılan olarak kapalı tutuyor; `src/Saydin.Api/Program.cs:315-317` kapalıyken korumayı pipeline'a hiç eklemiyor.
- **Etki / tetiklenme:** Saldırgan yeni rastgele ID'ler üreterek günlük hesaplama/asset kotasını ve kullanıcı başına senaryo sınırını sınırsız sıfırlayabilir. Bir cihaz ID'si log, deep-link, yedek veya istemci sızıntısıyla ele geçirilirse aynı header premium tier'ı, senaryo okuma ve silme yetkisini de taşır. UUID entropisi kör tahmini zorlaştırır; sorun tahmin değil, mint etme ve bearer-secret semantiğidir.
- **Öneri:** Sunucu tarafından imzalanmış, döndürülebilir installation credential veya gerçek auth kullanın; ownership'i doğrulanmış principal'a bağlayın. Üretimde IP/WAF ve tercihen dağıtık limiter'ı zorunlu kılın, rate limiting kapalı production startup'ını reddedin. Device ID'yi tek başına yetkilendirme kanıtı saymayın.
- **Regresyon testi:** Aynı IP'den sürekli yeni `X-Device-ID` üreten istekler yine 429'a ulaşmalı; başka installation credential ile bir senaryonun GET/DELETE'i 404/403 olmalı; credential rotation sahipliği güvenli biçimde taşımalı.

### API-03 — `ExtraData` için boyut/derinlik/şema sınırı yok; depolama ve yanıt büyütme mümkün — High

- **Kanıt:** `src/Saydin.Api/Models/Requests/SaveScenarioRequest.cs:5-14` keyfi `JsonElement?` kabul ediyor. `src/Saydin.Api/Services/SavedScenarioService.cs:60-64` yalnız temel alanları doğruluyor ve `:94` JSON'u doğrudan entity'ye geçiriyor. `src/Saydin.Shared/Data/Configurations/SavedScenarioConfiguration.cs:38-43` alanı sınırsız `jsonb` olarak modelliyor. Premium kullanıcı için liste `src/Saydin.Api/Repositories/SavedScenarioRepository.cs:59-64` üzerinde pagination olmadan bütün satırları ve JSON'u getiriyor.
- **Etki / tetiklenme:** Body sunucu/proxy sınırına kadar büyük JSON, CPU/LOH allocation, PostgreSQL TOAST/depolama, WAL/backup ve tekrarlanan GET response maliyeti yaratır. API-02 ile ID rotasyonu kullanıcı başına sınırı de aşabildiğinden kalıcı storage amplification oluşur.
- **Öneri:** Endpoint body limitini düşük ve açık bir değere sabitleyin; `ExtraData` için UTF-8 byte, JSON depth, property count ve senaryo-tipine özel şema/allowlist uygulayın. DB'de de `octet_length(extra_data::text)` CHECK ile defense-in-depth kurun; listeyi sayfalayın ve premium için de sistemsel hard cap belirleyin.
- **Regresyon testi:** Sınırın bir byte altı kabul, bir byte üstü 413/400 olmalı ve repository `CreateAsync` çağrılmamalı. Derin, geniş ve yüksek sıkıştırma oranlı payload testleri ile GET response üst sınırı doğrulanmalı.

### API-04 — DCA reel getiri, tüm taksitleri başlangıçta yatırılmış varsayıyor — High

- **Kanıt:** `src/Saydin.Api/Services/DcaCalculator.cs:148-191` yatırımları farklı tarihlerde oluşturuyor; fakat `:230-242` sadece başlangıç/son TÜFE'sini kullanıp toplam nominal portföy getirisini tam dönem enflasyonuna bölüyor. `tests/Saydin.Api.Tests/Services/DcaCalculatorTests.cs:423-438` yalnız sonucun null olmadığını kontrol ediyor, beklenen reel matematiği doğrulamıyor.
- **Etki / tetiklenme:** Aylık/haftalık DCA'da son taksit de ilk taksit kadar enflasyona maruz kalmış sayılır. Örneğin varlık fiyatı sabit, TÜFE 100→120 iken nominal getiri %0 olan seri mevcut formülle %-16,67 reel getiri gösterir; oysa sonraki nakit akışlarının maruz kaldığı enflasyon daha düşüktür. Finansal karar ekranı sistematik olarak yanıltılır.
- **Öneri:** Her alım tarihindeki TÜFE ile her nakit akışını bitiş tarihine reel olarak taşıyın ve reel maliyet/reel P&L üretin; yahut XIRR benzeri para-ağırlıklı reel metriği açık isimle sunun. Bu veri yoksa alanı kaldırın veya “basitleştirilmiş başlangıç-bitiş düzeltmesi” diye dürüstçe yeniden adlandırın.
- **Regresyon testi:** En az üç farklı tarihte yatırım, tarih başına TÜFE ve sabit varlık fiyatı içeren elle hesaplanabilir fixture ile tam beklenen reel maliyet ve yüzde assert edilmeli; yalnız `NotBeNull` yeterli olmamalı.

### API-05 — Senaryo plan limiti count-then-insert yarışıyla aşılabiliyor — Medium

- **Kanıt:** `src/Saydin.Api/Services/SavedScenarioService.cs:70` limit kontrolünü çağırıyor; `:178-185` ayrı `COUNT` yapıyor; insert ancak `:103` üzerinde gerçekleşiyor. `src/Saydin.Api/Repositories/SavedScenarioRepository.cs:66-72` bağımsız `SaveChangesAsync` kullanıyor. Kullanıcı başına limiti DB'de zorlayan constraint/atomik komut yok.
- **Etki / tetiklenme:** Limit-1 durumunda iki paralel POST aynı count'u görüp ikisi de insert eder; plan invariant'ı bozulur. Daha yüksek eşzamanlılıkla sınır daha fazla aşılır.
- **Öneri:** Count+insert'i kullanıcı bazlı advisory lock veya serializable transaction içinde atomik yapın; tercihen repository tek “limit altında insert” operasyonu sunsun. Retry/serialization failure sözleşmesini 422/409'a kontrollü çevirin.
- **Regresyon testi:** Gerçek PostgreSQL üzerinde limit 10, mevcut count 9 iken aynı kullanıcı için bariyerle iki paralel save başlatın; tam biri 201, diğeri limit hatası olmalı ve DB count 10 kalmalı.

### API-06 — Tam finansal tutarlar merkezi uygulama loglarına yazılıyor — High

- **Kanıt:** `src/Saydin.Api/Services/WhatIfCalculator.cs:345-348` hedef ve gereken yatırım tutarını, `:499-502` yatırım tutarını; `src/Saydin.Api/Services/DcaCalculator.cs:290-293` periyodik tutarı structured Information loguna yazıyor. `src/Saydin.Api/Program.cs:53-68` bu logları JSON console ve OTLP'ye gönderiyor. Activity log tarafında bucket uygulanmış olsa da bu ikinci telemetri kanalı aynı minimizasyonu uygulamıyor.
- **Etki / tetiklenme:** Her başarılı hesaplama, kullanıcının finansal niyet/tutarını container logu ve merkezi observability backend'ine çoğaltır. Erişim çevresi ve saklama süresi DB sözleşmesinden farklı olabilir; veri minimizasyonu ve olay müdahalesi kapsamı büyür. Senaryo endpoint'i ayrıca serbest metin label'ı `src/Saydin.Api/Endpoints/ScenariosEndpoints.cs:70-76` ile activity JSON'una yazıyor; label PII içerebilir.
- **Öneri:** Ham tutar/sonuç ve serbest metin label'ı loglamayın. Gerekliyse `AmountBucket` benzeri düşük kardinaliteli bucket, tip/sembol ve sonuç sınıfı kullanın. OTLP/console için merkezi redaction ve kısa retention/access policy uygulayın.
- **Regresyon testi:** Sentinel tutar ve PII içeren label ile hesaplama/save yapın; in-memory logger, console capture ve OTLP test exporter çıktısında sentinel değerlerin hiç bulunmadığını assert edin.

### API-07 — `DropWrite` kayıt düşürüyor fakat mevcut drop metriği hiç artmıyor — Medium

- **Kanıt:** `src/Saydin.Api/Program.cs:249-259` callback'siz `DropWrite` bounded channel yaratıyor. `src/Saydin.Api/Services/ChannelActivityLogger.cs:18-29` metriği yalnız `TryWrite == false` iken artırıyor. Oysa [.NET runtime BoundedChannel kaynağında](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Threading.Channels/src/System/Threading/Channels/BoundedChannel.cs) `DropWrite`, dolu kanalda öğeyi düşürdükten ve opsiyonel `itemDropped` callback'ini çağırdıktan sonra `true` dönüyor; [resmi Channels dokümantasyonu](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels) drop gözlemi için `Channel.CreateBounded(options, itemDropped)` overload'unu gösteriyor. `tests/Saydin.Api.Tests/Services/ChannelActivityLoggerTests.cs:31-50` yanlış varsayımı yorumda tekrarlayıp yalnız “throw etmedi/count 1” kontrol ediyor.
- **Etki / tetiklenme:** 10.000 öğelik kuyruk dolduğunda yeni activity log sessizce kaybolur; `saydin.activity_log.queue.drops.total` sıfır kalır ve alarm çalışmaz. `TryWrite=false` daha çok completed channel durumunu ifade ettiğinden mevcut kod drop ve kapanmayı da yanlış sınıflandırır.
- **Öneri:** Channel'ı `Channel.CreateBounded<ActivityLog>(options, itemDropped => ...)` ile kurun ve metric/tag üretimini callback'e taşıyın. Warning'i örnekleyin/rate-limit edin; callback'e action allowlist uygulamasını koruyun. Logger içindeki `!TryWrite` yolunu “channel closed/rejected” olarak ayrı telemetriye çevirin.
- **Regresyon testi:** Capacity 1 kanala iki farklı action yazın; ilk öğenin kaldığını, callback'in yalnız ikinci öğeyle tam bir kez çağrıldığını ve drop counter'ın 1 arttığını `MeterListener` ile doğrulayın. Writer complete edilmişken `TryWrite=false` için drop counter artmamalı.

### API-08 — Geçici enrichment hataları bir saat boyunca cache'lenen eksik başarıya dönüşüyor — Medium

- **Kanıt:** Reverse WhatIf fiyat geçmişi hatasını `src/Saydin.Api/Services/WhatIfCalculator.cs:271-281`, enflasyon hatasını `:289-319` yutuyor; sonucu `:343` bir saat cache'liyor. Normal WhatIf aynı deseni `:421-472` ve `:496`, DCA enflasyon hatasını `src/Saydin.Api/Services/DcaCalculator.cs:226-257` ve sonucu `:288` üzerinde cache'liyor.
- **Etki / tetiklenme:** PostgreSQL/servis kısa süreli hata verdiğinde ilk istek boş chart veya null reel sonuç alır ve bu eksik veri aynı cache key'i kullanan tüm kullanıcılara bir saat dağıtılır; backend iyileşse bile retry yapılmaz. Response, “veri yok” ile “hesap başarısız” ayrımını taşımıyor.
- **Öneri:** Degraded sonucu hiç cache'lemeyin veya saniyeler düzeyinde ayrı TTL kullanın; response'a `dataStatus/warnings` ekleyin. Geniş `catch (Exception)` yerine yalnız beklenen opsiyonel bağımlılık hatalarını yakalayın.
- **Regresyon testi:** Repository ilk çağrıda transient hata, ikincide veri döndürsün. İkinci aynı request repository'yi yeniden çağırmalı ve zengin sonucu dönmeli; eksik response bir saatlik cache'ten gelmemeli.

### API-09 — Asset cache “signature”ı yalnız count; aynı sayıdaki içerik değişikliği saatlerce görünmüyor — Medium

- **Kanıt:** `src/Saydin.Api/Services/AssetService.cs:23-44` ve `:57-86` `assets:sig` değerini `GetActiveAssetCountAsync` ile üretiyor, listeleri sırasıyla 6 saat/1 saat cache'liyor. `:49-52` yorumu içerik hash'i iddia etse de gerçek list key hâlâ count'a bağlı. `src/Saydin.Api/Repositories/PriceRepository.cs:16-17` yalnız count döndürüyor.
- **Etki / tetiklenme:** Bir asset pasifleştirilip başka biri aktive edildiğinde veya symbol/display/category aynı count korunarak değiştiğinde eski liste/key yeniden kullanılır. Yeni asset bulunamaz, pasif asset sunulur, lokalize ad/kategori saatlerce stale kalır.
- **Öneri:** DB tarafından monoton catalog revision/`max(updated_at)` veya kanonik içerik hash'i üretin; yönetim mutation'ında ilgili anahtarları açıkça invalidate edin. Process-local symbol index hash'i Redis'teki stale listeyi düzeltemez.
- **Regresyon testi:** İlk çağrı iki asset cache'lesin; repository aynı count ile farklı symbol/display/category döndürsün ve revision değişsin. İkinci çağrı eski list key'ini hit etmemeli.

### API-10 — Kota release gece yarısında başka günün sayacını azaltabiliyor — Medium

- **Kanıt:** `src/Saydin.Api/Services/DailyLimitGuard.cs:85-121` acquire sırasında günün key'ini oluşturuyor. `:136-158` release saat sağlayıcısını yeniden okuyup o anki günün key'ini hesaplıyor. Çağıranlar başarısız işlemde yalnız user/device/prefix ile release ediyor; edinilen key/lease taşınmıyor.
- **Etki / tetiklenme:** İstek A 23:59:59'da gün-1 kotasını alır, gece yarısından sonra istek B gün-2 kotasını alır; A sonradan hata verip release ederse gün-2 key'ini decrement ederek B'nin kullanımını siler. Gün-1 sayacı da gereksiz dolu kalır.
- **Öneri:** `TryAcquireAsync` exact Redis key/token içeren opaque lease döndürsün; release yalnız o lease'i atomik olarak bıraksın. Gerekirse Lua'da request token/idempotency kullanın.
- **Regresyon testi:** FakeTimeProvider ile acquire sonrası gece yarısını geçin; gün-2'de ayrı acquire yapıp gün-1 lease'ini bırakın. Redis'te gün-1 azalmalı, gün-2 değişmemeli.

### API-11 — Save request DB invariant'ları öncesinde doğrulanmıyor; kullanıcı hatası 500 oluyor — Medium

- **Kanıt:** `src/Saydin.Api/Services/SavedScenarioService.cs:60-64` doğrulama zincirinde AmountType ve tarih sıralaması yok; `:94-99` değerleri ham geçiriyor, `:226-245` yalnız opsiyonel string uzunluğu ve pozitif amount kontrol ediyor. EF configuration `src/Saydin.Shared/Data/Configurations/SavedScenarioConfiguration.cs:17-28` type constraint'i modellese de unit/date constraint'lerini modellemiyor. Gerçek şema `infrastructure/postgres/migrations/001_initial.sql:149-150` üzerinde unit allowlist ve `sell_date > buy_date` CHECK içeriyor.
- **Etki / tetiklenme:** Eksik/geçersiz `AmountType`, eşit veya ters tarih service'ten geçip `SaveChangesAsync` sırasında `DbUpdateException` üretir; özel handler olmadığından global 500'a dönüşür. WhatIf hesaplama buy==sell'i kabul ederken aynı sonuç save edilince DB tarafından reddedilebilir.
- **Öneri:** AmountType'ı trim/lowercase edip senaryo tipine göre allowlist ile doğrulayın; `SellDate is null || SellDate > BuyDate` kuralını açık 400 domain validation yapın. EF modelini migration constraint'leriyle senkronlayın; SQLSTATE/check name'i son savunma olarak kontrollü hata koduna map edin.
- **Regresyon testi:** Invalid/missing unit, sell<buy ve sell==buy için HTTP 400 + lokalize ProblemDetails assert edin; gerçek PG testinde hiçbir satır yazılmamalı ve 500 görülmemeli.

### API-12 — Ayarlanmış piyasa tarihi chart ve enflasyon sorgularına taşınmıyor — Medium

- **Kanıt:** `src/Saydin.Api/Services/WhatIfCalculator.cs:375-380` fiili buy/sell tarihini fiyat noktasından hesaplıyor; ancak price range `:424`, TÜFE sorgusu `:443-444` üzerinde kullanıcının istediği tarihleri kullanıyor. Reverse yolunda da fiili tarihler `:214-218`, range `:274`, TÜFE `:293-294` olarak ayrışıyor.
- **Etki / tetiklenme:** Ayın ilk günü hafta sonu/tatil olup fiyat önceki ayın son işlem gününe çekildiğinde, chart fiili alış fiyatını içermeyebilir ve TÜFE başlangıç ayı yanlış seçilebilir. Response aynı hesaplama içinde farklı ekonomik dönemleri kullanır.
- **Öneri:** Ekonomik hesaplama, chart ve inflation için `effectiveBuyDate/effectiveSellDate` tek source-of-truth üretin. Ürün özellikle seçilen takvim tarihini istiyorsa bu semantiği alan adlarında ayırın ve fiili fiyatın hangi döneme ait olduğunu açık gösterin.
- **Regresyon testi:** Ayın ilk pazar gününü önceki ay cuma fiyatına clip eden fixture ile range başlangıcı ve TÜFE repository argümanlarının fiili cuma olduğunu assert edin.

### API-13 — DCA, istek başına yüzlerce ardışık Redis/DB roundtrip yapıyor — Medium

- **Kanıt:** `src/Saydin.Api/Services/DcaCalculator.cs:137-150` tüm alım tarihlerini oluşturup her tarih için sırayla `GetNearestPriceAsync` bekliyor; terminal fiyat için `:195` bir çağrı daha yapıyor. Üst sınır 600 nokta ve kontrol liste tamamen üretildikten sonra. Haftalık generator `:305-316` çok geniş tarih aralığında önce yüz binlerce tarih oluşturabilir.
- **Etki / tetiklenme:** Tek premium DCA isteği 601 seri roundtrip yapabilir; cold cache'te DB connection/latency baskısı ve uzun request süresi yaratır. Çok geniş haftalık tarih aralığı 600 hatasına ulaşmadan gereksiz CPU/memory tüketir. API-02 ile abuse kolaylaşır.
- **Öneri:** Nokta sayısını aritmetik olarak önceden doğrulayın veya max+1'de üretimi kesin. Tüm tarih aralığını tek sorguda alın ve nearest işlem günlerini bellekte/bulk SQL ile eşleyin; terminal fiyatı aynı dataset'ten kullanın.
- **Regresyon testi:** 600 alımlı istek için command interceptor ile DB sorgu sayısının sabit küçük bir sınırda kaldığını assert edin; DateOnly tam aralığına yakın input hızlı 400 vermeli ve büyük liste allocate etmemeli.

### API-14 — Activity log hata kodunu doldurmuyor ve erken reddedilen istekleri hiç görmüyor — Medium

- **Kanıt:** `src/Saydin.Api/Middleware/ActivityLogMiddleware.cs:24-40` mevcut builder'a yalnız response status yazıyor. `src/Saydin.Api/Helpers/ActivityLogBuilder.cs:63-67` `WithError` sunuyor fakat production kullanımına rastlanmadı. Rate limiter `src/Saydin.Api/Program.cs:385-388` activity middleware'den önce; invalid device endpoint filter'ı da handler içindeki `GetOrCreateActivityLog` çağrısına ulaşmadan döner. Middleware yorumu `src/Saydin.Api/Middleware/ActivityLogMiddleware.cs:10-15` “tüm istekler” iddiasında.
- **Etki / tetiklenme:** Exception/429 satırlarında `error_code` null kalır; rate-limit, eksik/geçersiz device ID, binding ve filter reddi için builder olmadığı için satır oluşmaz. Abuse ve hata funnel'ları eksik/yanıltıcıdır.
- **Öneri:** Activity context'i endpoint öncesi merkezi middleware'de oluşturun veya ayrı request-audit yolu kurun. ProblemDetails `code` değerini güvenli bir response feature/item üzerinden middleware'e taşıyın; rate limiter reddini düşük maliyetli ayrı counter/log ile gözleyin.
- **Regresyon testi:** Feature-disabled, 429, invalid device ve malformed JSON isteklerinde doğru action sınıfı, final status ve `error_code` bulunan tek satır/counter assert edin.

### API-15 — `/metrics` ve `/health` uygulama katmanında herkese açık ve limiter dışında — Medium

- **Kanıt:** `src/Saydin.Api/Program.cs:326-329` iki path'i global limiter'dan açıkça çıkarıyor; `:414-415` auth/policy olmadan map ediyor.
- **Etki / tetiklenme:** Deployment ağı ayrıca kısıtlamıyorsa dış istemci metric adları, process/runtime davranışı ve dependency sağlığını toplayabilir; limiter dışı scrape ile serialization/health dependency çağrıları sürekli tetiklenebilir. İnceleme kapsamında ağ katmanı koruması garanti edilemiyor.
- **Öneri:** Management endpoint'lerini ayrı internal listener/port, private network veya mTLS/auth policy arkasına alın. Liveness dışında anonymous dış erişimi kapatın; scrape concurrency/frequency sınırı uygulayın.
- **Regresyon testi:** Public test client `/metrics` ve detaylı readiness'e 401/403/404 almalı; yalnız monitoring principal/network başarılı olmalı.

### API-16 — Kalıcı cihaz/konum davranış verisi için otomatik retention veya silme yolu yok — Medium

- **Kanıt:** `src/Saydin.Shared/Entities/ActivityLog.cs:9-18,28` stabil device ID, maskeli IP, ülke/şehir, davranış JSON'u ve zamanı saklıyor. İncelenen API/Shared kodunda retention, anonymization veya device deletion akışı yok. Repo dokümanı `docs/architecture/activity-logging.md:711-744` yalnız 7 gün sonra compression kuruyor; bir yıllık object-storage/drop adımını gelecek/operasyonel karar olarak anlatıyor. Compression silme değildir.
- **Etki / tetiklenme:** Harici operasyon job'ı ayrıca kurulmadıysa pseudonymous davranış ve konum verisi süresiz büyür; storage maliyeti, breach etkisi ve veri sahibi silme taleplerinin kapsamı artar.
- **Öneri:** Uygulanan ve izlenen Timescale retention/anonymization policy, açık saklama matrisi, yasal amaç/erişim kontrolü ve device/account silme workflow'u ekleyin. Policy'nin kurulu olduğunu startup/ops smoke check ile doğrulayın.
- **Regresyon testi:** Ephemeral Timescale üzerinde eski/yeni satırlarla retention job'ı çalıştırın; sınır dışı satırlar silinmeli/anonymize edilmeli, güncel satırlar kalmalı. Device deletion sonrası ilişkili tüm pseudonymous veriyi sorgulayarak doğrulayın.

### API-17 — Entegrasyon suite'i altyapı yokken “yeşil” ve sıfır testli bitebiliyor — Medium

- **Kanıt:** `tests/Saydin.Api.IntegrationTests/Saydin.Api.IntegrationTests.csproj:10-15` DB/Redis yoksa testlerin skip olacağını bilerek tanımlıyor. Fixture'lar tüm bağlantı hatalarını availability=false'a çeviriyor; bütün sekiz test `SkippableFact`. Altyapı değişkenleri verilmeyen koşuda **0 passed, 8 skipped, exit 0** gözlendi; bu bulgunun reprodüksiyonudur. Ayrı kök doğrulamada izole Compose + gerçek PostgreSQL/Redis ile **8 passed, 0 failed, 0 skipped** sonucu alındı; dolayısıyla bulgu testlerin bozuk olması değil, altyapı/prerequisite yokluğunun başarılı process sonucu üretmesidir.
- **Etki / tetiklenme:** CI env/secret/network/schema yanlışsa kırmızı yerine başarılı job oluşur. Repository SQL'i, CHECK constraint'leri, concurrency, cache ve HTTP pipeline regressions'ı hiç çalışmadan release ilerleyebilir.
- **Öneri:** Local optional suite ile CI-required suite'i ayırın. CI'da prerequisite probe başarısızlığını test failure yapın ve minimum executed integration-test sayısını doğrulayın. Ephemeral, yalnız test için ayrılmış PG/Redis kullanın.
- **Regresyon testi:** Connection string kaldırılmış CI senaryosu non-zero dönmeli; normal CI koşusunda skipped=0 ve beklenen minimum test sayısı kontrol edilmeli.

### API-18 — Bilinmeyen asset WhatIf'te yanlış hata koduna dönüşebiliyor — Medium

- **Kanıt:** Normal WhatIf `src/Saydin.Api/Services/WhatIfCalculator.cs:373-383`, reverse `:214-221` üzerinde asset varlığını kontrol etmeden price lookup yapıyor. DCA ise `src/Saydin.Api/Services/DcaCalculator.cs:132-135` doğru sırada asset'i doğruluyor. Fiyat satırı olmayan bilinmeyen symbol önce `PriceNotFoundException` üretir; `AssetNotFoundException` satırına ulaşılmaz.
- **Etki / tetiklenme:** İstemci geçersiz symbol için dokümante edilen `asset_not_found` yerine `price_not_found` alır; UI hata mesajı ve retry kararı yanlış olur. Unit mock'ları unknown-asset testinde fiyat döndürerek bu üretim sırasını maskeleyebilir.
- **Öneri:** Cache miss sonrası önce active asset'i resolve edin, ardından fiyat sorgulayın; inactive/bilinmeyen sembol ve valid asset/no-price durumlarını ayrı tutun.
- **Regresyon testi:** Boş gerçek DB'de bilinmeyen symbol ile WhatIf/reverse tam ProblemDetails type/code'unun `asset_not_found` olduğunu; var olan asset fakat eksik tarih için `price_not_found` olduğunu assert edin.

### API-19 — `DateOnly` uç değerleri doğrulanmadığı için geçerli JSON 500 üretebilir — Medium

- **Kanıt:** Request DTO'ları `src/Saydin.Api/Models/Requests/WhatIfRequest.cs:3-9` ve `DcaRequest.cs:3-10` açık alt/üst tarih sınırı koymuyor. `src/Saydin.Api/Repositories/PriceRepository.cs:36-37` `date.AddDays(±maxDays)` çağırıyor. Haftalık DCA loop'u `src/Saydin.Api/Services/DcaCalculator.cs:313-316` son iterasyondan sonra da `AddDays(7)` yapıyor.
- **Etki / tetiklenme:** `0001-01-01` veya `9999-12-31` bind edilebilir; nearest price ya da weekly generator `ArgumentOutOfRangeException` fırlatıp global 500 üretir. Tek istek kontrollü hata sözleşmesini ihlal eder.
- **Öneri:** Domain'in desteklenen veri tarih aralığını request başında doğrulayın. Repository'de pencereyi `DateOnly.MinValue/MaxValue` sınırlarına clamp edin; generator'ı increment öncesi break/max+1 kontrolüyle taşma güvenli yapın.
- **Regresyon testi:** Min/Max ve ±7 gün sınırındaki tarihler tüm ilgili HTTP endpoint'lerde lokalize 400 veya güvenli 404 vermeli; hiçbirinde 500 olmamalı.

### API-20 — Fatal startup hatası loglandıktan sonra process başarı koduyla çıkabilir — Medium

- **Kanıt:** `src/Saydin.Api/Program.cs:426-432` top-level exception'ı yakalayıp fatal logluyor fakat yeniden fırlatmıyor ve `Environment.ExitCode` ayarlamıyor.
- **Etki / tetiklenme:** Geçersiz connection string, port bind, options veya DI startup hatasında process exit code 0 olabilir. `on-failure` supervisor/CI bunu başarılı sonlanma sayıp restart/alert üretmeyebilir.
- **Öneri:** Flush garantisini `finally` içinde koruyup exception'ı rethrow edin veya `Environment.ExitCode = 1` ayarlayın. Container restart policy'ye güvenmek yerine Unix exit sözleşmesini doğru tutun.
- **Regresyon testi:** Bilerek geçersiz startup config ile process başlatın; kısa sürede non-zero exit ve fatal log assert edin.

### API-21 — Tek `/health`, liveness ile optional Redis readiness'ini birbirine bağlıyor — Medium

- **Kanıt:** `src/Saydin.Api/Program.cs:159-173` PostgreSQL ve Redis check'lerini aynı registry'ye ekliyor; `:175-177` Redis down olsa API'nin DB fallback ile çalışacağını açıkça söylüyor. `:415` tek `/health` endpoint'i tüm check'leri çalıştırıyor.
- **Etki / tetiklenme:** Redis kesintisinde fonksiyonel API `Unhealthy` olur. Aynı URL liveness probe olarak kullanılırsa orchestrator gereksiz restart/traffic removal döngüsü yaratır ve tasarlanan cache fail-open davranışını bozar.
- **Öneri:** `/live` yalnız process/self check; `/ready` zorunlu dependencies için tag predicate ile ayrılmalı. Redis'in readiness'teki rolünü ürün SLO'suna göre `Degraded` veya optional policy olarak tanımlayın; detayları public response'tan saklayın.
- **Regresyon testi:** Redis down/PostgreSQL up iken `/live` 200 olmalı; readiness'in beklenen 200-degraded veya 503 politikası ayrıca assert edilmeli ve liveness restart tetiklememeli.

### API-22 — Activity writer tüm `DbUpdateException`ları toxic row sanıyor — Medium

- **Kanıt:** `src/Saydin.Api/BackgroundServices/ActivityLogWriter.cs:152-174` `DbUpdateException`ın tamamını `Toxic`, diğer bütün exception'ları `Transient` sınıflandırıyor. `:177-194` toxic batch'i bisect edip tek satıra kadar düşürüyor.
- **Etki / tetiklenme:** PostgreSQL timeout, connection reset, failover veya serialization gibi provider hataları EF tarafından `DbUpdateException` ile sarılırsa geçici outage yüzlerce satırlık batch'in tekrar tekrar bölünüp tamamının “toxic row” diye kalıcı kaybedilmesine yol açar. Tersine programlama/serialization hataları gereksiz retry edilir.
- **Öneri:** Inner `PostgresException.SqlState`, `NpgsqlException.IsTransient`, timeout ve EF execution strategy sinyalleriyle sınıflandırın. Yalnız constraint/data-shape SQLSTATE'lerini toxic kabul edin; outage'ta bounded retry/circuit-breaker ve açık drop reason kullanın.
- **Regresyon testi:** Fake saver ile transient bağlantı hatası verin; bisection olmadan batch retry edilmeli. Check-constraint ihlal eden tek satırlı batch bisect edilip yalnız o satır drop edilmeli; iyi komşular yazılmalı.

### API-23 — Tanımlı temel business metric'leri production kodunda hiç kullanılmıyor — Low

- **Kanıt:** `src/Saydin.Shared/Diagnostics/SaydinMetrics.cs:17-34` `WhatIfCalculations`, `CalculationDuration` ve `PriceNotFoundCount` sayaç/histogramlarını tanımlıyor; incelenen production dosyalarında bu üç nesne için `Add`/`Record` çağrısı yok.
- **Etki / tetiklenme:** Dashboard ve alarm bu metrikleri bekliyorsa zaman serileri hiç oluşmaz; hesaplama hacmi, latency ve missing-price artışı gözlenemez. “Tanımlı” olması yanlış operasyon güveni yaratır.
- **Öneri:** Ortak hesaplama wrapper'ında duration/outcome kaydedin; sembol/tier tag'lerini bounded allowlist ile tutun. Kullanılmayacak instrument'ları silin ve dashboard sözleşmesini güncelleyin.
- **Regresyon testi:** `MeterListener` ile başarılı/hatalı WhatIf çağrısında counter ve histogram ölçümlerini, price miss'te ilgili counter'ı assert edin.

### API-24 — OpenAPI hata sözleşmesi gerçek endpoint davranışıyla uyuşmuyor — Low

- **Kanıt:** Tüm asset route'ları device ID istese de `src/Saydin.Api/Endpoints/AssetsEndpoints.cs:45-67` içinde ilk iki route 400'ü beyan etmiyor. Scenario DELETE device ID istediği halde `src/Saydin.Api/Endpoints/ScenariosEndpoints.cs:36-41` 400 beyan etmiyor. Scenario POST `:24-33` 409 beyan ediyor fakat incelenen handler/service/exception zincirinde 409 üreten yol bulunmadı.
- **Etki / tetiklenme:** Üretilen client SDK eksik 400 tipini generic hata sayar ve gerçekte oluşmayan 409 branch'i taşır; API dokümanı güvenilmezleşir.
- **Öneri:** `RequireDeviceId` kullanan her operation'a 400 ProblemDetails metadata'sı ekleyin; 409 gerçekten yoksa kaldırın, concurrency conflict tasarlanırsa somut code/type ile uygulayın.
- **Regresyon testi:** Üretilen OpenAPI JSON'unu snapshot/semantic contract testiyle doğrulayın; runtime'da üretilebilen her status operation responses içinde bulunmalı.

### API-25 — Rate limiter sayısal seçenekleri doğrulanmıyor — Low

- **Kanıt:** `src/Saydin.Api/Program.cs:315-338` config değerlerini doğrudan `FixedWindowRateLimiterOptions` içine geçiriyor; pozitiflik/range validation veya `ValidateOnStart` yok.
- **Etki / tetiklenme:** Production'da `PermitLimit=0/-1` veya `WindowSeconds=0/-1` typo'su startup yerine limiter yaratılırken/ilk istekte exception ya da tüm trafiğin yanlış reddi olarak ortaya çıkabilir.
- **Öneri:** Typed options + DataAnnotations/custom validator ile `PermitLimit > 0`, makul window aralığı ve environment policy uygulayın; `ValidateOnStart` kullanın.
- **Regresyon testi:** Her invalid config varyantı process'i deterministik non-zero startup failure ile durdurmalı; sınırdaki valid değer smoke request'i geçirmeli.

### API-26 — ActivityLog varsayılan zamanı enjekte edilen saat yerine sistem saatini kullanıyor — Low

- **Kanıt:** `src/Saydin.Shared/Entities/ActivityLog.cs:28` property initializer'da `DateTimeOffset.UtcNow` kullanıyor. API için geçerli repo kuralı saat erişimini `TimeProvider` üzerinden yapmayı şart koşuyor; builder explicit zaman sağlamazsa test ve production sistem saatine bağlanır.
- **Etki / tetiklenme:** Gece yarısı/retention/ordering testleri deterministik değildir; uygulamanın diğer saat tabanlı davranışlarıyla aynı fake clock altında ilerlemez.
- **Öneri:** Entity initializer'dan sistem saatini kaldırın; `ActivityLogBuilder` veya factory'ye `TimeProvider` enjekte edip `CreatedAt` değerini zorunlu verin. Shared entity'yi wall-clock sahibi yapmayın.
- **Regresyon testi:** FakeTimeProvider sabitlenmişken oluşturulan activity log'un `CreatedAt` değeri tam fake zamana eşit olmalı.

## İyi tasarım kararları

- Localized RFC 7807 handler zinciri ve ortak `traceId/code` sözleşmesi tutarlı; exception contract testleri tüm handler'ları tarıyor.
- EF Core global `NoTracking` (`src/Saydin.Api/Program.cs:148-157`), delete için explicit tracking ve parametreli interpolated SQL iyi performans/güvenlik tercihleri. Device user create yolu `ON CONFLICT DO NOTHING` ile atomik.
- Repository sahiplik sorguları scenario ID ile user ID'yi birlikte filtreliyor; IDOR'a karşı doğru defense-in-depth uygulanmış.
- Cache key'lerinde invariant decimal, dil ve inflation flag ayrımı çoğunlukla doğru. Redis/cache ve daily-limit arızalarında cancellation korunurken kontrollü fail-open uygulanmış.
- Hesaplamalarda `decimal`, açık rounding, non-positive fiyat guard'ları, culture-independent normalization ve chart nokta sınırı finansal/doğruluk performansı açısından olumlu.
- Activity analytics için ham tutar yerine `AmountBucket`, IP maskeleme, bounded channel ve background batch writer kullanılması doğru yön. Writer'ın shutdown drain, retry metriği ve toxic-row izolasyonu iyi temeller; API-07/API-22 bu mekanizmaların doğruluğunu tamamlamalı.
- `TimeProvider` SavedScenario, kota ve çoğu servis yoluna enjekte edilmiş; localized asset name ve seed/resource sözleşme testleri mevcut.
- 286 birim testin tamamı geçti; testler validation, cache hit/miss, Redis fail-open, localization, rounding, error handler ve pek çok sınır davranışını ayrıntılı kapsıyor.

## Eksik/yetersiz test alanları

Özel bulgulardaki regresyon testlerine ek olarak aşağıdaki kategoriler suite seviyesinde eksik:

- Gerçek PostgreSQL ile `PriceRepository`, `SavedScenarioRepository`, DB constraint/error mapping ve eşzamanlı scenario limit testleri.
- Gerçek HTTP success path'leri, malformed/oversized JSON, authentication/ownership ve farklı kültürlerde tam response sözleşmesi.
- Rate limiter açıkken burst/rotating-device davranışı ve çok-instance/distributed abuse senaryosu.
- Activity channel drop callback metriği, writer transient/toxic ayrımı, shutdown drain ve DB outage yük testi.
- DCA query-count/performance bütçesi, reel nakit akışı matematiği ve `DateOnly` min/max fuzz/property testleri.
- OpenAPI semantic snapshot ve her runtime ProblemDetails code/status'un dokümana yansıması.

## Residual risk

- Bu incelemenin ilk ortamında PostgreSQL/Redis sağlanmadığı için provider davranışı o koşuda dinamik doğrulanamadı. Bununla birlikte kök doğrulamadaki ayrı izole Compose ortamında gerçek PostgreSQL/Redis ile sekiz entegrasyon testinin tamamı geçti. Residual risk, suite'in genel başarısızlığı değil; CI'ın prerequisite eksikliğini skip+exit 0 ile maskeleyebilmesi ve mevcut sekiz testin kapsamadığı repository/concurrency alanlarıdır.
- Microsoft.OpenApi advisory'sinin mevcut server-generated doküman yolunda uzaktan tetiklenebilir olduğu gösterilmedi; risk burada audit/release kapısı ve transitive dependency olarak derecelendirildi.
- WAF, ingress, private network, OTLP backend erişimi/retention ve production secret/config değerleri kapsam dışıydı. Bunlar API-02/API-06/API-15/API-16 etkisini azaltabilir veya büyütebilir; uygulama kodu tek başına korumayı garanti etmiyor.
- Yük/fuzz/soak testi yapılmadı. DCA roundtrip ve büyük JSON etkisi statik akıştan kesin olarak görülse de gerçek eşikler deployment kaynaklarına göre ölçülmelidir.
- Rapor kodu değiştirmez; yalnız bu dosya oluşturulmuştur. Güvenlik audit'ini kapatan build yalnız teşhis içindi ve çözüm olarak önerilmemektedir.
