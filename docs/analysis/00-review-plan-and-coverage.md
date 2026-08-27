# Saydin.Services — Sistematik Review Planı ve Kapsam Matrisi

## Review kimliği

- **Tarih:** 2026-08-18
- **Branch / commit:** `main` / `9067dd2`
- **Durum:** Tamamlandı; birleşik karar ve aksiyon sırası [`README.md`](README.md) içinde
- **Başlangıç durumu:** Review raporları dışında çalışma ağacı temiz
- **Kapsam birimi:** `git ls-files` ile bulunan **233 tracked dosyanın tamamı**
- **Yaklaşık büyüklük:** 22,5 bin fiziksel satır
- **Amaç:** Yalnız hata aramak değil; doğruluk, güvenlik, finansal veri kalitesi, ürün deneyimi,
  geliştirici deneyimi ve işletilebilirlik bakımından birinci sınıf seviyeye giden farkları bulmak

## Review ilkeleri

1. Her tracked dosya en az bir uzman hattına atanır; yüksek riskli sınırlar birden fazla hat tarafından
   çapraz okunur.
2. Bir bulgu; kanıt, tetiklenme senaryosu, etki, önem derecesi ve uygulanabilir öneri içermeden kabul edilmez.
3. Yalnız teorik olasılıklar kesin bulgu gibi yazılmaz. Varsayım veya ortam bağımlılığı açıkça belirtilir.
4. Build/test sonucunun yeşil olması davranışsal doğruluğun kanıtı sayılmaz; kritik akışlar ayrıca manuel
   veri akışı ve sözleşme incelemesinden geçirilir.
5. Mevcut güçlü kararlar da kayda geçirilir; review çıktısı sadece negatif bulgulardan oluşmaz.

## Önem derecesi

| Seviye | Ölçüt |
|---|---|
| **Critical** | Yaygın veri kaybı/bozulması, doğrudan secret veya hassas veri ifşası, kolay uzaktan istismar ya da temel servisin güvenilir biçimde çalışamaması |
| **High** | Gerçekçi üretim koşulunda yanlış finansal sonuç, önemli güvenlik/izolasyon açığı, sürekli veri güncelliği kaybı veya CI'ın kritik regresyonu yakalayamaması |
| **Medium** | Sınırlı koşullarda işlev/operasyon bozulması, önemli gözlemlenebilirlik-test-bakım açığı veya kullanıcı deneyiminde belirgin kalite kaybı |
| **Low** | Düşük etkili tutarsızlık, dokümantasyon/ergonomi/temizlik sorunu ya da savunma derinliği iyileştirmesi |

## Dosya kapsamı ve review sahipliği

| Alan | Dosya | Yaklaşık satır | Birincil review hattı | Çapraz kontrol |
|---|---:|---:|---|---|
| Kök build/config/repo dosyaları | 14 | — | Platform, dokümantasyon ve kalite | Ana agent |
| `.claude/` | 3 | 356 | Platform, dokümantasyon ve kalite | Ana agent mimari taraması |
| `.github/` | 3 | 203 | Platform, CI/CD ve supply chain | Ana agent test doğrulaması |
| `docs/` (review öncesi) | 15 | 3.840 | Platform ve dokümantasyon | Kod/veri agentları tutarlılık kontrolü |
| `infrastructure/` | 22 | 1.282 | Veri/migration + platform/operasyon | Ana agent Compose ve fresh-init doğrulaması |
| `src/Saydin.Api/` | 70 | 5.217 | API/domain | Ana agent uçtan uca sözleşme ve güvenlik |
| `src/Saydin.Shared/` | 35 | 994 | API/domain + ingestion/veri | Ana agent şema-model tutarlılığı |
| `src/Saydin.PriceIngestion/` | 32 | 2.696 | Ingestion/veri | Ana agent veri güncelliği ve operasyon |
| `tests/Saydin.Api.Tests/` | 21 | 4.365 | API/domain | Platform agentı test stratejisi |
| `tests/Saydin.Api.IntegrationTests/` | 6 | 565 | API/domain | Platform agentı CI çalıştırılabilirliği |
| `tests/Saydin.PriceIngestion.Tests/` | 12 | 1.508 | Ingestion/veri | Platform agentı test stratejisi |
| **Toplam** | **233** | **~22.500** |  |  |

