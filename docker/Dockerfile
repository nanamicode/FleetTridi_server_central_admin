FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/FleetTridi.Server/FleetTridi.Server.csproj .
RUN dotnet restore FleetTridi.Server.csproj
COPY src/FleetTridi.Server/ .
RUN dotnet publish FleetTridi.Server.csproj -c Release -o /app/publish --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENV FLEETTRIDI_URLS=http://0.0.0.0:8787
VOLUME ["/app/data"]
EXPOSE 8787
ENTRYPOINT ["dotnet","FleetTridi.Server.dll"]
