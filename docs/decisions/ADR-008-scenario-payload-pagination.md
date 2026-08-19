# ADR-008 — Kaydedilmiş Senaryo Payload Bütçesi ve Cursor Sayfalama

- **Durum:** Uygulama katmanı kabul edildi; migration 018 release gate'i bekliyor
- **Tarih:** 2026-08-18
- **Karar verenler:** Backend ekibi
- **İlgili bulgu/work item:** `REM-API-03` (ExtraData abuse ve liste amplification)

## Bağlam

`POST /v1/scenarios` daha önce sınırsız bir request body içindeki keyfi `JsonElement`
değerini `jsonb` kolona geçiriyordu. Premium planda `MaxSavedScenarios=0` da sınırsız
storage anlamına geliyordu. `GET /v1/scenarios` bütün satırları tek array olarak okuyup
döndürdüğü için storage ve response/EF allocation maliyetleri birlikte büyüyebiliyordu.

Mevcut mobil sözleşme server reposunda tahmin edilmedi; sibling `Saydin.Client` kaynaklarından
kanıtlandı:

- `what_if`: `includeInflation`, `mode`;
- `comparison`: `winnerSymbol`, `winnerName`, `winnerReturn`, `includeInflation`;
- `dca`: `includeInflation`, `period`, `periodicAmount`;
- `portfolio`: `totalReturn`, `includeInflation`, `items`; her item
  `assetSymbol`, `assetDisplayName`, `amount`, `amountType`.

İstemci `GET /v1/scenarios` gövdesini doğrudan JSON array olarak parse ediyor. Bu gövdeyi
bir page envelope'a çevirmek breaking change olur.

## Değerlendirilen Seçenekler

1. **Mevcut endpoint'i doğrudan sayfalı yapmak.** En küçük server yüzeyidir ama yayınlanmış
   array sözleşmesini kırar.
2. **Offset pagination eklemek.** Kolaydır; eşzamanlı insert/delete altında duplicate/skip
   üretir ve büyük offset'te DB maliyeti büyür.
3. **Legacy endpoint'i bounded tutup additive keyset endpoint eklemek.** Eski istemciyi
   kırmaz; yeni istemci kararlı ve bounded sayfalama kullanabilir.
4. **Cursor'ı HMAC/Data Protection ile imzalamak.** Integrity sağlar; ancak yeni secret
   lifecycle'ı veya replica/restart arasında ortak kalıcı key ring gerektirir. Cursor herhangi
   bir erişim yetkisi taşımadığı ve her sorgu ayrıca current `userId` ile sınırlandığı için bu
   deployment karmaşıklığı güvenlik sınırını güçlendirmez.

## Karar

### Save request ve ExtraData

- Endpoint request body hard limit'i **32 KiB**'dır. `Content-Length` varsa okumadan,
  yoksa akışta 32 KiB + 1 byte okunur; aşım RFC 7807 **413** döner.
- JSON, model binding öncesinde endpoint'e özel toplam derinlik sınırıyla parse edilir.
  Web serializer'ın case-insensitive alan eşitliğiyle uyumlu duplicate property kontrolü
  uygulanır; `extraData` + `ExtraData` dahil ambiguous last-wins payload reddedilir.
- `ExtraData` yalnız JSON object veya null'dır. Server validator şeması **legacy-v1** olarak
  isimlendirilir ve yukarıdaki kanıtlanmış type/field/type matrisini allowlist eder. Mevcut
  istemci wire payload'ında olmayan `schemaVersion` zorunlu kılınmaz. Yeni alan/şema sürümü,
  istemci ve server birlikte versionlandıktan sonra additive olarak tanımlanmalıdır.
- Depolama bütçeleri: jsonb text UTF-8 **8192 byte**, depth **8**, toplam property **64**,
  toplam node **256** (property adları dahil), bütün array'lerde toplam item **128**, tek
  string'in decoded UTF-8 değeri **2048 byte**. Tüm kontroller user upsert/last-seen/count/insert
  yazılarından önce çalışır.
- 8192 hesabı raw client whitespace'ına göre değil PostgreSQL `jsonb::text` temsiline göre
  yapılır: decoded UTF-8 string'ler ile object/array colon/comma boşlukları hesaba katılır.
  Böylece uygulama sınırı migration 018 `octet_length(extra_data::text)` CHECK'iyle aynı
  boundary'yi kullanır. Toplam request body limiti ayrıca client whitespace/encoding abuse'u
  sınırlar.

### Storage hard cap

- Sistem hard save cap'i kullanıcı başına **100**'dür. Effective cap
  `configured <= 0 ? 100 : min(configured, 100)` olarak hesaplanır.
