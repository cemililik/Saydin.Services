# Saydin.Services — Birleşik Remediation Aksiyon Planı

> **Plan tarihi:** 2026-08-18  
> **Uygulama branch'i:** `development`  
> **Branch tabanı:** `development/a274c62`; `main/9067dd2` bu commit'in doğrudan atasıdır  
> **Kapsam:** 92 ham review kaydının tamamı  
> **Yayın durumu:** Açık Critical/High bulgular nedeniyle hazır değil

Canlı uygulama durumu ve kabul kanıtları:
[`06-remediation-progress.md`](06-remediation-progress.md).

## 1. Amaç ve izlenebilirlik

Bu plan yalnız bir yapılacaklar listesi değildir. Her bulguyu uygulanabilir teknik değişikliğe,
regresyon testine, rollout/rollback adımına ve ölçülebilir kapanış kanıtına bağlar. Ayrıntılı alan
planları şunlardır:

- [`remediation/01-api-remediation-plan.md`](remediation/01-api-remediation-plan.md): API raporundaki
  **26/26** bulgu.
- [`remediation/02-ingestion-remediation-plan.md`](remediation/02-ingestion-remediation-plan.md):
  ingestion/veri raporundaki **25/25** bulgu.
- [`remediation/03-platform-remediation-plan.md`](remediation/03-platform-remediation-plan.md):
  platform raporundaki 32 ve çapraz rapordaki 9 referansın tamamı, **13 konsolide work item**.

Alanlar arasındaki duplicate kayıtlar ayrı defect olarak uygulanmayacaktır. Örneğin OpenAPI advisory
tek RP-01 değişikliğinde, activity channel drop tek RP-03/API-07 değişikliğinde, worker supervision
tek SUP-001/RP-04 değişikliğinde çözülür; diğer rapor kayıtları aynı acceptance kanıtına bağlanır.

## 2. Değişmez uygulama kuralları

1. Bütün geliştirmeler yalnız `development` branch'inde yapılır; alt/feature branch açılmaz.
2. Aynı anda en fazla **iki implementation lane** açıktır. Üçüncü agent ancak salt-okunur review/test
   yapabilir; ana agent diff, test ve entegrasyon kontrolünü elinde tutar.
3. Aynı dosya/komponent kümesine iki agent eşzamanlı yazamaz. Domain planlarındaki conflict-lane sırası
   değişikliklerden önce kontrol edilir.
4. Critical containment ve kalıcı çözüm ayrı olabilir; containment bulguyu “kapalı” yapmaz. Kalıcı
   acceptance tamamlanana kadar status açık kalır.
5. Geçmiş migration dosyaları production geçmişini yeniden tanımlayacak biçimde değiştirilmez.
   Düzeltmeler additive migration, migrator ve schema gate ile yapılır.
6. Güvenlik audit'i, test skip'i, coverage eksikliği veya alert susturma çözüm olarak kabul edilmez.
7. Her davranış değişikliği exact regresyon testi ve negatif test taşır. Sadece mevcut suite'in yeşil
   olması kapanış kanıtı değildir.
8. Formatter, paket toplu upgrade'i ve davranış değişikliği aynı değişiklik grubunda karıştırılmaz.
9. Repo dışı production/OCI/branch-protection işleri immutable kanıt olmadan kapalı işaretlenmez.
10. Bir wave'in release kapısı geçmeden daha düşük wave production'a çıkmaz; bağımsız geliştirme
    hazırlanabilir fakat deploy edilemez.

## 3. Alınan temel kararlar

### D-01 — DCA reel getiri matematiği

Default yöntem **cash-flow CPI terminal ROI** olacaktır. Her katkı kendi katkı ayının TÜFE endeksinden
bitiş tarihinin satın alma gücüne taşınır:

`realCostAtEnd = Σ(contribution × endCpi / contributionCpi)`

`realReturn = terminalPortfolioValue / realCostAtEnd - 1`

