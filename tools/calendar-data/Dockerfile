# Native multi-arch .NET 10.0.400 SDK/runtime 10.0.11, pinned by manifest-list digest.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c

WORKDIR /workspace

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Saydin.DatabaseSecurity src/Saydin.DatabaseSecurity
COPY tools/calendar-data/Directory.Packages.props tools/calendar-data/Directory.Packages.props
COPY tools/calendar-data/src tools/calendar-data/src
COPY tools/calendar-data/tests tools/calendar-data/tests
COPY tools/calendar-data/data tools/calendar-data/data
COPY infrastructure/calendar infrastructure/calendar

RUN dotnet restore tools/calendar-data/tests/Saydin.CalendarData.Tests/Saydin.CalendarData.Tests.csproj \
    && dotnet test tools/calendar-data/tests/Saydin.CalendarData.Tests/Saydin.CalendarData.Tests.csproj \
        --configuration Release --no-restore \
    && dotnet run --project tools/calendar-data/src/Saydin.CalendarData/Saydin.CalendarData.csproj \
        --configuration Release --no-restore -- verify --data-root tools/calendar-data/data \
    && dotnet publish tools/calendar-data/src/Saydin.CalendarData/Saydin.CalendarData.csproj \
        --configuration Release --no-restore --output /app/calendar

RUN useradd -r -u 1001 -s /sbin/nologin appuser
USER appuser

ENTRYPOINT ["dotnet", "/app/calendar/Saydin.CalendarData.dll"]
CMD ["verify", "--data-root", "/workspace/tools/calendar-data/data"]
