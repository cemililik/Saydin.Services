# PR Review 2 — Remediation Yürütme ve Kapanış Kaydı

> **Tarih:** 2026-08-27<br>
> **Kaynak envanter:** [README.md](README.md), [01-findings-critical-high.md](01-findings-critical-high.md),
> [02-findings-medium.md](02-findings-medium.md), [03-findings-low.md](03-findings-low.md)<br>
> **Kapsam:** repo içi kod, migration, test, CI, Compose, backup, observability ve dokümantasyon<br>
> **Karar:** Review anındaki **2 Critical + 18 High repo engeli kapatıldı**. Medium/Low envanterin
> tamamı kapatılmış sayılmıyor; aşağıdaki açık kayıtlar ve dış ortam receipt'leri korunuyor.

Bu belge review dosyalarını yeniden yazmaz. Review, incelenen ağacın tarihsel fotoğrafıdır; bu
belge o fotoğrafa karşı hangi aksiyonların uygulandığını, hangi regresyonların yürütme sırasında
yakalandığını ve son ağacın hangi kanıtlarla doğrulandığını kaydeder.

## 1. Envanter tutarlılığı

Ana README'deki toplamlar doğruydu, severity içindeki tip dağılımı yanlıştı. Kaynak kayıtlar
yeniden sayıldığında dağılım şöyledir:

| | Critical | High | Medium | Low | Toplam |
|---|---:|---:|---:|---:|---:|
| `defect` | 2 | 18 | **52** | **22** | **94** |
| `excellence-gap` | — | — | **69** | **122** | **191** |
| **Toplam** | **2** | **18** | **121** | **144** | **285** |

Dolayısıyla `40 Medium + 34 Low defect` satırı `52 + 22` olarak düzeltildi; toplam defect,
excellence-gap ve genel kayıt sayısı değişmedi.

## 2. Critical ve High kapanış matrisi

| Bulgu | Uygulanan kök-neden aksiyonu | Regresyon kilidi / kanıt |
|---|---|---|
| Critical #1 | OTel health endpoint'i ağ readiness probe'uyla uyumlu `0.0.0.0:13133` bind'ına alındı. | Observability validator ve `loopback_health_endpoint` negatif mutasyonu. |
| Critical #2 | Migration sayısı shell kapılarında manifestten türetiliyor; `already_applied=N` literal'i yasaklandı. | Workflow validator ve gerçek fresh-stack development smoke. |
| High #3, #4, #6 | IPv4 registration günlük ağ kovası kaldırıldı; saatlik limit yüksek kapasiteli ve exact-IP tabanlı. IPv6 /64 günlük koruması korunuyor. Calculation network admission principal çözümünden sonra çalışıyor. | Limiter unit testleri, config/validator mutasyonları ve NAT dokümantasyonu. |
| High #5, #19 | Migration 024 lookup'ı indekslenebilir eşitlik probe'una döndürüldü; rehash yalnız gerekli sürüm geçişinde ve koşullu kilitle çalışıyor. | Raw SHA ve function-body SHA trust-root/DQA pinleri; migrator/DQA testleri. |
| High #7 | Activity writer varsayılan bilinmeyen hata yolu transient yapıldı; fatal yalnız açık şema/yetki sözleşme sınıflarında kaldı. Writer ölümü ayrıca ölçülüyor. | SQLSTATE ve Postgres-dışı exception sınıflandırma testleri. |
| High #8, #9 | TwelveData ve TCMB için aynı gün henüz yayımlanmamış veri retryable; takvim release'ine kalıcı bağlanan pencere migration 025 ile güvenli biçimde yeniden kuyruğa alınabiliyor. | Provider unit testleri, migration 025 ve gerçek fresh-schema smoke. |
| High #10, #17 | Role graph ve migrator aktif version'dan türetiliyor; eski roller NOLOGIN olarak doğrulanıyor; stable production secret alias'ları current role'a bağlanıyor. | RoleBootstrap 98/98, Migrator 78/78 ve yaşam döngüsü integration senaryoları. |
| High #11, #12, #14 | Calendar materialization atomik replace kullanıyor, farklı gün tekrarları destekleniyor; TCMB weekend/stale publication guard'ı ve güvenli candidate kökü eklendi. | CalendarData 94/94 ve negatif candidate/promotion testleri. |
| High #13 | Restic smoke root-owned geçici içeriği kontrollü temizliyor; cleanup geniş exception yolunda da çalışıyor. | Gerçek Docker backup static/behavior koşusu. |
| High #15 | Linux ownership testleri gerçek root/`chown` bağımlılığından ayrıldı ve fake UID ile deterministik hale getirildi. | RoleBootstrap unit süiti, sıfır skip. |
| High #16, #18, #20 | CLAUDE/CONTRIBUTING Compose komutları runtime env ve bootstrap önkoşuluyla düzeltildi. | Doküman komut sözleşmesi, development Compose validator ve link kapısı. |