Bu yöntem mevcut nominal P&L/ROI sorusunun reel karşılığıdır, deterministiktir ve XIRR solver edge
case'leri yaratmaz. Semantic breaking change olduğu için response yöntem/version bilgisi ve cache
namespace bump zorunludur. Reel XIRR ileride ayrı, açık adlı yıllıklandırılmış alan olabilir.

### D-02 — Ingestion checkpoint ve hata semantiği

`MAX(price_date)` veya `MAX(period_date)` tek checkpoint olmayacaktır. Additive
`ingestion_windows` ledger; logical range, attempt, lease, outcome, accepted/rejected count ve retry
state'ini tutacaktır. Yalnız açık `succeeded` veya doğrulanmış `expected_no_data` window checkpoint'i
ilerletir. `[]`, `null`, auth, schema veya partial payload otomatik başarı değildir.

### D-03 — Migration ownership ve partial-init

Tarihsel SQL dosyalarını değiştirmek yerine, tek sahibi olan always-run migrator kurulacaktır. Global
advisory lock, checksum/state, normalized connection target ve post-migration schema fingerprint
zorunludur. PostgreSQL `pg_isready` yalnız process liveness'tır; uygulama readiness'i son beklenen
migration/fingerprint'i doğrulamadan açılmaz. Partial ve sahipliği belirsiz DB otomatik drop/recreate
edilmez; fail-closed/quarantine uygulanır.

### D-04 — Worker fatal davranışı

İlk güvenli politika: enabled worker fatal olduğunda exception host'a taşınır ve process non-zero
sonlanır; container orchestrator bounded restart/backoff uygular. Bağımsız heartbeat worker başarısını
temsil etmez. İleride in-process supervisor kullanılacaksa bounded budget sonunda yine host fail eder.

### D-05 — Anonymous installation identity

Önerilen hedef, hash'i DB'de tutulan server-issued opaque installation credential ve Redis destekli
registration/abuse limitidir. Bu değişiklik mobil client rollout, premium entitlement transfer ve
credential recovery/revocation sözleşmesini etkilediğinden implementation öncesi tek açık ürün karar
kapısıdır. Hazırlık olarak principal abstraction ve production rate-limit guard geriye uyumlu eklenebilir;
legacy header'ın write/delete yetkisini kaldırma tarihi ayrıca onaylanmalıdır.

### D-06 — Production deployment kaynağı

Development Compose production fallback'i değildir. OCI belgelerindeki inline örnekler kanonik,
version-controlled production manifest, Caddy config, secret contract ve signed image-digest delivery
zincirine dönüştürülmeden production-ready sayılmaz.

## 4. Öncelik ve wave planı

### Wave 0 — Güvenilir geliştirme ve release kapısı

Bu wave iki Critical bulgunun güvenle doğrulanabilmesi için önkoşuldur; severity sırasını değiştiren ürün
işi değil, test/build altyapısıdır.

| Sıra | Work item | Severity | Çıktı | Kapanış kapısı |
|---:|---|---|---|---|
| 0.1 | RP-01 / REM-API-01 | High, release blocker | Güvenli 2.x `Microsoft.OpenApi` pin'i; uyumlu OpenAPI patch | Audit açık clean restore, solution/API image build, advisory 0, OpenAPI smoke |
| 0.2 | RP-02 / API-17 | High | Gerçek TimescaleDB/Redis required CI, test DB guard, skip=0 | En az 380 test; 8 integration çalışmış; fresh migration; eksik infra non-zero |
| 0.3 | Plan/evidence harness | Program prerequisite | TRX, migration/fault kanıtı, bulgu status ledger | Her item için owner, test, evidence ve rollback alanı |

### Wave 1 — Critical veri ve şema bütünlüğü

