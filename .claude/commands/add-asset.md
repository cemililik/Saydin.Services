# /add-asset — Yeni Asset Ekleme Rehberi

Yeni bir finansal asset eklerken bu 9 adımlı checklist'in **tamamını** uygula.

## Kullanım

```
/add-asset THYAO "Türk Hava Yolları" stock twelvedata "THYAO:BIST"
```

---

## Checklist

Hiçbir adımı atlama. Tümü tamamlanmadan görevi "bitti" sayma.

### Adım 1: Migration Dosyası

`infrastructure/postgres/migrations/` altında en son numaralandırmayı devam ettiren yeni bir migration dosyası oluştur.

```sql
-- 00N_add_asset_<symbol>.sql
INSERT INTO assets (symbol, display_name, category, is_active, source, source_id)
VALUES ('<SYMBOL>', '<Görünen Ad>', '<category>', true, '<source>', '<source_id>');
```

Migration'ı mevcut dosyaya ekleme — yeni dosya oluştur.

> ⚠️ F2.9-9 / [C-I-30]: `assets` tablosu şemasını ekleme öncesi doğrula
> (`source_id` kolonu zorunlu mu? `category` enum değerleri neler?).
> ```bash
> docker compose exec postgres psql -U saydin -d saydin -c "\d assets"
> ```

### Adım 2: Adapter Kontrolü

Belirtilen kaynak (`source`) için adapter mevcut mu kontrol et:
- `src/Saydin.PriceIngestion/Adapters/I{Source}Adapter.cs`
- `src/Saydin.PriceIngestion/Adapters/{Source}Adapter.cs`

Mevcut değilse yeni adapter oluştur. Interface:
```csharp
public interface I{Source}Adapter
{
    Task<IReadOnlyList<PricePoint>> FetchRangeAsync(
        string assetSymbol,
        DateOnly from,
        DateOnly to,
        CancellationToken ct);
}
```

Polly retry + circuit breaker + timeout zorunludur.

### Adım 3: Worker Kaydı

`src/Saydin.PriceIngestion/Workers/IngestionOrchestrator.cs` dosyasına yeni asset'i ekle:
- Hangi adapter kullanılacak
- Zamanlanmış çalışma saati (market kapanış sonrası)
- `market_holidays` tablosu için tatil günleri

### Adım 4: Adapter Unit Testi

`tests/Saydin.PriceIngestion.Tests/` altında yeni asset için deserializasyon testi:

```csharp
[Fact]
public async Task FetchRangeAsync_ValidSymbol_ReturnsPricePoints()
{
    // Arrange — gerçek API yanıtı fixture'ı
    // Act
    // Assert — close fiyatı decimal, date dolu vb.
}
```

Gerçek API çağrısı yapan entegrasyon testi ayrı klasörde.

### Adım 5: API Otomatik Kontrolü

`GET /v1/assets` endpoint'ini çağırarak yeni asset'in listede göründüğünü doğrula.

### Adım 6: Türkçe Görünen Ad Doğrulaması

Display name Türkçe kullanıcıya uygun mu?
- ✓ "Türk Hava Yolları" değil "THYAO"
- ✓ "Altın (Gram/TL)" değil "XAU"
- ✓ "Bitcoin" (evrensel) ✓

### Adım 7: Manuel Entegrasyon Testi

```bash
# Ingestion'ı manuel tetikle (development endpoint veya admin komutu)
# Ardından veritabanını kontrol et:
docker exec saydin-postgres psql -U saydin -d saydin \
  -c "SELECT price_date, close FROM price_points p JOIN assets a ON a.id = p.asset_id WHERE a.symbol = '<SYMBOL>' ORDER BY price_date DESC LIMIT 5;"
```

Veri akıyorsa ✅, akmıyorsa hata loglarını incele.

### Adım 8: Lokalize Asset Display Name (F1.8-7 / [C-I-29] / [G-I-06])

`src/Saydin.Api/Resources/ErrorMessages.resx` ve `ErrorMessages.en.resx` dosyalarına
asset'in Türkçe ve İngilizce display name'lerini ekle. Key formatı:
`Asset_{SYMBOL}` — `IAssetNameLocalizer` runtime'da bu key üzerinden çözer.

```xml
<!-- ErrorMessages.resx (tr-TR) -->
<data name="Asset_THYAO"><value>Türk Hava Yolları</value></data>

<!-- ErrorMessages.en.resx -->
<data name="Asset_THYAO"><value>Turkish Airlines</value></data>
```

Anahtar yoksa `AssetNameLocalizer.Localize` DB'deki `display_name` kolonuna düşer
(fallback). Türkçe nüansı (örn. "Lira" değil "TL") DB değil resx'e yazılmalı.

### Adım 9: Dokümantasyon Güncelleme

`docs/architecture.md` dosyasındaki "Desteklenen Asset'ler" tablosuna yeni satır ekle:

> F1.8-6 / [C-I-28] / [G-I-05]: Önceki sürümde `docs/architecture/overview.md`
> referansı vardı — bu dosya backend repo'da yok. Doğru hedef: kök `docs/architecture.md`.

```markdown
| `<SYMBOL>` | <Görünen Ad> | <category> | <source> |
```

---

## Tamamlanma Kriteri

Tüm 9 adım ✅ ise asset başarıyla eklendi.

Herhangi bir adım eksikse görevi "bitti" sayma — kullanıcıya hangi adımın eksik olduğunu bildir.
