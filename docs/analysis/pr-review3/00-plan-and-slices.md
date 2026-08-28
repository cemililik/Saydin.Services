# PR Review 3 — PR #16 Retro-Review Planı ve Dilimler

> **Tarih:** 2026-08-28 · **Kapsam:** merge edilmiş PR #16 (`d7bbc36`) değişikliğinin
> geriye dönük satır-seviyesi incelemesi.

## 1. Neden

PR #16 merge edildiğinde **913 dosyaydı** ve hiçbir otomatik satır-seviyesi inceleme
çalışmadı:

| Araç | Sonuç |
|------|-------|
| CodeRabbit | `Review skipped — 908 files, 808 over the limit of 100` |
| Sourcery | `unable to review` (ayrıca 150.000 diff karakteri limiti) |
| Qodo | `billing-blocked` |
| **Inline review yorumu** | **0** |

2 Critical + 18 High bulgunun kapatıldığı; güvenlik/admission, migration trust, role
lifecycle ve backup/PITR sözleşmelerini değiştiren bir PR, yalnız statik analiz
(SonarCloud, CodeQL) ve testlerle main'e girdi. Statik analizin gördüğü ile bir
review'ın gördüğü aynı şey değildir: CI kapılarını açarken ortaya çıkan üç gerçek
defect ([pr-review2/09](../pr-review2/09-ci-gate-remediation.md)) yalnız testler
çalışabildiği için görünür olmuştu.

## 2. Mekanizma

- `review/base-pr16` dalı PR #16'nın tabanında (`9067dd2`) donduruldu.
- Sekiz dilim dalı bu tabandan çıkıp yalnız kendi yollarını `71e003d`'den aldı.
- PR'lar **`review/base-pr16`'ya** açıldı (main'e değil) ve **merge edilmez**.

Doğrulanmış iki özellik:

- **CI yanmaz.** `ci.yml` yalnız `main`/`development` için tetiklenir; review PR'larında
  `gh pr checks` yalnız harici bot'ları gösterir.
- **Dilimler tek başına derlenmek zorunda değil.** CodeRabbit diff üzerinde statik
  inceleme yapar; derlenmiş ve testten geçmiş bütün hâli main'dedir.

## 3. Bölümleme ve kanıtı

İncelenecek yüzey: 913 dosyadan `tools/calendar-data/data/**` (278, SHA-256 pinli kanıt
artefaktı) ve `docs/**` (76) çıkarıldıktan sonra **559 dosya**.

| # | PR | Dal | Dosya |
|---|----|-----|-------|
| 1 | [#17](https://github.com/cemililik/Saydin.Services/pull/17) | `review/s1-api-security` | 57 |
| 2 | [#18](https://github.com/cemililik/Saydin.Services/pull/18) | `review/s2-db-control-plane` | 74 |
| 3 | [#19](https://github.com/cemililik/Saydin.Services/pull/19) | `review/s3-shared-and-activity-log` | 50 |
| 4 | [#20](https://github.com/cemililik/Saydin.Services/pull/20) | `review/s4-price-ingestion` | 80 |
| 5 | [#21](https://github.com/cemililik/Saydin.Services/pull/21) | `review/s5-audit-and-repair` | 69 |
| 6 | [#22](https://github.com/cemililik/Saydin.Services/pull/22) | `review/s6-api-services` | 73 |
| 7 | [#23](https://github.com/cemililik/Saydin.Services/pull/23) | `review/s7-runtime-ops` | 97 |
| 8 | [#24](https://github.com/cemililik/Saydin.Services/pull/24) | `review/s8-ci-and-release` | 59 |

**Bölümleme kanıtı** — hiçbir dosyanın incelemesiz kalmadığının tek garantisi budur:

```
expected (diff eksi data eksi docs) = 559
sekiz dalın birleşimi (unique)      = 559
MISSING    : (boş)
DUPLICATED : (boş)
EXTRA      : (boş)
her dilimin içeriği 71e003d ile     : 8/8 IDENTICAL
```

Kurulum sırasında üç hata bu sayımlarla yakalandı ve düzeltildi: zsh'ın değişkenleri
kelimelere bölmemesi, `git checkout <ref> -- <path>`'in **silinen** 6 dosyayı geri
getirememesi, ve `tools/calendar-data` altındaki 278 data dosyasının S7'ye sızması.

## 4. Ön koşul: `.coderabbit.yaml`

Taban dala iki değişiklik eklendi (dilim dalları miras alır):

- `auto_review.base_branches`'e `review/base-pr16` — dilim PR'ları otomatik incelensin.
- `path_filters`'a `!tools/calendar-data/data/**` — kanıt artefaktları (278 dosya)
  inceleme kapsamı dışına çıkar. `.sonarcloud.properties` ve `.codacy.yaml` aynı yolu
  zaten hariç tutuyor; **bu filtre main'e de alınmalıdır** ki gelecekteki PR'lar
  CodeRabbit'in dosya limitine 278 dosya daha yakın başlamasın.

## 5. Planda öngörülmeyen kısıt — CodeRabbit inceleme kotası

Sekiz PR aynı anda açıldığında yalnız biri incelendi; kalanı:

> Review limit reached — Next included review available in 59 minutes.
> You've used all free OSS reviews for now.

Public repo OSS kotasına tabi (PR run config'i "Pro Plus" gösterse de). Pratik sonuç:
incelemeler **saatte bir** temposuna yayılmak zorunda, sekiz dilim için ~8 saat.
Ayrıca CodeRabbit incremental'dır ve "zaten incelenmiş commit'i yeniden incelemez";
kotaya takılmış bir dilimi yeniden tetiklemek yeni bir commit gerektirebilir.

## 6. İlerleme

| Dilim | Durum |
|-------|-------|
| #18 DB kontrol düzlemi | **incelendi — 29 aksiyon alınabilir bulgu** ([01](01-slice-02-findings.md)) |
| #17 API security | tetiklendi, kotaya takıldı |
| #19–#24 | kotayı bekliyor |

## 7. Kapanış adımları

1. Kalan dilimler incelenir, bulgular bu dizine yazılır.
2. Critical/High kayıtlar `development`'a küçük düzeltme PR'ları olur (normal CI kapısı).
3. Medium/Low kayıtlar backlog'a alınır ve
   [pr-review2 §3.4](../pr-review2/09-ci-gate-remediation.md) açık kayıtlarıyla birleşir.
4. Review PR'ları **merge edilmeden** kapatılır, `review/*` dalları silinir.
