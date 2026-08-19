# ADR-001 — Migration & Schema Evolution Stratejisi

- **Durum:** Kabul edildi (2026-08-18 C-02 control-plane + migration 019 privilege-separation
  revizyonu). Faz 4'teki
  `initdb.d` + `apply-migrations.sh` çalıştırma kararı aşağıdaki güncel kararla superseded'dır;
  numaralandırılmış SQL kaynak formatı korunur.
- **Tarih:** 2026-05-27 (ilk taslak) · 2026-05-29 (Faz 4) · 2026-08-18 (C-02)
- **Karar verenler:** Backend ekibi
- **İlgili bulgular (code review):** Claude `[C-G-001/008/009/010]`, `[C-YAPISAL-1..4]`, `[C-H-DC-11]`; GPT-5 `[G-G-04]`, `[G-G-06]`; Faz 4 `F4-1`, `F4-8`

---

## Güncel Karar (2026-08-18 — C-02)

Kanonik şema kaynağı `infrastructure/postgres/migrations/` altındaki immutable, sıralı
`.sql`/optional `.sh` dosyalarıdır. Bunları çalıştırmanın tek normal deploy yolu artık küçük
`.NET` console uygulaması `Saydin.DatabaseMigrator` ve onun migration dosyalarını aynı image'a
bake eden `infrastructure/postgres/Dockerfile.migrator` artifact'ıdır.

- Runner, tüm hedeflerde persistent Npgsql connection üzerinde session-level advisory lock alır;
  her SQL gövdesi ve tracking kaydını runner-owned aynı transaction'da işler. Uygulanmış dosyalar
  raw-byte SHA-256 ile doğrulanır; 001–022 trust-root hash'leri binary içinde pinlidir ve DQA aynı
  canonical map'i derler.
- Control-plane ve tarihsel migration transaction'ları `search_path=public,pg_temp` kullanır.
  `pg_catalog` burada bilinçli olarak explicit yazılmaz: PostgreSQL catalog'u otomatik olarak en önce
  ararken unqualified tarihsel `CREATE` hedefi `public` kalır. Migration 019'un fully-qualified gövdesi
  ise contract GUC'larından sonra daha dar `pg_catalog,pg_temp` path'ine geçer ve exact assert edilir.
- Boş DB tam bootstrap edilir. Eksiksiz 014 legacy DB business data yeniden yazılmadan checksum ve
  state alanlarıyla baseline edilir. Partial/ambiguous, bilinmeyen/yeni version, checksum drift,
  core şema fingerprint drift ve `postgres`/`template0`/`template1` hedefleri non-zero reddedilir.
- Global rol grafiği schema migration'dan önce one-shot `Saydin.DatabaseRoleBootstrap` tarafından
  kurulur/doğrulanır. Normal migrator versioned least-privilege login + owner-only password file ile
  bağlanır ve NOLOGIN owner'a `SET ROLE` eder. OID-10 admin connection yalnız explicit legacy
  privilege cutover control-plane yolunda kabul edilir; runtime consumer credential'ı değildir.
- TimescaleDB 2.16.1 internal-schema sözleşmesi exact'tir: `_timescaledb_internal` için PUBLIC,
  versioned login ve foreign role grant'i yoktur; extension/bootstrap admin schema-owner
  `CREATE+USAGE`, altı capability, NOLOGIN hypertable relation-owner ve passwordless scheduler yalnız
  `USAGE` alır. Capability `USAGE` planner için, relation-owner `USAGE` future chunk creation için
  zorunludur. Root table ACL'leri source/compressed chunk'lara Timescale tarafından aynalanır;
  dolayısıyla direct chunk SELECT kök SELECT ile eşdeğer kabul edilir, write güvenlik sınırı exact
  propagated ingestion fence + canlı window/lease sözleşmesidir. Bu davranış schema, chunk ACL,
  trigger, RLS/policy, foreign-role deny ve scheduled BGW testleriyle kilitlenir.
