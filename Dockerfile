# $BUILDPLATFORM keeps the SDK stage on the runner's native architecture so
# the (slow) build never runs under QEMU emulation. Only the runtime layer is
# emitted for $TARGETARCH via `dotnet publish -a`.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# TARGETARCH is injected by buildx (amd64 / arm64); `dotnet publish -a`
# accepts these Docker arch names directly.
ARG TARGETARCH

WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json SbeB3Exchange.slnx ./
COPY schemas/ schemas/
COPY src/ src/

RUN dotnet restore src/B3.Exchange.Host/B3.Exchange.Host.csproj -a $TARGETARCH
RUN dotnet publish src/B3.Exchange.Host/B3.Exchange.Host.csproj \
    -a $TARGETARCH -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# wget is needed by the HEALTHCHECK directive below; the aspnet base image
# does not ship it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

# Issue #260: persistence snapshots (when the channel config opts in via
# a `persistence.dataDir` block) are written here. Mount a durable volume
# at this path (e.g. `-v b3matching-state:/var/lib/b3matching`) for
# restart-safety; without a volume the directory lives inside the
# container's writable layer and is wiped on `docker rm`.
VOLUME ["/var/lib/b3matching"]

# Default config is mounted into /app/config by docker-compose; override with
# CMD if needed.
EXPOSE 9876
EXPOSE 8080

STOPSIGNAL SIGTERM

# Healthcheck hits the Kestrel /health/live endpoint. Requires the host
# config's `http` block to be present (default port 8080). Disable by
# overriding HEALTHCHECK in a derived image if HTTP is turned off.
HEALTHCHECK --interval=10s --timeout=3s --start-period=10s --retries=3 \
    CMD wget -qO- http://127.0.0.1:8080/health/live || exit 1

ENTRYPOINT ["/app/B3.Exchange.Host"]
CMD ["/app/config/exchange-simulator.json"]
