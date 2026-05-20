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

COPY --from=build /app/publish .

# BOTNEXUS_HOME is the config directory; mount a volume here with your config.json (and optionally auth.json).
# API keys can also be supplied via environment variables (e.g. GITHUB_TOKEN, OPENAI_API_KEY).
ENV BOTNEXUS_HOME=/app/config
ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Development

VOLUME ["/app/config"]

EXPOSE 5000

HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=5 \
    CMD curl -f http://localhost:5000/health || exit 1

ENTRYPOINT ["dotnet", "BotNexus.Gateway.Api.dll"]
