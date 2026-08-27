# Doğrulanmış Medium Bulgular

> 121 kayıt (52 `defect`, 69 `excellence-gap`).

| Hat | Kapsam | Medium |
|---|---|---:|
| R01 | API güvenlik ve admission yüzeyi | 6 |
| R02 | Installation kimlik yaşam döngüsü + migration 023/024 | 3 |
| R03 | Activity logging ve pseudonymization | 8 |
| R04 | API servis/repository katmanı + Shared | 4 |
| R05 | PriceIngestion worker/repository | 11 |
| R06 | PriceIngestion adapter/mapper | 8 |
| R07 | Migrator + RoleBootstrap + DatabaseSecurity | 2 |
| R08 | DataQualityAudit + DataRepair | 6 |
| R09 | calendar-data ve calendar infrastructure | 5 |
| R10 | infrastructure/backup | 9 |
| R11 | Deployment, Prometheus, Alertmanager, OTEL | 8 |
| R12 | Release supply chain, CI workflow, kapılar | 6 |
| R13 | Saydin.Api test kalitesi | 8 |
| R14a | PriceIngestion + calendar test kalitesi | 8 |
| R14b | Migrator/RoleBootstrap/DQA/DataRepair test kalitesi | 3 |
| R15 | Dokümantasyon, ADR, runbook | 5 |
| R16 | Compose, solution, build konfigürasyonu | 4 |
| R17 | REMEDIATION DENETİMİ | 6 |
| R18 | ÜRÜN VE GELİŞTİRİCİ DENEYİMİ | 11 |

---

### 1. 503 yanıtı Retry-After taşımıyor ve tek `security_limiter_unavailable` kodu kalıcı ile geçici hatayı birleştiriyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | product-ux |
| **Hat** | R01 — API güvenlik ve admission yüzeyi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Security/SecurityAdmissionProblem.cs:16-18,27-36,43-48,77-82; SecurityLimiterDecision.cs:42-43; ApiErrorCodes.cs:33; Resources/ErrorMessages(.en).resx:53-54` |

**Bulgu.** Finder'ın iddiası doğru; küçük bir düzeltme: client_address_untrusted `RedisFailure` değil `SecurityLimiterReason.InvalidSubject` ile üretiliyor — sonuç aynı, çünkü SecurityAdmissionProblem reason'a hiç bakmadan tek 503 zarfı yazıyor ve UnavailableFor RetryAfter'ı zaten sıfırlıyor. Log tarafında 'security_client_address_untrusted' kararlı kodu üretiliyor ama ApiErrorCodes'ta tanımlı olmadığı için istemciye asla ulaşmıyor.

**Etki.** Redis kesintisinde tüm istemciler backoff ipucu olmadan yeniden dener (degrade sistemde retry fırtınası). XFF kirlenmesi/trust drift durumunda etkilenen kullanıcı kalıcı 503 alır ama gövde 'daha sonra tekrar deneyin' dediği için ne istemci ne destek ekibi kalıcı hatayı ayırt edebilir; runbook (api-availability.md:13-19) ayrımı yapabiliyor, istemci yapamıyor.

**Öneri.** (1) Unavailable dalında redis_failure/malformed_reply için bounded + jitter'lı Retry-After yaz. (2) client_address_untrusted için ayrı kararlı kod (`security_client_address_untrusted` — log'da zaten kullanılıyor) ve ayrı type URI tanımla, ApiErrorCodes'a ekle, ErrorMessages.resx/.en.resx'e lokalize metin koy ve bu dalda Retry-After YAZMA (kalıcı hata sinyali). (3) ProducesProblem(429/503) çağrılarını Retry-After başlığını da bildirecek şekilde genişlet.

---

### 2. `saydin_security_admission_decisions_total` üzerinde hiçbir Prometheus kuralı yok

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R01 — API güvenlik ve admission yüzeyi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/prometheus/rules/api.yml:1-46; redis.yml:12-18; docs/decisions/ADR-003-rate-limiting.md:41-42` |

**Bulgu.** ADR-003'ün açıkça istediği 'availability alarmı' toplam Redis kaybı için SaydinRedisUnavailable + SaydinApiErrorBudgetBurn ile karşılanıyor; ADR ihlali değil. Gerçek boşluk daha dar ve yine önemli: admission kararı metriğinin hiçbir kuralı yok, dolayısıyla error-budget eşiğinin (%5) altında kalan KISMİ ve KALICI admission reddi (özellikle normal işleyişte sıfır olması gereken client_address_untrusted serisi) hiç alarm üretmiyor; nöbetçi ancak elle metrik gruplayarak (runbook api-availability.md:13-19) fark edebiliyor.

**Etki.** Reverse-proxy trust yapılandırması kısmen kayarsa veya belirli bir taşıyıcı/VPN grubunun istekleri XFF taşırsa, kullanıcıların bir kısmı süresiz 503 alır ve bu durum hiçbir alarm üretmez. Fail-closed tasarımın maliyeti ölçülebilir ama alarmlanabilir değil.

**Öneri.** api.yml'e iki kural ekle: (a) `sum(rate(saydin_security_admission_decisions_total{outcome="unavailable",reason="client_address_untrusted"}[10m])) > 0` → warning, runbook_url api-availability.md; (b) `...{outcome="unavailable",reason=~"redis_failure|malformed_reply"}[5m] > 0` → critical, runbook_url redis-unavailable.md. Üçüncü olarak `outcome="limited", bucket=~"registration|calculation_network"` oranı için warning (bkz. R01-01). Kuralları infrastructure/prometheus/tests/rules.test.yml'e promtool testiyle birlikte ekle.

---

### 3. Edge katmanı istemci kaynaklı X-Forwarded-For'u temizlemiyor; trust sözleşmesinin edge yarısı sürüm kontrolünde tanımlı değil

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | security |
| **Hat** | R01 — API güvenlik ve admission yüzeyi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/deployment/Caddyfile:14; src/Saydin.Api/Security/DistributedSecurityLimiterMiddleware.cs:70-71; src/Saydin.Api/Runtime/ApiRuntimeContract.cs:59-60; infrastructure/deployment/compose.production.yml:301` |

**Bulgu.** Güvenlik açığı yok — zincir her yönde fail-closed (spoof edilen XFF admission'ı geçemez). Gerçek kusur availability ve doğrulanabilirlik tarafında: public API'nin 'istek kabul edilir mi' davranışı, repo'da hiçbir yerde tanımlanmayan ve sürümü bile görünmeyen (digest env değişkeni) bir Caddy varsayılanına bağlı. Somut 503 sonucu Caddy sürümüne göre değişir ve hiçbir uçtan uca test bunu kilitlemiyor.

**Etki.** Caddy imajı yükseltildiğinde davranış iki yönde de sessizce değişebilir: kalıntı bırakan yönde → meşru kurumsal proxy/VPN arkasındaki kullanıcılar kalıcı 503 (üstelik R01-02 nedeniyle sebebi yanıttan anlaşılamaz); ezen yönde → her şey çalışır ama trust sözleşmesi hiç sınanmamış kalır. Regresyon ancak üretimde fark edilir.

**Öneri.** Caddyfile'da güveni açık yaz: `reverse_proxy saydin-api:8080 { header_up X-Forwarded-For {remote_host} }` (append değil replace) ve gerekiyorsa global `servers { trusted_proxies static private_ranges }`. Ardından sözleşmeyi gerçek Caddy + API ile bir kabul testine bağla (.github/compose.integration.yml): istemci `X-Forwarded-For: 1.2.3.4` ve `1.2.3.4, 5.6.7.8` gönderdiğinde yanıt 200 olmalı ve limiter gerçek istemci IP'sini saymalı. Caddy digest'ini validate-production.py'nin görebileceği şekilde sürüm kontrolüne al.

---

### 4. Endpoint yüzey metadata'sı ile gerçek endpoint kümesi arasında otomatik değişmez yok

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | architecture-rule |
| **Hat** | R01 — API güvenlik ve admission yüzeyi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Runtime/ApiEndpointSurface.cs:28-41; src/Saydin.Api/Middleware/ApiPortBoundaryMiddleware.cs:56,78-80; src/Saydin.Api/Program.cs:360-379; tests/Saydin.Api.Tests/Middleware/ApiManagementBoundaryHttpTests.cs:33-55` |

**Bulgu.** Doğru. Her iki katman da metadata YOKLUĞUNU fail-open ele alıyor; bu diff'in ana savunma kazanımı (port yüzeyi ayrımı) tamamen elle disipline ve `productEndpoints` grubunu kullanma alışkanlığına bağlı. Hiçbir derleme/test aşaması bunu doğrulamıyor.

**Etki.** Gelecekte `app.MapPost("/admin/...")` gibi bir operatör endpoint'i doğrudan `app` üzerine (grup dışına) map edilirse, hem selector policy hem boundary middleware onu PublicProduct sayar ve Caddy'nin @internal regexp'i yalnız metrics/health-ready/openapi/scalar'ı kapattığı için endpoint public internetten erişilebilir olur. Regresyon ancak üretimde fark edilir.

**Öneri.** Saydin.Api.Tests'e gerçek uygulamayı ayağa kaldıran bir contract testi ekle (WebApplicationFactory + EndpointDataSource.Endpoints): (a) her RouteEndpoint ApiEndpointSurfaceMetadata taşımalı, (b) Surface==Management olanların NormalizePath sonrası route pattern kümesi tam olarak {ReadyPath, MetricsPath}, PublicLiveness olanlarınki {LivePath} olmalı. Ayrıca ApiPortEndpointSelectorPolicy.ApplyAsync'te metadata'sız adayı — en azından Production'da — geçersiz say (fail-closed varsayılan).

---

### 5. Admission'da reddedilen her istek yine de bir activity_logs satırına mal oluyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | performance |
| **Hat** | R01 — API güvenlik ve admission yüzeyi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Program.cs:346-350; src/Saydin.Api/Middleware/ActivityLogMiddleware.cs:34-68; tests/Saydin.Api.Tests/Middleware/ActivityLogMiddlewareTests.cs:17-48; infrastructure/prometheus/rules/api.yml:40-46` |

**Bulgu.** Ağ seviyesindeki limiter 429/503'leri ActivityLogMiddleware'in İÇİNDE üretildiği için her reddedilen istek kalıcı bir activity_logs satırı yazıyor; bu, saldırgan-kontrollü ve limitle sınırlanmayan bir yazma yolu (60/dk limitine takılan 10.000 istek yine 10.000 satır üretir). Filtre seviyesindeki admission denetimi kasıtlı ve testle kilitli olduğu için KORUNMALI; yalnız en dıştaki ağ limiter'ı ActivityLog'un önüne alınabilir.

**Etki.** Limiter'ın koruması gereken kalıcı depolama yolu korunmuyor: flood sırasında bounded channel (10k, DropWrite) taşarsa SaydinActivityLogLoss critical alarmı (api.yml:40-46) ateşlenir ve nöbetçi kök neden yerine semptom için sayfa alır; aynı pencerede meşru trafiğin denetim izi de kaybolur.

**Öneri.** `UseWhen(... DistributedSecurityLimiterMiddleware)` çağrısını `UseMiddleware<ActivityLogMiddleware>()`'ten ÖNCEYE al. Filtre seviyesindeki admission (endpoint filter) ActivityLog'un içinde kalır, dolayısıyla ProductFailuresBeforeHandler_AreAuditedWithStableOutcome ve denetim sözleşmesi bozulmaz; ağ seviyesi reddi ise yalnız SecurityAdmissionDecisions sayacına yazılır. Değişikliği 'ağ limiter reddi activity_logs satırı üretmez' assertion'ı taşıyan bir testle kilitle.

---

### 6. Rate-limit ayar sabitleri üç yerde tekrarlanıyor ve release kapısı tam-eşitlikle kilitliyor — operasyonel yeniden kalibrasyon yolu yok

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R01 — API güvenlik ve admission yüzeyi |
| **Doğrulama** | CONFIRMED (doğrulayıcı ek bulgusu) |
| **Konum** | `infrastructure/deployment/validate-production.py:222-232; infrastructure/deployment/compose.production.yml:194-200; src/Saydin.Api/appsettings.json:31-44` |

**Bulgu.** validate-production.py:224-231 `security_limits` sözlüğünde "3"/"5"/"20"/"100"/"500" değerlerini string olarak tam eşitlikle karşılaştırıyor: `if any(api_env.get(key) != expected ...): reject(errors, "security_limiter_production_limits_invalid")`. Aynı değerler compose.production.yml:196-200 ve appsettings.json:37-41'de de yazılı. Sınır kontrolü değil birebir sabit eşitliği yapılıyor. Buna karşılık ExactIpLimit/NetworkLimit/PrincipalLimit/WindowSeconds compose'da hiç pinlenmiyor ve validator'da hiç kontrol edilmiyor — asimetrik.

**Etki.** Gerçek trafikle kalibre edilmesi gereken tavanlar bir CI kapısında magic literal olarak donduruldu; üretimde yaygın 429 gözlemlendiğinde tepki süresi bir release döngüsü kadar. Ayrıca aynı sabitin üç kopyası sessizce sapabilir (appsettings.json ile compose farklı olursa yalnız compose geçerlidir, validator appsettings'i hiç görmez).

**Öneri.** Validator'ı tam-eşitlikten bounded-range + tutarlılık kontrolüne çevir (exactHourly ≤ exactDaily ≤ networkDaily vb. — DistributedSecurityLimiterOptions.HasValidShape bu değişmezleri zaten kodluyor, aynı kuralı tek kaynaktan türet). Böylece ayar değişikliği imzalı manifest üzerinden yapılabilir ama güvenlik değişmezleri korunur. ExactIpLimit/NetworkLimit/PrincipalLimit/WindowSeconds'ı da üretim env'ine taşıyıp aynı aralık kontrolüne bağla.

---

### 7. Keyring rehash yalnız kullanım anında; eski key sürümünü düşürme kararı için ne runbook ne telemetri var

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R02 — Installation kimlik yaşam döngüsü + migration 023/024 |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/postgres/migrations/024_installation_credential_rehash.sql:88-152; src/Saydin.Api/Services/InstallationCredentialKeyring.cs:37,120-125; infrastructure/postgres/migrations/021_api_trust_expand.sql:220-225,500-510; infrastructure/prometheus/rules/api.yml` |

