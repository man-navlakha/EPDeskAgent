FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src

COPY EPDeskExtractionSandbox/EPDeskExtractionSandbox.csproj EPDeskExtractionSandbox/
COPY EPDeskExtractionWorker/Services/Extraction/ EPDeskExtractionWorker/Services/Extraction/
RUN dotnet restore EPDeskExtractionSandbox/EPDeskExtractionSandbox.csproj

COPY EPDeskExtractionSandbox/ EPDeskExtractionSandbox/
RUN dotnet publish EPDeskExtractionSandbox/EPDeskExtractionSandbox.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS final
WORKDIR /app

ARG CLAMAV_VERSION=1.4.5
ARG CLAMAV_DEB_SHA256=49b7d9592c13e8834fd1e2339bf22b2fabe378317393b51b3879de8520c5b01f

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        passwd \
        poppler-utils \
        tesseract-ocr \
        tesseract-ocr-eng \
        util-linux \
    && curl --proto '=https' --tlsv1.2 --fail --location --retry 3 \
        --output /tmp/clamav.deb \
        "https://github.com/Cisco-Talos/clamav/releases/download/clamav-${CLAMAV_VERSION}/clamav-${CLAMAV_VERSION}.linux.x86_64.deb" \
    && echo "${CLAMAV_DEB_SHA256}  /tmp/clamav.deb" | sha256sum --check --strict - \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        /tmp/clamav.deb \
    && rm -f /tmp/clamav.deb \
    && rm -rf /var/lib/apt/lists/* \
    && if ! getent group clamav >/dev/null; then groupadd --system clamav; fi \
    && if ! id -u clamav >/dev/null 2>&1; then \
        useradd --system --gid clamav --home-dir /var/lib/clamav \
            --shell /usr/sbin/nologin clamav; \
       fi \
    && groupadd --system epdesk-scan \
    && usermod --append --groups epdesk-scan app \
    && usermod --append --groups epdesk-scan clamav \
    && install -d -o root -g clamav -m 0750 /etc/clamav \
    && install -d -o clamav -g clamav -m 0750 \
        /var/lib/clamav /var/log/clamav /tmp/epdesk-clamd \
    && install -d -o clamav -g epdesk-scan -m 0750 /run/clamav \
    && install -d -o app -g app -m 0700 /tmp/epdesk-extraction-sandbox

COPY --from=build /app/publish ./
COPY EPDeskExtractionSandbox/entrypoint.sh ./entrypoint.sh
RUN chmod 0555 ./entrypoint.sh

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0 \
    HOME=/tmp

USER root
EXPOSE 8080

STOPSIGNAL SIGTERM
ENTRYPOINT ["/app/entrypoint.sh"]
