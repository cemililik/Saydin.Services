# Saydin.Api / domain remediation planı

**Kaynak rapor:** [`../01-api-domain-review.md`](../01-api-domain-review.md)  
**Plan tarihi:** 18 Ağustos 2026  
**Kapsam:** API raporundaki 26 bulgunun tamamı  
**Kısıt:** Bu belge yalnız planlama çıktısıdır; production kodu değiştirmez.

## 1. Planlama ilkeleri

1. Beş High bulgu önce ele alınır. High düzeltmelerinin ürün/kontrat kararları Wave 0'da, uygulama ve release kanıtları Wave 1'de tamamlanmadan daha düşük öncelikli davranış değişiklikleri release edilmez.
2. Her work item kaynak bulgu kimliğini korur. “Duplicate” alanı yalnız `03-platform-docs-quality-review.md` ve `04-validation-and-cross-cutting-review.md` içindeki bağımsız kayıtlarla ilişki kurar:
   - **Tam:** aynı defect ve aynı temel çözüm.
   - **Kısmi:** ortak bileşen/risk var, fakat bu item'ın bütün acceptance kriterlerini karşılamaz.
   - **Yok:** 03/04'te aynı bulgu bulunmuyor.
3. Ürün kararı gerektiren item implementation'a girmeden kısa ADR/API-contract kararı alır. Karar verilemezse eski yanlış davranış sessizce korunmaz; ilgili özellik kontrollü olarak kapatılır veya açık bir degraded/unsupported sözleşmesi döner.
4. Her item ayrı davranış PR'ı olabilir; ancak aşağıdaki çakışma kümelerinde tek owner ve sıralı merge kullanılır. Toplu formatlama, dependency upgrade ve davranış değişikliği aynı PR'da karıştırılmaz.
5. Tahminler tek geliştirici için kod+test+review eforudur; client rollout, hukuk/onay ve production rollout bekleme süresini içermez.

### Karmaşıklık ölçeği

| Seviye | Yaklaşık efor | Anlamı |
|---|---:|---|
| XS | ≤1 gün | Lokal ve kontrat değiştirmeyen değişiklik |
| S | 1–3 gün | Az sayıda komponent, belirgin test yüzeyi |
| M | 3–5 gün | Birden çok katman veya gerçek altyapı testi |
| L | 5–10 gün | API/DB kontratı, migration ya da performans refactor'ı |
| XL | 10+ gün | Client migration, hukuk/operasyon veya çok aşamalı rollout |

## 2. Wave'ler ve global kabul kapıları

| Wave | Amaç | İçerik | Wave acceptance gate |
|---|---|---|---|
| **W0 — Karar ve baseline** | Koddan önce ürün, güvenlik, privacy ve matematik sözleşmesini sabitlemek | API-02/03/04/06/08/12/14/15/16/21 için ADR/contract; mevcut OpenAPI, metric, log ve performans baseline'ları | Her kararın owner'ı, seçeneği, backward-compatibility yaklaşımı, rollout/rollback ve ölçülebilir SLO'su onaylı; kararsız High item yok |
| **W1 — Release ve High risk** | Release güvenliği ve beş High bulguyu kapatmak; doğrulama kapısını fail-closed yapmak | API-01, 02, 03, 04, 06; ayrıca çapraz doğrulanmış API-07, 17, 20 | Audit açık temiz build; High/Critical vulnerability yok; unit 286+ ve gerçek PG/Redis integration skip=0; DCA exact fixture; auth/abuse, oversize ve log-sentinel testleri; fatal startup non-zero; channel drop metriği kanıtı |
| **W2 — Domain bütünlüğü ve performans** | DB/Redis yarışlarını, cache doğruluğunu ve hesaplama tutarlılığını düzeltmek | API-05, 08, 09, 10, 11, 12, 13, 18, 19 | Gerçek PG/Redis concurrency testleri; degraded-cache testi; aynı-count asset invalidation; sabit DCA query budget; tüm boundary/error-contract testleri yeşil |
| **W3 — Operasyon, privacy ve gözlemlenebilirlik** | Yönetim yüzeyi, activity log, health ve metrik sözleşmelerini üretime hazır yapmak | API-14, 15, 16, 21, 22, 23, 25, 26 | Internal-only management smoke; retention/deletion drill; transient/toxic fault injection; MeterListener ölçümleri; invalid config fail-fast; deterministic clock testleri |
| **W4 — Kontrat kapanışı** | Son gerçek runtime davranışından OpenAPI ve client sözleşmesini üretmek | API-24 ve bütün semantic snapshot/release-note işleri | OpenAPI semantic snapshot runtime status/code matrisiyle bire bir; generated client smoke; tüm 26 item'ın evidence link'i ve owner sign-off'u mevcut |

W1 kapısı, W2/W3 geliştirmesinin başlamasını değil production'a çıkmasını sınırlar; bağımsız dallarda hazırlık yapılabilir. Aynı dosya kümelerinde aşağıdaki merge sırası korunur.

## 3. Dosya/komponent çakışma haritası

| Küme | Work item'lar | Başlıca dosya/komponent | Önerilen sıra ve sahiplik |
|---|---|---|---|
| **C1 — Bootstrap/pipeline** | 07, 14, 15, 20, 21, 23, 25, 26 | `Program.cs`, DI/options, middleware sırası, health/metrics, channel | Tek API platform owner'ı. Sıra: 20 → 25 → 07 → 21/15 → 14 → 23/26. Her merge sonrası process/HTTP smoke çalıştırılır. |
| **C2 — WhatIf/DCA** | 04, 06, 08, 12, 13, 18, 19, 23 | `DcaCalculator`, `WhatIfCalculator`, response modelleri, asset/inflation repository'leri, cache keys | Tek domain owner'ı. 04 matematik/kontrat önce; 13 bulk data yolu sonra; 18/19 guard'lar; 12 effective dates; 08 cache quality; 06 redaction ve 23 metric en son. Cache/version değişiklikleri bir kez konsolide edilir. |
| **C3 — SavedScenario/kimlik** | 02, 03, 05, 11, 24 | endpoint/request/response, `SavedScenarioService/Repository`, `User`, EF config/migration | Auth principal abstraction'ı (02) önce ayrı seam olarak eklenir. Ardından 11 validation → 03 limits/pagination → 05 atomic insert. 24 yalnız final status'lar sabitlenince yapılır. |
| **C4 — Kota/abuse** | 02, 10, 15, 25 | `DailyLimitGuard`, rate limiter, plan resolver, endpoint caller'ları | Security owner'ı. Auth/registration abuse modeli 02 ile sabitlenir; lease kontratı 10 ayrı PR; typed limiter config 25; ingress/management boundary 15 ile ortak load testi. |
| **C5 — Activity/telemetry/privacy** | 06, 07, 14, 16, 22, 23, 26 | builder/middleware/channel/writer, `ActivityLog`, metrics, migrations, Serilog/OTLP | Privacy kararı 06/16 önce. Channel kayıp ölçümü 07, writer sınıflandırması 22, saat 26, request audit 14, metric wiring 23 sırasıyla. Aynı saturation fixture'ı paylaşılır. |
| **C6 — Asset/cache/repository** | 09, 12, 13, 18, 19 | `AssetService`, `PriceRepository`, repository interface'leri, asset catalog | Repository interface değişikliklerini tek migration window'unda topla. 09 revision semantiği, 13 bulk price API, 18/19 hata/boundary guard'ları; 12 bu API'leri tüketir. |
| **C7 — Build/CI/contract** | 01, 17, 24 | package graph/lock, workflow, integration fixtures, OpenAPI snapshot | 01 release'i açar; 17 required CI'ı güvenilir yapar; 24 final API davranışından snapshot üretir. 03/04 planlarıyla tek delivery epic altında izlenir. |

