# Developer setup

The single guide for building, running, and developing BotNexus from source. If you're installing a released version, see [Install from release](getting-started-release.md).

---

## Prerequisites

| Requirement | Version | Notes |
|-------------|---------|-------|
| **.NET SDK** | 10.0+ | Run `dotnet --version` to verify. You need the SDK, not just the runtime. |
| **PowerShell** | 7.0+ | Windows ships with it. Linux/macOS: `brew install powershell` or your package manager. |
| **Git** | 2.x+ | Any recent version. |
| **GitHub Copilot subscription** | — | Required for the default Copilot provider's OAuth flow. |

---

## 1. Clone, build, and run

```powershell
git clone https://github.com/sytone/botnexus.git
cd botnexus
.\scripts\dev-loop.ps1
```

That's it. `dev-loop.ps1` builds the full solution, runs Gateway tests, and starts the Gateway — stopping at the first failure.

The Gateway starts at `http://localhost:5005` with the WebUI at the root URL.

> **First run?** BotNexus auto-creates `~/.botnexus/` with a default `config.json`. No manual setup needed.

Install the pre-commit hook (once, after cloning):

```powershell
.\scripts\install-pre-commit-hook.ps1
```

### Verify it's running

```powershell
curl http://localhost:5005/health
# Returns: {"status":"ok"}
```

---

## 2. Configure the Copilot provider

Edit `~/.botnexus/config.json`:

```json
{
  "gateway": {
    "listenUrl": "http://localhost:5005",
    "defaultAgentId": "assistant"
  },
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

On your first message to the agent, you'll see:

```text
Go to https://github.com/login/device and enter code: ABCD-1234
```

Open that URL, enter the code, and authorize. The token is saved to `~/.botnexus/auth.json` and refreshes automatically.

---

## 3. Open the WebUI and chat

Open `http://localhost:5005` in your browser. Type a message and press Enter.

Key features:

- **Real-time streaming** — responses appear as they generate
- **Model selector** — switch models per session
- **Command palette** — type `/help`, `/reset`, `/status`, or `/models`
- **Session management** — view, switch, and delete past conversations

---

## 4. Development workflow

Use `dev-loop.ps1` for the edit → build → test → run cycle:

```powershell
# Build + test + run (the standard workflow)
.\scripts\dev-loop.ps1

# Watch mode — auto-rebuild on source changes
.\scripts\dev-loop.ps1 -Watch

# Custom port
.\scripts\dev-loop.ps1 -Port 9090
```

### What changes need a restart?

| Change type | Restart needed? |
|---|---|
| Agent definitions, models, enabled/disabled | No — hot-reloads in ~500ms |
| Provider settings (API keys, base URLs) | No — hot-reloads |
| Agent workspace files (`SOUL.md`, `IDENTITY.md`, etc.) | No — takes effect on next message |
| `gateway.listenUrl` (port binding) | Yes |
| Extension DLL additions | Yes |

### Running tests

```powershell
# Full test suite (required before committing)
dotnet test BotNexus.slnx --nologo --tl:off

# Specific test project
dotnet test tests\BotNexus.Gateway.Tests

# Specific test by name
dotnet test BotNexus.slnx --filter "FullyQualifiedName~MyTestName"
```

All tests must pass before committing. The pre-commit hook enforces this.

---

## 5. Customize your agent

Each agent gets a workspace at `~/.botnexus/agents/{agent-name}/`:

| File | Purpose |
|------|---------|
| `SOUL.md` | Core personality and values |
| `IDENTITY.md` | Role, style, and behavioral constraints |
| `USER.md` | User preferences |
| `MEMORY.md` | Long-term distilled knowledge |

Edit these files to shape agent behavior. Changes take effect on the next message — no restart needed.

### Add more agents

Add entries under the `agents` key in `config.json`:

```json
{
  "agents": {
    "assistant": {
      "provider": "copilot",
      "model": "gpt-4.1",
      "enabled": true
    },
    "researcher": {
      "provider": "copilot",
      "model": "claude-opus-4.6",
      "enabled": true
    }
  }
}
```

Set the default agent with `gateway.defaultAgentId`.

---

## Key endpoints

| Path | Purpose |
|------|---------|
| `/` | Blazor WebUI (chat, configuration, agent management) |
| `/health` | Health check (no auth) |
| `/api/agents` | Agent management |
| `/api/chat` | Send messages (REST) |
| `/api/sessions` | Session management |
| `/api/config/validate` | Config validation |
| `/hub/gateway` | SignalR streaming hub |
| `/swagger` | API documentation browser |

