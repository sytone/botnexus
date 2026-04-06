# Development Loop & Local Deployment

> Quick reference for the full edit → build → test → run → verify cycle. For complete onboarding, see [Getting Started from Source](getting-started-dev.md).

---

## Prerequisites

| Requirement | Version | Verify |
|---|---|---|
| .NET SDK | 10+ | `dotnet --version` |
| PowerShell | 7+ | `pwsh --version` |
| Git | Any recent | `git --version` |

Optional:

- **curl** — API endpoint testing
- **wscat** or **websocat** — WebSocket testing
- **GitHub Copilot subscription** — Required for the Copilot provider's OAuth flow

---

## Quick Start (5 Minutes)

```powershell
# 1. Build and run (build + test + start gateway)
.\scripts\dev-loop.ps1

# 2. Open WebUI in your browser
start http://localhost:5005/webui

# 3. Verify health
curl http://localhost:5005/health
# Returns: {"status":"ok"}
```

That's it. The Gateway is running on port 5005 with the WebUI ready for testing.

---

## The Full Dev Loop

```
 ┌──────────┐     ┌──────────┐     ┌──────────┐     ┌──────────┐     ┌──────────┐
 │  Edit    │ ──► │  Build   │ ──► │  Test    │ ──► │  Run     │ ──► │  Verify  │
 │  Code    │     │  Solution│     │  All     │     │  Gateway │     │  WebUI / │
 └──────────┘     └──────────┘     └──────────┘     └──────────┘     │  API     │
                                                                      └──────────┘
```

### Step 1: Edit

Modify files in the appropriate project:

| Area | Directory |
|---|---|
| Gateway API (routes, middleware) | `src/gateway/BotNexus.Gateway.Api/` |
| Gateway core (routing, config, hot reload) | `src/gateway/BotNexus.Gateway/` |
| Gateway contracts | `src/gateway/BotNexus.Gateway.Abstractions/` |
| Session persistence | `src/gateway/BotNexus.Gateway.Sessions/` |
| CLI tool | `src/gateway/BotNexus.Cli/` |
| WebUI (HTML/CSS/JS) | `src/BotNexus.WebUI/wwwroot/` |
| Channel adapters | `src/channels/` |

> **Frozen directories:** Do NOT modify `src/providers/`, `src/agent/`, or `src/coding-agent/`.

### Step 2: Build

```powershell
# Full solution (recommended — catches cross-project issues)
dotnet build BotNexus.slnx --nologo --tl:off

# Gateway API only (faster for gateway-only changes)
dotnet build src\gateway\BotNexus.Gateway.Api\BotNexus.Gateway.Api.csproj --nologo --tl:off

# Clean build (when stuck on stale artifacts)
dotnet clean BotNexus.slnx && dotnet build BotNexus.slnx --nologo --tl:off
```

### Step 3: Test

```powershell
# All tests
dotnet test BotNexus.slnx --nologo --tl:off

# Gateway tests only (fastest feedback)
dotnet test tests\BotNexus.Gateway.Tests --nologo --tl:off

# Specific test by name
dotnet test BotNexus.slnx --nologo --tl:off --filter "FullyQualifiedName~MyTestName"

# Skip build if you just built (faster)
dotnet test BotNexus.slnx --nologo --tl:off --no-build
```

Tests use an isolated `BOTNEXUS_HOME` so they don't touch your real `~/.botnexus/` directory.

**Test projects:**

| Project | Scope |
|---|---|
| `tests/BotNexus.Gateway.Tests` | Gateway routing, sessions, middleware |
| `tests/BotNexus.AgentCore.Tests` | Agent core logic, tool execution |
| `tests/BotNexus.CodingAgent.Tests` | Coding agent behaviors |
| `tests/BotNexus.Providers.Core.Tests` | Provider core abstractions |
| `tests/BotNexus.Providers.Copilot.Tests` | Copilot provider specifics |
| `tests/BotNexus.Providers.OpenAI.Tests` | OpenAI provider |
| `tests/BotNexus.Providers.Anthropic.Tests` | Anthropic provider |
| `tests/BotNexus.Providers.OpenAICompat.Tests` | OpenAI-compatible provider |

### Step 4: Run the Gateway

```powershell
# Option 1: Dev loop script — build + test + run (recommended)
.\scripts\dev-loop.ps1

# Option 2: Start gateway directly — build + run (skip tests)
.\scripts\start-gateway.ps1

# Option 3: Raw dotnet (manual environment setup)
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://localhost:5005"
dotnet run --project src\gateway\BotNexus.Gateway.Api
```

