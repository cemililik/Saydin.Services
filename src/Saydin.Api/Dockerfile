# CFGR-003: Base image patch-level pin. SHA digest pin'leme prod için
# (Dependabot/Renovate ile otomatik bump) ileride eklenmeli; floating `:10.0`
# yerine `:10.0.0-bookworm-slim` minor + flavor sabit kalır.
FROM mcr.microsoft.com/dotnet/sdk:10.0.0-bookworm-slim AS build
WORKDIR /app

# Bağımlılık katmanını önbelleğe al
# Directory.Packages.props: Central Package Management — csproj'lar Version belirtmiyor,
# tüm versiyonlar bu dosyadan gelir. COPY zincirinden düşerse `dotnet restore` NU1015 verir.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Saydin.Shared/Saydin.Shared.csproj src/Saydin.Shared/
COPY src/Saydin.Api/Saydin.Api.csproj src/Saydin.Api/
RUN dotnet restore src/Saydin.Api/Saydin.Api.csproj

# Kaynak kodu kopyala ve derle
COPY src/Saydin.Shared/ src/Saydin.Shared/
COPY src/Saydin.Api/ src/Saydin.Api/
RUN dotnet publish src/Saydin.Api/Saydin.Api.csproj \
    -c Release \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.0-bookworm-slim AS runtime
WORKDIR /app

# libgssapi-krb5-2: .NET runtime GSSAPI/Kerberos kütüphanesini yükler;
# yoksa ilk Npgsql SCRAM-SHA-256 handshake bozulur.
# curl: container-level HEALTHCHECK (review F1.7-6) — /health endpoint'ine HTTP probe.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 curl \
    && rm -rf /var/lib/apt/lists/*

# Güvenlik: root olmayan kullanıcı
RUN useradd -r -u 1001 -s /sbin/nologin appuser
USER appuser

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Container liveness — Program.cs `MapHealthChecks("/health")` cevap vermezse unhealthy.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Saydin.Api.dll"]