---

## Provider reference

### GitHub Copilot (recommended for dev)

Uses OAuth device code flow. 26+ models across Claude, GPT, and Gemini families with automatic API format routing.

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

### OpenAI-compatible (local models, Azure, etc.)

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

## Configuration reference

### Home directory

BotNexus stores all user data at `~/.botnexus/`:

```text
~/.botnexus/
├── config.json          # Platform configuration
├── auth.json            # Provider credentials (OAuth tokens)
├── extensions/          # Dynamic extension assemblies
├── tokens/              # OAuth token storage
├── sessions/            # Conversation history (JSONL)
├── logs/                # Application logs
└── agents/              # Per-agent workspace directories
    └── {agent-name}/
        ├── SOUL.md
        ├── IDENTITY.md
        ├── USER.md
        └── MEMORY.md
```

Override with `$env:BOTNEXUS_HOME = "C:\custom\path"`.

### Configuration layering

Settings resolve in priority order (highest first):

1. **Environment variables** — `BotNexus__Gateway__ListenUrl`, etc.
2. **`~/.botnexus/config.json`** — User configuration
3. **`appsettings.json`** — Project defaults
4. **Code defaults** — Built-in constants

### Environment variables

| Variable | Purpose | Default |
|----------|---------|---------|
| `BOTNEXUS_HOME` | Home directory | `~/.botnexus` |
| `ASPNETCORE_ENVIRONMENT` | ASP.NET Core environment | `Development` (set by scripts) |
| `ASPNETCORE_URLS` | HTTP listen URL | `http://localhost:5005` |
| `BotNexus__ConfigPath` | Explicit config file path | `~/.botnexus/config.json` |

---

## Gateway auth

**Development mode:** When no API keys are configured, all requests are allowed without authentication.

**Production mode:** Add API keys under `gateway.apiKeys` to enable enforcement:

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

Pass the key via `X-Api-Key` header or `?apiKey=` query parameter. The `/health` and `/swagger` endpoints are exempt from auth.

---

## Extension development

BotNexus loads dynamic extensions from `~/.botnexus/extensions/`.

1. Create a class library targeting `net10.0`
2. Reference `BotNexus.Gateway.Abstractions`
3. Build and copy the DLL to `~/.botnexus/extensions/{providers|channels|tools}/`
4. Restart the Gateway

For the full guide with code examples, see [Extension development](extension-development.md).

---

## Scripts reference

All scripts live in `scripts/`.

| Script | Purpose | Key flags |
|--------|---------|-----------|
| `dev-loop.ps1` | Build + test + run cycle | `-Watch`, `-Port`, `-SkipBuild`, `-SkipTests` |
| `start-gateway.ps1` | Build and start the Gateway | `-Port`, `-SkipBuild` |
| `export-openapi.ps1` | Export OpenAPI spec to `docs/api/openapi.json` | `-Port`, `-OutputPath`, `-SkipBuild` |
| `install-pre-commit-hook.ps1` | Install Git pre-commit hook (run once) | — |

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| `dotnet: command not found` | Install .NET 10+ SDK from https://dotnet.microsoft.com/download |
| Build fails | `dotnet clean BotNexus.slnx; dotnet build BotNexus.slnx` |
| Port 5005 already in use | `.\scripts\dev-loop.ps1 -Port 8080` |
| Config file not found | Run the Gateway once — it auto-creates `~/.botnexus/` |
| OAuth code expired | Send another message to trigger a fresh device code |
| WebUI shows "Disconnected" | Restart: `.\scripts\dev-loop.ps1` |
| Config changes ignored | Most settings hot-reload. `listenUrl` changes require restart. |
| Tests fail | `dotnet clean BotNexus.slnx; dotnet build BotNexus.slnx --nologo --tl:off; dotnet test BotNexus.slnx --nologo --tl:off` |

---

## Next steps

- **[Configuration guide](configuration.md)** — Full reference for every config option
- **[Architecture overview](architecture/overview.md)** — How BotNexus works internally
- **[Extension development](extension-development.md)** — Build custom channels and tools
- **[CLI reference](cli-reference.md)** — All CLI commands
- **[API reference](api-reference.md)** — REST API documentation
