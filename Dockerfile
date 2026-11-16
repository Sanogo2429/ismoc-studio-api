# ---- Build ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 1) Fichiers projet seuls : la restauration NuGet est mise en cache tant qu'ils ne changent pas.
COPY ["IsMocStudio.api.csproj", "./"]
COPY ["IsMocStudio.Core/IsMocStudio.Core.csproj", "IsMocStudio.Core/"]
COPY ["IsMocStudio.Business/IsMocStudio.Business.csproj", "IsMocStudio.Business/"]
COPY ["IsMocStudio.Data/IsMocStudio.Data.csproj", "IsMocStudio.Data/"]
# Cible explicite : le dossier contient aussi IsMocStudio.sln (sinon MSB1011).
RUN dotnet restore "IsMocStudio.api.csproj"

# 2) Code source, puis publication du seul projet API (et de ses dépendances).
COPY . .
RUN dotnet publish "IsMocStudio.api.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- Exécution ------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Dossier des données (conversations, pièces jointes, livrables) accessible à l'utilisateur non root.
RUN mkdir -p /app/App_Data && chown -R app:app /app/App_Data
USER app

ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
# Render fournit le port dans $PORT ; 8080 par défaut en local.
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} exec dotnet IsMocStudio.api.dll"]
