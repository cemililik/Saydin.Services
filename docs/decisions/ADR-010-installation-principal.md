# ADR-010 — Installation Principal ve Opaque Credential

- **Durum:** Kabul edildi — additive 021 ve forward-only retention 022 uygulandı
- **Tarih:** 2026-08-19
- **Karar verenler:** Backend ekibi
- **İlgili bulgular:** API-02, API-TRUST-001, API-10

## Bağlam

`X-Device-ID` istemcinin serbestçe seçtiği bir değerdir. Biçimsel doğrulama possession
proof değildir; başka bir değer göndererek scenario sahipliği veya plan kotası elde
edilebilirdi. Eski anonim scenario'ları cihaz id'sine bakarak yeni principal'a devretmek de
aynı nedenle güvenli değildir.

## Karar

- Sunucu 32 CSPRNG byte üretir ve unpadded 43 karakter base64url credential'ı yalnız bir
  kez, `Cache-Control: no-store` ile döner.
- İstekler tek `Authorization: Installation <credential>` header'ı kullanır. Malformed,
  unknown, expired ve revoked durumları aynı generic `401`/`WWW-Authenticate: Installation`
  sözleşmesine döner.
- PostgreSQL raw credential saklamaz. Yalnız private-file keyring ile
  HMAC-SHA-256 verifier ve key version tutulur. API credential tablolarına doğrudan yetkili
  değildir; locked-search-path, exact-ACL `SECURITY DEFINER` fonksiyonlarını çağırır.
- Rotation iki fazlıdır. Begin eski credential'ı aktif tutar ve pending replacement
  üretir; commit yeni credential ile atomik activate+old revoke yapar. Commit retry
  idempotent, pending credential business API'de geçersizdir. Principal-başına transaction
  advisory lock begin/commit/revoke deadlock'larını kapatır.
- Existing users `legacy_quarantined`; yeni registration `active` ve `device_id=NULL` olur.
  `X-Device-ID` hiçbir route'u authorize etmez ve compiled auto-claim/upsert yolu yoktur.
  Bağımsız account/payment/support proof sistemi kurulmadan legacy scenario transferi yoktur.
- Asset catalog singleton revision+canonical SHA her cache-affecting asset mutation'unda aynı
  transaction'da yenilenir. Tüm asset/price/WhatIf/DCA cache envelope'ları catalog token'ına
  bağlıdır.
- Principal silme public/API işlemi değildir. Control-plane/owner `users` satırını silmeden önce
  scheduler-owned, locked-search-path `SECURITY DEFINER` trigger fonksiyonu ilişkili activity
  kayıtlarının `user_id` alanını `NULL`, eski device bağını sabit `server-redacted` yapar. Audit
  olayı silinmez; FK artık redaction tamamlanmadan silmeyi reddeden `NO ACTION` sözleşmesidir.
- Timescale scheduler'ın activity hypertable üzerindeki explicit `SELECT,UPDATE` self-grant'i,
  owner olmasına rağmen Timescale permission path'inin ihtiyaç duyduğu en dar çalışma yetkisidir.
  Owner, API, audit veya PUBLIC için geniş `UPDATE`/`DELETE` grant'i yoktur.

## Migration ve Rollout

`021_api_trust_expand.sql` immutable SHA-256:
`1f44aa1413d611cb8b078541e0100985c33614274e2fd700a8f8b94303045c1e`.

`022_principal_retention.sql` immutable SHA-256:
`568017c27eb6038a06b48ee00f2f0820bba6cf7b577dd5f283291ac9995e8afd`.

021 additive'dir. Legacy private erişim API kodunda kapanmıştır; kanıtsız otomatik claim yoktur.
022 forward-only'dir. Role bootstrap, 022 henüz uygulanmamışsa yalnız owner'ın çağırabildiği tek
kullanımlık admin-owned transition helper'ını exact fingerprint ile kurar. Migration mevcut
compressed chunk'ları transaction içinde decompress eder, FK/owner/compression kontratını dönüştürür,
chunk'ları yeniden compress eder ve 7 günlük policy'yi geri kurar; helper ve geçici schema/CREATE/
REFERENCES yetkileri commit öncesi tamamen tüketilir. Fresh rollout sırası bu nedenle
`role-bootstrap ensure/verify → migrator apply/verify → DQA` olarak sabittir. API binary rollback'i
022'yi geri almaz; trigger ve fail-closed FK kalıcıdır.

## Sonuçlar / Riskler

- Random/replayed/revoked credential private veriye erişemez; cross-principal scenario 404'tür.
- Bearer kopyası çalınırsa replay edilebilir. Device-bound asymmetric proof ayrı ürün
  kararıdır; bu ADR onun sağlandığını iddia etmez.
- Legacy kullanıcılar proof olmadan eski scenario'larını devralamaz. Recovery/retention
  süresi ürün ve hukuk onayı gerektirir.
- Silinen principal'ın activity olayı korunur ancak principal ve eski device korelasyonu kalıcı
  olarak redakte edilir. Yeniden ilişkilendirme iddiası veya public self-delete endpoint'i yoktur.

## İlgili Dökümanlar

- [ADR-003](ADR-003-rate-limiting.md)
- [Scenario payload integrity](ADR-008-scenario-payload-pagination.md)
- `infrastructure/postgres/migrations/021_api_trust_expand.sql`
- `infrastructure/postgres/migrations/022_principal_retention.sql`
- `src/Saydin.Api/Endpoints/InstallationEndpoints.cs`