### Step 5: Verify

Once the Gateway is running:

| Endpoint | URL | Auth Required |
|---|---|---|
| Health check | `http://localhost:5005/health` | No |
| WebUI | `http://localhost:5005/webui` | No |
| Swagger (API docs) | `http://localhost:5005/swagger` | No |
| REST API | `http://localhost:5005/api/*` | Yes (if keys configured) |
| WebSocket | `ws://localhost:5005/ws` | Yes (if keys configured) |

```powershell
# Health check
curl http://localhost:5005/health
# {"status":"ok"}

# List agents
curl http://localhost:5005/api/agents
```

### Restart After Changes

1. **Stop the running Gateway** — press `Ctrl+C` in the terminal
2. **Rebuild and restart:**

```powershell
.\scripts\dev-loop.ps1
```

Or for faster iteration when you've already run tests:

```powershell
.\scripts\dev-loop.ps1 -SkipTests
```

---

## CLI Commands

The `botnexus` CLI tool provides quick management of configuration without editing JSON manually.

### Common Workflow

```powershell
# Initialize home directory on first setup
botnexus init

# List configured agents
botnexus agent list

# Add a new agent for testing
botnexus agent add test-agent --provider openai --model gpt-4o

# Update configuration
botnexus config set gateway.listenUrl http://localhost:8080

# Validate configuration
botnexus validate
```

### Quick Reference

| Command | Purpose |
|---|---|
| `botnexus init` | Create `~/.botnexus/` with defaults |
| `botnexus agent list` | Show all configured agents |
| `botnexus agent add <id>` | Add an agent |
| `botnexus agent remove <id>` | Remove an agent |
| `botnexus config get <key>` | Read a config value (e.g., `gateway.listenUrl`) |
| `botnexus config set <key> <value>` | Set a config value |
| `botnexus validate` | Validate configuration (or `--remote` for running gateway) |

For complete documentation, see [CLI Reference](cli-reference.md).

---

## Watch Mode

For continuous development with auto-rebuild on file changes:

```powershell
.\scripts\dev-loop.ps1 -Watch
```

This uses `dotnet watch` to automatically rebuild and restart the Gateway when source files change. No manual restart needed.

---

## CLI Tool

The `botnexus` CLI provides commands for managing configuration and agents without editing JSON files directly.

### Running the CLI

```powershell
# Run from source
dotnet run --project src\gateway\BotNexus.Cli -- <command>

# Or if installed as a .NET tool
botnexus <command>
```

### Key Commands

| Command | Description |
|---|---|
| `botnexus init` | Initialize `~/.botnexus/` with default config and directories |
| `botnexus validate` | Validate config.json (local or against running gateway with `--remote`) |
| `botnexus agent list` | List all configured agents |
| `botnexus agent add <id>` | Add an agent (`--provider`, `--model`, `--enabled` options) |
| `botnexus agent remove <id>` | Remove an agent from config |
| `botnexus config get <key>` | Read a config value by dotted path (e.g., `gateway.listenUrl`) |
| `botnexus config set <key> <value>` | Set a config value by dotted path |

Use `--verbose` on any command for additional output.

---

## Scripts Reference

### `scripts/dev-loop.ps1`

Full development cycle: build → test → run.

| Parameter | Default | Description |
|---|---|---|
| `-Port` | `5005` | Gateway listen port (1–65535) |
| `-Watch` | off | Enable `dotnet watch` for auto-rebuild |
| `-SkipBuild` | off | Skip the solution build step |
| `-SkipTests` | off | Skip Gateway tests |

**What it does:**

1. Builds the full solution (`BotNexus.slnx`) — unless `-SkipBuild`
2. Runs Gateway tests (`tests/BotNexus.Gateway.Tests`) — unless `-SkipTests`
3. Starts the Gateway (direct or watch mode)

If the build or tests fail, it **stops and does not start the Gateway**.

**Examples:**

```powershell
# Full cycle: build + test + run
.\scripts\dev-loop.ps1

# Custom port
.\scripts\dev-loop.ps1 -Port 8080

# Watch mode (auto-rebuild on file changes)
.\scripts\dev-loop.ps1 -Watch

# Fast restart (skip rebuild and tests — use after initial pass)
.\scripts\dev-loop.ps1 -SkipBuild -SkipTests

# Skip tests only (rebuild but don't wait for tests)
.\scripts\dev-loop.ps1 -SkipTests
```

### `scripts/start-gateway.ps1`

Quick start without tests: build → run.