`bin/`, `obj/`, `TestResults/` ve Docker/NuGet cache'leri tracked kaynak kapsamına dahil değildir;
üretilen çıktı oldukları için kaynak review konusu değildir. Harici Saydın meta reposu, canlı üretim
telemetrisi ve üçüncü taraf servislerin gerçek cevapları bu repository review'inin erişim sınırı dışındadır;
bunlardan doğan doğrulama gereksinimleri residual risk olarak raporlanır.

## Uzman review hatları

### 1. API ve domain hattı

Minimal API sözleşmeleri, doğrulama, hata semantiği, katman sınırları, Redis/EF erişimi, quota,
concurrency, privacy, lokalizasyon, performans ve ilgili unit/integration testleri.

Çıktı: [`01-api-domain-review.md`](01-api-domain-review.md)

### 2. Ingestion, veri ve migration hattı

Dış kaynak adaptörleri, mapper doğruluğu, finansal hassasiyet, retry/timeout/circuit breaker,
idempotency, zamanlama, data freshness, UPSERT/transaction, şema-model drift ve migration güvenliği.

Çıktı: [`02-ingestion-data-review.md`](02-ingestion-data-review.md)

### 3. Platform, dokümantasyon ve kalite hattı

CI/CD, dependency ve container supply chain, secrets/config, deployment hardening, observability,
backup/DR, geliştirici deneyimi, ADR/doküman-kod tutarlılığı ve test kalite kapıları.

Çıktı: [`03-platform-docs-quality-review.md`](03-platform-docs-quality-review.md)

### 4. Çapraz doğrulama hattı

Mekanik mimari taramalar, Docker build, izole gerçek PostgreSQL/Redis testleri, migration fresh-init,
NuGet vulnerability audit, format/yapılandırma/link doğrulaması ve agent bulgularının yeniden üretimi.

Çıktı: [`04-validation-and-cross-cutting-review.md`](04-validation-and-cross-cutting-review.md)

## Mekanik doğrulama kapıları

| Kapı | Beklenen kanıt |
|---|---|
| Mimari yasaklar | Servisler arası yasak referans, Controller, raw SQL interpolation, sync-over-async, production `new HttpClient`, finansal `float/double` taraması |
| Derleme | Her iki production Dockerfile'ın temiz Release publish/build sonucu |
| Unit + integration | Solution testleri; integration testleri için review'e özel izole Compose projesi ve gerçek PostgreSQL/Redis |
| Migration | Boş review volume'unda `001` → son migration zincirinin `ON_ERROR_STOP` ile tamamlanması ve tracking tablosunun doğrulanması |
| Dependency güvenliği | Direct + transitive NuGet vulnerability audit; prerelease ve lock/reproducibility incelemesi |
| Kaynak formatı | `dotnet format --verify-no-changes`, warnings-as-errors build ve temel JSON/YAML/shell syntax doğrulaması |
| Dokümantasyon | Yerel Markdown linkleri, code fence dengesi, komutların gerçek Compose/CLI sözleşmesiyle tutarlılığı |
| Kapsam | Her top-level dosya grubunun bir review hattına atanmış olması ve çapraz risklerin ana özette birleştirilmesi |

## Tamamlanma ölçütü

Review; dört rapor tamamlandığında, tüm bulgular çapraz doğrulandığında, duplicate/false-positive kayıtlar
temizlendiğinde, mekanik kapı sonuçları yazıldığında ve ana `README.md` önceliklendirilmiş birleşik özeti
gösterdiğinde tamamlanmış sayılır.

Bu ölçütlerin tamamı 2026-08-18 tarihinde karşılandı. Alan raporlarındaki tekrarlar silinmedi; bağımsız
kanıt izini korumak için yerinde bırakıldı ve ana raporda tek aksiyon başlıkları altında konsolide edildi.
