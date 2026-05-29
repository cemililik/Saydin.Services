# ADR-006 — Activity Log Finansal Tutar Politikası (KVKK)

- **Durum:** Önerilen — **legal/ürün onayı bekliyor** (kod uygulandı; politika canlı gizlilik metniyle uyumludur)
- **Tarih:** 2026-05-29
- **Karar verenler:** Backend ekibi (legal sign-off: aşağıdaki "İnsan onayı")
- **İlgili bulgular (code review):** Claude `[C-A-14]`; Faz 2 `F2.1-5`; Faz 4 `F4-6` (Açık Soru 6)

---

## Bağlam

`activity_logs.data` (JSONB) ürün analitiği için her işlemin parametrelerini saklar.
Faz 4 öncesi endpoint'ler **ham finansal tutarları** yazıyordu:
- Girdi: `request.Amount` / `TargetAmount` / `PeriodicAmount` (ham `decimal`).
- Sonuç: `ProfitLossTry`, `RequiredInvestmentTry`, `TotalInvestedTry`, `CurrentValueTry`,
  `AverageCostPerUnit` (mutlak TL figürleri).

`activity_logs` ayrıca cihaz IP'sini (maskeli /24-/48), device-id'yi ve coğrafi konumu
taşır. "Tam tutar + asset + tarih + cihaz/IP" kombinasyonu kullanıcı **profillemesine**
ve KVKK **veri minimizasyonu** ilkesinin ihlaline açıktır. Dahası, yayınlanmış gizlilik
politikası gerçek/tam finansal bilginin **toplanmadığını** belirtir — yani mevcut kod
bu metinle **çelişiyordu** (bu bir uyumluluk düzeltmesidir, yeni bir kısıt değil).

---

## Değerlendirilen Seçenekler

- **(a) Tutarı tamamen çıkar:** Analytics değeri (büyüklük dağılımı) kaybolur.
- **(b) Kaba aralığa bucket'la — SEÇİLEN:** Ham tutar yerine `"10k-100k"` gibi etiket.
  Profilleme engellenir, analytics değeri korunur.
- **(c) Yuvarla:** Hâlâ yakın-tam figür; minimizasyon zayıf.
- **(d) Hash'le — REDDEDİLDİ:** Düşük entropili (birkaç bin makul yuvarlak değer) bir
  tutarın hash'i brute-force ile geri çevrilebilir → yanlış güven; gerçek bir KVKK
  önlemi değildir.

---

## Karar

1. **Girdi tutarları bucket'lanır.** Ham `Amount`/`TargetAmount`/`PeriodicAmount` yerine
   `AmountBucket.Coarse(decimal)` kaba aralık etiketi yazılır:
   `"0" · "0-1k" · "1k-10k" · "10k-100k" · "100k-1M" · "1M+"`. Yanına `amountType`
   loglanır (units/grams için etiket o birim cinsinden yorumlanır).
2. **Sonuç TL tutarları LOGLANMAZ.** `ProfitLossTry`, `RequiredInvestmentTry`,
   `TotalInvestedTry`, `CurrentValueTry`, `AverageCostPerUnit` activity_logs'tan çıkarıldı.
   Yalnız **yüzde** alanları (`ProfitLossPercent`, `RealProfitLossPercent`, `IsProfit`)
   ve `TotalPurchases` tutulur — bunlar mutlak para figürü içermez, analytics için yeterli.
3. **Hash kullanılmaz** (yukarıdaki gerekçe).
4. **Tek source-of-truth:** Bucket sınırları `Saydin.Shared/Constants/AmountBucket.cs`'de;
   bu ADR ve gizlilik politikası buraya atıfta bulunur. Birim testi sınırları kilitler
   (`AmountBucketTests`).

Uygulayan kod: `WhatIfEndpoints.cs` (calculate/compare/reverse), `DcaEndpoints.cs`.
`ScenariosEndpoints` zaten tutar loglamıyor (yalnız id/type/symbol/label) — değişmedi.

### Gerekçe
- KVKK veri minimizasyonu + yayınlanmış gizlilik metniyle uyum (kod artık gerçeği metne
  uyduruyor).
- IP zaten maskeli (`IpMasker` /24-/48); bucket, finansal eksen için aynı minimizasyonu
  uygular.
- Analytics (popülerlik, kar/zarar yüzdesi dağılımı) korunur; hiçbir materialized view
  ham `data->amount` alanına bağlı değil (doğrulandı).
- CLAUDE.md finansal kuralı: bucket karşılaştırması yalnız `decimal` ile yapılır.

---

## Sonuçlar / Risk

**Olumlu:** Kod yayınlanmış gizlilik politikasıyla uyumlu hale gelir; ileri analitik için
yeterli sinyal korunur; tek source-of-truth + kontrat testi.

**Risk / açık uçlar (insan):**
- **Geçmiş satırlar:** 014 öncesi `activity_logs` satırları ham tutar içerebilir.
  Karar gerekli: anonimleştir/purge VEYA 1 yıllık saklama (retention) maddesine güven.
- **Bucket sınırları + sonuç-TL bırakma kararı** legal/ürün onayıyla kesinleşmeli.
- Onaya kadar **Durum = Önerilen**; onay sonrası "Kabul edildi"ye çekilir.

---

## İlgili Dökümanlar

- `Saydin.Shared/Constants/AmountBucket.cs` — bucket sınırları (source-of-truth)
- `Saydın` meta repo `docs/architecture/activity-logging.md` — data payload şeması
- `Saydın` meta repo `docs/privacy-policy.html` — gizlilik metni (uyum)
- Faz aksiyon planı: `docs/code-reviews/ACTION-PLAN.md` (F4-6)