| Sıra | Work item | Severity | Uygulama dilimleri | Kapanış kapısı |
|---:|---|---|---|---|
| 1.1 | ING-001 / C-01 containment | Critical | Hata yutmayı durdur; başarısız chunk sonrası ilerleme/retry engeli; exact fault tests | İkinci chunk'ın adapter/job/repository fault'unda üçüncü chunk çağrılmaz; restart ikinciyi hedefler |
| 1.2 | DBM-001..005 / C-02 | Critical | Always-run migrator, advisory lock, checksum, schema fingerprint/readiness, recovery runbook | Kill/restart ve parallel runner testlerinde partial DB uygulamaya açılmaz; legacy complete DB no-op |
| 1.3 | ING-001 durable ledger | Critical | Additive window ledger, typed outcome, atomic state/data, feature-flag rollout | `MAX` yeni/ara gap'i saklayamaz; success/0 yalnız reason'lı expected-no-data; crash matrisi geçer |
| 1.4 | Data-quality audit | Critical follow-up | Read-only gap/invalid/provenance/stale-job audit; repair manifest | Production-benzeri restore'da audit; repair dry-run; unresolved gap açık risk olarak görünür |

### Wave 2 — High finansal doğruluk, provider ve security

| Grup | Work item'lar | Öncelikli sonuç |
|---|---|---|
| Finansal matematik | API-04, API-12, API-18, API-19 | Cash-flow CPI reel ROI, effective date source-of-truth, doğru error/boundary semantiği |
| Provider doğruluğu | OXR-001, MAP-001, DAT-001, EVDS-001/002, RES-001 | Typed provider outcome, completeness, final/reference price, doğru EVDS ufku/key ve resilience |
| Veri invariant/provenance | DAT-002/003, ORM-001 | Pozitif/OHLC/TÜFE constraint rollout'u; source/as-of/finality/payload hash; EF/DB parity |
| Kimlik ve abuse | API-02, API-03, API-05, API-10 | Server-issued principal temeli, body/schema/page limitleri, atomic scenario limit, quota lease |
| Privacy | API-06, API-16 | Ham finansal tutar/label loglarını kaldır; retention/deletion ve sink redaction |
| Worker/audit güveni | SUP-001, JOB-001, RP-03/04, API-07/20/22 | Fatal exit, freshness health, drop callback, transient/toxic classification, stale job recovery |

Wave 2 release gate: exact finans fixture'ları, provider error matrix, DB constraints, auth/oversize/log
sentinel testleri ve worker/channel fault testleri birlikte geçer; açık Critical yoktur.

### Wave 3 — High production operability ve delivery

| Work item | Sonuç |
|---|---|
| CON-001, TXN-001 | Distributed lease; data + terminal job/window state atomikliği |
| RP-07 | Sertleştirilmiş OCI production manifest; private networks; secret/rate-limit/metrics sınırı |
| RP-08 | Bir kez build edilen signed digest, SBOM/provenance, staging promotion ve rollback |
| RP-09 | RPO/RTO, şifreli off-host backup, PITR kararı ve ölçülmüş restore drill |
| RP-10 | Data freshness/drop/backup/API SLO'ları, version-controlled alert ve on-call runbook |
| API-15/21, PLT health | Internal management yüzeyi; ayrı live/ready; optional Redis policy |

### Wave 4 — Medium doğruluk, performans ve kalite

- API-08/09: degraded response cache politikası ve gerçek catalog revision invalidation.
- API-11/13: DB invariant öncesi validation; bulk DCA price/CPI sorgusu ve query budget.
- API-14/23/25/26: request audit/error code, business metric wiring, options fail-fast ve deterministic time.
- M-01..M-09 ingestion: job atomicity/cancellation, ORM parity, secret taşıma, telemetry, docs test
  komutu, büyük migration rehearsal, fatal exit ve tek connection contract.
- RP-05/06: merged coverage ratchet, kritik I/O test matrisi, exact SDK, lock files, image/dependency
  pinning, license kararı ve kontrollü formatter temizliği.
- RP-11: executable documentation, config/schedule/metric drift testleri.

### Wave 5 — Low ve kapanış işleri

- API OpenAPI semantic snapshot ve runtime status/code matrisi.
- PR template, README, development guide, CLAUDE komutları ve historical review linkleri.
- Security/contribution/license/release policy, CODEOWNERS yedekliliği ve branch/environment governance.
- OCI end-to-end rehearsal, restore/rollback/game-day ve go/no-go evidence paketi.
- Bütün ham bulguların status'u evidence link'iyle `closed`, `accepted-with-expiry` veya gerçekten dış
  bağımlılığa takılıysa `blocked` olur; sahipsiz açık kayıt kalmaz.

