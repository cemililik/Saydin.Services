# tag: 10.0 (mcr.microsoft.com/dotnet/sdk)
FROM mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/Saydin.DatabaseSecurity/Saydin.DatabaseSecurity.csproj src/Saydin.DatabaseSecurity/packages.lock.json src/Saydin.DatabaseSecurity/
COPY src/Saydin.DataRepair/Saydin.DataRepair.csproj src/Saydin.DataRepair/packages.lock.json src/Saydin.DataRepair/
RUN dotnet restore src/Saydin.DataRepair/Saydin.DataRepair.csproj -p:RestoreLockedMode=true

COPY src/Saydin.DatabaseSecurity/ src/Saydin.DatabaseSecurity/
COPY src/Saydin.DataRepair/ src/Saydin.DataRepair/
COPY src/Saydin.DatabaseMigrator/MigrationTrustRoot.cs src/Saydin.DatabaseMigrator/MigrationTrustRoot.cs
RUN dotnet publish src/Saydin.DataRepair/Saydin.DataRepair.csproj \
    -c Release --no-restore -o /out

# tag: 10.0 (mcr.microsoft.com/dotnet/runtime)
FROM mcr.microsoft.com/dotnet/runtime@sha256:a365ce6a50b09176855d085c69da3fc1204a48432e36087e9a208f6e5860e235
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/* \
    && useradd -r -u 1001 -s /sbin/nologin appuser \
    && install -d -o appuser -g appuser -m 0700 \
        /run/saydin-secrets /run/repair /run/repair/evidence \
        /var/lib/saydin/repair-receipts
COPY --from=build --chown=appuser:appuser /out/ ./
USER appuser
ENTRYPOINT ["dotnet", "Saydin.DataRepair.dll"]
