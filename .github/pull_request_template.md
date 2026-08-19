## Değişiklik Özeti

<!-- Ne değişti ve neden? -->

## Değişiklik Türü

- [ ] Bug fix
- [ ] Yeni özellik
- [ ] Refactoring
- [ ] Test
- [ ] Dokümantasyon
- [ ] Altyapı / CI

## Test

- [ ] Birim testler eklendi / güncellendi
- [ ] İlgili projelerde `dotnet restore --locked-mode` ve normal `dotnet test` geçti
- [ ] Davranış değişikliği varsa gerçek PostgreSQL/Redis sınır testi geçti
- [ ] Altyapı değişikliği varsa `infrastructure/deployment/validate-production-assets.sh` geçti
- [ ] Root Compose değişikliği varsa iki proje + mutation gate geçti
- [ ] İlgili endpoint'ler manuel test edildi

Yerel stack için önce `./infrastructure/secrets/bootstrap-dev-database.sh`, ardından
`docker compose --env-file .env --env-file .env.database-runtime up -d` kullanılır.
Admin arayüzleri yalnız `--profile devtools`; ingestion yalnız explicit `--profile ingestion`
ve worker seçimiyle başlatılır.

## Kontrol Listesi

- [ ] `decimal` kullanıldı, `float`/`double` yok
- [ ] Log mesajlarında string interpolation yok
- [ ] Secret değer env/argv/URL/log/evidence içine yazılmadı; yalnız strict private file sınırı kullanıldı
- [ ] Yeni/yenilenen bağımlılık central pin + `packages.lock.json` ile kilitlendi; lisans/audit incelendi
- [ ] Migration eklendiyse önceki migration byte'ları değişmedi; manifest/trust-root/count/fingerprint ratchet güncellendi
- [ ] Yeni endpoint/auth değişikliği varsa README, canonical architecture ve ilgili ADR güncellendi
- [ ] Korumalı endpoint örnekleri `Authorization: Installation <token>` kullanıyor; `X-Device-ID` auth değil
- [ ] Mimari kurallar ihlal edilmedi (CLAUDE.md)
- [ ] Cache key / TTL / Redis kullanımı değiştiyse `docs/cache-strategy.md` güncellendi (F2.9-10)
- [ ] Operasyonel davranış değiştiyse alert + `docs/runbooks/` bağlantısı ve bounded label seti güncellendi
- [ ] Doküman bağlantıları `.github/scripts/check-doc-links.py` ile doğrulandı