## 5. Kontrollü paralellik haritası

| Lane | Sahip olduğu dosyalar | Başlangıç | Paralel olabileceği lane | Çakışma yasağı |
|---|---|---|---|---|
| **A — Build/CI** | `Directory.Packages.props`, csproj/lock, `.github/workflows`, test fixture bootstrap | Wave 0 | B ingestion runtime | API contract snapshot ve toplu dependency upgrade aynı anda yok |
| **B — Ingestion critical** | Base/Evds worker, adapter outcome, ingestion repositories/entities, yeni migration'lar | Wave 1 | A build/CI | Migration/bootstrap lane ile aynı dosyada paralel yazım yok |
| **C — Migration/bootstrap** | apply-migrations, migrator, Compose DB readiness, ADR-001 | B containment merge sonrası | API domain | Yeni migration numarasını yalnız tek owner tahsis eder |
| **D — API domain/security** | DCA/WhatIf/scenario/quota/auth/cache | Wave 2 | C veya platform deploy | `Program.cs`/Shared entity değişiklikleri önceden rebase ve tek owner |
| **E — Runtime/platform** | channel/health/metrics/prod manifest/delivery | Wave 2–3 | Saf domain calculator | `Program.cs`, Compose ve observability docs tek owner sırasıyla |

Ana agent her lane teslimatında şu sırayı uygular: diff review → hedefli test → solution test → gerçek
infra testi gerekiyorsa izole Compose → bulgu acceptance kontrolü → status/evidence güncellemesi. Sonraki
lane ancak bu kapıdan sonra aynı dosya kümesine girebilir.

## 6. İlk uygulama paketi

İlk paket yalnız iki bağımsız lane içerir:

1. **Lane A — RP-01:** `Microsoft.OpenApi` güvenli aynı-major pin'i, audit açık build ve OpenAPI smoke.
2. **Lane B — C-01 containment:** Base/EVDS hata propagasyonu, chunk-stop/retry davranışı ve fault
   regresyon testleri. Bu containment tamamlanınca durable ledger migration tasarımına geçilir.

Bu iki değişiklik birbirinin production dosyalarına dokunmaz. Lane A build kapısını açar; Lane B yeni
kalıcı veri boşluğu üretimini hemen durdurur. İki diff ayrı incelenir ve birlikte full regression'dan
geçirilir.

İlk paketin kabulünden sonra sıra önce RP-02 fail-closed CI'a, ardından C-02 migration kontrol
düzlemine geçer. Durable ledger additive şema gerektirdiği için C-02'nin güvenli runner/checksum
temeli kabul edilmeden enforcement moduna alınmaz; böylece aynı anda iki farklı migration sahipliği
oluşmaz.

## 7. Program kapanış kriteri

Program ancak aşağıdakilerin tamamında kapanır:

- 92 ham kaydın tamamı alan planındaki work item ve immutable acceptance kanıtına bağlıdır.
- Audit açık build, High/Critical dependency ve image vulnerability olmadan geçer.
- Critical crash/migration/gap fault matrisi ve finansal reference fixture'ları geçer.
- Required CI gerçek infra ile skip=0, merged coverage/changed-lines ratchet ve docs/config drift
  kapılarını çalıştırır.
- Production manifest yalnız signed digest'leri kullanır; restore, rollback, alert ve worker-failure
  provaları ölçülmüştür.
- Açık Critical/High yoktur. Medium istisna varsa owner, expiry, compensating control ve release onayı
  vardır. Low kayıtların tamamı kapanmış veya açıkça izlenebilir yönetişim item'ına bağlanmıştır.

Takvim hedefi kalite kapısını değiştirmez. Bu plan büyük bir tek PR olarak uygulanmayacak; her wave
kanıt üretip bir sonraki wave'i açacaktır.
