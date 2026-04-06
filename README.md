# BotNexus

A modular, extensible AI agent execution platform built in C#/.NET. BotNexus enables running multiple AI agents concurrently, each powered by configurable LLM providers, receiving messages from multiple channels, and executing tools dynamically.

## 📖 Getting Started

| Guide | Audience |
|-------|----------|
| **[Getting Started →](docs/getting-started.md)** | First-time users — clone to running in minutes |
| **[Developer Guide →](docs/dev-guide.md)** | Developers and agents — build, test, run locally |
| **[API Reference →](docs/api-reference.md)** | REST and WebSocket endpoint documentation |
| **[Architecture →](docs/architecture/overview.md)** | System design, components, and extension points |

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

## Gateway Service

The **BotNexus Gateway** is the central hub for multi-agent orchestration. It provides:

- **REST API** — Agents, sessions, chat, configuration endpoints
- **WebSocket** — Real-time streaming with agents
- **Multi-agent routing** — Route messages to different agents by ID
- **Session persistence** — Durable conversation history (JSONL)
- **Hot reload** — Edit `config.json` and changes apply live (no restart)
- **Health checks** — Built-in `/health` endpoint for monitoring
- **WebUI** — Real-time chat dashboard at `/webui`

### Quick Start

```bash
# Build the solution
dotnet build BotNexus.slnx

# Run the Gateway (port 5005 by default)
# Option 1: PowerShell dev script (build + test + run)
.\scripts\dev-loop.ps1

# Option 2: Start gateway only
.\scripts\start-gateway.ps1

# Option 3: Direct dotnet command
dotnet run --project src/gateway/BotNexus.Gateway.Api
```

Open `http://localhost:5005/webui` for the real-time chat dashboard. See the [Developer Guide](docs/dev-guide.md) for the full workflow.

### Configuration

Edit `~/.botnexus/config.json` to configure:

```json
{
  "gateway": {
    "listenUrl": "http://localhost:5005",
    "defaultAgentId": "assistant",
    "sessionsDirectory": "workspace/sessions"
  },
  "agents": {
    "assistant": {
      "provider": "copilot",
      "model": "gpt-4.1",
      "systemPromptFile": "prompts/assistant.txt",
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

### Key API Endpoints

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/health` | GET | Health check status |
| `/api/agents` | GET | List all agents |
| `/api/agents` | POST | Register a new agent |
| `/api/agents/{id}` | GET | Get agent details |
| `/api/agents/{id}/sessions/{sid}/status` | GET | Check agent instance status |
| `/api/chat` | POST | Send a message to an agent |
| `/api/chat/steer` | POST | Inject steering message into active run |
| `/api/chat/follow-up` | POST | Queue follow-up for next run |
| `/api/sessions` | GET | List sessions |
| `/api/sessions/{id}` | GET | Get session history |
| `/api/sessions/{id}` | DELETE | Delete a session |
| `/ws` | WebSocket | Real-time streaming with agents |
| `/webui` | GET | Interactive WebUI dashboard |

### WebSocket Protocol

Connect to `ws://localhost:5005/ws?agent={agentId}&session={sessionId}` for real-time streaming.

**Client → Server message types:**
- `{ "type": "message", "content": "..." }` — Send message to agent
- `{ "type": "abort" }` — Abort current execution
- `{ "type": "steer", "content": "..." }` — Inject steering message
- `{ "type": "follow_up", "content": "..." }` — Queue follow-up
- `{ "type": "ping" }` — Keepalive

**Server → Client message types:**
- `{ "type": "connected", "connectionId": "...", "sessionId": "..." }` — Connection established
- `{ "type": "message_start", "messageId": "..." }` — Agent started processing
- `{ "type": "thinking_delta", "delta": "...", "messageId": "..." }` — Thinking/reasoning content
- `{ "type": "content_delta", "delta": "..." }` — Streaming content chunk
- `{ "type": "tool_start", "toolCallId": "...", "toolName": "..." }` — Tool execution started
- `{ "type": "tool_end", "toolCallId": "...", "toolName": "...", "toolResult": "...", "toolIsError": false }` — Tool result received
- `{ "type": "message_end", "messageId": "...", "usage": {...} }` — Agent completed
- `{ "type": "error", "message": "...", "code": "..." }` — Error occurred
- `{ "type": "pong" }` — Keepalive response

### Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    Gateway.Api (ASP.NET)                │
│  [REST Controllers] [WebSocket Handler] [WebUI Files]   │
└────────────┬──────────────────────────────┬─────────────┘
             │                              │
        Message Bus                  Session Persistence
             │                              │
┌────────────▼──────────────────────────────▼─────────────┐
│                   BotNexus.Gateway                       │
│  [Agent Router] [Hot Reload] [Channel Manager]          │
└────────────┬────────────────────────────────────────────┘
             │
        Extension Points
             │
    ┌────────┼────────┐
    │        │        │
   [IIsolationStrategy]  [IChannelAdapter]  [ISessionStore]
   (in-process/sandbox)  (Discord/Slack)    (File/Memory/Redis)
```

### For More Details

👉 **Read [src/gateway/README.md](src/gateway/README.md)** for detailed architecture, configuration, and development guide.

### CLI Tool

BotNexus includes a command-line tool for managing configuration, agents, providers, diagnostics, and the Gateway lifecycle:

```bash
# Install as a .NET tool
dotnet tool install --global --add-source ./src/BotNexus.Cli/bin/Release/net10.0 botnexus

# Or run directly from source
dotnet run --project src/BotNexus.Cli -- doctor
```

Key commands: `botnexus init`, `botnexus validate`, `botnexus agent list/add/remove`, `botnexus config get/set`, `botnexus doctor`, `botnexus status`, `botnexus start`, `botnexus stop`. Run `botnexus --help` for the full list.

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
- [Developer Guide](docs/dev-guide.md) — Build, test, and run locally
- [API Reference](docs/api-reference.md) — REST and WebSocket endpoints
- [Architecture Overview](docs/architecture/overview.md) — System design and components
- [Developer Guide](docs/dev-guide.md) — Build, test, and deploy with dev-loop script
- [Configuration Guide](docs/configuration.md) — Complete configuration reference
- [Extension Development](docs/extension-development.md) — Build custom providers, channels, and tools
- [Workspace & Memory](docs/architecture/workspace-and-memory.md) — Agent workspace and memory system
- [Cron & Scheduling](docs/cron-and-scheduling.md) — Scheduled tasks and heartbeats
- [Skills Guide](docs/skills.md) — Modular knowledge packages for agents

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
