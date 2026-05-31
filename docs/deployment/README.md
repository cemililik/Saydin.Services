# Saydın Backend — Deployment Dokümantasyonu

Bu dizin, backend'in **production'a alınması** (hosting, sunucu kurulumu, geçiş) ile ilgili
belgeleri içerir. Yerel geliştirme iş akışı için [`../development-guide.md`](../development-guide.md);
ölçeklendirme tetikleyicileri için [`../high-traffic-checklist.md`](../high-traffic-checklist.md).

## Belgeler

| Doküman | İçerik |
|---|---|
| [`hosting-comparison.md`](hosting-comparison.md) | **Karar süreci.** Tüm hosting seçeneklerinin (Oracle/AWS/Azure/GCP/Firebase/managed-DB/serverless/ücretli-VPS) karşılaştırması, TimescaleDB lisans kısıtı analizi, karar matrisi ve kaynaklar. |
| [`oci-migration-plan.md`](oci-migration-plan.md) | **Geçiş runbook'u.** Oracle Cloud A1'e fazlı (Faz 0–9) lift-and-shift planı: provisioning, sertleştirme, ARM build, fresh-init migration, Caddy/TLS, yedekleme, cutover. Kopyala-çalıştır config'ler (prod compose, Caddyfile, backup script, prod `.env`). |
| [ADR-007](../decisions/ADR-007-hosting-deployment.md) | **Karar kaydı.** Hosting/deployment kararının resmî ADR'ı (bağlam, seçenekler, karar, sonuçlar/risk). |

## Özet karar

> Backend, **Oracle Cloud Always Free Ampere A1** (ARM, 4 OCPU / 24 GB, kalıcı **$0**) VM'ine
> mevcut `docker-compose` **lift-and-shift** edilir; kod/migration değişmez, TimescaleDB tam
> korunur. Önüne TLS için **Caddy + Let's Encrypt** konur (ngrok'un yerini alır). Oracle
> kapasite vermezse yedek plan: **Hetzner CAX11 (≈ €3.79/ay)**.
>
> **Neden VM (managed değil):** Projenin TimescaleDB **compression** bağımlılığı (TSL lisansı)
> hiçbir ücretsiz *managed* PostgreSQL'de mevcut değil → $0 + TimescaleDB için tek yol
> self-host. Detay: [`hosting-comparison.md`](hosting-comparison.md) §3.

## Okuma sırası

1. Kararın *neden*'i → [ADR-007](../decisions/ADR-007-hosting-deployment.md)
2. Karşılaştırmanın *detayı* → [`hosting-comparison.md`](hosting-comparison.md)
3. *Nasıl* uygulanır → [`oci-migration-plan.md`](oci-migration-plan.md)

## Durum

| Faz | Durum |
|---|---|
| Karar + dokümantasyon | ✅ Tamamlandı (2026-05-31) |
| OCI provisioning → cutover (Faz 1–9) | ⏳ Uygulama bekliyor (runbook hazır) |

> Geçiş tamamlandıkça `oci-migration-plan.md` master checklist'i ve bu tablo güncellenir.