- `schema_migrations.state`, adımı `running`/`succeeded`/`skipped_optional`/`failed` olarak;
  `saydin_migration_control.state` tüm hedefi `bootstrapping`/`baselining`/`ready`/`failed` olarak
  açıkça kaydeder. Optional 012b yalnız parola yokken `skipped_optional` olabilir.
- PostgreSQL `pg_isready` yalnız bağlantı sinyalidir. Compose'ta migrator `restart: "no"` one-shot
  çalışır; API, ingestion, pgAdmin, test runner ve PostgreSQL exporter
  `condition: service_completed_successfully` ile şema readiness'ini bekler.
- CI iki ayrı UUID-izole TimescaleDB cluster kullanır; role-bootstrap + one-shot exit +
  `--verify-only`, 21 terminal migration/21 checksum/2 hypertable/`ready` SQL kapısı ve en az 76
  executed, sıfır skipped/failed/notExecuted migrator TRX kapısı required'dır. Role-bootstrap için
  ayrıca 39 unit + 7 real-PG TRX kapısı vardır.

### Rollout ve Recovery Sözleşmesi

Fresh rollout sırası: OID-10 role-bootstrap `ensure/verify` → managed migrator login secret mount →
one-shot migration/verify → consumer servislerdir. Complete-014 veya managed-through-018 legacy
cutover; eski consumer drain → açık `--legacy-privilege-cutover --admin-connection-file` → 019
terminal fingerprint → runtime credential switch → eski credential rotation sırasını izler. Migrator
non-zero dönerse consumer'ları zorla başlatma, DB'yi otomatik drop/recreate etme, tracking satırı
elle ekleme veya tarihsel dosyayı değiştirme yasaktır. Hedef clone'unda audit/backfill tamamlanır;
gerekirse yeni additive düzeltme migration'ı üretilir ve aynı image akışı tekrar çalıştırılır.

Bu faz yeni business-schema migration eklemediği için uygulama image rollback'i additive control
metadata'yı bırakabilir; eski uygulamalar bu tabloları/kolonları okumaz. Migrator artifact rollback'i
ise yalnız hedef manifestiyle checksum/version uyumlu önceki artifact'a yapılabilir. Uyum yoksa eski
runner'a veya `apply-migrations.sh`'e düşmek güvenli rollback değildir; servisler kapalı tutulur ve
staging clone üzerinden forward-fix hazırlanır. `apply-migrations.sh` retired compatibility
entrypoint olarak exit 64 ile fail-closed'dur; migration uygulamaz ve normal/recovery deploy
mekanizması değildir.

## Tarihsel Bağlam (audit trail; güncel çalıştırma modeli değildir)

Saydın Services, PostgreSQL şemasını numaralandırılmış SQL dosyalarıyla yönetiyor:
`infrastructure/postgres/migrations/001_initial.sql` … `010_add_geo_columns.sql`.
Bu dosyalar Docker compose'da `/docker-entrypoint-initdb.d/` altına mount edilerek
**yalnızca fresh DB başlatılırken** uygulanıyor (postgres image varsayılan
davranışı: volume boşken alfabetik sıraya göre yürütür).

Code review iki ayrı kategoride sorun tespit etti:

1. **Sıralama tutarsızlığı.** Migration 008 (`add_activity_logs.sql`) geçmişte
   retroaktif güncellenmiş ve `country`/`city` kolonlarını içerecek şekilde
   genişletilmiş; migration 010 (`add_geo_columns.sql`) aynı kolonları ekliyor.
   Sıralı yürütmede 010, "kolon zaten var" hatasıyla başarısız olur.
2. **Mount gap'i.** `docker-compose.yml` 001–006'yı tek tek mount ediyor;
   007–010 init listesine eklenmemiş. Fresh DB'de DCA, activity logging ve
   GeoIP özellikleri çalışmıyor.

Ayrıca tüm SQL akışında **hangi migration'ın uygulandığını izleyen tablo yok**;
production'da "ne uygulandı?" sorusunun cevabı eski commit hash'lerine bakmakla
yanıtlanıyor — denetlenebilir değil.