| Parameter | Default | Description |
|---|---|---|
| `-Port` | `5005` | Gateway listen port (1–65535) |
| `-SkipBuild` | off | Skip the build step |

Sets `ASPNETCORE_ENVIRONMENT=Development` and `ASPNETCORE_URLS=http://localhost:{port}` automatically.

### `scripts/export-openapi.ps1`

Exports the OpenAPI spec from a running Gateway instance.

| Parameter | Default | Description |
|---|---|---|
| `-Port` | `15099` | Temporary port for the API during export |
| `-OutputPath` | `docs/api/openapi.json` | Where to save the spec |
| `-SkipBuild` | off | Skip the build step |

Starts the Gateway on a temporary port, fetches `/swagger/v1/swagger.json`, and saves it. The process is stopped automatically after export.

### `scripts/install-pre-commit-hook.ps1`

Installs a Git pre-commit hook that:

1. Builds the full solution
2. Runs Gateway tests
3. Blocks commits if either step fails

Bypass for docs-only commits: `git commit --no-verify`

---

## Authentication Setup

### Development Mode (No Keys)

By default, with no API keys configured, all endpoints are open. This is the quickest way to develop locally.

### Enabling API Key Auth

Add keys to `~/.botnexus/config.json`:

```json
{
  "gateway": {
    "apiKeys": {
      "dev-key": {
        "apiKey": "sk-dev-secret",
        "displayName": "Dev API Key"
      }
    }
  }
}
```

Then include the key in requests:

```powershell
# Via header
curl -H "X-Api-Key: sk-dev-secret" http://localhost:5005/api/agents

# Via query parameter
curl "http://localhost:5005/api/agents?apiKey=sk-dev-secret"
```

### Provider Authentication (auth.json)

Provider credentials (e.g., Copilot OAuth tokens) live in `~/.botnexus/auth.json`, separate from gateway API keys.

**Resolution order for provider credentials:**

1. `~/.botnexus/auth.json` — OAuth tokens and enterprise endpoints
2. Environment variables — `BOTNEXUS_COPILOT_APIKEY`, etc.
3. `config.json` provider section — `apiKey` field

Reference auth.json tokens in `config.json` with the `auth:` prefix:

```json
{
  "providers": {
    "copilot": {
      "apiKey": "auth:copilot",
      "baseUrl": "https://api.githubcopilot.com"
    }
  }
}
```

---

## Platform Configuration

### Config File Location

`~/.botnexus/config.json` — created on first Gateway run or manually.

Override with: `$env:BOTNEXUS_HOME = "C:\custom\path"`

### Minimal Config

```json
{
  "gateway": {
    "listenUrl": "http://localhost:5005",
    "defaultAgentId": "assistant"
  },
  "agents": {
    "assistant": {
      "provider": "copilot",
      "model": "gpt-4.1",
      "enabled": true
    }
  },
  "providers": {
    "copilot": {
      "apiKey": "auth:copilot",
      "baseUrl": "https://api.githubcopilot.com"
    }
  }
}
```

### Configuration Layering

Settings are resolved in priority order (highest first):

1. **Environment variables** — `BotNexus__Gateway__ListenUrl`, etc.
2. **`~/.botnexus/config.json`** — User configuration (primary)
3. **`appsettings.json`** — Project defaults (in `src/gateway/BotNexus.Gateway.Api/`)
4. **Code defaults** — Built-in constants

### Hot Reload

The Gateway watches `config.json` and automatically reloads changes after a 500ms debounce. No restart required for:

- Agent definitions (new agents, model changes, enabled/disabled)
- Provider settings (API keys, base URLs)
- API key changes

**Requires restart:** `gateway.listenUrl` changes (port rebinding).

### Home Directory Structure

```
~/.botnexus/
├── config.json          # Platform configuration
├── auth.json            # Provider credentials (OAuth tokens)
├── agents/              # Per-agent workspace directories
│   └── assistant/
│       ├── SOUL.md      # Core personality
│       ├── IDENTITY.md  # Role and constraints
│       ├── USER.md      # User preferences
│       └── MEMORY.md    # Long-term knowledge
├── sessions/            # Conversation history (JSONL)
├── tokens/              # OAuth tokens (if used)
├── extensions/          # Extension DLLs (if customized)
└── logs/                # Daily log files (botnexus-YYYY-MM-DD.log)
```

---

## Live Testing with Copilot

To test the full agent loop with a real LLM provider:

### 1. Configure the Copilot Provider

Ensure `~/.botnexus/config.json` has:

