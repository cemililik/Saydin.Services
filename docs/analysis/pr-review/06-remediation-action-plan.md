# PR Review Remediation Aksiyon Planı

> **Başlangıç tabanı:** `development` @ `f9f608d`
> **Main eşliği:** `origin/main` @ `9067dd2`, `development` geçmişinde; divergence yok (`0 behind / 3 ahead`)
> **Kapsam:** `docs/analysis/pr-review/` altındaki bütün Critical, High, Medium ve Low kayıtlar
> **Yürütme branch'i:** yalnız `development`; ek branch açılmayacak
> **Durum (2026-08-24):** Plan uygulandı; exact kapanış ve dış release koşulları için
> [07-remediation-progress.md](07-remediation-progress.md) otoritatiftir.

## 1. Sonuç ve temel yaklaşım

İş, önem derecesi kadar bağımlılık sırasına göre de yürütülecek. Önce yanlış-yeşil kapılar ve
çalışmayan kontrol düzlemleri düzeltilecek; ardından veri kaybı, güvenlik, üretim sürekliliği ve
finansal doğruluk riskleri; sonra Medium ve Low kayıtlar kapatılacak.

Bir bulgu yalnız aşağıdaki koşullar birlikte sağlandığında kapanmış sayılır:

1. Kod/konfigürasyon/doküman davranışı düzeltilmiş veya bulgunun daha üst bir kayıtta
   supersede edildiği açıkça kaydedilmiş olmalı.
2. Eski hatayı gerçekten tetikleyen regresyon testi önce kırmızı, düzeltmeden sonra yeşil olmalı.
3. İlgili dar test süiti ve bağlı geniş mekanik kapı geçmeli.
4. Kabul kanıtı komut, sonuç ve gerekiyorsa receipt/artefakt ile kaydedilmeli.
5. Satır kapsamı tek başına güvenlik veya davranış kanıtı sayılmamalı.

`018` ve `022` dahil mevcut `001`–`022` migration byte'ları değiştirilmeyecek. Şema değişikliği
gerekirse yalnız yeni `023+` forward-only migration, trust-root, manifest, checksum ve gerçek-PG
negatifleriyle eklenecek.

## 2. Envanter uzlaştırması

Kaynak raporda numaralı envanter `2 Critical + 14 High + 56 Medium + 149 Low = 221` olarak
hesaplanmış. Uygulama planı aşağıdaki düzeltmeleri ayrıca izler:

- `M10`, required migrator job'ını deterministik kırdığı için **P0/High** planlama seviyesine
  yükseltildi.
- `M34`, ilan edilen 15 dakikalık RPO'yu ihlal ettiği için **High** planlama seviyesine
  yükseltildi. README zaten bunu High olarak özetliyor; ayrıntılı rapordaki Medium etiketi sapmış.
- `MA-A`, ayrı 222. bulgu değil; `M2 + M7` limiter gözlemlenebilirlik paketinin çapraz etki
  açıklaması olarak tutulacak.
- `MA-B`, ayrı kod kusuru değil; `development` içindeki runbook'lar `main`e ulaşmadan production
  deploy yapılmamasını gerektiren bir **release koşulu** olarak tutulacak.
- README'deki “5 PLAUSIBLE” iddiası tekil ID'lerle geri izlenemiyor. Critical/High kayıtların tümü
  `CONFIRMED`; Medium kayıtlar da `CONFIRMED`/`CONFIRMED (verifier)`. Beş ID tahmin edilmeyecek;
  özgün doğrulayıcı çıktısı bulunamazsa toplu iddia rapordan kaldırılacak.

Bu normalizasyonla çalışma kuyruğu: `2 Critical + 16 High-priority + 54 Medium + 149 Low`, ayrıca
bir release koşuludur. Kaynak ID'ler değiştirilmeyecek.

Critical/High dosyasına yapılan atıflarda şu sabit kısa kimlikler kullanılacak:

