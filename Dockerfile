# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for better layer caching
COPY BotNexus.slnx Directory.Build.props Directory.Packages.props ./
COPY src/ src/

RUN dotnet restore src/gateway/BotNexus.Gateway.Api/BotNexus.Gateway.Api.csproj

RUN dotnet publish src/gateway/BotNexus.Gateway.Api/BotNexus.Gateway.Api.csproj \
    -c Release -o /app/publish --no-restore

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

# BOTNEXUS_HOME is the config directory; mount a volume here with your config.json (and optionally auth.json).
# API keys can also be supplied via environment variables (e.g. GITHUB_TOKEN, OPENAI_API_KEY).
ENV BOTNEXUS_HOME=/app/config
ENV BOTNEXUS_DATA_DIR=/app/data
ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production

VOLUME ["/app/config", "/app/data"]

EXPOSE 5000

HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=5 \
    CMD curl -f http://localhost:5000/health || exit 1

ENTRYPOINT ["dotnet", "BotNexus.Gateway.Api.dll"]