```json
{
  "providers": {
    "copilot": {
      "apiKey": "auth:copilot",
      "baseUrl": "https://api.githubcopilot.com",
      "defaultModel": "gpt-4.1"
    }
  },
  "agents": {
    "assistant": {
      "provider": "copilot",
      "model": "gpt-4.1",
      "enabled": true
    }
  }
}
```

### 2. Authenticate via OAuth

On first message to the agent, the Gateway triggers the OAuth device code flow:

1. Console prints: `Go to https://github.com/login/device and enter code: ABCD-1234`
2. Open the URL in your browser and enter the code
3. Click **Authorize** when prompted
4. Token is saved to `~/.botnexus/auth.json` and auto-refreshes

### 3. Test via WebUI

Open `http://localhost:5005/webui` and send a message. You should see streaming responses from the configured model.

### 4. Test via API

```powershell
# REST (non-streaming)
curl -X POST http://localhost:5005/api/chat `
  -H "Content-Type: application/json" `
  -d '{"agentId":"assistant","message":"Hello, what can you do?"}'
```

### 5. Test via WebSocket

```powershell
# Using wscat (npm install -g wscat)
wscat -c "ws://localhost:5005/ws?agent=assistant&session=test-session"
# Then type: {"type":"message","content":"Hello!"}
```

---

## WebUI Testing

Navigate to `http://localhost:5005/` or `http://localhost:5005/webui` in your browser.

The WebUI provides:

- **Real-time chat** with streaming responses
- **Model selector** — switch models per session
- **Tool call visibility toggle** — show/hide tool execution details
- **Session management** — browse, switch, and delete sessions
- **Command palette** — type `/` for commands (`/help`, `/reset`, `/status`, `/models`)

The WebUI is exempt from API key auth — it serves static files from `src/BotNexus.WebUI/wwwroot/`. WebSocket connections from the WebUI to `/ws` are subject to auth unless running in development mode.

Multiple browser tabs are supported for parallel conversations.

---

## Swagger (API Explorer)

Navigate to `http://localhost:5005/swagger` to browse the interactive API documentation.

From Swagger you can:

- View all REST endpoints with request/response schemas
- Send test requests directly from the browser
- See XML documentation comments from source code
- Download the OpenAPI spec for client generation

To export the spec as a file:

```powershell
.\scripts\export-openapi.ps1
# Saves to docs/api/openapi.json
```

---

## Environment Variables

| Variable | Purpose | Default |
|---|---|---|
| `BOTNEXUS_HOME` | Home directory for config, tokens, sessions | `~/.botnexus` |
| `ASPNETCORE_ENVIRONMENT` | ASP.NET Core environment | `Development` (set by scripts) |
| `ASPNETCORE_URLS` | HTTP listen URL | `http://localhost:5005` (set by scripts) |
| `BotNexus__ConfigPath` | Explicit config file path override | (uses `~/.botnexus/config.json`) |

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `dotnet: command not found` | .NET SDK not installed | Install .NET 10+ SDK |
| Build fails | Stale artifacts | `dotnet clean BotNexus.slnx && dotnet build BotNexus.slnx --nologo --tl:off` |
| Port 5005 in use | Another process on the port | Use `-Port 8080` or stop the other process |
| 401 on API calls | API keys configured but not sent | Add `X-Api-Key` header, or remove `apiKeys` from config for dev mode |
| 429 Too Many Requests | Agent concurrency limit reached | Increase `maxConcurrentSessions` or wait |
| WebSocket 4409 | Duplicate session connection | Close other tabs/clients using the same session |
| Config changes ignored | Watching limitation | Changes to `listenUrl` require a restart; other changes hot-reload |
| OAuth code expired | Took too long to authorize | Send another message to trigger a fresh device code |
| Extensions not loading | Wrong path | Check `ExtensionsPath` in config points to built DLLs |
| Tests fail intermittently | Stale build | `dotnet clean BotNexus.slnx && dotnet build BotNexus.slnx && dotnet test BotNexus.slnx` |
| Gateway starts, no agents | Missing config | Ensure `agents` and `providers` sections exist in `config.json` |
| WebUI shows "Disconnected" | Gateway not running | Restart: `.\scripts\start-gateway.ps1` |

---

## Further Reading

- [Getting Started from Source](getting-started-dev.md) — Full onboarding walkthrough
- [Developer Guide](dev-guide.md) — Comprehensive dev reference
- [Gateway README](../src/gateway/README.md) — Architecture, extension points, WebSocket protocol
- [Configuration Guide](configuration.md) — Complete config reference
- [Architecture Overview](architecture.md) — System design and message flow
- [Extension Development](extension-development.md) — Build custom providers, channels, and tools