## 4. DCA reel getiri için matematik kararı

API-04 implementation'ından önce kavram seçilmelidir. Aynı “reel getiri” adı altında farklı, her biri meşru ama farklı soruyu yanıtlayan ölçüler vardır.

### Alternatif A — Nakit akışı bazlı, bitiş tarihi satın alma gücüyle reel P&L/ROI

Her katkı `C_i`, katkı ayının TÜFE endeksi `I_i`, bitiş endeksi `I_T`, terminal portföy değeri `V_T` olsun:

- `ReelMaliyet_T = Σ(C_i × I_T / I_i)`
- `ReelKarZarar_T = V_T - ReelMaliyet_T`
- `ReelKarZararYuzdesi = V_T / ReelMaliyet_T - 1`

Bu, bütün katkıları bitiş gününün TL satın alma gücüne taşır. Eşdeğer olarak bütün nakit akışları ve terminal değer ortak bir baz dönemine deflate edilebilir; oran değişmez.

**Artıları:** Mevcut nominal `profitLossPercent = P&L / total invested` sözleşmesiyle aynı anlam ailesindedir; deterministik, additive, kolay açıklanır; root-finding yoktur; sabit fiyat fixture'ı elle doğrulanabilir.  
**Eksileri:** Yıllıklandırılmış değildir; her alım ayı için CPI gerekir; “yatırım aracının performansı”ndan çok kullanıcının gerçek satın alma gücü sonucunu ölçer.

### Alternatif B — Reel XIRR / para-ağırlıklı yıllık getiri

Nakit akışları sabit satın alma gücüne çevrilir: her tarihte `-C_i / I_i`, terminalde `+V_T / I_T`; düzensiz tarihlerle yıllık `r` kökü çözülür.

**Artıları:** Zamanlamayı açıkça hesaba katan, yıllıklandırılmış ve portföyler arasında karşılaştırılabilir money-weighted metrik verir.  
**Eksileri:** Numerical solver/tolerans/day-count convention gerektirir; bazı cash-flow dizilerinde çözüm bulunmayabilir veya işaret yapısı değişirse birden çok kök olabilir; son kullanıcıya açıklaması güçtür ve mevcut nominal yüzdeyle aynı kavram değildir.

### Alternatif C — Reel time-weighted return

Her katkı arasında portföy/asset segment getirisi hesaplanıp enflasyondan arındırılır ve zincirlenir.

**Artıları:** Katkı büyüklüğü/zamanından bağımsız yatırım aracı veya strateji performansını ölçer; fon performansı karşılaştırmasına uygundur.  
**Eksileri:** Kullanıcının “yatırdığım paranın bugünkü reel sonucu” sorusunu yanıtlamaz; her nakit akışı öncesi/sonrası güvenilir valuation ister; mevcut response semantiğinden daha büyük ürün değişikliğidir.

### Alternatif D — Mevcut aggregate Fisher düzeltmesi

Toplam nominal ROI'yi başlangıç-bitiş enflasyonuna bölmek yalnız tek başlangıç nakit akışında anlamlıdır. DCA katkılarını başlangıçtan beri yatırılmış varsaydığı için DCA'da korunmamalıdır. Yalnız lump-sum WhatIf için kullanılabilir.

### Önerilen default

**Alternatif A** mevcut `RealProfitLossPercent` alanının default matematiği olmalıdır. Gerekçe: response'un nominal P&L yüzdesiyle aynı kullanıcı sorusunu reel satın alma gücüyle yanıtlar; deterministik ve test edilebilir; solver edge-case'i eklemez. İleride ileri seviye analytics gerekiyorsa Alternatif B, farklı ve açık adlı `AnnualizedRealMoneyWeightedReturnPercent` alanı olarak eklenmelidir. Alternatif C de ancak “asset/strateji performansı” ürünü için ayrı metrik olmalıdır.

Bu değişiklik JSON shape'i aynı kalsa bile **semantic breaking change** sayılır. Response'a `realReturnMethod = cashflow_cpi_terminal_v1` benzeri makine-okunur yöntem bilgisi eklenmesi, cache namespace/version bump, release note ve mobil analytics güncellemesi gerekir. Her katkı ayının CPI'ı yoksa uydurma/interpolation yapılmamalı; ürün kararı doğrultusunda null+warning/degraded status dönmeli ve incomplete sonuç normal 1 saatlik cache'e yazılmamalıdır.

## 5. High work item'lar

### REM-API-01 — Güvenli OpenAPI dependency graph ve audit açık build

- **Kaynak / severity / wave:** API-01 / High / **W1**
- **Duplicate:** **Tam:** 03/PLT-H01, 04/XVR-H01. **Kısmi:** 03/PLT-M01, 04/XVR-M03 (audit/lock/reproducibility).
- **Önkoşullar:** Uyumlu `Microsoft.AspNetCore.OpenApi` patch'i ve `Microsoft.OpenApi >= 2.7.5` çözüm grafiği boş-cache restore ile doğrulanmalı. Merkezi package/lock-file politikası 03/04 owner'ıyla kararlaştırılmalı.
- **Ürün kararı:** Yok. Security gate'i kapatmak veya süreli suppress etmek kabul edilen seçenek değildir.
- **Teknik çözüm:** `Directory.Packages.props` içinde güvenli `Microsoft.OpenApi` 2.x sürümünü explicit central pinle; üst OpenAPI/Scalar paketini uyumlu patch'e yükselt; lock file/locked restore kararını C7 kapsamında uygula. `NuGetAudit=false` hiçbir release komutunda bulunmamalı.
- **Dosya/komponent çakışması:** C7; `Directory.Packages.props`, `src/Saydin.Api/Saydin.Api.csproj`, lock dosyaları, OpenAPI smoke/contract testleri. API-24 snapshot PR'ı bu upgrade sonrasına rebase edilmeli.
- **Regresyon testi:** Boş NuGet cache ile audit açık restore/build; `dotnet list package --vulnerable --include-transitive`; development ortamında OpenAPI JSON + Scalar smoke ve semantic parse testi.
- **Acceptance gate:** Resolved `Microsoft.OpenApi` en az 2.7.5; High/Critical vulnerability sıfır; normal API image/solution Release build 0 warning/0 error; suppress/bypass yok.
- **Karmaşıklık:** **S (1–2 gün)**

### REM-API-02 — Server-issued installation identity, ownership ve abuse sınırı

