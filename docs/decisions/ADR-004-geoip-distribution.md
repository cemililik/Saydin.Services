# ADR-004 — GeoIP (MaxMind GeoLite2) Dağıtım Stratejisi

- **Durum:** Kabul edildi
- **Tarih:** 2026-05-29
- **Karar verenler:** Backend ekibi (lisans/operasyon onayı: aşağıdaki "İnsan onayı")
- **İlgili bulgular (code review):** Claude `[C-G-GeoIP-1]`, `[C-YAPISAL-5]`; GPT-5 `[G-G-05]`; Faz 1 `F1.6-4`, Faz 2 `F2.6-12`; Faz 4 `F4-7` (Açık Soru 7)

---

## Bağlam

`activity_logs` zenginleştirmesi için IP→ülke/şehir çözümlemesi MaxMind **GeoLite2-City**
veritabanı (`.mmdb`) ile yapılır (`MaxMindGeoIpResolver`, `Program.cs` Singleton).
`.mmdb`:
- Büyük bir binary'dir (repoyu şişirir),
- MaxMind GeoLite2 **EULA**'sı kayıtlı hesap + license key gerektirir ve yeniden
  dağıtımı kısıtlar.

Bu nedenle dosya **repoya commit edilmez** (`.gitignore *.mmdb`). compose
`./infrastructure/geoip` dizinini api konteynerine `/app/geoip` olarak read-only mount
eder; `GeoIp__DatabasePath=/app/geoip/GeoLite2-City.mmdb`. Dosya yoksa resolver
`LogWarning` yazar ve `country`/`city` = null döner (mevcut davranış,
`MaxMindGeoIpResolver.cs:24-35,39-40`).

---

## Karar

1. **`.mmdb` ASLA commit edilmez** (`.gitignore *.mmdb` ile enforce). Yalnız `.gitkeep`
   tracked'dir.
2. **Edinme — MaxMind hesabı + license key (env var secret):**
   - **Dev:** Geliştirici `infrastructure/geoip/README.md`'deki tek-satır komutu
     (`GEOIP_ACCOUNT_ID`/`GEOIP_LICENSE_KEY` ile `geoipupdate` veya curl permalink)
     çalıştırıp `infrastructure/geoip/GeoLite2-City.mmdb` dosyasını indirir.
   - **Production:** deploy/init adımı (veya `geoipupdate` sidecar/cron) mount'lu volume'a
     güncel DB'yi indirir; license key deploy ortamının **secret store**'undan gelir
     (bkz. [ADR-005](ADR-005-secrets-management.md)) — asla `appsettings.json`'a yazılmaz.
3. **CI'a download adımı EKLENMEZ.** Testler gerçek `.mmdb` gerektirmez; resolver
   warn+null'a düşer (testler bunu doğrular). Böylece license key fork PR CI'sına
   sızdırılmaz ve CI hızlı kalır.
4. **En iyi-çaba (best-effort) sözleşmesi:** `.mmdb` yoksa `LogWarning` + null geo.
   Geo enrichment yalnız gözlemlenebilirlik içindir; **bir isteği asla başarısız etmez.**
5. **Yenileme kadansı:** MaxMind GeoLite2 ~haftalık güncellenir; production `geoipupdate`
   ile bu kadansta tazeler. Eskime analytics için kabul edilebilir.

### Gerekçe
- CLAUDE.md güvenlik kuralı: API/license key'ler env var / user-secrets ile gelir, asla
  appsettings.json'a yazılmaz.
- CLAUDE.md log seviyesi: "LogWarning → beklenen ama anormal durum" — eksik DB tam bu.
- MVP pragmatizmi: geo kritik değil; istek-yolunda sert dış bağımlılık ve CI'da gizli
  anahtar riski yaratmamak.

---

## Sonuçlar / Risk

**Olumlu:** Lisans uyumlu, repo küçük, CI hızlı/güvenli, istek-yolunda sert dış bağımlılık
yok, eksik DB graceful degrade eder.

**Risk:** Deploy'da indirme unutulursa geo sessizce kapalı kalır → **mitigasyon:** deploy
checklist + başlangıçtaki `LogWarning` (operasyonel sinyal). License key yönetimi
ADR-005'e bağlıdır.

---

## İlgili Dökümanlar

- [`infrastructure/geoip/README.md`](../../infrastructure/geoip/README.md) — indirme talimatı
- [`docs/development-guide.md`](../development-guide.md) — GeoIP (opsiyonel) edinme notu
- [`ADR-005-secrets-management.md`](ADR-005-secrets-management.md) — license key saklama
- CLAUDE.md — Güvenlik bölümü (key env var)
- Faz aksiyon planı: `docs/code-reviews/ACTION-PLAN.md` (F4-7)