**Bulgu.** Verifier rehash mekanizması eklendi ama operatörün 'eski key sürümü artık düşürülebilir' diyebilmesi için gereken hiçbir şey yok: ne rotasyon runbook'u, ne 'aktif ama eski key sürümündeki credential sayısı' metriği/alarmı, ne toplu backfill, ne de credential'lar için bir expires_at. Rotasyon ayrıca tek yönlü (hem keyring hem 024 downgrade'i reddeder) ve orphan kalan credential revoke bile edilemez — tek çıkış yeniden kayıttır, ki bu R02-02'deki registration cap'ine çarpar ve kayıtlı senaryoları kalıcı kaybettirir.

**Etki.** Rotasyon penceresinde uygulamayı açmamış kurulumlar (tatil, ikinci cihaz, düşük etkileşim) key düşürüldüğünde sessizce ve geri dönüşsüz kimliğini kaybeder. Operatörün karar için ölçütü yok; hatanın büyüklüğü ancak destek trafiğinden anlaşılır.

**Öneri.** (1) `docs/runbooks/installation-keyring-rotation.md` ekle: ekleme → aktifleştirme → drain → düşürme adımları ve her adımın kapı sorgusu. (2) Kapı sorgusunu ölçülebilir yap: audit/owner kimliğiyle `SELECT hash_key_version,count(*) FROM installation_credentials WHERE state='active' GROUP BY 1` sonucunu gauge olarak dışa ver; SaydinBackupLoginExpiring desenine benzer bir alarm kur ve sayı sıfırlanmadan key düşürmeyi deploy kapısıyla engelle. (3) Uzun kuyruğu kes: idle credential'lara expires_at uygula veya aynı SECURITY DEFINER fonksiyonunu owner kimliğiyle batch çağıran tek seferlik control-plane rehash job'ı ekle (pending credential'ların rehash edilmediğini de hesaba kat). (4) Rotasyonun tek yönlü olduğunu ADR-010'a yaz.

---

### 8. 023/024 ve yeni admission bucket'ları sahibi olan ADR'lerde yok; remediation raporu 'kusur kalmadı' diyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | documentation |
| **Hat** | R02 — Installation kimlik yaşam döngüsü + migration 023/024 |
| **Doğrulama** | CONFIRMED |
| **Konum** | `docs/decisions/ADR-010-installation-principal.md:3,43-58; docs/decisions/ADR-003-rate-limiting.md:19-31; docs/architecture.md; docs/high-traffic-checklist.md; docs/analysis/pr-review/07-remediation-progress.md:15` |

**Bulgu.** 023/024'ün kararları ve dört yeni admission bucket'ı yalnız kodda ve migration SQL'inde yaşıyor; sahibi olan ADR-010 ve ADR-003 ile architecture.md/high-traffic-checklist.md güncellenmemiş, buna karşın remediation raporu tüm doküman kusurlarının kapandığını iddia ediyor.

**Etki.** 'Neden 5/gün', 'neden /24', 'neden lazy rehash', 'rotasyon neden geri alınamaz' sorularının cevabı hiçbir yerde yazılı olmadığı için ikinci bir okuyucu limitleri veya key rotation planını güvenle değiştiremez; kapanış iddiası bir sonraki reviewer'ı da yanıltır.

**Öneri.** ADR-010'u 023/024 ile güncelle (durum satırı, immutable SHA'lar, pending-commit admission, rehash'in tek yönlülüğü). ADR-003'e registration ve calculation-network bucket'larını, subject seçimini (exact IP vs /24) ve CGNAT varsayımını ekle. architecture.md ve high-traffic-checklist.md'deki bucket listelerini beş bucket'a genişlet. 07-remediation-progress.md'nin kapanış iddiasını bu hattaki açık kalemlerle düzelt.

---

### 9. Registration kotası handler başarısız olsa bile tüketiliyor: geçici bir 5xx, kullanıcının saatlik/günlük kayıt bütçesini kalıcı olarak yakıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R02 — Installation kimlik yaşam döngüsü + migration 023/024 |
| **Doğrulama** | CONFIRMED (doğrulayıcı ek bulgusu) |
| **Konum** | `src/Saydin.Api/Endpoints/EndpointExtensions.cs:98-115; src/Saydin.Api/Security/DistributedSecurityLimiter.cs:62-74,126-164; src/Saydin.Api/Endpoints/InstallationEndpoints.cs:43-68` |

**Bulgu.** `RequireRegistrationAdmission` filtresi `TryAcquireRegistrationAsync`'i `next(ctx)` çağrılmadan ÖNCE çalıştırıyor (EndpointExtensions.cs:108-114). Lua betiği izin verdiği anda dört bucket'ın da sayacını koşulsuz artırıyor (`redis.call('HSET', KEYS[i],'window',window,'count',count+1)`, DistributedSecurityLimiter.cs:62-73) — handler'ın sonucuna bakan bir telafi/decrement yolu yok. `RegisterAsync` (InstallationEndpoints.cs:43-68) hiçbir hata yakalamıyor: `repository.RegisterAsync` bir PostgresException fırlatırsa istek GlobalExceptionHandler üzerinden 500 döner, ama registration sayaçları çoktan artmıştır. Aynı durum başarılı bir 201 yanıtının ağda kaybolduğu ve istemcinin yeniden denediği senaryoda da geçerlidir. Varsayılanlar bunu keskinleştiriyor: RegistrationExactHourlyLimit=3, RegistrationExactDailyLimit=5 (appsettings.json:37-38). Karşılaştırma noktası: aynı repo ürün kotasında tam da bu sorunu çözen bir lease/release deseni kullanıyor (ADR-003:26-31, `QuotaLease` — acquire, sonra sonuca göre release).

**Etki.** Kısa bir veritabanı kesintisi, etkilenen IP'ler için 24 saate kadar süren kalıcı bir kurulum kilidine dönüşüyor; kullanıcı hiçbir başarılı kayıt yapmadan bütçesini kaybediyor ve generic `security_rate_limited` mesajı nedeni açıklamıyor. R02-02'deki paylaşılan-NAT riskiyle birleşince tek bir arıza penceresi tüm bir /24'ü gün sonuna kadar kilitleyebilir.

**Öneri.** Registration sayacını yalnız gerçekten yaratılan principal için tüket: (a) admission'ı başarılı INSERT sonrasına taşı, ya da (b) ürün kotasında zaten kullanılan lease/release desenini registration bucket'ına uygula ve handler 2xx dışında bittiğinde lease'i serbest bırak. Ayrıca `RegisterAsync`'te DB hatasını yakalayıp lokalize bir 503 üret ve bunu bucket'a göre etiketli bir metrikle işaretle. Regresyon kilidi: repository'yi hata fırlatacak şekilde sahteleyip 500 sonrası sayacın artmadığını doğrulayan bir test.

---

### 10. Shutdown'da iptal edilen batch hem 'kayıp' sayaçlanıyor hem de drain tarafından başarıyla yazılıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R03 — Activity logging ve pseudonymization |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/BackgroundServices/ActivityLogWriter.cs:45-55,112-122,194-211; infrastructure/prometheus/rules/api.yml:40-47` |

**Bulgu.** Kapanışta iptal edilen bir flush, satırları `outcome="cancelled"` olarak write_failures sayacına ekler; buffer temizlenmediği için aynı satırlar DrainRemainingAsync tarafından yeni bir CTS ile tekrar yazılır ve tipik olarak başarılı olur. Sayaç 'kayıp' anlatısını taşıdığı hâlde kayıp yoktur. Sinyalin gerçekten alarm üretip üretmemesi export yoluna bağlıdır (Kestrel önce durduğu için scrape değil, OTLP shutdown flush'ı belirleyicidir) — ancak metric semantiği her hâlükârda yanlıştır.

**Etki.** Denetim izi kaybı sinyali gerçeği yansıtmıyor: kayıp olmadığı hâlde critical alarm üretilebiliyor, buna karşılık operatör gerçek kayıpla sahte kaybı ayırt edemiyor. docs/runbooks/api-errors.md:15 'tüm activity-loss sayaçları düz kalmalı' dediği için her deploy sonrası gereksiz bekleme doğuyor.

**Öneri.** Cancelled dalında write_failures'ı artırma; kaybı yalnız gerçekten düşürülen satırda (drain timeout, retry_exhausted, toxic_row, kurtarılamayan fatal) say. Ayrım gerekiyorsa `outcome="cancelled_retried"` gibi ayrı bir etiket kullanıp SaydinActivityLogLoss regex'inden çıkar; kurala kısa bir `for:` ekle ve inventory.test.yml'e 'shutdown'da yeniden yazılan batch alarm üretmez' negatif senaryosunu koy.

---

### 11. Writer fault olduktan sonra kanala yazılan satırlar hiçbir sayaca düşmeden yok oluyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R03 — Activity logging ve pseudonymization |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/BackgroundServices/ActivityLogWriter.cs:29-56,137-141; src/Saydin.Api/BackgroundServices/ActivityLogChannelLifetime.cs:16-20; src/Saydin.Api/Services/ChannelActivityLogger.cs:17-20; src/Saydin.Api/Program.cs:291-305` |

**Bulgu.** Writer ExecuteAsync fault verdiğinde channel complete edilmiyor ve kapasite boş kalıyor; fault anı ile ActivityLogChannelLifetime.StopAsync arasında üretilen tüm activity log satırları (kuyrukta bekleyenler dâhil) okuyucusu olmayan kanala yazılıp kaybediliyor. Bu kayıp için ne queue_drops ne queue_rejected_writes ne de write_failures artıyor; tek iz Serilog LogCritical satırıdır.

**Etki.** ADR-006 denetim izi sessizce delinir: 'kaybolan bir activity log satırı nasıl fark edilir?' sorusunun cevabı bu senaryoda 'fark edilmez'dir. SaydinActivityLogLoss yalnız fatal batch'in kendi satır sayısı kadar (çoğu zaman ≤50) artar, gerçek kayıp bunun kat kat üstünde olabilir.

**Öneri.** ExecuteAsync'e finally ekleyip fault hâlinde `channel.Writer.TryComplete(ex)` çağır → sonraki yazımlar `queue_rejected_writes{reason="writer_dead"}` olarak sayılsın; ayrıca fault anındaki `channel.Reader.Count` kadar satırı `outcome="writer_dead"` ile write_failures'a ekle. Kapanışta metric flush'ının garanti edildiğini (OTLP shutdown flush) doğrulayan bir test/oyun günü adımı ekle veya kaybı ayrıca structured log alanı olarak görünür kıl.

---

### 12. 30 sn'lik shutdown drain bütçesi host'un 30 sn'lik varsayılan ShutdownTimeout'una sığmıyor; kesilen drain iz bırakmıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R03 — Activity logging ve pseudonymization |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/BackgroundServices/ActivityLogWriter.cs:13,65-98; src/Saydin.Api/Program.cs (HostOptions.ShutdownTimeout ayarlanmamış); infrastructure/deployment/compose.production.yml:184` |

**Bulgu.** Drain bütçesi (30 sn) host'un varsayılan ShutdownTimeout'una (30 sn) eşittir ve drain CTS'i host token'ına bağlı değildir; Kestrel drain'i bu bütçenin bir kısmını yediği için drain hiçbir zaman tam 30 sn alamaz. Host süresi dolduğunda süreç, drain hâlâ çalışırken sonlanır ve 85-89'daki timeout uyarısı yazılmadığı için kayıp tamamen sessiz olur.

**Etki.** ActivityLogChannelLifetime ile kurulan ve activity-logging.md §4.3'te 'bounded biçimde yazılır' diye belgelenen drain garantisi gerçekleşmez; docs/runbooks/container-restart.md:8'deki 'activity writer drain completed within stop grace' doğrulama adımı kanıtsız kalır.

**Öneri.** `builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(45))` ekle (compose stop_grace_period 60 sn'nin altında) ve ShutdownDrainTimeout'u bunun belirgin altında tut (ör. 20 sn). Drain CTS'ini host token'ıyla CreateLinkedTokenSource yapıp iptal hâlinde kalan satır sayısını `outcome="shutdown_abandoned"` ile sayaçla. Bu bütçe ilişkisini activity-logging.md §4.3'e yaz.

---

### 13. Günlük kota Redis anahtarları ham principal/user GUID'i taşıyor — bu diff'te EKLENEN CLAUDE.md pseudonym kuralını ihlal ediyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | security |
| **Hat** | R03 — Activity logging ve pseudonymization |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Services/DailyLimitGuard.cs:244-258; src/Saydin.Api/Endpoints/AssetsEndpoints.cs:86,117,171; src/Saydin.Api/Services/WhatIfCalculator.cs:46,87,177; src/Saydin.Api/Services/DcaCalculator.cs:49; CLAUDE.md (bu diff'te eklenen pseudonym kuralı); docs/cache-strategy.md:163-164` |

**Bulgu.** DailyLimitGuard'ın Redis kota anahtarları hem ham installation principal GUID'ini (deviceId yolu) hem de ham users.id GUID'ini (user yolu) düz metin olarak taşır. Aynı process'te HMAC pseudonym üreten iki otorite (SecurityLimiterPseudonymizer, ActivityPrincipalPseudonymizer) bulunmasına rağmen kullanılmaz. Bu diff CLAUDE.md'ye ham principal kullanımını yasaklayan bir kural ekler ama kodu ve cache-strategy.md'yi buna uyumlulaştırmaz.

**Etki.** Redis dump'ı, snapshot yedeği veya SCAN erişimi olan bir yan bileşen 48 saatlik pencerede tüm aktif principal/user id'lerini ve günlük kullanım profillerini okuyabilir. Ayrıca yeni eklenen CLAUDE.md kuralı ilk günden ihlal durumundadır — sözleşme metni ile kod arasında kalıcı drift.

**Öneri.** Kota subject'ini purpose-separated bir HMAC ile türet (ör. domain string 'saydin.quota.subject.v1'), BuildUsageKey'i ham GUID kabul etmeyecek şekilde daralt (prefix/şekil doğrulaması) ve testle kilitle. Kural bilinçli gevşetilecekse istisnayı cache-strategy.md'de değil CLAUDE.md'de açıkça yaz — iki metin aynı anda doğru olamaz.

---

### 14. Action allowlist üç yerde tekrarlanıyor, aralarında parite kapısı yok; EF modeli DB'de artık var olmayan bir CHECK'i tarif ediyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | developer-experience |
| **Hat** | R03 — Activity logging ve pseudonymization |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Shared/Constants/ActivityActions.cs:33-48; src/Saydin.Shared/Data/Configurations/ActivityLogConfiguration.cs:12-22; infrastructure/postgres/migrations/023_installation_lifecycle_admission.sql:157-162,278; tests/Saydin.DatabaseMigrator.Tests/MigrationRunnerIntegrationTests.cs:1758-1790` |

**Bulgu.** Action allowlist artık üç bağımsız yerde tekrarlanıyor (C# sabiti, 023'teki plpgsql trigger dizisi, EF'in artık var olmayan chk_activity_action modeli) ve hiçbir otomatik parite kapısı yok. EF'teki HasCheckConstraint runtime'ı etkilemez (EF migration kullanılmıyor), dolayısıyla doğrudan bir hata değil; asıl kusur, 'ActivityActions.All ile birebir aynı kalmalı' yorumunun hiçbir mekanizmayla desteklenmemesi ve modelin gerçek şemayla çelişmesidir.

**Etki.** Bir geliştirici ActivityActions'a 16. action ekleyip trigger'ı güncellemeyi unutursa derleme ve unit testler geçer; üretimde o action'ın her satırı 23514 alır, ToxicRow olarak bisection'a girer (50'lik batch için ~2N ek DB round-trip'i), satır satır düşürülür ve yalnız `toxic_row` sayacı + critical alarm ile geriye dönük fark edilir. Denetim izinde o özellik hiç görünmez.

**Öneri.** Gerçek-PG integration testinde `ActivityActions.All`'ı döngüyle INSERT edip hepsinin kabul edildiğini, listede olmayan bir değerin reddedildiğini doğrula (literal listeler yerine sabitten türet). ActivityLogConfiguration'daki chk_activity_action modelini kaldır veya 'artık trigger ile enforce ediliyor' yorumuyla açıkla; chk_activity_data_size predicate'indeki 10000 literalini ActivityLogLimits.DataMaxBytes'tan üret.

---

### 15. Pseudonym anahtarının sürüm/rotasyon hikâyesi yok: değer anahtar sürümü taşımıyor, runbook ve dual-key kabulü yok

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R03 — Activity logging ve pseudonymization |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Services/ActivityPrincipalPseudonymizer.cs:20-31,59-75; src/Saydin.Api/Options/ActivityPrincipalPseudonymOptions.cs:9-10; docs/runbooks/ (activity-principal-hmac hiç geçmiyor)` |

**Bulgu.** Pseudonym anahtarı tek dosyalı, sürümsüz ve rotasyon prosedürsüzdür; üretilen `p1:` öneki yalnız şema sürümünü kodlar, anahtar sürümünü değil. Aynı repoda installation credential'ları için sürümlü keyring + dual-key kabul + rotasyon runbook'u varken bu materyal için hiçbiri yoktur.

**Etki.** Anahtar sızar veya bir ortam yeniden materyalize edilirken dosya yeniden üretilirse aynı principal öncesi/sonrası iki farklı pseudonym üretir; denetim izi sessizce ikiye bölünür — M-KEYRING remediation'ının tam olarak önlemek istediği durum. KVKK açısından pseudonymization anahtarının yaşam döngüsü ve kompromis prosedürü belgesizdir.

**Öneri.** Pseudonym'e anahtar sürümünü göm (`p1.2:<hex>`) veya keyring dosyası + ActiveKeyVersion modeline geç; docs/runbooks/activity-pseudonym-key-rotation.md ekleyip 'eski satırlar yeniden hesaplanmaz, kesme tarihi şu şekilde kaydedilir' adımını yaz; ADR-006/activity-logging.md KVKK bölümüne anahtar sahipliği ve saklama sınırını ekle.

---

### 16. Kanal yaşam döngüsünün tüm garantisi test edilmeyen bir DI kayıt sırası varsayımına dayanıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | test-quality |
| **Hat** | R03 — Activity logging ve pseudonymization |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Program.cs:301-305; src/Saydin.Api/BackgroundServices/ActivityLogChannelLifetime.cs:6-12; tests/Saydin.Api.Tests/BackgroundServices/ActivityLogWriterTests.cs:125-141` |

**Bulgu.** ActivityLogChannelLifetime'ın sağladığı 'ingress önce kapanır, writer sonra durur' garantisi yalnızca DI kayıt sırası + Host'un ters durdurma semantiği + Kestrel'in en sonda kaydedilmesi varsayımlarına dayanıyor ve hiçbir test bu üç varsayımdan hiçbirini doğrulamıyor. Var olan test bileşenleri elle doğru sırayla sürüyor, yani varsayımı test etmek yerine yeniden üretiyor.

**Etki.** Program.cs'de AddHostedService<ActivityLogChannelLifetime>() çağrısından sonra yeni bir hosted service eklenirse veya .NET sürümü sırayı değiştirirse drain garantisi sessizce bozulur: writer, ingress kapanmadan durdurulur ve kapanışta üretilen satırlar (tam da bu değişikliğin çözdüğü sorun) yeniden kaybolmaya başlar; hiçbir test kırılmaz.

**Öneri.** WebApplication'ı ayağa kaldırıp `services.GetServices<IHostedService>()` sırasında ActivityLogWriter'ın ActivityLogChannelLifetime'dan önce ve GenericWebHostService'in en sonda olduğunu doğrulayan bir sözleşme testi ekle; Program.cs'e yorum düşmek yerine kapı kur.

---

### 17. Commit ACK'i kaybolan batch retry'da 23505 alıp ToxicRow'a düşüyor: zaten kalıcı olan satırlar 'kayıp' olarak sayılıp critical alarm üretiyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | data-integrity |
| **Hat** | R03 — Activity logging ve pseudonymization |
| **Doğrulama** | CONFIRMED (doğrulayıcı ek bulgusu) |
| **Konum** | `src/Saydin.Api/BackgroundServices/ActivityLogBatchStore.cs:17-23,47-49; src/Saydin.Api/BackgroundServices/ActivityLogWriter.cs:126-131,163-186; src/Saydin.Shared/Entities/ActivityLog.cs:8,28` |

**Bulgu.** ActivityLog.Id istemci tarafında `Guid.CreateVersion7()` ile üretiliyor (ActivityLog.cs:8) ve PK (Id, CreatedAt) — yani retry'da AYNI anahtar tekrar INSERT edilir. EfActivityLogBatchStore.SaveAsync düz `AddRangeAsync` + `SaveChangesAsync` kullanıyor, ON CONFLICT/idempotency yok. Classifier 23xxx'i ToxicRow sayıyor (ActivityLogBatchStore.cs:47-49). Transient retry yolu (08xxx) tam da bağlantı kopmasına karşı tasarlandığı için, transaction commit edip ACK kaybolduğunda ikinci deneme 23505 unique_violation alır → ToxicRow → BisectAndFlushAsync satır satır böler → her satır tek başına yine 23505 alır → HandleToxicAsync `ReportFailure(1, toxic_row)` ile düşürülmüş sayılır. Mevcut integration testi (tests/Saydin.Api.IntegrationTests/ActivityLogWriterIntegrationTests.cs:166-176) ilk çağrıda hiç INSERT yapmadan bağlantıyı öldürdüğü için bu pencereyi hiç kapsamıyor.

**Etki.** Aslında veritabanında bulunan 50 satır 'toxic_row' olarak sayaçlanır; SaydinActivityLogLoss (for: yok, increase>0) critical firing yapar ve operatör gerçekte olmayan bir denetim izi kaybını kovalar. Ayrıca bisection 50 satır için ~99 ek DB round-trip'i harcar.

**Öneri.** EfActivityLogBatchStore'u idempotent yap: `ON CONFLICT (id, created_at) DO NOTHING` ile parametreli toplu INSERT kullan (CLAUDE.md'nin ExecuteSqlInterpolatedAsync UPSERT deseni), ya da en azından 23505'i ToxicRow yerine 'already_persisted' olarak ayrı sınıflandırıp write_failures'a yazma. Gerçek-PG testine 'commit sonrası retry duplicate üretmez ve kayıp sayaçlamaz' senaryosunu ekle.

---

### 18. DCA reel getirisi her ayın ilk günlerinde kalıcı null'a düşüyor: terminal LKV kademesi ara aylara uygulanmıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | correctness |
| **Hat** | R04 — API servis/repository katmanı + Shared |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Services/DcaCalculator.cs:300-316, 364-368, 415-422; src/Saydin.Api/Repositories/InflationRepository.cs:44-61` |

**Bulgu.** Exact-CPI zorunluluğunun eşiği terminal fiyat gününün ayına (`requestedTerminalMonth`) bağlı; fiilen kullanılan deflatör ayına (`terminalObservation.PeriodDate`) bağlı değil. LKV ayı ile terminal ay arasında kalan her katkı ayı exact-only sözleşmesine tabi kaldığı için, TÜİK yayın gecikmesi 1 aydan fazla olduğunda (ör. 1–3 Ağustos'ta en son final CPI Haziran, terminal fiyat günü Ağustos) Temmuz katkısı için exact CPI aranıyor, bulunamıyor ve `RealProfitLossPercent/Try`, `InflationAdjustedInvestedTry`, `CumulativeInflationPercent` null dönüyor. Aynı pencerede WhatIf saf LKV kullandığı için reel getiri döndürmeye devam ediyor.

**Etki.** Her ayın ilk ~3 gününde (TÜİK bir önceki ayın TÜFE'sini ayın ~3'ünde yayınlar) günlük fiyatı olan varlıklarda DCA reel getiri özelliği varsayılan yolda kapalı kalıyor. Sonuç yanlış değil ama eksik; ayrıca aynı pencerede sonuç cache'lenmediği için her istek bulk fiyat sorgusunu ve iki CPI sorgusunu yeniden çalıştırıyor. Önceki review'in ilgili bulgusu tam kapanmamış.

**Öneri.** Önce `terminalObservation = GetLatestFinalIndexValueAsync(requestedTerminalMonth)` çağır, exact eşiğini `month < terminalObservation.PeriodDate` yap ve `month >= terminalObservation.PeriodDate` olan tüm katkılar için `terminalIndex` kullan (bugün yalnız `month == requestedTerminalMonth` için yapılıyor). `FakeTimeProvider` ile 'terminal Ağustos, LKV Haziran, Temmuz katkısı var' senaryosunu kilitleyen bir unit test ekle.

---

### 19. 6 haneli birim yuvarlaması kullanıcının girdiği tutarı sessizce eksiltiyor ve test bu davranışı sabitliyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | financial |
| **Hat** | R04 — API servis/repository katmanı + Shared |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Services/WhatIfCalculator.cs:472-475; src/Saydin.Api/Services/DcaCalculator.cs:211-218; tests/Saydin.Api.Tests/Services/DcaCalculatorTests.cs:156-170` |

**Bulgu.** Aynen iddia edildiği gibi. Rounding hatasının üst sınırı `0.5e-6 * buyPrice`; 3.000.000 TL birim fiyatlı BTC + 100 TL istek örneğinde 100/3.000.000 = 0,0000333… → 0,000033 → yatırılan 99,00 TL, yani %1 sapma. Yön tutarlılığı (ileri/geri hesap) düzelmiş ama sermaye eksiltmesi ve bunun istemciye açıklanmaması kapatılmamış.

**Etki.** Yüksek birim fiyatlı varlık + küçük tutar kombinasyonunda kullanıcının sorduğu sorunun cevabı %1'e kadar farklı bir sermaye üzerinden veriliyor; DCA'da hata her periyotta tekrarlanıp `AverageCostPerUnit` ve reel getiri zincirine taşınıyor. Uyarı, ek alan veya minimum-tutar kapısı yok.

**Öneri.** Para matematiğini ham (yuvarlanmamış) birim üzerinden yürütüp 6 haneli yuvarlamayı yalnız `UnitsAcquired` display alanına uygula; ya da birim hassasiyetini asset kategorisine bağla. Her iki durumda response'a `RequestedAmountTry`/`UninvestedRemainderTry` ekle. Testi formülü tekrar etmek yerine `InitialValueTry == Round(UnitsAcquired * BuyPrice, 2)` ve `|Amount - InitialValueTry| <= BuyPrice * 5e-7` değişmezleriyle assert et.

---

### 20. Yeni gelecek-tarih reddi UTC 'bugün'e dayandığı için Türkiye saatiyle 00:00–03:00 arasında bugünün tarihi 400 ile reddediliyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | product-ux |
| **Hat** | R04 — API servis/repository katmanı + Shared |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Services/WhatIfCalculator.cs:622-630 (+ çağrılar 50, 95, 181, 228, 416); src/Saydin.Api/Services/DcaCalculator.cs:78-86, 118-122` |

**Bulgu.** Aynen iddia edildiği gibi. Türkiye UTC+3 olduğu için her gün 00:00–03:00 İstanbul arası UTC takvim günü bir gün geride; cihazın 'bugün' olarak sunduğu tarihi `sellDate`/`endDate` gönderen istek `ValidationException` → 400 alıyor. Etki `/calculate`, `/compare`, `/reverse` ve `/dca` endpoint'lerinin tamamını kapsıyor.

**Etki.** Hedef pazarın yerel gecesinde üç saatlik bir pencerede geçerli bir tarih reddediliyor; hata mesajı ('Hesaplama bitiş tarihi gelecekte olamaz') kullanıcının takviminde geçmiş bir gün için anlamsız. Bu, diff'ten önce çalışan bir istek şekli için regresyon.

**Öneri.** Sınırı `Europe/Istanbul` yerel gününe çevir (`TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), istanbul)`) veya `value > utcToday.AddDays(1)` ile bir günlük timezone toleransı bırak. Seçilen konvansiyonu `docs/architecture.md`'de belgele ve `FakeTimeProvider` ile 'UTC 22:00 → İstanbul ertesi gün' senaryosunu kilitleyen bir test ekle.

---

### 21. DCA yanıt sözleşmesi istemci için mutabakat kurulamaz: TotalPurchases ↔ SkippedPurchaseDates ↔ PeriodicAmount uyuşmuyor, opsiyonel alanların null semantiği belgesiz

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | product-ux |
| **Hat** | R04 — API servis/repository katmanı + Shared |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Models/Responses/DcaResponse.cs:18-50; src/Saydin.Api/Services/DcaCalculator.cs:226-236, 294, 351-356, 388-411` |

**Bulgu.** Aynen iddia edildiği gibi. Ek olarak `RealReturnMethod`'un dolu kalması bir test tarafından da sabitlenmiş durumda (DcaCalculatorTests.cs:734-765), yani sözleşme kusuru kasıtlı olarak kilitlenmiş.

**Etki.** Flutter istemcisi planlanan alım sayısını hiçbir alandan türetemez, `PeriodicAmount * TotalPurchases` ile `TotalInvestedTry`'ı bağdaştıramaz ve dolu `RealReturnMethod`'a bakıp reel getiri beklerken null alanlarla karşılaşır. WhatIf ve DCA ekranları aynı kavram için farklı null semantiği taşıyor.

**Öneri.** `PlannedPurchases` ve `RequestedInvestedTry` alanlarını ekle; `SkippedPurchaseDates`'i non-nullable `IReadOnlyList<DateOnly>` yap (boş dizi = atlanan yok); `RealReturnMethod`'u yalnız reel hesap tamamlandığında doldur veya ayrı bir 'unavailable' değeri kullan; `InflationDataAsOf`'u deprecate edip tek `InflationTerminalMonth` üzerinden yürü. Meta repo `docs/architecture/api-contract.md`'yi null/boş semantiğiyle güncelle.

---

### 22. Lease yenilemesindeki tek geçici DB hatası tüm ingestion sürecini düşürüyor; süreç kendi bayat lease'ini 30 dk beklemek zorunda kalıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R05 — PriceIngestion worker/repository |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:60-74,175-178,443-459; src/Saydin.PriceIngestion/Workers/EvdsInflationWorker.cs:128-131,338-353; src/Saydin.PriceIngestion/Workers/IngestionOrchestrator.cs:117-137; src/Saydin.PriceIngestion/Program.cs:131-135,163-164; src/Saydin.PriceIngestion/Repositories/IngestionWindowRepository.cs:342-346` |

**Bulgu.** İddia satır satır doğrulandı. Tek ekleme: aynı fail-fatal yol yalnız lease yenilemesi için değil, `PlanWindowsAsync`'in fırlattığı `CalendarNotReadyException` (BaseAssetWorker.cs:93-99'daki ön kontrolle arasında TOCTOU penceresi var) ve `RecordFailureAsync`'in bounded finalize timeout'u için de geçerli — yani "geçici DB hatası = süreç ölümü" sınıfı finder'ın belirttiğinden biraz daha geniş.

**Etki.** Rutin bir PostgreSQL failover/blip beş worker'ı birden iptal eder, süreç exit 1 ile ölür ve restart sonrası elindeki pencere için 30 dakikaya kadar `Busy` bekler. Bu süre boyunca tüm kaynaklarda ingestion durur ve metric/trace export'u kesilir.

**Öneri.** (a) `RenewLeaseAsync` çevresinde geçici Npgsql hatalarını sınırlı sayıda yeniden dene; yalnız `false` dönüşü lease-lost sayılsın. (b) `RunAsync`'e `IngestionLeaseLostException` + geçici DB hataları için bounded retry/backoff ekle — pencere durable olduğundan worker'ın ölmesi gerekmiyor. (c) `AddDbContextFactory`'de `EnableRetryOnFailure` etkinleştir. (d) `ClaimNextAsync`'te aynı makine/pid prefix'ine sahip bayat lease'in erken reclaim'ine izin ver.

---

### 23. Retryable pencerelerde exponential backoff yok — sabit 5 dk/30 dk sonsuza kadar

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R05 — PriceIngestion worker/repository |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Repositories/IngestionWindowRepository.cs:389,512-520; src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:32,250-256; src/Saydin.PriceIngestion/Workers/EvdsInflationWorker.cs:29,197-203` |

**Bulgu.** Kod iddiası doğrulandı; **doküman iddiası REDDEDİLDİ**. `07-remediation-progress.md:57`'deki "backoff" ifadesi, önceki review'ın 02-findings-medium.md #17 bulgusuna (HTTP retry zincirinde backoff yok) atıftır ve o düzeltme gerçekten yapılmıştır: HttpResilienceExtensions.cs:49-73 `BackoffType = Exponential`, `UseJitter = true`, `JitteredExponentialDelay` mevcut. Yani doküman yalan söylemiyor; eksik olan yalnızca **ledger seviyesindeki** pencere-yeniden-deneme backoff'udur. Bu, HTTP katmanındaki 3 denemelik backoff'un dış katmanı olduğundan etki de finder'ın anlattığından bir miktar düşüktür.

**Etki.** Uzun süreli provider kesintisinde pencere her 5 dakikada bir süresiz yeniden denenir (günde 288 deneme/asset). OpenExchangeRates ücretsiz planı (1.000 istek/ay) birkaç günlük kesintide tükenebilir. Sonsuz retryable pencere hiçbir zaman terminal bir operatör sinyali (abandoned) üretmez.

**Öneri.** `AttemptCount`'u kullanarak jitter'lı exponential backoff uygula (`min(LogicalRetryDelay * 2^(attempt-1), 6h)`), bir üst deneme eşiğinden sonra pencereyi `Abandoned`'a taşıyıp Critical alarm üret. Doküman ifadesini değiştirmeye gerek yok; istenirse "HTTP retry backoff" olarak netleştirilebilir.

---

### 24. `next_attempt_at`e göre uyanma testi tautolojik: due değeri LogicalRetryDelay ile birebir aynı seçilmiş

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | test-quality |
| **Hat** | R05 — PriceIngestion worker/repository |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.PriceIngestion.Tests/Workers/BaseAssetWorkerTests.cs:288-338; src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:132-135,372-382` |

**Bulgu.** Doğrulandı. Test tamamen değersiz değil (asset sıralaması ve starvation-yokluğu gerçekten doğrulanıyor), ancak adında ve gerekçesinde vaat ettiği `next_attempt_at` sözleşmesini ayırt edemiyor.

**Etki.** Ledger'ın `next_attempt_at` sözleşmesine uyum regresyonu CI'da sinyal üretmez; retryable pencereler sessizce bir sonraki günlük/aylık koşuya kayabilir.

**Öneri.** `logicalRetryDelay`'i `due`dan farklı seç (ör. retry 60 dk, due +7 dk), `+6:59`da uyanmadığını ve `+7:00`da uyandığını doğrula. Ayrı bir case ile `Busy(NextAttemptAt: null)` durumunda fallback'in `LogicalRetryDelay` olduğunu doğrula.

---

### 25. Freshness hydration testi adında "hosted service boundary" diyor ama yalnız internal yardımcı metodu çağırıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | test-quality |
| **Hat** | R05 — PriceIngestion worker/repository |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.PriceIngestion.Tests/Workers/IngestionFreshnessHydrationServiceTests.cs:12-38; src/Saydin.PriceIngestion/Workers/IngestionFreshnessHydrationService.cs:21-34; src/Saydin.PriceIngestion/Program.cs:163-164` |

**Bulgu.** Doğrulandı. Test adı, kapsadığından fazlasını iddia ediyor: doğrulanan şey `RefreshSafelyAsync`'in yuttuğu, `StartAsync`/`ExecuteAsync`'in onu çağırdığı değil.

**Etki.** Önceki review'ın "tek geçici DB hatası tüm süreci durduruyor" bulgusunun düzeltmesi test tarafından korunmuyor; regresyon üretimde ilk DB blip'inde host startup/host çökmesi olarak ortaya çıkar.

**Öneri.** Testi `((IHostedService)service).StartAsync(ct)` üzerinden çalıştır ve `FakeTimeProvider` + kısa `IngestionFreshness:RefreshSeconds` ile arka arkaya iki hatadan sonra üçüncü tick'te `telemetry.PublishState`'in çağrıldığını doğrulayan bir `ExecuteAsync` testi ekle.

---

### 26. Üretimde artık çalışmayan kod testlerle canlı tutuluyor: `ComputeMissingRanges`, `TargetDate` override'ları, `daily_update`/`inflation_daily` job tipleri

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | test-quality |
| **Hat** | R05 — PriceIngestion worker/repository |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:36-39,462-481; src/Saydin.PriceIngestion/Workers/TcmbWorker.cs:31-35; src/Saydin.PriceIngestion/Workers/CoinGeckoWorker.cs:24-25; src/Saydin.PriceIngestion/Workers/OpenExchangeRatesWorker.cs:27-28; tests/Saydin.PriceIngestion.Tests/Workers/BaseAssetWorkerTests.cs:33-41,360-425` |

**Bulgu.** Doğrulandı. Özellikle zararlı olan alt madde `TcmbWorker_UsesProviderCutoffThenAuthoritativeLatestEligibleDay` testinin ilk iki assert'i: 16:30 cutoff'unun "üretim hedef seçimi" olduğu izlenimini veriyor, oysa üretimde hedef yalnız `ResolveLatestExpectedObservationAsync`'ten geliyor ve cutoff sadece `notAfter` üst sınırı olarak kullanılıyor. Bu tam da R05-01/R05-V1'de yanlış anlaşılan noktadır.

**Etki.** Yanlış güven: yeşil testler üretimde hiç çalışmayan kod yollarını kanıtlıyor gibi görünüyor ve cutoff semantiği ikinci bir okuyucuya yanlış aktarılıyor. Ayrıca `ResolveTargetDate` gibi yalnız test için var olan yüzey ve dört dosyada ölü kod bakım yükü.

**Öneri.** `ComputeMissingRanges`i, CoinGecko/OXR/TCMB `TargetDate` override'larını ve `ResolveTargetDate` seam'ini sil. TwelveData cutoff'unu (hâlâ canlı olan tek kullanım) `ResolveBackfillThroughForTestAsync` üzerinden test et. `daily_update`/`inflation_daily`'nin artık üretilmediğini `docs/architecture/database-schema.md`'de not düş (constraint geriye dönük veriler için kalsın).

---

### 27. Kalıcı olarak bloke olmuş scope için metrik/alarm yok; operatör hangi asset'in neden takıldığını tek sorguyla göremiyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R05 — PriceIngestion worker/repository |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:257-261,321-325; src/Saydin.PriceIngestion/Repositories/IngestionFreshnessTelemetry.cs:87-125; src/Saydin.PriceIngestion/Repositories/IngestionWindowRepository.cs:70-78; docs/runbooks/ingestion-stale.md:1-27` |

**Bulgu.** Doğrulandı, küçük bir nüansla: hata anında `SaydinMetrics.IngestionAttempts` sayacı `outcome="permanent_failure"/"partial_rejected"` etiketiyle bir kez artıyor (IngestionFreshnessTelemetry.cs:67-69), yani tamamen sessiz değil. Ancak bu tek atışlık bir counter; bloke durumu **süregelen bir durum** olarak yayınlayan hiçbir gauge yok ve alarmlanabilir bir "şu anda N pencere bloke" sinyali mevcut değil.

**Etki.** En kritik arıza modu (sessiz kalıcı durma) gözlemlenebilir değil; nöbetçi hangi asset/pencere olduğunu bulmak için elle SQL yazmak zorunda ve runbook kurtarma yolunu hiç anlatmıyor → MTTR uzar.

**Öneri.** (a) Hydration sorgusuna `state='permanent_failed'` pencere sayısını ekleyip `saydin_ingestion_scope_blocked` gauge'u olarak (`source`, `job_type`, `outcome_code` etiketli) yayınla. (b) Prometheus'a Critical alert + runbook linki ekle. (c) `docs/runbooks/ingestion-stale.md`'ye bloke pencereyi bulan SQL'i ve imzalı `requeue_permanent_window` adımını (ve v2 pencerelerde `calendar_release_id` bağının sabit kaldığı uyarısını) ekle.

---

### 28. EvdsInflationWorker, BaseAssetWorker'ın ~150 satırlık lease/deadline/scheduling mantığını kopyalıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | simplicity |
| **Hat** | R05 — PriceIngestion worker/repository |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Workers/EvdsInflationWorker.cs:86-112,213-228,279-393; src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:122-156,264-279,384-503` |

**Bulgu.** Doğrulandı; bu bir üslup tercihi değil, iki kopyada bağımsız evrilme riski taşıyan somut bir tekrar (aynı isimli `WorkerPass` tipinin farklı sözleşmeye sahip olması sapmanın başladığının kanıtı).

**Etki.** Bir sonraki lease/deadline düzeltmesi tek kopyaya uygulanırsa davranış sessizce ayrışır; okuyucu "aynı ama farklı" iki worker sözleşmesini kafasında tutmak zorunda; test yükü iki katına çıkıyor.

**Öneri.** Lease yenileme + mutlak deadline + terminalization + `min(next_attempt_at, scheduled)` uyanma mantığını asset-agnostik bir `IngestionWindowDrainer` (veya `LeasedProviderCall<T>`) yardımcısına çıkar; `BaseAssetWorker` ve `EvdsInflationWorker` yalnız scope/plan/validate farklarını sağlasın.

---

### 29. Her ertelenen geçiş tüm asset'ler için tam backfill aralığını yeniden planlıyor ve aynı takvim readiness sorgusunu iki kez çalıştırıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | performance |
| **Hat** | R05 — PriceIngestion worker/repository |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:90-101,372-382; src/Saydin.PriceIngestion/Repositories/IngestionWindowRepository.cs:205-251,1183-1188` |

**Bulgu.** Gözlem doğrulandı, ancak finder'ın (a) önerisi HATALI: `BackfillAsync`'teki ön `EnsureCalendarReadyAsync` çağrısı gereksiz değil — `PlanWindowsAsync` hazır değilken `CalendarNotReadyException` **fırlatıyor** (repo:206-207) ve bu exception `RunAsync`'te yakalanmadığı için (R05-02) tüm süreci düşürür. Ön kontrol, fatal exception'ı yumuşak `calendar_not_ready` ertelemesine çeviren kasıtlı bir kapıdır. Doğru düzeltme onu silmek değil, readiness sonucunu `PlanWindowsAsync`'e parametre olarak geçirmek (veya PlanWindows'un readiness'i fırlatmak yerine döndürmesi).

**Etki.** Retryable bir pencere varken her 5 dakikada 30 asset × (2 tam aralık COUNT + advisory lock'lu transaction + pencere listesi) çalışır — günde on binlerce gereksiz sorgu ve `ingestion_windows` üzerinde gereksiz lock trafiği. Asset sayısı ve geçmiş uzadıkça doğrusal kötüleşir.

**Öneri.** (a) Readiness'i tek kez hesaplayıp `PlanWindowsAsync`'e parametre olarak geçir (imzayı `MarketCalendarReadiness` alacak şekilde genişlet). (b) Planlamayı yalnız "yeni gün eklendi" veya "ilk geçiş" durumunda yap; retryable uyanmalarda doğrudan `DrainAsync`'e gir. (c) Readiness'i (release id + coverage) worker içinde kısa TTL'li cache'le.

---

### 30. `ProviderExceptionSanitizer.ForLog` exception zincirini ve stack'i yok ediyor; beklenmeyen adapter hatasının kök nedeni loglara ulaşmıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R05 — PriceIngestion worker/repository |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Workers/ProviderExceptionSanitizer.cs:9-26; src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:185-199,432-441; src/Saydin.PriceIngestion/Workers/EvdsInflationWorker.cs:137-156` |

**Bulgu.** Doğrulandı, bir düzeltmeyle: finder "orijinal **tip** de kayboluyor" diyor — bu kısmen yanlış. BaseAssetWorker.cs:190-191 ve EvdsInflationWorker.cs:142-143 log şablonunda `{ExceptionType}` = `ex.GetType().Name` structured property olarak taşınıyor, ayrıca tip `ForLog`/`Detail` mesajının içinde de var (aslında iki kez, bkz. R05-V3). Gerçekten kaybolan şey **inner exception zinciri** ve **stack trace'in ilk satırı dışındaki her şey**.

**Etki.** Bu tam da bir asset'i kalıcı bloke eden yol (`adapter_exception_permanent` → PermanentBlocked). Kök neden analizi için tek kanıt tek satırlık bir mesaj; olayı yeniden üretmek gerekiyor. Gizlilik/tanılanabilirlik dengesi tanılanabilirlik aleyhine fazla kaymış.

**Öneri.** Sanitize edilmiş **zincir** üret: her `InnerException` seviyesi için `Type: sanitize(Message)` (derinlik 3-4 sınırlı) ve stack'in ilk N (ör. 5) karesi. Serilog'a sahte exception nesnesi vermek yerine `sanitized_chain` structured property'si kullan.

---

### 31. Mutlak provider deadline'ı tek HTTP alışverişi için boyutlandırılmış ama pencere başına yüzlerce istek yapan adapter'lara uygulanıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | performance |
| **Hat** | R05 — PriceIngestion worker/repository |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:34,384-403; src/Saydin.PriceIngestion/Adapters/OpenExchangeRatesAdapter.cs:55-110,138-140; src/Saydin.PriceIngestion/Workers/OpenExchangeRatesWorker.cs:19-21; src/Saydin.PriceIngestion/Extensions/HttpResilienceExtensions.cs:12` |

**Bulgu.** Tasarım uyumsuzluğu doğrulandı, ancak finder'ın etki tarifi iki noktada abartılı: (1) `_dayCache` (24 saat TTL, OpenExchangeRatesAdapter.cs:33-34) başarılı günleri sakladığı ve cache hit'lerinde 200 ms gecikme uygulanmadığı için ikinci deneme çok hızlı tamamlanır — yani livelock değil, birkaç 5 dakikalık döngüde kendini toparlayan bir yavaşlama. (2) Başarısız deneme kotadan **yeniden istek yakmaz** (cache hit'ler HTTP'ye gitmez); yalnız süreç restart'ı sonrası cache sıfırlandığında yeniden yakar. Bu nedenle severity Medium'da kalıyor ama "kota tükenmesi" iddiası zayıf.

**Etki.** OXR'nin ilk 365 günlük backfill penceresi orta gecikmede 3 dk deadline'ını aşarak `provider_deadline` retryable hatasına düşer; ilerleme yalnız süreç-içi cache sayesinde korunur. Süreç sık yeniden başlıyorsa (bkz. R05-02) her seferinde baştan başlar ve kota yakar.

**Öneri.** Deadline'ı pencere büyüklüğüne göre ölçekle (`ProviderDeadline = max(TotalRequestTimeout, beklenenİstekSayısı × perRequestBudget)`) veya gün-başına-istek yapan adapter'lar için `ChunkDays`'i tek deneme bütçesine sığacak şekilde küçült (OXR/TCMB için ör. 30) — böylece hem deadline hem lease yenileme hem de kısmi ilerleme durable olur.

---

### 32. Bloke pencerelerin kurtarma yolu asset başına tek tek imzalı plan gerektiriyor; toplu kurtarma için ne API ne runbook var

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R05 — PriceIngestion worker/repository |
| **Doğrulama** | CONFIRMED (doğrulayıcı ek bulgusu) |
| **Konum** | `src/Saydin.PriceIngestion/Repositories/IngestionWindowRepository.cs:556-569; src/Saydin.DataRepair/SignedRepairPlan.cs:92-102; docs/runbooks/ingestion-stale.md:21-22` |

**Bulgu.** `RequeuePermanentAsync` imzası `(Guid windowId, DateTimeOffset nextAttemptAt, ct)` — tek pencere. `SignedRepairPlan` doğrulaması her `requeue_permanent_window` operasyonu için ayrı `windowId` + `preimage_sha256` + `next_attempt_at_utc` istiyor ve `windows.Add(id)` ile tekrarı reddediyor. Kaynak-geneli bir arıza (R05-01/R05-V1: takvim ya da provider kaynaklı, tüm asset'leri aynı anda vuran hata) 30 sembolde 30 ayrı operasyon demek; her birinin window id'sini bulmak için de dokümante edilmiş bir sorgu yok (`ingestion-stale.md` yalnız "reviewed provenance workflow" diyor).

**Etki.** Kurtarma insan hatasına açık ve yavaş; kanıt-temelli onarım tasarımının doğru olan katı sözleşmesi, toplu senaryoda pratik bir operasyon yolu bırakmıyor → uzun MTTR.

**Öneri.** (a) Bloke pencereleri listeleyen salt-okunur bir komut/sorgu ekle (DataQualityAudit çıktısı veya runbook'ta hazır SQL) ki plan hazırlığı otomatikleşsin. (b) Plan üretimini kolaylaştıran bir yardımcı (window id + preimage hash listesini üreten script) ekle. (c) Alternatif olarak `requeue_permanent_window`'a scope-tabanlı (source + job_type + tarih aralığı) ve budget-sınırlı bir varyant tanımla.

---

### 33. TryReadDecimal JSON null'da fırlatıyor; "ND/boş değeri atla" yolları ölü kaldı

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | data-integrity |
| **Hat** | R06 — PriceIngestion adapter/mapper |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Mappers/ProviderValueParser.cs:19-30 (yeni dosya); src/Saydin.PriceIngestion/Mappers/EvdsInflationMapper.cs:55-63; src/Saydin.PriceIngestion/Mappers/TwelveDataMapper.cs:115-119` |

**Bulgu.** Bu diff `GetString()` tabanlı ayrıştırmayı yeni `ProviderValueParser.TryReadDecimal`'a taşırken JSON `null` davranışını sessizce değiştirdi: eskiden null skip'lenirken artık `contract_value_kind_invalid` fırlatıyor. Bu yüzden EVDS'in `"ND"/boş → continue` ve TwelveData'nın `open/high/low/volume is null → continue` guard'ları JSON null için ölü kod. Ayrıca `Try*` konvansiyonu ihlal ediliyor (bool sözleşmesi veren metot fırlatıyor) ve aynı dosyadaki `ReadString` Null'ı düzgün ele alarak asimetriyi görünür kılıyor.

**Etki.** Sağlayıcı yayınlanmamış bir alanı JSON `null` olarak dönerse: EVDS'te tek eksik ay için tasarlanmış `not_published_yet` RETRYABLE yolu (EvdsInflationAdapter.cs:130-135) devreye giremez, window PermanentFailed olur ve aylık TÜFE ingestion'ı operatör requeue'suna kadar durur. TwelveData'da hacimsiz bir bar tüm chunk'ı kalıcı bloke eder. Diff öncesi her iki durumda da satır atlanıyordu.

**Öneri.** `TryReadDecimal`'a `case JsonValueKind.Null: value = 0; return false;` ekle; throw'u yalnız Object/Array/True/False için bırak. Alternatif olarak fırlatan sürümü `ReadDecimal` adıyla ayır. `"TP_FG_J0": null` ve `"volume": null` için negatif test ekle; `ProviderValueParser` için kendi test dosyasını yaz.

---

### 34. Mutlak provider deadline pencere başına ama tek HTTP alışverişine göre boyutlandırılmış

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R06 — PriceIngestion adapter/mapper |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:34,392-403; src/Saydin.PriceIngestion/Workers/OpenExchangeRatesWorker.cs:19-21; src/Saydin.PriceIngestion/Adapters/OpenExchangeRatesAdapter.cs:56,141-142` |

**Bulgu.** Tek HTTP alışverişi için tasarlanmış `TotalRequestTimeout` (3 dk) sabiti, N istekli bir adapter çağrısının tamamına pencere bütçesi olarak uygulanıyor. OER'de tek window = 365 sıralı istek + 72,8 s zorunlu pacing → istek başına ~293 ms kalır; ~300 ms RTT'de soğuk cache'li ilk pass deadline'a çarpar. TCMB'de (90 gün ≈ 62 istek) marj rahattır, dolayısıyla sorun pratikte OER'e özgüdür.

**Etki.** Soğuk cache'li ilk OER backfill pass'i `provider_deadline` ile terminalleşir ve worker 3 dakikayı yalnız cache doldurmaya harcar. Window kaybolmaz: retryable, 5 dk sonra tekrar denenir ve 24 saatlik `_dayCache` sayesinde ilerleme korunur, birkaç pass'te tamamlanır. Gerçek maliyet operasyonel gürültü (yanıltıcı `provider_deadline` terminal kayıtları), gecikmiş backfill ve container restart sıklığı 5 dk'nın altına inerse yakınsamama riskidir.

**Öneri.** Bütçeyi iki seviyeye ayır: tek HTTP alışverişi için pipeline'ın `TotalRequestTimeout`'u, pencere için chunk-farkında bir `ProviderDeadline` (`min(LeaseDuration, ChunkDays × istek başına bütçe)`). En azından `OpenExchangeRatesWorker` içinde `ProviderDeadline`'ı override et ve pacing gecikmesini bütçe dışında tut. `docs/architecture.md`'de "çağrı başına" ile "istek başına" ayrımını açıkça yaz; `ChunkDays`'i OER için düşürmek (ör. 90) de aynı sonucu verir.

---

### 35. Circuit breaker, korumak için tasarlandığı stall/timeout modunda hiçbir zaman açılamaz

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R06 — PriceIngestion adapter/mapper |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Extensions/HttpResilienceExtensions.cs:14-16,33-40; docs/architecture.md:100-103; CLAUDE.md resilience maddesi` |

**Bulgu.** Circuit breaker yalnız hızlı-hata modunda (5xx/connection refused; mantıksal çağrı ≈ 14 s) 120 s'lik örnekleme penceresinde 2 örnek biriktirebilir. Sağlayıcı askıda kaldığında bir mantıksal çağrı ~134 s (üst sınır 180 s) sürdüğünden `SamplingDuration=120 s < zincir süresi` olur ve `MinimumThroughput=2` asla karşılanmaz — devre açılmaz. `docs/architecture.md:100-103` ve CLAUDE.md bu ayrımı yapmadan koşulsuz koruma vaat ediyor.

**Etki.** Provider stall'unda (yarı-açık TCP, proxy stall) her mantıksal deneme tam retry zincirini yürütür ve devre hiç açılmaz; her window denemesi ~3 dk worker zamanı + bağlantı harcar ve 5 dk'da bir tekrarlanır. Kaynak israfı bounded olduğu için veri kaybı yok; asıl zarar mimari dokümanın üretim davranışını yanlış tarif etmesi — önceki review'de aynı dosyada tespit edilen doküman-kod uyuşmazlığının tekrarı.

**Öneri.** `SamplingDuration`'ı en kötü zincir süresinin 2-3 katına çıkar (ör. 600 s) veya `MinimumThroughput=1` yap; alternatif olarak breaker'ı retry zincirinin içine (attempt seviyesine) taşı. CB testini `retryDelayOverride` vermeden, gerçek `BaseRetryDelay` ve `FakeTimeProvider` ile attempt-timeout üzerinden ilerleyen bir senaryoyla yaz. Doküman ve CLAUDE.md'de "hızlı-hata modunda" kaydını ekle.

---

### 36. Pipeline zaman aşımı bütçelerinin davranışsal testi yok; yeni test sabit doğrulayan totoloji

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | test-quality |
| **Hat** | R06 — PriceIngestion adapter/mapper |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.PriceIngestion.Tests/HttpResilienceExtensionsTests.cs:106-113; tests/Saydin.PriceIngestion.Tests/Workers/BaseAssetWorkerTests.cs:251-286` |

**Bulgu.** `PipelineBudgetConstants_AreSingleThreeMinuteContract` (HttpResilienceExtensionsTests.cs:106-113) yalnız sabitlerin değerini doğrulayan bir totoloji; `AddSaydinResilience` içindeki iki `AddTimeout` stratejisinin pipeline'da bulunduğunu hiçbir test doğrulamıyor. Tek gerçek stall testi (`BaseAssetWorkerTests.cs:251-286`) resilience pipeline'ı olmayan çıplak `HttpClient` kullanıyor, dolayısıyla yalnız worker deadline'ını kapsıyor. Bulgudaki satır referansları hatalıydı; iddia doğru.

**Etki.** Önceki review'de High olarak kapatılan regresyonun (`TotalRequestTimeout` stratejisinin silinmesi) aynısı tekrar girerse zorunlu unit kapısı yakalamaz. Bu, "testi iddiaya uydurma" sınıfı bir kapanış: sabit korunuyor, davranış korunmuyor. AttemptTimeout için de aynı boşluk var.

**Öneri.** `FakeTimeProvider` + gerçek `AddSaydinResilience` üzerinden iki davranışsal test yaz: (1) header'ı hiç göndermeyen handler ile 30 s ilerlet → attempt timeout'un retry tetiklediğini `CallCount` ile doğrula; (2) her denemede takılan handler ile 180 s ilerlet → çağıranın `TimeoutRejectedException` aldığını doğrula. Sabit-eşitlik testini bırakacaksan `AddSaydinResilience`'in ürettiği pipeline'da iki timeout stratejisinin var olduğunu da assert et.

---

### 37. HTTP gövde sınırı, gözlem-başına evidence sabitini yeniden kullanıyor ve aşım kalıcı hata üretiyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | data-integrity |
| **Hat** | R06 — PriceIngestion adapter/mapper |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Adapters/ProviderPayload.cs:19,32; src/Saydin.Shared/Entities/ObservationAuthority.cs:25; src/Saydin.PriceIngestion/Adapters/CoinGeckoAdapter.cs:99-104` |

**Bulgu.** `BoundedHttpContent.ReadAsync` transport (tüm HTTP yanıtı) limiti olarak gözlem-başına evidence sabiti `ObservationAuthorityLimits.SourceRawBytes` = 64 KiB'ı kullanıyor; iki farklı sözleşme tek sabite bağlı. Aşım beş adapter'da da `PermanentFailure("payload_too_large")` üretiyor → PermanentFailed + operatör requeue. CoinGecko 365 günlük chunk'ında yanıt (kullanılmayan `market_caps`/`total_volumes` dahil, `precision=6`) tahminen 45-50 KB, yani limitin ~%75'i.

**Etki.** Sağlayıcı yanıta bir alan eklerse ya da `ChunkDays`/`precision` büyütülürse boyut aşımı retryable değil KALICI window blokajına dönüşür ve ilgili asset'in fiyat güncelliği operatör müdahalesine kadar durur. Ayrıca evidence limitini (migration 020 `source_raw` sözleşmesi) değiştiren biri farkında olmadan transport limitini de değiştirir; tersi de geçerli.

**Öneri.** Transport limiti için ayrı, sağlayıcı başına ayarlanabilir bir sabit tanımla (`ProviderTransportLimits.MaxResponseBytes`) ve evidence sabitinden ayır. Aşımı `RetryableFailure` yap veya en azından gerçekleşen yanıt boyutlarını bir histogram metriğiyle yayınla ki limite yaklaşma önceden alarma dönüşsün. CoinGecko URL'ine kullanılmayan dizileri kırpan bir parametre yoksa bile, boyut riskini yorumla belgele.

---

### 38. BaseAssetWorker ve EvdsInflationWorker arasında deadline/lease/drain durum makinesi kopyalanmış

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | simplicity |
| **Hat** | R06 — PriceIngestion adapter/mapper |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Workers/EvdsInflationWorker.cs:279-393 ile src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:384-502` |

**Bulgu.** `WithLeaseRenewalAsync`, `ObserveDetachedAsync`, `RenewUntilCancelledAsync`, `DrainDisposition` ve `DrainResult` iki worker'da birebir kopya; `ProviderDeadline` iki yerde ayrı tanımlı. Ayrışma başlamış: `WorkerPass` base'de `Include` metotlarına sahipken EVDS'te yalnız `Empty` taşıyor. Bulgudaki "RecordPermanentBlocked yalnız base'de" alt-iddiası yanlıştır — EvdsInflationWorker.cs:207-210'da mevcuttur. `GetDelayUntilNextRun` farkı ise meşru (günlük vs aylık zamanlama).

**Etki.** Deadline semantiğindeki (ör. R06-03 düzeltmesi) veya lease yenileme aralığındaki her değişiklik iki yerde yapılmak zorunda. Birinin unutulması EVDS (aylık TÜFE) yolunda sessiz davranış farkı yaratır ve bu yol ayda bir çalıştığı için en geç fark edilen yoldur. İkinci bir okuyucu için hangi kopyanın kanonik olduğu belirsiz.

**Öneri.** Lease + deadline + detached-observation sarmalayıcısını ortak bir tipe çıkar (ör. `ProviderExecution.RunWithLeaseAsync<T>(claim, operation, deadline, leaseRenewer, logger, ct)`); `DrainDisposition`/`DrainResult`/`WorkerPass` paylaşılan internal tipler olsun. Zamanlama farkını (`GetDelayUntilNextRun`) abstract bırak.

---

### 39. ProviderExceptionSanitizer bu kod tabanının fiilen kullandığı auth şemalarını maskelemiyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | security |
| **Hat** | R06 — PriceIngestion adapter/mapper |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Workers/ProviderExceptionSanitizer.cs:16-34 (yeni dosya); tests/Saydin.PriceIngestion.Tests/Workers/BaseAssetWorkerTests.cs:17-31; src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:411-412` |

**Bulgu.** `SecretValuePattern` `name[:=]value` biçimi bekliyor; `Authorization: Token <sır>` / `Authorization: apikey <sır>` gibi şema-kelimeli değerlerde yalnız şema kelimesini redakte ediyor, sır metinde kalıyor. Header formatındaki `key: <sır>` hiçbir regex'e uymuyor (`key` yalnız query-string desenindedir, bulgunun "alternasyonda hiç yok" ifadesi bu yönüyle eksik). Kod tabanının fiilen kullandığı üç şemanın (`apikey …`, `Token …`, `key` header) hiçbiri tek koruma testinde (BaseAssetWorkerTests.cs:17-31) yer almıyor — test yalnız implementasyonun kapsadığı `api_key=` ve `?token=` formatlarını doğruluyor. Ayrıca `ForLog` orijinal exception'ı sentetik bir `InvalidOperationException` ile değiştirip stack'in yalnız ilk satırını saklıyor.

**Etki.** Sır bir sağlayıcı hata mesajına ya da bir header format exception'ına düşerse hem log'a hem `ingestion_jobs.error_message`'a (IngestionWindowRepository.cs:534) yazılabilir. İstismar yolu spekülatif olduğundan asıl somut kayıp teşhis kabiliyeti: her adapter exception'ında tam stack trace kaybediliyor, buna karşılık lease-kaybı yolunda ham exception hiç redaksiyondan geçmeden loglanıyor — koruma tutarsız uygulanıyor.

**Öneri.** Regex tahmini yerine konfigürasyondan okunan gerçek secret değerlerini bir `ISecretRedactor`'a kaydedip metinde birebir ara ve maskele; bu üç şemayı da otomatik kapsar. Regex kalacaksa `key`/`appid` alternasyonu ve `(?:\s*[:=]\s*|\s+)(?:Bearer|Token|apikey)?\s*` öneki ekle, değeri satır sonuna kadar maskele. Testi gerçek şemalarla (`Authorization: Token …`, `key: …`, `apikey …`) genişlet. `ForLog` orijinal exception'ı `InnerException` olarak korusun ya da tam stack'i redakte edip saklasın; lease-kaybı yolundaki `LogDebug(operationError, …)` çağrılarını da `ForLog`'dan geçir.

---

### 40. Program.cs'teki 30 saniyelik client.Timeout ayarları ölü konfigürasyon

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | developer-experience |
| **Hat** | R06 — PriceIngestion adapter/mapper |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Program.cs:84,92,101,111,121; src/Saydin.PriceIngestion/Extensions/HttpResilienceExtensions.cs:25` |

**Bulgu.** Beş HTTP client kaydındaki `client.Timeout = TimeSpan.FromSeconds(30)` ölü konfigürasyondur: `AddSaydinResilience` sonradan `ConfigureHttpClient` ile `Timeout.InfiniteTimeSpan` yazar ve HttpClientActions kayıt sırasıyla uygulandığı için Infinite kazanır. Ezme hiçbir yorumla belgelenmemiş.

**Etki.** Etkin davranış kayıt sırasına bağlı ve okunan dosyada görünmüyor. Bir okuyucu 30 s'yi geçerli sanıp CLAUDE.md'nin timeout zorunluluğunu karşılanmış sayar; gerçekte bütçe tamamen Polly pipeline'ındadır. Sıra tersine dönecek bir refactor'da `ResponseHeadersRead` ile okunan gövde 30 s'lik `HttpClient.Timeout`'a takılır, attempt/total timeout'larla çakışır ve `provider_deadline` sözleşmesi bozulur.

**Öneri.** `Program.cs`'teki beş `client.Timeout = TimeSpan.FromSeconds(30)` satırını sil; `HttpResilienceExtensions.cs:25`'in yanına "bütçe Polly pipeline'ındadır; `HttpClient.Timeout` bilinçli olarak devre dışı" yorumunu ekle. Ezmenin uygulandığını doğrulayan bir test yaz (`CreateClient("tcmb").Timeout == Timeout.InfiniteTimeSpan`).

---

### 41. Versioned credential lifecycle production release düzleminde uygulanamıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R07 — Migrator + RoleBootstrap + DatabaseSecurity |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/deployment/compose.production.yml:113-123,160,213,274; infrastructure/deployment/validate-production.py:289-291,335,341; docs/runbooks/database-role-credential-lifecycle.md:31-35,59-63` |

**Bulgu.** İmzalı production release düzlemi `database-migrator` ve `data-repair` login sürümlerini `"1"`e fail-closed pinliyor (validate-production.py:289-291, :341) ve tüm bootstrap secret yollarını `*-v1` olarak sabitliyor. Diğer runtime servisleri için validator sürüm doğrulaması yapmıyor, ancak compose değerleri yine de literal `"1"`. Buna karşılık `database-role-credential-lifecycle.md` rotate→cutover→retire akışını sınırsız destekleniyormuş gibi anlatıyor; kardeş runbook `backup-login-renewal.md:21-23` ise aynı sınırı açıkça belgeliyor.

**Etki.** Olay anında (rol kimliğinin de değişmesi gereken bir kompromize, ya da periyodik sürüm tazeleme) operatör runbook'un cutover adımına geldiğinde imzalı release validator'ının reddiyle karşılaşır. Pratikte üretimde uygulanabilir tek ayak `reset-password`; rotate/retire kağıt üzerinde var, release yolunda yok. Bu, operatörü ya durmuş bir prosedürle ya da R07-01'i tetikleyen manuel bir kısayolla baş başa bırakır.

**Öneri.** İki seçenekten birini yap: (a) lifecycle'ı release düzlemine bağla — login sürümünü ve secret dosya adını tek bir `SAYDIN_*_LOGIN_VERSION` değişkeninden türet, validator'ı sabit `"1"` yerine 'contract allowlist'inden tek ve tutarlı bir sürüm' kuralına çevir; ya da (b) `database-role-credential-lifecycle.md`'ye `backup-login-renewal.md:21-23` ile birebir aynı üslupta bir sınır paragrafı ekle: 'production runtime şu an v1-only; v2+ cutover release pipeline'ında desteklenmiyor'. Hangisi seçilirse seçilsin R07-01'in migrator pinini de kapatmadan runbook 'desteklenen' diyemez.

---

### 42. Dev backup login validity bootstrap anında donduruluyor; 60 gün sonra tüm dev stack başlamaz ve çözüm hiçbir yerde yazmıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | developer-experience |
| **Hat** | R07 — Migrator + RoleBootstrap + DatabaseSecurity |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/secrets/bootstrap-dev-database.sh:66,78-83; docker-compose.yml:165-167; src/Saydin.DatabaseRoleBootstrap/RoleBootstrapDatabaseOperations.cs:210-231,1183-1186; docs/development-guide.md:446,455` |

**Bulgu.** Dev bootstrap, backup login'in 60 günlük geçerlilik damgasını `.env.database-runtime`'a bir kez yazar ve bu dosya yalnız `bootstrap-dev-database.sh` yeniden çalıştırıldığında tazelenir. Değer değişmediği sürece `ExtendManagedBackupValidityAsync` hiç tetiklenmez ve `VerifyRoleAttributes` süre dolumunu görmez; kırılma `AuthenticateBackupAsync`'te `backup_authentication_failed` olarak yüzeye çıkar ve `service_completed_successfully` zinciri yüzünden api/ingestion/migrator hiç başlamaz. development-guide.md §10 bu senaryoyu anmıyor ve aynı bölümdeki `-U saydin` örnekleri de artık geçersiz (`POSTGRES_USER: saydin_admin`).

**Etki.** Depoya 60+ gün sonra dönen bir geliştirici için dokümante edilmiş dev akışı kendiliğinden bozulur; hata kodu (`backup_authentication_failed`) ile çözüm (`bootstrap-dev-database.sh`'i tekrar çalıştır) arasında hiçbir bağ yoktur. Yeni bir katılımcı bunu secret bozulması sanıp volume silmeye yönelebilir; sorun giderme bölümündeki komutlar da yanlış kullanıcı adıyla ikinci bir yanlış ize götürür.

**Öneri.** 1) `bootstrap-dev-database.sh`'e mevcut `.env.database-runtime`'daki `SAYDIN_BACKUP_V1_VALID_UNTIL` 30 günden yakınsa uyaran veya otomatik tazeleyen bir kontrol ekle (üretim runbook'undaki 30 günlük uyarı eşiğiyle aynı sabit). 2) `development-guide.md` §10'a 'role-bootstrap `backup_authentication_failed` ile çıkıyor → `./infrastructure/secrets/bootstrap-dev-database.sh` tekrar çalıştır' maddesini ekle. 3) Aynı bölümdeki `pg_isready -U saydin` ve `psql -U saydin` örneklerini `-U saydin_admin` olarak düzelt.

---

### 43. DQ-006 kapsamı imzalı lane asset kümesine daraltıldı; kanıt paketi kapsamı belirtmiyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | data-integrity |
| **Hat** | R08 — DataQualityAudit + DataRepair |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.DataQualityAudit/AuditSql.cs:664-679; src/Saydin.DataQualityAudit/AuditRunner.cs:1095-1102; tests/Saydin.DataQualityAudit.IntegrationTests/AuditDatabaseFixture.cs:1998-2033` |

**Bulgu.** Daraltma ve kanıtın kapsamı belirtmemesi doğrulandı. Ancak finder'ın tetik örneği kısmen yanlış: SignedAuditInput.cs:113-120 non-evds lane'ler için `AssetId is null` olmasını REDDEDİYOR, yani 'coingecko global job' lane'i mümkün değil; $2 yalnız evds-only (enflasyon) manifest'lerde boş kalır. Asıl kalıcı boşluk, scope dışındaki aktif tcmb/twelvedata asset'lerinin calendar binding bütünlüğünün hiç bakılmaması ve bunun imzalı kanıttan okunamamasıdır.

**Etki.** Critical bir kontrolün gerçek kapsamı yalnız manifest hash'i üzerinden dolaylı okunabiliyor; 'DQ-006 clean' ifadesi operatör için takvim bağlama bütünlüğünün tamamının doğrulandığı anlamına gelmiyor. Kazanılan maliyet ise küçük (assets/asset_market_calendars/market_calendars yüzler ölçeğinde).

**Öneri.** (a) Bu üç kontrolü global bırakıp ayrı ve küçük bir bütçeyle sınırla, ya da (b) daraltmayı koru ama `AuditCheckResult`'a `Scope` (`global`|`lane_assets`) alanı ekleyip EvidenceContent.SchemaVersion/RulesetVersion'ı bump et. Her iki durumda da evds-only (boş `$2`) manifest'i kapsayan bir integration testi ekle.

---

### 44. Audit'in onarım öneri sözlüğü ile DataRepair'in kabul ettiği operasyon sözlüğü birbirini tutmuyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | product-ux |
| **Hat** | R08 — DataQualityAudit + DataRepair |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.DataQualityAudit/AuditAccumulator.cs:78-98; src/Saydin.DataRepair/SignedRepairPlan.cs:94-113` |

**Bulgu.** Sözlük uyumsuzluğu ve dokümantasyon boşluğu doğrulandı. Yumuşatıcı iki nokta: (1) `manual_review` plan operasyonu bir 'catch-all' iş emri olarak mevcut olduğundan yürütülemeyen aksiyonlar teknik olarak ifade edilebilir; (2) operatör window id'lerini HMAC'i kırarak değil doğrudan `ingestion_windows` sorgusuyla bulabilir. Dolayısıyla akış 'kopuk' değil, tipli ve belgesiz bir eşleme eksikliğidir — kind 'defect' değil excellence-gap.

**Etki.** Olay anında operatör DQ-003/006/009 önerilerini plana çevirmeye çalışırken tahmin yürütmek ve `plan_operation_type_invalid` ile deneme-yanılma yapmak zorunda; iyileştirme kâğıt üzerinde tipli, uçta yürütülemez.

**Öneri.** `RepairAction` → plan operasyon tipi eşlemesini tek yerde (kodda paylaşılan sabit + `docs/runbooks/data-repair.md`'de tablo) yaz; yürütülebilir karşılığı olmayan aksiyonlar için açıkça `manual_review` iş emrine düşürüldüğünü belgele. `RepairRecommendationPolicyTests`'i wire string'leri ve DataRepair'in kabul ettiği tip kümesiyle karşılaştıran bir teste dönüştür.

---

### 45. İmzalı onarım planını üretecek araç veya belgelenmiş prosedür yok

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R08 — DataQualityAudit + DataRepair |
| **Doğrulama** | CONFIRMED |
| **Konum** | `docs/runbooks/data-repair.md:60-66,138-176; src/Saydin.DataRepair/RepairOptions.cs:47-64; src/Saydin.DataRepair/README.md:8-30` |

**Bulgu.** İddia doğrulandı: yürütme tarafı (compose bağlama, release attestation, volume doğrulaması, receipt saklama) çok ayrıntılı belgelenmişken girdi üretimi tamamen belgesiz ve araçsız.

**Etki.** Gerçek bir olayda operatör planı üretemez; yanlış hesaplanmış tek bir preimage `repair_preimage_rejected` ile döner ve döngü elle tekrarlanır. Bu, alt sistemin birinci sınıf olmasını engelleyen en büyük tek boşluktur.

**Öneri.** (1) Salt-okunur bir `plan-template`/`--emit-preimages` alt komutu ekle (mevcut audit login'i ve trust lease'i zaten var); (2) `docs/runbooks/data-repair.md`'ye plan şeması, alan-alan türetme kuralları, nonce/approval-token üretimi ve offline imzalama seremonisi bölümü yaz; (3) uçtan uca bir integration kabul testiyle mühürle.

---

### 46. Dry-run gerçek bir önizleme değil; apply yolundaki dört fail-closed kapısını denemiyor ve README bunu yanlış anlatıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R08 — DataQualityAudit + DataRepair |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.DataRepair/Program.cs:57-66; src/Saydin.DataRepair/RepairDatabase.cs:110-131; src/Saydin.DataRepair/RepairOptions.cs:68-82; src/Saydin.DataRepair/README.md:68-70` |

**Bulgu.** Tüm iddialar doğrulandı; ek olarak README'nin 'validates every preimage and safety guard' cümlesi doğrudan bir doküman-kod uyumsuzluğudur ve runbook (satır 155-157) bağımsız gözden geçiricinin kararını bu dry-run kanıtına dayandırmasını şart koşar.

**Etki.** Onay ritüeli, gözden geçiricinin karar veremeyeceği iki sayıya dayanıyor; destructive adım ise dry-run'ın kanıtlamadığı dört kapıyı (guard bütçesi, approval token, receipt root, KMS erişimi) değişim penceresi açıldıktan ve advisory lock alındıktan sonra ilk kez deniyor.

**Öneri.** (1) Dry-run'da her `requeue_permanent_window` için makine-okunur tek satır bas (op index, window, scope, state, outcome, next_attempt, preimage, guard); (2) `ComputeGuardAsync`'i dry-run'da `lockRows:false` ile çalıştırıp bütçeyi gerçekten sına; (3) receipt/approval/KMS argümanlarını dry-run'da isteğe bağlı kabul edip verildiklerinde yalnız doğrula; (4) README'deki guard iddiasını düzelt.

---

### 47. İmzalı girdi ve kanıt sözleşmelerinin semantiği değişti ama hiçbir sürüm numarası bump edilmedi

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | data-integrity |
| **Hat** | R08 — DataQualityAudit + DataRepair |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.DataQualityAudit/AuditRunner.cs:95-97; src/Saydin.DataQualityAudit/AuditModels.cs:39-43,85-101; src/Saydin.DataQualityAudit/SignedAuditInput.cs:72,93-95` |

**Bulgu.** Sürüm bump'ının yapılmadığı doğrulandı. Eski manifest'in yeniden çalıştırılması tetiği zayıftır (manifest'ler `ExpiresAtUtc <= now` ile zaten reddedilir), asıl kusur arşivlenmiş imzalı kanıtın 'hangi kural setiyle üretildi' sorusunu artık ayırt edememesidir — runbook bu kanıtların finansal saklama süresi boyunca korunmasını şart koşuyor.

**Etki.** Aynı `ruleset=DQ-001..009/v1, schemaVersion=1` etiketini taşıyan iki bundle farklı kapsam ve farklı aksiyon sözlüğüyle üretilmiş olabilir; uzun süreli denetlenebilirlik zayıflıyor.

**Öneri.** `RulesetVersion`'ı `DQ-001..009/v2` yap, `EvidenceContent.SchemaVersion`'ı 2'ye çıkar; `AuditInputManifest.SchemaVersion`'ı 2'ye çıkarıp `schemaVersion==1` için ayrı `input_manifest_schema_unsupported` kodu üret ve bu sürümlerin ne zaman bump edileceğini kısa bir yorum bloğuyla belgele.

---

### 48. Üretim compose'undaki DQA komutu `--production-target-authority-file` taşımıyor; production beyanlı bir manifest ile DQA hiç çalışamaz

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R08 — DataQualityAudit + DataRepair |
| **Doğrulama** | CONFIRMED (doğrulayıcı ek bulgusu) |
| **Konum** | `infrastructure/deployment/compose.production.yml:691-718; infrastructure/deployment/validate-production.py:293-303; src/Saydin.DataQualityAudit/EvidenceSigning.cs:313-318` |

**Bulgu.** `EvidenceSignerFactory.Create` → `ValidateProductionTargetAuthority`: `if (authorityFile is null) { if (manifestClaimsProduction) throw Invalid("production_target_authority_missing"); return false; }` (EvidenceSigning.cs:313-318). Üretim compose'undaki `data-quality-audit` komutu (compose.production.yml:691-718) `scan --input ... --signer-mode oci-kms-instance-principal ... --output ...` argümanlarını sayıyor ama `--production-target-authority-file` içermiyor; `validate-production.py:295-303` de bu argümanı `required_dqa` listesine koymuyor. Repo genelinde bu dosyayı üreten/monte eden hiçbir script yok (`grep -rn authority infrastructure/` yalnız ilgisiz `data_repair_runtime_authority_missing` token'ını döndürüyor); dosya yalnız test yardımcılarında üretiliyor (tests/Saydin.DataQualityAudit.Tests/TestFiles.cs:68-75).

**Etki.** Üretimde belgelenmiş/valide edilmiş tek DQA çalıştırma yolu, tasarımın öngördüğü production manifest ile fail-closed düşüyor; operatörü ya doğaçlama argüman eklemeye ya da manifest'te environment'ı düşürerek üretim güvenlik kapılarını devre dışı bırakmaya itiyor. DQA için runbook olmadığından bu tercih hiçbir yerde yönlendirilmiyor.

**Öneri.** Compose komutuna `--production-target-authority-file /run/saydin-secrets/private/production-target` ekleyip dosyayı `audit_secret` volume'una root-only control plane ile önceden yerleştir (validate-private-material.py envanterine dahil et); `validate-production.py` `required_dqa` listesine bu argümanı ekle; DQA runbook'unda dosyanın nasıl üretildiğini ve neyi temsil ettiğini yaz (bkz. R08-07).

---

### 49. Materializer cutoff'u yalnız duvar saatinden türetiyor: >1 aylık kesinti sonrası plan otomatik onarılamıyor, ay/yıl başında henüz var olmayan arşiv sayfası isteniyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R09 — calendar-data ve calendar infrastructure |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `tools/calendar-data/src/Saydin.CalendarData/CalendarPlanMaterializer.cs:16-42 ve :50-57` |

**Bulgu.** `TcmbProviderCutoff(utcNow)` yalnız Europe/Istanbul saatinden bugün/dün üretir; taban bundle'ın `coverageThrough`'una hiç bakmaz. Plan yalnız iki kaynak listeler: `tcmb-annual-{cutoff.Year}` ve `tcmb-month-{cutoff:yyyyMM}`. `CalendarDataGenerator.GenerateTcmb` ise `from`..`through` arasındaki HER ay için kaynak arar (`tcmb_month_source_missing`). Yani taban coverage 2026-08-17 iken timer 2026-11-09'a kadar durursa plan yalnız 202611'i ister, 202609/202610 arşivleri ne tabanda ne planda bulunur → `tcmb_month_source_missing: 2026-09`. Ayrıca ayın 1'i hafta sonu/tatile denk geldiğinde (örn. 2026-02-01 Pazar) ertesi sabah 06:00'daki koşu, o ayın ilk yayını henüz yapılmamışken `/kurlar/202602/Feb_tr.html` sayfasını ister.

**Etki.** Otomasyonun kendini toparlama yeteneği yok: her uzun kesinti, elle plan yazımı gerektirir (tam da bu değişikliğin ortadan kaldırmayı hedeflediği iş). Ay/yıl dönümlerinde öngörülebilir, tekrarlayan alarm gürültüsü ve bir günlük coverage gecikmesi doğar.

**Öneri.** Cutoff'u taban bundle'ın `coverageThrough`'u ile birleştir: plan, `base.coverageThrough` ile cutoff arasındaki TÜM eksik ay arşivlerini (ve gerekli yıl indekslerini) kaynak olarak listelesin; hedef ayı kanıttan türet (o ayda henüz yayın yoksa bir önceki aya düş) ki var olmayan sayfa hiç istenmesin. Bu iki senaryo için `CalendarPlanMaterializerTests`'e ay/yıl sınırı ve çok-aylık boşluk testleri ekle.

---

### 50. promote-reviewed-bundle.sh başarı yolunda `rm -rf -- "$candidate"` yapıyor; argüman quarantine köküne/prefix'e kısıtlanmamış ve bu yıkıcı yol hiç test edilmiyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R09 — calendar-data ve calendar infrastructure |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `infrastructure/calendar/promote-reviewed-bundle.sh:71-80` |

**Bulgu.** `mv -T -n` sonrası koşulsuz `rm -rf -- "$candidate"` çalışır. `$candidate` için tek doğrulama `verify-candidate.sh` içindeki dizin/canonical/symlink/envanter/imza kontrolleridir; `SAYDIN_CALENDAR_STAGING_ROOT` altında olduğu veya `candidate-` ile başladığı hiçbir yerde kontrol edilmez. Karşılaştırma: aynı değişiklik setindeki `SecureBundleStorage.DeletePrivateTree` (SecureBundleStorage.cs:79-88) tam da bu disiplini uyguluyor (beklenen parent + `.pending-` prefix). `InfrastructureCalendarContractTests` yalnız `Assert.Contains("calendar_quarantine_candidate_removed", promotion)` yapıyor; `Promotion_SourceMutationAfterFirstVerification_...` testi sadece BAŞARISIZ yolu kapsıyor, silme davranışını hiçbir test çalıştırmıyor.

**Etki.** Geri alınamaz silme, doğrulanmış bir kök/prefix kısıtı olmadan ve sıfır davranışsal test kapsamıyla üretime giriyor. Ayrıca quarantine kopyasının adli inceleme için saklanmaması bilinçli bir retention kararı olarak runbook'ta gerekçelendirilmemiş.

**Öneri.** `rm -rf` öncesi `case "$candidate" in "$SAYDIN_CALENDAR_STAGING_ROOT"/candidate-*) ;; *) fail "candidate_not_in_quarantine" ;; esac` benzeri bir kısıt ekle (staging kökünü zorunlu parametre/env yap). Başarılı promotion + candidate temizliği için `verify-candidate.sh` stub'ı ile uçtan uca bir test ekle: target oluştu, pending yok, candidate silindi, çıktı üç satırı da içeriyor.

---

### 51. Yeni genel `IOException` catch'i tanılamayı yok ediyor; `DeletePrivateTree` catch bloğunda fırlarsa asıl acquisition hatası kayboluyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R09 — calendar-data ve calendar infrastructure |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `tools/calendar-data/src/Saydin.CalendarData/Program.cs:67-71; tools/calendar-data/src/Saydin.CalendarData/CalendarAcquisition.cs:195-200` |

**Bulgu.** `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"bundle_unreadable:{ex.GetType().Name}"); }` — hangi dosya, hangi errno, hangi aşama olduğu tamamen düşürülür; tüm IO hataları (disk dolu, mount readonly, izin) tek ve yanıltıcı `bundle_unreadable` kodu altında toplanır. Ayrıca `catch { SecureBundleStorage.DeletePrivateTree(pendingRoot, stagingRoot); throw; }` — `Directory.Delete(recursive:true)` IOException fırlatırsa (ör. dolu disk, NFS meşgul dosya) orijinal `CalendarDataException` (`source_http_status_invalid`, `tcmb_publication_evidence_regressed` vb.) atılır ve operatöre yalnız `bundle_unreadable:IOException` ulaşır.

**Etki.** Bu CLI'nin tek gözlemlenebilirlik yüzeyi bounded stdout/stderr sözleşmesidir (README'de belgeli istisna). Sözleşmenin ayırt ediciliğinin düşmesi, fail-closed davranışın teşhis edilebilirliğini doğrudan azaltır; runbook "acquisition/promotion fails" trigger'ından sonraki ilk adımı imkânsızlaştırır.

**Öneri.** `bundle_unreadable` satırına en azından ex.Message'ın path içermeyen kısaltmasını veya sabit bir aşama etiketi (`stage=copy_base|stage=write_manifest`) ekle. Temizliği `catch { try { DeletePrivateTree(...); } catch (IOException cleanup) { Console.Error.WriteLine($"staging_cleanup_failed:{cleanup.GetType().Name}"); } throw; }` biçiminde sararak orijinal hatayı koru (sessiz yutma değil, ayrı kod ile raporlama).

---

### 52. Yeni davranışların kritik negatif senaryoları test edilmiyor; "idempotent" testi günlük tekrarı doğruluyormuş izlenimi veriyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | test-quality |
| **Hat** | R09 — calendar-data ve calendar infrastructure |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `tools/calendar-data/tests/Saydin.CalendarData.Tests/CalendarPlanMaterializerTests.cs:8-32; tools/calendar-data/tests/Saydin.CalendarData.Tests/CalendarCoverageEvidenceTests.cs:8-40` |

**Bulgu.** `TcmbPlan_IsDeterministicIdempotentAndUsesOfficialPublicationCutoff` materializer'ı AYNI `beforePublication` timestamp'iyle iki kez çağırıp bayt eşitliği assert ediyor; farklı gün senaryosu (`materialized_plan_conflict`) hiç çalıştırılmıyor — R09-01 bu boşluk yüzünden kaçtı. `CalendarCoverageEvidenceTests` yalnız hafta içi (`2026-08-19`) durumunu kapsıyor; hafta sonu muafiyeti (R09-02) test edilmiyor. `VerifyCandidateBehaviorTests`'te `docker` stub'ı `case " $* " in *' --network none '*) exit 0` — testin adı "OfflineReplay" iddia etse de `--read-only`, `--user`, `--memory`, mount readonly gibi hiçbir hardening bayrağı doğrulanmıyor.

**Etki.** CI, bu hattın iki High'ını da yakalayamaz. Test isimleri kapsamı olduğundan geniş gösteriyor (tautoloji riski), bu da ikinci bir okuyucuyu yanıltır.

**Öneri.** (1) Farklı `utcNow` ile ikinci materialize çağrısı için test ekle ve beklenen davranışı (rotasyon/atomik replace) sabitle. (2) `coverageThrough` hafta sonu + aradaki hafta içi günlerin yayımlanmamış olduğu bir fixture ile fail-closed testi ekle. (3) `docker` stub'ını `--read-only`, `--user`, `--network none`, `dst=/candidate,readonly` bayraklarının hepsini isteyecek şekilde sıkılaştır veya testi `EnforcesSignatureHashesAndInventory` olarak yeniden adlandır.

---

### 53. TCMB için `SaydinTcmbCalendarCoverageExpiring` yapısal olarak sürekli firing; runbook trigger'ı bu gürültüyü normalleştiriyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R09 — calendar-data ve calendar infrastructure |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `docs/runbooks/calendar-release.md:3-4; infrastructure/prometheus/rules/ingestion.yml:72-80; src/Saydin.PriceIngestion/Repositories/IngestionFreshnessTelemetry.cs:127-139` |

**Bulgu.** `RecordCalendarHorizon("tcmb_indicative_fx", ..., yesterday: true)` → `horizon = coverageThrough - (Istanbul today - 1)`. TCMB takvimi tanımı gereği geriye dönüktür; en sağlıklı durumda `coverageThrough == dün` yani `horizon == 0`. Alarm ifadesi ise `horizon{tcmb_indicative_fx} < 45 and >= 0` — yani takvim mükemmel durumdayken bile 15 dk sonra warning firing'e geçer ve hiç iyileşmez. Runbook bu değişiklikte trigger'ı "TCMB warning horizon ... is below 45 days" olarak yeniden yazarak bu durumu meşru bir tetikleyici gibi tarif ediyor.

**Etki.** Kalıcı warning → alarm yorgunluğu; "40 alert envanteri geçti" tipi kapılar bunu yakalamaz çünkü ifade sözdizimsel olarak geçerlidir. Runbook–kural–metrik üçlüsü arasında anlamsal uyumsuzluk var. (Not: prometheus kuralı bu hattın dosya listesinde değil; bulgu runbook değişikliği üzerinden tespit edildi, monitoring hattıyla birleştirilebilir.)

**Öneri.** `SaydinTcmbCalendarCoverageExpiring`'i TCMB için kaldır (geriye dönük takvimde "expiring horizon" kavramı yok) veya eşiği TCMB semantiğine uygun hale getir (ör. `horizon < -1` warning, `< -2` critical). Runbook trigger cümlesini yalnızca BIST için 45 gün, TCMB için "son kanıtlı yayın gününü içermiyor" olacak şekilde ayır.

---

### 54. Base backup süresince physical-probe kilidi tutulduğu için off-host WAL yüklemesi tamamen duruyor — 15 dk RPO her gün base yedeği kadar ihlal ediliyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | data-integrity |
| **Hat** | R10 — infrastructure/backup |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `infrastructure/backup/backup-entrypoint.sh:433-443 (base_backup), 583-586 ve 616-623 (wal_stream defer/continue)` |

**Bulgu.** `base_backup` satır 433'te `acquire_physical_probe_lock` alıyor ve kilidi ancak satır 443'te, `pg_basebackup` tamamlandıktan SONRA bırakıyor (managed login'in CONNECTION LIMIT 2 sınırı nedeniyle kasıtlı). WAL döngüsünde satır 583-586: `if ! try_acquire_physical_probe_lock; then printf backup_wal_highwater_probe_deferred; continue; fi`. `continue` yalnız metriği değil, satır 624-652'deki observation yazımı, `restic ... backup "$spool"`, `write_wal_recovery_metric`, watermark ilerlemesi ve satır 654'teki yerel retention `find`'ını da atlıyor. Yani base yedeği boyunca off-host'a HİÇ WAL gitmiyor. `write_failure_metric` de çalışmıyor (die yok, sadece continue).

**Etki.** Gerçek recovery point base yedeği süresi + 5 dk kadar geriler → README/manifest'in 15 dk RPO taahhüdü günde bir kez ihlal edilir. Süre ~45 dakikayı aşarsa `SaydinWalBackupStale` (>1800s, for 15m) her gün critical sayfa üretir; operatör bunu "normal base penceresi" diye görmezden gelmeye alışırsa gerçek WAL kesintisi de kaçar. Önceki review'in M34 bulgusunun ("15 dk RPO garanti edilmiyor") base penceresi için hâlâ açık olduğu anlamına gelir.

**Öneri.** `continue` yerine kilit alınamadığında "probe'suz yükleme" yoluna düş: observation marker'ı yazmadan ve `saydin_backup_last_success_timestamp_seconds{kind="wal"}` metriğini ilerletmeden `restic backup "$spool"` + watermark + retention adımlarını yine de çalıştır (veriyi off-host'a taşı, tazelik iddiasını yükseltme). Alternatif olarak base backup için ayrı bir 3. bağlantı bütçesi (CONNECTION LIMIT 3) ayır ve probe kilidini yalnız IDENTIFY_SYSTEM/SHOW süresince tut. `infrastructure/backup/README.md`'ye base penceresinde ne olduğunu açıkça yaz ve rules.test.yml'ye bu senaryo için bir vaka ekle.

---

### 55. Yeni WAL high-water probe'unda (ve pg_receivewal/pg_basebackup çağrılarında) hiçbir wall-clock timeout yok; asılı bir bağlantı paylaşılan kilidi tutarak her iki yedek hattını da kalıcı durduruyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R10 — infrastructure/backup |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `infrastructure/backup/backup-entrypoint.sh:583-601 (probe), 186-196 (verify_physical_authentication), 436-442 (pg_basebackup), 155-162 (acquire_physical_probe_lock)` |

**Bulgu.** Satır 588-596'daki iki `psql -X -A -t --no-password --dbname="host=$PGHOST port=$PGPORT user=$PGUSER dbname=postgres replication=true"` çağrısında ne `connect_timeout=` ne `-c 'SET statement_timeout'` ne de harici `timeout(1)` sarmalayıcısı var; compose'daki env'de de `PGCONNECT_TIMEOUT` yok (compose.production.yml:850-878). libpq varsayılanı sonsuz bekleme, TCP keepalive Linux varsayılanı ~2 saat. Bu çağrılar `try_acquire_physical_probe_lock` başarıyla döndükten SONRA yapılıyor, yani kilit askıda kalan bağlantı boyunca tutuluyor. Aynı şekilde `verify_physical_authentication`'daki `pg_receivewal --create-slot` ve satır 436'daki `pg_basebackup` de zamansız. `base_backup` ise satır 155-162'de kilidi en fazla 7200 s bekliyor, sonra 75 ile ölüyor.

**Etki.** WAL döngüsü ilk probe'ta süresiz bloke olur: off-host WAL yüklemesi tamamen durur, `write_failure_metric` hiç yazılmaz, container `restart: unless-stopped` altında yeniden başlamaz (süreç yaşıyor). Paralel olarak base scheduler 2 saat kilidi bekler, `backup_physical_probe_lock_timeout` (75) ile döner ve backoff döngüsüne girer — yani her iki yedek hattı da kendiliğinden toparlanmaz. Tek sinyal 30-45 dk sonra gelen `SaydinWalBackupStale`/`SaydinBaseBackupStale`.

**Öneri.** Tüm PG istemci çağrılarına `PGCONNECT_TIMEOUT=10` (env) ve psql probe'larına `-c 'SET statement_timeout=...'` yerine harici `timeout -s TERM 30 psql ...` sarmalayıcısı ekle; `pg_receivewal --create-slot` ve `pg_basebackup` için de üst sınır belirle (`timeout` + `run_tracked`). Probe zaman aşımında kilidi bırak, `backup_wal_highwater_probe_unavailable` sayacını metrik olarak yayınla ve N ardışık başarısızlıkta `write_failure_metric wal` yaz.

---

### 56. WAL spool'unda kalan geçici dosya (`.saydin-wal-observation.$$` / `.last-offhost-segment.$$`) sonraki TÜM restore drill'lerini kalıcı olarak düşürüyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R10 — infrastructure/backup |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `infrastructure/backup/backup-entrypoint.sh:624-631, 641-648, 632-635 (exclude listesi), 405-409 (backup_exit temizlik listesi), 654-657 (yerel retention); infrastructure/backup/wal-recovery-evidence.py:159-176` |

**Bulgu.** Marker `printf ... > "$spool/.saydin-wal-observation.$$"` ile yazılıp `mv` ile yayınlanıyor; watermark için de `$spool/.last-offhost-segment.$$` kullanılıyor. `backup_exit`'in `rm -f` listesi (satır 405-409) yalnız `/tmp` altındaki dosyaları siliyor, spool'daki geçici dosyaları değil; satır 654'teki `find ... -name '????????????????????????' -o -name '????????.history'` de bunları eşlemiyor; `restic backup` exclude'ları (`*.partial`, `.restic-cache`, `.last-offhost-segment`) basename tam eşleşmesi olduğu için `.last-offhost-segment.1234` ve `.saydin-wal-observation.1234` snapshot'a dahil oluyor. Restore tarafında `wal-recovery-evidence.py:169-176` bilinmeyen her dosya adı için `wal_restore_entry_invalid` fırlatıyor.

**Etki.** Bir kez oluştuktan sonra her `saydin-backup restore` çağrısı `restore_wal_recovery_evidence_invalid` (78) ile düşer → aylık drill kalıcı kırmızı → `promote-production.yml` ≤31 günlük imzalı restore receipt istediği için üretim promotion'ı da bloke olur. Fail-closed ama kurtarma yolu hiçbir runbook'ta yazılı değil ve hata mesajı hangi dosyanın sorunlu olduğunu söylemiyor.

**Öneri.** Geçici dosyaları spool dışına (`/tmp`) yaz ve atomik `mv` için aynı dosya sistemi gerekiyorsa sabit tek bir isim kullan (`.saydin-wal-observation.tmp`) ve döngü başında koşulsuz sil. Ek olarak restic exclude'larını `--exclude='.saydin-wal-observation.*' --exclude='.last-offhost-segment*'` ile genişlet ve `wal-recovery-evidence.py`'de reddedilen dosya adını hata mesajına ekle. `base-backup-behavior-smoke.py`'ye "spool'da artık temp varken drill hâlâ geçerli" negatif vakası ekle.

---

### 57. Yerel WAL spool'u 14 gün tutuluyor ama hiçbir kapasite güvencesi/boyutlandırma yok; yeni `archive_timeout=300s` yerel WAL hacmini günde ~4,5 GiB'a çıkarıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R10 — infrastructure/backup |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `infrastructure/backup/backup-entrypoint.sh:531-534,654-657; infrastructure/deployment/compose.production.yml:20 (archive_timeout=300s), 875 (backup_wal_spool:/work/wal), 921; infrastructure/backup/README.md:69-98; infrastructure/deployment/production.env.example:107` |

**Bulgu.** `archive_timeout=300s` her 5 dakikada bir (WAL etkinliği varsa) segment döndürüyor ve pg_receivewal tamamlanan her segmenti tam 16 MiB olarak yazıyor — bunu projenin kendi testi kanıtlıyor (`archive-timeout-receiver-smoke.py:196-198`: `size != 16 * 1024 * 1024` → hata). Yerel retention satır 654-657'de `-mtime +14 -delete`, yani 288 segment/gün × 16 MiB × 14 gün ≈ 63 GiB üst sınır. Bu retention işlevsel olarak zorunlu: restore yolu yalnız EN SON wal-observation snapshot'ını geri yüklüyor (backup-entrypoint.sh:697-708), dolayısıyla 7 gün öncesine PITR için o snapshot'ın içeriğinde 7 günlük segment bulunması gerekiyor. Buna karşılık base staging için `SAYDIN_BACKUP_BASE_STAGING_MIN_FREE_BYTES >= 8589934592` zorunlu bir `df` kontrolü var (satır 369-385); WAL spool'u için hiçbir `df`/mountpoint/kapasite kontrolü yok. `configure_cache "$spool/.restic-cache"` (satır 534) restic cache'ini de aynı volume'e koyuyor. production.env.example ve deployment/README bu volume için hiçbir boyut önermiyor.

**Etki.** Spool volume/host diski dolduğunda `pg_receivewal` yazamaz ve WAL akışı durur; slot `max_slot_wal_keep_size=8GB` sınırına ulaşınca geçersizleşir ve README'nin kendi ifadesiyle "kurtarma zinciri geçersiz" hale gelir. Base staging için titizlikle konulan kapasite kapısının WAL tarafında hiç bulunmaması, en kritik (RPO taşıyan) hattı korumasız bırakıyor. Tek koruma `SaydinHostDiskPressure` (%15).

**Öneri.** `wal_stream` başlangıcına base staging ile simetrik bir kontrol ekle: mountpoint + tmpfs reddi + `SAYDIN_BACKUP_WAL_SPOOL_MIN_FREE_BYTES` zorunlu `df` eşiği (ör. ≥96 GiB) ve eşik altına inildiğinde `write_failure_metric wal` + fail-closed. Restic cache'ini spool volume'ünden ayır. `infrastructure/backup/README.md` ve `production.env.example`'a beklenen yerel WAL hacmini (`86400/archive_timeout × wal_segment_size × 14`) formülüyle yaz ve `saydin_backup_wal_spool_free_bytes` metriği + alarm ekle.

---

### 58. Backup SQL-deny kabul kapısı neden-özgüllüğünü kaybetti ve statik test eski iddiayı geri eklemeyi kalıcı olarak yasaklıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | test-quality |
| **Hat** | R10 — infrastructure/backup |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `.github/scripts/run-backup-auth-tests.sh:69-74 (silinen satırlar: eski 63-64 ve 76); infrastructure/backup/tests/backup-static-self-test.py:230-236 (`locale_independent_backup_auth`)` |

**Bulgu.** Diff iki negatif iddiayı siliyor: `! grep -Eqi 'fatal|password authentication failed|no pg_hba.conf entry' "$log"` ve `grep -q 'pg_hba.conf rejects connection' "$log" || die "backup_auth_sql_rejection_not_hba"`. Kalan kontrol yalnız `if psql ... 'SELECT 1'; then die backup_auth_sql_access_allowed; fi` — yani psql'in HERHANGİ bir nedenle başarısız olması "sql-deny geçti" olarak sayılıyor (`SAYDIN_TARGET_DATABASE` yanlış, DNS/port hatası, SSL uyuşmazlığı, sunucu kapalı). Üstüne `backup-static-self-test.py:230-234` artık bu iki metnin hem entrypoint'te hem acceptance script'inde BULUNMAMASINI zorunlu kılıyor, yani düzeltme "iddiayı testten çıkarıp testi bu çıkarmayı doğrulayacak şekilde yeniden yazmak" biçiminde yapılmış. Silmenin gerekçesi (`03-findings-low.md:116`, L108 — lokalize edilebilir sunucu mesajı) meşru, ama yerine lokale bağımlı olmayan hiçbir neden kontrolü konmamış; `base-backup-behavior-smoke.py`'deki sahte psql yalnız Türkçe mesajın kabul edildiğini kanıtlıyor, reddin HBA kaynaklı olduğunu değil.

**Etki.** "Backup login'in ordinary SQL erişimi kapalı" güvencesi artık CI'da kanıtlanmıyor, yalnız "psql başarısız oldu" gözleniyor. Bu, README'nin ve deployment admission'ının en çok güvendiği ayrıcalık sınırının kanıt değerini düşürür ve statik kural nedeniyle kolayca geri alınamaz hale gelir.

**Öneri.** Lokale bağımsız bir neden kanıtı ekle: (a) aynı koşuda başka bir managed login'in (ör. audit) aynı veritabanına başarıyla bağlanabildiğini göster (differential kanıt — ağ/DB adı sorununu eler), ve/veya (b) sunucu tarafı log'unda `connection_rejected`/SQLSTATE 28000 satırını `log_min_messages` üzerinden doğrula, ve/veya (c) `manage_backup_hba.py --verify` çıktısını aynı adımda kapıya bağla. `locale_independent_backup_auth` anahtarını "lokalize metin yok VE neden-özgül kanıt var" olarak yeniden yaz.

---

### 59. `recoveryTargetReached` receipt alanı sabit `True` yazılıyor ve üretim promotion kapısı bu sabiti kanıt sayıyor; statik test de sabiti şart koşuyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | test-quality |
| **Hat** | R10 — infrastructure/backup |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `infrastructure/backup/restore-drill.sh:357-363 (`"recoveryTargetReached":True`); .github/workflows/promote-production.yml:109 (`assert receipt["recoveryTargetReached"] is True`); infrastructure/backup/tests/backup-static-self-test.py:137-146,272-273` |

**Bulgu.** Drill, receipt'e `"recoveryTargetReached":True` sabitini gömüyor; hiçbir ölçümden türetilmiyor. Promotion kapısı `assert receipt["recoveryTargetReached"] is True` diyor — yani sabiti doğruluyor. Statik self-test `restore_recovery_contract` içinde literal `'"recoveryTargetReached":True'` metninin varlığını zorunlu kılıyor (satır 140,145), mutasyon testi ise yalnız `SELECT NOT pg_is_in_recovery();` ifadesinin `SELECT true;` ile değiştirilmesini yakalıyor. Gerçek kanıt yalnız dolaylı: `recovery_target_action=promote` altında hedefe ulaşılamazsa PostgreSQL FATAL verir. Buna ek olarak `lastReplayedTransactionAt` boş olabiliyor (restore-drill.sh:280 `""` kabul ediliyor) ve promotion `transaction is None` durumunu sınırsız kabul ediyor (satır 110-114), yani "restore edilen küme gerçekten hedefe kadar replay etti" için hiçbir sayısal kanıt yok.

**Etki.** İmzalı DR kanıtının başlık alanı ölçüm değil sabit; kapı yeşil ama bilgi taşımıyor. Drill hiçbir veri düzeyi değişmezi (ör. `price_points` içinde hedef zamandan önceki bir satırın varlığı, `max(price_date)`) doğrulamadığı için "şema/kimlik doğru ama içerik boş/eksik" bir restore hâlâ geçebilir.

**Öneri.** Alanı ölçümden türet: PostgreSQL log'undaki `recovery stopping before/after ...` satırını veya `pg_last_xact_replay_timestamp()`/`pg_last_wal_replay_lsn()` ile `targetTime` ilişkisini receipt'e yaz ve promotion'da `lastReplayedTransactionAt <= targetTime` + üst sınır kontrolü yap (boş olması yalnız gerçekten sessiz bir hedef için gerekçelendirilsin, ayrı bir `quietTarget: true` bayrağıyla). Ek olarak drill'e küçük bir veri değişmezi sorgusu ekle (ör. `SELECT count(*)>0 FROM price_points WHERE price_date <= <target>::date`) ve sonucunu receipt'e/kapıya bağla.

---

### 60. Docker yokken üç davranış smoke'u sessizce "pass" sayılıyor; statik self-test hem skip'i hem tutarsız skip kodlarını gizliyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | test-quality |
| **Hat** | R10 — infrastructure/backup |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `infrastructure/backup/tests/backup-static-self-test.py:296-308,365-385; infrastructure/backup/tests/restic-wal-observation-smoke.py:25-29; infrastructure/backup/tests/archive-timeout-receiver-smoke.py:58-61; infrastructure/backup/tests/base-backup-behavior-smoke.py:127-130; infrastructure/backup/tests/restore-volume-init-smoke.py:44-46` |

**Bulgu.** `restic_wal_observation_behavior`, `restore_cleanup_behavior` ve `archive_timeout_receiver_behavior` koşulsuz çağrılıyor ve yalnız `returncode == 0` bakılıyor. Bu smoke'ların ikisi docker yoksa `print("..._skipped:docker_unavailable"); return 0` yapıyor — yani docker'sız bir ortamda üç davranış kapısı da vakumsal olarak geçiyor ve script yine `backup_static_self_test_passed:N` basıyor. Skip sözleşmesi tutarsız: `restore-volume-init-smoke.py` skip'te 77, diğer üçü 0 döndürüyor; 77 zaten hiç kullanılmıyor çünkü o smoke yalnız docker varken çağrılıyor. CLAUDE.md ise integration kabul için açıkça "zero-skip gate, total=executed=passed" istiyor.

**Etki.** "Backup statik + davranış kapısı geçti" çıktısı, gerçek PostgreSQL/restic davranışının hiç doğrulanmadığı bir koşuda da üretilir; bu, tam olarak bu review'in aradığı "yeşil ama anlamsız" sinyaldir. Ayrıca gerçek bir CI hatasında (bkz. R10-01) hangi smoke'un skip'lendiği çıktıdan anlaşılamıyor.

**Öneri.** Tek bir skip sözleşmesi belirle (ör. 77) ve `backup-static-self-test.py`'de skip'i açıkça ele al: docker beklenen ortamda (CI) skip'i HATA say (`SAYDIN_REQUIRE_DOCKER_SMOKES=1` env ile), yerelde açıkça "skipped" olarak listele ve final satırında `passed=N skipped=M` yayınla. `.github/workflows/ci.yml`'de `SAYDIN_REQUIRE_DOCKER_SMOKES=1` ver.

---

### 61. `restore` alt komutu yalnız drill düzenine sabitlenmiş; gerçek felaket kurtarma için ne script ne de adım adım runbook var (RTO 120 dk iddiasına rağmen)

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R10 — infrastructure/backup |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `infrastructure/backup/backup-entrypoint.sh:680-720 (`/restore-drill/wal-recovery-evidence.json`, `canonical=/restore-drill/pgdata`, `DISPOSABLE_RESTORE_ONLY`); infrastructure/backup/prepare-recovery.sh:7; docs/runbooks/backup-failure.md:1-24` |

**Bulgu.** `restore_snapshot` kanıt dosyasını sabit `/restore-drill/wal-recovery-evidence.json`'a, PGDATA'yı sabit `/restore-drill/pgdata`'ya yazıyor; `prepare-recovery.sh:7` `[ "$data" = /restore-drill/pgdata ]` dışındaki her yolu 78 ile reddediyor; `SAYDIN_RESTORE_CONFIRM` yalnız `DISPOSABLE_RESTORE_ONLY` kabul ediyor ve `restore_target_guard.py` hedefi `<root>/work` leaf'ine kısıtlıyor. `docs/runbooks/backup-failure.md` gerçek kurtarma için yalnız düzyazı ("restore the base, replay WAL to the selected target timestamp") veriyor; tek bir komut, tek bir dosya yolu, tek bir hedef-volume adı yok. Bu runbook bu değişiklik setinde hiç güncellenmemiş (git status'ta yok), oysa yeni hata kodları (`restore_wal_recovery_point_stale`, `backup_wal_receiver_not_caught_up`, `backup_wal_highwater_probe_unavailable`, `backup_base_staging_capacity_insufficient`, `backup_physical_probe_lock_timeout`, `backup_repository_prune_deferred`) `SaydinBackupFailure`'ın runbook_url'i olan bu dosyada hiç geçmiyor.

**Etki.** Aylık drill mükemmel otomatize edilmiş, ama gerçek kurtarma tamamen prova edilmemiş elle bir iş olarak kalıyor; 120 dakikalık RTO taahhüdü hiçbir zaman ölçülmüyor. Ayrıca alarm→runbook zinciri kopuk: en olası yeni hata kodlarının hiçbiri işaret edilen runbook'ta yok.

**Öneri.** (a) `restore` alt komutunu parametrik hale getir: kanıt/PGDATA hedeflerini `SAYDIN_RESTORE_TARGET` altına taşı ve `SAYDIN_RESTORE_CONFIRM` için ikinci bir onay değeri (`PRODUCTION_RECOVERY_APPROVED_<incident-id>`) + ayrı bir hedef guard'ı tanımla; `prepare-recovery.sh`'in sabit yol kontrolünü de buna göre gevşet (yine de üretim volume'ünü reddederek). (b) `docs/runbooks/backup-failure.md`'ye yeni hata kodları tablosu (kod → anlam → ilk aksiyon) ve gerçek kurtarma için birebir kopyalanabilir komut dizisi ekle. (c) drill'de ölçülen uçtan uca süreyi receipt'e `elapsedSeconds` olarak yaz ve RTO'ya karşı raporla.

---

### 62. Yeni high-water probe'unun gerçek PostgreSQL/HBA yolu (replication modunda psql) hiçbir kapıda koşmuyor; tüm off-host WAL akışı bu yola bağlı

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | test-quality |
| **Hat** | R10 — infrastructure/backup |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `infrastructure/backup/backup-entrypoint.sh:587-601; .github/scripts/run-backup-auth-tests.sh:41-74; infrastructure/backup/tests/base-backup-behavior-smoke.py:385-392 (sahte psql); infrastructure/backup/manage_backup_hba.py:74-83` |

**Bulgu.** WAL yüklemesi artık `psql --dbname="host=... user=... dbname=postgres replication=true" -c IDENTIFY_SYSTEM` ve `-c 'SHOW wal_segment_size'` başarısına koşulsuz bağlı (başarısızlıkta `continue` → hiç yükleme yok). `run-backup-auth-tests.sh` — "Required physical-protocol acceptance" gate'i — yalnız `pg_receivewal` ve `pg_basebackup`'ı gerçek sunucuya karşı koşuyor; replication modunda psql'i hiç denemiyor. `base-backup-behavior-smoke.py`'deki psql sahte bir shell script (satır 385-392) ve `saydin-wal-highwater` de sabit çıktı veren sahte bir script (satır 394-396). `manage_backup_hba.py:80-82` kuralları (`hostssl replication <role> <cidr> scram-sha-256` ardından `host all <role> ... reject`) okundu — teoride physical walsender `replication` anahtar sözcüğüyle eşleşiyor, ama bu hiçbir yerde gerçek sunucuya karşı doğrulanmıyor.

**Etki.** Yükleme zinciri sessizce durur: her döngü `backup_wal_highwater_probe_unavailable` yazıp `continue` eder, `write_failure_metric` hiç çalışmaz, `SaydinBackupFailure` tetiklenmez; tek sinyal 45 dakika sonra gelen `SaydinWalBackupStale`'dir. Deploy kapısı bunu önceden yakalayamaz çünkü kabul testi bu yolu hiç koşmuyor.

**Öneri.** `run-backup-auth-tests.sh`'ye üçüncü bir pozitif adım ekle: aynı credential ile `psql --dbname="host=$PGHOST port=$PGPORT user=$PGUSER dbname=postgres replication=true" -c IDENTIFY_SYSTEM` ve `SHOW wal_segment_size` çalıştır, çıktıyı `saydin-wal-highwater` ile gerçek segment adına çevirip spool'daki gerçek segmentle karşılaştır. Bunu `deploy-release.sh`'in `verify-auth` adımına da bağla (`verify_auth` şu an yalnız pg_receivewal + SQL-deny doğruluyor).

---

### 63. Canlı metric label admission'ı zaman-sınırsız `/api/v1/series` sorgusuna dayanıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R11 — Deployment, Prometheus, Alertmanager, OTEL |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/release/deploy-release.sh:341-343; infrastructure/deployment/validate-prometheus-runtime.py:74-92; infrastructure/deployment/compose.production.yml (prometheus_data: {external: true})` |

**Bulgu.** Kapı gerçekten 'retention penceresinde bir zamanlar bu ad ve label seti vardı' anlamına geliyor; 'şu anda canlı olarak üretiliyor' anlamına gelmiyor. En kritik boşluk activity-log sayaç ailesinde, çünkü bu ailenin adı/label'ı kaybolursa devreye girecek bir `absent()` alert'i yok.

**Etki.** Metric sözleşmesi bozan bir sürüm, kalıcı TSDB'de eski seri durduğu sürece 'prometheus_runtime_accepted' alır ve receipt 'passed' imzalanır; activity-log kaybı alarmı sessizce boş vektöre düşer.

**Öneri.** Sorguya dar bir `&start=$(date +%s)-300&end=$(date +%s)` penceresi ekle veya `/api/v1/query` üzerinden `count by (job,...) (last_over_time(metric[2m]))` kullan; validate-prometheus-runtime.py'ye tazelik koşulunu taşı ve monitoring-runtime-self-test.py'ye 'yalnız eski örnekli seri → reddedilmeli' mutasyonunu ekle. Ayrıca activity-log sayaç ailesi için `absent()` tabanlı bir kural düşünülmeli.

---

### 64. Rendered Alertmanager config kapısı yalnız substring arıyor; README'nin iddia ettiği anlamsal kontrolleri yapmıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R11 — Deployment, Prometheus, Alertmanager, OTEL |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/deployment/validate-private-material.py:138-150; infrastructure/alertmanager/README.md:8-12; infrastructure/deployment/private-material-self-test.py:54-88` |

**Bulgu.** Kapı 'eksik watchdog route', 'placeholder URL' ve 'non-HTTPS URL' durumlarını gerçekten reddediyor; ancak 'send_resolved içermeyen receiver' ve 'watchdog route'unun gerçekten bir dakikalık repeat_interval ile external-watchdog'a bağlı olması' iddiaları karşılanmıyor. README bu iki noktada implementasyondan fazlasını iddia ediyor.

**Etki.** Dead-man's-switch'in tek otomatik kapısı kısmen boş; heartbeat yanlış aralıkta veya resolve bildirimi olmadan gidebilir. README'yi okuyan operatör kapının kendisini koruduğunu sanarak manuel doğrulamayı atlayabilir.

**Öneri.** Validator'ı minimal bir YAML okuyucuyla anlamsal hale getir: watchdog matcher'lı route'un `receiver`ının `external-watchdog` olması, o route'un `repeat_interval <= 1m` olması, `external-watchdog` receiver'ının en az bir `webhook_configs` girdisinde `send_resolved: true` bulunması ve watchdog URL host'unun operator-critical/warning host'larından farklı olması ayrı ayrı doğrulansın. private-material-self-test.py'ye bu dört durum için birer mutasyon ekle; README iddiasını ancak o zaman bırak.

---

### 65. Deploy, rule/target envanterini doğruluyor ama watchdog alert'inin Alertmanager'a ulaştığını hiç kontrol etmiyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R11 — Deployment, Prometheus, Alertmanager, OTEL |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/release/deploy-release.sh:277-296, 336-349; infrastructure/prometheus/rules/tls-runtime.yml:73-78` |

**Bulgu.** Kural değerlendirme tarafı (rule health + envanter eşitliği) artık kanıtlanıyor; Prometheus→Alertmanager teslimatı ve route eşleşmesi kanıtlanmıyor ve tamamen manuel promotion prosedürüne bırakılmış. Otomatikleştirilebilir bir kontrol insan adımı olarak kalmış.

**Etki.** Watchdog matcher'ı yanlış yazılırsa veya notification yolu bozulursa deploy 'passed' imzalanır ama hiçbir heartbeat dışarı çıkmaz; dead-man's-switch'in kendisi sessizce ölü olur.

**Öneri.** Monitoring runtime kapısına ekle: (a) `wget -qO- 'http://alertmanager:9093/api/v2/alerts?filter=alertname%3D%22SaydinWatchdog%22'` yanıtında tam bir aktif alert; (b) `/api/v2/alerts/groups?receiver=external-watchdog` içinde SaydinWatchdog'un görünmesi; (c) `prometheus_notifications_dropped_total` / `prometheus_notifications_errors_total` değerlerinin deploy penceresinde artmamış olması. Üçü de mevcut `until` döngüsüne oturur.

---

### 66. 11 target job envanteri yalnız Python sabitinde; prometheus.production.yml ile statik olarak bağlanmamış

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | developer-experience |
| **Hat** | R11 — Deployment, Prometheus, Alertmanager, OTEL |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/deployment/validate-prometheus-runtime.py:13-16; infrastructure/prometheus/prometheus.production.yml:17,23,27,31,35,39,43,47,51,66,70; infrastructure/deployment/validate-observability.py:184-200; infrastructure/deployment/monitoring-runtime-self-test.py:32-42` |

**Bulgu.** İddia doğru. Ayrıca hatanın deploy'un en sonunda — monitoring, API ve Caddy zaten force-recreate/başlatılmışken — ortaya çıkması etkiyi keskinleştiriyor: yarım uygulanmış bir release ve manuel rollback gerektiriyor.

**Etki.** Ucuz bir statik kontrolle CI'da yakalanabilecek bir tutarsızlık, en pahalı noktada (canlı deploy'un sonunda) ortaya çıkıyor.

**Öneri.** validate-observability.py'ye prometheus.production.yml'deki `job_name` kümesinin `validate-prometheus-runtime.EXPECTED_JOBS` ile eşit olduğunu doğrulayan bir kapı ekle (kopyalayarak değil import ederek) ve observability-self-test.py'ye 'job eklendi/silindi → reddedilmeli' mutasyonu koy. `required_labels` anahtarlarından deploy-release.sh'in `match[]` regex'ini türet ya da regex'i validator'a taşı.

---

### 67. Ingestion kapalı ortamlarda iki critical alert kalıcı olarak firing kalıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R11 — Deployment, Prometheus, Alertmanager, OTEL |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/prometheus/rules/ingestion.yml:4-10, 56-62; infrastructure/release/deploy-release.sh:317-334; infrastructure/alertmanager/alertmanager.template.yml:18-20; .github/workflows/deploy-staging.yml:58` |

**Bulgu.** İddia doğru: ingestion kapalıyken tam olarak iki critical alert kalıcı firing olur ve operator-critical receiver'ına yarım saatte bir gider.

**Etki.** Critical route'un sinyal/gürültü oranı bozulur; ekip critical bildirimleri susturmayı öğrenirse gerçek backup/API/activity-log critical'ları kaçırılır. Alternatif, sona erdiğinde kimsenin fark etmeyeceği kalıcı bir silence'tır.

**Öneri.** Kuralları ortama göre yükle (ör. `rules/optional-ingestion/` dizinini yalnız ingestion açıkken mount et ve validate-prometheus-runtime'ın beklenen alert envanterini aynı koşula bağla) ya da ifadeleri bir 'ingestion beklenir mi' sinyaline bağla. En azından deploy-release.sh ingestion kapalıyken bu iki alert için süreli bir Alertmanager silence açsın ve runbook bunu belgelesin.

---

### 68. Monitoring düzleminin kendi sağlığı hiçbir alert tarafından izlenmiyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R11 — Deployment, Prometheus, Alertmanager, OTEL |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/prometheus/rules/*.yml (40 alert); infrastructure/deployment/validate-prometheus-runtime.py:39-52; infrastructure/prometheus/prometheus.production.yml:66-72` |

**Bulgu.** İddia doğru. SaydinWatchdog yalnız 'tüm zincir ölü' durumunu yakalar; 'tek kural unhealthy' veya 'tek receiver 5xx' durumunda watchdog kendi route'undan teslim edilmeye devam ettiği için hiçbir sinyal üretilmez.

**Etki.** Backup, activity-log ve API critical'ları, monitoring düzlemi kısmen bozukken sessizce kaybolabilir; deploy anındaki tek seferlik rule-health kontrolü bunu kapatmaz.

**Öneri.** `rules/monitoring-self.yml` ekle: `increase(prometheus_rule_evaluation_failures_total[10m]) > 0`, `increase(prometheus_notifications_errors_total[10m]) > 0`, `increase(prometheus_notifications_dropped_total[10m]) > 0`, `increase(alertmanager_notifications_failed_total[15m]) > 0`, `increase(prometheus_tsdb_compactions_failed_total[1h]) > 0`. Her biri için inventory.test.yml'e pozitif + negatif test ve telemetry-pipeline.md'ye bir bölüm ekle.

---

### 69. Denetim bütünlüğü sinyali üreten `saydin_activity_log_data_truncations_total` hiçbir alert tarafından tüketilmiyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R11 — Deployment, Prometheus, Alertmanager, OTEL |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Shared/Diagnostics/SaydinMetrics.cs:161-179; src/Saydin.Api/Helpers/ActivityLogBuilder.cs:114; infrastructure/prometheus/rules/api.yml:38-45` |

**Bulgu.** Bulgunun asıl geçerli yarısı truncation sayacıdır: kodun kendi yorumu bu sayacı 'sessiz kaybın tek göstergesi' olarak tanımlıyor ama hiçbir kural onu tüketmiyor. Security admission oranı için alert yokluğu bir kusur değil, savunulabilir bir tercihtir; öneriden çıkarılmalı ya da açık bir muafiyet olarak yazılmalıdır.

**Etki.** JSONB boyut sınırını aşan bir payload sürümü activity_logs `data` alanını sessizce placeholder'a düşürür; ADR-006 denetim izi eksik yazılır ve bu yalnız log arkeolojisiyle fark edilir.

**Öneri.** `increase(saydin_activity_log_data_truncations_total{job="saydin-api"}[30m]) > 0` (warning, activity-logging runbook'una bağlı) kuralını ekle ve inventory.test.yml'e pozitif+negatif test yaz. Daha kalıcısı: validate-observability.py'ye 'SaydinMetrics.cs'deki her metrik ya bir alert ifadesinde ya da yazılı bir muafiyet listesinde geçmeli' kapısı — security admission metriği o listeye gerekçesiyle girsin.

---

### 70. API metrikleri Prometheus'a iki yoldan da giriyor; ikinci kopyanın tüketicisi yok, filtrelenmiyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | performance |
| **Hat** | R11 — Deployment, Prometheus, Alertmanager, OTEL |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Program.cs:102-112; infrastructure/otel/otel-collector.production.yml:46-56, 95-98; infrastructure/prometheus/prometheus.production.yml:17-29` |

**Bulgu.** İddia doğru ve nüansı şudur: yolun kendisi ingestion için zorunlu, gereksiz olan yalnız API metriklerinin ikinci kopyasıdır. Ayrıca collector'ın prometheus exporter'ı `job`/`instance` etiketlerini resource attribute'lardan türettiği için Prometheus tarafında `exported_job` ile ayrışırlar — yani karışıklık değil, saf tekrar söz konusudur.

**Etki.** API metriklerinin (http_server_request_duration histogram bucket'ları ve .NET runtime serileri dâhil) seri sayısı ve scrape yükü iki katına çıkıyor; job filtresini unutan ad-hoc sorgular iki kat sonuç veriyor. docs/architecture/observability.md:26 ikinci yolu 'bilinçli' diyor ama ikisinin de metrik taşımasını gerekçelendirmiyor.

**Öneri.** API'nin metrics pipeline'ından `AddOtlpExporter`'ı kaldır (trace/log OTLP'de kalsın) veya collector metrics pipeline'ına `service.name=saydin-api` kaynaklı metrikleri düşüren bir `filter` processor'ü ekle. Seçimi observability.md'de netleştir ve validate-observability.py'ye karşılık gelen statik kontrolü koy.

---

### 71. Release tedarik zincirinin statik kapısı (`validate-release.py`) ve CI-admission self-test'i yalnız release workflow'unda koşuyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R12 — Release supply chain, CI workflow, kapılar |
| **Doğrulama** | CONFIRMED |
| **Konum** | `.github/workflows/release-images.yml:56-57 (tek çağrı yeri); karşı taraf: .github/workflows/ci.yml:96-131` |

**Bulgu.** Olgular doğru ama sınıflandırma fazla ağır. Bu bir 'defect/High' değil, shift-left eksikliği: regresyon release anında fail-closed yakalanıyor (release-images.yml:46 'Fail-closed static and mutation gates' adımı build/imza öncesi koşuyor), yani yanlış bir deploy üretilmiyor. Kayıp, geri bildirimin PR'dan release penceresine kaymasıdır.

**Etki.** deploy-release.sh'e tekrar inline runtime-image sözlüğü konması veya monitoring admission sırasının bozulması PR CI'ında görünmez; hata ancak self-hosted release runner'da release başlatıldığında ortaya çıkar ve release penceresini yakar. Orijinal Critical'ın tekrar oluşma yolu 'merge edilebilir' kalır.

**Öneri.** `production-assurance` job'ına iki satır ekle: `python3 infrastructure/release/validate-release.py` ve `python3 .github/scripts/test-verify-release-ci-admission.py`. Ardından `validate-workflows.py`'nin mevcut token kontrolü listesine (satır 70-77) bu iki komutu ekle ki adım geri çıkarılamasın.

---

### 72. Migration sayısı hâlâ türetilmiyor: `26` sabiti 9 ayrı yerde elle tutuluyor ve doğrulayıcı literal'i literal ile karşılaştırıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | developer-experience |
| **Hat** | R12 — Release supply chain, CI workflow, kapılar |
| **Doğrulama** | CONFIRMED |
| **Konum** | `.github/workflows/ci.yml:615,622,629,636,640; .github/scripts/validate-workflows.py:92-95; tests/Saydin.PriceIngestion.IntegrationTests/IngestionDatabaseFixture.cs:65; tests/Saydin.DataRepair.IntegrationTests/RepairDatabaseFixture.cs:37; .github/scripts/run-development-compose-smoke.sh:178,183; tests/Saydin.DataRepair.IntegrationTests/run-isolated.sh:193` |

**Bulgu.** İddia doğru ve R12-01 ile kanıtlanmış durumda. Not: `run-unit-coverage.sh:45-56` zaten doğru deseni gösteriyor (`find ... | sort` ile keşfedilen envanteri konfigüre edilmiş liste ile `diff` karşılaştırması) — yani repo bu türetmeyi başka yerde yapmayı biliyor, migration sayısında yapmıyor.

**Etki.** Her yeni migration 9 dosyalık mekanik bir edit turu gerektiriyor; doğrulayıcı literal-literal karşılaştırdığı için birlikte bayatlamayı yakalayamıyor ve atlanan yerler (smoke, run-isolated) sessizce yanlış sayı bekliyor.

**Öneri.** `validate-workflows.py` içinde sayıyı envanterden hesapla (`len([p for p in (root/'infrastructure/postgres/migrations').iterdir() if p.suffix in ('.sql','.sh')])`) ve ci.yml'deki literal'i bu değere göre üret/doğrula; ci.yml'de sayıyı job-level `env: SAYDIN_EXPECTED_MIGRATIONS` değişkenine taşı; test fixture'larını `MigrationTrustRoot.Versions.Count` üzerinden bağla; shell kapılarını `find`-türetmeli yap. `run-unit-coverage.sh`'deki envanter-diff desenini örnek al.

---

### 73. `Saydin.PriceIngestion.IntegrationTests` coverage üretmiyor; `coverage-admission`'ın kardinalite kontrolü tesadüfen tutuyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | test-quality |
| **Hat** | R12 — Release supply chain, CI workflow, kapılar |
| **Doğrulama** | CONFIRMED |
| **Konum** | `.github/workflows/ci.yml:894-899; .github/scripts/run-ingestion-ledger-tests.sh:44-49; karşı taraf: .github/scripts/run-unit-coverage.sh:45-56` |

**Bulgu.** İddia birebir doğru; ek kanıt olarak `run-unit-coverage.sh` unit tarafında tam olarak önerilen envanter-eşitliği kontrolünü zaten yapıyor, yani integration tarafındaki sayı-kontrolü bilinçli bir tercih değil, eksik uygulama.

**Etki.** Ingestion ledger yollarının (lease fence, next_attempt_at, permanent-window) integration kapsamı birleşik changed-line coverage kapısına hiç girmiyor; `Saydin.PriceIngestion.Adapters` gibi kritik namespace eşikleri olduğundan düşük hesaplanabilir. Kardinalite kontrolü bu boşluğu maskeliyor.

**Öneri.** `run-ingestion-ledger-tests.sh`'e migrator runner'daki gibi `--settings .github/scripts/coverage.settings.xml --collect "XPlat Code Coverage"` + tek-rapor kardinalite kontrolü + `mv ... ingestion-ledger-integration.coverage.cobertura.xml` ekle. `coverage-admission`'daki sayı kontrolünü beklenen dosya adları kümesiyle eşitlik kontrolüne çevir (unit taraftaki `diff -u` deseni gibi).

---

### 74. Yeni release image'ı `src/Saydin.DataRepair/Dockerfile` required CI'da hiç build edilmiyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R12 — Release supply chain, CI workflow, kapılar |
| **Doğrulama** | CONFIRMED |
| **Konum** | `.github/workflows/release-images.yml:124; karşı taraf: .github/workflows/ci.yml:944-1004` |

**Bulgu.** Kaydın DataRepair yarısı doğru, Caddy yarısı YANLIŞ: `infrastructure/deployment/Dockerfile.caddy` required `production-assurance` job'ında `validate-production-assets.sh:93-95` tarafından gerçekten build ediliyor. Sekiz release Dockerfile'ından yalnız biri (DataRepair) required CI'da hiç derlenmiyor.

**Etki.** Yeni first-party release artefaktı için build-doğruluğu geri bildirimi PR'dan release penceresine kayıyor; DataRepair Dockerfile'ındaki bir COPY/lock/publish kırılması ancak self-hosted release runner'da, imzalama/SBOM öncesi ortaya çıkar ve release'i yarıda keser.

**Öneri.** `docker-build` job'ına `src/Saydin.DataRepair/Dockerfile` için bir `docker/build-push-action` adımı ekle (push:false, load:true, `type=gha` cache scope'u ile). Daha kalıcısı: `validate-workflows.py`'ye 'release matrisindeki her Dockerfile, ci.yml docker-build'de veya validate-production-assets.sh'de build ediliyor' kontrolü koy.

---

### 75. `production-assurance` 20 dakikalık timeout içinde soğuk tam .NET stack build'i + DB bootstrap smoke'u da koşuyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | developer-experience |
| **Hat** | R12 — Release supply chain, CI workflow, kapılar |
| **Doğrulama** | PLAUSIBLE |
| **Konum** | `.github/workflows/ci.yml:96-99,112-113; .github/scripts/run-development-compose-smoke.sh:120-176` |

**Bulgu.** Olgular doğrulandı (cache yok, aynı job'da ağır statik kapılar var, timeout 20 dk). Gerçek sürenin 20 dakikayı aşıp aşmadığı ancak koşuda ölçülebilir; bu nedenle kesin değil ama risk somut.

**Etki.** Cache miss veya yavaş runner'da job 'timeout' ile iptal olur ve gerçek neden görünmez; her PR bu maliyeti öder. Kararsızlaşan yeni kapının tipik akıbeti `continue-on-error` veya timeout şişirmedir — kapının değeri erozyona uğrar.

**Öneri.** Smoke'u ayrı bir job'a taşı (hızlı statik kapılar — validate-workflows, check-doc-links, release self-test'leri — smoke süresine bağlı kalmasın) ve o job'a ölçülmüş süreye göre timeout ver; ayrıca `COMPOSE_BAKE=true` + `type=gha` cache ile smoke build'ini `docker-build` scope'larıyla paylaştır.

---

### 76. Lokal unit kapısı (`tests` compose servisi) hiç ihtiyaç duymadığı tam DB bootstrap zincirine ve Redis'e bağlı — ve bu bağımlılık artık sözleşmeyle sabitlendi

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | developer-experience |
| **Hat** | R12 — Release supply chain, CI workflow, kapılar |
| **Doğrulama** | CONFIRMED (doğrulayıcı ek bulgusu) |
| **Konum** | `docker-compose.yml:477-500 (tests servisi); .github/scripts/validate-development-compose.py:13-16,153-157 (POST_BOOTSTRAP_CONSUMERS); .github/scripts/run-local-tests.sh:14-21; CLAUDE.md:31-32` |

**Bulgu.** `tests` servisi `depends_on: {database-role-bootstrap-post-migration: service_completed_successfully, redis: service_healthy}` ve `ConnectionStrings__Redis` env'i taşıyor. Ama aynı servisin entrypoint'i `run-local-tests.sh` ve o script satır 14-21'de `*Saydin.Services.sln*|*IntegrationTests*|*Saydin.DatabaseMigrator.Tests*` argümanlarını fail-closed reddediyor — yani bu servis içinde gerçek PG/Redis kullanan hiçbir suite koşamaz. `grep -rn 'ConnectionStrings__Redis' tests/` yalnız `tests/Saydin.Api.IntegrationTests/**` içinde eşleşiyor (RedisFixture.cs:19, ErrorContractHttpTests.cs:56, IntegrationTestEnvironment.cs:56); `ErrorContractWebAppFactory` de yalnız IntegrationTests projesinde. Kesin kanıt: CI'ın `build-and-test` job'ı (ci.yml:22-95) tam olarak aynı `run-unit-coverage.sh`'yi hiçbir PostgreSQL/Redis servisi olmadan, çıplak ubuntu runner'da koşuyor ve geçiyor — yani yedi unit projesinin hiçbiri DB/Redis'e ihtiyaç duymuyor. Buna rağmen `validate-development-compose.py:13-16` `tests`'i `POST_BOOTSTRAP_CONSUMERS` içine koyup post-bootstrap bağımlılığını required CI'da zorunlu kılıyor (mutasyon testi: `api_bypasses_post_bootstrap`).

**Etki.** Repo'nun en sık çalıştırılan lokal kapısı, hiç kullanmadığı bir altyapıyı ayağa kaldırmak için dakikalar harcıyor ve `REDIS_PASSWORD` gibi ek ön koşullar dayatıyor; DB stack'i sağlıksızsa saf unit testleri hiç koşturulamıyor. Bağımlılık artık bir CI sözleşmesiyle sabitlendiği için kaldırılması ek bir kapı değişikliği gerektiriyor — yani yanlış varsayım kalıcılaştırılmış durumda.

**Öneri.** `tests` servisinden `depends_on` ve `ConnectionStrings__Redis`'i kaldır (unit suite'inin hiçbiri kullanmıyor; CI bunu zaten kanıtlıyor) ve `validate-development-compose.py`'deki `POST_BOOTSTRAP_CONSUMERS` kümesinden `tests`'i çıkar. Gerçek PG/Redis gerektiren suite'ler zaten `run-local-tests.sh` tarafından reddediliyor ve kanonik yolları `.github/compose.integration.yml`. Böylece lokal unit döngüsü saniyeler mertebesine iner ve CLAUDE.md:31'deki vaat gerçeğe uyar.

---

### 77. Yeni EF↔migration parity testi, migration 023'ün kaldırdığı chk_activity_action constraint'ini modelde zorunlu kılarak drift'i kilitliyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | test-quality |
| **Hat** | R13 — Saydin.Api test kalitesi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.Api.Tests/Data/SharedEfParityTests.cs:40-70; infrastructure/postgres/migrations/023_installation_lifecycle_admission.sql:278,316-322; src/Saydin.Shared/Data/Configurations/ActivityLogConfiguration.cs:17` |

**Bulgu.** SharedEfParityTests yalnızca ADD-CONSTRAINT formunu tarayıp DROP'ları hesaba katmadığı için, migration 023 tarafından kalıcı olarak kaldırılan (ve postcondition'ı 'var olmamalı' diye doğrulayan) chk_activity_action'ı EF modelinde ARANMASI gereken bir isim sayıyor; EF konfigürasyonundaki drift'i temizlemek isteyen geliştiriciyi test kırarak engelliyor. Runtime etkisi yok (EF check constraint'leri yalnız migration üretiminde kullanılır, bu repo EF migration kullanmıyor); etki parity iddiasının yanlış olması ve düzeltmenin kilitlenmesidir.

**Etki.** 'EF modeli checked-in migration'larla parity' güvencesi activity_logs için yanlış; şema okuyucusu korumanın hâlâ CHECK'te olduğunu sanır, oysa koruma scheduler-owned trigger'a taşındı. Aynı regex yaklaşımı gelecekte DROP edilen her chk_ nesnesinde sessizce tekrarlanır.

**Öneri.** Migration'ları numara sırasıyla oynatıp ADD/DROP olaylarından 'son durum' kümesi üreten ortak bir yardımcı yaz (ApiTrustSchemaModelTests ile paylaş), sonra ActivityLogConfiguration'dan chk_activity_action'ı kaldır ve trigger tabanlı allowlist'i fonksiyonel bir integration testiyle (izinsiz action ile INSERT → 23514) kilitle.

---

### 78. Management-port HTTP testinin yeniden yazımı ApiRuntimeContract.Configure(KestrelServerOptions) kapsamını tamamen düşürdü

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | test-quality |
| **Hat** | R13 — Saydin.Api test kalitesi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.Api.Tests/Middleware/ApiManagementBoundaryHttpTests.cs:33-38 (git diff: `-builder.WebHost.ConfigureKestrel(runtime.Configure)`); src/Saydin.Api/Runtime/ApiRuntimeContract.cs:70-74` |

**Bulgu.** Doğru; ancak bu bir 'yanlış davranış' değil, bu commit'in getirdiği bir kapsam kaybıdır: `Configure(KestrelServerOptions)` (public+management ListenAnyIP) artık hiçbir birim/entegrasyon testi tarafından çağrılmıyor. Regresyon tamamen sessiz kalmaz (Docker health check / Prometheus scrape deploy'da patlar), ama L01 port-izolasyon remediation'ının en dış katmanı CI kapısında kanıtsız.

**Etki.** ListenAnyIP→ListenLocalhost veya yanlış port gibi bir değişiklik tüm test paketini yeşil bırakır; hata ancak deploy sonrası (management scrape ölümü ya da management yüzeyinin yanlış arayüze açılması) görülür.

**Öneri.** ApiRuntimeContractTests'e `KestrelServerOptions` overload'ı için bir Fact ekle: `options.CodeBackedListenOptions` üzerinden iki endpoint'in IPAddress.Any + doğru portlar olduğunu doğrula. Bu, mevcut HTTP testinin ListenHandle kurgusunu bozmadan kapsamı geri getirir.

---

### 79. Yeni SecurityAdmissionTelemetry ve saydin.security admission sayacı için hiç test yok — üstelik istek yolunda fırlatan bir doğrulama içeriyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | test-quality |
| **Hat** | R13 — Saydin.Api test kalitesi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Security/SecurityAdmissionTelemetry.cs:17-49; src/Saydin.Shared/Diagnostics/SaydinMetrics.cs:176-179 (test yok)` |

**Bulgu.** İddia doğru. Mevcut 5 enum değeri dolaylı olarak DistributedSecurityLimiterTests ve InstallationAuthenticationFilterTests akışlarında uyandırılıyor; eksik olan (a) metrik adı/etiket şeması, (b) enum genişlemesine karşı fail-fast davranışı, (c) allowlist dışı değerlerin reddi için tek bir assertion bulunmamasıdır.

**Etki.** İki runbook'un teşhis adımı doğrulanmamış bir metrik sözleşmesine dayanıyor; SecurityLimiterReason/Outcome genişlemesi korumalı endpoint'lerde toplu 500'e dönüşebilir ve CI bunu yakalamaz.

**Öneri.** `[Collection(MetricsTestCollection.Name)]` altında MeterListener ile (a) saydin.security.admission.decisions.total etiketlerini, (b) Enum.GetValues üzerinden theory ile Record'un hiçbir enum değerinde fırlatmadığını, (c) allowlist dışı değerlerin ArgumentOutOfRangeException verdiğini doğrulayan bir suite ekle.

---

### 80. Bu commit'te eklenen lokalizasyon anahtarları resx key-varlığı regresyon kilidine eklenmedi

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | test-quality |
| **Hat** | R13 — Saydin.Api test kalitesi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.Api.Tests/Localization/ErrorMessagesLocalizationTests.cs:31-71` |

**Bulgu.** Doğru; sayılar biraz farklı: 50 kullanılan anahtardan 35'i listede, 23'ü değil (finder 49/35/14 demiş). InstallationCredentialInvalid dolaylı olarak `GetString_WithDifferentCultures...` Fact'i tarafından korunuyor, Detail varyantı korunmuyor.

**Etki.** Bir anahtar resx'ten silinir veya .en.resx'e eklenmezse IStringLocalizer ham anahtarı döndürür ve RFC 7807 gövdesinde kullanıcıya 'SecurityRateLimitedDetail' gibi bir string gider; tüm test paketi yeşil kalır.

**Öneri.** InlineData listesini elle bakımlı olmaktan çıkar: merkezi bir ErrorMessageKeys sabit sınıfı tanımlayıp hem üretim kodu hem test ondan beslensin. Kısa vadede 23 eksik anahtarı ekle ve QuotaUnavailableExceptionHandlerTests'e title'ın ham anahtar olmadığı assertion'ını koy.

---

### 81. 'Her endpoint bir yüzey bildirir' invariant'ı ve port==0 kaçış kapısı test edilmiyor; selector policy metadata'sız endpoint'lerde fail-open

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | test-quality |
| **Hat** | R13 — Saydin.Api test kalitesi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Runtime/ApiEndpointSurface.cs:31-40,44-53; src/Saydin.Api/Program.cs:354-378; tests/Saydin.Api.Tests/Middleware/ApiPortBoundaryMiddlewareTests.cs:17-33` |

**Bulgu.** Büyük ölçüde doğru, tetikleme düzeltilmeli: middleware Classify (ApiPortBoundaryMiddleware.cs:50-56) bilinmeyen path'i management portunda zaten Rejected yapıyor; metadata unutulan endpoint public portta servis edilir, dolayısıyla asıl risk 'management niyetli' bir endpoint'in grup dışında map edilip public yüzeye düşmesidir. port==0 kaçış kapısının tamamen kaldırılması tüm WebApplicationFactory testlerini kırar (dolaylı koruma); test edilmeyen yön yalnızca `!environment.IsProduction()` koşulunun düşürülmesidir ve gerçek Kestrel'de LocalPort 0 olmaz.

**Etki.** L01 remediation'ının çekirdek savunması (surface metadata) sözleşme testi olmadan duruyor; en olası regresyon yolu (metadata'sız yeni endpoint) kapıda yakalanmıyor.

**Öneri.** Gerçek Program/MapXEndpoints grafiğini ayağa kaldırıp EndpointDataSource.Endpoints üzerinden 'her RouteEndpoint tam olarak bir ApiEndpointSurfaceMetadata taşır' Fact'i ekle; Matrix'e Production + port==0 satırlarını ve ayrı bir Development testinde kaçış kapısının bilinçli davranışını ekle.

---

### 82. ActivityLog yazıcı sınıflandırıcısının Postgres-dışı dalı (SocketException/IOException/TimeoutException/IsTransient) hiç test edilmiyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | test-quality |
| **Hat** | R13 — Saydin.Api test kalitesi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.Api.Tests/BackgroundServices/ActivityLogWriterTests.cs:16-40; src/Saydin.Api/BackgroundServices/ActivityLogBatchStore.cs:74-81; src/Saydin.Api/BackgroundServices/ActivityLogWriter.cs:143-147` |

**Bulgu.** İddia doğrulandı. Ek nüans: sınıflandırıcının varsayılanı FatalHost olduğu için bu dalın bozulması sessiz yanlış sınıflandırma değil doğrudan host sonlanması (crash-loop) demektir; integration tarafı yalnız pg_terminate_backend (57P01, PostgresException) senaryosunu kapsıyor, gerçek soket kopmasını değil.

**Etki.** Ağ kopması / PG host erişilemezliği yolunda regresyon koruması yok; önceki High bulgu #6'nın hedeflediği 'geçici PG koşulu tüm host'u düşürüyor' senaryosu kanıtsız kalıyor.

**Öneri.** Theory'yi `Exception -> beklenen kind` biçimine genişlet: DbUpdateException(IOException), DbUpdateException(SocketException), DbUpdateException(TimeoutException), InvalidOperationException (→ FatalHost) ve iç içe sarmalanmış bir vaka. FatalHost'un host'u düşürmesi bilinçliyse HostOptions'ı Saydin.Api'de de açıkça set edip testle belgele.

---

### 83. jsonb sayısal normalizasyon beklentileri gerçek PostgreSQL'e karşı doğrulanmıyor; parity integration testinin korpusu exponent vakalarını dışarıda bırakıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | test-quality |
| **Hat** | R13 — Saydin.Api test kalitesi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.Api.Tests/Services/ScenarioExtraDataValidatorTests.cs:101-117; tests/Saydin.Api.IntegrationTests/SavedScenarioRepositoryIntegrationTests.cs:22-40` |

**Bulgu.** Doğru. Unit beklentileri PostgreSQL numeric semantiğine göre bugün doğru görünüyor; eksik olan, gerçek-DB oracle eldeyken en riskli dalın (exponent/ölçek normalizasyonu) o oracle'a karşı hiç koşulmamasıdır.

**Etki.** Tahmin edici ile gerçek jsonb boyutu arasındaki bir sapma 8192 baytlık uygulama sınırını DB CHECK'inden (chk_saved_scenarios_extra_data_size) ayırır: ya erken red ya da 23514 → kullanıcıya beklenmedik hata.

**Öneri.** Unit theory'nin InlineData korpusunu integration parity testine taşı (aynı dizi octet_length ile karşılaştırılsın), unit tarafı hızlı geri besleme için bırak; aynı yaklaşımı JsonbStorageSize.UpperBound için pg_column_size karşılaştırmasını exponent/derin nesne/uzun string ile genişleterek uygula.

---

### 84. SharedEfParityTests'in elle bakımlı prefix allowlist'i EF-modellenmiş üç tablonun 18 CHECK constraint'ini sessizce kapsam dışı bırakıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | test-quality |
| **Hat** | R13 — Saydin.Api test kalitesi |
| **Doğrulama** | CONFIRMED (doğrulayıcı ek bulgusu) |
| **Konum** | `tests/Saydin.Api.Tests/Data/SharedEfParityTests.cs:58-68` |

**Bulgu.** `ownedPrefixes` yalnız sekiz prefix içeriyor: chk_activity_, chk_asset_catalog_, chk_asset_market_, chk_inflation_rates_, chk_installation_credentials_, chk_market_calendar, chk_price_points_, chk_users_. Testin kendi regex'ini python ile emüle ettim: migration'larda 63 chk_ adı var, allowlist bunların yalnız 38'ini kapsıyor. Kapsam dışı kalan 25 addan 18'i EF tarafından FİİLEN modelleniyor — chk_saved_scenarios_type/unit/dates/type_unit/extra_data_object/extra_data_size (SavedScenarioConfiguration.cs:19-30), chk_ingestion_jobs_type/status (IngestionJobConfiguration.cs:16-20) ve chk_ingestion_windows_* (10 adet, IngestionWindowConfiguration.cs). Bunları allowlist'e eklemek testi kırmıyor: migration−EF farkı yalnız EF'de hiç modellenmeyen chk_price_attribution_*, chk_inflation_attribution_*, chk_provider_fetch_payloads_* (7 ad).

**Etki.** 'EF modeli ile checked-in migration'lar parity' iddiası adının düşündürdüğünün ~%60'ı kadar yüzey kapsıyor; en önemlisi kullanıcı verisi tutan saved_scenarios (extra_data boyut/şekil CHECK'leri dahil) korumasız. Allowlist elle bakımlı olduğu için her yeni tablo sessizce dışarıda kalır.

**Öneri.** Kapsamı tersine çevir: 'EF modelinde tablosu bulunan her constraint' olarak hesapla ve yalnız EF'de hiç modellenmeyen üç tabloyu (price_attribution, inflation_attribution, provider_fetch_payloads) gerekçeli istisna listesine al. Ayrıca Should().Contain(expected) yerine çift yönlü karşılaştırma kullan ki EF'de olup migration'ın son durumunda olmayan nesneler (R13-01'deki chk_activity_action drift'i) de yakalansın.

---

### 85. Failure-finalize timeout'u yakalanmamış OperationCanceledException üretip ingestion sürecini fatal düşürüyor; iki test bunu sözleşmeye çeviriyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R14a — PriceIngestion + calendar test kalitesi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:186-200 ↔ :60-74 ↔ :264-280; src/Saydin.PriceIngestion/Workers/EvdsInflationWorker.cs:136-150; src/Saydin.PriceIngestion/Workers/IngestionOrchestrator.cs:116-137; tests/Saydin.PriceIngestion.Tests/Workers/BaseAssetWorkerTests.cs:185-197; tests/Saydin.PriceIngestion.Tests/Workers/EvdsInflationWorkerTests.cs:101-113` |

**Bulgu.** Adapter exception yolundaki bounded failure-finalize çağrısı korumasızdır: 5 sn'lik finalize token'ı tetiklenirse fırlayan OperationCanceledException RunAsync'in `when (ct.IsCancellationRequested)` filtresine takılmaz, worker task'ı fault'lar ve IngestionOrchestrator bunu infrastructure-fatal sayarak exit 1 ile tüm ingestion sürecini düşürür. Aynı sınıf hata cancellation yolunda (MarkCancelledBoundedAsync) bilinçli olarak yutulup LogWarning'e çevrilmişken, exception yolunda korunmamıştır; iki yeni unit test bu asimetriyi düzeltmek yerine `ThrowAsync<OperationCanceledException>()` ile sözleşme haline getirmektedir.

**Etki.** Adapter hatası anında PostgreSQL 5 sn'den uzun yanıt vermezse (failover, lock bekleme, pool doygunluğu) tek bir window'un failure kaydını yazamamak tüm price + TÜFE ingestion sürecinin exit 1 ile düşmesine yükselir. Süreç restart ederken lease süresi (30 dk) dolana kadar ilgili scope Busy kalır ve telemetri/metrik export'u kesilir. Testler kusuru maskelediği için gelecekte bir düzeltme regresyon sayılacaktır.

**Öneri.** RecordFailureAsync çağrısını MarkCancelledBoundedAsync ile aynı şekilde `try/catch (Exception ex)` içine al; hatada `LogWarning` + `DrainResult.Deferred(now + LogicalRetryDelay)` dön (lease expiry zaten reclaim eder). Aynı düzeltmeyi EvdsInflationWorker'da da uygula. Testleri davranışa göre yeniden yaz: exception fırlatılmadığını, çağrının FailureFinalizeTimeout içinde döndüğünü ve RunAsync'in döngüde kaldığını doğrula.

---

### 86. VerifyCandidateBehaviorTests gerçek script'i çalıştırıyor ama docker stub'ı offline replay'i tamamen atlıyor; sandbox bayrakları hiçbir testte doğrulanmıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | test-quality |
| **Hat** | R14a — PriceIngestion + calendar test kalitesi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tools/calendar-data/tests/Saydin.CalendarData.Tests/VerifyCandidateBehaviorTests.cs:9-13, :57-58 ↔ infrastructure/calendar/verify-candidate.sh:31,35,66,74,81-97; tools/calendar-data/tests/Saydin.CalendarData.Tests/InfrastructureCalendarContractTests.cs:43-47` |

**Bulgu.** Yeni davranışsal verifier testi imza/manifest-hash/envanter/owner kapılarını gerçekten koşturuyor (önceki review'in isteği bu yönde karşılanmış), ancak admission'ın ikinci güvencesi olan hardened offline replay davranışsal olarak test edilmiyor: stub docker `--network none` gördüğü an başarı döndüğü için sandbox bayraklarının kaybı, yanlış alt-komut veya replay divergence hiçbir testte kırmızıya dönmez. Ek olarak stub `jq` `select(...)` guard'larını yok saydığından envelope schema/snapshotSetId doğrulama semantiği de gerçekte koşmuyor (bkz. ek bulgu R14a-A3).

**Etki.** Calendar candidate admission zincirinin en kritik kapısı (deterministik offline replay + sandbox hardening) regresyona açık; bir refactor `--read-only`/`--cap-drop ALL`/`--user`/`readonly` mount'u düşürse veya replay'i etkisizleştirse beş test case'i de contract testi de yeşil kalır.

**Öneri.** Stub docker'ı `--read-only`, `--cap-drop ALL`, `--security-opt no-new-privileges`, `--user <uid>:<gid>`, `dst=/candidate,readonly` ve `verify --data-root /candidate` argümanlarını doğrulayıp eksikte non-zero dönen bir script'e çevir. En az bir case'te stub'ı non-zero döndürüp replay divergence'ının reddedildiğini kanıtla; `expected_output_hash_mismatch`, `snapshot_set_mismatch`, `candidate_contains_symlink` ve post-replay `manifest_changed` için InlineData ekle. İdeali: required Linux Docker gate'inde gerçek imajla uçtan uca en az bir replay case'i.

---

### 87. TCMB coverage kanıt kapısı hafta sonu coverage_through günlerinde atlanıyor; plan materializer düzenli olarak hafta sonu cutoff üretiyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | data-integrity |
| **Hat** | R14a — PriceIngestion + calendar test kalitesi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tools/calendar-data/src/Saydin.CalendarData/CalendarDataGenerator.cs:165-181, :261-274; tools/calendar-data/src/Saydin.CalendarData/CalendarPlanMaterializer.cs:49-56; tools/calendar-data/src/Saydin.CalendarData/CalendarAcquisition.cs:132-151; infrastructure/calendar/systemd/calendar-acquisition-tcmb.timer; tools/calendar-data/tests/Saydin.CalendarData.Tests/CalendarCoverageEvidenceTests.cs:8-37; src/Saydin.PriceIngestion/Repositories/IngestionWindowRepository.cs:213-233` |

**Bulgu.** Fail-closed olduğu iddia edilen `tcmb_coverage_beyond_last_publication` kapısı ile hafta içi günleri asıl koruyan ResolveTcmbCoverageThrough clamp'i, coverage_through hafta sonuna denk geldiğinde birlikte devre dışı kalır. 06:00 Europe/Istanbul timer'ı her hafta en az iki kez hafta sonu cutoff üretir; o koşularda arşivde henüz görünmeyen bir hafta içi publication günü sessizce `no_publication` olarak mühürlenir ve hiçbir kapı devreye girmez. Negatif senaryonun testi yoktur; mevcut test yalnız hafta sonu passthrough'unu pozitif olarak kilitler.

**Etki.** Yanlış bir authoritative calendar release üretilir; ingestion o günü ExpectedNoData sayar, price_points'te kalıcı boşluk oluşur ve gün "beklenmiyor" işaretli olduğu için stale-data alarmı da tetiklenmez. PlanWindowsAsync mevcut window'ları yeniden açmadığından sonraki doğru release bunu otomatik onarmaz; kurtarma imzalı DataRepair gerektirir.

**Öneri.** Kapıyı hafta sonu için anlamlı hale getir: `through` hafta sonuysa ondan geriye doğru en yakın hafta içi günün `published` içinde olmasını zorunlu kıl. Alternatif olarak TcmbProviderCutoff'u hafta sonu/tatilde en son hafta içi güne çek. Testlere (a) CoverageThrough=cumartesi + eksik cuma publication'ı senaryosunu, (b) TcmbProviderCutoff için cumartesi/pazar girdilerini ve 13:30:00 UTC sınırının hafta sonu davranışını ekle.

---

### 88. "Bir asset'in permanent window'u diğerlerini etkilemiyor" iddiasının worker-pass düzeyinde regresyon testi yok

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | test-quality |
| **Hat** | R14a — PriceIngestion + calendar test kalitesi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.PriceIngestion.Tests/Workers/BaseAssetWorkerTests.cs:78-96, :288-338, :666-672 ↔ src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:88-100, :141-144, :202-203, :494-500` |

**Bulgu.** Permanent-window izolasyonunun yalnız tek-asset, tek-chunk seviyesindeki "throw etmiyor" davranışı test edilmiştir. DrainAsync'in `PermanentBlocked` claim dalı unit testlerde hiç çalışmaz ve BackfillAsync'in permanent bir asset'ten sonra sibling asset'lere devam ettiğini doğrulayan bir test yoktur; "tek asset tüm hattı kilitlemiyor" iddiası davranışsal kanıta değil kod okumasına dayanmaktadır.

**Etki.** Envanterdeki en yüksek etkili High'ın (tek asset tüm hattı kilitliyor + crash-loop) regresyon koruması yoktur; `case PermanentBlocked:` dalı yeniden throw'a dönse veya BackfillAsync döngüsü break'e çevrilse mevcut 20+ worker testinin hiçbiri kırmızıya dönmez.

**Öneri.** RunAsync üzerinden iki asset'li bir test ekle: A-FIRST için ClaimNextAsync `PermanentBlocked`, Z-LAST için `Claimed`→`Complete`. Assert: (1) Z-LAST adapter çağrısı yapıldı, (2) RunAsync exception fırlatmadı ve döngüde kaldı, (3) scope kimlikli LogCritical bir kez yazıldı, (4) permanent + retryable sibling karışımında NextWakeAt sibling'in next_attempt_at'i oldu. IngestionOrchestratorTests'e "permanent window fatal değildir" case'ini ekle.

---

### 89. "Hiçbir şey olmadı" negatif assert'leri tek bir `await Task.Yield()` ile senkronize ediliyor; wait-loop'lar duvar saatine bağlı

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | test-quality |
| **Hat** | R14a — PriceIngestion + calendar test kalitesi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.PriceIngestion.Tests/Workers/BaseAssetWorkerTests.cs:244-248, :329-331, :536-545; tests/Saydin.PriceIngestion.Tests/Workers/EvdsInflationWorkerTests.cs:157-160, :202-211; tests/Saydin.PriceIngestion.Tests/HttpResilienceExtensionsTests.cs:32-35, :131-140` |

**Bulgu.** Yeni eklenen en değerli davranışların (mutlak provider deadline sonrası lease renewal'ın durması, ledger next_attempt_at uyanması, retry gecikmesinin alt sınırı) negatif assert'leri gözlemlenebilir bir sinyale değil tek bir `await Task.Yield()`'e bağlanmıştır; WaitUntilAsync ise duvar saatine dayalı 1 saniyelik bir spin bütçesi kullanır. Bu, zero-skip/zero-fail kapısı olan bir repoda hem regresyon kaçırma hem merge-bloker flake riski üretir.

**Etki.** Deadline sonrası renewal'ı durdurmayan bir regresyon, devam thread-pool'a geç kuyruklandığı için fark edilmeyebilir; ters yönde yüklü CI runner'da 1 sn bütçesi aşılırsa required job TimeoutException ile düşer.

**Öneri.** Negatif assert'leri gözlemlenebilir sinyale bağla: fake repository'ye TaskCompletionSource tabanlı "renewal çağrıldı" sinyali ekleyip `await Task.WhenAny(signal, Task.Delay(...))` ile bekle veya FakeTimeProvider ile deterministik bir TaskScheduler kullan. WaitUntilAsync bütçesini en az 30 sn'ye çıkar, `DateTime.UtcNow` yerine `Stopwatch` kullan ve spin yerine kısa TCS/`Task.Delay` beklemesine geç. HttpResilienceExtensionsTests'teki await'siz `CallCount`/`IsCompleted` assert'lerini bir sinyal beklemesinin arkasına al.

---

### 90. Permanent-blocked scope yalnız Critical log üretiyor; metrik/alarm yok, dolayısıyla testlenebilir bir gözlemlenebilirlik sözleşmesi de yok

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R14a — PriceIngestion + calendar test kalitesi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs:256-262, :296-310; src/Saydin.Shared/Diagnostics/SaydinMetrics.cs:83; infrastructure/prometheus/rules/ingestion.yml:49-50` |

**Bulgu.** Kalıcı olarak izole edilmiş bir ingestion scope'u için yayılan tek sinyal LogCritical'dır; calendar-not-ready yolunun aksine sayaç ve Prometheus alert kuralı yoktur. Bu hem operasyonel tespiti log aggregation'a bağımlı kılar hem de davranışın metrik tabanlı bir regresyon testinin yazılmasını imkânsızlaştırır.

**Etki.** Bir asset'in provider credential'ı kalıcı bozulduğunda süreç ayakta, health yeşil ve calendar_not_ready sayacı 0 kalır; durmuş lane yalnız dolaylı olarak (saydin_ingestion_lag büyümesi) ve gecikmeli fark edilir. Kurtarma imzalı DataRepair `requeue_permanent_window` gerektirdiğinden erken tespit değerlidir.

**Öneri.** SaydinMetrics'e `saydin_ingestion_permanent_blocked_total{source,scope,code}` sayacı (tercihen aktif blocked scope sayısı için bir gauge) ekle ve RecordPermanentBlocked ile PersistTypedFailureAsync'in permanent dalında artır. infrastructure/prometheus/rules/ingestion.yml'a alert, tests/inventory.test.yml'a unit-test kuralı ekle. Worker testinde MeterListener ile etiketleri doğrula.

---

### 91. 3 dakikalık pipeline bütçesi yalnız sabit eşitliğiyle test ediliyor; total-timeout'un gerçekten kestiği davranışsal olarak kanıtlanmıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | test-quality |
| **Hat** | R14a — PriceIngestion + calendar test kalitesi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.PriceIngestion.Tests/HttpResilienceExtensionsTests.cs:106-113 ↔ tests/Saydin.PriceIngestion.Tests/Workers/BaseAssetWorkerTests.cs:250-286 ↔ src/Saydin.PriceIngestion/Extensions/HttpResilienceExtensions.cs:25, :41-48` |

**Bulgu.** Pipeline'ın "tek 3 dakikalık sözleşme" güvencesi yalnız sabit karşılaştırmasıyla korunmaktadır; ne total-timeout stratejisinin askıda kalan bir isteği kestiği ne de HttpClient.Timeout'un sonsuza alınmasıyla birlikte doğru davrandığı davranışsal olarak test edilir. Worker'ın stall testi üretim pipeline'ını hiç kullanmaz.

**Etki.** Header acquisition'da askıda kalan bir provider için pipeline seviyesindeki savunma katmanı sessizce kaybolabilir; geriye yalnız worker deadline'ı kalır ve bu regresyon hiçbir kapıda görünmez.

**Öneri.** Build(...) yardımcısına yanıt dönmeyen (TCS ile askıda) bir handler seçeneği ekle; FakeTimeProvider ile 3 dk ilerletip `client.GetAsync(...)`'in TimeoutRejectedException/TaskCanceledException ile bittiğini ve handler.CallCount'un beklenen değerde olduğunu doğrula. StalledHttpResponseBody_... testinde HttpClient'ı elle kurmak yerine AddSaydinResilience ile kurulmuş named client kullan.

---

### 92. Migration sayısı `26` fixture'larda ve CI'da 10+ yerde sabit; High #15'i üreten bayatlama sınıfı korunuyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | developer-experience |
| **Hat** | R14a — PriceIngestion + calendar test kalitesi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.PriceIngestion.IntegrationTests/IngestionDatabaseFixture.cs:64-69; tests/Saydin.DataRepair.IntegrationTests/RepairDatabaseFixture.cs:37; .github/workflows/ci.yml:615,622,629,636,640; .github/scripts/validate-workflows.py:92-95; karşılaştırma: tests/Saydin.PriceIngestion.IntegrationTests/PriceAuthorityMigrationIntegrationTests.cs:119-124` |

**Bulgu.** Mevcut `26` değeri doğrudur; kusur değerin yanlışlığı değil, tek otoriteden türetilmemiş olmasıdır. Aynı sayı iki fixture, dört CI kapısı, bir CI özet satırı ve bir validate-workflows.py kuralı arasında elle senkron tutulmaktadır; 025 numaralı migration eklendiğinde required integration-test job'ı ingestion, DataRepair, DQA ve fresh-schema adımlarında aynı anda düşer ve düzeltme 10 ayrı satırın elle güncellenmesini gerektirir.

**Etki.** Her migration eklemesi öngörülebilir bir "tüm CI kırmızı" turu üretir; bu, envanterdeki High #15'in (hard-coded `=23`) kök nedeninin aynısıdır ve remediation dokümanındaki kanıt tablosu bir sonraki migration'da yeniden bayatlar.

**Öneri.** Fixture readiness probe'larını PriceAuthorityMigrationIntegrationTests'teki gibi "terminal olmayan migration yok + gerekli versiyonlar succeeded" invariant'ına çevir. CI tarafındaki bilinçli ratchet'i korumak isteniyorsa sayıyı tek bir `.github/scripts/schema-expectations.json` kaynağından oku ve hem ci.yml hem validate-workflows.py bu tek değeri kullansın; böylece migration eklemesi tek satırlık bir güncelleme olur.

---

### 93. IntegrationEnvironmentTests remediation sonrası hedeflediği güvenlik kontrolüne hiç ulaşmıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | test-quality |
| **Hat** | R14b — Migrator/RoleBootstrap/DQA/DataRepair test kalitesi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.DataQualityAudit.IntegrationTests/IntegrationEnvironmentTests.cs:9-20; tests/Saydin.DataQualityAudit.IntegrationTests/IntegrationEnvironment.cs:26-31,44-55,60-63` |

**Bulgu.** Finder'ın tespiti birebir doğru. Ek kanıt: eski (HEAD) sürüm CI'da fiilen etkiliydi çünkü gerçek `SAYDIN_AUDIT_TEST_ADMIN_CONNECTION_FILE` set haldeydi ve yalnız RUN_ID override edildiği için gerçekten `expectedDatabase` uyuşmazlığı tetikleniyordu. Yeni sürüm env enjeksiyonunu kısmi bıraktığı için akış ilk `RequireValue`'da kesiliyor; assertion daraltması bunu maskeliyor. Yani bu bir 'iyileştirme' değil, net bir kapsam kaybı.

**Etki.** DQA integration fixture'ının canlı/staging/production veritabanına DML uygulamasını engelleyen tek unit-seviye koruma artık test edilmiyor. IntegrationEnvironment.cs:48-55'teki host/database guard'ı tamamen silinse bile test yeşil kalır.

**Öneri.** `values` sözlüğüne `Require`'ın tükettiği TÜM zorunlu değişkenleri ekle — ADMIN_CONNECTION_FILE için testin kendi oluşturduğu 0600 geçici dosyayı (SecureSecretFile Linux sözleşmesine uygun) kullan — sonra yalnız database adını run-id ile uyumsuz bırak ve `.WithMessage("Unsafe audit integration database target.")` assertion'ını geri getir. Host'ta `prod`/`staging` ve database adında `prod`/`staging` geçen negatif vakaları `[Theory]` olarak ekle.

---

### 94. DataRepair reject kodlarının 36/74'ü test edilmiyor; DQA kanıt imza sınırı tamamen kapsam dışı

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | test-quality |
| **Hat** | R14b — Migrator/RoleBootstrap/DQA/DataRepair test kalitesi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.DataRepair/DqaEvidenceVerifier.cs:27,33,37,48,61,75; src/Saydin.DataRepair/RepairExecutor.cs:190,213; tests/Saydin.DataRepair.Tests/SignedRepairPlanTests.cs:93-103; src/Saydin.DataRepair/README.md:115-120` |

**Bulgu.** Doğrulandı: 74 reject kodundan 36'sı hiçbir testte tetiklenmiyor. Kritik boşluk, imzalı kanıt paketinin güven sınırıdır — `manifest.sig` bozulması, imzalayan anahtar uyuşmazlığı, non-canonical manifest, manifest.sha256 uyuşmazlığı, path traversal/symlink ve plan↔kanıt content binding'in hiçbiri negatif test edilmemiş; yalnız içerik dosyası hash'i test edilmiş. `repair_commit_state_uncertain` terminal operatör dalı da kapsam dışı olmasına rağmen README'de özel prosedürü var.

**Etki.** `RepairCryptography.Verify(...)` çağrısı kaldırılsa/negatiflense ya da `ResolveContained` traversal kontrolü gevşetilse mevcut unit+integration testlerin tamamı yeşil kalır. İmzasız/sahte bir DQA kanıt paketiyle imzalı repair planının yürütülmesi regresyona karşı korumasız. Ayrıca 07-remediation-progress.md'nin 'açık test kusuru kalmadı' iddiası bu ölçüde doğrulanmıyor.

**Öneri.** SignedRepairPlanTests'e negatif vakaları ekle: (1) manifest.sig bayt bozulması → `evidence_signature_invalid`; (2) farklı public key → `evidence_signer_mismatch`; (3) anahtar sırası bozulmuş manifest → `evidence_manifest_not_canonical`; (4) manifest.sha256 uyuşmazlığı → `evidence_manifest_hash_invalid`; (5) `files[].path` içinde `../` ve symlink → `evidence_path_invalid`; (6) plan.Evidence.ContentSha256 uyuşmazlığı → `evidence_content_binding_invalid`. Integration tarafında `CommitThenThrow` + ne pre- ne post-image'e uyan DB durumu kurgulayarak `repair_commit_state_uncertain` dalını kapat. Kalan kapsam dışı kodlar için repoda gerekçeli bir 'kapsam dışı' listesi tut.

---

### 95. Mega-test deseni yeni migrator integration testinde tekrarlanıyor; 'concurrent' iddiası kanıtlanmıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | test-quality |
| **Hat** | R14b — Migrator/RoleBootstrap/DQA/DataRepair test kalitesi |
| **Doğrulama** | CONFIRMED |
| **Konum** | `tests/Saydin.DatabaseMigrator.Tests/InstallationCredentialRehashMigrationIntegrationTests.cs:11-77; .github/workflows/ci.yml:810-812` |

**Bulgu.** Finder'ın tespiti birebir doğru. Ek doğrulama: migrator TRX ratchet'i (184) projenin gerçek test sayısına birebir eşit, dolayısıyla altı sözleşmenin beşinin sessizce çalışmaması sayıyı hiç etkilemez. Ayrıca eşzamanlılık kurgusunda senkronizasyon primitifi yok; test adı ('IsConcurrent...') kanıtlanmamış bir özellik iddia ediyor.

**Etki.** Ratchet ve TRX kapıları assertion kaybına karşı koruma sağlamıyor; ilk hata sonrası diğer beş sözleşmenin durumu CI çıktısından bilinemez. 'Eşzamanlılık kanıtlandı' izlenimi determinizmi olmayan bir kurguya dayanıyor — `resolve_installation_and_rehash` içinde gerçek bir yarış (eksik FOR UPDATE/CAS) olsa test yeşil kalır.

**Öneri.** Altı senaryoyu ayrı `[SkippableFact]`/`[SkippableTheory]` vakalarına böl (collection fixture paylaşıldığı için maliyet artmaz) ve ci.yml minimum'unu buna göre yükselt. Eşzamanlılık için iki bağlantıyı `SemaphoreSlim`/`Barrier` ile aynı anda serbest bırak, ardından rehash'in tam olarak bir kez uygulandığını `hash_key_version` ve `xmin`/`updated_at` üzerinden assert et.

---

### 96. README.md ve development-guide.md hâlâ '24 migration' diyor; gerçek zincir ve CI kapısı 26

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | documentation |
| **Hat** | R15 — Dokümantasyon, ADR, runbook |
| **Doğrulama** | CONFIRMED |
| **Konum** | `README.md:51 · docs/development-guide.md:72,255 · .github/workflows/ci.yml:615,622,629,636 · docs/architecture/database-schema.md:5-6,64` |

**Bulgu.** README.md:51 ve docs/development-guide.md:72,255 migration sayısını 24 olarak veriyor; gerçek zincir ve zorunlu CI şema kapısı 26'dır (`26,2,26,26,ready`). Aynı repodaki docs/architecture/database-schema.md doğru değeri taşıyor, yani doküman seti kendi içinde çelişiyor.

**Etki.** Şema hazır-olma kapısının dokümante edilmiş beklenen değeri zorunlu CI kapısıyla çelişiyor; operatör elle doğrulama yaptığında 26 görüp şemayı hatalı sanabilir, ya da CI kırmızıya döndüğünde beklenen değerin 24 olduğunu sanıp kapıyı gevşetmeye çalışabilir.

**Öneri.** README.md:51 ve development-guide.md:72,255'teki 24'leri 26 yap. Sayıyı üç ayrı yerde tekrarlamak yerine development-guide'ı `docs/architecture/database-schema.md`'ye referans verecek şekilde tekilleştir ve `.github/scripts/check-doc-links.py` yanına 'dokümanlarda geçen migration sayısı ci.yml SCHEMA_STATE ile tutarlı mı' şeklinde küçük bir grep kapısı ekle.

---

### 97. CLAUDE.md, installation-credential mimarisini yansıtmıyor: endpoint→repository doğrudan erişimi ve raw NpgsqlDataSource kullanımı hâlâ 'YASAK' olarak duruyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | architecture-rule |
| **Hat** | R15 — Dokümantasyon, ADR, runbook |
| **Doğrulama** | CONFIRMED |
| **Konum** | `CLAUDE.md:104-111,421-422 · src/Saydin.Api/Endpoints/InstallationEndpoints.cs:43-47,102,153,197 · src/Saydin.Api/Repositories/InstallationRepository.cs:7,101` |

**Bulgu.** CLAUDE.md'nin katman kuralı ve 'raw Npgsql' yasağı, aynı repodaki installation credential yolunun bilinçli tasarımıyla (endpoint→repository doğrudan çağrı + paylaşılan `NpgsqlDataSource` + parametreli SQL fonksiyonları) çelişiyor; sözleşme dosyası diğer istisnalar için revize edildiği hâlde bu iki kural için istisna eklenmemiş.

**Etki.** Sözleşme dosyası ile gerçek mimari çelişiyor. Ya kural anlamsızlaşır ya da `/check-architecture` veya rutin bir refactor sırasında güvenlik-kritik sabit-zamanlı verifier resolve/rehash yolu ve `PostgresErrorCodes.InvalidAuthorizationSpecification` tabanlı 401 davranışı 'kural gereği' yanlış yeniden yazılır.

**Öneri.** CLAUDE.md'ye calendar-data HttpClient istisnası tarzında kapsamı dar ve gerekçeli bir istisna ekle: 'installation credential lifecycle (021–024) sabit-zamanlı verifier resolve/rehash gerektirdiğinden `Saydin.Api/Repositories/InstallationRepository` EF yerine paylaşılan `NpgsqlDataSource` + parametreli SQL fonksiyonları kullanır; `InstallationEndpoints` bu repository'yi doğrudan çağırır ve `PostgresException` yakalar.' Alternatif olarak ince bir `IInstallationService` katmanı ekleyip kuralı olduğu gibi koru.

---

### 98. 'Hafif lokal unit kapısı' olarak belgelenen `tests` servisi postgres + migrator + iki bootstrap + HBA + redis zincirini ayağa kaldırıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | developer-experience |
| **Hat** | R15 — Dokümantasyon, ADR, runbook |
| **Doğrulama** | CONFIRMED |
| **Konum** | `docker-compose.yml:477-496 · .github/scripts/run-local-tests.sh:6-9 · .github/scripts/run-unit-coverage.sh:19-27,44-47 · CLAUDE.md:29-31 · docs/development-guide.md:231-242` |

**Bulgu.** `tests` servisi varsayılan yolunda hiçbir şekilde PostgreSQL veya Redis kullanmadığı hâlde `depends_on` ile post-migration bootstrap zincirinin tamamına ve zorunlu `REDIS_PASSWORD` interpolasyonuna bağlanmıştır; dokümanların 'DB gerekmez' etiketi komutun gerçek maliyetiyle çelişir.

**Etki.** Belirtilen ergonomi ile gerçek maliyet uyuşmuyor: tek bir unit test projesini çalıştırmak dakikalarca süren bir data-plane kurulumu tetikler ve bootstrap yapılmamış temiz bir checkout'ta hiç başlamaz. Commit kapısı gereksiz yere kırılgan hale gelmiş.

**Öneri.** `tests` servisinden `depends_on` ve `ConnectionStrings__Redis`'i kaldır (varsayılan yol bunları kullanmıyor); Redis gerektiren bir senaryo kalırsa ayrı bir `tests-with-infra` servisi tanımla. En azından dokümanlarda `--no-deps` bayrağını göster ve development-guide.md:240'taki 'DB gerekmez' etiketini komutla tutarlı hale getir.

---

### 99. data-repair.md, deploy-release.sh ve validate-production.sh'in güvenlik-kritik preflight'ını markdown içine kopyalıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R15 — Dokümantasyon, ADR, runbook |
| **Doğrulama** | CONFIRMED |
| **Konum** | `docs/runbooks/data-repair.md:16-110 · infrastructure/deployment/validate-production.sh:19-25 · infrastructure/release/deploy-release.sh:84-121` |

**Bulgu.** data-repair.md yaklaşık 90 satır güvenlik-kritik shell'i (production config render + validate-production + helper-image çıkarımı + üç volume kontratı doğrulaması) script'lerden kopyalayarak inline taşıyor; bu kopya hiçbir self-test tarafından kapsanmıyor ve script'lerle senkron kalacağını garanti eden bir kapı yok.

**Etki.** Güvenlik preflight'ının ikinci, test edilmeyen bir kopyası var; deploy-release.sh'teki sandbox bayrakları ya da validator argümanları güncellendiğinde sapma fark edilmeden production repair admission'ını zayıflatabilir.

**Öneri.** `infrastructure/release/verify-repair-admission.sh` adında tek bir script çıkar (manifest verify + env verify-existing + config render + validate-production + üç volume kontratı + release-binding kontrolü), diğer doğrulayıcılar gibi bir self-test ekle ve runbook'u `verify-repair-admission.sh "$RELEASE_DIR" "$ENV_FILE"` tek satırına indir.

---

### 100. development-guide.md'deki zorunlu integration TRX minimumlarının tamamı ci.yml ile uyuşmuyor; iki required suite hiç anılmıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | documentation |
| **Hat** | R15 — Dokümantasyon, ADR, runbook |
| **Doğrulama** | CONFIRMED (doğrulayıcı ek bulgusu) |
| **Konum** | `docs/development-guide.md:255-258 · .github/workflows/ci.yml:654,726,748,763,766,780,795,798,812` |

**Bulgu.** docs/development-guide.md:256-257 'API integration TRX'inde en az 57, ingestion ledger TRX'inde 39, role-bootstrap TRX'lerinde 76+7, migrator TRX'inde 124 ve DQA TRX'lerinde 82+72 executed test' diyor. .github/workflows/ci.yml'deki gerçek `verify-integration-trx.py --minimum-executed` değerlerini adım adlarıyla eşleştirdim: 'Required integration suite' (API) = 66 (satır 726), 'Ingestion ledger TRX fail-closed kapısı' = 44 (satır 748), 'Role-bootstrap TRX fail-closed kapıları' = 98 ve 13 (satır 795, 798), 'Migrator TRX fail-closed kapısı' = 184 (satır 812), 'Data-quality audit TRX fail-closed kapıları' = 96 ve 106 (satır 763, 766). Beş sayının beşi de yanlış. Ayrıca 'DataRepair TRX fail-closed kapısı' (32, satır 780) ve 'Calendar-data TRX fail-closed kapısı' (92, satır 654) required suite'ler olarak var ama dokümanda hiç geçmiyor. Bu paragraf aynı changeset'te düzenlenmiş (R15-03'teki 24→26 hatasıyla aynı satır bloğu).

**Etki.** Zorunlu zero-skip/minimum-executed kapılarının dokümante edilmiş değerleri gerçek kapılarla çelişiyor; en riskli senaryoda kapının kendisi 'yanlış' sanılıp gevşetilir. İki required suite (DataRepair, calendar-data) dokümantasyonda hiç görünmediği için kapsam da eksik temsil ediliyor.

**Öneri.** Sayıları ci.yml ile eşitle (66 / 44 / 98+13 / 184 / 96+106) ve DataRepair (32) ile calendar-data (92) kapılarını ekle. Daha kalıcı çözüm: sayıları dokümanda tekrar etmek yerine paragrafı `.github/workflows/ci.yml` içindeki `integration-test` job'ına referans vermekle sınırla; sayı gerekiyorsa check-doc-links.py yanına doküman↔workflow tutarlılığını grep'leyen küçük bir kapı ekle.

---

### 101. `tests` servisinin fail-closed scope guard'ı argümansız `dotnet test` ile atlanıyor ve yeşil sonuç üretiyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | test-quality |
| **Hat** | R16 — Compose, solution, build konfigürasyonu |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `.github/scripts/run-local-tests.sh:13-22 ↔ docker-compose.yml:477-494` |

**Bulgu.** `run-local-tests.sh` yalnız *argüman metni* üzerinde filtre yapıyor: `case "$argument" in *Saydin.Services.sln*|*IntegrationTests*|*Saydin.DatabaseMigrator.Tests*) ... exit 64`. Ancak `tests` servisinin `working_dir: /src` (docker compose config ile doğrulandı) ve `/src` altında tam olarak bir `.sln` var (`Saydin.Services.sln`). `docker compose run --rm tests test` → `exec dotnet test` → MSBuild cwd'deki tek solution'ı çözer ve tüm çözümü, integration test projeleri dahil, çalıştırır. Integration testler `SkippableFact` + `Skip.IfNot(db.Available, ...)` kullandığı için (tests/Saydin.Api.IntegrationTests/ActivityLogWriterIntegrationTests.cs:21-24) DB kimlik bilgisi olmayan bu serviste hepsi *skip* olur ve `dotnet test` exit 0 döner. Guard'ın kendi yorumu bunu açıkça yasaklamak istiyor: "Refuse commands which would otherwise produce skips or fixture failures and look like a successful local integration gate."

**Etki.** Yeni eklenen fail-closed kapı tam olarak engellemek için yazıldığı senaryoda fail-open. Geliştirici gerçek PG/Redis kabulünü çalıştırdığını sanarak yanlış güvenle commit atabilir; guard'ın verdiği güvence gerçek değil.

**Öneri.** Argüman metni yerine davranışı kapat: (a) `tests` servisine `working_dir: /src/tests` gibi solution içermeyen bir dizin ver ya da (b) `run-local-tests.sh` içinde argüman listesinde `test`/`build`/`run` varsa mutlaka açık bir proje yolu talep et (`(($# < 2))` → reject) ve ek olarak `dotnet test`e `--filter Category!=Integration` yerine `RunConfiguration` seviyesinde zero-skip zorlaması ekle. `validate-development-compose.py`'daki `local_test_scope_contract` mutasyon setine "argümansız `test`" vakasını da ekle.

---

### 102. Pre-migration `database-role-bootstrap`, gerçek replication authentication yaptığı halde `database-backup-hba` kapısını taşımıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R16 — Compose, solution, build konfigürasyonu |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `docker-compose.yml:261-302 (database-role-bootstrap depends_on) ↔ src/Saydin.DatabaseRoleBootstrap/RoleBootstrapRunner.cs:40-50` |

**Bulgu.** Post-migration servis doğru şekilde `database-backup-hba: service_completed_successfully` ile kapılanmış (docker-compose.yml:338-347), ancak *aynı* command'i çalıştıran pre-migration `database-role-bootstrap` yalnız `postgres: service_healthy` ile kapılanıyor (satır 300-302). RoleBootstrapRunner.cs:40-50: `case BootstrapCommand.Ensure:` → `if (ensureResult.BackupManaged) await AuthenticateBackupAsync(...)`. `BackupManaged`, `IsBackupPhaseReadyAsync` (RoleBootstrapDatabaseOperations.cs:530+) `saydin_role_contract` tablosunu görünce true olur — yani 022 uygulanmış her mevcut veritabanında ikinci ve sonraki `up` koşularında pre-migration bootstrap fiziksel replication bağlantısı açmayı dener. Bu bağlantı yalnız `database-backup-hba`'nın yazdığı HBA kurallarıyla mümkün; başarısızlıkta RoleBootstrapDatabaseOperations `backup_authentication_failed` → exit 69 döner ve migrator dahil tüm downstream başlamaz.

**Etki.** Tek bir HBA hatası, kök nedenle ilgisiz ve yanıltıcı bir `backup_authentication_failed` teşhisiyle tüm dev stack'ini bloke eder; geliştirici HBA servisine bakmak yerine credential'larda hata arar. CI smoke yalnız temiz (fresh) yolu kanıtladığı için bu regresyon kapıda yakalanmaz.

**Öneri.** `database-role-bootstrap` servisine de `database-backup-hba: {condition: service_completed_successfully}` ekle (backup-hba yalnız `postgres: service_healthy`e bağlı olduğu için döngü oluşmaz) ve `validate-development-compose.py`'a bu kapıyı da mutasyon testiyle bağla. Alternatif olarak `run-development-compose-smoke.sh`'a "aynı volume üzerinde ikinci `up`" adımını ekleyip idempotent yeniden başlatmayı da kanıtla.

---

### 103. Salt-unit hâline gelen `tests` servisi hâlâ tüm veritabanı zincirine ve Redis'e bağlı

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | developer-experience |
| **Hat** | R16 — Compose, solution, build konfigürasyonu |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `docker-compose.yml:477-494` |

**Bulgu.** Servisin entrypoint'i artık `run-local-tests.sh` ve argümansız çalışmada yalnız `run-unit-coverage.sh` (7 unit projesi, DB/Redis kullanmaz) koşuyor; servis yorumu da "purpose-specific DB credential taşımaz" diyor. Buna rağmen `depends_on: database-role-bootstrap-post-migration: service_completed_successfully` + `redis: service_healthy` korunmuş (satır 489-493) ve `ConnectionStrings__Redis` env'i (satır 485) hiçbir unit testte kullanılmıyor. Sonuç: `docker compose run --rm tests` postgres + secret-source-generator + secret-materializer + database-identity + database-backup-hba (pg_hba.conf mutasyonu + `pg_reload_conf()`) + iki role-bootstrap + migrator zincirini ayağa kaldırıyor.

**Etki.** Commit öncesi kapı dakikalar sürüyor, DB stack'i sağlıklı değilse hiç çalışmıyor ve bir unit test koşusu üretim benzeri bir kontrol düzlemini mutasyona uğratıyor — bu üç etki de kapının kullanılmama olasılığını artırıyor. `--no-deps` çıkış yolu hiçbir dokümanda yazmıyor.

**Öneri.** `tests` servisinden `depends_on` ve `ConnectionStrings__Redis`'i kaldır (integration kabulü zaten `.github/compose.integration.yml`'de). Gerçekten DB'li bir yerel deneme isteniyorsa bunu ayrı bir `tests-integration` servisine taşı. Geçici çözüm olarak CLAUDE.md/development-guide'a `--no-deps` bayrağını ekle ve `validate-development-compose.py`'a "unit test servisi DB'ye bağlı olmamalı" kuralını yaz.

---

### 104. Dev backup login'i 60 günde sessizce sona eriyor; dev tarafı yenileme prosedürü belgelenmemiş

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R16 — Compose, solution, build konfigürasyonu |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `docker-compose.yml:166-167 (database-identity), 279-280 (--backup-v1-valid-until), 325 (SAYDIN_BACKUP_V1_VALID_UNTIL)` |

**Bulgu.** `database-identity` `SAYDIN_BACKUP_V1_VALID_UNTIL` değerini `((clock_timestamp() AT TIME ZONE 'UTC')::date + 60)` olarak üretip `.env.database-runtime`'a yazıyor; bu dosya kalıcı ve gitignored. RoleBootstrapDatabaseOperations.cs:795-801 `VerifyBackupIsolationAndAvailabilityAsync` içinde `if (backups.All(role => role.ValidUntilUtc!.Value < now.AddHours(24))) throw TopologyRejected("backup_role_rotation_horizon_insufficient")`. Yani metadata üretiminden ~59 gün sonra her `docker compose up` post-migration bootstrap'ta bu kodla durur ve `saydin-api` dahil hiçbir downstream servis başlamaz. `docs/runbooks/backup-login-renewal.md` yalnız production imzalı deployment akışını anlatıyor ("Update SAYDIN_BACKUP_V1_VALID_UNTIL in the non-secret production configuration and run the normal signed deployment"); `docs/development-guide.md` ve README'de dev için "bootstrap-dev-database.sh'i yeniden çalıştır" adımı hiç yok. Ek karışıklık: `database-identity` profilsiz olduğu için her `docker compose up`'ta yeniden çalışıp log'a *kullanılmayan* yeni bir tarih basıyor; log'daki değer fiilen kullanılan değerden farklı.

**Etki.** Zaman bombası niteliğinde dev ortam arızası; teşhis edilmesi zor, çünkü hata mesajı yenileme yolunu göstermiyor ve doküman araması boş dönüyor. Uzun süreli dallarda / yeni katılan geliştiricide tekrarlanabilir.

**Öneri.** (1) `docs/development-guide.md`'ye "Backup login süresi doldu" başlığı ekle: `backup_role_rotation_horizon_insufficient` görüldüğünde `./infrastructure/secrets/bootstrap-dev-database.sh` yeniden çalıştırılır (bootstrap tarihi ileriye taşır, `ExtendManagedBackupValidityAsync` forward-only uzatmayı destekler). (2) `bootstrap-dev-database.sh`'a mevcut `.env.database-runtime` içindeki tarih 30 günden yakınsa uyarı bas. (3) `database-identity` servisini `devtools`/dedicated bir profile al ya da adını `database-identity-oneshot` yapıp `docker compose up`'ın default setinden çıkar — böylece log'daki yanıltıcı ikinci tarih üretilmez.

---

### 105. Activity-log yazıcısının catch-all dalı bilinmeyen SQLSTATE'te tüm API host'unu düşürüyor (PARTIALLY-FIXED)

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | operability |
| **Hat** | R17 — REMEDIATION DENETİMİ |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/BackgroundServices/ActivityLogBatchStore.cs (Classify, catch-all FatalHost dalları); src/Saydin.Api/BackgroundServices/ActivityLogWriter.cs (FlushAsync FatalHost dalı, ExecuteAsync); src/Saydin.Api/Program.cs (HostOptions ayarı yok)` |

**Bulgu.** Bilinen SQLSTATE listesi genişletildi ve 57P01/53300 gibi somut örnekler kapandı; ancak enümere edilmeyen her SQLSTATE (25006, 58030, 58P01, 54000, XX000) ve her PostgresException olmayan non-transient hata hâlâ FatalHost'a düşüp exception'ı ExecuteAsync dışına fırlatıyor. API HostOptions ayarlamadığı için bu, denetim (audit) yolundaki bir hatanın tüm ürün API host'unu durdurması demektir.

**Etki.** Kritik olmayan audit yazma yolundaki bilinmeyen bir DB hata sınıfı tüm ürün API'sini düşürüp `restart: unless-stopped` altında crash-loop üretebilir; her restart kuyruktaki activity log'ları kaybeder. `07-remediation-progress.md`'nin "writer-local bounded recovery" özeti catch-all dalı için geçerli değil.

**Öneri.** Bilinmeyen SQLSTATE varsayılanını TransientBatch (bounded retry sonrası drop + metrik) yap; FatalHost'u yalnız açıkça enümere edilmiş şema/yetki sınıflarıyla sınırla. Ek olarak API'de `HostOptions.BackgroundServiceExceptionBehavior = Ignore` + `saydin_activity_log_writer_stopped` metriği + critical alert ile fail-fast'i yalnız writer'a lokalize et. Test setine 25006 ve 58030 ekle.

---

### 106. CGNAT arkasındaki mobil kullanıcılar için registration kapısı public IP başına 5/gün ile kilitleniyor; cache-strategy.md NAT davranışını ters anlatıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | product-ux |
| **Hat** | R17 — REMEDIATION DENETİMİ |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Security/DistributedSecurityLimiter.cs (TryAcquireRegistrationAsync, TryNormalizeAddress); src/Saydin.Api/appsettings.json:37-41; src/Saydin.Api/Security/DistributedSecurityLimiterMiddleware.cs (TryGetTrustedClientAddress); docs/cache-strategy.md (registration paragrafı)` |

**Bulgu.** Registration exact bucket anahtarı tam istemci IP'sinin HMAC pseudonym'idir; CGNAT arkasındaki tüm aboneler aynı bucket'ı paylaşır ve public IP başına günde 5 (saatte 3) kayıtla sınırlanır. `docs/cache-strategy.md`'deki NAT ifadesi olgusal olarak yanlıştır. Aynı /24 için 500/gün hesaplama sınırı da paylaşılan bir tavan üretir.

**Etki.** Türkiye'de en yaygın erişim yolu olan CGNAT mobil ağlarda yeni kullanıcı onboarding'i ve hesaplama akışı meşru kullanıcılar için bloklanabilir; doküman bu riski kapatılmış gibi anlattığı için operatör/geliştirici yanlış varsayımla ilerler.

**Öneri.** (a) cache-strategy.md'deki NAT cümlesini düzelt. (b) Registration'ı attestation (App Attest/Play Integrity) veya proof-of-work gibi kimlik sinyaline bağla; IP cap'i yalnız sinyal yoksa uygula. (c) Bilinen CGNAT/mobil ASN'ler için ayrı ve ölçüme dayalı bucket sınıfı tanımla; SecurityAdmissionTelemetry bucket/outcome dağılımını dashboard'la ve alarm kur. (d) 429 yanıtına registration ve calculation'ı ayırt eden lokalize `code` ekle.

---

### 107. DR düzeltmelerinin tek davranışsal kanıtı Docker'ın varlığına koşullu ve kontrol sayısı hiçbir yerde pinlenmemiş

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | test-quality |
| **Hat** | R17 — REMEDIATION DENETİMİ |
| **Doğrulama** | CONFIRMED |
| **Konum** | `infrastructure/backup/tests/backup-static-self-test.py (docker koşullu blok ve son özet satırı); .github/workflows/ci.yml:115-121; .github/scripts/validate-workflows.py (ratchet yok)` |

**Bulgu.** İddia doğru. Tek fark: GitHub-hosted `ubuntu-latest` runner'da Docker her zaman mevcut olduğu için kapı bugün fiilen çalışıyor; risk, runner/daemon konfigürasyonu değiştiğinde kapının sessizce boşalması ve buna karşı hiçbir sayı ratchet'inin bulunmamasıdır.

**Etki.** #13 (`--cap-add CHOWN`) ve #14 (disk-backed staging) için tek davranışsal regresyon koruması, hiçbir uyarı üretmeden devre dışı kalabilir; PITR/DR güvencesi sessizce kaybolur ve CI yeşil kalır.

**Öneri.** Docker smoke'larını `required` dict'ine KOŞULSUZ ekle (Docker yoksa `False` yazıp fail et; lokal geliştirici için açık `--allow-no-docker` bayrağı bırak, CI'da verme). Ayrıca `backup_static_self_test_passed:<n>` beklenen sayısını validate-workflows.py'a release manifest self-test'teki gibi ratchet olarak pinle.

---

### 108. Permanent-blocked ingestion lane'i için operatör kurtarma yolu alarm→runbook zincirinde yok

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R17 — REMEDIATION DENETİMİ |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.PriceIngestion/Workers/BaseAssetWorker.cs (RecordPermanentBlocked); docs/runbooks/ingestion-stale.md; docs/runbooks/data-repair.md; infrastructure/prometheus/rules/ingestion.yml (runbook_url)` |

**Bulgu.** İddia doğru ve aslında bir adım daha kötü: yalnız `ingestion-stale.md` permanent-window kurtarmasını anlatmıyor değil, `data-repair.md` de `requeue_permanent_window` planını hiç anmıyor. Alarmdan kurtarma prosedürüne giden hiçbir doküman yolu yok; plan tipi yalnız kaynak kodda (SignedRepairPlan/RepairDatabase) ve testlerde adlandırılmış.

**Etki.** Tek bir asset'in permanent window izolasyonu nedeniyle çalan SaydinDailyIngestionStale alarmında nöbetçi operatör, durumun ne olduğunu ve tek çıkış yolunun imzalı DataRepair planı olduğunu runbook zincirinden öğrenemez; MTTR uzar ve runbook adım 2'nin yasakladığı worker restart'ı denenmeye açıktır.

**Öneri.** (a) SaydinMetrics'e bounded label'lı (`source`,`job_type`,`outcome_code`) `saydin_ingestion_permanent_blocked` sayacı ekle ve ayrı critical alert tanımla. (b) ingestion-stale.md'ye `ingestion_windows.state='permanent_failed'` teşhis sorgusu ve data-repair.md'ye link içeren bir adım ekle. (c) data-repair.md'de `requeue_permanent_window` planını açıkça belgele. (d) check-doc-links.py'a alert runbook'unun ilgili kurtarma runbook'una link verdiğini doğrulayan kural ekle.

---

### 109. DCA reel getirisi ara katkı ayları için exact-only kaldığından, her ayın ilk günlerinde tüm reel getiri null'a düşüyor ve /calculate ile çelişiyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | product-ux |
| **Hat** | R17 — REMEDIATION DENETİMİ |
| **Doğrulama** | CONFIRMED |
| **Konum** | `src/Saydin.Api/Services/DcaCalculator.cs (requiredExactMonths / missingMonths dalı / cache koşulu); src/Saydin.Api/Repositories/InflationRepository.cs (GetExactIndexValuesAsync vs GetIndexValuesAsync/GetNearestRowAsync); src/Saydin.Api/Services/WhatIfCalculator.cs` |

**Bulgu.** Terminal ay LKV ile çözüldüğü için #5 kapandı; ancak M-1 gibi ara katkı ayları hâlâ exact-only. TÜİK M-1 TÜFE'sini tipik olarak ayın 3'ünde yayınladığından, ayın 1-3'ü arasında M-1'de katkısı olan (yani neredeyse tüm) aylık DCA planlarında tüm reel getiri alanları null döner; aynı anda /calculate LKV kullandığı için reel getiri gösterir. Bu istekler ayrıca hiç cache'lenmez.

**Etki.** Enflasyona göre düzeltilmiş getiri özelliği her ay birkaç gün için kapanıyor, aynı üründe iki ekran çelişiyor ve kullanıcıya tek sinyal jenerik bir uyarı kodu oluyor; cache'lenmeme nedeniyle bu pencerede DB yükü de artıyor.

**Öneri.** Ara aylar için de kademeli sözleşme uygula: eksik ara ay için `period_date <= o ay` en son final gözlemi deflatör kabul et, kullanılan ayı `InflationDataAsOf` ile bildir ve `RealReturnMethod`'u ayırt edici bir değere çevir (örn. `cashflow_cpi_lkv_v1`); yalnız hiç gözlem yoksa null'a düş. `inflationCalculationComplete=false` yolunu kısa TTL ile cache'le. FakeTimeProvider ile "ayın 2'si, M-1 CPI'ı yok" senaryosunu kilitleyen test ekle.

---

### 110. `07-remediation-progress.md`'nin "repo kapsamında açık kusur kalmadı" iddiası desteklenmiyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | documentation |
| **Hat** | R17 — REMEDIATION DENETİMİ |
| **Doğrulama** | CONFIRMED |
| **Konum** | `docs/analysis/pr-review/07-remediation-progress.md (Sonuç bölümü ve "Otoritatif test kanıtı" tablosu)` |

**Bulgu.** Belge, düzeltmelerin çoğunun gerçekten kök nedeni kapattığı doğru olmakla birlikte, blanket "açık kusur kalmadı" iddiasıyla en az dört doğrulanmış residual yüzeyi gizliyor ve pinlenmemiş sayılara (backup static 57, TRX/coverage tablosu) otorite atfediyor.

**Etki.** Belge production promotion kararını yönlendirdiği için ("dört dış kabul koşulu dışında repo hazır"), release kararı eksik bilgiyle alınır; bir sonraki reviewer bu yüzeyleri yeniden denetlemez.

**Öneri.** "Bilinen residual" bölümü ekle ve R17-01/02/03/04'ü açık yüzey olarak listele; "kusur kalmadı" cümlesini "envanterdeki bulgular için kök-neden düzeltmeleri uygulandı; aşağıdaki residual yüzeyler bilinçli olarak açık" ile değiştir. Test kanıt tablosunu bir CI artefaktına bağla veya sayıları çıkarıp yalnız CI'daki `--minimum-executed` ratchet'lerini otorite say.

---

### 111. GET /v1/scenarios/page artık OpenAPI'de limit/cursor query parametrelerini bildirmiyor — sayfalama codegen'de görünmez oldu

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | product-ux |
| **Hat** | R18 — ÜRÜN VE GELİŞTİRİCİ DENEYİMİ |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `src/Saydin.Api/Endpoints/ScenariosEndpoints.cs:65-69,156-190; tests/Saydin.Api.Tests/Endpoints/OpenApiSemanticContractTests.cs:60-70` |

**Bulgu.** Diff öncesi handler imzası `GetScenarioPageAsync(int? limit, string? cursor, HttpContext, ...)` idi; Minimal API bunları query parametresi olarak bind eder ve .NET 10 OpenAPI üreticisi `parameters` altına yazar. Yeni imza `GetScenarioPageAsync(HttpContext, ISavedScenarioService, IStringLocalizer, CancellationToken)` — tüm parametreler servis/context, dolayısıyla operasyon `parameters` listesi boş üretiliyor. Değerler artık `ParsePageQuery(httpContext.Request.Query, localizer)` ile elle okunuyor. OpenApiSemanticContractTests yalnız status kodu setlerini (`[("/v1/scenarios/page", "get")] = ["200","400","401","429","503"]`) doğruluyor, hiçbir yerde `parameters` assert edilmiyor.

**Etki.** ADR-008'in ana ürün çıktısı olan cursor sayfalaması makine-okunur sözleşmede kayboldu. Sözleşme testi bu regresyonu yakalamıyor çünkü yalnız yanıt kodlarını denetliyor.

**Öneri.** Query bağlamayı imzada koru ve doğrulamayı bind sonrası yap — ör. `[FromQuery] string? limit, [FromQuery] string? cursor` alıp `ParsePageQuery` içinde parse et; ya da imzayı koruyup `.WithOpenApi(op => { op.Parameters.Add(...); return op; })` ile parametreleri açıkça bildir. Ayrıca OpenApiSemanticContractTests'e operasyon başına beklenen `parameters` adı/in/required setini ekle (`/v1/scenarios/page` → `limit:query`, `cursor:query`; `/v1/scenarios/{id}` → `id:path`), böylece parametre kaybı fail-closed olur. Ek olarak `ScenarioPageLimitInvalid` mesajı ('limit 1 ile 100 arasında olmalıdır') `limit=abc` parse hatası için de kullanılıyor; parse ve aralık hatalarına ayrı mesaj ver.

---

### 112. ProblemDetails `field` uzantısı bazen C# property adı (PascalCase), bazen wire adı (camelCase) — istemci tek bir eşleme kuralı kuramıyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | product-ux |
| **Hat** | R18 — ÜRÜN VE GELİŞTİRİCİ DENEYİMİ |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `src/Saydin.Api/Endpoints/ScenariosEndpoints.cs:177,187; src/Saydin.Api/Services/DcaCalculator.cs:84,123; src/Saydin.Api/Services/WhatIfCalculator.cs:50,629; src/Saydin.Api/Repositories/SavedScenarioRepository.cs:152-161; src/Saydin.Api/Endpoints/AssetsEndpoints.cs:168` |

**Bulgu.** Bu diff aynı anda iki konvansiyon ekliyor: `field: "limit"` / `field: "cursor"` (ScenariosEndpoints.cs:177,187 — wire adı) ve `field: nameof(request.EndDate)` → `"EndDate"` (DcaCalculator.cs:84,123 — PascalCase). Mevcut kod tabanında da her iki taraf var: `"dateRange"`, `"request"` vs. `"ExtraData"`, `"Type"`, `"AmountType"`, `"SellDate"`. JSON request property'leri camelCase serialize edildiği için `"SellDate"` değeri istemcinin gönderdiği `sellDate` alanıyla birebir eşleşmiyor. ValidationExceptionHandler.cs:47-48 değeri olduğu gibi `extensions["field"]` içine yazıyor.

**Etki.** Hata deneyimi eyleme dönüştürülemiyor: istemci hangi girdinin hatalı olduğunu güvenilir şekilde işaretleyemiyor. Bir endpoint'i öğrenen geliştirici diğerini tahmin edemiyor.

**Öneri.** Tek kural belirle ve zorla: `field` her zaman **wire adı** (JSON property adı, camelCase; query parametresi için query adı) olsun. `nameof(request.EndDate)` kullanımlarını `"endDate"` gibi sabitlerle veya `JsonNamingPolicy.CamelCase.ConvertName(nameof(...))` ile değiştir. Mevcut PascalCase değerleri (`ExtraData`, `Type`, `AmountType`, `SellDate`) tek seferde çevir ve `ExceptionHandlerContractTests`'e 'her `field` değeri `^[a-z][A-Za-z0-9.]*$` desenine uyar' fail-closed assert'i ekle. Bu bir breaking change olduğu için meta repo api-contract.md'de ve release note'ta bildir.

---

### 113. İki farklı 404 şekli: port-boundary reddi RFC 7807 gövdesi döner, eşleşmeyen normal route boş gövde döner

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | product-ux |
| **Hat** | R18 — ÜRÜN VE GELİŞTİRİCİ DENEYİMİ |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `src/Saydin.Api/Middleware/ApiPortBoundaryMiddleware.cs:18-35; src/Saydin.Api/Program.cs (MapFallback/UseStatusCodePages yok)` |

**Bulgu.** Bu diff `ApiPortRequestKind.Rejected` yoluna tam ProblemDetails gövdesi ekliyor: `Type=https://saydin.app/errors/route-not-found`, `code=route_not_found`, `traceId` ve lokalize `RouteNotFound`/`RouteNotFoundDetail`. Buna karşılık `Program.cs` içinde ne `MapFallback` ne `UseStatusCodePages` var; public port'ta eşleşmeyen bir yol (`GET /v1/scenariso`) routing tarafından gövdesiz 404 alır — `code` yok, `traceId` yok, `Content-Type` problem+json değil.

**Etki.** Hata sözleşmesi kısmen tutarlı: yeni eklenen 404 birinci sınıf, mevcut 404 hiç sözleşmeye uymuyor. Ayrıca `traceId` olmadığı için bir istemci hata raporunu sunucu trace'iyle ilişkilendirmek imkânsız.

**Öneri.** `Program.cs`'e `app.MapFallback(...)` veya `app.UseStatusCodePages(async ctx => ...)` ekleyip aynı `route_not_found` problem gövdesini üret; gövde oluşturmayı `ApiPortBoundaryMiddleware`'den ortak bir `RouteNotFoundProblem.WriteAsync(context, localizer)` helper'ına çıkar ve her iki yol da onu çağırsın. `ExceptionHandlerContractTests`'e 'public port'ta tanımsız bir yol problem+json ve `code=route_not_found` döner' testi ekle. Aynı kuralı 405 için de düşün.

---

### 114. Yeni security admission metriği ölçülüyor ama alarma, zero-init'e ve runtime kontrat doğrulamasına bağlanmadı

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R18 — ÜRÜN VE GELİŞTİRİCİ DENEYİMİ |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `src/Saydin.Shared/Diagnostics/SaydinMetrics.cs:172-195; infrastructure/prometheus/rules/api.yml (tamamı); infrastructure/deployment/validate-prometheus-runtime.py:81-88; docs/runbooks/api-availability.md:14; docs/runbooks/redis-unavailable.md:11` |

**Bulgu.** `SaydinMetrics.SecurityAdmissionDecisions` eklendi ve iki runbook operatöre `saydin_security_admission_decisions_total{outcome="unavailable"}` serisini `bucket,reason` ile gruplamasını söylüyor. Ancak: (1) `infrastructure/prometheus/rules/api.yml` içinde bu metriğe dayanan tek bir alert yok — 5 alert'in hiçbiri admission'a bakmıyor; (2) aynı commit'te eklenen `SaydinMetrics.InitializeActivityLogContractSeries()` activity-log sayaçlarını sıfır değerle materyalize ediyor ama admission sayacını etmiyor; (3) `validate-prometheus-runtime.py:81-84` zorunlu seri/label listesinde `saydin_activity_log_*` ve `saydin_process_start_time_seconds` var, `saydin_security_admission_decisions_total` yok.

**Etki.** Yeni fail-closed kapı için 'bu tetiklenirse ne yapılır' cevabı yazıldı ama operatörü oraya götürecek sinyal yok. Metriğin adı/label şeması canlı scrape admission'ında doğrulanmadığı için sessizce yanlış adla yayınlansa fark edilmez.

**Öneri.** (1) `api.yml`'ye iki alert ekle: `SaydinSecurityAdmissionUnavailable` (`sum(increase(saydin_security_admission_decisions_total{outcome="unavailable"}[10m])) > 0`, severity critical, runbook_url=api-availability.md) ve `SaydinSecurityAdmissionLimitedSpike` (warning, runbook_url=api-availability.md); `inventory.test.yml`'ye her ikisi için pozitif ve negatif fixture ekle. (2) `InitializeActivityLogContractSeries`'i `InitializeMetricContractSeries` olarak genelleştirip `SecurityAdmissionDecisions.Add(0, bucket=network, outcome=allowed, reason=allowed)` sıfır serisini de yayınla. (3) `validate-prometheus-runtime.py:81` sözlüğüne `"saydin_security_admission_decisions_total": {"job","bucket","outcome","reason"}` ekle. (4) `SaydinApiErrorBudgetBurn` runbook_url'ini admission bölümüne çapa ile yönlendir.

---

### 115. 503 admission ve quota yanıtları `Retry-After` taşımıyor — istemciye ne zaman deneyeceği söylenmiyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | product-ux |
| **Hat** | R18 — ÜRÜN VE GELİŞTİRİCİ DENEYİMİ |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `src/Saydin.Api/Security/SecurityAdmissionProblem.cs:27-35,39-46; src/Saydin.Api/Exceptions/QuotaUnavailableExceptionHandler.cs:19-33` |

**Bulgu.** `SecurityAdmissionProblem.SetRetryAfter` yalnız `SecurityLimiterOutcome.Limited` (429) yolunda çağrılıyor; `Unavailable` (503) yolunda hiç çağrılmıyor. `QuotaUnavailableExceptionHandler` de 503 dönerken Retry-After yazmıyor. Detail metinleri ise 'Lütfen daha sonra tekrar deneyin' / 'Please try again later' diyor — 'daha sonra'nın ne kadar olduğu belirtilmiyor.

**Etki.** Fail-closed davranış doğru ama istemci sözleşmesi eksik: 503'ler eyleme dönüştürülebilir değil. Retry davranışı istemci tahminine bırakılıyor, bu da kesinti sırasında thundering-herd riski yaratıyor.

**Öneri.** 503 admission ve quota yanıtlarına bounded, jitter'lı bir `Retry-After` (ör. 5-15 sn) ekle ve aynı değeri ProblemDetails `extensions["retryAfterSeconds"]` olarak da yaz (header'a erişimi kısıtlı istemciler için). `SetRetryAfter`'ı `SecurityAdmissionProblem` içinde her iki yolda da çağır; `QuotaUnavailableExceptionHandler`'a aynı helper'ı ver. `ExceptionHandlerContractTests`'e 'her 429/503 yanıtı Retry-After taşır' assert'i ekle.

---

### 116. CanonicalJson iki ayrı kopyada yaşamaya devam ediyor ve MaxDepth kontratları farklı; parity testi bu farkı kapsamıyor ve tautolojik

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `defect` |
| **Boyut** | correctness |
| **Hat** | R18 — ÜRÜN VE GELİŞTİRİCİ DENEYİMİ |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `src/Saydin.DataQualityAudit/CanonicalJson.cs:10-14; src/Saydin.DataRepair/CanonicalJson.cs:8-16; src/Saydin.DataRepair/DqaEvidenceVerifier.cs:31; tests/Saydin.DataRepair.Tests/CanonicalJsonParityTests.cs:11-32` |

**Bulgu.** DataRepair kopyası `JsonDocumentOptions { AllowTrailingCommas=false, CommentHandling=Disallow, MaxDepth=32 }` ile parse ediyor; DQA kopyası `JsonDocument.Parse(json.ToArray())` — varsayılan MaxDepth=64 ile. `DqaEvidenceVerifier.cs:31` DQA'nın ürettiği manifest baytlarını **DataRepair'in** canonicalizer'ıyla yeniden kanonikleştiriyor. Parity testi yalnız 3 sığ InlineData ile bayt eşitliği, 2 InlineData ile `Should().Throw<Exception>()` (herhangi bir exception — NullReference dahil geçer) doğruluyor; derinlik sınırı hiç test edilmiyor. Metot adları da ayrışmış (`SerializeCanonical` vs `Serialize`, `WriteElement` vs `Write`).

**Etki.** İki kopya arasındaki kontrat farkı, imzalı kanıt zincirinin iki ucunda farklı kabul kuralı demektir. Bir olay sırasında onarım aracının sessizce reddetmesi en kötü zamanda ortaya çıkar. Reflection'la yazılmış parity testi de bu ayrışmayı yakalayamıyor — sadece kopyaların var olduğunu belgeliyor.

**Öneri.** Kopyaları birleştir: `Saydin.Shared` (veya küçük bir `Saydin.CanonicalJson` projesi ya da paylaşılan `<Compile Include="../shared/CanonicalJson.cs">` link'i) altında tek bir `CanonicalJson` bırak ve iki tool da onu referans alsın — böylece reflection tabanlı parity testine hiç gerek kalmaz. Birleştirme kısa vadede mümkün değilse en azından `JsonDocumentOptions`'ı iki tarafta birebir aynı yap (MaxDepth=32 her ikisinde) ve parity testine (a) derinlik-32/33 sınır vakası, (b) `Should().Throw<Exception>()` yerine `Should().Throw<JsonException>()` gibi tipli assert, (c) property sırası/unicode/negatif sayı için property-based bir üretici ekle.

---

### 117. Aynı admission problem gövdesi iki kez, aynı reason→string eşlemesi üç kez yazılmış; kopyalar şimdiden ayrışmış

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | simplicity |
| **Hat** | R18 — ÜRÜN VE GELİŞTİRİCİ DENEYİMİ |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `src/Saydin.Api/Security/SecurityAdmissionProblem.cs:11-66; src/Saydin.Api/Security/DistributedSecurityLimiterMiddleware.cs:77-84; src/Saydin.Api/Endpoints/EndpointExtensions.cs:259-267; src/Saydin.Api/Security/SecurityAdmissionTelemetry.cs:42-49` |

**Bulgu.** `SecurityAdmissionProblem.Result()` (IResult) ve `SecurityAdmissionProblem.WriteAsync()` (HttpResponse) aynı iki problemi bağımsız iki kod yoluyla üretiyor — ve davranışları ayrışmış: `Result()` `Allowed` gelirse `ArgumentOutOfRangeException` fırlatıyor, `WriteAsync()` aynı girdiyi sessizce 503 'limiter unavailable' yapıyor. `SecurityLimiterReason` → stabil string eşlemesi üç yerde: `DistributedSecurityLimiterMiddleware.StableReason` (yalnız 3 case + `_ => "unexpected"`), `EndpointExtensions.StableReason` (5 case + `_ => "unexpected"`), `SecurityAdmissionTelemetry.Reason` (5 case + throw). Üç kopya zaten farklı davranıyor.

**Etki.** Yeni bir okuyucu için güvenlik admission hata yolunun tek bir otoritesi yok. Kopyalar arasındaki mevcut ayrışma (Allowed davranışı, eksik case'ler) zaten bir bakım tuzağı; telemetrinin sıcak yolda exception fırlatması ise admission reddini 500'e çevirebilir.

**Öneri.** Tek bir `SecurityAdmissionProblem.Build(HttpContext, IStringLocalizer, SecurityLimiterDecision) → (int status, ProblemDetails body, int? retryAfterSeconds)` fonksiyonu yaz; `Result()` ve `WriteAsync()` ikisi de bunu sarsın ve `Allowed` için ikisi de aynı şekilde (fırlatarak) davransın. `StableReason`'ı sil, tek otorite olarak `SecurityAdmissionTelemetry.Reason`'ı `internal static string Stable(SecurityLimiterReason)` olarak public'e çıkar ve üç çağıran da onu kullansın. `SecurityAdmissionTelemetry.Record`'daki `ArgumentOutOfRangeException`'ları istek yolunda fırlatmak yerine `Debug.Assert` + `"unexpected"` fallback'e çevir; kontratı bir unit testle (enum'un her değeri için eşleme var mı) kilitle.

---

### 118. Production limiter değerleri validate-production.py'de tam eşitlikle sabitlenmiş — kullanılabilirlik ayarı operatöre kapalı

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | operability |
| **Hat** | R18 — ÜRÜN VE GELİŞTİRİCİ DENEYİMİ |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `infrastructure/deployment/validate-production.py:224-232; infrastructure/deployment/compose.production.yml:196-200; src/Saydin.Api/Security/DistributedSecurityLimiterOptions.cs:25-38` |

**Bulgu.** `validate-production.py:224-232` `security_limits` sözlüğünde beş limiti string olarak birebir pinliyor (`"3"`,`"5"`,`"20"`,`"100"`,`"500"`) ve `any(api_env.get(key) != expected ...)` ise `security_limiter_production_limits_invalid` ile reddediyor. Buna karşılık `DistributedSecurityLimiterOptions.IsValid` zaten aralık ve sıralama (hourly ≤ daily, exact ≤ network) invariant'larını doğruluyor — yani kod tarafında güvenli bir aralık sözleşmesi mevcut.

**Etki.** Bir kullanılabilirlik/kapasite ayarı, statik bir eşitlik assert'iyle donduruldu. Fail-closed niyet doğru ama araç yanlış: acil durumda limitin yükseltilmesi bir doğrulama script'i düzenlemesini gerektiriyor.

**Öneri.** Eşitlik yerine bounded aralık + invariant doğrula: `3 <= RegistrationExactHourlyLimit <= RegistrationExactDailyLimit <= 100`, `RegistrationNetworkHourlyLimit <= RegistrationNetworkDailyLimit`, `100 <= CalculationNetworkDailyLimit <= 100000` gibi. Böylece 'limiter kapatılamaz / anlamsız değere çekilemez' güvencesi korunurken operatör olay sırasında tavanı yükseltebilir. Değişikliğin izlenebilir kalması için değerleri `saydin_security_limit_configured{bucket=...}` gauge'u olarak yayınla ve runbook'ta 'limit değiştirildiyse metrikten doğrula' adımı ekle.

---

### 119. /24 ağ bucket'ları CGNAT gerçeğiyle çatışıyor; günlük pencerede 429 mesajı dakikalık limitten ayırt edilemiyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | product-ux |
| **Hat** | R18 — ÜRÜN VE GELİŞTİRİCİ DENEYİMİ |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `src/Saydin.Api/Security/DistributedSecurityLimiter.cs:251-280 (TryNormalizeAddress), 133-197; src/Saydin.Api/Security/DistributedSecurityLimiterOptions.cs:25-38; docs/cache-strategy.md:181-187` |

**Bulgu.** `TryNormalizeAddress` IPv4 için `network[3]=0` (yani /24), IPv6 için ilk 8 bayt (/64) kullanıyor. Yeni bucket'lar: `RegistrationNetworkDailyLimit=100`, `CalculationNetworkDailyLimit=500` — her ikisi de **sabit takvim penceresi** (`floor(now_ms / 86400000)`), dolayısıyla `Retry-After` 24 saate kadar çıkabiliyor. 429 gövdesi ise dakikalık limiterle **aynı** başlığı (`RateLimited`) ve detay metnini (`SecurityRateLimitedDetail` — 'belirtilen süre sonunda tekrar deneyin') ve aynı `code=security_rate_limited` değerini taşıyor. docs/cache-strategy.md:184-185 'aynı NAT'taki farklı istemciler ise tekil IP registration bucket'larını paylaşmaz' diyerek asıl bağlayıcı olan **ağ** bucket'ının paylaşıldığını gölgeliyor; repo'da CGNAT ölçeği hiçbir yerde bu limitler bağlamında ele alınmamış (CGNAT yalnız GeoIP tarafında, MaxMindGeoIpResolver.cs:101 ve activity-logging.md:357'de kabul ediliyor).

**Etki.** Lansmanda toplu kullanılamazlık riski; kullanıcıya gösterilen mesaj ('bu istemciden çok fazla istek alındı') yanlış — istek onun değil, ağını paylaşan başkalarının. Ürün kotası (`DailyLimitExceeded`, lokalize ve anlamlı) ile güvenlik limiti aynı 429 altında ayırt edilemiyor.

**Öneri.** (1) Ağ bucket'ı için ayrı bir kod ve mesaj ayır: `code=security_network_rate_limited` + yeni resx anahtarları (`SecurityNetworkRateLimited`/`Detail`: 'Paylaşılan ağınızdan gelen istek sayısı sınıra ulaştı'), böylece istemci 'bir dakika bekle' ile 'paylaşılan ağ, yarın tekrar dene'yi ayırt edip doğru UX gösterebilsin. (2) `retryAfterSeconds`'ı ProblemDetails'a da koy (bkz. R18-06). (3) Sabit takvim penceresi yerine sliding/rolling pencere kullanarak 24 saatlik ceza yerine kademeli açılma sağla. (4) CGNAT'ı `docs/high-traffic-checklist.md` ve `docs/cache-strategy.md`'de açıkça yaz, ağ limitlerini 'lansman sonrası ilk telemetriye göre kalibre edilecek' olarak işaretle ve `saydin_security_admission_decisions_total{bucket="calculation_network",outcome="limited"}` üzerine bir warning alert'i koy. (5) cache-strategy.md:184-185'teki yanıltıcı NAT cümlesini düzelt.

---

### 120. Operatör CLI'ları `argument_required` gibi hangi argüman olduğunu söylemeyen kodlar döndürüyor; --help yok, kod→aksiyon tablosu yok

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | developer-experience |
| **Hat** | R18 — ÜRÜN VE GELİŞTİRİCİ DENEYİMİ |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `src/Saydin.DataRepair/RepairOptions.cs:49,80,151,191-192; src/Saydin.DataRepair/Program.cs:85; src/Saydin.DataQualityAudit/AuditOptions.cs:75,144,167; src/Saydin.DataRepair/README.md; docs/runbooks/data-repair.md` |

**Bulgu.** `RepairOptions.Parse` yedi zorunlu ortak anahtardan hangisi eksikse aynı `Invalid("argument_required")`'ı fırlatıyor (satır 80: `foreach (var key in CommonKeys) if (!values.ContainsKey(key)) throw Invalid("argument_required")`). `Program.cs:85` bunu `repair rejected: code=argument_required` olarak stderr'e yazıyor — başka hiçbir bağlam yok. `args.Length == 0` da aynı kodu üretiyor, yani `--help` yok. AuditOptions aynı deseni tekrarlıyor. Repo genelinde hiçbir `.sh`/CLI'da `--help`/`usage()` yok. `src/Saydin.DataRepair/README.md` ve `docs/runbooks/data-repair.md` çok detaylı ama hiçbirinde kod→anlam→aksiyon tablosu yok.

**Etki.** En yüksek riskli, en az sık çalıştırılan araç (imzalı operatör onarımı) aynı zamanda en zayıf hata ergonomisine sahip. Olay sırasında dakikalar kaynak kod okumaya gider ve yanlış denemeler ekstra deneme-yanılma turu üretir.

**Öneri.** (1) Kodu argüman adıyla zenginleştir — anahtar adları gizli değil: `Invalid($"argument_required:{key}")`. Aynısını `argument_invalid`, `signer_argument_mismatch` için de yap. (2) `args.Length == 0` veya `--help`/`-h` gelirse moda göre izin verilen anahtar listesini ve exit kod tablosunu stdout'a bas, exit 0 dön (calendar-data CLI'daki belgeli `Console` istisnasıyla aynı bounded sözleşme). (3) `src/Saydin.DataRepair/README.md`'ye ve `docs/runbooks/data-repair.md`'ye `code` → anlam → operatör aksiyonu tablosu ekle (`argument_required`, `command_unknown`, `repair_target_mismatch`, `repair_audit_identity_mismatch`, `postgres_*`, `database_transport`, `cancelled`). (4) Aynı iyileştirmeyi `Saydin.DataQualityAudit`'e uygula; iki araç aynı `Invalid(code)` desenini paylaştığı için ortak bir `CliRejection` helper'ına çıkarılabilir.

---

### 121. En ağır iki backup davranış smoke'u docker yoksa sessizce atlanıyor; geçiş satırı bunu bildirmiyor

| | |
|---|---|
| **Önem** | Medium |
| **Tip** | `excellence-gap` |
| **Boyut** | test-quality |
| **Hat** | R18 — ÜRÜN VE GELİŞTİRİCİ DENEYİMİ |
| **Doğrulama** | DOĞRULANMADI (yalnız üreten agent) |
| **Konum** | `infrastructure/backup/tests/backup-static-self-test.py:365-389; .github/workflows/ci.yml:120; tests/Saydin.Api.IntegrationTests/Fixtures/IntegrationTestEnvironment.cs:15` |

**Bulgu.** `if shutil.which("docker") is not None:` + `docker info` başarılıysa `restore-volume-init-smoke.py` (136 satır) ve `base-backup-behavior-smoke.py` (552 satır) çalıştırılıyor; docker yoksa `required` sözlüğüne bu iki anahtar hiç eklenmiyor ve script `backup_static_self_test_passed:{len(required)}` yazıp exit 0 dönüyor — atlandıklarına dair tek kelime yok, sadece sayı değişiyor. Buna karşılık repo'nun kendi idiomu fail-closed: integration testleri `SAYDIN_INTEGRATION_REQUIRED=true` ile 'skip yasak' kapısı kullanıyor ve CI 'sıfır failed/skipped/notExecuted' şartı arıyor.

**Etki.** DR/backup güvencesinin en davranışsal kısmı opsiyonel hale gelmiş durumda ve kaybı gözlemlenemiyor. Bu, repo'nun her yerde uyguladığı zero-skip disiplininin tek istisnası.

**Öneri.** Repo idiomunu uygula: `BACKUP_DOCKER_SMOKE_REQUIRED` ortam değişkeni ekle, CI'da (`ci.yml:120` adımında) `true` olarak set et. Değişken `true` iken docker bulunamazsa fail-closed çık (`backup_static_failed:docker_smoke_unavailable`). Değişken set değilse mevcut atlama davranışı sürsün ama çıktı satırı açıkça `backup_static_self_test_passed:{N} skipped:restore_volume_init_docker_smoke,base_backup_docker_behavior_smoke` yazsın, böylece atlama hiçbir zaman sessiz olmasın.

---
