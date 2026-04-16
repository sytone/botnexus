# Developer Guide

A comprehensive guide for running BotNexus locally — for developers and AI agents who need to build, test, and operate the Gateway.

## Table of Contents

1. [Prerequisites](#prerequisites)
2. [Quick Start](#quick-start)
3. [Development Workflow](#development-workflow)
4. [Scripts Reference](#scripts-reference)
5. [Configuration](#configuration)
6. [Running the Gateway](#running-the-gateway)
7. [Testing](#testing)
8. [WebUI Access](#webui-access)
9. [Agent Configuration](#agent-configuration)
10. [Provider Setup](#provider-setup)
11. [Auth Setup](#auth-setup)
12. [Extension Development Workflow](#extension-development-workflow)
13. [OpenAPI Spec Export](#openapi-spec-export)
14. [Troubleshooting](#troubleshooting)

---

## Prerequisites

| Requirement | Version | Notes |
|-------------|---------|-------|
| **.NET SDK** | 10.0+ | Target framework is `net10.0`. Run `dotnet --version` to verify. |
| **PowerShell** | 7.0+ | Required for dev scripts. Windows ships with it; Linux/macOS install via `brew install powershell` or package manager. |
| **Git** | 2.x+ | For cloning and version control. |
| **OS** | Windows, Linux, or macOS | All platforms supported. Windows paths use backslashes; scripts use PowerShell (cross-platform). |

**Optional:**

| Tool | Purpose |
|------|---------|
| SignalR client | Validate `/hub/gateway` hub methods and events |
| `curl` | HTTP client for testing REST endpoints |
| A code editor | VS Code recommended for C# development |

---

## Quick Start

Three commands from clone to running Gateway:

```powershell
# 1. Clone the repository
git clone https://github.com/your-org/botnexus.git && cd botnexus

# 2. Build the solution
dotnet build BotNexus.slnx

# 3. Run the Gateway
.\scripts\start-gateway.ps1
```

The Gateway starts at `http://localhost:5005` with the WebUI served at the root URL.

> **First run?** BotNexus auto-creates `~/.botnexus/` with a default `config.json`. No manual setup needed for development mode.

---

## Development Workflow

The recommended edit → build → test → run cycle:

```text
┌──────────┐     ┌──────────┐     ┌──────────┐     ┌──────────┐
│  Edit    │ ──► │  Build   │ ──► │  Test    │ ──► │  Run     │
│  Code    │     │  Solution│     │  Gateway │     │  Gateway │
└──────────┘     └──────────┘     └──────────┘     └──────────┘
                      │                │                 │
                dotnet build     dotnet test     start-gateway.ps1
                BotNexus.slnx    Gateway.Tests    or dev-loop.ps1
```

### Single-Command Workflow

Use `dev-loop.ps1` to run the full cycle in one command:

```powershell
.\scripts\dev-loop.ps1
```

This builds the entire solution, runs Gateway tests, and starts the Gateway — stopping at the first failure.

### Watch Mode

For continuous development with auto-rebuild on file changes:

```powershell
.\scripts\dev-loop.ps1 -Watch
```

This uses `dotnet watch` to automatically rebuild and restart the Gateway when source files change.

### Manual Step-by-Step

```powershell
# Build the full solution
dotnet build BotNexus.slnx

# Run Gateway tests
dotnet test tests\BotNexus.Gateway.Tests

# Start the Gateway
.\scripts\start-gateway.ps1
```

---

## Scripts Reference

All scripts live in the `scripts/` directory.

### `start-gateway.ps1`

Builds and starts the Gateway API.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `-Port` | int | `5005` | HTTP listen port (1–65535) |
| `-SkipBuild` | switch | off | Skip the build step |

**What it does:**
1. Builds `src/gateway/BotNexus.Gateway.Api/BotNexus.Gateway.Api.csproj`
2. Sets `ASPNETCORE_ENVIRONMENT=Development`
3. Sets `ASPNETCORE_URLS=http://localhost:{port}`
4. Runs the Gateway with `dotnet run --no-build`

**Examples:**

```powershell
# Start on default port
.\scripts\start-gateway.ps1

# Start on custom port
.\scripts\start-gateway.ps1 -Port 8080
```

### `dev-loop.ps1`

Full build → test → run cycle for rapid development.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `-Port` | int | `5005` | HTTP listen port (1–65535) |
| `-Watch` | switch | off | Enable watch mode (auto-rebuild on changes) |
| `-SkipBuild` | switch | off | Skip the solution build step |
| `-SkipTests` | switch | off | Skip Gateway tests |

**What it does:**
1. Builds the full solution (`BotNexus.slnx`)
2. Runs Gateway tests (`tests/BotNexus.Gateway.Tests`)
3. If `-Watch`: starts `dotnet watch run` with auto-rebuild
4. Otherwise: delegates to `start-gateway.ps1`

**Examples:**

```powershell
# Full cycle: build + test + run
.\scripts\dev-loop.ps1

# Full cycle on custom port
.\scripts\dev-loop.ps1 -Port 9090

# Watch mode (auto-rebuild)
.\scripts\dev-loop.ps1 -Watch

# Fast restart (skip rebuild and tests)
.\scripts\dev-loop.ps1 -SkipBuild -SkipTests
```

**Output:**

```text
🔧 Building full solution...
✅ Build succeeded
🧪 Running Gateway tests...
✅ Tests passed
🚀 Starting Gateway API
   URL:        http://localhost:5005
   Environment: Development
```

### `export-openapi.ps1`

Exports the OpenAPI specification from the running Gateway API.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `-Port` | int | `15099` | Temporary port for the API during export |
| `-OutputPath` | string | `docs/api/openapi.json` | Where to save the spec |
| `-SkipBuild` | switch | off | Skip the build step |

**What it does:**
1. Builds the Gateway API project
2. Starts the API on a temporary port with an empty config (to prevent user config from hijacking the port binding)
3. Waits for the health check to pass
4. Fetches `/swagger/v1/swagger.json`
5. Saves the spec to `docs/api/openapi.json`
6. Stops the API process and cleans up temp files

**Examples:**

```powershell
# Export with defaults
.\scripts\export-openapi.ps1

# Export on a different port, skip build
.\scripts\export-openapi.ps1 -Port 15200 -SkipBuild
```

> **Note:** The script sets `BotNexus__ConfigPath` to an empty JSON file during export so `PlatformConfig.GetListenUrl()` returns null and doesn't override the `ASPNETCORE_URLS` port binding.

### `install-pre-commit-hook.ps1`

Installs a Git pre-commit hook for the repository. Run once after cloning.

---

## Configuration

### Config File Location

BotNexus stores its configuration at `~/.botnexus/config.json`. On first run, the `BotNexusHome.Initialize()` method creates the directory structure:

```text
~/.botnexus/
├── config.json          # Primary configuration file
├── extensions/          # Dynamic extension assemblies
├── tokens/              # OAuth token storage (e.g., copilot.json)
├── sessions/            # Conversation history (JSONL files)
├── logs/                # Application logs
└── agents/              # Per-agent workspace directories
    └── {agent-name}/
        ├── SOUL.md
        ├── IDENTITY.md
        ├── USER.md
        └── MEMORY.md
```

### Override Home Directory

Set the `BOTNEXUS_HOME` environment variable to use a custom location:

```powershell
$env:BOTNEXUS_HOME = "C:\my-botnexus"
```

### Minimal config.json

A working configuration with the Copilot provider:

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
      "isolationStrategy": "in-process",
      "enabled": true
    }
  },
  "providers": {
    "copilot": {
      "apiKey": "auth:copilot",
      "baseUrl": "https://api.githubcopilot.com",
      "defaultModel": "gpt-4.1"
    }
  }
}
```

### Configuration Layering

Settings are resolved in priority order (highest first):

1. **Environment variables** — `BotNexus__Gateway__ListenUrl`, `BotNexus__Gateway__DefaultAgentId`, etc.
2. **`~/.botnexus/config.json`** — User configuration (primary)
3. **`appsettings.json`** — Project defaults (in the Gateway API project)
4. **Code defaults** — Built-in constants

### Hot Reload

The Gateway watches `config.json` with a `FileSystemWatcher`. Changes are debounced (500ms) and applied automatically — no restart needed.

**What hot-reloads:**
- Agent definitions (new agents, model changes, enabled/disabled)
- Provider settings (API keys, base URLs, model defaults)
- Channel settings
- API key configuration

**What requires restart:**
- `listenUrl` changes (port binding)
- New isolation strategy registrations
- Extension DLL additions

---

## Running the Gateway

### Default Startup

```powershell
.\scripts\start-gateway.ps1
```

The Gateway starts with:
- **URL:** `http://localhost:5005`
- **Swagger:** `http://localhost:5005/swagger`
- **Health:** `http://localhost:5005/health`
- **Environment:** `Development`

### Environment Variables

| Variable | Purpose | Default |
|----------|---------|---------|
| `BOTNEXUS_HOME` | Home directory for config, tokens, sessions | `~/.botnexus` |
| `ASPNETCORE_ENVIRONMENT` | ASP.NET Core environment | `Development` (set by scripts) |
| `ASPNETCORE_URLS` | HTTP listen URL | `http://localhost:5005` (set by scripts) |
| `BotNexus__ConfigPath` | Explicit config file path | `~/.botnexus/config.json` |

### Direct dotnet run

If you prefer not to use scripts:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://localhost:5005"
dotnet run --project src\gateway\BotNexus.Gateway.Api
```

### Verify the Gateway Is Running

```powershell
# Health check (no auth required)
curl http://localhost:5005/health
# Returns: {"status":"ok"}

# List agents (auth required in production, open in dev mode)
curl http://localhost:5005/api/agents
```

### Key Endpoints

| Path | Purpose |
|------|---------|
| `/health` | Health check (no auth) |
| `/api/agents` | Agent management |
| `/api/chat` | Send messages (REST, non-streaming) |
| `/api/sessions` | Session management |
| `/api/config/validate` | Config validation |
| `/hub/gateway` | SignalR streaming hub |
| `/swagger` | API documentation browser |

---

## Testing

### Test Projects

| Project | Scope |
|---------|-------|
| `tests/BotNexus.Gateway.Tests` | Gateway routing, sessions, middleware |
| `tests/BotNexus.AgentCore.Tests` | Agent core logic, tool execution |
| `tests/BotNexus.CodingAgent.Tests` | Coding agent behaviors |
| `tests/BotNexus.Providers.Core.Tests` | Provider core abstractions |
| `tests/BotNexus.Providers.Copilot.Tests` | Copilot provider specifics |
| `tests/BotNexus.Providers.OpenAI.Tests` | OpenAI provider |
| `tests/BotNexus.Providers.Anthropic.Tests` | Anthropic provider |
| `tests/BotNexus.Providers.OpenAICompat.Tests` | OpenAI-compatible provider |

### Run All Tests

```powershell
dotnet test BotNexus.slnx
```

### Run Gateway Tests Only

```powershell
dotnet test tests\BotNexus.Gateway.Tests
```

### Run a Specific Test

```powershell
dotnet test BotNexus.slnx --filter "FullyQualifiedName~MyTestName"
```

### Watch Mode (Auto-Rerun on Changes)

```powershell
dotnet watch test --project tests\BotNexus.Gateway.Tests
```

### Test Output Options

```powershell
# Verbose output
dotnet test BotNexus.slnx --verbosity normal

# Show test names
dotnet test BotNexus.slnx --logger "console;verbosity=detailed"
```

---

## WebUI Access

The Gateway serves a real-time chat dashboard at the root URL (`/`).

### Accessing the WebUI

Open in your browser: `http://localhost:5005`

### Features

- **Real-time chat** with any configured agent
- **Model selector** — dropdown to switch models per session
- **Tool call visibility toggle** — show/hide tool execution details
- **Command palette** — type `/` for commands:
  - `/help` — available commands
  - `/reset` — reset conversation
  - `/status` — system status
  - `/models` — list available models
- **Session management** — view, switch, and delete sessions

### WebUI Auth

The WebUI is served as static files at the root URL and is **exempt from API key authentication** via the static file provider. SignalR connections from the WebUI to `/hub/gateway` are subject to auth unless running in development mode.

---

## Agent Configuration

Agents are defined in `~/.botnexus/config.json` under the `agents` key.

### Agent Definition Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `provider` | string | Yes | Provider name (e.g., `"copilot"`, `"openai"`, `"anthropic"`) |
| `model` | string | Yes | Model identifier (e.g., `"gpt-4.1"`, `"claude-opus-4.6"`) |
| `systemPromptFile` | string | No | Path to an external system prompt file |
| `isolationStrategy` | string | No | Execution strategy: `"in-process"` (default), `"sandbox"`, `"container"`, `"remote"` |
| `enabled` | bool | No | Whether the agent is active (default: `true`) |

### Example: Multiple Agents

```json
{
  "agents": {
    "assistant": {
      "provider": "copilot",
      "model": "gpt-4.1",
      "isolationStrategy": "in-process",
      "enabled": true
    },
    "researcher": {
      "provider": "copilot",
      "model": "claude-opus-4.6",
      "systemPromptFile": "prompts/researcher.txt",
      "isolationStrategy": "in-process",
      "enabled": true
    },
    "fast-helper": {
      "provider": "copilot",
      "model": "gpt-5.4-mini",
      "isolationStrategy": "in-process",
      "enabled": true
    }
  }
}
```

### Agent Workspace

Each agent gets a workspace at `~/.botnexus/agents/{agent-name}/` with bootstrap files:

| File | Purpose |
|------|---------|
| `SOUL.md` | Core personality and values |
| `IDENTITY.md` | Role, style, behavioral constraints |
| `USER.md` | User-specific preferences |
| `MEMORY.md` | Long-term distilled knowledge |

Edit these files to customize agent behavior. Changes take effect on the next conversation — no restart needed.

### Default Agent

Set `gateway.defaultAgentId` to specify which agent handles requests when no agent is specified:

```json
{
  "gateway": {
    "defaultAgentId": "assistant"
  }
}
```

---

## Provider Setup

Providers connect agents to upstream LLM APIs. Configure them under the `providers` key.

### Provider Config Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `apiKey` | string | Yes* | API key or auth reference (e.g., `"auth:copilot"`) |
| `baseUrl` | string | Yes* | API base URL |
| `defaultModel` | string | No | Default model when agent doesn't specify one |

\* At least one of `apiKey` or `baseUrl` is required.

### GitHub Copilot (OAuth)

Copilot uses OAuth device code flow for authentication.

**Step 1:** Configure the provider in `config.json`:

```json
{
  "providers": {
    "copilot": {
      "apiKey": "auth:copilot",
      "baseUrl": "https://api.githubcopilot.com",
      "defaultModel": "gpt-4.1"
    }
  }
}
```

**Step 2:** On first use, the OAuth flow runs automatically:
1. Console prints: `"Go to {VerificationUri} and enter code: {UserCode}"`
2. Open the URL in your browser and enter the code
3. Token is stored at `~/.botnexus/tokens/copilot.json`
4. Token auto-refreshes when expired

**Copilot models** — 26+ pre-registered models across multiple families:
- Claude: `claude-sonnet-4.5`, `claude-opus-4.6`, etc.
- GPT: `gpt-4o`, `gpt-4.1`, `gpt-5.4`, etc.
- Gemini: `gemini-2.5-pro`, etc.
- Each model declares its API format; routing is automatic.

### OpenAI

```json
{
  "providers": {
    "openai": {
      "apiKey": "sk-...",
      "baseUrl": "https://api.openai.com/v1",
      "defaultModel": "gpt-4o"
    }
  }
}
```

### Anthropic

```json
{
  "providers": {
    "anthropic": {
      "apiKey": "sk-ant-...",
      "baseUrl": "https://api.anthropic.com",
      "defaultModel": "claude-sonnet-4-20250514"
    }
  }
}
```

### OpenAI-Compatible

For any provider that implements the OpenAI API format (Azure OpenAI, local models, etc.):

```json
{
  "providers": {
    "local": {
      "apiKey": "not-needed",
      "baseUrl": "http://localhost:11434/v1",
      "defaultModel": "llama3"
    }
  }
}
```

---

## Auth Setup

BotNexus has two authentication layers: **provider auth** (Gateway → LLM APIs) and **gateway auth** (clients → Gateway).

### Provider Authentication

Controls how the Gateway authenticates with upstream LLM providers.

**Resolution order:**
1. `~/.botnexus/auth.json` — OAuth tokens and enterprise endpoints
2. Environment variables — `BOTNEXUS_COPILOT_APIKEY`, `BOTNEXUS_OPENAI_APIKEY`, etc.
3. `config.json` provider section — `apiKey` field

**Copilot OAuth (`~/.botnexus/auth.json`):**

```json
{
  "copilot": {
    "type": "oauth",
    "access": "ghu_...",
    "refresh": "ghr_...",
    "expires": 1234567890000,
    "endpoint": "https://api.githubcopilot.com"
  }
}
```

Reference it in `config.json` with `"apiKey": "auth:copilot"`.

### Gateway Endpoint Protection

The `GatewayAuthMiddleware` protects HTTP and SignalR endpoints.

**Development mode:** When no API keys are configured in `gateway.apiKeys`, all requests are allowed without authentication. Every caller gets admin-level access.

**Production mode:** Add at least one API key to enable enforcement:

```json
{
  "gateway": {
    "apiKeys": {
      "my-key": {
        "apiKey": "sk-my-secret-key",
        "tenantId": "dev",
        "callerId": "developer",
        "displayName": "Dev API Key",
        "allowedAgents": [],
        "permissions": ["chat:send", "sessions:read"],
        "isAdmin": true
      }
    }
  }
}
```

**How to pass the API key:**

```bash
# Via header
curl -H "X-Api-Key: sk-my-secret-key" http://localhost:5005/api/agents

# Via query parameter
curl "http://localhost:5005/api/agents?apiKey=sk-my-secret-key"
```

**Endpoints exempt from auth:**

| Path | Reason |
|------|--------|
| `/health` | Load balancer health checks |
| `/swagger` | API documentation browser |

### Multi-Tenant API Keys

Each key can restrict access to specific agents and permissions:

| Field | Type | Description |
|-------|------|-------------|
| `apiKey` | string | The secret key value |
| `tenantId` | string | Logical tenant grouping |
| `callerId` | string | Caller identifier for audit logs |
| `displayName` | string | Human-readable label |
| `allowedAgents` | string[] | Agent IDs this key can access (empty = all) |
| `permissions` | string[] | Granted scopes (e.g., `"chat:send"`, `"sessions:read"`) |
| `isAdmin` | bool | Full unrestricted access |

---

## Extension Development Workflow

BotNexus supports dynamic extensions for providers, channels, and tools loaded from the `~/.botnexus/extensions/` directory.

### Quick Start

1. Create a new class library targeting `net10.0`
2. Reference `BotNexus.Gateway.Abstractions` for channel/provider interfaces
3. Build the extension:

```powershell
dotnet build path\to\MyExtension.csproj
```

4. Copy the output to the extensions directory:

```text
~/.botnexus/extensions/
├── providers/    # Provider extension DLLs
├── channels/     # Channel adapter DLLs
└── tools/        # Tool extension DLLs
```

5. Restart the Gateway to load the new extension.

### Verifying Extensions

After starting the Gateway, confirm your extension is loaded:

```powershell
# List loaded extensions
curl http://localhost:5005/api/extensions

# List registered channels (for channel extensions)
curl http://localhost:5005/api/channels
```

For the full extension development guide with code examples, see [Extension Development](extension-development.md).

---

## OpenAPI Spec Export

Export the Gateway's OpenAPI specification for API documentation or client generation:

```powershell
.\scripts\export-openapi.ps1
```

This generates `docs/api/openapi.json` containing all 15+ REST endpoint definitions with XML doc descriptions. Use this spec with tools like Swagger UI, Redocly, or client generators (e.g., `nswag`, `openapi-generator`).

The exported spec is also browsable at `http://localhost:5005/swagger` when the Gateway is running.

---

## Troubleshooting

### Build Fails

```powershell
# Clean and rebuild
dotnet clean BotNexus.slnx
dotnet build BotNexus.slnx

# If NuGet issues, restore packages explicitly
dotnet restore BotNexus.slnx
```

### Port Already in Use

```powershell
# Find what's using port 5005
netstat -ano | findstr :5005

# Use a different port
.\scripts\start-gateway.ps1 -Port 8080
```

### Config File Not Found

```powershell
# Check if ~/.botnexus exists
Test-Path ~/.botnexus/config.json

# BotNexus creates it on first run. To force creation:
dotnet run --project src\gateway\BotNexus.Gateway.Api
# The BotNexusHome.Initialize() method creates the directory structure.
```

### OAuth Token Expired

```powershell
# Delete the expired token — next request triggers re-auth
Remove-Item ~/.botnexus/tokens/copilot.json
```

### Gateway Starts but No Agents Available

Check your `config.json`:
1. Ensure at least one agent is defined under `agents`
2. Ensure the agent's `provider` matches a key under `providers`
3. Ensure `enabled` is `true` (or absent — defaults to true)
4. Validate config: `curl http://localhost:5005/api/config/validate`

### Tests Fail

```powershell
# Run with verbose output to see failure details
dotnet test BotNexus.slnx --verbosity normal

# Run only the failing project
dotnet test tests\BotNexus.Gateway.Tests --verbosity normal
```

### Extension DLLs Not Loading

Verify extension assemblies are in the correct subdirectories:

```text
~/.botnexus/extensions/
├── providers/    # Provider DLLs
├── channels/     # Channel adapter DLLs
└── tools/        # Tool extension DLLs
```

---

## See Also

- [Architecture Overview](architecture/overview.md) — System design and component responsibilities
- [API Reference](api-reference.md) — REST and SignalR endpoint documentation
- [Configuration Guide](configuration.md) — Complete field-by-field config.json reference
- [SignalR Protocol](websocket-protocol.md) — SignalR hub contract reference
- [Extension Development](extension-development.md) — Building custom providers, channels, and tools
- [Getting Started](getting-started.md) — First-time user guide with end-to-end walkthrough
