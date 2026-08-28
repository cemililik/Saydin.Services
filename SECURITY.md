# Güvenlik Politikası

## Desteklenen sürümler

Güvenlik düzeltmeleri aktif `development` hattında hazırlanır, gerekli kapılardan geçtikten
sonra imzalı release manifest'iyle yayınlanır. Yalnız son üretim release'i desteklenen sürüm
olarak kabul edilir. Henüz yayınlanmamış commit veya yerel geliştirme stack'i için güvenlik
garantisi verilmez.

## Açık bildirme

Bir güvenlik açığını public issue, discussion, pull request veya log örneğinde paylaşmayın.
Repository'nin **Security → Report a vulnerability** özel bildirim kanalını kullanın:

<https://github.com/cemililik/Saydin.Services/security/advisories/new>

Özel kanal kullanılamıyorsa hassas ayrıntıyı public alana taşımadan repository sahibinden
özel bir iletişim kanalı isteyin. Bildirimde mümkünse etkilenen sürüm/digest, tekrar üretim
koşulları, beklenen etki ve secret içermeyen minimal kanıt bulunsun.

## Müdahale ilkeleri

- Alındı teyidi, öncelik ve yayın takvimi etkiye ve doğrulanabilirliğe göre belirlenir; sabit
  bir çözüm süresi taahhüt edilmez.
- Credential, token, müşteri verisi, ham IP, veritabanı dump'ı veya production secret'ı kanıta
  eklenmez. Yanlışlıkla paylaşılan credential derhal rotate edilir.
- Düzeltme; test, dependency audit, migration trust-root, image signature/SBOM ve staging
  kapılarından geçmeden üretime promote edilmez.
- Güvenli liman veya bug-bounty programı ilan edilmemiştir. Yasal izin olmadan production
  verisine erişmeyin, servis kesintisi veya kalıcılık yaratmayın.

Operasyonel olay müdahalesi için [runbook dizinine](docs/runbooks/README.md) bakın.