- **Kaynak / severity / wave:** API-02 / High / **W0 karar → W1 foundation ve rollout**
- **Duplicate:** **Kısmi:** 03/PLT-H03 ve PLT-H04 (production rate limit/public surface ve keyfi device ID); 04'te tam tekrar yok.
- **Önkoşullar:** Trust-boundary/threat model; mobil client sürüm dağılımı; premium tier'ın bugün hangi kayıtla ilişkilendiği; signing/opaque-secret key yönetimi; Redis tabanlı dağıtık registration/IP limiter kapasitesi.
- **Ürün kararı:** Anonim installation mı hesap auth mı; cihaz değişimi/recovery; credential rotation/revocation; premium entitlement transferi; eski `X-Device-ID` için migration penceresi; 401/403/404 enumeration politikası; attestation gerekip gerekmediği.
- **Teknik çözüm:** Önerilen ilk aşama, yüksek entropili **server-issued opaque installation credential**; yalnız hash'i DB'de, principal/installation ID auth middleware ile context'e taşınır. SavedScenario ownership ve tier bu principal'a bağlanır. Kısa ömürlü access token + rotate edilebilir secret tercih edilirse revocation açık kalır. Registration ve tüm iş endpoint'leri Redis destekli per-IP/installation limiter ile korunur; yeni ID/credential mint ederek kota sıfırlama engellenir. Dual-read migration yalnız süreli olmalı; legacy header ile yeni write/DELETE cutover sonrasında reddedilmelidir.
- **Dosya/komponent çakışması:** C3+C4; `EndpointExtensions`, yeni auth/principal abstraction, `DeviceContext`, `User`/configuration/migration, `PlanLimitResolver`, `DailyLimitGuard`, scenario service/repository, bütün endpoint/test client'ları. API-05/10/25 ile tek security owner.
- **Regresyon testi:** Başka credential ile GET/DELETE IDOR; token rotation/revocation; legacy cutover; aynı IP'den binlerce registration/rotating credential için 429; premium entitlement korunumu; çok-instance Redis limiter; loglarda secret bulunmaması.
- **Acceptance gate:** Device ID tek başına authentication/authorization sağlamıyor; rotating-ID abuse günlük hard cap'i aşamıyor; stolen/revoked credential kullanılamıyor; migration telemetrisi legacy kullanımın hedef eşiğin altına indiğini kanıtlıyor; mobile rollback yolu testli.
- **Karmaşıklık:** **XL (10–20+ gün, client rollout hariç)**

### REM-API-03 — Scenario payload bütçesi, typed schema ve pagination

- **Kaynak / severity / wave:** API-03 / High / **W0 karar → W1**
- **Duplicate:** 03/04'te **yok**.
- **Önkoşullar:** Gerçek mobil payload boyut dağılımı; her scenario type için gerekli `ExtraData` alanları; proxy/Kestrel body limitleri; DB migration ve client pagination desteği.
- **Ürün kararı:** Endpoint/request byte limiti; JSON depth/property limiti; type başına schema; page size/max page; premium hard cap; oversize status'un 400 mü 413 mü olacağı ve eski client uyumu.
- **Teknik çözüm:** Binding öncesi endpoint request-body sınırı; global olmayan, scenario'ya özel JSON depth; `JsonElement` serbest torbası yerine type-discriminated versioned DTO/validator. UTF-8 byte sayısı server validation ve `octet_length(extra_data::text)` DB CHECK ile iki kat zorlanır. GET cursor pagination (`createdAt,id`) ve bounded page size kullanır; response total count zorunlu değilse pahalı count'tan kaçınılır. Cache/compression limit sayılmaz.
- **Dosya/komponent çakışması:** C3; `SaveScenarioRequest`, scenario endpoint/service/repository/response, `SavedScenarioConfiguration`, yeni migration, exception handler/resources, HTTP+PG tests. API-05/11 önce/sonra sırası C3 tablosuna göre.
- **Regresyon testi:** Byte sınırı ±1, aşırı depth/property count, sıkıştırılabilir dev body, type-schema unknown property; DB direct-insert constraint; cursor duplicate/missing-row concurrency; bounded response size ve repository create-not-called.
- **Acceptance gate:** Oversize payload allocation/depolama öncesi 413/400; DB constraint API'yi bypass eden oversize insert'i reddediyor; tek response belirlenen byte/page bütçesini aşmıyor; load testinde memory/DB/WAL bütçesi onaylı.
- **Karmaşıklık:** **L (5–8 gün)**

### REM-API-04 — DCA reel nakit-akışı matematiği

- **Kaynak / severity / wave:** API-04 / High / **W0 matematik kararı → W1**
- **Duplicate:** 03/04'te **yok**; ana rapor P0 içinde konsolide edilmiştir.
- **Önkoşullar:** Bölüm 4'teki yöntem ADR'si; tüm purchase ayları için bulk CPI repository kontratı; missing/provisional CPI semantiği; response/cache versiyon kararı.
- **Ürün kararı:** Önerilen Alternatif A'nın kabulü; alanın mevcut adla semantik değişimi veya versioned yeni alan; rounding; CPI ay eşleme; missing CPI'da null/degraded; XIRR'ın sonraki ürün olup olmadığı.
- **Teknik çözüm:** Her `DcaPurchase` katkısını kendi ayının CPI'ıyla terminal satın alma gücüne taşı; reel maliyet, reel P&L ve reel yüzdeyi decimal ile ve en sonda round ederek hesapla. `realReturnMethod`/schema version ekle; cache namespace'i bump et. CPI setini tek bulk sorguda al; missing veri tamamlanmış sonuç gibi cache'lenmez. Aggregate Fisher yalnız lump-sum WhatIf'te kalır.
- **Dosya/komponent çakışması:** C2+C6; `DcaCalculator`, DCA response, inflation repository/interface, cache key, localizer/docs ve DCA tests. API-13 bulk price refactor'ından önce matematik kontratı; API-08 degraded cache ile aynı response version.
- **Regresyon testi:** Sabit asset fiyatı + en az üç katkı + farklı CPI ayları için elle exact fixture; tek katkının Fisher ile eşdeğerliği; CPI sabitken reel=nominal; missing CPI; rounding/property testleri; cache v1/v2 karışmaması.
- **Acceptance gate:** ADR formülüyle bağımsız spreadsheet/reference implementation aynı sonucu veriyor; mevcut yanlış `NotNull` testi exact assertion'a dönüşüyor; yöntem response'ta görünür; finans/product sign-off; eski cache sonucu servis edilmiyor.
- **Karmaşıklık:** **L (5–10 gün)**

### REM-API-06 — Finansal ve serbest metin telemetri redaction'ı

