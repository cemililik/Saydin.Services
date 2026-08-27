# CFGR-003 follow-up: SHA digest pin (immutable). Sonar S6596: image
# referansında tag VE digest birlikte kullanılmaz — digest tek başına yeterli
# (immutable kanonik referans). Dependabot Docker ecosystem `# tag: v10.0`
# yorumlarını da bump edebilir; tag bilgisi yorumda saklanır.
# tag: 10.0 (mcr.microsoft.com/dotnet/sdk)
FROM mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /app

# Bağımlılık katmanını önbelleğe al
# Directory.Packages.props: Central Package Management — csproj'lar Version belirtmiyor,
# tüm versiyonlar bu dosyadan gelir. COPY zincirinden düşerse `dotnet restore` NU1015 verir.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Saydin.DatabaseSecurity/Saydin.DatabaseSecurity.csproj src/Saydin.DatabaseSecurity/
COPY src/Saydin.Shared/Saydin.Shared.csproj src/Saydin.Shared/
COPY src/Saydin.Api/Saydin.Api.csproj src/Saydin.Api/
RUN dotnet restore src/Saydin.Api/Saydin.Api.csproj

# Kaynak kodu kopyala ve derle
COPY src/Saydin.DatabaseSecurity/ src/Saydin.DatabaseSecurity/
COPY src/Saydin.Shared/ src/Saydin.Shared/
COPY src/Saydin.Api/ src/Saydin.Api/
RUN dotnet publish src/Saydin.Api/Saydin.Api.csproj \
    -c Release \
    -o /app/publish

# tag: 10.0 (mcr.microsoft.com/dotnet/aspnet)
FROM mcr.microsoft.com/dotnet/aspnet@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94 AS runtime
WORKDIR /app

# libgssapi-krb5-2: .NET runtime GSSAPI/Kerberos kütüphanesini yükler;
# yoksa ilk Npgsql SCRAM-SHA-256 handshake bozulur.
# curl: container-level HEALTHCHECK — dependency-free public liveness probe.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 curl \
    && rm -rf /var/lib/apt/lists/*

# Güvenlik: root olmayan kullanıcı
RUN useradd -r -u 1001 -s /sbin/nologin appuser
USER appuser

COPY --from=build /app/publish .

EXPOSE 8080 9090
ENV ASPNETCORE_ENVIRONMENT=Production

# Container liveness is deliberately independent of PostgreSQL and Redis readiness.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "Saydin.Api.dll"]
