# /review-code — Kod İnceleme Agent'ı

Bu komutu çalıştırdığında belirtilen dosya(lar) üzerinde kapsamlı bir inceleme yap.

## Kullanım

```
/review-code src/Saydin.Api/Endpoints/WhatIfEndpoints.cs
/review-code src/Saydin.PriceIngestion/Adapters/
```

---

## İnceleme Adımları

### 1. Finansal Hassasiyet Kontrolü

Aşağıdaki isimleri taşıyan değişkenlerin tipini kontrol et:
`price`, `amount`, `value`, `rate`, `profit`, `loss`, `quantity`, `total`, `fee`

```
✗ BUG: double price = 23.45;        → decimal olmalı
✗ BUG: float amount = 10000f;       → decimal olmalı
✓ OK:  decimal price = 23.45m;
```

### 2. Mimari Uyum Kontrolü

```
✗ Saydin.Api → dış finansal API HTTP çağrısı var mı?
✗ Saydin.PriceIngestion → HTTP endpoint var mı? (WebApplication, MapGet/Post vb.)
✗ Controller sınıfı var mı? (ApiController attribute veya ControllerBase)
✗ new HttpClient() kullanımı var mı?
✗ Endpoint handler doğrudan Repository çağırıyor mu?
✗ Domain entity DTO olarak kullanılıyor mu?
```

### 3. Güvenlik Kontrolü

```
✗ SQL string interpolation: $"SELECT ... {variable}" → parameterized query kullan
✗ API key hardcode: "sk-", "Bearer eyJ" vb. string literal → environment variable kullan
✗ Exception yutma: catch { } veya catch (Exception) { } içinde log yok
✗ Timeout tanımlanmamış HTTP çağrısı
```

### 4. Async/Await Doğruluğu

```
✗ .Result veya .Wait() kullanımı → deadlock riski
✗ async void (event handler dışında) → exception yutulur
✗ CancellationToken almasına rağmen iç çağrılara geçirilmemesi
✓ ConfigureAwait: library kodu için ConfigureAwait(false)
```

### 5. İsimlendirme Kuralları

```
✗ Interface 'I' prefix eksik: WhatIfCalculator → IWhatIfCalculator
✗ Async method suffix eksik: Calculate() → CalculateAsync()
✗ Private field '_' prefix eksik: priceRepository → _priceRepository
✗ DTO record değil class olarak tanımlanmış
```

### 6. Polly Kontrolü (Dış API Adapter'ları İçin)

```
✗ IExternalApiAdapter implementasyonunda Polly retry yok
✗ Circuit breaker policy yok
✗ Timeout policy yok
```

### 7. Test Kapsamı

```
✗ Services/ katmanında public method testlenmemiş
✗ Test adı MethodName_Scenario_ExpectedResult formatında değil
```

---

## Rapor Formatı

Her ihlal için şu formatı kullan:

```
❌ [KURAL] dosya_yolu:satır_numarası
   Sorun: <sorunun açıklaması>
   Düzeltme: <nasıl düzeltilmeli>
```

İhlal yoksa:
```
✅ Tüm kontroller geçti.
```

Özet olarak ihlal sayısını kategoriye göre listele.