Şu an EF Core EntityFrameworkCore zaten projeye dahil; `dotnet ef migrations add`
komutu da CLAUDE.md'de örnekleniyor ama hiç kullanılmıyor.

---

## Değerlendirilen Seçenekler

### Seçenek A — Mevcut SQL akışını sürdür, izlenebilirliği elle ekle

- `infrastructure/postgres/migrations/` altında `applied_migrations` tablosu yarat.
- Her migration dosyası sonunda `INSERT INTO applied_migrations VALUES (...)`.
- Docker init klasör mount eder; CI dışı bir tool ile production'da elle apply.

**Artı:** Mevcut akışa minimum müdahale. Türk takım hızlı geri dönüş.
**Eksi:** Idempotency, rollback, branch'ten gelen out-of-order migration'ları
ele almıyor. EF model değişiklikleri ile SQL drift kontrolü yapılmıyor
(F1.5-2'de `PricePoint.Volume` precision drift'i bu yüzden ortaya çıktı).

### Seçenek B — EF Core Migrations'a tam geçiş

- Mevcut SQL dosyalarını `dotnet ef migrations add InitialBaseline --idempotent` ile
  EF Core formatına dönüştür.
- `__EFMigrationsHistory` tablosu EF tarafından yönetilir (otomatik tracking).
- Production deploy adımı: container start'ta `context.Database.Migrate()` veya
  ayrı bir `ef database update` step'i.
- Geçişte mevcut DB'lerde `EFMigrationsHistory`'yi seed ederek "applied at"
  baseline atılır.

**Artı:**
- Tracking otomatik (CLAUDE.md "ingestion_jobs örneği gibi" tek yer).
- EF model değişiklikleri ile SQL otomatik üretilir; drift fırsatı azalır.
- Rollback komutu var (`dotnet ef database update <previous>`).
- IDE/CI tooling olgun.

**Eksi:**
- TimescaleDB `create_hypertable`, `add_compression_policy` gibi PostgreSQL-spec
  çağrıları EF tarafından üretilmiyor — bu adımlar yine raw SQL olmalı (EF
  migration içinde `migrationBuilder.Sql(...)` ile gömülebilir).
- Mevcut DB'lerde history tablosu seed gerekiyor; yanlış yapılırsa duplicate
  apply riski.
- Tahmini efor: 1-2 gün migration tooling kurulumu + retrospective baseline.

### Seçenek C — Hybrid: Tracking tablosu + Mevcut SQL akışı + EF Core "design-time" sync

- `schema_migrations(version, applied_at, checksum)` tablosu manuel oluştur.
- Mevcut SQL akışı korunur; her migration sonunda kendi `INSERT INTO schema_migrations`.
- EF Core `Database.EnsureSchemaMatches()` benzeri bir startup health check ile
  "EF model ↔ DB schema drift'i" warning olarak loglanır (engelleyici değil).
- Production deploy: `psql -f` ile sıralı uygulama, başarısızsa transaction rollback.

**Artı:** Mevcut hız korunur; EF migrations kurulum maliyeti yok.
**Eksi:** Drift detection elle kuruluyor; rollback komutu manuel; tooling
ekosistemi olmayan kendi yarı-çözümümüz.

---

## Karar (Önerilen)

> **⚠️ SUPERSEDED (Faz 4):** Bu bölümdeki orijinal öneri (**Seçenek B — EF Core**) Faz 4'te
> **Seçenek C (Hybrid)** ile değiştirildi. Güncel karar için aşağıdaki
> "Karar Revizyonu (Faz 4 — F4-1/F4-8)" bölümüne bakın. Aşağıdaki içerik denetim izi
> (audit trail) için korunmuştur ve **artık geçerli değildir**.

**Seçenek B — EF Core Migrations'a geçiş**, **Faz 1 / Sprint 2** içinde tamamlanacak.