Sonuç: Critical/High kayıtların hiçbiri yalnız yorum veya doküman beyanıyla kapatılmadı; her kök
neden kod/konfigürasyon değişikliği ve en az bir otomatik kabul mekanizmasıyla bağlandı.

## 3. Uygulanan Medium ve çapraz-kesen aksiyonlar

Review'deki tekrar eden kayıtlar kök neden paketleri altında birleştirildi:

- **Security/admission (#1, #2, #9, #13, #106, #114, #115):** kalıcı istemci-adresi hatası ile
  geçici Redis/limiter arızası ayrıldı; 503 kodları ve `Retry-After` semantiği düzeltildi; günlük
  kota anahtarlarından ham GUID çıkarılıp ayrı HMAC domain'i kullanıldı; başarısız registration
  handler'ı ayırdığı saatlik kotayı atomik olarak geri bırakıyor; admission-unavailable alarmı
  eklendi.
- **Activity log (#10–#12, #17, #69, #105):** iptal edilen flush kayıp sayılmıyor; drain bütçeleri
  host timeout'una sığıyor; writer-dead/shutdown-abandoned sinyalleri var; retry edilmiş insert
  `ON CONFLICT DO NOTHING` ile commit-ACK kaybına dayanıklı; truncation metriği alarma bağlı.
- **Finansal doğruluk (#18–#21, #109):** İstanbul iş günü/tarih sınırı kullanılıyor; hesap ham
  birimle yapılıp yalnız gösterim yuvarlanıyor; toplam alım gerçek istek tutarıyla mutabık; DCA
  terminal LKV kademesi ara aylarda da uygulanıyor.
- **Ingestion/provider (#22, #23, #33, #35, #37, #39, #40, #85, #87):** lease renewal geçici
  hataları bounded retry alıyor; retry penceresi deterministik exponential backoff kullanıyor;
  JSON null güvenli; circuit sampling deadline'ı kapsıyor; transport/body limitleri ayrıldı;
  kullanılan auth şemaları logda maskeleniyor; ölü `HttpClient.Timeout` ayarları kaldırıldı;
  failure-finalization timeout'u host'u düşürmüyor; weekend coverage guard fail-open değil.
- **DQA/DataRepair ve role yaşam döngüsü (#41, #43–#48):** kanıt ve imzalı girdi schema-v2'ye
  taşındı; DQ-001…DQ-009 kapsamı, production-target authority, imzalı onarım/receipt ve current-role
  production bağları eklendi; runbook'lar güncellendi.
- **Calendar güvenliği (#50, #52, #53):** promotion yalnız canonical staging root'un doğrudan,
  symlink olmayan candidate child'ını kabul ediyor; sürekli firing TCMB alarmı kaldırıldı; gerçek
  günlük tekrar/candidate negatifleri test edildi.
- **Backup/PITR (#54–#58, #60, #62, #107, #121):** base backup sırasında WAL'ın off-host akışı
  kesilmiyor; doğrulanamayan snapshot `wal-unverified` etiketiyle ve freshness ilerletmeden tutuluyor;
  PostgreSQL komutları wall-clock timeout'lu; spool tam olarak `/work/wal`, non-tmpfs, UID 1001,
  mode 0700 ve en az 96 GiB boş alan sözleşmeli; temp adları sabit ve exit/startup cleanup'lı;
  SQL-deny outage'dan `pg_isready` ile ayrılıyor; gerçek replication-mode `IDENTIFY_SYSTEM` ve
  `SHOW wal_segment_size` acceptance'a bağlı. Docker yoksa lokal çıktı açık `skipped:` listesi
  taşırken CI/release fail-closed; kontrol sayısı 64'e pinli.
- **Observability/release (#63, #64):** `/api/v1/series` yalnız son beş dakikayı sorguluyor.
  Alertmanager private material doğrulaması artık yapı-sensitif: watchdog route'u
  `external-watchdog`, `repeat_interval <= 1m`, `send_resolved: true`, HTTPS ve operatör
  receiver'larından ayrı host şartlarını uygular; dört negatif mutasyon bunu kilitler.
- **Build/test/docs (#72, #76, #77, #96, #97, #100–#103, #110, #111, #116):** migration sayısı
  türetiliyor; lokal unit servisi DB/Redis'ten ayrıldı ve yalnız explicit mevcut `.csproj` kabul
  ediyor; EF parity migration sırasındaki DROP CONSTRAINT'i hesaba katıyor; aktif dokümanlar ve
  integration eşikleri güncellendi; OpenAPI `limit/cursor` sözleşmesi geri geldi; CanonicalJson
  depth paritesi testleniyor. Önceki remediation belgesinin mutlak “kusur kalmadı” iddiası yerine
  bu belgedeki açık-risk kaydı otoritatif hale getirildi.

## 4. Yürütme sırasında bulunan ek regresyonlar

Statik incelemede görünmeyen iki trust uyuşmazlığı gerçek, sıfırdan PostgreSQL smoke'unda yakalandı:

1. Migration 025 trigger fonksiyonunu yeniden oluştururken hardened `search_path` niteliğini
   düşürmüştü. Fonksiyon `SET search_path=pg_catalog,pg_temp` ile düzeltildi; raw migration ve body
   pinleri güncellendi.
2. Migration 024'ün sargable düzeltmesi function body'yi değiştirmiş, Migrator/DQA beklenen body
   SHA'sı eski kalmıştı. Beklenti gerçek gövdeyle yeniden pinlendi.

| Artifact | SHA-256 |
|---|---|
| `023_installation_lifecycle_admission.sql` | `1b76002b7c2e3b9156e433e1268a085027e383fa0025e82f398f2bb27aa1663e` |
| `024_installation_credential_rehash.sql` | `afda0e5a86b8d4b2c6b0f809372db72933f5c7e5b4b1dd18eaa8dd50dbc773d9` |
| `025_ingestion_calendar_rebind.sql` | `a20338e2d3db8f75a848949a937baaee4fa3f426e58814a4de352b0cfc2be051` |
| 024 function body | `b009448de892a425e191e649fbd942b6dd77777fa68d9b339b8010cadcbb3de2` |
| 025 trigger function body | `ae2468290e4f09338e9120f25bafa65e3575b1f6dc941aa65e4867f733de428a` |

Final fresh-stack koşusu 27 migration'ı uyguladı; pre-bootstrap, exact HBA, post-bootstrap,
verify-only, API/exporter health ve sıfır residual cleanup ile `development_compose_smoke_passed`
üretti.

## 5. Doğrulama kanıtı

| Kapı | Sonuç |
|---|---|
| Pinned .NET SDK Release solution build | **0 warning / 0 error**, 21 proje |
| Root unit matrisi | **1.236/1.236**, fail/skip 0: API 658, ingestion 182, DQA 97, Migrator 78, RoleBootstrap 98, DataRepair 29, CalendarData 94 |
| Development fresh-stack smoke | **PASS**, 27 migration + bootstrap/HBA/verify/API/exporter + residual `0:0:0:0` |
| Production asset paketi | **PASS**: production 68, observability 18, private-material 11, monitoring-runtime 12, volume 2 mutasyon |
| Prometheus / Alertmanager / OTel / Tempo / Loki / Caddy | Native config/rule testleri **PASS**; host-backup 12 kural |
| Backup static + behavior | **64/64 PASS**, gerçek Docker/restic/base-backup/archive/volume smoke'ları |
| Workflow / development Compose / docs | 6 workflow, 21 Compose mutasyonu, 98 dosya ve 209 lokal link **PASS** |
| Migration trust sonrası hedefli süitler | Migrator 78/78 ve DQA 97/97 **PASS** |

Bu yürütmede final ağaca karşı bütün çok-veritabanlı canonical integration matrisi yeniden
koşturulmadı. Gerçek PostgreSQL fresh development smoke'u ve hedefli trust süitleri çalıştı; önceki
integration receipt'i final değişikliklerin tümünü temsil ediyor diye sunulmuyor.

## 6. Açık kayıtlar ve yayın sınırı

Critical/High engel kalmadı. Aşağıdaki Medium defect kayıtları bilinçli olarak açık bırakıldı;
merge blocker değil, P1 teknik borç olarak izlenmelidir:

| Kayıt | Açık iş |
|---|---|
| #7 | Eski installation key sürümünün kullanım/rehash telemetrisi ve güvenli retirement runbook'u. |
| #24 | `next_attempt_at` uyanma testini tautolojik fixture'dan bağımsız zaman oracle'ına taşımak. |
| #25 | Freshness hydration'ı gerçekten hosted-service boundary üzerinden test etmek. |
| #26 | Üretimde erişilemeyen ingestion compatibility kodunu kaldırmak veya açık deprecation sınırı koymak. |
| #31 / #34 | Çok-istekli provider pencereleri için deadline'ı istek bütçesiyle boyutlandırmak; iki kayıt aynı kök nedendir. |
| #36 | Polly total/attempt timeout'larını gerçek gecikmeli handler ile davranışsal olarak test etmek. |
| #49 | Bir aydan uzun calendar acquisition kesintisi ve ay/yıl başı archive yayın gecikmesi için catch-up planı. |
| #93 | IntegrationEnvironment güvenlik testini hedeflediği koda ulaştırmak. |
| #94 | DataRepair reject-code ve DQA imza sınırı negatif kapsamını tamamlamak. |

Low envanterde 22 defect, ayrıca Medium/Low toplam 191 excellence-gap bulunuyor. Bu yürütme bazılarını
yan etki olarak kapatsa da kayıt bazında tam bir Low/excellence reconciliation yapılmadığından toplu
“kapandı” iddiası yoktur; [07-excellence-roadmap.md](07-excellence-roadmap.md) backlog kaynağıdır.

Production promotion için repo-yeşil olması tek başına yeterli değildir. Gerçek staging kimliğiyle
signed deploy, KMS/object-store üzerinde PITR drill receipt'i, private Alertmanager/dead-man
round-trip ve provisioned volume owner/mode kanıtları üretilmeden production promotion yapılmamalıdır.

## 7. Son karar

- Review'in P0 kapsamı olan 2 Critical ve 18 High için **repo içi engel kaldırıldı**.
- Final kod ağacı build, unit, gerçek fresh-schema smoke, backup behavior ve production asset
  kapılarında yeşildir.
- Merge kararı verilebilir; ancak açık Medium/Low backlog kabulü ve canonical integration CI koşusu
  PR korumasında zorunlu kalmalıdır.
- Production release, yukarıdaki dış ortam receipt'leri olmadan onaylanmamalıdır.
