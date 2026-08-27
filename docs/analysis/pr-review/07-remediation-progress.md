# PR Review Remediation Kapanış ve Kanıt Kaydı

> **Yürütme branch'i:** `development`<br>
> **Başlangıç HEAD:** `f9f608dc974a`<br>
> **Branch güvenliği:** `origin/main`, çalışma HEAD'inin atası; ek branch açılmadı<br>
> **Son güncelleme:** 2026-08-24

Bu belge [aksiyon planının](06-remediation-action-plan.md) otoritatif kapanış kaydıdır. Kaynak
bulgu raporları review anındaki tarihsel kanıttır; mevcut uygulama durumunu bu belge belirler.

## Sonuç

`docs/analysis/pr-review/` envanterindeki doğrulanmış Critical, High, Medium ve Low repo
bulguları düzeltildi veya daha üst bir davranışsal kabul maddesi altında açıkça supersede edildi.
Repo kapsamında açık kod, test, CI, doküman veya statik-konfigürasyon kusuru kalmadı.

Production promotion yine de açık değildir. Gerçek staging/production kimliği, KMS, object store,
private alert receiver ve provisioned volume gerektiren dört dış kabul koşulu bu belgenin son
bölümünde release blocker olarak tutulur. Bunlar repo kusuru değil, operatör ortamında üretilmesi
gereken receipt'lerdir.

