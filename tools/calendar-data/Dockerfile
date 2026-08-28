# tag: 10.0 (mcr.microsoft.com/dotnet/sdk)
FROM mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /workspace

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Saydin.DatabaseSecurity/Saydin.DatabaseSecurity.csproj src/Saydin.DatabaseSecurity/packages.lock.json src/Saydin.DatabaseSecurity/
COPY tools/calendar-data/src/Saydin.CalendarData/Saydin.CalendarData.csproj tools/calendar-data/src/Saydin.CalendarData/packages.lock.json tools/calendar-data/src/Saydin.CalendarData/
COPY tools/calendar-data/tests/Saydin.CalendarData.Tests/Saydin.CalendarData.Tests.csproj tools/calendar-data/tests/Saydin.CalendarData.Tests/packages.lock.json tools/calendar-data/tests/Saydin.CalendarData.Tests/
RUN dotnet restore tools/calendar-data/tests/Saydin.CalendarData.Tests/Saydin.CalendarData.Tests.csproj \
        -p:RestoreLockedMode=true

COPY src/Saydin.DatabaseSecurity/ src/Saydin.DatabaseSecurity/
COPY tools/calendar-data/src/ tools/calendar-data/src/
COPY tools/calendar-data/tests/ tools/calendar-data/tests/
COPY tools/calendar-data/data/ tools/calendar-data/data/
COPY tools/calendar-data/Dockerfile tools/calendar-data/Dockerfile
COPY infrastructure/calendar/ infrastructure/calendar/
RUN dotnet test tools/calendar-data/tests/Saydin.CalendarData.Tests/Saydin.CalendarData.Tests.csproj \
        --configuration Release --no-restore \
    && dotnet run --project tools/calendar-data/src/Saydin.CalendarData/Saydin.CalendarData.csproj \
        --configuration Release --no-restore -- verify --data-root tools/calendar-data/data \
    && dotnet publish tools/calendar-data/src/Saydin.CalendarData/Saydin.CalendarData.csproj \
        --configuration Release --no-restore --output /out

# tag: 10.0 (mcr.microsoft.com/dotnet/runtime)
FROM mcr.microsoft.com/dotnet/runtime@sha256:a365ce6a50b09176855d085c69da3fc1204a48432e36087e9a208f6e5860e235 AS runtime
WORKDIR /workspace
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd -g 1001 appuser \
    && useradd -r -u 1001 -g 1001 -s /sbin/nologin appuser \
    && install -d -o appuser -g appuser -m 0700 /workspace/tools/calendar-data
COPY --from=build /out/ /app/calendar/
COPY --from=build /workspace/tools/calendar-data/data/ /workspace/tools/calendar-data/data/
USER appuser
ENTRYPOINT ["dotnet", "/app/calendar/Saydin.CalendarData.dll"]
CMD ["verify", "--data-root", "/workspace/tools/calendar-data/data"]
