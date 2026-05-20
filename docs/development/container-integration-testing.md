# Container-Based Integration Testing

Container-based integration testing is the **recommended** approach for running full BotNexus platform scenarios. Any machine with a container runtime (Docker, Podman, etc.) can start the complete gateway and run realistic end-to-end scenarios against it without a local .NET SDK install or a pre-built repository.

The existing process-based integration tests (`BotNexus.Integration.Tests` without `--gateway-url`) remain supported for local development workflows where starting a container is inconvenient.

## How It Works

1. A multi-stage `Dockerfile` at the repo root builds `BotNexus.Gateway.Api` and produces a small runtime image.
2. A `docker-compose.yml` wraps it for local developer use — mount your `~/.botnexus` directory and the gateway starts with your existing configuration.
3. The integration test runner (`BotNexus.Integration.Tests`) accepts a `--gateway-url` flag. When set it skips spawning a local gateway process and connects directly to the container.
4. A GitHub Actions workflow (`container-integration.yml`) builds the image in CI, starts the container with secrets injected as environment variables, then runs the scenario suite.

## Quick Start (Local)

### Prerequisites

- Docker Desktop (Windows/macOS) or Docker Engine (Linux) / Podman with Docker-compatible CLI
- Your `~/.botnexus/config.json` (and optionally `auth.json`) with provider credentials

### Start the gateway container

```bash
docker compose up --build
```

The gateway starts on `http://localhost:5000`. Open a browser and navigate to that URL to confirm it is running.

Stop it with `Ctrl+C` or `docker compose down`.

### Run scenarios against the container

```bash
# From the repo root — .NET SDK required for the test runner
dotnet run --project tests/BotNexus.Integration.Tests -- --gateway-url=http://localhost:5000

# Or run only the smoke test (no LLM calls)
dotnet run --project tests/BotNexus.Integration.Tests -- \
  --gateway-url=http://localhost:5000 \
  --scenario-dir=tests/container/scenarios

# Filter to a specific scenario by name fragment
dotnet run --project tests/BotNexus.Integration.Tests -- \
  --gateway-url=http://localhost:5000 agent-lifecycle
```

Alternatively, pass the gateway URL as an environment variable:

```bash
BOTNEXUS_GATEWAY_URL=http://localhost:5000 \
  dotnet run --project tests/BotNexus.Integration.Tests
```

## Starting the Container Manually (without Compose)

```bash
# Build the image
docker build -t botnexus:dev .

# Run with your local config mounted
docker run -d --name botnexus-gateway \
  -p 5000:5000 \
  -v ~/.botnexus:/app/config:ro \
  -e ASPNETCORE_ENVIRONMENT=Development \
  botnexus:dev

# Check it is healthy
curl http://localhost:5000/health

# Tail the logs
docker logs -f botnexus-gateway

# Stop and remove
docker stop botnexus-gateway && docker rm botnexus-gateway
```

## Supplying Provider Credentials

Credentials are resolved in this order:

1. `auth.json` in the mounted config directory (`/app/config/auth.json`)
2. Environment variables injected into the container

| Provider | Environment variable |
|---|---|
| GitHub Copilot | `GITHUB_TOKEN`, `GH_TOKEN`, or `COPILOT_GITHUB_TOKEN` |
| OpenAI | `OPENAI_API_KEY` |
| Anthropic | `ANTHROPIC_API_KEY` or `ANTHROPIC_OAUTH_TOKEN` |

Pass them at `docker run` time:

```bash
docker run -d --name botnexus-gateway \
  -p 5000:5000 \
  -v /path/to/config:/app/config:ro \
  -e GITHUB_TOKEN="${GITHUB_TOKEN}" \
  -e OPENAI_API_KEY="${OPENAI_API_KEY}" \
  botnexus:dev
```

Or add them to a `.env` file and reference it with `docker run --env-file .env`.

## Container Config Template

`tests/container/config.json` is a minimal gateway config committed to the repository. It declares the supported providers but contains no credentials — safe to commit. Use it as a starting point when you do not want to mount your personal `~/.botnexus` directory:

```bash
docker run -d --name botnexus-gateway \
  -p 5000:5000 \
  -v "$(pwd)/tests/container":/app/config:ro \
  -e GITHUB_TOKEN="${GITHUB_TOKEN}" \
  botnexus:dev
```

## Smoke Scenarios (No LLM Required)

`tests/container/scenarios/smoke.json` validates the gateway API surface without making LLM calls. These run in CI on every push without provider secrets:

```bash
dotnet run --project tests/BotNexus.Integration.Tests -- \
  --gateway-url=http://localhost:5000 \
  --scenario-dir=tests/container/scenarios
```

## CI

The `container-integration.yml` workflow has two jobs:

| Job | Trigger | Requires secrets |
|---|---|---|
| `container-smoke` | Every push/PR to `main` | No — uses `tests/container/scenarios/smoke.json` |
| `container-scenarios` | When repo variable `CONTAINER_INTEGRATION_ENABLED=true` and not a fork | Yes — `GH_TOKEN`, `OPENAI_API_KEY`, `ANTHROPIC_API_KEY` |

To enable full scenario runs in CI, set the repository variable `CONTAINER_INTEGRATION_ENABLED` to `true` in **Settings → Variables → Actions**, then add the required secrets.

## Scenario Cleanup in Container Mode

When running against a container the test runner automatically deletes any agents registered during a scenario (via `DELETE /api/agents/{id}`) once the scenario completes. This keeps the gateway state clean between scenarios without restarting the container.

Scenarios that include explicit `api_delete` steps (like `agent-lifecycle.json`) will perform that delete as part of their steps; the runner's cleanup is a safety net for any that do not.
