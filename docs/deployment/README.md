# Saydın Backend — Deployment Dokümantasyonu

Bu dizin, backend'in **production'a alınması** (hosting, sunucu kurulumu, geçiş) ile ilgili
belgeleri içerir. Yerel geliştirme iş akışı için [`../development-guide.md`](../development-guide.md);
ölçeklendirme tetikleyicileri için [`../high-traffic-checklist.md`](../high-traffic-checklist.md).

## Belgeler

| Doküman | İçerik |
|---|---|
| [`hosting-comparison.md`](hosting-comparison.md) | **Karar süreci.** Tüm hosting seçeneklerinin (Oracle/AWS/Azure/GCP/Firebase/managed-DB/serverless/ücretli-VPS) karşılaştırması, TimescaleDB lisans kısıtı analizi, karar matrisi ve kaynaklar. |
| [`oci-migration-plan.md`](oci-migration-plan.md) | **Arşivlenmiş karar/tarihçe.** İlk Oracle A1 lift-and-shift tasarımı; inline komut ve config'ler güncel production kaynağı değildir. |
| [`../runbooks/`](../runbooks/README.md) | **Kanonik operasyon yolu.** İmzalı release promotion/rollback, alert response, backup/PITR ve restore drill. |
| [`../runbooks/data-repair.md`](../runbooks/data-repair.md) | İmzalı planla operator-only DataRepair dry-run/apply/rollback ve kalıcı receipt saklama prosedürü. |
| [`../../infrastructure/deployment/compose.production.yml`](../../infrastructure/deployment/compose.production.yml) | **Kanonik production manifest'i.** Digest-only image, private network, external secret/volume ve runtime hardening sözleşmesi. |
| [ADR-007](../decisions/ADR-007-hosting-deployment.md) | **Karar kaydı.** Hosting/deployment kararının resmî ADR'ı (bağlam, seçenekler, karar, sonuçlar/risk). |

## Özet karar

> Self-hosted TimescaleDB + Caddy sınırı korunur; cloud/region/domain seçimi dış operator
> kararıdır. Üretim, development Compose'u taşımak yerine signed/digest-only release manifest'i
> ve bağımsız production Compose kullanır. Aynı imajlar amd64/arm64 için bir kez build edilip
> staging'de doğrulanan exact digest'lerle promote edilir.
>
> **Neden VM (managed değil):** Projenin TimescaleDB **compression** bağımlılığı (TSL lisansı)
> hiçbir ücretsiz *managed* PostgreSQL'de mevcut değil → $0 + TimescaleDB için tek yol
> self-host. Detay: [`hosting-comparison.md`](hosting-comparison.md) §3.

## Okuma sırası

1. Kararın *neden*'i → [ADR-007](../decisions/ADR-007-hosting-deployment.md)
2. Karşılaştırmanın *detayı* → [`hosting-comparison.md`](hosting-comparison.md)
3. *Nasıl* uygulanır → [`../runbooks/release-promotion.md`](../runbooks/release-promotion.md),
   [`../runbooks/restore-drill.md`](../runbooks/restore-drill.md) ve kanonik production manifest'i

## Durum

| Faz | Durum |
|---|---|
| Production manifest/release/runbook artefaktları | ✅ Repository'de, fail-closed validator sahibi |
| Registry/domain/KMS/bucket/on-call ve backup role/HBA | ⏳ Operator girdisi; placeholder ile deploy reddedilir |
| Staging/prod publish, restore drill ve cutover | ⏳ Dış ortam kabul kanıtı bekliyor |

> Arşiv plan değiştirilmez; güncel acceptance ve incident kanıtı signed release manifest'i ve
> ilgili runbook kaydına eklenir.
