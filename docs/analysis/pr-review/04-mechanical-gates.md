# Mekanik Kapılar ve Kanıt Kaydı

> Ana agent tarafından `f9f608d` üzerinde doğrudan çalıştırılan, agent yorumundan bağımsız kontroller.
> Tarih: 2026-08-20

## 1. Derleme

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet build Saydin.Services.sln -c Debug
```

| Ölçüt | Sonuç |
|---|---|
| Sonuç | **Build succeeded** |
| Warning | 0 |
| Error | 0 |
| Derlenen proje | 17 |

## 2. Unit test süitleri (gerçek altyapı gerektirmeyenler)

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test <proje>
```

| Süit | Sonuç | `06-remediation-progress.md` iddiası |
|---|---|---|
| `Saydin.Api.Tests` | **545 passed / 0 failed / 0 skipped** | `545/545` ✅ eşleşiyor |
| `Saydin.PriceIngestion.Tests` | **145 passed / 0 / 0** | doküman kademeli olarak 104 → 140 diyor; güncel ağaç 145 |
| `Saydin.CalendarData.Tests` | **80 passed / 0 / 0** | `80/80` ✅ |
| `Saydin.DataQualityAudit.Tests` | **84 passed / 0 / 0** | `84/84` ✅ |
| `Saydin.DataRepair.Tests` | **15 passed / 0 / 0** | `15/15` ✅ |
| `Saydin.DatabaseRoleBootstrap.Tests` | **76 passed / 0 / 0** | `76/76` ✅ |
| **Toplam** | **945 passed, 0 failed, 0 skipped** | |

Sonuç: remediation kaydındaki unit test kabul iddiaları **birebir yeniden üretilebilir**.

## 3. Altyapısız modda test davranışı (izolasyon sözleşmesi)

| Süit | PG/Redis yokken | Yorum |
|---|---|---|
| `Saydin.Api.IntegrationTests` | `12 passed, 45 skipped`, **exit 0** | `SkippableFact` — CLAUDE.md'nin "lokal optional" sözleşmesine uygun |
| `Saydin.DatabaseMigrator.Tests` | `57 passed, **93 failed**`, exit 1 | Hard-fail; aynı solution'da iki farklı izolasyon sözleşmesi var (bkz. L18c bulgusu) |

## 4. `CLAUDE.md` yasak listesi taraması

`src/**` ve `tools/calendar-data/src/**` üzerinde:

| Kural | Sonuç | Not |
|---|---|---|
| Finansal `double`/`float` | **TEMİZ** | `double` yalnız OTel `Histogram<double>`, PDF koordinatı ve `pg_stat` süresi |
| `ControllerBase` / `[ApiController]` | **TEMİZ** | — |
| `new HttpClient()` | **1 ihlal** | `tools/calendar-data/.../Program.cs:18` — one-shot CLI |
| Dapper | **TEMİZ** | — |
| `Thread.Sleep` | **TEMİZ** | — |
| `Console.WriteLine` | **6 ihlal** | Yalnız calendar-data CLI |
| Sync-over-async | **TEMİZ** | Tek eşleşme Polly `Outcome.Result` property'si |
| `async void` | **TEMİZ** | — |
| SQL string interpolation | **TEMİZ** | — |
| API'de doğrudan `DateTime.UtcNow` | **TEMİZ** | Tek eşleşme yorum satırı |
| Log mesajında string interpolation | **TEMİZ** | — |
| Dinamik DDL identifier kaçışı | **DOĞRU** | `QuoteIdentifier`/`QuoteLiteral` standarda uygun; ayrıca sunucu tarafı `format('%I','%L',$1,$2)` |

## 5. Migration değişmezliği

`git diff a274c62 f9f608d -- infrastructure/postgres/migrations/` yalnız **eklenen** dosya döndürdü
(`015` … `022`). `001`–`014` **değiştirilmedi** — CLAUDE.md immutability kuralı korunmuştur.
Ağaçtaki toplam migration dosyası: **24** (`012b` shell adımı dahil).

## 6. Kritik bulguların ampirik yeniden üretimi

### 6.1 Kök `docker-compose.yml` kontrol düzlemi (Critical #1)