### Gerekçe

1. Saydın zaten `EntityFrameworkCore` kullanıyor; entity ↔ DB drift'i tek
   gerçek source-of-truth (EF model) üzerinden kontrol edilebilir.
2. CLAUDE.md "Migration Komutları" bölümünde `dotnet ef migrations add` örneği
   var — niyet zaten EF olduğunu gösteriyor.
3. Tracking, rollback, drift detection tek pakette gelir; üç bayrı problemi
   tek karar kapatır.
4. TimescaleDB-spec çağrıları için `migrationBuilder.Sql("SELECT
   create_hypertable(...)")` pratik ve test edilmiş bir pattern.

### Faz 0'da Uygulanan Geçici Önlem

ADR-B tam geçişe kadar:

- **F0.1-1:** `docker-compose.yml` tek-tek mount yerine klasör mount kullanıyor
  (`./infrastructure/postgres/migrations:/docker-entrypoint-initdb.d:ro`).
  Böylece 007–010 dahil tüm mevcut SQL'ler fresh DB'de otomatik çalışıyor.
- **F0.1-2:** Migration 010 idempotent yapıldı (`IF NOT EXISTS`). Hem fresh
  init'te no-op, hem 008'in retroaktif değişikliğini almamış mevcut DB'lerde
  eksikleri tamamlar.
- **F0.1-5:** Migration 004 TÜFE seed satırları `source = 'seed-approximation'`
  ile işaretlendi; EVDS worker `ON CONFLICT DO UPDATE` ile bu satırları
  gerçek TÜİK verisiyle değiştiriyor (`InflationIngestionRepository`).

### Geçiş Planı (Faz 1)

1. **Baseline migration üretimi.** `dotnet ef migrations add InitialBaseline
   --project src/Saydin.Shared --startup-project src/Saydin.Api`
2. **Mevcut migration'larla diff alma.** 001..010'daki `CREATE TABLE`,
   `ALTER TABLE`, `CREATE INDEX` blokları ile EF'in ürettiği baseline'ı
   karşılaştır; drift varsa Shared/Entity ve Configuration sınıflarını
   senkronize et.
3. **TimescaleDB blokları.** `create_hypertable`, `add_compression_policy`
   çağrılarını baseline migration'ın `Up` metoduna `migrationBuilder.Sql(...)`
   olarak göm.
4. **History seed.** Mevcut production DB'lerde:
   ```sql
   INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
   VALUES ('<timestamp>_InitialBaseline', '<ef-version>');
   ```
5. **CI'a entegrasyon.** Pull request'lerde `dotnet ef migrations script
   --idempotent --output build/migration.sql` çalıştır; sonucu artifact olarak yükle.
6. **Deploy step'i.** Container start'ta `Database.Migrate()` yerine **dedicated
   migration job** (Kubernetes Job / Compose `depends_on: condition:
   service_completed_successfully`) tercih edilir — concurrent apply riskini önler.

### Geçiş Sonrası `infrastructure/postgres/migrations/*.sql` Akıbeti

- SQL dosyaları **silinmez**; `legacy/` alt-klasörüne taşınır.
- README notu eklenir: "Yeni migration'lar EF Core ile üretilir; bu dosyalar
  geçmiş baseline'ın referansıdır."

---

## Tarihsel Karar Revizyonu (Faz 4 — F4-1/F4-8; 2026-08-18'de superseded)

> **Tarih:** 2026-05-29 · **Durum:** Kabul edildi
>
> İlk taslağın önerdiği **Seçenek B (EF Core Migrations)** Faz 1'de **uygulanmadı**;
> ekip pratikte numaralandırılmış SQL akışını sürdürdü ve Faz 2/3'te 011/012/012b/013
> migration'larını bu formatta ekledi. Üstelik 008b/013, TimescaleDB compression
> penceresini (TS 2.16.1 `ALTER COLUMN TYPE` kısıtı) SQL düzeyinde çözdü — EF Core'un
> modelleyemediği bir davranış. Bu nedenle Faz 4'te karar **bilinçli olarak revize**
> edildi.

