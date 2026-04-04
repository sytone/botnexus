# BotNexus

A modular, extensible AI agent execution platform built in C#/.NET. BotNexus enables running multiple AI agents concurrently, each powered by configurable LLM providers, receiving messages from multiple channels, and executing tools dynamically.

## 📖 [Getting Started Guide →](docs/getting-started.md)

New to BotNexus? The **Getting Started** guide walks you from clone → build → running with AI agents in minutes. If you're migrating from OpenClaw, it covers that too.

---

## Key Features

- **Multi-Agent** — Run multiple agents with independent configs (model, provider, system prompt, tools)
- **Multi-Channel** — Discord, Slack, Telegram, WebSocket, and REST API
- **Multi-Provider** — GitHub Copilot (26 models: Claude, GPT, GPT-5, Gemini, Grok via model-aware routing), OpenAI, Anthropic, Azure OpenAI
- **Model-Aware Routing** — Each model defines its API format (Anthropic Messages, OpenAI Completions, OpenAI Responses); requests route to the correct handler automatically
- **Extensible** — Dynamic assembly loading with folder-based extension system
- **Skills System** — Modular knowledge packages for agents (git workflows, coding standards, best practices)
- **MCP Support** — Model Context Protocol servers (stdio and SSE transports)
- **Session Persistence** — Conversation history persisted to disk (JSONL format)
- **Observable** — Correlation IDs, health checks, real-time activity stream via WebUI
- **CLI Tool** — `botnexus` command-line interface for config, agents, providers, doctor, and Gateway lifecycle
- **Diagnostics** — 13 health checkups across 6 categories with auto-fix support (`botnexus doctor`)
- **Hot Reload** — Edit `config.json` and changes apply live (agents, providers, cron) — no restart needed
- **REST API** — Agent CRUD, session management, skills, system status endpoints
- **WebUI** — Real-time chat with model selector, tool visibility toggle, and command palette (`/help`, `/reset`, `/status`, `/models`)
- **Tool Control** — Disable tools per agent via `DisallowedTools` config
- **Skill Control** — Disable skills per agent via `DisabledSkills` config (supports wildcards)
- **Loop Detection** — Configurable safeguards against infinite tool call loops (`MaxRepeatedToolCalls`)
- **Model Logging** — Actual model used logged per provider call for debugging and observability
- **Config Audit** — Config changes backed up to `.bak`, OAuth token operations logged
- **Agent Templates** — Auto-bootstrapped workspace with SOUL.md, IDENTITY.md, USER.md, HEARTBEAT.md, MEMORY.md

## Quick Start

```bash
# Build the solution
dotnet build BotNexus.slnx

# Run the Gateway
dotnet run --project src/BotNexus.Gateway
```

### CLI Tool

BotNexus includes a command-line tool for managing configuration, agents, providers, diagnostics, and the Gateway lifecycle:

```bash
# Install as a .NET tool
dotnet tool install --global --add-source ./src/BotNexus.Cli/bin/Release/net10.0 botnexus

# Or run directly from source
dotnet run --project src/BotNexus.Cli -- doctor
```

Key commands: `botnexus config validate`, `botnexus doctor`, `botnexus status`, `botnexus start`, `botnexus stop`. Run `botnexus --help` for the full list.

On first run, BotNexus creates `~/.botnexus/` with a default `config.json`. Edit this file to configure providers, channels, and agents:

```
~/.botnexus/
├── config.json          # Your configuration (primary)
├── extensions/          # Channel, provider, and tool plugins
├── tokens/              # OAuth token storage
├── workspace/sessions/  # Conversation history
└── logs/
```

### Minimal Configuration (`~/.botnexus/config.json`)

```json
{
  "BotNexus": {
    "Agents": {
      "Model": "gpt-4o"
    },
    "Providers": {
      "copilot": {
        "Auth": "oauth",
        "DefaultModel": "gpt-4o",
        "ApiBase": "https://api.githubcopilot.com"
      }
    },
    "Gateway": {
      "Host": "0.0.0.0",
      "Port": 18790
    }
  }
}
```