`docker-compose.yml`'deki birebir argüman listesiyle:

```
$ dotnet Saydin.DatabaseRoleBootstrap.dll ensure --admin-connection-file … --deployment-id … \
    --role-prefix … --timescaledb-version 2.16.1 --uuid-ossp-version 1.1 \
    --{migrator,api,ingestion,calendar-importer,exporter,audit}-password-file …
role-bootstrap failed: code=argument_required
EXIT=64

$ PG*=… SAYDIN_*=… dotnet Saydin.DatabaseMigrator.dll --verify-only
migration rejected: code=argument_required
EXIT=3
```

`grep -ci backup docker-compose.yml` → **0**. Buna karşılık `.github/compose.integration.yml`
(satır 67, 81-82, 137, 151-152, 203, 217-218, 270, 284-285) ve
`infrastructure/deployment/compose.production.yml` (108-109, 122, 159, 677) argümanları veriyor.

### 6.2 `deploy-release.sh` manifest bağlama (Critical #2)

```
deploy-release.sh runtime map eksik anahtarlar: ['loki', 'tempo']
→ KeyError: 'loki'  (script deployment_manifest_binding_failed / exit 78 verir)
```

`infrastructure/release/release-manifest.schema.json` `runtimeImages` için **11 anahtarın
tamamını `required`** yapıyor (`loki` ve `tempo` dahil); `deploy-release.sh:40-42` içindeki
`runtime` sözlüğünde yalnız **9** anahtar var.

### 6.3 Required CI ingestion-ledger kapısı (High)

| Kaynak | Beklenen `schema_migrations` sayısı |
|---|---|
| Ağaçtaki migration dosyası | **24** |
| `IngestionDatabaseFixture.cs:58-59` (readiness probe) | **24** |
| `.github/workflows/ci.yml:568` (fresh schema gate) | **24** |
| `PriceAuthorityMigrationIntegrationTests.cs:120` | **23** ← bayat |

## 7. Konfigürasyon ve script sözdizimi

| Kapı | Sonuç |
|---|---|
| `bash -n` (tüm `*.sh`) | 100% PASS |
| `python3 -m py_compile` (tüm `*.py`) | 100% PASS |
| YAML parse (27 dosya) | 100% PASS |
| `docker compose config` (dev / production / integration) | Üçü de env olmadan **bilinçli fail-closed** (`${VAR:?mesaj}`) |
| `ErrorMessages.resx` ↔ `.en.resx` anahtar paritesi | **80/80**, fark yok |
| Prometheus kurallarındaki `saydin_*` metriklerinin kodda karşılığı | Tamamı mevcut (backup metrikleri `backup-entrypoint.sh` textfile çıktısından) |
| Alert `runbook_url` hedeflerinin varlığı | 12/12 dosya mevcut (ancak `blob/main/…` — dosyalar henüz yalnız `development`'ta) |

## 8. Shell sertlik profili

Tüm yeni `infrastructure/**` script'leri `#!/bin/sh` + `set -eu` (+ çoğunda `umask 077`).
`pipefail` POSIX `sh`'ta olmadığı için yok; ancak yoğun pipe kullanımı var
(`backup-entrypoint.sh` 50, `deploy-release.sh` 29, `rollback-release.sh` 28,
`verify-candidate.sh` 32). Boru hattı ortasındaki hatalar sessizce yutulabilir.

## 9. Supply chain hijyeni

| Kapı | Sonuç |
|---|---|
| Üçüncü taraf GitHub Action'ları | **16/16 commit SHA ile pinli** |
| Workflow varsayılan izinleri | Tümü `permissions: {}` veya job bazlı en az yetki |
| `continue-on-error` / `\|\| true` | **Yok** |
| `if: always()` kullanımı | 4 yerde, yalnız artefakt yükleme ve cleanup — hata maskelemiyor |

## 10. Diff kapsama doğrulaması

| Ölçüt | Değer |
|---|---|
| Review edilen dosya | 554 |
| Eklenen / silinen satır | 92.466 / 3.867 |
| Bir review hattına atanmayan dosya | **0** |
| Kapsam dışı (bilinçli) | `tools/calendar-data/data/snapshots/**`, `data/normalized/*.csv` |
