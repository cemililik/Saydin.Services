# GeoIP Veritabanı (MaxMind GeoLite2)

Bu klasör çalışma anında `GeoLite2-City.mmdb` dosyasını barındırır. Dosya **repoya
commit EDİLMEZ** (`.gitignore` → `*.mmdb`; lisans gereği — bkz.
[`docs/decisions/ADR-004-geoip-distribution.md`](../../docs/decisions/ADR-004-geoip-distribution.md)).
Yalnız `.gitkeep` tracked'dir.

`docker-compose.yml`, bu dizini api konteynerine `/app/geoip` olarak **read-only** mount
eder; `GeoIp__DatabasePath=/app/geoip/GeoLite2-City.mmdb`.

## Edinme (geliştirici)

1. https://www.maxmind.com/en/geolite2/signup adresinden **ücretsiz** hesap aç.
2. Account ID + License Key oluştur (*My Account → Manage License Keys*).
3. `.env`'ye ekle: `GEOIP_ACCOUNT_ID` ve `GEOIP_LICENSE_KEY`.
4. İndir (curl + permalink):

```bash
set -a; source .env; set +a   # GEOIP_ACCOUNT_ID / GEOIP_LICENSE_KEY yükle
curl -sSL -u "$GEOIP_ACCOUNT_ID:$GEOIP_LICENSE_KEY" \
  "https://download.maxmind.com/geoip/databases/GeoLite2-City/download?suffix=tar.gz" \
  | tar -xz --strip-components=1 -C infrastructure/geoip --wildcards '*/GeoLite2-City.mmdb'
```

(Alternatif: MaxMind'in resmi [`geoipupdate`](https://github.com/maxmind/geoipupdate)
aracını `GEOIP_ACCOUNT_ID`/`GEOIP_LICENSE_KEY` ile kullan.)

## Dosya yoksa ne olur?

Servis normal başlar; `MaxMindGeoIpResolver` bir **`LogWarning`** yazar ve tüm IP
çözümlemeleri `country`/`city` = `null` döner. **İstekler başarısız OLMAZ** — geo
enrichment en iyi-çaba (best-effort) gözlemlenebilirlik verisidir.

## CI

CI `.mmdb` **indirmez**; testler gerçek DB gerektirmez (resolver warn+null'a düşer).
License key fork PR CI'sına verilmez.

## Production

Deploy/init adımı (veya `geoipupdate` cron/sidecar) mount'lu volume'a güncel DB'yi indirir;
license key deploy ortamının **secret store**'undan gelir (bkz.
[`ADR-005-secrets-management.md`](../../docs/decisions/ADR-005-secrets-management.md)).
MaxMind GeoLite2 ~haftalık güncellenir; bu kadansta tazelenmesi önerilir.
