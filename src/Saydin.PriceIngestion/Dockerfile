# CFGR-003: Base image patch-level pin (bkz. Saydin.Api/Dockerfile).
FROM mcr.microsoft.com/dotnet/sdk:10.0.0-bookworm-slim AS build
WORKDIR /app

# Directory.Packages.props: Central Package Management — csproj'lar Version belirtmiyor,
# tüm versiyonlar bu dosyadan gelir. COPY zincirinden düşerse `dotnet restore` NU1015 verir.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Saydin.Shared/Saydin.Shared.csproj src/Saydin.Shared/
COPY src/Saydin.PriceIngestion/Saydin.PriceIngestion.csproj src/Saydin.PriceIngestion/
RUN dotnet restore src/Saydin.PriceIngestion/Saydin.PriceIngestion.csproj

COPY src/Saydin.Shared/ src/Saydin.Shared/
COPY src/Saydin.PriceIngestion/ src/Saydin.PriceIngestion/
RUN dotnet publish src/Saydin.PriceIngestion/Saydin.PriceIngestion.csproj \
    -c Release \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0.0-bookworm-slim AS runtime
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
