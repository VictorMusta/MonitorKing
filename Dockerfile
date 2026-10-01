# Serveur central MonitorKing (Linux). Les agents, eux, tournent sous Windows sur chaque PC surveillé.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/MonitorKing.Core/MonitorKing.Core.csproj src/MonitorKing.Core/
COPY src/MonitorKing.Server/MonitorKing.Server.csproj src/MonitorKing.Server/
RUN dotnet restore src/MonitorKing.Server/MonitorKing.Server.csproj
COPY src/MonitorKing.Core/ src/MonitorKing.Core/
COPY src/MonitorKing.Server/ src/MonitorKing.Server/
COPY src/MonitorKing.Dashboard/ src/MonitorKing.Dashboard/
RUN dotnet publish src/MonitorKing.Server/MonitorKing.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
# Fuseau horaire pour les rapports (heures locales) ; ICU est déjà présent pour les formats français.
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data && chown app:app /data
ENV TZ=Europe/Paris \
    MonitorKing__DataDirectory=/data \
    MonitorKing__Port=8080
WORKDIR /app
COPY --from=build /app .
USER app
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "MonitorKing.Server.dll"]