- `AppConfig.maxSavedScenarios` planın ham `0=unlimited` değerini değil effective cap'i döner;
  premium istemci artık yanlış bir sınırsızlık vaadi görmez.
- 100'den fazla mevcut satır silinmez. Yeni save 422 alır; tüm mevcut satırlar additive cursor
  endpointinden okunabilir.

### Liste sözleşmeleri

- Legacy `GET /v1/scenarios` bare-array response'unu korur; `(created_at,id) DESC` sırasıyla
  en fazla **100** kayıt döner.
- Yeni `GET /v1/scenarios/page?limit=&cursor=` response'u
  `{ "items": [...], "nextCursor": "..." }` biçimindedir. Default limit **20**, maksimum
  **50**; repository yalnız `limit + 1` satır okur.
- Stable keyset `(created_at,id) DESC`'dir. Sonraki sorgu
  `created_at < cursor.createdAt OR (created_at = cursor.createdAt AND id < cursor.id)` kullanır;
  her sorguda ayrıca `user_id = currentUserId` zorunludur.
- Cursor v1, sabit 25 byte payload'ın canonical unpadded Base64Url gösterimidir:
  `version(1) + UTC ticks(8) + Guid(16)`. Exact encoded/decoded length, version, epoch sonrası
  geçerli tarih, non-empty Guid ve canonical round-trip doğrulanır; bozuk değer generic 400'dür.
  Cursor **unsigned ve authorization değildir**. Yapısal olarak geçerli forged tuple decode
  olabilir; bunun etkisi yalnız aynı kullanıcıya ait bounded listenin başlangıç konumunu seçmektir.
  Tenant izolasyonunu cursor değil zorunlu repository `userId` predicate'i sağlar.
- Page activity yeni bir DB action değeri açmaz. Mevcut allowlist'teki `scenario_list` kullanılır;
  data içinde düşük kardinaliteli `paginated=true/false` ve `hasNextPage` bulunur.

## Migration 018 Release Gate'i (bu lane'de uygulanmadı)

Migration numarası **018** rezerve edilmiştir. Calendar lane'indeki 017 tamamlanmadan migration,
migrator manifesti, CI/Compose veya Shared mapping değiştirilmez. 018 şunları içermelidir:

1. Mevcut ihlalleri read-only preflight ile say; object olmayan veya
   `octet_length(extra_data::text) > 8192` satır varsa otomatik truncate/delete etmeden fail-closed.
2. `extra_data IS NULL OR jsonb_typeof(extra_data) = 'object'` ve
   `extra_data IS NULL OR octet_length(extra_data::text) <= 8192` CHECK'lerini ekle ve validate et.
3. `saved_scenarios(user_id, created_at DESC, id DESC)` composite index'ini ekle. Eski iki kolonlu
   index ancak yeni index query planında doğrulandıktan sonra ayrı, kontrollü cleanup'ta düşürülsün.
4. Gerçek PostgreSQL fixture'ında 8192 kabul/8193 red ve non-object red zorunludur. Ayrı pagination
   fixture'ı aynı `created_at` değerli UUID'lerle en az iki sayfayı yürüyüp duplicate/missing
   olmadığını ve başka `user_id` satırının hiçbir sayfaya sızmadığını kanıtlamalıdır; LINQ-to-Objects
   testi PostgreSQL UUID sıralamasının kanıtı sayılmaz.
5. Temsili tek-kullanıcı veri setinde `EXPLAIN (ANALYZE, BUFFERS)` page sorgusunun composite
   index scan kullandığını, explicit sort/large offset yapmadığını ve en fazla `limit+1` satıra
   kadar okuduğunu doğrula.

## Sonuçlar ve Residual Risk

- Request/JSON/DB allocation'ları bounded, type-confused ve bilinmeyen alanlar fail-closed olur.
- Eski mobil array kontratı korunur; yeni istemci cursor endpointine kademeli geçebilir.
- Migration 018 yayınlanana kadar unsafe doğrudan DB writer için octet/object defense-in-depth
  yoktur; API path'i yine sınırlandırılmıştır.
- `CountByUserIdAsync` ile insert aynı atomik işlem değildir. İki eşzamanlı save cap'in bir üstüne
  çıkabilir; bu yarış `API-05` atomik constraint/transaction lane'inde kapanacaktır.
- Keyset bir snapshot değildir: cursor sonrasında eklenen daha yeni kayıtlar mevcut traversal'da
  görünmez; silinen kayıtlar boşluk bırakabilir. Buna karşın sıralama boundary'si aynı timestamp'te
  duplicate/skip üretmez.
- Legacy istemci 100'den eski kayıtları göstermez; veri kaybı yoktur ve cursor endpointi bütün
  mevcut kayıtları erişilebilir tutar. Mobil istemcinin cursor'a geçişi ayrı ürün work item'ıdır.
