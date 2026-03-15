FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

COPY Directory.Build.props ./
COPY src/Saydin.Shared/Saydin.Shared.csproj src/Saydin.Shared/
COPY src/Saydin.PriceIngestion/Saydin.PriceIngestion.csproj src/Saydin.PriceIngestion/
RUN dotnet restore src/Saydin.PriceIngestion/Saydin.PriceIngestion.csproj

COPY src/Saydin.Shared/ src/Saydin.Shared/
COPY src/Saydin.PriceIngestion/ src/Saydin.PriceIngestion/
RUN dotnet publish src/Saydin.PriceIngestion/Saydin.PriceIngestion.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

RUN adduser --disabled-password --gecos "" appuser
USER appuser

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Saydin.PriceIngestion.dll"]