- **Kaynak / severity / wave:** API-06 / High / **W0 privacy kararı → W1**
- **Duplicate:** **Kısmi:** 03/PLT-M07 (telemetry kimliği/saklama); 04'te tam tekrar yok.
- **Önkoşullar:** Veri sınıflandırma matrisi; console/OTLP backend erişim ve retention envanteri; analytics'in gerçekten ihtiyaç duyduğu bucket/alanlar; incident-debug minimumu.
- **Ürün kararı:** Amount bucket sınırları; label analytics'in tamamen kaldırılması veya client-side kategorileştirme; pseudonymous device/user alanlarının hangi log tier'ında tutulacağı; retention ve erişim owner'ı.
- **Teknik çözüm:** WhatIf/DCA Information loglarından ham input/output tutarlarını kaldır; yalnız symbol, yöntem, outcome, duration ve düşük kardinaliteli `AmountBucket` kullan. Activity JSON'dan serbest label'ı çıkar; gerekirse `hasLabel` boolean tut. Serilog/OTLP pipeline'ına defense-in-depth redaction/destructuring policy ekle; secret ve credential alanları denylist değil allowlist ile loglansın.
- **Dosya/komponent çakışması:** C2+C5; calculator'lar, `ScenariosEndpoints`, `AmountBucket`, Serilog setup/config, activity docs/ADR ve log assertion tests. API-16 retention politikasıyla birlikte privacy sign-off.
- **Regresyon testi:** Benzersiz sentinel amount/label/credential ile WhatIf/DCA/scenario; in-memory logger, JSON console ve OTLP test exporter'da sentinel taraması; bucket boundary tests; exception logunda request object destructure edilmemesi.
- **Acceptance gate:** Otomatik PII/financial sentinel suite bütün sink'lerde sıfır sızıntı; approved telemetry schema dışı property yok; retention/access policy owner'ı ve süresi belgeli; dashboard gerekli sinyalleri koruyor.
- **Karmaşıklık:** **M (3–5 gün)**

## 6. Medium work item'lar

### REM-API-05 — Atomik scenario limit altında insert

- **Kaynak / severity / wave:** API-05 / Medium / **W2**
- **Duplicate:** 03/04'te **yok**.
- **Önkoşullar:** API-11 validation/error mapping; limit aşımının 422 mi conflict'in 409 mu olacağı; gerçek PG concurrency fixture.
- **Ürün kararı:** Aynı anda gelen son slot isteklerinde loser status/code; unlimited premium için sistem hard cap; retry'nin istemciye görünürlüğü.
- **Teknik çözüm:** Repository'de transaction içinde user'a özgü `pg_advisory_xact_lock` (veya doğrulanmış serializable transaction+retry), ardından count ve insert. Tek atomik domain operasyonu servis dışına ayrık count sızdırmaz. Constraint/serialization SQLSTATE'i typed domain exception'a çevrilir.
- **Dosya/komponent çakışması:** C3; scenario service/repository/interface, PG transaction, exception handler/resources, API-24 status metadata ve integration tests.
- **Regresyon testi:** Limit-1'de barrier ile 2 ve 20 paralel request; tam kalan slot kadar başarı, final count==limit; farklı user'lar birbirini bloklamıyor; retry/cancellation transaction'ı sızdırmıyor.
- **Acceptance gate:** 100 tekrarlı gerçek PG concurrency testinde invariant hiç bozulmuyor; loser deterministik ProblemDetails alıyor; lock süresi/p95 bütçesi kabul edilmiş.
- **Karmaşıklık:** **M (3–5 gün)**

### REM-API-07 — Channel drop callback ve doğru kayıp telemetrisi

- **Kaynak / severity / wave:** API-07 / Medium, çapraz doğrulamada High / **W1**
- **Duplicate:** **Tam:** 04/XVR-H03. **Kısmi:** 03/PLT-H07 ve PLT-M16 (alarm/docs davranışı).
- **Önkoşullar:** Drop warning sampling oranı ve alarm eşiği; channel capacity/load baseline.
- **Ürün kararı:** Yok; yalnız kabul edilen activity-log kayıp bütçesi/SLO operasyon kararıdır.
- **Teknik çözüm:** Bounded channel creation'ı tek factory'ye taşı; `itemDropped` callback'te allowlisted action tag'li counter artır. `TryWrite=false`ı closed/rejected olarak ayrı metric/logla; her drop için warning loglayıp yeni baskı yaratma, sample/rate-limit et.
- **Dosya/komponent çakışması:** C1+C5; `Program.cs`, `ChannelActivityLogger`, `SaydinMetrics`, channel/writer tests ve observability docs.
- **Regresyon testi:** Capacity 1: ikinci item callback'e tam bir kez; retained item doğru; `MeterListener` drop=1. Completed writer: rejected metric artar, drop artmaz. Saturation+drain stress ve tag cardinality.
- **Acceptance gate:** Kontrollü saturation'da dropped item sayısı ile metric bire bir; closed channel false-positive yok; alarm test notification üretir; yorum/doküman runtime semantiğiyle uyumlu.
- **Karmaşıklık:** **S (1–2 gün)**

### REM-API-08 — Degraded response kalite sözleşmesi ve cache politikası

