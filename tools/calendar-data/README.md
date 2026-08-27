# Authoritative calendar release runbook (CAL-001)

Bu araç iki güven sınırını ayırır:

1. Resmî TCMB/Borsa İstanbul içeriği ağ erişimli `acquire` komutuyla owner-only karantinada
   content-addressed snapshot ve manifest olarak hazırlanır. Komut yalnız exact resmî HTTPS
   host/path/port allowlist'ine çıkar; redirect'i yeniden doğrular, media/encoding/size/timeout
   sınırlarını uygular ve çıktıyı aynı filesystem içinde atomik yayınlar.
2. `verify` ve `import` internet kullanmaz. Manifest host/path/media/hash allowlist'i, parser
   semantiği ve normalized byte hash'i doğrulanmadan veritabanına hiçbir şey yazılmaz.

Raw snapshot byte'ları veritabanına yazılmaz. Raw trust boundary, repodaki content-addressed
snapshot ile offline verifier'ın exact raw SHA-256/parser kontrolüdür. PostgreSQL yalnız normalize
gün satırlarını ve full source-manifest provenance metadata aggregate'ini yeniden hesaplar. Bu
dokümandaki “source bundle hash” ifadesi raw dosyaların DB hash'i değil, bu internal metadata
aggregate'inin hash'idir.

Price-ingestion runtime'ı resmî siteye takvim için çıkmaz ve bilinmeyen günü hafta içi/hafta
sonu varsayımıyla uydurmaz. Coverage eksikse provider çağrısından önce
`calendar_coverage_missing` üretir ve `saydin.ingestion.calendar.not_ready.total` metriğini artırır.

## Production güncelleme sözleşmesi

- **TCMB (`tcmb_indicative_fx`)**: Systemd timer her gün 06:00 Europe/Istanbul'da acquisition
  job'ını çalıştırmalı. Job planı idempotent biçimde materialize eder; 16:30 İstanbul provider
  cutoff'unu aşmayan en yeni yıllık/aylık resmî arşiv sayfalarını staging'e indirir.
  Exact URI `https://www.tcmb.gov.tr/kurlar/kurYYYY_tr.html` ve
  `/kurlar/YYYYMM/Mon_tr.html` dışına redirect/host/path kabul edilmez. Gün aylık arşivde henüz
  yayınlanmamışsa requested coverage arşivdeki son kanıtlı publication gününe clamp edilir.
  Aktif release içindeki en son `observation_expected=true` gün worker hedefidir; dolayısıyla
  bugün kanıtlıysa bugün, değilse son eligible gün kullanılır ve bilinmeyen ileri gün için
  provider çağrısı yapılmaz. Sonra snapshot SHA-256'ları,
  `retrievedAt`, yeni `snapshotSetId`, coverage ve expected-output kontratı güncellenir.
- **BIST Pay (`bist_pay_xist`)**: Yıllık timer Borsa İstanbul yeni yıl Pay Piyasası tatil PDF'ini
  yayımlayınca
  ve en geç mevcut horizon bitimine 60 gün kala acquisition job yeni resmî PDF + index snapshot'ı
  hazırlar. PDF semantiği full/partial/closed olarak replay edilmeden release yayınlanmaz.
  PDF'de listelenmeyen hafta içi `full_session` satırları doğrudan tarih kanıtı değil, resmî
  closure/partial-session listesindeki yokluktan yapılan açık bir çıkarımdır; index snapshot'ı
  exact yıllık authority PDF bağlantılarını çapraz doğrular.
- TCMB active coverage Istanbul-yesterday'dan geri kalırsa veya BIST horizon 45 günden azsa
  `infrastructure/prometheus/rules/ingestion.yml` critical alert üretir. Worker'ın
  `calendar.not_ready` metriğinde tek artış production readiness ihlalidir;
  provider çağrı sayısı aynı window için sıfır kalmalıdır.

Acquisition çıktısı doğrudan production'a gönderilmez. Snapshot/manifest/normalized dosyaları
code review ve acquisition kimliğinden ayrı reviewer anahtarıyla imzalı artifact promotion'dan
geçirilir. `review-envelope.json`, source manifest ve expected-output byte hash'lerini bağlar;
manifest de tüm raw snapshot hash'lerini bağlar. `retrievedAt` authority publication zamanı
değildir; indirmenin UTC audit zamanıdır. Raw bytes ve resmî URI provenance kaydıdır.