| Kimlik | Kaynak sıra | Önem | Kısa ad |
|---|---:|---|---|
| `CH-01` | 1 | Critical | Kök development Compose backup argümanları |
| `CH-02` | 2 | Critical | Release runtime image map `KeyError` |
| `CH-03` | 3 | High | Management path trailing-slash bypass |
| `CH-04` | 4 | High | Principal mint ile kota sıfırlama |
| `CH-05` | 5 | High | DCA terminal CPI / null reel getiri |
| `CH-06` | 6 | High | ActivityLog transient DB hatasında host düşmesi |
| `CH-07` | 7 | High | Backup login `VALID UNTIL` yaşam döngüsü |
| `CH-08` | 8 | High | Permanent ingestion window crash-loop |
| `CH-09` | 9 | High | `next_attempt_at` zamanlayıcı tarafından yok sayılıyor |
| `CH-10` | 10 | High | Provider gövdesinde mutlak timeout yok |
| `CH-11` | 11 | High | ActivityLogLoss alert'i hiç tetiklenemiyor |
| `CH-12` | 12 | High | Deploy monitoring düzlemini başlatmıyor |
| `CH-13` | 13 | High | Restore drill `CHOWN` yetkisi olmadan ölüyor |
| `CH-14` | 14 | High | Base backup tmpfs/memory sınırında kırılıyor |
| `CH-15` | 15 | High | Ingestion-ledger migration count testi bayat |
| `CH-16` | 16 | High | DataRepair yıkıcı guard test açığı |

### Konsolide kapanacak kayıtlar

Aşağıdaki kayıtlar kaybolmayacak; aynı davranışsal kabul maddesi altında birlikte kapanacak:

