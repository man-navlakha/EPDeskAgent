FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src

COPY EPDeskExtractionWorker.csproj ./
RUN dotnet restore EPDeskExtractionWorker.csproj

COPY . ./
RUN dotnet publish EPDeskExtractionWorker.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS final
WORKDIR /app

COPY --from=build --chown=app:app /app/publish ./

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0

USER app
EXPOSE 8080

ENTRYPOINT ["dotnet", "EPDeskExtractionWorker.dll"]