## Architecture

| Component | Description |
|-----------|-------------|
| **Gateway** | Main orchestrator — message bus, agent routing, channel management, hot reload |
| **Agent** | Per-agent processing loop with context building and tool execution |
| **Core** | 14 interface contracts, configuration, extension loading |
| **Cli** | `botnexus` command-line tool — config, agents, providers, doctor, Gateway lifecycle |
| **Diagnostics** | 13 health checkups with auto-fix, used by CLI doctor and `/api/doctor` endpoint |
| **Channels** | Discord, Slack, Telegram, WebSocket implementations |
| **Providers** | Copilot (OAuth, model-aware routing), OpenAI, Anthropic LLM backends |
| **Session** | JSONL-based conversation persistence |
| **WebUI** | Real-time activity monitoring dashboard |

### Provider Model-Aware Routing

BotNexus uses a **Pi-style, model-aware provider architecture**:

- **ModelDefinition** — Each model explicitly declares its API format (Anthropic Messages, OpenAI Completions, OpenAI Responses)
- **IApiFormatHandler** — Separate handler per API format (3 handlers in Copilot provider for 26 models)
- **Automatic Routing** — Request model determines handler; no provider name needed
- **Copilot Models** — 26 pre-registered models: Claude (6), GPT-4/4o/o1/o3 (8), GPT-5 (7), Gemini (4), Grok (1)

Example:
```json
{
  "Agents": {
    "analyst": {
      "Model": "gpt-4o",        // Routes to openai-completions handler
      "Provider": "copilot"
    },
    "researcher": {
      "Model": "gpt-5.4",       // Routes to openai-responses handler
      "Provider": "copilot"
    },
    "coder": {
      "Model": "claude-opus-4.6", // Routes to anthropic-messages handler
      "Provider": "copilot"
    }
  }
}
```

See [Architecture Guide](docs/architecture.md#provider-architecture-pi-style) and [Configuration Guide](docs/configuration.md#copilot-provider-supported-models) for details.



- **[Getting Started](docs/getting-started.md)** ← Start here
- [API Reference](docs/api-reference.md) — REST API endpoints (agents, sessions, system)
- [Development Workflow](docs/development-workflow.md) — Build, test, and deploy with dev-loop script
- [Architecture Overview](docs/architecture.md)
- [Configuration Guide](docs/configuration.md)
- [Extension Development](docs/extension-development.md)
- [Workspace & Memory](docs/workspace-and-memory.md)
- [Cron & Scheduling](docs/cron-and-scheduling.md)

## Project Structure

```
src/
├── BotNexus.Core          # Abstractions, config, extension loader
├── BotNexus.Gateway       # Main host, agent router, WebSocket, hot reload
├── BotNexus.Cli           # CLI tool (botnexus command)
├── BotNexus.Diagnostics   # Health checkups (doctor) with auto-fix
├── BotNexus.Api           # OpenAI-compatible REST API
├── BotNexus.Agent         # Agent loop, tool registry, MCP
├── BotNexus.Session       # JSONL session persistence
├── channels/
│   └── BotNexus.Channels.*  # Channel implementations (Discord, Slack, Telegram)
├── BotNexus.Providers.*   # LLM provider implementations
├── BotNexus.Tools.*       # Tool extensions
└── BotNexus.WebUI         # Real-time monitoring UI
extensions/                # Built extension binaries (auto-populated)
tests/                     # Unit, integration, and E2E tests
```

## Configuration

BotNexus uses a layered configuration model:

1. **Code defaults** — Built-in constants
2. **appsettings.json** — Project defaults
3. **~/.botnexus/config.json** — User configuration (primary)
4. **Environment variables** — Override any setting via `BotNexus__Path__To__Property`

Set `BOTNEXUS_HOME` environment variable to override the `~/.botnexus/` location.

## License

See [LICENSE](LICENSE) for details.
