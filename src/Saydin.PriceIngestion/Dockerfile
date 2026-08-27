# CFGR-003 follow-up + Sonar S6596: SHA digest pin (digest tek başına; tag yok).
# tag: 10.0 (mcr.microsoft.com/dotnet/sdk)
FROM mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /app

# Directory.Packages.props: Central Package Management — csproj'lar Version belirtmiyor,
# tüm versiyonlar bu dosyadan gelir. COPY zincirinden düşerse `dotnet restore` NU1015 verir.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Saydin.DatabaseSecurity/Saydin.DatabaseSecurity.csproj src/Saydin.DatabaseSecurity/
COPY src/Saydin.Shared/Saydin.Shared.csproj src/Saydin.Shared/
COPY src/Saydin.PriceIngestion/Saydin.PriceIngestion.csproj src/Saydin.PriceIngestion/
RUN dotnet restore src/Saydin.PriceIngestion/Saydin.PriceIngestion.csproj

COPY src/Saydin.DatabaseSecurity/ src/Saydin.DatabaseSecurity/
COPY src/Saydin.Shared/ src/Saydin.Shared/
COPY src/Saydin.PriceIngestion/ src/Saydin.PriceIngestion/
RUN dotnet publish src/Saydin.PriceIngestion/Saydin.PriceIngestion.csproj \
    -c Release \
    -o /app/publish

# tag: 10.0 (mcr.microsoft.com/dotnet/runtime)
FROM mcr.microsoft.com/dotnet/runtime@sha256:a365ce6a50b09176855d085c69da3fc1204a48432e36087e9a208f6e5860e235 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

RUN useradd -r -u 1001 -s /sbin/nologin appuser
USER appuser

COPY --from=build /app/publish .

# Container liveness (review F1.7-6) — LivenessHeartbeatService /tmp/saydin-ingestion-healthy
# dosyasını her 30 saniyede touch eder. Probe dosyanın son 2 dakika içinde dokunulduğunu
# doğrular (process hung / Task.WhenAll deadlock durumlarında mtime güncellenmez).
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD test -f /tmp/saydin-ingestion-healthy \
        && find /tmp/saydin-ingestion-healthy -mmin -2 -print -quit | grep -q . \
        || exit 1

ENTRYPOINT ["dotnet", "Saydin.PriceIngestion.dll"]
