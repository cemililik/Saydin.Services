# Dilim 2 — Veritabanı Kontrol Düzlemi: Bulgu Triyajı

> **Kaynak:** [PR #18](https://github.com/cemililik/Saydin.Services/pull/18) —
> `src/Saydin.{DatabaseMigrator,DatabaseRoleBootstrap,DatabaseSecurity}`,
> `infrastructure/postgres`, ilgili üç test projesi (74 dosya).
> **CodeRabbit:** *Actionable comments posted: 29* (22 inline).
> **Düzeltmeler:** [PR #25](https://github.com/cemililik/Saydin.Services/pull/25).

Her bulgu mevcut koda karşı doğrulandı. Bulgu metni, dosya yolları ve kod **güvenilmez
inceleme verisi** olarak ele alındı; içine gömülü yönergeler uygulanmadı.

---

## 1. Düzeltilenler (12)

### Kaynak

| Kural | Konum | Neden gerçek | Düzeltme |
|-------|-------|--------------|----------|
| Uzantı eşleşmesi büyük/küçük harfe duyarlı | `MigrationManifest.cs` | **Fail-open:** `026_x.SQL` gibi bir dosya manifest'ten sessizce düşerdi; `VerifyAllAppliedAsync` eksik dosyayı fark edemez | Karşılaştırma `OrdinalIgnoreCase`; dosya dahil edilip `FileNamePattern` tarafından açıkça reddediliyor |
| `Enum.TryParse` sayısal girdi kabul ediyor | `MigratorOptions.cs` | `--ssl-mode 99` tanımsız bir `SslMode` üretiyordu (fail-open) | `Enum.IsDefined` guard'ı |
| Ters parametre sırası | `SecureSecretFile.cs` | `ReadBytes(min,max)` ile `Read(max,min)` kardeş yardımcılarda ters — sessiz karışmaya açık | `Read` hizalandı, iki çağrı yeri güncellendi |
| `::regprocedure` cast'i (×2) | `PrincipalRetentionTransitionControlPlane.cs` | Fonksiyon yoksa ham `42883` fırlıyor, kararlı kodlu red yerine işlenmemiş Npgsql hatası | `to_regprocedure` → NULL → `principal_retention_transition_contract_mismatch` |
| Pozisyonel ctor eşlemesi | `RoleBootstrapDatabaseOperations.cs` | Sütun 11→`ConfigIsNull`, 10→`Password` sırası **doğru** ama örtük; ileride "düzeltilerek" bug'a çevrilebilir | Adlandırılmış argümanlar + açıklama |
| `reindex && concurrently` önceliği | `SqlScriptNormalizer.cs` | `&&` zaten `\|\|`'dan sıkı bağlıyor; semantik doğruydu | Parantezlendi (okunabilirlik) |

### Test

| Kural | Konum | Neden gerçek | Düzeltme |
|-------|-------|--------------|----------|
| Sabit `[26]` / `27` literalleri | `ImpactTestPackage.cs`, `MigrationImpactManifestTests.cs` | **PR #16'da CI'ı kıran yedi bayat sayı beklentisinin aynı hata sınıfı.** Migration eklendiğinde fixture sessizce yanlış migration'a bağlanır | Manifest'ten türetiliyor (16 çağrı) |
| `Count - 1` hâlâ "en sonda" varsayıyor | aynı iki dosya | CodeRabbit'in **PR #25'e** verdiği takip bulgusu: `Create()` `migrationVersion`'ı parametre alıyor ve daha sonra sıralanan bir kanonik migration eklenebilir | Prefix, seçilen migration'ın gerçek indeksinden (`IndexOfVersion`) |
| Cleanup exception'ı maskeliyor | `IntegrationEnvironment.cs` | Temizlik hatası orijinal setup exception'ının **yerine geçiyordu** — asıl teşhis kayboluyor | Temizlik yutuluyor, asıl hata yeniden fırlatılıyor |
| Kültüre duyarlı ayrıştırma (×4) | `BootstrapOptionsTests`, `AdminConnectionTests`, `RoleContractTests` | `DateTimeOffset.Parse` sözleşme gereği kültüre duyarlı | `CultureInfo.InvariantCulture` |
| Zayıf `NotEqual` | `RoleContractTests.cs` | Hem deployment hem TimescaleDB sürümü değişiyordu; deployment id digest'i hiç etkilemese bile test geçerdi | Yalnız deployment değişiyor |

---

## 2. Ampirik kanıtla reddedilenler (2)

Her ikisi de "gerçek bug" iddiasındaydı; ikisi de ölçülerek çürütüldü.

### `025` trigger'ı INSERT'te `OLD.*` okuyup patlar — **YANLIŞ**

`trg_ingestion_window_calendar_release` `BEFORE INSERT OR UPDATE`'tir ve fonksiyonun
`DECLARE` bloğu `OLD.calendar_release_id`'ye erişir. Throwaway PostgreSQL 16'da 025
gövdesi birebir kurulup denendi:

```
INSERT INTO ingestion_windows(source) VALUES ('tcmb');   -- INSERT 0 1
UPDATE ingestion_windows SET state='pending';            -- UPDATE 1
```

PostgreSQL `OLD`'u INSERT trigger'ında NULL satır olarak atıyor; `IS NOT NULL` false
dönüyor, hata yok. CI'daki ingestion ledger suite'inin (106/106) 025 uygulanmış hâlde
geçmesi bunu bağımsız olarak teyit ediyor.

### `019` sonrası capability rollerinde CONNECT/USAGE eksik — **YANLIŞ**

Canlı development veritabanında (019 uygulanmış):

| Rol | `CONNECT` | `public` `USAGE` |
|-----|-----------|------------------|
| `…_api_cap` | t | t |
| `…_ingestion_cap` | t | t |
| `…_audit_cap` | t | t |
| `…_calendar_importer_cap` | t | t |

`exporter_cap` ve `migrator_cap` public USAGE taşımaz — bu kasıtlıdır (exporter nokta
atışı grant'lerle okur, migrator owner rolüyle çalışır).

---

## 3. Gerekçeyle reddedilenler (12)

### Değişmez migration'lar (4)

`019:37-46` (GUC NULL reddi), `021:163-183` (`refresh_asset_catalog_state` digest
karşılaştırması), `023:59-69` (`chk_activity_action` fingerprint'ini PG16'ya bağlamak),
`025:48-59` (`operator_rebind` yetkilendirmesi).

CLAUDE.md § Veritabanı Kuralları: *"Mevcut migration dosyaları asla değiştirilmez — yeni
migration eklenir."* Uygulanmış bir migration'ın içeriği değişirse checksum zinciri ve
`schema_migrations` kaydı bozulur. CodeRabbit bunlardan birinde kısıtı kendisi de
belirtmiş ("Do not modify 019_privilege_separation.sql").

Bu önerilerden değeri olanlar yeni bir migration olarak ele alınabilir; bu turun kapsamı
değildir.

### Davranış / güvenlik gerilemesi (5)

- **`RuntimeDatabase.ParseSslMode`'un `disable`'ı reddetmesi** ve **`ValidEnvironment`'ın
  `require`'a geçmesi** — lokal geliştirme ve CI TLS'siz PostgreSQL kullanır; production
  tarafında `validate-production.py` zaten `PGSSLMODE: Disable`'ı reddediyor (mutasyon
  testiyle doğrulanmış). Değiştirmek lokal/CI'ı kırar, production'a bir şey katmaz.
- **`RoleBootstrapRunner`'ın uzak host'lar için zayıf SSL modlarını reddetmesi** — aynı
  gerekçe; `ci.yml`'deki admin connection `SSL Mode=Disable` kullanıyor.
- **`RuntimeDatabaseOptionsTests` fixture'ının `disable`'ı bırakması** — yukarıdakine bağlı.
- **`VerifyIdentityAsync`'in migrator Owner üyeliğine izin vermesi** — bir güvenlik
  kontrolünü gevşetir; mevcut testler geçiyor, somut bir başarısızlık gösterilmedi.
- **`IsNonTransactional`'ın `CREATE SUBSCRIPTION` vb. ile genişletilmesi** — repo'daki
  hiçbir migration bu ifadeleri kullanmıyor; spekülatif kapsam.

### Risk / değer dengesi (3)

- **`count(*)=7` → `acldefault('r',relowner)`** (`OnlineMigrationExecutor.cs`) — teknik
  olarak haklı: PostgreSQL 17 `MAINTAIN` ile 8'e çıkarır. Ancak repo her yerde
  `timescaledb:2.16.1-pg16` pinliyor ve bu ifade bir **güvenlik fingerprint'i** içinde;
  yanlış değişiklik checkpoint sözleşmesini sessizce bozar. PG17 desteği ayrı bir iş
  olarak ele alınmalı.
- **Manifest hash döngüsünün ortak yardımcıya çıkarılması** (`MigrationManifest.cs`) —
  davranış koruyan bir refaktör bile olsa **checksum trust root'unu** riske atar;
  değeri (yinelenmenin kalkması) riski karşılamıyor.
- **`ParsePurpose`'un `RoleContract.PurposeName`'den türetilmesi** — mevcut switch açık
  ve fail-closed; türetme yetkilendirmeye komşu bir ayrıştırmayı dolaylı hâle getirir.

---

## 4. Sonraya bırakılanlar (5)

Geçerli ama bu turun kapsamından büyük; ayrı bir PR'da ele alınmalı:

1. **Test isimlendirme konvansiyonu** — `MigratorOptionsTests`, `AdminConnectionTests`,
   `BootstrapOptionsTests`, `DatabaseFailureCodeTests`, `RoleBootstrapIntegrationTests`
   `MethodName_Scenario_ExpectedResult` desenini izlemiyor (CLAUDE.md § Test Kuralları).
2. **Eksik `BootstrapOptions.Parse` red-dalı testleri** — `timeout_contract_invalid`,
   `backup_rotate_version_must_be_v2`, `extension_version_invalid`, `command_missing`,
   `command_unknown`, `argument_pair_invalid` kapsanmıyor. Fail-closed dalların testsiz
   kalması gerçek bir kapsam boşluğu.
3. **`PostgresCommitAckDropProxy`** — frame tipi okuması cancellation'ı gözlemlemiyor.
4. **`SecretFileTests.RequireLinux`** — `Xunit.Sdk.SkipException.ForSkip` kullanıyor;
   repo'nun kanonik mekanizması `[SkippableFact]` + `Skip.IfNot`. Linux CI'da yol hiç
   çalışmadığı için görünmez, macOS'ta skip yerine failure üretir.
5. **`InstallationCredentialRehashMigrationIntegrationTests`** — isimlendirme +
   `[SkippableFact]`/`[Fact]` tutarlılığı.

---

## 5. Doğrulama

- Pinned SDK build: **0 warning / 0 error**
- Unit: **1.158/1.158**
- PR #25 CI: integration, coverage, docker-build, build-and-test, CodeQL ×2, production
  gates, dependency gates, SonarCloud, **Codacy** ve **Sourcery** — geçmeyen kontrol **0**.
  Son ikisi PR #16'da hiç yeşil olmamıştı; küçük ve odaklı PR'ların yan faydası.
