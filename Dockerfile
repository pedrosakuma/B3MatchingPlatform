FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY Directory.Build.props global.json SbeB3Exchange.slnx ./
COPY schemas/ schemas/
COPY src/ src/

RUN dotnet restore src/B3.Exchange.Host/B3.Exchange.Host.csproj
RUN dotnet publish src/B3.Exchange.Host/B3.Exchange.Host.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app
COPY --from=build /app .

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