- **Kaynak / severity / wave:** API-08 / Medium / **W0 karar → W2**
- **Duplicate:** 03/04'te **yok**.
- **Önkoşullar:** API-04 response version; cache stratejisi ADR; beklenen transient exception taxonomy; client warning UI.
- **Ürün kararı:** Chart/inflation yokluğunda 200+warning mi partial/degraded status mı; degraded TTL (öneri: normal cache'e hiç yazmamak, gerekirse saniyelik negative cache); hangi enrichment zorunlu.
- **Teknik çözüm:** `dataStatus` ve bounded `warnings` sözleşmesi; yalnız beklenen optional-data exception'larını yakala. Complete sonuç normal TTL, degraded sonuç cache dışı veya ayrı çok kısa namespace/TTL. Response cache key/version API-04 ile bir kez bump edilir.
- **Dosya/komponent çakışması:** C2; WhatIf/DCA calculator, response modelleri, cache helper/keys, localizer ve tests.
- **Regresyon testi:** İlk transient fail, ikinci success aynı request'te repository yeniden çağrılır; permanent no-data ile transient hata ayrılır; cancellation yutulmaz; degraded payload eski complete cache'i overwrite etmez.
- **Acceptance gate:** Dependency iyileştikten sonraki ilk request complete sonuç döner; normal 1 saatlik cache'te degraded entry yok; client warning contract testi ve cache metrics mevcut.
- **Karmaşıklık:** **M (3–5 gün)**

### REM-API-09 — Monoton asset catalog revision ile cache invalidation

- **Kaynak / severity / wave:** API-09 / Medium / **W2**
- **Duplicate:** 03/04'te **yok**.
- **Önkoşullar:** Asset mutation'larının bütün yazarları/seed yolları; migration stratejisi; catalog revision'ın transaction sınırı.
- **Ürün kararı:** Asset değişikliklerinin maksimum görünürlük gecikmesi; yönetim mutation'ında strong invalidation gerekip gerekmediği.
- **Teknik çözüm:** Count yerine tek satırlı monoton `asset_catalog_revision` veya transactionally güncellenen revision/`updated_at` kontratı kullan. Her asset mutation aynı transaction'da revision artırır; Redis list/info/symbol index key'leri revision'a bağlıdır. İçerik hash ancak tüm canonical alanlarla deterministik ve maliyeti kabul edilirse ikinci seçenektir.
- **Dosya/komponent çakışması:** C6; Asset entity/config/migration/yazarları, `IPriceRepository/PriceRepository`, `AssetService`, symbol index ve cache tests.
- **Regresyon testi:** Aynı count ile activate/deactivate swap, display/category değişimi ve transaction rollback; iki instance eski/yeni revision; stale key yeni revision'da okunmuyor.
- **Acceptance gate:** Bütün asset mutation türleri belirlenen gecikme içinde görünür; aynı-count fixture geçer; revision rollback'te ilerlemez; Redis outage DB correctness'i bozmaz.
- **Karmaşıklık:** **M (3–5 gün)**

### REM-API-10 — Exact-key ve idempotent günlük kota lease'i

- **Kaynak / severity / wave:** API-10 / Medium / **W2**
- **Duplicate:** 03/04'te **yok**.
- **Önkoşullar:** API-02 principal modeli; bütün acquire/release caller envanteri; Redis key/TTL migration yaklaşımı.
- **Ürün kararı:** Hangi hata sınıflarında kullanım release edilir; timeout/istemci disconnect tüketim sayılır mı; idempotency süresi.
- **Teknik çözüm:** `TryAcquireAsync` exact day key, unique lease token ve limit metadata'sı taşıyan opaque lease döndürür. Release o key/token için Lua ile tam bir kez decrement eder; gün/saat yeniden hesaplanmaz. Unlimited durum explicit no-op lease olabilir. Caller yalnız başarıyla edinilmiş lease'i `finally/catch` politikasıyla bırakır.
- **Dosya/komponent çakışması:** C4; `IDailyLimitGuard/DailyLimitGuard`, WhatIf/DCA services, Assets endpoints, Redis integration/unit tests.
- **Regresyon testi:** 23:59 acquire→00:00 başka acquire→ilk lease release; double release; process retry; cancelled acquire; Redis fail-open ve TTL boundary.
- **Acceptance gate:** Gün-2 sayacı gün-1 release'inden etkilenmiyor; token başına en çok bir decrement; bütün caller'lar lease API kullanıyor; gerçek Redis parallel testleri geçiyor.
- **Karmaşıklık:** **M (3–5 gün)**

### REM-API-11 — Scenario request invariant validation ve DB hata eşleme

- **Kaynak / severity / wave:** API-11 / Medium / **W2**
- **Duplicate:** 03/04'te **yok**.
- **Önkoşullar:** Scenario type→amount-unit matrisi; sell==buy ürün kararı; API-05 conflict status; migration/model drift kontrolü.
- **Ürün kararı:** Aynı gün buy/sell save edilebilir mi? Mevcut DB `sell > buy` diyor; değişecekse calculator, schema ve UI birlikte değişmeli. Validation status/code ve localized copy.
- **Teknik çözüm:** Binding sonrası normalize edilmiş type/unit/date validator; canonical `QuantityUnits` kullanımı; DB CHECK'leri EF configuration'da bire bir modelle. Defense-in-depth olarak constraint name/SQLSTATE'i typed 400/409 domain exception'a map et; beklenmeyen DbUpdate 500 kalır.
- **Dosya/komponent çakışması:** C3; request/service/config/migration, exception handlers, resx, endpoints/OpenAPI ve PG tests. API-05 önkoşulu.
- **Regresyon testi:** null/blank/mixed-case/unknown unit; sell<,==,>buy; type-unit kombinasyonları; direct DB violation; tr/en exact ProblemDetails; malformed request user yaratmıyor.
- **Acceptance gate:** Kullanıcı kaynaklı bu invariant ihlallerinin hiçbiri 500 değil; EF model/migration constraint isimleri drift testinde eşleşiyor; DB'ye geçersiz satır yazılmıyor.
- **Karmaşıklık:** **M (3–5 gün)**

### REM-API-12 — Requested ve effective market date semantiği

- **Kaynak / severity / wave:** API-12 / Medium / **W0 karar → W2**
- **Duplicate:** 03/04'te **yok**.
- **Önkoşullar:** API-04 CPI ay semantiği; UI'ın requested/actual date gösterimi; nearest-price backward/forward iş kuralı.
- **Ürün kararı:** Chart ve CPI ekonomik olarak fiili execution date'i mi kullanmalı? Öneri: evet; requested date yalnız kullanıcı niyeti olarak ayrı alan. Forward fallback varsa kullanıcının açık onayı/etiketi.
- **Teknik çözüm:** Tek `EffectivePriceWindow` value object'i requested ve actual buy/sell'i taşır. P&L, range ve CPI repository çağrıları actual dates kullanır; response ikisini açık isimlerle döndürür. Cache key hesaplama semantiğine actual date/revision dahil edilir veya requested key altında doğru version cache'lenir.
- **Dosya/komponent çakışması:** C2+C6; WhatIf/reverse calculator, response/cache, price/inflation calls ve tests. API-08/04 ile response version ortak.
- **Regresyon testi:** Ayın ilk pazarının önceki ay cumasına clip'i; tatil/forward fallback; chart ilk/son noktası; CPI ay argümanları; cache collision.
- **Acceptance gate:** Tek request'teki fiyat, chart ve CPI aynı effective pencereyi kullanıyor; requested/actual ayrımı OpenAPI ve UI contract'ında görünür; product fixture onaylı.
- **Karmaşıklık:** **M (3–5 gün)**

### REM-API-13 — Bulk DCA fiyat çözümü ve sabit sorgu bütçesi

- **Kaynak / severity / wave:** API-13 / Medium / **W2**
- **Duplicate:** **Kısmi:** 03/PLT-M12 (repository/performance test boşluğu); 04'te tam tekrar yok.
- **Önkoşullar:** API-04 matematik kontratı; nearest backward-first semantiği; DB index/query plan baseline; maksimum 600 ürün kararı.
- **Ürün kararı:** Maksimum purchase noktası ve maximum range; duplicate market-day purchase'ların birleştirme semantiği korunacak mı.
- **Teknik çözüm:** Nokta sayısını aritmetik/checked hesapla ve max+1'de reject et. Gerekli tarih min/max'ını ±7 gün genişletip fiyatları tek range query ile al; purchase dates'i bellekte deterministic backward-first/forward fallback ile eşle. Terminal fiyat aynı setten; eksik pencere typed price-not-found. CPI bulk query API-04 ile ayrı ama sabit sayıda.
- **Dosya/komponent çakışması:** C2+C6; DCA calculator/generator, AssetService/IPriceRepository/PriceRepository, indexes ve query-count tests.
- **Regresyon testi:** 1/600/601 point; duplicate weekend clip; exact backward priority; full DateOnly range hızlı reject; command interceptor query count; benchmark/load cold cache.
- **Acceptance gate:** 600 purchase için price DB roundtrip sabit hedefte (öneri ≤2, ideal 1); geniş invalid input büyük liste allocate etmeden bounded sürede 400; p95 ve allocation bütçesi baseline'dan onaylı.
- **Karmaşıklık:** **L (5–10 gün)**

### REM-API-14 — Erken red ve ProblemDetails code için request-audit sözleşmesi

- **Kaynak / severity / wave:** API-14 / Medium / **W0 karar → W3**
- **Duplicate:** **Kısmi:** 03/PLT-M16 (observability dokümanı ile middleware gerçeği); 04'te tam tekrar yok.
- **Önkoşullar:** API-02 auth/rate pipeline; API-06/16 privacy-retention; bounded action/error taxonomy; abuse sırasında DB write amplification bütçesi.
- **Ürün kararı:** Invalid device/malformed JSON/429 olayları durable activity row mu, aggregate metric mi? Öneri: business request sonuçları bounded activity row; yüksek hacimli pre-auth/429 olayları sampled security metric/log, ham payload yok.
- **Teknik çözüm:** Routing sonrası endpoint metadata'dan merkezi audit context oluştur. Exception handlers ve rate rejection ortak `ApiErrorFeature`/context item ile canonical error code yayınlar; middleware final status+code okur. Builder oluşmayan yollar için bounded fallback action vardır. Per-request DB logu abuse vektörü olmayacak şekilde sampling/counter ayrımı yapılır.
- **Dosya/komponent çakışması:** C1+C5; Program pipeline, ActivityLog middleware/builder, handlers, rate limiter, endpoints, action constants/config/migration ve integration tests.
- **Regresyon testi:** Success, domain exception, 500, invalid device, malformed JSON, 404 unmatched, 429; tam bir kayıt/counter, final status/code, body/secret yok; yüksek hacim write budget.
- **Acceptance gate:** Dokümante edilen her matched business request outcome gözlenebilir; `error_code` null yalnız gerçekten hata yoksa; pre-auth flood DB'yi büyütmüyor; docs ve gerçek pipeline aynı.
- **Karmaşıklık:** **L (5–8 gün)**

### REM-API-15 — Internal management yüzeyi ve public erişim ayrımı

- **Kaynak / severity / wave:** API-15 / Medium / **W0 topology kararı → W3**
- **Duplicate:** **Tam:** 03/PLT-H04. **Kısmi:** 03/PLT-H03; 04'te tam tekrar yok.
- **Önkoşullar:** Production ingress/network topology; monitoring auth/mTLS/IP allowlist yeteneği; API-21 live/ready yolları; Compose production overlay planı.
- **Ürün kararı:** Liveness public olabilir mi; readiness detail ve metrics kimlere açık; management port/network standardı.
- **Teknik çözüm:** Metrics ve detailed readiness'i ayrı internal listener/management port ve private network'e taşı; mTLS/auth veya allowlist defense-in-depth. Public listener yalnız gerekiyorsa minimal liveness döndürür. Management endpoint'leri global business limiter'dan bağımsız olabilir ama scrape concurrency/frequency ve auth ile korunur.
- **Dosya/komponent çakışması:** C1+C4; `Program.cs`/Kestrel config, health routes, Compose/ingress, monitoring config, docs ve HTTP smoke. API-21 ile tek PR/epic olabilir.
- **Regresyon testi:** Public client metrics/readiness 401/403/404; monitoring principal/internal network 200; scrape burst bounded; response detail secret/connection string içermiyor.
- **Acceptance gate:** External port scan'de management yüzeyi yok; production topology testli; monitoring scrape ve probes çalışıyor; network/auth policy deployment manifestinde zorunlu.
- **Karmaşıklık:** **M (3–5 gün, infra koordinasyonu hariç)**

### REM-API-16 — Activity data retention, anonymization ve deletion lifecycle

- **Kaynak / severity / wave:** API-16 / Medium / **W0 hukuk/privacy kararı → W3**
- **Duplicate:** **Kısmi:** 03/PLT-H05 (cold export/backup) ve PLT-M07 (telemetry retention/access); 04'te tam tekrar yok.
- **Önkoşullar:** Hukuki amaç/saklama matrisi; backup/cold storage planı; device→principal migrationı API-02; Timescale policy desteği; deletion request ownership doğrulaması.
- **Ürün kararı:** Hot/cold retention günleri; legal hold; delete mi irreversible anonymize mı; account/device deletion SLA'sı; analytics minimum granularity.
- **Teknik çözüm:** Migration/ops automation ile time-based retention policy; gerekiyorsa retention öncesi şifreli ve erişim kontrollü aggregate/cold export. Principal deletion workflow activity rowsını delete veya onaylı irreversible anonymize eder; backup expiry de politika kapsamındadır. Compression retention sayılmaz. Policy installation/status metriği ve runbook eklenir.
- **Dosya/komponent çakışması:** C5; ActivityLog entity/config, Timescale migrations/jobs, deletion service/endpoint, docs/ADR, monitoring. API-06 veri minimizasyonu ve API-02 principal ile aynı privacy epic.
- **Regresyon testi:** Eski/yeni/hold satırlarıyla retention; deletion/anonymization ve ilişkili index/JSON; backup expiry drill; policy eksikken readiness/alert.
- **Acceptance gate:** Onaylı süreyi aşan veri hot DB'de yok; deletion SLA otomatik test/drill ile kanıtlı; policy her production DB'de installed/healthy; hukuk/privacy owner sign-off.
- **Karmaşıklık:** **XL (10+ gün, hukuk/ops dahil)**

### REM-API-17 — Required CI'da gerçek altyapı ve zero-skip gate

- **Kaynak / severity / wave:** API-17 / Medium, platform raporlarında High / **W1**
- **Duplicate:** **Tam:** 03/PLT-H02, 04/XVR-H02. **Kısmi:** 03/PLT-M11/M12/M13, 04/XVR-M01/M02.
- **Önkoşullar:** İzole Compose/service-container template; fresh migration job; test DB allowlist; CI resource/time budget.
- **Ürün kararı:** Yok; required branch gate/retry politikası engineering governance kararıdır.
- **Teknik çözüm:** Required integration job disposable digest-pinned TimescaleDB+Redis kurar, fresh migrations uygular, test-only connection strings sağlar. CI mode'da fixture prerequisite hatası skip değil fail olur. TRX sonuçlarından skipped==0 ve minimum executed count gate edilir; local optional mode ayrı trait/script olabilir.
- **Dosya/komponent çakışması:** C7; workflow, Compose override, integration csproj/fixtures, migration scripts, coverage aggregation. 03/04 remediation owner'ıyla duplicate ticket açılmaz; tek epic altında bu acceptance kriterleri kullanılır.
- **Regresyon testi:** Infra/env yok → non-zero; yanlış schema → non-zero; gerçek infra → 8+ passed/0 skipped; paralel proje adı/port isolation; cleanup başarısız olsa bile sonraki run etkilenmiyor.
- **Acceptance gate:** Required CI'da integration skip=0; mevcut en az 8 test gerçekten çalışıyor; fresh schema ve unique test DB kanıtı; branch protection job'ı zorunlu.
- **Karmaşıklık:** **M (3–5 gün)**

### REM-API-18 — Asset varlığı ile fiyat yokluğu hata ayrımı

- **Kaynak / severity / wave:** API-18 / Medium / **W2**
- **Duplicate:** 03/04'te **yok**.
- **Önkoşullar:** API-09 catalog cache doğruluğu; inactive asset ürün semantiği; canonical error code matrisi.
- **Ürün kararı:** Inactive asset `asset_not_found` mı `asset_unavailable` mı; enumeration gizleme gereksinimi.
- **Teknik çözüm:** WhatIf/reverse cache miss'te active asset'i önce resolve et; sonra latest/nearest price. Valid asset/no date `price_not_found`; bilinmeyen/inactive canonical asset hatası. Ortak helper normal/reverse/DCA sırasını aynılaştırır.
- **Dosya/komponent çakışması:** C2+C6; WhatIf calculator, AssetService/cache, exception contract ve HTTP/unit tests.
- **Regresyon testi:** Unknown, inactive, active-no-price ve active-valid için exact type/code/status; cold/warm asset cache; repository call order.
- **Acceptance gate:** Üç hesaplama endpoint'i aynı error matrix'i döndürüyor; unknown asset'te price repository çağrılmıyor; OpenAPI API-24'te eşleşiyor.
- **Karmaşıklık:** **S (1–2 gün)**

### REM-API-19 — DateOnly domain sınırı ve overflow-safe date arithmetic

- **Kaynak / severity / wave:** API-19 / Medium / **W2**
- **Duplicate:** 03/04'te **yok**.
- **Önkoşullar:** Desteklenen tarih ürün aralığı ve asset catalog first/last date; API-13 generator refactor.
- **Ürün kararı:** Global minimum tarih mi asset-specific availability mi; gelecekteki tarihler; out-of-range 400 ile no-price 404 ayrımı.
- **Teknik çözüm:** Request başında checked, localized domain validation. Repository nearest window DateOnly min/max'a saturating clamp uygular. Weekly/monthly generator increment öncesi end/max kontrolü ve max+1 cap kullanır; unchecked AddDays/AddMonths yok.
- **Dosya/komponent çakışması:** C2+C6; request/service validators, DCA generator, PriceRepository, exception resources ve boundary tests. API-13 ile aynı generator PR'ında yapılabilir ama ayrı commit/test.
- **Regresyon testi:** Min/Max, ±7 gün, month-end/leap-day, future ve destek başlangıcı; property/fuzz “hiç ArgumentOutOfRange/500 yok”; hızlı reject allocation testi.
- **Acceptance gate:** Public date inputlarının hiçbiri arithmetic exception ile 500 üretmiyor; 400/404 matrisi belgeli ve lokalize; repository doğrudan çağrısı da overflow-safe.
- **Karmaşıklık:** **S (2–3 gün)**

### REM-API-20 — Fatal bootstrap için non-zero process exit

- **Kaynak / severity / wave:** API-20 / Medium / **W1**
- **Duplicate:** **Tam:** 04/XVR-M05. 03'te tam tekrar yok.
- **Önkoşullar:** Serilog flush davranışı ve host process test harness'i.
- **Ürün kararı:** Yok.
- **Teknik çözüm:** Top-level catch fatal logdan sonra non-zero exit garantiler; tercih exception'ı flush `finally` sonrasında rethrow etmek, hosting model buna izin vermiyorsa explicit exit code. Aynı pattern ingestion Program ile 04 owner'ı tarafından hizalanır.
- **Dosya/komponent çakışması:** C1; `Program.cs`, process/container smoke tests ve restart docs. API-25 invalid config testi bunu tüketir.
- **Regresyon testi:** Invalid connection URI, port bind ve DI/options failure; process kısa sürede non-zero, fatal log bir kez, flush tamam.
- **Acceptance gate:** Bütün bootstrap fault fixture'ları non-zero; orchestrator `on-failure` restart/alert smoke'u geçiyor; clean shutdown 0 kalıyor.
- **Karmaşıklık:** **XS (≤1 gün)**

### REM-API-21 — Liveness/readiness/degraded cache ayrımı

- **Kaynak / severity / wave:** API-21 / Medium / **W0 SLO kararı → W3**
- **Duplicate:** **Tam:** 03/PLT-M05. 04'te tam tekrar yok.
- **Önkoşullar:** API-15 management topology; zorunlu dependency/SLO listesi; deployment probe davranışı.
- **Ürün kararı:** Redis down readiness 200-degraded mı 503 mü; PostgreSQL down readiness; public liveness response detail.
- **Teknik çözüm:** Tag predicate ile `/health/live` yalnız self/process, `/health/ready` zorunlu dependencies; opsiyonel Redis ayrı degraded component/metric. Compose/Kubernetes probe'ları doğru endpoint'e bağlanır; detailed JSON yalnız internal management listener'dadır.
- **Dosya/komponent çakışması:** C1; Program health registration/mapping, Compose/ingress, docs ve integration tests. API-15 ile ortak topology PR'ı önerilir.
- **Regresyon testi:** PG/Redis dört durum matrisi; hung health dependency timeout; live endpoint dependency çağırmıyor; probe restart/traffic behavior smoke.
- **Acceptance gate:** Redis-only outage liveness restart üretmiyor; readiness kararı ADR ile bire bir; deployment manifest yanlış endpoint kullanmıyor; internal detail dışarı sızmıyor.
- **Karmaşıklık:** **S–M (2–4 gün)**

### REM-API-22 — Provider-aware activity write hata sınıflandırması

- **Kaynak / severity / wave:** API-22 / Medium / **W3**
- **Duplicate:** **Kısmi:** 03/PLT-M12 yalnız test boşluğunu kapsar; defect'in tam tekrarı 03/04'te yok.
- **Önkoşullar:** Npgsql/EF exception taxonomy; retry budget; API-07 drop/write metric ayrımı; fault-injection seam.
- **Ürün kararı:** Kabul edilen activity loss/retry latency bütçesi; shutdown sırasında retry mi bounded drop mu.
- **Teknik çözüm:** Saf/test edilebilir classifier inner exception zincirinden cancellation, timeout, `NpgsqlException.IsTransient`, SQLSTATE connection/serialization/deadlock ve constraint/data error sınıflarını ayırır. Yalnız row-specific constraint/data hatası bisect; outage batch seviyesinde bounded retry/backoff/circuit davranışı. Outcome metric reason'ları bounded.
- **Dosya/komponent çakışması:** C5; `ActivityLogWriter`, classifier/helper, metrics, Npgsql fault tests ve docs.
- **Regresyon testi:** Connection reset, timeout, 40001/deadlock, check/not-null/length violation, serializer bug, cancellation; iyi+toxic mixed batch; shutdown attempt budget.
- **Acceptance gate:** Transient batch hiçbir zaman row-by-row toxic drop'a gitmiyor; toxic fixture yalnız kötü satırı kaybediyor; attempts/drop reason metric'i gerçek outcome ile eşit; saturation test geçiyor.
- **Karmaşıklık:** **M (3–5 gün)**

## 7. Low work item'lar

### REM-API-23 — Business metric wiring ve bounded label sözleşmesi

- **Kaynak / severity / wave:** API-23 / Low / **W3**
- **Duplicate:** **Kısmi:** 03/PLT-H07 ve PLT-M19 (alarm/doküman); 04'te tam tekrar yok.
- **Önkoşullar:** Metric/dashboard owner; API-04/08 outcome/method sözleşmesi; label cardinality bütçesi.
- **Ürün kararı:** Hangi KPI/SLO gerçekten kullanılacak; kullanılmayan instrument'ın silinmesi kabul mü; tier/symbol dimension gereksinimi.
- **Teknik çözüm:** Ortak calculation instrumentation scope ile count+duration+outcome; price-not-found handler/repository boundary'sinde tek increment. Labels route/method/tier/outcome ve bounded supported symbol set; amount/device/exception message yok. Kullanılmayan instrument kaldırılır, docs/dashboard aynı committe güncellenir.
- **Dosya/komponent çakışması:** C1+C2+C5; `SaydinMetrics`, calculators/handlers, OTel registration, tests/dashboard/rules. API-06 privacy ve API-07 metric naming ile review.
- **Regresyon testi:** `MeterListener` success/failure/cache-hit/price-miss; double-count yok; cardinality allowlist; duration cancellation dahil.
- **Acceptance gate:** Dashboard query'leri canlı seri döndürüyor; instrument başına owner/unit/labels belgeli; no-op metric yok; alert rule testleri 03 planıyla geçiyor.
- **Karmaşıklık:** **S–M (2–4 gün)**

### REM-API-24 — Runtime status/code matrisiyle OpenAPI hizalama

- **Kaynak / severity / wave:** API-24 / Low / **W4**
- **Duplicate:** 03/04'te **yok**; API-01 OpenAPI smoke ile dosya çakışması vardır.
- **Önkoşullar:** API-02 auth status'ları, API-05 conflict/limit status'u, API-11 validation, API-14 error taxonomy ve API-18 hata sırası final olmalı.
- **Ürün kararı:** 409/422 ayrımı ve auth'ta 401/403/404 enumeration politikası.
- **Teknik çözüm:** Endpoint metadata'yı final ProblemDetails status/code matrisiyle güncelle; mümkünse reusable convention/extension `RequireDeviceId`/auth error metadata'sını merkezi ekler. Generated OpenAPI'yi semantic snapshot ile doğrula; kullanılmayan 409 kaldırılır veya gerçek conflict ile bağlanır.
- **Dosya/komponent çakışması:** C3+C7; Assets/Scenarios ve diğer endpoint files, OpenAPI config/package, snapshot/client tests. En son merge edilir.
- **Regresyon testi:** Her route için runtime-enumerated status vs OpenAPI responses set comparison; missing device, validation, not-found, limit, conflict, rate-limit; generated client deserialize smoke.
- **Acceptance gate:** Runtime'da üretilebilen bütün documented contract status'ları spec'te, spec'teki business status'lar runtime fixture'da; semantic snapshot intentional approval; client codegen uyarısız.
- **Karmaşıklık:** **S (1–2 gün)**

### REM-API-25 — Typed, fail-fast rate limiter options

- **Kaynak / severity / wave:** API-25 / Low / **W3**
- **Duplicate:** **Kısmi:** 03/PLT-H03 production rate-limit/fail-fast gereksinimi; 04'te tam tekrar yok.
- **Önkoşullar:** API-02 distributed limiter ve environment policy; API-20 non-zero exit; kabul edilen permit/window sınırları.
- **Ürün kararı:** Free/public endpoint limitleri; production'da disabled config'e izin verilip verilmeyeceği (öneri: verilmemesi); internal management istisnası.
- **Teknik çözüm:** Typed `RateLimitingOptions`, custom/DataAnnotations validator, `ValidateOnStart`; positive ve üst sınırlar; Production guard. `Program.cs` yalnız validated options tüketir. Distributed limiter API-02'de ayrı olsa da aynı config namespace açıkça ayrılır.
- **Dosya/komponent çakışması:** C1+C4; Program/options/appsettings/production overlay, validation resources ve process tests.
- **Regresyon testi:** 0/negative/aşırı permit/window, disabled Production, valid Development/Production; invalid config non-zero startup; boundary load.
- **Acceptance gate:** Yanlış limiter config ile hiçbir request kabul eden process ayağa kalkmıyor; production baseline enabled; config schema/docs aynı; API-20 process testi geçiyor.
- **Karmaşıklık:** **S (1–2 gün)**

### REM-API-26 — ActivityLog için TimeProvider-owned timestamp

- **Kaynak / severity / wave:** API-26 / Low / **W3**
- **Duplicate:** 03/04'te **yok**.
- **Önkoşullar:** ActivityLog construction noktalarının envanteri; API-14 merkezi builder ve API-16 retention time semantics.
- **Ürün kararı:** Yok; timestamp'in request başlangıcı mı send/build zamanı mı olacağı teknik sözleşmede açık seçilmelidir. Öneri: request başlangıç zamanı.
- **Teknik çözüm:** Shared entity wall clock initializer'ını kaldır; `ActivityLogBuilder`/factory `TimeProvider` alır ve request başında UTC timestamp'i explicit set eder. DB default yalnız out-of-process/legacy insert defense'i olarak kalabilir; production API her zaman değer gönderir.
- **Dosya/komponent çakışması:** C1+C5; `ActivityLog`, builder/DI, middleware, EF config ve tests. API-14 refactor'ı builder constructor'ını değiştireceği için aynı owner/sıralı merge.
- **Regresyon testi:** FakeTimeProvider exact CreatedAt; request sırasında saat ilerlese de seçilen semantik; midnight/order; serialization/DB roundtrip UTC.
- **Acceptance gate:** API activity creation path'inde `UtcNow` doğrudan kullanımı yok; bütün timestamp testleri fake clock ile deterministic; retention fixture aynı zaman kaynağını kullanıyor.
- **Karmaşıklık:** **S (1–2 gün)**

## 8. İzlenebilirlik ve kapanış kontrol listesi

Her work item kapanırken ticket/PR aşağıdaki kanıtları taşır:

- Kaynak `API-xx` ve varsa **tek** konsolide 03/04 duplicate epic link'i; duplicate için ikinci bağımsız implementation ticket'ı açılmaz.
- Onaylı ürün/ADR kararı ve API/backward-compatibility notu.
- Değişen dosya/komponent listesi, C1–C7 owner'ı ve merge sırası.
- Bu plandaki regresyon testinin adı/sonucu; gerçek PG/Redis gerektiğinde skip=0 kanıtı.
- Item acceptance gate'inin makine-okunur CI, smoke, metric veya drill çıktısı.
- Cache/schema/version/migration değiştiyse rollout, rollback ve eski client davranışı.
- Privacy/security item'larında sentinel, secret scan ve access/retention sign-off'u.

Tüm plan tamamlandığında global release gate şunları aynı commit/artifact digest için birlikte göstermelidir: audit açık güvenli build; unit ve required real-infrastructure integration skip=0; DCA bağımsız referans matematiği; auth/abuse ve oversize negatif testleri; concurrency invariant'ları; management yüzeyi izolasyonu; activity drop/write fault injection; retention/deletion drill; runtime/OpenAPI semantic eşleşmesi. Bu kanıtların herhangi biri eksikse ilgili bulgu “kod merge edildi” gerekçesiyle kapanmış sayılmaz.