- `L1 → M42` (`L1` içindeki 401 iddiası bayat; açık kalan 429/503'tür)
- `L4 → CH-03`, `L8 → M53`, `L13 → M8`, `L16 → M6`, `L37 → M52`
- `L103 → M55`, `L109 → CH-02`
- `L50 ↔ L134`, `L54 ↔ L136`, `L55 ↔ L126`, `L61 ↔ L128`
- `L88 ↔ L132`, `L115 ↔ L139`
- `M1 + M6 + M44` tek lokalizasyon sözleşmesi
- `M2 + M7 + MA-A` tek limiter gözlemlenebilirlik/runbook paketi
- `M46 ⊂ M47`, `M23 + M49 ⊂ CH-16`, `M33 ⊂ CH-12`
- `M35` base-backup paketi içinde `CH-14` ile birlikte

## 3. Alınmış teknik kararlar

1. **Base backup:** Streaming yerine disk-backed, yalnız backup uid'sinin yazabildiği ayrı staging
   volume seçildi. Böylece mevcut `pg_verifybackup`, system-identifier ve restore formatı korunarak
   blast radius küçültülecek. Başarı/hata/sinyal yollarında temizleme ve kapasite preflight zorunlu.
2. **ActivityLog dayanıklılığı:** Host genelinde `BackgroundServiceExceptionBehavior=Ignore`
   kullanılmayacak. Transient/toxic/fatal sınıflandırması writer içinde yapılacak; bounded retry ve
   görünür drop metriği korunacak. Gerçek şema/yetki drift'i fail-fast kalacak.
3. **DCA terminal CPI:** Ara katkı aylarında exact CPI şartı korunacak; yalnız terminal deflatörü
   için terminal tarihten küçük/eşit son final CPI kullanılacak. Kullanılan ay açıkça dönecek ve
   `RealReturnMethod` yeni sürüme yükseltilecek.
4. **TCMB gün seçimi:** Hard-coded “bugün” veya “dün” yerine aktif authoritative calendar içinde
   provider cutoff'unu aşmayan en son kanıtlı `observation_expected=true` gün kullanılacak. Bugünün
   yayını kanıtlıysa bugün; değilse son kanıtlı eligible gün. Kanıtsız ileri coverage fail-closed.
5. **Registration koruması:** Attestation ilk kurtarma paketini bloklamayacak. Önce exact-IP ve ağ
   prefix'i için atomik saatlik/günlük registration bucket'ları ile principal yanında network
   pseudonym calculation bucket'ı eklenecek. Production limit değerleri environment'ta zorunlu ve
   ölçülebilir olacak; shared-NAT testleri bulunacak.
6. **Low `L42`:** Bağımsız üretim arızası değil, impact-manifest üst kapısının arkasındaki
   defense-in-depth lexer açığı olarak korunacak ve regresyon testiyle düzeltilecek.
7. **Low `L114`:** Otomatik “stable paket bump” yapılmayacak. Resmî paket hâlâ beta-only ise
   belgeli prerelease istisnası veya doğrulanmış exporter değişimi seçilecek.

## 4. Uygulama dalgaları

### Dalga 0 — Envanter ve kapı güvenilirliği

Kod düzeltmesinden önce:

- Bu plandaki ID ledger'ı `Open / In progress / Verified / Superseded` durumlarıyla tutulacak.
- `README.md`, `04-mechanical-gates.md` ve bulgu sayımları yukarıdaki normalizasyona göre
  düzeltilecek; izlenemeyen PLAUSIBLE iddiası temizlenecek.
- `development` branch'i ve `origin/main` ancestry her batch başında doğrulanacak.
- Mevcut 24 migration'ın raw SHA seti kaydedilecek; her batch sonunda değişmezlik kontrol edilecek.
- Repodaki 21 `.csproj`, 7 Dockerfile, bütün tracked shell/Python/YAML/JSON dosyaları dinamik
  envanterlenecek; hard-coded eksik listeler kapı sayılmayacak.

### Dalga 1 — P0: kontrol düzlemlerini ve required CI'ı ayağa kaldır

Sıra bağımlıdır; bir kabul tamamlanmadan sonraki production-admission işi kapanmış sayılmaz.

| Sıra | Paket | Kapsam | Uygulama özeti | Zorunlu kabul |
|---:|---|---|---|---|
| 1 | `P0-REL` | `CH-02`, `L109` | 11 runtime image anahtarını tek Python otoritesinden manifest, renderer ve deploy binder'a üret | Eksik/fazla key negatifleri; gerçek binding bloğu; staging script binding aşamasını geçer |
| 2 | `P0-DBM` | `M10`, `M46`, `M47` | Üretimdeki `ValidateTrustedPrefix` ile test beklentilerini hizala; ölü historical yolunu kaldır; impact reject matrisi | Required migrator TRX `total=executed=passed`, skip/fail 0; güncel ratchet |
| 3 | `P0-DEV` | `CH-01`, `CH-07` kod yolu | Managed backup rolünün ileri tarihli validity uzatmasını transaction içinde destekle; dev secret/argüman zincirini tamamla. `CH-07` alarm/runbook kapanışı `H-DR`dedir | Fresh stack ve ikinci bootstrap exit 0; expiry/extension/foreign marker negatifleri; root Compose smoke CI |
| 4 | `P0-ING-CI` | `CH-15`, `L131` | Migration020 testinden toplam migration sayısı sorumluluğunu kaldır; exact trust-root/terminal kontrole dayan | Required ingestion-ledger sıfır skip/fail; yeni migration bu testi bozmaz |
| 5 | `P0-DR-INIT` | `CH-13`, `L111` | Yalnız volume-init container'ına `CAP_CHOWN`; taze volume uid/gid/mode ve cleanup smoke | Gerçek Docker smoke; E2E drill gerçek, imzalı receipt üretir; artık volume/network/container kalmaz |

### Dalga 2 — High: veri kaybı, üretim görünürlüğü ve dış güvenlik yüzeyi

| Sıra | Paket | Kapsam | Ana karar ve kabul |
|---:|---|---|---|
| 1 | `H-DR` | `CH-07` operasyon kapanışı, `CH-14`, `M34–M38`, `L106–L108`, `L111–L112` | Backup expiry preflight/alarm ve validity yenileme runbook'u; disk-backed staging; ilk backup hemen; bounded retry; Restic lock retry; run-id DQA dizini; `archive_timeout`; gerçek son recoverable segment metriği. Memory limitinden büyük backup, iki ardışık drill ve düşük-trafik 15 dk RPO kanıtı |
| 2 | `H-MON` | `CH-11`, `CH-12`, `M28–M33`, `L98–L105` | Prometheus/Alertmanager + dört exporter deploy/reload; gerçek rule inventory/readiness; label-bağımsız ActivityLogLoss; pozitif ve negatif promtool testleri; network/validator hardening |
| 3 | `H-API-SEC` | `CH-03`, `CH-04`, `M1–M2`, `M6–M7`, `M42`, `M44`, `L1–L4`, `L116–L117`, `L123`, `MA-A` | Public/management endpoint ayrımı ve Caddy defense-in-depth; bounded registration/network quota; localized ProblemDetails; reason metriği. Slash varyantları public'te kapalı, limit aşımı DB satırı yaratmadan 429, Redis-down fail-closed ve görünür |
| 4 | `H-ING` | `CH-10 → CH-08 → CH-09`, `M13–M15`, `M17–M20`, `L55–L63`, `L124–L130`, `L133` | Önce body+parse dahil mutlak deadline, sonra scope izolasyonu, sonra `next_attempt_at` scheduler. Permanent asset sibling'ları durdurmaz; 5/30 dk retry zamanında; stalled stream lease'i sonsuz yenilemez |
| 5 | `H-API-RUNTIME` | `CH-06`, `L20–L28` | Writer-local transient/toxic/fatal sınıflandırması ve bounded recovery. PG restart/53300 host'u düşürmez; 42xxx/3D000/28xxx contract drift fail-fast |
| 6 | `H-FIN` | `CH-05`, `M5`, `M8`, `L13–L19`, `L118–L121` | Terminal LKV CPI sürümlü sözleşmesi; raw authority payload cache/API DTO'dan çıkar; literal finansal oracle'lar. Güncel varsayılan DCA reel sonucu non-null, ara CPI eksikliği fail-closed |
| 7 | `H-REPAIR` | `CH-16`, `M22–M23`, `M48–M49`, `L64–L76`, `L141–L149` | Trust/target, guard/CAS, lease/uncertain commit ve receipt/evidence test matrisi. Güvenlik-kritik reject invariant'larının her biri gerçek-PG veya uygun fault-injection ile tetiklenir; ratchet yükselir |

`H-DR` ile `H-MON` production compose ve release dosyalarını ortak kullandığı için aynı anda dosya
düzenlemeyecek. `H-ING` içindeki üç High aynı worker dosyalarına dokunduğundan sıralı yürütülecek.

### Dalga 3 — Kalan Medium kayıtlar

High paketleriyle birlikte kapanmayan Medium kayıtlar aşağıdaki domain sırasıyla bitirilecek:

| Paket | Medium ID'leri | Bağımlılık / kabul odağı |
|---|---|---|
| `M-DB-SCHEMA` | `M9` | Donmuş 022 öncesi bounded/resumable preparation; production-benzeri hacim, kesinti ve disk/timeout provası |
| `M-KEYRING` | `M3` | Aktif key sürümüne atomik verifier upgrade; revoked/pending yükselmez; activity pseudonym sabit; gerekiyorsa 023+ |
| `M-SCENARIO` | `M4` | 018 değişmeden preflight, kontrollü export/arşiv prosedürü ve ADR hizası; veri silme ayrı operatör onayı |
| `M-ROLE-AUTH` | `M11–M12` | Düz parolayı PostgreSQL gözlem yüzeyinden çıkaran istemci-side SCRAM verifier; tekrar kullanılabilir rotation/retire sözleşmesi ve runbook |
| `M-CALENDAR` | `M16`, `M24–M27` | En son kanıtlı eligible gün; plan materializer; gerçek `verify-candidate.sh`; uid/gid promotion uyumu; `M27` pre-release blokerdir |
| `M-DQA` | `M21`, `M48` | Çok-window lane sayım drift'i, inflation fence ve bozuk-veri preflight negatifleri |
| `M-BUILD` | `M39–M40`, `M45` | 21 proje envanteri; RoleBootstrap/DatabaseSecurity solution'a dahil; default unit ve izole gerçek-integration komutları net |
| `M-API-TEST` | `M41`, `M43` | Paralel MeterListener flake'ini kaldır; finansal payload redaksiyonunu SUT girdisiyle kanıtla |
| `M-DOCS` | `M50–M56` | Davranışlar tamamlandıktan sonra migration/Redis/ADR/cache/alert/Docker-only dokümanlarını tek geçişte hizala |

Bu tabloyla birlikte High paketlerine katılan Medium ID'ler de sayılır: `M1–M2`, `M5–M8`,
`M10`, `M13–M15`, `M17–M20`, `M22–M23`, `M28–M38`, `M42`, `M44`, `M46–M49`.
Dolayısıyla `M1..M56` aralığında kapsam dışı ID yoktur.

### Dalga 4 — Low kayıtlar

Low kayıtlar tek devasa değişiklik olarak ele alınmayacak. Önce aşağıdaki güvenlik/operasyon ağırlıklı
Low'lar kendi üst domain paketlerine yükseltilerek gerçek entegrasyon kabulüyle kapatılacak:

`L17, L39, L41, L48, L53, L57, L62, L63, L65, L66, L69–L77, L81, L84–L85,
L96, L100–L101, L104–L105, L107, L111, L117, L125, L130, L133, L141–L142, L149`.

Kalan Low işleri şu kesintisiz lane batch'leriyle yürütülecek:

Aşağıdaki aralıklar aynı zamanda dosya-sahipliği haritasıdır; üst pakette kapanmış bir Low bu dalgada
yeniden uygulanmaz, yalnız parent kabul kanıtına bağlı olduğu doğrulanır.

| Batch | Lane / Low ID aralığı | Kabul odağı |
|---|---|---|
| `L-A` | L01 `1–4`, L02 `5–12`, L03 `13–19`, L04 `20–28`, L05 `29–35`, L18a `116–123` | API/EF/RFC7807/OpenAPI/localization, gerçek SQL paritesi, literal finansal beklentiler, sessiz platform PASS yok |
| `L-B` | L06 `36–38`, L07 `39–49`, L08 `50–54`, L18c `134–140` | Migration immutability, exact reject kodları, bootstrap/security gerçek-PG, solution/test envanteri |
| `L-C` | L09 `55–57`, L10 `58–63`, L13 `77–87`, L18b `124–133` | Gerçek concurrency/write fence, typed provider outcome, culture-independent replay, non-root calendar promotion |
| `L-D` | L11 `64–68`, L12 `69–76`, L18e `141–149` | Signed-input kod matrisi, fiziksel hedef/ACL, receipt fsync/fault injection, OCI KMS cancellation ve test izolasyonu |
| `L-E` | L14 `88–97`, L15 `98–105`, L16 `106–112`, L17 `113–115` | CI/release/observability/backup mutation testleri, bütün restore/locked-mode/build envanteri |

Low `1..149` ID aralığı böylece tam ve tekil olarak kapsanır. `MA-B`, production promotion'dan
önce 12 benzersiz runbook hedefinin `main` üzerinde erişilebilir olduğunun doğrulandığı ayrı release
koşuludur; `development`tan `main`e merge bu plan kapsamında otomatik yapılmayacak.

### Dalga 5 — Doküman ve kanıt kapanışı

- Her bulgu ledger'ında çözüm commit'i, test adı, komut ve sonuç yer alacak.
- `README.md`, `04-mechanical-gates.md`, `05-lane-summaries.md` ve mevcut remediation progress
  kayıtları gerçek son sayılarla güncellenecek.
- Doküman-only bulgular davranış kodundan sonra kapanacak; aynı doküman tekrar tekrar yazılmayacak.
- “Kapandı” denilen hiçbir test için yalnız eski sayı veya line coverage gösterilmeyecek.

## 5. Paralel agent çalışma modeli

Kontrolü korumak için aynı anda en fazla **iki implementer agent + ana agent** çalışacak. Kritik/high
paketlerde yüksek muhakemeli model, mekanik test/doküman batch'lerinde dengeli model kullanılabilir.

Kurallar:

1. Her agent'a tek, bounded paket ve açık dosya sahipliği verilir.
2. Aynı dosyaya iki agent yazmaz. `deploy-release.sh`, `compose.production.yml`,
   `.github/workflows/ci.yml`, `Saydin.Services.sln`, `Directory.*` ve plan/progress dokümanlarının
   entegrasyonu ana agent'a aittir.
3. Agentlar yeni branch açmaz ve branch değiştirmez; yalnız `development` üzerinde çalışır.
4. Implementer kendi dar testini çalıştırır; ana agent diff review + geniş kapıdan sonra değişikliği
   kabul eder. Güvenlik/DR/DataRepair paketlerinde bağımsız verifier kullanılır.
5. Paylaşılan dosya gerektiren paketler paralel değil, bariyerli yürütülür.
6. Commit gerekiyorsa ana agent, doğrulama sonrası küçük ve domain-bazlı commit oluşturur; başarısız
   gate varken sonraki pakete geçilmez.

Önerilen ilk paralel dağılım:

- Agent A: `P0-REL` (release runtime-map otoritesi ve davranış testi)
- Agent B: `P0-DBM` + `P0-ING-CI` (yalnız test/source dosyaları; workflow entegrasyonu hariç)
- Ana agent: `P0-DEV`, paylaşılan CI/Compose entegrasyonu ve bütün diff doğrulaması

## 6. Mekanik ve davranışsal final kapıları

Kapılar hızlı/deterministikten pahalı/ortam bağımlıya doğru bu sırayla çalıştırılacak:

1. **Branch ve değişmezlik:** `development`, `origin/main` ancestry, beklenmeyen diff yok;
   `001–022` raw SHA seti değişmemiş.
2. **Sözdizimi/statik:** bütün tracked shell dosyalarında `bash -n` + ShellCheck; Python AST/compile;
   JSON/YAML parse; `.yml` ve `.yaml`; workflow validator/actionlint; doc linkleri.
3. **Mutation/self-test:** release manifest binding, rollback admission, backup HBA/static,
   observability, production/development Compose, coverage ve workflow self-testleri.
4. **Locked restore:** exact SDK image/digest ile bütün projelerde lockfile zorlaması.
5. **Build:** solution/envanterdeki 21 projenin tamamı, 0 warning / 0 error.
6. **Unit + coverage:** bütün unit projeleri; `total == executed == passed`, failed/skipped/notExecuted
   sıfır; güncellenmiş ratchet ve coverage eşikleri.
7. **Compose/images:** development, integration ve production render; 7 gerçek Dockerfile build;
   hardening/secret/permission mutationları.
8. **Gerçek altyapı entegrasyonu:** API, ingestion ledger, migrator, role-bootstrap, calendar, DQA ve
   DataRepair scriptleri; her biri exact minimum ve zero-skip/fail.
9. **Development canlı smoke:** fresh project/volume/secret, role-bootstrap, 24+ migration,
   `database-migrator --verify-only`, API/Redis/PostgreSQL readiness ve ikinci bootstrap yakınsaması.
10. **Staging/DR:** manifest-bound staging deploy; monitoring readiness + rule inventory; backup,
    düşük-trafik WAL RPO ve gerçek restore drill; imzalı receipt.
11. **Remote required CI:** build, integration, coverage admission, production assurance,
    supply-chain, CodeQL ve bütün Docker build job'ları yeşil.
12. **Release koşulu:** runbook hedefleri `main`de erişilebilir; open bulgu sayısı sıfır;
    supersede edilen her ID'nin parent kabul kanıtı var.

Final sayılar sabit metin olarak kopyalanmayacak; güncel envanterden türetilecek. Başlangıç raporundaki
17 proje/945 unit sayıları eksiktir: repo 21 `.csproj` içerir ve CI yedi unit projesini kapsar.

## 7. Başlama kriteri

İlk geliştirme paketi `P0-REL`dir. `P0-REL` ve `P0-DBM` yeşil olmadan yeni migration veya geniş
production deploy değişikliğine başlanmayacak. Her dalga sonunda branch/main ancestry ve çalışma
ağacı yeniden raporlanacak.
