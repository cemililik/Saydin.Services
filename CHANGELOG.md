# Değişiklik Geçmişi — Saydin.Services

Bu dosya backend servislerindeki önemli değişiklikleri izler. Format
[Keep a Changelog](https://keepachangelog.com/tr/1.1.0/) esinlidir; sürümler
[Semantic Versioning](https://semver.org/lang/tr/) ile etiketlenir.

## [Unreleased]

### Faz 4 — Code Review Aksiyon Planı: ADR / Karar Bekleyenler (2026-05-29)

Code-review aksiyon planının (`docs/code-reviews/ACTION-PLAN.md`) **Faz 4** kalemleri:
bilinçli ertelenen, ürün/legal/mimari **karar** gerektiren 14 bulgu karara bağlandı; ADR'lar
yazıldı ve sonuçsal kod/doküman değişiklikleri uygulandı.

#### Eklendi
- **Mimari Karar Kayıtları (ADR):** `ADR-002-compare-quota`, `ADR-003-rate-limiting`,
  `ADR-004-geoip-distribution`, `ADR-005-secrets-management`,
  `ADR-006-activity-log-financial-policy` + backend ADR konvansiyonu için
  `docs/decisions/README.md` (F4-2/5/7/13/6/10).
- **IP-bazlı rate limiting** (F4-5, ADR-003): ASP.NET Core `RateLimiter` middleware,
  **config-gated ve varsayılan kapalı** (`RateLimiting:Enabled=false`). IP-bazlı sabit
  pencere; reddetme 429 + lokalize RFC 7807 ProblemDetails + `Retry-After`. Yeni resx
  anahtarları: `RateLimited`, `RateLimitedDetail`.
- **Migration izleme tablosu** (F4-1/F4-8, ADR-001 revize): `014_schema_migrations.sql`
  (additive, idempotent) + var olan DB'ler için `infrastructure/postgres/apply-migrations.sh`
  deploy runner'ı.
- **`AmountBucket` helper** (F4-6, ADR-006): finansal tutarları kaba aralığa indirger
  (`Saydin.Shared/Constants/AmountBucket.cs`) + birim kontrat testi.
- **GeoIP edinme dokümantasyonu** (F4-7, ADR-004): `infrastructure/geoip/README.md` +
  `.env.example` `GEOIP_ACCOUNT_ID`/`GEOIP_LICENSE_KEY`.

#### Değişti
- **KVKK veri minimizasyonu** (F4-6): Activity log'larda ham finansal tutar yerine kaba
  aralık (`AmountBucket.Coarse`) yazılır; mutlak sonuç TL tutarları (ProfitLossTry,
  CurrentValueTry, RequiredInvestmentTry, TotalInvestedTry, AverageCostPerUnit) artık
  loglanmaz — yalnız yüzde alanları tutulur. WhatIf (calculate/compare/reverse) + DCA
  endpoint'leri güncellendi. (Yayınlanmış gizlilik politikasıyla uyum.)
- **Reverse What-If `try` yuvarlaması** (F4-3): `TargetValueTry` ham hedeften değil birim
  granülasyonundan **ileri-türetilir** (`Round(UnitsAcquired × SellPrice, 2)`) → UI'da
  `UnitsAcquired × SellPrice == TargetValueTry` birebir uyuşur.
- **Ingestion worker'ları varsayılan kapalı** (F4-11): `appsettings.json` baseline
  `Enabled=false`, `IngestionOrchestrator` fallback `?? false` (fail-closed). Aktivasyon
  `WORKER_*_ENABLED` env ile.
- **ADR-001 migration stratejisi revize edildi:** "Seçenek B (EF Core)" yerine "Seçenek C
  (numaralı SQL + izleme tablosu, hybrid)" — EF Core'a tam geçiş post-MVP'ye ertelendi.
- **CLAUDE.md:** mock politikası netleştirildi (F4-9 — service unit testlerde NSubstitute
  serbest, DB/integration testlerde mock yasak) + ADR organizasyon konvansiyonu (F4-10).
- Dokümantasyon: `architecture.md` (rate limiting, finansal yuvarlama, DCA anchor-day,
  exception zinciri 403 konvansiyonu), `cache-strategy.md` (compare kotası),
  `development-guide.md` (rate limit / GeoIP / worker / secrets / CodeRabbit notları).

#### Doğrulandı (kod değişikliği gerekmedi)
- **Compare kotası = 1 hesaplama** (F4-2, ADR-002): mevcut davranış ratify edildi; per-feature
  alt-kotalar post-MVP.
- **Feature-disabled = 403 Forbidden** (F4-14): `FeatureDisabledExceptionHandler` zaten 403
  dönüyordu; yalnız stale doc (`architecture.md`'deki eski `InvalidOperationException` atfı)
  düzeltildi.
- **DCA anchor-day** (F4-4): index-based `AddMonths` + CLAMP zaten Faz 1'de uygulanmıştı;
  `architecture.md`'ye politika notu eklendi.

#### Kaldırıldı
- `.dockerignore`'dan ölü `.sourcery.yaml` referansı (F4-12; dosya zaten Faz 1'de silinmişti).

#### İnsan onayı bekleyen (kod hazır)
- Precautionary secret rotation (F4-13, **PENDING**), KVKK bucket sınırları + geçmiş satır
  purge kararı (F4-6 legal), compare/reverse public roadmap kota uyumu (F4-2 ürün), reverse
  `targetValueTry` display semantiği (F4-3 UX), GeoIP lisans/ops sahipliği (F4-7), meta repo
  ADR README (F4-10).

#### Doğrulama
- Build: 0 uyarı / 0 hata (SDK 10.0). Testler: 346 geçti (Api 257 · PriceIngestion 86 ·
  Integration 3, 0 skipped). Migration zinciri 001→014 fresh-init'te abort'suz; `schema_migrations`
  16 satır.
