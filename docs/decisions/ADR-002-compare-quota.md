# ADR-002 — Karşılaştırma (Compare) Endpoint Kota Semantiği

- **Durum:** Kabul edildi (MVP) — ürün, per-feature alt-kotaları post-MVP'ye onayladı
- **Tarih:** 2026-05-29
- **Karar verenler:** Backend ekibi (ürün onayı: aşağıdaki "Sonuçlar / İleride")
- **İlgili bulgular (code review):** Claude `[C-B-WhatIf-5]`; Faz 1 `F1.3-1` (Açık Soru 1); Faz 4 `F4-2`

---

## Bağlam

`POST /v1/what-if/compare` 2–5 sembolü tek istekte karşılaştırır ve her sembol için
bir `WhatIf` çekirdek hesabı çalıştırır (`WhatIfCalculator.CompareAsync` →
`CalculateCoreAsync` döngüsü). Günlük kullanım kotası `IDailyLimitGuard` tarafından
Redis'te atomik bir Lua script ile tutulur (free tier: `DailyCalculationLimit = 20`,
key prefix `usage:whatif:`).

Açık soru: **bir Compare çağrısı kotadan kaç düşmeli?** Seçenekler:
- Compare = **1 hesaplama** (tek `TryAcquireAsync`), veya
- Compare = **N hesaplama** (sembol sayısı kadar), veya
- Compare'ın **ayrı bir alt-kotası** (`usage:compare:` + `DailyComparisonLimit`).

Bu, `roadmap.md`'nin ürün tablosuyla da ilişkilidir: roadmap "Karşılaştırma = Günde 5"
şeklinde **ayrı** bir kota ilan eder; mevcut kod ise Compare'i tek What-If havuzundan
1 işlem sayar.

---

## Değerlendirilen Seçenekler

### Seçenek A — Compare = 1 hesaplama (paylaşılan havuz) — MEVCUT / SEÇİLEN
- Tek `TryAcquireAsync(usage:whatif:…)` çağrısı; sembol sayısından bağımsız.
- **Artı:** Cömert free tier (roadmap monetizasyon felsefesi: "sınır miktar üzerinden,
  özellik üzerinden değil"). Basit, atomik, ek key yok. Mevcut davranış — kod değişmez.
- **Eksi:** roadmap'in "5/gün" rakamından sapar (kullanıcı lehine).

### Seçenek B — Compare = N hesaplama
- `symbols.Count` kez increment.
- **Artı:** "Maliyet adil" (her sembol bir hesap). **Eksi:** UX cezalandırıcı (tek
  karşılaştırma 5 kotayı yer); cömert-free felsefesine aykırı.

### Seçenek C — Ayrı alt-kota (`usage:compare:` + `DailyComparisonLimit`)
- Compare/reverse/dca için ayrı sayaçlar (roadmap: compare=5, reverse=3, dca=3).
- **Artı:** roadmap rakamlarını birebir uygular. **Eksi:** `PlanOptions`'a yeni alanlar,
  yeni Lua key'leri, daha fazla test; MVP için aşırı mühendislik.

---

## Karar

**Seçenek A** — `POST /v1/what-if/compare` günlük kotadan **1 hesaplama** düşer
(sembol sayısı 2–5 fark etmez), paylaşılan `usage:whatif:` havuzundan. Tek `What-If`,
Reverse What-If ve Compare bu sayacı **paylaşır**; DCA ayrı `usage:dca:` sayacı kullanır.

Uygulayan kod: `WhatIfCalculator.cs` (tek `TryAcquireAsync`, CONC-001 yorumu) ve
`DailyLimitGuard.cs`. **Kod değişikliği yok** — bu ADR mevcut davranışı kararlaştırır.

### Gerekçe
- roadmap'in kendi monetizasyon ilkesi (`roadmap.md:296-319`): "Free tier cömert olmalı;
  sınır **miktar** üzerinden konur — özellik üzerinden değil." Compare'i 5 kotaya
  mal etmek bu ilkeyle çelişir.
- Atomiklik ve basitlik: tek key, tek Lua çağrısı, yarış yok.
- CLAUDE.md finansal/altyapı kuralları etkilenmez.

---

## Sonuçlar / İleride

- **roadmap.md uyumsuzluğu (ürün kararı):** Public roadmap "Karşılaştırma = Günde 5",
  "Ters senaryo = Günde 3", "DCA = Günde 3" ilan eder; MVP kodu bunları **ayrı
  alt-kota olarak uygulamaz** (Compare/Reverse paylaşılan 20'lik havuzdan 1'er, DCA ayrı
  havuzda limit 20). Per-feature alt-kotalar (Seçenek C) **post-MVP hedefi** olarak
  ertelenmiştir. `roadmap.md`'ye bu sapmayı belirten bir dipnot eklendi.
- Per-feature kota gerektiğinde Seçenek C uygulanır: `PlanOptions`'a
  `DailyComparisonLimit`/`DailyDcaLimit` alanları + `usage:compare:`/`usage:reverse:`
  prefix'leri + Lua script reuse.

---

## İlgili Dökümanlar

- [`docs/cache-strategy.md`](../cache-strategy.md) — `usage:*` sayaç key'leri
- [`docs/architecture.md`](../architecture.md) — Rate limiting / kota bölümü
- `Saydın` meta repo `docs/roadmap.md` — Free vs Premium tablosu (dipnot)
- Faz aksiyon planı: `docs/code-reviews/ACTION-PLAN.md` (F4-2)
