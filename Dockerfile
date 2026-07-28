# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for better layer caching
COPY BotNexus.slnx Directory.Build.props Directory.Packages.props ./
COPY src/ src/

RUN dotnet restore src/gateway/BotNexus.Gateway.Api/BotNexus.Gateway.Api.csproj

RUN dotnet publish src/gateway/BotNexus.Gateway.Api/BotNexus.Gateway.Api.csproj \
    -c Release -o /app/publish --no-restore

# Publish the first-party extensions into an image-resident probe directory (#2376).
# Publishing only the gateway shipped an image with NO extensions at all: no SignalR
# hub, therefore no portal and no realtime channel, and GET /api/extensions returned [].
# Extensions are discovered per-directory from a manifest, so each extension gets its own
# self-contained output folder mirroring the local `botnexus gateway start` deploy layout.
# The manifest is copied explicitly rather than relying on each csproj declaring it as a
# Content item, because several extension projects do not.
RUN set -eux; \
    for manifest in $(find src/extensions -name botnexus-extension.json); do \
        projectDir=$(dirname "$manifest"); \
        project=$(find "$projectDir" -maxdepth 1 -name '*.csproj' | head -n 1); \
        [ -n "$project" ] || continue; \
        name=$(basename "$projectDir"); \
        dotnet publish "$project" -c Release -o "/app/extensions/$name"; \
        cp "$manifest" "/app/extensions/$name/botnexus-extension.json"; \
    done

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# The aspnet:10.0 base image ships neither curl nor wget (only dotnet), so the
# HEALTHCHECK below needs curl installed explicitly. Without it the probe exits 1
# on every interval and the container is reported unhealthy forever even though
# GET /health returns 200. See issue #1432.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Extensions live on the image itself, NOT under /app/config. /app/config is a declared
# VOLUME, so anything baked there is shadowed by the caller's mount on a stock `docker run`
# and the gateway starts with zero extensions. See issue #2376.
COPY --from=build /app/extensions ./extensions

# BOTNEXUS_HOME is the config directory; mount a volume here with your config.json (and optionally auth.json).
# API keys can also be supplied via environment variables (e.g. GITHUB_TOKEN, OPENAI_API_KEY).
ENV BOTNEXUS_HOME=/app/config
ENV BOTNEXUS_DATA_DIR=/app/data
# Extension probe root. Overrides the default {BOTNEXUS_HOME}/extensions so the shipped
# extensions are discovered even when /app/config is mounted. An explicit
# gateway.extensions.path in config.json still wins over this.
ENV BOTNEXUS_EXTENSIONS_PATH=/app/extensions
ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production

VOLUME ["/app/config", "/app/data"]

EXPOSE 5000

HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=5 \
    CMD curl -f http://localhost:5000/health || exit 1

ENTRYPOINT ["dotnet", "BotNexus.Gateway.Api.dll"]
