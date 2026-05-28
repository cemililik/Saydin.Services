# ADR-001 — Migration & Schema Evolution Stratejisi

- **Durum:** Önerilen (Faz 0'da klasör mount fix uygulandı; uzun vadeli tracker kararı bekliyor)
- **Tarih:** 2026-05-27
- **Karar verenler:** Backend ekibi
- **İlgili bulgular (code review):** Claude `[C-G-001/008/009/010]`, `[C-YAPISAL-1..4]`, `[C-H-DC-11]`; GPT-5 `[G-G-04]`, `[G-G-06]`

---

## Bağlam

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

## Sonuçlar / Risk

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
