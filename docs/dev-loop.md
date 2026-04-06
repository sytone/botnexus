# Development Loop & Local Deployment

> Quick reference for building, testing, and running BotNexus locally. For full onboarding, see [Getting Started from Source](getting-started-dev.md).

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

## Building

### Full Solution

```bash
dotnet build BotNexus.slnx
```

### Gateway Only

```bash
dotnet build src/gateway/BotNexus.Gateway.Api/BotNexus.Gateway.Api.csproj
```

### Clean Build

```bash
dotnet clean BotNexus.slnx && dotnet build BotNexus.slnx
```

---

## Testing

### Run All Tests

```bash
dotnet test
```

### Gateway Tests Only

```bash
dotnet test tests/BotNexus.Gateway.Tests
```

### Verbose Output

```bash
dotnet test --verbosity detailed
```

Tests use an isolated `BOTNEXUS_HOME` environment variable so they don't touch your real `~/.botnexus/` directory.

---

## Running the Gateway Locally

### Option 1: Dev Loop Script (Recommended)

The `dev-loop.ps1` script builds the full solution, runs Gateway tests, and starts the server:

```powershell
.\scripts\dev-loop.ps1
```

**With watch mode** — auto-rebuilds when source files change:

```powershell
.\scripts\dev-loop.ps1 -Watch
```

**Custom port:**

```powershell
.\scripts\dev-loop.ps1 -Port 8080
```

### Option 2: Start Gateway Directly

Skip tests and just start the Gateway:

```powershell
.\scripts\start-gateway.ps1
```

With custom port:

```powershell
.\scripts\start-gateway.ps1 -Port 8080
```

### Option 3: Raw dotnet

```bash
dotnet run --project src/gateway/BotNexus.Gateway.Api
```

### Default Endpoints

Once running, the Gateway is available at:

| Endpoint | URL | Auth Required |
|---|---|---|
| Health check | `http://localhost:5005/health` | No |
| WebUI | `http://localhost:5005/webui` | No |
| Swagger (API docs) | `http://localhost:5005/swagger` | No |
| REST API | `http://localhost:5005/api/*` | Yes (if keys configured) |
| WebSocket | `ws://localhost:5005/ws` | Yes (if keys configured) |

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

```bash
# Via header
curl -H "X-Api-Key: sk-dev-secret" http://localhost:5005/api/agents

# Via query parameter
curl "http://localhost:5005/api/agents?apiKey=sk-dev-secret"
```

### Provider Authentication

Provider credentials (e.g., Copilot OAuth) are configured separately from gateway API keys and stored in `~/.botnexus/auth.json`.

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

### Hot Reload

The Gateway watches `config.json` and automatically reloads changes after a 500ms debounce. No restart required for:

- Agent definitions
- Provider settings
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
└── logs/                # Daily log files
```

---

## WebUI Access

Navigate to `http://localhost:5005/` or `http://localhost:5005/webui` in your browser.

The WebUI provides:

- **Real-time chat** with streaming responses
- **Session management** — browse and continue past conversations
- **Agent selection** — switch between configured agents
- **API explorer** — view REST API at `/swagger`
- **Health status** — check Gateway health at `/health`

Multiple browser tabs are supported for parallel conversations.

---

## Swagger (API Explorer)

Navigate to `http://localhost:5005/swagger` to browse the interactive API documentation.

From Swagger you can:

- View all REST endpoints with request/response schemas
- Send test requests directly from the browser
- See XML documentation comments from source code
- Download the OpenAPI spec for client generation

---

## Scripts Reference

### `scripts/dev-loop.ps1`

Full development cycle: build → test → run.

| Parameter | Default | Description |
|---|---|---|
| `-Port` | 5005 | Gateway listen port |
| `-Watch` | off | Enable `dotnet watch` for auto-rebuild |

**What it does:**

1. Builds the full solution (`BotNexus.slnx`)
2. Runs Gateway tests (`tests/BotNexus.Gateway.Tests`)
3. Starts the Gateway (direct or watch mode)

If tests fail, it **stops and does not start the Gateway**.

### `scripts/start-gateway.ps1`

Quick start without tests: build → run.

| Parameter | Default | Description |
|---|---|---|
| `-Port` | 5005 | Gateway listen port |

Sets `ASPNETCORE_ENVIRONMENT=Development` automatically.

### `scripts/install-pre-commit-hook.ps1`

Installs a Git pre-commit hook that:

1. Builds the full solution
2. Runs unit tests
3. Blocks commits if either step fails

Bypass for docs-only commits: `git commit --no-verify`

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `dotnet: command not found` | .NET SDK not installed | Install .NET 10+ SDK |
| Build fails | Stale artifacts | `dotnet clean && dotnet build` |
| Port 5005 in use | Another process on the port | Use `-Port 8080` or stop the other process |
| 401 on API calls | API keys configured but not sent | Add `X-Api-Key` header, or remove `apiKeys` from config for dev mode |
| 429 Too Many Requests | Agent concurrency limit reached | Increase `maxConcurrentSessions` or wait |
| WebSocket 4409 | Duplicate session connection | Close other tabs/clients using the same session |
| Config changes ignored | Watching limitation | Changes to `listenUrl` require a restart; other changes hot-reload |
| OAuth code expired | Took too long to authorize | Send another message to trigger a fresh device code |
| Extensions not loading | Wrong path | Check `ExtensionsPath` in config points to built DLLs |
| Tests fail intermittently | Stale build | Clean and rebuild: `dotnet clean && dotnet build && dotnet test` |

---

## Further Reading

- [Getting Started from Source](getting-started-dev.md) — Full onboarding walkthrough
- [Gateway README](../src/gateway/README.md) — Architecture, extension points, WebSocket protocol
- [Configuration Guide](configuration.md) — Complete config reference
- [Architecture Overview](architecture.md) — System design and message flow
- [Extension Development](extension-development.md) — Build custom providers, channels, and tools
