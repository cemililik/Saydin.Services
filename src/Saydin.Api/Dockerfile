FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Bağımlılık katmanını önbelleğe al
COPY Directory.Build.props ./
COPY src/Saydin.Shared/Saydin.Shared.csproj src/Saydin.Shared/
COPY src/Saydin.Api/Saydin.Api.csproj src/Saydin.Api/
RUN dotnet restore src/Saydin.Api/Saydin.Api.csproj

# Kaynak kodu kopyala ve derle
COPY src/Saydin.Shared/ src/Saydin.Shared/
COPY src/Saydin.Api/ src/Saydin.Api/
RUN dotnet publish src/Saydin.Api/Saydin.Api.csproj \
    -c Release \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Güvenlik: root olmayan kullanıcı
RUN useradd -r -u 1001 -s /sbin/nologin appuser
USER appuser

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "Saydin.Api.dll"]