Kaynak numaralı envanter `2 Critical + 14 High + 56 Medium + 149 Low = 221` kayıttır. Planlama
severity drift'ini `M10` ve `M34` için yükseltmiş; mükerrerleri birleştirmiş; `MA-B`yi kod bulgusu
değil release-sequencing koşulu olarak ayırmıştır. Kimlik ve birleşim tablosu
[aksiyon planında](06-remediation-action-plan.md#2-envanter-uzlaştırması) korunur.

## Paket durumu

| Dalga | Paketler | Repo durumu | Dış kabul |
|---|---|---|---|
| P0 | `P0-REL`, `P0-DBM`, `P0-DEV`, `P0-ING-CI`, `P0-DR-INIT` | **Verified** | Release binding ve gerçek DR receipt'leri aşağıda |
| High | `H-DR`, `H-MON`, `H-API-SEC`, `H-ING`, `H-API-RUNTIME`, `H-FIN`, `H-REPAIR` | **Verified** | DR ve monitoring canlı ortam receipt'leri aşağıda |
| Medium | `M-DB-SCHEMA`, `M-KEYRING`, `M-SCENARIO`, `M-ROLE-AUTH`, `M-CALENDAR`, `M-DQA`, `M-BUILD`, `M-API-TEST`, `M-DOCS` | **Verified** | Yok |
| Low | `L-A`–`L-E`; mükerrer/supersede eşlemeleri dahil | **Verified** | `MA-B` merge/release sırası aşağıda |

Özellikle son açık Low denetim maddeleri de kapatıldı: canlı `market_holidays` açıklaması
forward-only 024 migration'da düzeltildi (`L38`); CalendarData one-shot CLI için scoped
`HttpClient`/console istisnası yazılı hale getirildi (`L82`); BIST kapanış takviminden yapılan açık
gün çıkarımı kanıtın niteliğini gizlemeyen `inferred_open_from_official_closure_schedule` reason
code'una bağlandı (`L85`). Prometheus exporter prerelease kullanımı otomatik stable bump yerine
[ADR-011](../../decisions/ADR-011-prometheus-exporter-prerelease.md) ile kontrollü istisnadır (`L114`).

## Uygulama özeti

- Development ve CI kontrol düzlemleri managed role/backup secret, exact HBA, post-bootstrap ve
  fresh-schema zincirleriyle ayağa kaldırıldı; physical backup yetkisi SQL erişimi vermiyor.
- Release manifest, first-party DataRepair dahil runtime image kümesinin tek otoritesi oldu;
  deploy/rollback binder, pre-mutation signature ve exact image admission ile fail-closed.
- Backup/PITR düzlemi disk-backed staging, bounded lock/backoff, `archive_timeout`, physical WAL
  observation, exact recovery high-water, cleanup ve schema-v2 receipt sözleşmeleriyle sertleştirildi.
- Monitoring ağı ayrıştırıldı; deploy readiness, exact 40 alert/11 target inventory, canlı metric
  label admission, private material ve writable-volume owner/mode kapıları eklendi.
- API management-port sınırı, registration/calculation kotaları, pending rotation admission,
  installation audit lifecycle ve credential rehash forward-only şema zinciriyle kapatıldı.
- ActivityLog transient/toxic/fatal sınıflandırması writer-local bounded recovery sağlıyor; şema ve
  auth drift'i fail-fast kalıyor. JSONB boyut kapıları PostgreSQL davranışıyla gerçek-PG pariteli.
- Ingestion mutlak deadline, backoff/`next_attempt_at`, permanent-window izolasyonu, lease fence,
  authority finality ve kanıtlı takvim cutoff'u ile fail-closed çalışıyor.
- Finansal DCA/What-if akışları terminal CPI finality, authority payload redaksiyonu, tek-query
  projection ve literal oracle testleriyle doğrulandı.
- DQA ve DataRepair; signed target/scope/budget, bütün kritik schema/ACL/function/trigger drift'leri,
  CAS/lease/uncertain-commit, fsync receipt ve gerçek-PG negatif matrisleriyle genişletildi.
- DatabaseRoleBootstrap canonical client-side SCRAM verifier, tekrar kullanılabilir versioned
  rotate/reset/retire ve bounded session drain sözleşmesine geçirildi.
- Calendar acquisition/promotion; sealed authority cutoff'u, idempotent materialization, iki-pass
  imza doğrulama, rootless uid/gid ve resource-bound image/scheduler kapılarıyla tamamlandı.

## Migration bütünlüğü

- Canonical manifest ve trust root: **26 artifact/version**; `001`–`024`, ayrıca `008b` ve optional
  `012b` dahil.
- `001`–`022` başlangıç HEAD'ine karşı byte-identical kaldı.
- `023_installation_lifecycle_admission` SHA-256:
  `1b76002b7c2e3b9156e433e1268a085027e383fa0025e82f398f2bb27aa1663e`.
- `024_installation_credential_rehash` SHA-256:
  `6cb1135983b348f5a4a153324b228a2d245f006c1fd8ef122f71f99630b99e2a`.
- SQL dosyası, `MigrationTrustRoot` ve DQA embedded pin değerleri birebir eşleşir.
- `025_impact_test` yalnız imzalı migrator acceptance tail fixture'ıdır; canonical production
  trust root'un parçası değildir.
- Fresh DB admission'ı dört hedefte exact `26,2,26,26,ready`; initial run
  `applied=26/already=0/skipped_optional=1/backup_required=true`, post-bootstrap verify
  `0/26/0/false` döndürür.

## Otoritatif test kanıtı

Tam merkezi gerçek-infrastructure koşusu:
`eb03bf08631d4517841b5faba65c203a`.

| TRX | Total / executed / passed | Failed / skipped / notExecuted |
|---|---:|---:|
| CalendarData | 92 / 92 / 92 | 0 / 0 / 0 |
| API integration | 66 / 66 / 66 | 0 / 0 / 0 |
| Ingestion ledger | 44 / 44 / 44 | 0 / 0 / 0 |
| DQA unit | 96 / 96 / 96 | 0 / 0 / 0 |
| DQA integration | 106 / 106 / 106 | 0 / 0 / 0 |
| DataRepair integration | 32 / 32 / 32 | 0 / 0 / 0 |
| RoleBootstrap unit | 98 / 98 / 98 | 0 / 0 / 0 |
| RoleBootstrap integration | 13 / 13 / 13 | 0 / 0 / 0 |
| DatabaseMigrator, iki cluster | 184 / 184 / 184 | 0 / 0 / 0 |

Koşuda exact iki-cluster HBA install/verify, `pg_basebackup`, `pg_receivewal`, backup-role SQL deny,
secret inventory, calendar release ve ingestion sonrası schema verify kapıları da geçti. Bağımsız
cleanup envanteri container/network/volume/image/temp için `0/0/0/0/0` oldu.

Ayrı root unit/coverage kapısı yedi projede **1.233/1.233** test çalıştırdı:
`655 + 182 + 97 + 78 + 98 + 29 + 94`; failed/skipped/notExecuted sıfır. Weighted unique source
coverage `%78,57` line / `%66,19` branch; değişen çalıştırılabilir satır coverage'ı `%84,03`.

Pinned .NET SDK Release solution build'i **0 warning / 0 error**. Workflow ve development Compose
validator'ları; 67 production, 16 observability, 7 private-material, 12 monitoring-runtime ve 2
volume mutation kapısı; Prometheus/Alertmanager/OTel/Tempo/Loki/Caddy doğrulamaları; release
manifest self-test 25, rollback admission, backup static 57, HBA 8, Actionlint ve bütün tracked
shell dosyaları için pinned ShellCheck geçti.

## Dış release kabul koşulları

Aşağıdaki koşullar tamamlanıp receipt'leri arşivlenmeden production promotion yapılmamalıdır:

1. **`P0-REL` / `CH-02`, `L109`:** gerçek staging kimliğiyle manifest-bound deploy çalıştırmak ve
   runtime image binding receipt'ini üretmek.
2. **`P0-DR-INIT` + `H-DR`:** production KMS/object-store üzerinde iki ardışık PITR drill'i ve
   production-benzeri büyük dataset provası çalıştırmak; imzalı schema-v2 restore receipt'lerini ve
   sıfır residual kaynak envanterini kaydetmek.
3. **`H-MON`:** private Alertmanager receiver/dead-man round-trip, gerçek live target/rule
   inventory ve provisioned writable volume owner/mode receipt'ini staging'de üretmek.
4. **`MA-B`:** alert `runbook_url` hedeflerinin erişilebilir olması için bu `development`
   değişiklikleri `main`e ulaşmadan release başlatmamak.

Bu koşullar yeni kod değişikliği gerektirmez; yetkili operatör ve dış ortam kanıtı gerektirir.