### Yeni Karar: Seçenek C (Hybrid) — MVP için

1. **Numaralandırılmış SQL init zinciri korunur** (001…013 + 008b/012b). Battle-tested;
   compression penceresini doğru kodluyor. Fresh init `docker-entrypoint` ile
   alfabetik + `ON_ERROR_STOP=1` çalışır.
2. **Hafif izleme tablosu eklenir:** `014_schema_migrations.sql` →
   `schema_migrations(version PK, applied_at, checksum)`. Additive ve idempotent:
   - Fresh init: tüm önceki sürümler + kendisi `ON CONFLICT DO NOTHING` ile kaydedilir.
   - Var olan (014 öncesi) DB: dosya elle uygulanınca 001..014 geçmişini DDL yeniden
     çalıştırmadan back-register eder.
   - `ALTER COLUMN TYPE` içermez → compression penceresini etkilemez; 013'ten sonra
     (alfabetik 014 > 013) güvenle çalışır.
3. **Production / var olan DB deploy mekanizması (F4-8):**
   `infrastructure/postgres/apply-migrations.sh` — `schema_migrations`'a bakıp yalnız
   KAYITLI OLMAYAN `.sql`/`.sh` dosyalarını alfabetik sırada `psql -v ON_ERROR_STOP=1`
   ile uygular ve kaydeder. Bu script `migrations/` klasörünün **dışındadır** ve
   initdb.d'ye mount edilmez → fresh init sırasında asla otomatik çalışmaz. Deploy adımı
   (CI/Job) olarak elle çağrılır. Var olan 014-öncesi DB'lerde önce `014` elle uygulanır.

### EF Core'a tam geçiş — neden ertelendi (post-MVP)

- TimescaleDB `create_hypertable`/compression policy çağrıları EF Core tarafından
  üretilmez; geçiş yine de raw `migrationBuilder.Sql(...)` + var olan DB'lerde riskli
  retrospektif `__EFMigrationsHistory` seed gerektirir (L efor, düşük MVP getirisi).
- Tek geliştirici, paralel branch-migration baskısı yok → EF'in rollback/drift
  araçlarına henüz ihtiyaç yok. Şu pain'ler ortaya çıkınca yeniden değerlendirilecek.
- **Seçenek B dokümante edilmiş gelecek yoludur**; bu ADR onu silmez, erteler.

## Sonuçlar / Risk

> **NOT (Faz 4):** Bu bölümdeki sonuç/risk değerlendirmesi **ertelenen Seçenek B (EF Core)**
> yoluna aittir. MVP'de uygulanan **Seçenek C (Hybrid)** için güncel sonuçlar yukarıdaki
> "Karar Revizyonu (Faz 4 — F4-1/F4-8)" bölümündedir.

**Olumlu:**
- Schema versioning denetlenebilir hale gelir.
- EF model ↔ DB drift compile-time/CI'da yakalanır.
- Rollback komutu standart.

**Risk:**
- Hatalı baseline + history seed → mevcut production DB'de duplicate apply.
  **Mitigasyon:** Pre-production'da staging clone üzerinde dry-run; tüm
  CREATE TABLE bloklarının EF baseline'ında "already exists" → "no-op" şeklinde
  döndüğünü doğrula.
- TimescaleDB raw SQL bloklarında syntax değişikliği gözden kaçabilir.
  **Mitigasyon:** Integration test (Testcontainers + TimescaleDB image) ile
  fresh DB'yi baseline migration ile inşa et, çıktıyı mevcut 001_initial.sql
  ile diff'le.

---

## İlgili Dökümanlar

- [`CLAUDE.md`](../../CLAUDE.md) — "Migration Komutları" bölümü
- [`docs/architecture.md`](../architecture.md) — Veritabanı şeması
- Faz 0 aksiyon planı: `docs/code-reviews/ACTION-PLAN.md` (lokal)