BIST `full_session` satırları resmî tatil/erken kapanış çizelgesinde doğrudan listelenmez.
Hafta sonu olmayan ve exact yıllık authority PDF'inde kapanış/yarım gün olarak yer almayan günler
`inferred_open_from_official_closure_schedule` reason code'u ile üretilir. Bu satırdaki
`evidence_raw_sha256`, doğrudan bir açık-seans kaydı iddiası değil, tamamlayıcısı alınan exact
resmî kapanış çizelgesinin provenance'ıdır; index→PDF çapraz bağı üretimden önce doğrulanır.

## Ağ erişimli acquisition

Plan yalnız değişen/yeni resmî kaynakları listeler; doğrulanmış base bundle'daki diğer content-
addressed byte'lar taşınır. `snapshotSetId` her candidate için ilerletilmelidir. Aşağıdaki komut
DB credential kabul etmez ve `import`/`activate` çağırmaz:

```bash
docker run --rm \
  -v "$BASE_BUNDLE:/input/base:ro" \
  -v "$PLAN:/input/plan.json:ro" \
  -v "$QUARANTINE:/output" \
  saydin-calendar-data@sha256:<digest> acquire \
  --base-data-root /input/base \
  --plan /input/plan.json \
  --staging-root /output \
  --output-name candidate-<snapshot-set-id>
```

Tracked example plan, daily/yearly timer, global non-overlap lock, hard timeout ve bounded retry
artifact'ları `infrastructure/calendar/` altındadır. Scheduler output'u otomatik promote etmez.
Reviewer detached signature, envelope hash ve `--network none` replay doğrulamasından sonra ayrı
promotion script'ini çalıştırır; bu adım da DB'yi aktive etmez.

## Offline doğrulama

```bash
docker build -f tools/calendar-data/Dockerfile -t saydin-calendar-data:verify .
docker run --rm --network none saydin-calendar-data:verify
```

Bu kapı source manifest, 274 mevcut content-addressed source, parser replay, row count ve iki
normalized SHA-256 değerini fail-closed doğrular. Yeni bundle aynı kapıdan geçmelidir.

## Staging → verify → seal → activate

Dedicated one-shot release job explicit `PGHOST`/`PGPORT`/`PGDATABASE`/`PGUSER`/`PGSSLMODE`,
exact role-contract metadata ve absolute owner-only `SAYDIN_CALENDAR_DATABASE_PASSWORD_FILE`
kullanır. Raw connection URL/password env kabul edilmez. Komut bağlantı bilgisini loglamaz;
startup probe exact `calendar_importer` login/capability kimliğini DB yazımından önce doğrular.
Job'ın egress allowlist'i yalnız PostgreSQL endpoint'idir. Runtime ingestion kimliği bu tablolarda
DML yetkisi almamalıdır.

```bash
docker run --rm --network <database-network> \
  --env-file <nonsecret-runtime-metadata.env> \
  -v <calendar-secret-volume>:/run/saydin-secrets:ro \
  saydin-calendar-data:<immutable-tag> \
  import --data-root tools/calendar-data/data \
  --calendar tcmb_indicative_fx \
  --release-id 019c0000-0000-7000-8000-000000000001 \
  --release-version 2 \
  --expected-current-release ca100000-0000-7000-8000-000000000001
```

Komut Phase A bytes'ını yeniden üretip doğrular; calendar-scoped PostgreSQL advisory lock alır;
unsealed release/source/day satırlarını tek transaction'da yazar; DB'nin core `sha256(bytea)`
seal trigger'ını çalıştırır ve active pointer'ı compare-and-swap ile değiştirir. Seal veya pointer
başarısızsa tüm staging rollback olur. Aynı release id/payload ile paralel iki invocation
idempotent başarıdır; başka payload, version veya unexpected current pointer fail'dir.

Yeni window ilk claim'de yeni release id'ye bağlanır. Daha önce bağlanmış/retry olan window active
pointer değişse bile eski immutable release'i kullanır.

## Kontrollü rollback

Önce mevcut active id ve hedef eski release'in sealed/hash/coverage kaydı change ticket'a eklenir.
Sonra CAS rollback yapılır:

```bash
docker run --rm --network <database-network> \
  --env-file <nonsecret-runtime-metadata.env> \
  -v <calendar-secret-volume>:/run/saydin-secrets:ro \
  saydin-calendar-data:<immutable-tag> \
  activate --calendar tcmb_indicative_fx \
  --release-id ca100000-0000-7000-8000-000000000001 \
  --expected-current-release 019c0000-0000-7000-8000-000000000001
```

Rollback yalnız sealed ve aynı calendar'a ait release'i kabul eder. Eski release'in coverage'i
güncel hedefi karşılamıyorsa worker bilinçli olarak `CalendarNotReady` kalır; veri uydurmaz.
Rollback sonrası yeni window'lar rollback release'ini alır, zaten claimed window'lar değişmez.
