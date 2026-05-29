# ADR-005 — Secrets Management Stratejisi

- **Durum:** Kabul edildi — mekanik kontroller mevcut; **secret rotation insan onayı bekliyor**
- **Tarih:** 2026-05-29
- **Karar verenler:** Backend ekibi (prod store + rotation onayı: aşağıdaki "İnsan onayı")
- **İlgili bulgular (code review):** GPT-5 `[G-H-04]`, `[G-H-03]`; Claude `[C-H-ENV-2]`, `[C-H-DC-2]`, `[C-H-APPSET-PI-1]`; Faz 1 `F1.7-1`, `F1.7-2`, `F1.7-5`; Faz 4 `F4-13`

> **Not (cross-repo numara):** Meta repo'da `ADR-005-backend-monorepo.md` ayrı bir ADR'dır.
> Bu dosya backend reposunun **bağımsız** ADR uzayındadır (bkz.
> [`docs/decisions/README.md`](README.md)); aynı numara, farklı kapsam — çakışma kasıtlıdır.

---

## Bağlam

Saydın device-id bazlı (kullanıcı PII auth'u yok) tek-backend monorepo'dur. Sır yüzeyi:
birkaç **free-tier dış API key'i** (CoinGecko, OpenExchangeRates AppId, TwelveData, EVDS,
MaxMind license) + **DB/Redis/pgAdmin parolaları**. Faz 0/1'de mekanik sertleştirme zaten
yapıldı; geriye **ADR dokümanı** + bir **insan rotasyon aksiyonu** kaldı.

**Doğrulanmış mevcut durum (kod değişikliği gerekmez):**
- **Dev:** Her iki proje `UserSecretsId` taşır (`Saydin.Api.csproj`,
  `Saydin.PriceIngestion.csproj`); `development-guide.md` `dotnet user-secrets` akışını
  belgeler.
- **`.env` hijyeni:** `.gitignore` (`.env`, `.env.*`, `!.env.example`) — `.env` izlenmiyor,
  git geçmişinde yok. `.dockerignore` `.env*`'i image'dan dışlar; yalnız `.env.example`
  (placeholder değerler) tracked.
- **Compose fail-fast (F1.7-1):** `POSTGRES_PASSWORD` / `PGADMIN_PASSWORD` /
  `REDIS_PASSWORD` her kullanımda `${VAR:?...}` ile zorunlu. API key'ler yalnız env ile
  enjekte (`ExternalApis__*`), hardcode yok.
- **appsettings hijyeni (F1.7-5):** PriceIngestion `appsettings.json`'da `ExternalApis`
  bloğu yok; API key'ler tamamen env'den. Hiçbir appsettings'te gerçek sır yok.
- **CI:** workflow'lar hiçbir secret referans etmiyor (build+test secret gerektirmiyor).

---

## Değerlendirilen Seçenekler (production store)

### Seçenek A — Host env / gitignored prod `.env` — MVP SEÇİMİ

- Tek-host Docker Compose deploy; sırlar host'taki `chmod 600` gitignored `.env`'den
  interpolation ile gelir (image'a girmez — `.dockerignore` garanti). Compose `${VAR:?...}`
  fail-fast guard'ları zaten var.
- **Artı:** Sıfır ek altyapı, mevcut modelle birebir. **Eksi:** Çok-host/çok-operatör
  senaryosunda elle yönetim zorlaşır.

### Seçenek B — Docker Compose `secrets:` (file-mount) — DOKÜMANTE EDİLMİŞ SONRAKİ ADIM

- Sırlar dosya olarak mount edilir (`*_FILE` konvansiyonu). Deploy topolojisi
  netleşince geçilir.

### Seçenek C — Vault / cloud KMS (AWS/Azure/GCP) — ERTELENDİ (post-MVP)

- **Artı:** Merkezi, otomatik rotation, audit. **Eksi:** Tek-host MVP ayak izi için
  operasyonel olarak ağır — aşırı mühendislik.

---

## Karar

**Katmanlı model** (mevcut durumu ratify eder):

| Katman | Sır kaynağı |
|---|---|
| **Dev** | `dotnet user-secrets` (native run) + gitignored `.env` (compose). `.env.example` template. Kural: hiçbir sır appsettings.json'a veya git'e girmez. |
| **CI** | GitHub Actions encrypted secrets — yalnız gerçekten gerektiğinde (registry push / dış API integration testi). Şu an gerekmiyor; öyle kalsın. Sır loglara echo edilmez. |
| **Production (MVP)** | **Seçenek A** — host'ta `chmod 600` gitignored `.env` + compose `${VAR:?...}` fail-fast. **Seçenek B** (compose `secrets:`) dokümante edilmiş sonraki adım; **Seçenek C** (Vault/KMS) "graduation" tetikleyicisine ertelendi. |

**Graduation tetikleyicisi (Seçenek C'ye geç):** çok-host deploy VEYA birden çok operatör
VEYA uyumluluk gereksinimi.

**Rotation runbook (insan aksiyonu):**
- Her key'in verildiği yer: CoinGecko, OpenExchangeRates (AppId), TwelveData, EVDS,
  MaxMind (license) hesapları + DB/Redis/pgAdmin parolaları.
- **Kadans:** şüpheli sızıntıda, operatör ayrılışında ve en az **6–12 ayda bir**.
- **F1.7-2 precautionary rotation:** Gerçek değerler lokal `.env`'de bulunduğundan
  bir kerelik önlem rotasyonu **yapılmalıdır**. **Durum: PENDING — güvenlik/ops onayı
  ve uygulaması gerekiyor** (bu ADR koddan yapamaz).

---

## Sonuçlar / Risk

**Olumlu:** Mevcut kontrolleri tek yerde belgeler; düşük-efor MVP prod hedefi + net
graduation yolu. Kod/compose değişikliği gerekmez (her şey zaten yerinde).

**Risk:** (1) Precautionary rotation yapılmazsa eski key'ler geçerli kalır → insan
aksiyonu olarak işaretlendi. (2) Cross-repo ADR-005 numara tekrarı → header'da ayrıştırıldı.

---

## İlgili Dökümanlar

- [`docs/development-guide.md`](../development-guide.md) — user-secrets / env akışı
- `docker-compose.yml` — `${VAR:?...}` fail-fast guard'ları
- `.env.example` — placeholder template
- CLAUDE.md — Güvenlik bölümü
- Faz aksiyon planı: `docs/code-reviews/ACTION-PLAN.md` (F4-13, F1.7-2)
