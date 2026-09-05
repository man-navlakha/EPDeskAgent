FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src

# EPDeskMcpServer reads the EF model and Backblaze client from EPDeskServerApi,
# so both project files are restored together. Copying the .csproj files first
# keeps the restore layer cached across source-only changes.
COPY EPDeskServerApi/EPDeskServerApi.csproj EPDeskServerApi/
COPY EPDeskMcpServer/EPDeskMcpServer.csproj EPDeskMcpServer/
RUN dotnet restore EPDeskMcpServer/EPDeskMcpServer.csproj

COPY EPDeskServerApi/ EPDeskServerApi/
COPY EPDeskMcpServer/ EPDeskMcpServer/

RUN dotnet publish EPDeskMcpServer/EPDeskMcpServer.csproj \
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

ENTRYPOINT ["dotnet", "EPDeskMcpServer.dll"]
