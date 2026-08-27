# Mekanik Kapılar ve Kanıt Kaydı

> Ana agent tarafından çalışma ağacı üzerinde doğrudan çalıştırılan, agent yorumundan bağımsız
> kontroller. Tarih: 2026-08-27.

## 1. Derleme

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet build Saydin.Services.sln -c Debug
```

**Build succeeded — 0 Warning, 0 Error.**

## 2. Unit test süitleri

| Süit | Bu review | Önceki review (`f9f608d`) | Değişim |
|---|---:|---:|---:|
| `Saydin.Api.Tests` | **641** | 545 | +96 |
| `Saydin.PriceIngestion.Tests` | **179** | 145 | +34 |
| `Saydin.CalendarData.Tests` | **92** | 80 | +12 |
| `Saydin.DataQualityAudit.Tests` | **96** | 84 | +12 |
| `Saydin.DataRepair.Tests` | **28** | 15 | +13 |
| `Saydin.DatabaseRoleBootstrap.Tests` | **98** | 76 | +22 |
| **Toplam** | **1.134** | 945 | **+189** |

Tümü `0 failed, 0 skipped`.

## 3. Önceki review'in iki Critical'ının kapanma kanıtı

### PR1 Critical #1 — kök `docker-compose.yml` kontrol düzlemi

| Kanıt | Değer |
|---|---|
| `grep -ci backup docker-compose.yml` | **19** (önce **0**) |
| `backup-v1` secret üretimi | `docker-compose.yml:36` |
| Secret kurulumu (0400, uid 1001) | `:67` |
| `SAYDIN_BACKUP_V1_VALID_UNTIL` türetimi | `:166-178` |
| `--backup-v1-valid-until` argümanı | `:282` |
| Yeni `database-backup-hba` servisi | `:193` |

**Sonuç: FIXED.**

### PR1 Critical #2 — `deploy-release.sh` manifest bağlama `KeyError`

| Kanıt | Değer |
|---|---|
| Hard-coded `runtime` sözlüğü | **Kaldırılmış**; script 370 satıra yeniden yazılmış |
| Bağlama yeni konumu | `render-deployment-env.py:45-60` (generic + `data_repair` kontrolü) |
| Şema `runtimeImages.required` | **12 anahtar** (`loki`, `tempo`, `data_repair` dahil) |
| Regresyon testi | `release-manifest-self-test.py:137-145` — `loki` ve `data_repair` eksikliği/drift'i için negatif test |
| Monitoring düzlemi | `deploy-release.sh:277` `alertmanager tempo loki otel-collector postgres-exporter redis-exporter ...` başlatıyor |

**Sonuç: FIXED**, üstelik tek-kaynak ilkesiyle.

## 4. Bu review'in iki Critical'ının ampirik doğrulaması

### Critical — `run-development-compose-smoke.sh` bayat migration sayısı

| Kaynak | Değer |
|---|---|
| Ağaçtaki migration dosyası (`.sql` + `.sh`) | **26** |
| `MigrationManifest.cs:59-62` — sayılan uzantılar | `.sql` ve `.sh` |
| `MigrationRunner.cs:240` — `--verify-only` dönüşü | `new MigrationRunResult(0, manifest.Migrations.Count, 0)` |
| Üretilen çıktı (`Program.cs:36-37`) | `applied=0; already_applied=26; skipped_optional=0; ...` |
| `run-development-compose-smoke.sh:178,183` aradığı | `already_applied=25` |

`grep -q` eşleşmez → `set -eu` altında script non-zero döner. **Yeni required CI kapısı her koşuda
deterministik olarak kırmızıdır.** Bu, önceki review'in `schema_migrations count = 23` bulgusuyla
**aynı anti-desendir**: o örnek düzeltilmiş, desen yeni bir kapıda yeniden üretilmiştir.

### Critical — OTel Collector readiness probe'u loopback'e bağlı endpoint'i ağdan yokluyor

| Kaynak | Değer |
|---|---|
| `infrastructure/otel/otel-collector.production.yml:2-3` | `health_check.endpoint: 127.0.0.1:13133` |
| `infrastructure/release/deploy-release.sh:286` | `wget -q --spider http://otel-collector:13133/` (Prometheus container'ından) |
| Sonuç | Bağlantı reddedilir; 60 denemeden sonra `deployment_monitoring_readiness_failed` |

## 5. `CalendarPlanMaterializer` günlük çakışması (ana agent doğrulaması)

| Kaynak | Değer |
|---|---|
| `CalendarPlanMaterializer.cs:39` | `SnapshotSetId = $"cal-tcmb-{cutoff:yyyy-MM-dd}"` — her gün değişir |
| `CalendarPlanMaterializer.cs:22` | `CoverageThrough = cutoff` — her gün değişir |
| `CalendarPlanMaterializer.cs:45-46` | `WritePrivateFileIdempotent(outputPath, …, "materialized_plan_conflict")` |
| `SecureBundleStorage.cs:58-63` | Dosya varsa: içerik birebir aynıysa sessiz dönüş, **değilse `throw`** |
| `run-acquisition.sh:44,51-53` | Plan yolu sabit (`$SAYDIN_CALENDAR_TCMB_PLAN`); dosya **silinmiyor** |

2. günde içerik farklı → `materialized_plan_conflict`. Önceki review'in "günlük timer 2. koşuda
kırılıyor" bulgusu **bir katman yukarıda yeniden üretilmiştir**.

## 6. Diff kapsama doğrulaması

| Ölçüt | Değer |
|---|---|
| Review edilen dosya | 361 (304 tracked + 57 untracked) |
| Değişiklik | +20.011 / −3.397 |
| Bir hatta atanmayan dosya | **0** |
| Kapsam dışı (bilinçli) | `tools/calendar-data/data/snapshots/**`, `docs/analysis/pr-review/**` |

## 7. Review yürütme kaydı ve sınırı

| Ölçüt | Değer |
|---|---|
| Hat | 19 (17 dosya kapsamlı + R17 remediation denetimi + R18 ürün/DX) |
| Bulgu üreten agent | 19/19 tamamlandı |
| Bağımsız doğrulayıcı | **15/19 tamamlandı** |
| Doğrulanamayan hat | `R09`, `R10`, `R16`, `R18` — oturum limiti |
| Telafi | Bu dört hattaki High kayıtlar ana agent tarafından elle denetlendi; `R16`/`R18`'in High'ı `R15` doğrulayıcısı tarafından zaten `CONFIRMED` edilmişti |

**Bu bir kapsam sınırıdır ve raporda her kayıtta açıkça işaretlenmiştir** (`DOĞRULANMADI
(yalnız üreten agent)`). Bu 58 kayıt, doğrulanmış 227 kayıtla aynı güven düzeyinde değildir.
