FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["backend/TrustVault.Api/TrustVault.Api.csproj", "backend/TrustVault.Api/"]
RUN dotnet restore "backend/TrustVault.Api/TrustVault.Api.csproj"

COPY backend/TrustVault.Api/ backend/TrustVault.Api/
WORKDIR "/src/backend/TrustVault.Api"
RUN dotnet build "TrustVault.Api.csproj" -c Release -o /app/build
RUN dotnet publish "TrustVault.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .

# Run as non-root user for security hardening
RUN mkdir -p /app/storage && chown -R $APP_UID:$APP_UID /app/storage
USER $APP_UID

ENTRYPOINT ["dotnet", "TrustVault.Api.dll"]
