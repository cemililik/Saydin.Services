# /check-architecture — Mimari Uyum Kontrolü

Tüm kod tabanını mimari kurallara göre tara. Herhangi bir parametre gerekmez.

## Kullanım

```
/check-architecture
```

---

## Kontrol Listesi

### 1. Yasak Bağımlılıklar

```bash
# Saydin.PriceIngestion → Microsoft.AspNetCore referansı olmamalı
grep -r "Microsoft.AspNetCore" src/Saydin.PriceIngestion/

# Saydin.Api → Saydin.PriceIngestion referansı olmamalı
grep -r "Saydin.PriceIngestion" src/Saydin.Api/

# Servisler birbirini referans almamalı (Shared dışında)
grep -r "using Saydin.Api" src/Saydin.PriceIngestion/
grep -r "using Saydin.PriceIngestion" src/Saydin.Api/
```

### 2. Controller Yasağı

```bash
# Controller sınıfı olmamalı
grep -rn "ControllerBase\|ApiController\|\[Route\]" src/Saydin.Api/
```

### 3. Dış API Çağrısı Yasağı (Api Servisi)

```bash
# Saydin.Api içinde dış finansal API URL'leri olmamalı
grep -rn "tcmb\|coingecko\|goldapi\|twelvedata" src/Saydin.Api/ --include="*.cs"
```

### 4. Yasak Kodlama Kalıpları

```bash
# new HttpClient() yasak
grep -rn "new HttpClient()" src/ --include="*.cs"

# Thread.Sleep yasak
grep -rn "Thread\.Sleep" src/ --include="*.cs"

# .Result ve .Wait() yasak
grep -rn "\.Result\b\|\.Wait()" src/ --include="*.cs"

# async void yasak (event handler dışında)
grep -rn "async void" src/ --include="*.cs"

# float/double finansal değişkenlerde
grep -rn "double price\|float price\|double amount\|float amount\|double value\|float value" src/ --include="*.cs"

# SQL string interpolation yasak
grep -rn '\$".*SELECT\|INSERT\|UPDATE\|DELETE' src/ --include="*.cs"
```

### 5. Zorunlu Kodlama Kalıpları

```bash
# IHostedService'in CancellationToken alması gerekir
grep -rn "class.*:.*IHostedService\|class.*:.*BackgroundService" src/ --include="*.cs"
# → StartAsync ve ExecuteAsync metodlarının CancellationToken parametresi olmalı

# IHttpClientFactory kullanımı
grep -rn "IHttpClientFactory\|AddHttpClient" src/ --include="*.cs"
# → tüm HttpClient'lar factory üzerinden alınmalı
```

---

## Rapor Formatı

```
=== MİMARİ UYUM RAPORU ===
Tarih: <tarih>

BAŞARILAR:
  ✅ Controller sınıfı bulunamadı
  ✅ new HttpClient() bulunamadı
  ...

İHLALLER:
  ❌ [YASAK_BAĞIMLILIK] src/Saydin.Api/Services/PriceService.cs:45
     Saydin.PriceIngestion assembly'sine referans var

  ❌ [FİNANSAL_TİP] src/Saydin.Api/Services/WhatIfCalculator.cs:78
     double profitAmount → decimal olmalı

ÖZET: 0 başarılı, N ihlal
```

İhlal bulunamazsa: `✅ Tüm mimari kontroller geçti.`
