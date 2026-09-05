FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src

COPY EPDeskServerApi/EPDeskServerApi.csproj EPDeskServerApi/
COPY tools/EPDeskExtractionControl/EPDeskExtractionControl.csproj tools/EPDeskExtractionControl/
RUN dotnet restore tools/EPDeskExtractionControl/EPDeskExtractionControl.csproj

COPY EPDeskServerApi/ EPDeskServerApi/
COPY tools/EPDeskExtractionControl/ tools/EPDeskExtractionControl/
RUN dotnet publish tools/EPDeskExtractionControl/EPDeskExtractionControl.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS final
WORKDIR /app

COPY --from=build --chown=app:app /app/publish ./

ENV DOTNET_EnableDiagnostics=0

USER app

ENTRYPOINT ["dotnet", "EPDeskExtractionControl.dll", "enqueue-bulk", "--batch-size", "250"]
