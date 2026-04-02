# Getting Started with BotNexus

> From zero to running with AI agents — a step-by-step guide.

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Build from Source](#2-build-from-source)
3. [First Run](#3-first-run)
4. [Using the Web Interface](#4-using-the-web-interface)
5. [Configure the Copilot Provider](#5-configure-the-copilot-provider)
6. [Create Your First Agent](#6-create-your-first-agent)
7. [Talk to Your Agent](#7-talk-to-your-agent)
8. [Set Up Multiple Agents](#8-set-up-multiple-agents)
9. [Configure Channels (optional)](#9-configure-channels-optional)
10. [Configure Cron Jobs (optional)](#10-configure-cron-jobs-optional)
11. [Migrating from OpenClaw](#11-migrating-from-openclaw)
12. [Managing the Environment](#12-managing-the-environment)
13. [CLI Tool](#13-cli-tool)
14. [Security](#14-security)
15. [Next Steps](#15-next-steps)

---

## 1. Prerequisites

| Requirement | Details |
|---|---|
| **.NET 10+ SDK** | [Download](https://dotnet.microsoft.com/download). Verify with `dotnet --version`. |
| **Git** | Any recent version. Verify with `git --version`. |
| **GitHub account** | Required for the Copilot provider's OAuth flow. You need an active GitHub Copilot subscription. |

Optional but recommended:

- **curl** — for testing API endpoints (built into modern Windows and macOS)
- A WebSocket-capable browser — for the built-in WebUI

---

## 2. Build from Source

Clone the repository and build:

```bash
git clone https://github.com/your-org/botnexus.git
cd botnexus
dotnet build BotNexus.slnx
```

Expected output (last lines):

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

> **Troubleshooting:** If you see SDK version errors, ensure .NET 10+ is installed. Run `dotnet --list-sdks` to check.

---

## 3. First Run

Start the Gateway:

```bash
dotnet run --project src/BotNexus.Gateway
```

### What happens on first run

BotNexus automatically initializes a home directory at `~/.botnexus/`. Here's what gets created:

```
~/.botnexus/
├── config.json              # Default configuration (created if missing)
├── extensions/
│   ├── providers/           # LLM provider extension assemblies
│   ├── channels/            # Channel extension assemblies
│   └── tools/               # Tool extension assemblies
├── agents/                  # Agent workspace directories
├── tokens/                  # OAuth token storage
├── sessions/                # Conversation history (JSONL)
└── logs/                    # Daily log files (botnexus-YYYYMMDD.log)
```

The default `config.json` is created as a minimal template with **no providers or channels pre-configured**. You add what you need:

```json
{
  "BotNexus": {
    "ExtensionsPath": "~/.botnexus/extensions",
    "Providers": {},
    "Channels": {
      "Instances": {}
    },
    "Tools": {
      "Extensions": {},
      "McpServers": {}
    }
  }
}
```

This is intentional — the system starts clean. You'll add the Copilot provider (or another provider) as your first configuration step.

> **Custom home directory:** Set the `BOTNEXUS_HOME` environment variable to override the default `~/.botnexus/` location.

### Verify it's running

The Gateway starts on **port 18790** by default. Check the health endpoint:

```bash
curl http://localhost:18790/health
```

Expected response (on first run):

```json
{
  "status": "Healthy",
  "checks": {
    "messageBus": {
      "status": "Healthy",
      "description": "Message bus is alive",
      "duration": 0.15,
      "data": null
    },
    "providers": {
      "status": "Healthy",
      "description": "No providers configured",
      "duration": 0.08,
      "data": null
    },
    "channels": {
      "status": "Healthy",
      "description": "No enabled channels configured",
      "duration": 0.05,
      "data": null
    },
    "extensionLoader": {
      "status": "Healthy",
      "description": "Extension loader completed successfully",
      "duration": 0.32,
      "data": {
        "loaded": 0,
        "failed": 0,
        "warnings": 0
      }
    }
  },
  "totalDuration": 0.6
}
```

You should also see console output like:

```
info: BotNexus[0] BotNexus home: C:\Users\you\.botnexus
info: Microsoft.Hosting.Lifetime[14] Now listening on: http://0.0.0.0:18790
```

> **Note:** On first run, you'll see "No providers configured" and "No enabled channels configured" — these are healthy states. You'll configure providers and channels as needed in the next steps.

> **Troubleshooting:** If port 18790 is in use, edit `~/.botnexus/config.json` and change `Gateway.Port` (see [Section 13](#13-security) for full Gateway config).

---

## 4. Using the Web Interface

The **WebUI** is the easiest way to interact with BotNexus. It provides a real-time chat interface, session management, and a live view of all loaded extensions. No command-line knowledge required!

### Accessing the WebUI

After starting the Gateway, open your browser to:

```
http://localhost:18790/
```

You should see the BotNexus web interface load immediately. The UI connects to the Gateway via WebSocket automatically.

> **Note:** Modern browsers (Chrome, Firefox, Safari, Edge) with WebSocket support work best. The UI detects the correct protocol automatically (ws for HTTP, wss for HTTPS).

### The Web Interface Layout

The WebUI has a clean two-panel design:

#### **Left Sidebar** — Navigation & Extensions
- **💬 New Chat** — Create a new chat session instantly
- **📋 Sessions** — Browse past conversations; click to reload and continue chatting
- **📡 Channels** — View all connected messaging channels and their status
- **🧠 Agents** — See available agents with their models and settings
- **🧩 Extensions** — Panel showing loaded providers, tools, and health status
- **📊 Activity Monitor** — Optional real-time feed of all messages across channels
- **🌐 Connection Status** — Shows WebSocket connection state (connected/disconnected)

#### **Main Chat Area** — Your Conversation
- **Welcome Screen** — Displays when no session is selected; instructs you to start a new chat
- **Chat Messages** — Shows conversation history with timestamps and message roles (User/Assistant/Tool)
- **Input Area** — Type your message and press `Enter` or click **Send**

### Quick Start: Send Your First Message

1. **Click "Start New Chat"** in the sidebar
2. **Type your message** in the text area (e.g., "Hello! What can you do?")
3. **Press `Enter` or click Send**
4. **Watch the agent respond** — the UI streams responses in real-time

The session is created automatically on your first message and saved to disk.

### Understanding Sessions

**Sessions** are persistent conversations tied to a specific agent. Each session:
- Has a unique key (format: `channel:connection-id:agent-name`)
- Stores full message history
- Can be paused and resumed anytime
- Persists across Gateway restarts

In the **Sessions** panel:
- **📋 Sessions list** shows all past sessions sorted by recency
- **Agent name** — which agent handled the conversation
- **Message count** — total messages in that session
- **Timestamp** — when the session was last updated
- Click any session to reload it and continue the conversation

### Viewing Extensions

The **Extensions** panel shows a health summary and all loaded components:

```
✅ 4 loaded        ← Green if no failures
❌ 0 failed
📡 1 channel       ← Copilot, Discord, Slack, etc.
🧠 1 provider      ← LLM backend (e.g., Copilot, OpenAI)
🔧 15 tools        ← Available AI tools
```

Expand **Providers** and **Tools** to see what's available. This is useful for debugging configuration or seeing what capabilities your agent has access to.

### Real-Time Activity Monitor

The **Activity Monitor** (📊) shows real-time events from across all channels:

- **Toggle the switch** to enable or disable the live feed
- Events are color-coded:
  - 🟢 **Message Received** — user input captured
  - 🟠 **Response Sent** — agent's reply
  - 🔴 **Errors** — any failures or issues

This is helpful for monitoring multi-channel setups or debugging unexpected behavior.

### WebSocket Connection

The UI automatically connects to the Gateway's WebSocket endpoint (`/ws`). You should see:

```
🌐 Connected
```

in the connection status.

**If you see "Disconnected":**
- Check that the Gateway is running (`dotnet run --project src/BotNexus.Gateway`)
- Verify the Gateway is on `http://localhost:18790` (or your custom host/port)
- Try refreshing the page (Ctrl+R or Cmd+R)
- The UI will auto-reconnect every 3 seconds

### Tips & Tricks

- **Shift+Enter** to add line breaks without sending
- **Click a session** to load it and continue conversing
- **Refresh buttons** (↻) in each section reload that panel's data
- **Sessions are persistent** — close the browser anytime; your chat will be there when you return
- **Multiple tabs supported** — open multiple WebUI tabs to run parallel conversations

---

## 5. Configure Your First Provider (Copilot)

### Important: Where provider assemblies are loaded from

BotNexus loads provider DLLs from the `ExtensionsPath` directory configured in your config. **The pre-built provider assemblies ship with the repository** at `{repo-root}/extensions/providers/` — they are **not** deployed to `~/.botnexus/extensions/` automatically.

**For development (running from the repo via `dotnet run`):**
- Set `ExtensionsPath` to point to the repo's extensions directory so the pre-built providers are found
- Example: `"ExtensionsPath": "{repo-root}/extensions"` or use a relative path from where you run `dotnet run`
- Or, omit `ExtensionsPath` entirely to use the development default

**For production (running an installed tool):**
- Extensions must be built and copied to `~/.botnexus/extensions/` manually or during tool installation
- Set `ExtensionsPath` to `~/.botnexus/extensions` (or omit it if this is the default)

---

The Copilot provider is not pre-configured by default. Add it to `~/.botnexus/config.json`:

```json
{
  "BotNexus": {
    "ExtensionsPath": "{repo-root}/extensions",
    "Providers": {
      "copilot": {
        "Auth": "oauth",
        "DefaultModel": "gpt-4o",
        "ApiBase": "https://api.githubcopilot.com"
      }
    }
  }
}
```

> **Note:** Replace `{repo-root}` with the path to where you cloned the BotNexus repository (e.g., `/home/user/botnexus` or `C:\repos\botnexus`). For development, you can also use a relative path like `./extensions` if you run `dotnet run` from the repo root.

Restart the Gateway to pick up the configuration change (Ctrl+C, then `dotnet run --project src/BotNexus.Gateway`).

> **Hot Reload:** Most configuration changes (agents, providers, cron jobs, API key) are applied automatically when you save `config.json` — no restart required. Only host/port and extension path changes require a restart. See [Configuration Guide — Hot Reload](configuration.md#hot-reload) for details.

> **Note:** The Copilot provider uses GitHub's built-in OAuth client ID and API endpoint automatically. No additional setup is needed beyond adding it to the config.

### The OAuth device code flow

When BotNexus first needs to call the Copilot API (i.e., when you send your first message), it triggers the GitHub device code flow. Here's what you'll see in the Gateway console:

```
info: BotNexus.Providers.Copilot.GitHubDeviceCodeFlow[0]
      Go to https://github.com/login/device and enter code: ABCD-1234
```

**Steps:**

1. Open **https://github.com/login/device** in your browser
2. Enter the code shown in the console (e.g., `ABCD-1234`)
3. Click **Authorize** when prompted
4. BotNexus receives the token and saves it to `~/.botnexus/tokens/copilot.json`

The token is cached and refreshed automatically. You only need to do this once (until the token expires).

> **Troubleshooting:** If authorization times out (the code expires after 15 minutes), just send another message to trigger a fresh code. If you see "access_denied", verify your GitHub account has an active Copilot subscription.

---

## 6. Create Your First Agent

Agents are named configurations with their own workspace, personality, and settings. Edit `~/.botnexus/config.json` to add one:

```json
{
  "BotNexus": {
    "ExtensionsPath": "~/.botnexus/extensions",
    "Providers": {
      "copilot": {
        "Auth": "oauth",
        "DefaultModel": "gpt-4o",
        "ApiBase": "https://api.githubcopilot.com"
      }
    },
    "Agents": {
      "Model": "gpt-4o",
      "MaxTokens": 8192,
      "Temperature": 0.1,
      "Named": {
        "assistant": {
          "Name": "assistant",
          "Provider": "copilot",
          "Model": "gpt-4o",
          "EnableMemory": true
        }
      }
    }
  }
}
```

Restart the Gateway (Ctrl+C, then `dotnet run --project src/BotNexus.Gateway`).

### What happens

BotNexus creates a workspace directory for your agent at:

```
~/.botnexus/agents/assistant/
├── SOUL.md           # Core personality, values, and boundaries
├── IDENTITY.md       # Role, communication style, constraints
├── USER.md           # User preferences and collaboration style
├── MEMORY.md         # Long-term distilled knowledge
├── HEARTBEAT.md      # Periodic task instructions
└── memory/
    └── daily/        # Daily memory log files (YYYY-MM-DD.md)
```

Each file is plain Markdown with HTML comments explaining its purpose. These files are loaded into the system prompt for every conversation.

### Customize your agent

Edit the workspace files to shape your agent's personality:

**`~/.botnexus/agents/assistant/SOUL.md`** — What the agent *is*:

```markdown
# Soul

You are a helpful, thoughtful assistant. You value clarity and precision.
You admit when you don't know something rather than guessing.
```

**`~/.botnexus/agents/assistant/IDENTITY.md`** — How the agent behaves:

```markdown
# Identity

- Name: Assistant
- Role: General-purpose AI assistant
- Style: Conversational but efficient. Use bullet points for complex answers.
- Constraints: Never execute destructive operations without confirmation.
```

**`~/.botnexus/agents/assistant/USER.md`** — What you want the agent to know about you:

```markdown
# User

- Name: Jon
- Timezone: Pacific Time
- Preferences: Prefers concise answers. Values working code over pseudocode.
```

Changes to workspace files take effect on the **next conversation** — no restart required.

### Agent configuration reference

| Property | Type | Default | Description |
|---|---|---|---|
| `Name` | string | (required) | Agent identifier |
| `Provider` | string | — | LLM provider to use (e.g., `"copilot"`) |
| `Model` | string | inherits | Model override (e.g., `"gpt-4o"`) |
| `MaxTokens` | int | 8192 | Maximum response tokens |
| `Temperature` | double | 0.1 | Response randomness (0.0–2.0) |
| `MaxToolIterations` | int | 40 | Max tool-use loops per turn |
| `Timezone` | string | `"UTC"` | Agent's timezone |
| `EnableMemory` | bool | false | Enable persistent memory system |
| `MaxContextFileChars` | int | 8000 | Max characters loaded from each workspace file |
| `AutoLoadMemory` | bool | true | Auto-load MEMORY.md into context |
| `MemoryConsolidationIntervalHours` | int | 24 | Hours between memory consolidation |
| `ConsolidationModel` | string | — | Model to use for memory consolidation |

---

## 7. Talk to Your Agent

Now that you have an agent configured, it's time to start chatting! BotNexus gives you three ways to interact with your agents, but **the WebUI is the easiest and recommended for most users**.

### Option A: WebUI (browser) — **Recommended** ⭐

This is the primary way most users interact with BotNexus. Open your browser to:

```
http://localhost:18790/
```

You'll see:
- A **welcome screen** with instructions
- A **sidebar** showing all sessions, channels, agents, and extensions
- A **chat panel** ready for input

**To send your first message:**

1. Click **"💬 Start New Chat"** in the sidebar
2. Type your message in the text area (e.g., `"Hello! What can you help me with?"`)
3. Press `Enter` or click **Send**
4. Watch the agent respond in real-time

The session is automatically created and saved. You can continue chatting or start new sessions anytime. For full details, see [Section 4: Using the Web Interface](#4-using-the-web-interface).

### Option B: WebSocket (direct connection)

For programmatic access or custom integrations, connect directly to:

```
ws://localhost:18790/ws
```

Send JSON messages:

```json
{"type": "message", "content": "Hello! What can you do?", "agent": "assistant"}
```

You'll receive responses as:

```json
{"type": "connected", "connection_id": "abc-123-def"}
```

```json
{"type": "delta", "content": "I'm your"}
```

```json
{"type": "delta", "content": " assistant..."}
```

```json
{"type": "response", "content": "I'm your assistant. I can help with..."}
```

> **Tip:** The `"agent"` field targets a specific named agent. Omit it to use the default agent. Set it to `"all"` to broadcast to all agents.

### Option C: REST API sessions

List and inspect conversation sessions:

```bash
# List all sessions
curl http://localhost:18790/api/sessions

# Get a specific session
curl http://localhost:18790/api/sessions/ws:abc-123-def:assistant
```

> **Note:** The REST API provides session and configuration management. Chat interactions happen over WebSocket.

### First conversation

When you send your first message, you'll see the OAuth flow kick in (see [Section 5](#5-configure-the-copilot-provider)). After authenticating, the agent responds using the Copilot API with your workspace files as context.

---

## 8. Set Up Multiple Agents

Add more agents to the `Named` section in `config.json`:

```json
"Agents": {
  "Model": "gpt-4o",
  "Named": {
    "assistant": {
      "Name": "assistant",
      "Provider": "copilot",
      "Model": "gpt-4o",
      "EnableMemory": true
    },
    "researcher": {
      "Name": "researcher",
      "Provider": "copilot",
      "Model": "gpt-4o",
      "EnableMemory": true,
      "Temperature": 0.3,
      "MaxTokens": 16384
    },
    "note-taker": {
      "Name": "note-taker",
      "Provider": "copilot",
      "Model": "gpt-4o",
      "EnableMemory": true,
      "Temperature": 0.0
    }
  }
}
```

Each agent gets its own workspace directory:

```
~/.botnexus/agents/
├── assistant/
│   ├── SOUL.md
│   ├── IDENTITY.md
│   └── ...
├── researcher/
│   ├── SOUL.md
│   ├── IDENTITY.md
│   └── ...
└── note-taker/
    ├── SOUL.md
    ├── IDENTITY.md
    └── ...
```

### Talking to specific agents

Over WebSocket, address a specific agent with the `"agent"` field:

```json
{"type": "message", "content": "Summarize today's news", "agent": "researcher"}
```

```json
{"type": "message", "content": "Save this as a note: ...", "agent": "note-taker"}
```

### Agent awareness

BotNexus auto-generates an `AGENTS.md` file in each agent's workspace that lists all configured agents. This gives each agent awareness of the others — useful for routing or delegation scenarios.

---

## 9. Configure Channels (optional)

BotNexus supports multiple messaging channels as extensions. Channels are configured under `BotNexus.Channels.Instances` in your config:

```json
"Channels": {
  "SendProgress": true,
  "SendToolHints": false,
  "SendMaxRetries": 3,
  "Instances": {
    "telegram": {
      "Enabled": true,
      "BotToken": "your-telegram-bot-token",
      "AllowFrom": ["123456789"]
    },
    "discord": {
      "Enabled": true,
      "BotToken": "your-discord-bot-token",
      "AllowFrom": ["987654321"]
    },
    "slack": {
      "Enabled": true,
      "BotToken": "xoxb-your-slack-token",
      "SigningSecret": "your-signing-secret",
      "AllowFrom": ["U01234567"]
    }
  }
}
```

| Setting | Description |
|---|---|
| `SendProgress` | Send typing/progress indicators to the channel |
| `SendToolHints` | Show tool execution hints in channel messages |
| `SendMaxRetries` | Retry count for failed message sends |
| `AllowFrom` | Whitelist of user/chat IDs allowed to interact |

### Extension folder structure

Channel, provider, and tool implementations are loaded from `~/.botnexus/extensions/`:

```
~/.botnexus/extensions/
├── channels/         # Channel plugins (e.g., Telegram, Discord)
├── providers/        # LLM provider plugins
└── tools/            # Tool plugins
```

**Development mode:** When running `dotnet run` in development, extensions are loaded from `{repo-root}/extensions/` instead. This allows you to build extensions without installing them to your home directory.

**Production mode:** Extensions are always loaded from `~/.botnexus/extensions/`.

> See [Extension Development](extension-development.md) for building custom channel extensions.

---

## 10. Configure Cron Jobs (optional)

BotNexus includes a built-in cron service for scheduling recurring tasks. Jobs are defined centrally under `BotNexus.Cron.Jobs`:

```json
"Cron": {
  "Enabled": true,
  "TickIntervalSeconds": 10,
  "Jobs": {
    "morning-briefing": {
      "Schedule": "0 8 * * 1-5",
      "Type": "agent",
      "Agent": "assistant",
      "Prompt": "Good morning! Give me a briefing for today.",
      "Timezone": "America/Los_Angeles"
    },
    "memory-consolidation": {
      "Schedule": "0 2 * * *",
      "Type": "maintenance",
      "Action": "consolidate-memory",
      "Agents": ["assistant", "researcher"]
    },
    "system-health": {
      "Schedule": "*/30 * * * *",
      "Type": "system",
      "Action": "health-check"
    }
  }
}
```

### Job types

| Type | Description |
|---|---|
| `agent` | Sends a prompt to a specific agent. Requires `Agent` and `Prompt`. |
| `maintenance` | Runs maintenance tasks (e.g., memory consolidation). Requires `Action`. |
| `system` | Runs system-level tasks (e.g., health checks). Requires `Action`. |

### Cron expression format

Standard 5-field cron expressions: `minute hour day-of-month month day-of-week`

| Example | Meaning |
|---|---|
| `0 8 * * 1-5` | 8:00 AM, Monday through Friday |
| `0 2 * * *` | 2:00 AM daily |
| `*/30 * * * *` | Every 30 minutes |
| `0 9 1 * *` | 9:00 AM on the 1st of each month |

> See [Cron & Scheduling](cron-and-scheduling.md) for the complete reference.

---

## 11. Migrating from OpenClaw

If you're coming from OpenClaw, the good news is that BotNexus uses the same workspace file format. Migration is mostly moving files to new locations.

### Workspace file mapping

| OpenClaw | BotNexus | Notes |
|---|---|---|
| `~/.openclaw/workspace/SOUL.md` | `~/.botnexus/agents/{name}/SOUL.md` | Same format — copy directly |
| `~/.openclaw/workspace/IDENTITY.md` | `~/.botnexus/agents/{name}/IDENTITY.md` | Same format — copy directly |
| `~/.openclaw/workspace/USER.md` | `~/.botnexus/agents/{name}/USER.md` | Same format — copy directly |
| `~/.openclaw/workspace/MEMORY.md` | `~/.botnexus/agents/{name}/MEMORY.md` | Same format — copy directly |
| `~/.openclaw/workspace/memory/YYYY-MM-DD.md` | `~/.botnexus/agents/{name}/memory/daily/YYYY-MM-DD.md` | Note: goes into `daily/` subdirectory |

### Copy workspace files

After creating your agent (see [Section 6](#6-create-your-first-agent)), copy your OpenClaw workspace:

**Windows (PowerShell):**

```powershell
$agent = "assistant"
Copy-Item "$env:USERPROFILE\.openclaw\workspace\SOUL.md" "$env:USERPROFILE\.botnexus\agents\$agent\SOUL.md"
Copy-Item "$env:USERPROFILE\.openclaw\workspace\IDENTITY.md" "$env:USERPROFILE\.botnexus\agents\$agent\IDENTITY.md"
Copy-Item "$env:USERPROFILE\.openclaw\workspace\USER.md" "$env:USERPROFILE\.botnexus\agents\$agent\USER.md"
Copy-Item "$env:USERPROFILE\.openclaw\workspace\MEMORY.md" "$env:USERPROFILE\.botnexus\agents\$agent\MEMORY.md"
Copy-Item "$env:USERPROFILE\.openclaw\workspace\memory\*" "$env:USERPROFILE\.botnexus\agents\$agent\memory\daily\" -Recurse
```

**macOS/Linux:**

```bash
AGENT="assistant"
cp ~/.openclaw/workspace/SOUL.md ~/.botnexus/agents/$AGENT/SOUL.md
cp ~/.openclaw/workspace/IDENTITY.md ~/.botnexus/agents/$AGENT/IDENTITY.md
cp ~/.openclaw/workspace/USER.md ~/.botnexus/agents/$AGENT/USER.md
cp ~/.openclaw/workspace/MEMORY.md ~/.botnexus/agents/$AGENT/MEMORY.md
cp ~/.openclaw/workspace/memory/*.md ~/.botnexus/agents/$AGENT/memory/daily/
```

### What's different from OpenClaw

| Aspect | OpenClaw | BotNexus |
|---|---|---|
| **Workspace location** | Single `~/.openclaw/workspace/` | Per-agent: `~/.botnexus/agents/{name}/` |
| **Daily memory** | `memory/YYYY-MM-DD.md` | `memory/daily/YYYY-MM-DD.md` |
| **Configuration** | YAML-based | JSON-based (`config.json`) |
| **Multi-agent** | Single agent | Multiple named agents |
| **New files** | — | `HEARTBEAT.md` for periodic task instructions |
| **Extensions** | Built-in channels | Dynamic assembly loading from `extensions/` |
| **Cron** | Per-agent cron jobs | Centralized cron service |
| **Providers** | Single provider | Multiple concurrent providers |

### What works the same

- **Workspace file format** — Markdown with the same structure
- **SOUL.md, IDENTITY.md, USER.md** — Identical purpose and format
- **Memory system** — Daily notes + consolidated long-term memory
- **OAuth flow** — Same GitHub device code flow for Copilot

---

## 12. Managing the Environment

### Health and status endpoints

```bash
# Health check — are all components healthy?
curl http://localhost:18790/health

# Ready check — is the system ready to accept requests?
curl http://localhost:18790/ready

# List loaded extensions (channels, providers, tools)
curl http://localhost:18790/api/extensions

# List configured agents
curl http://localhost:18790/api/agents

# List registered providers
curl http://localhost:18790/api/providers

# List available tools
curl http://localhost:18790/api/tools

# List channel status
curl http://localhost:18790/api/channels

# List cron jobs
curl http://localhost:18790/api/cron

# Get cron job details and history
curl http://localhost:18790/api/cron/morning-briefing

# View cron execution history
curl http://localhost:18790/api/cron/history?limit=20

# Manually trigger a cron job
curl -X POST http://localhost:18790/api/cron/morning-briefing/trigger

# List active sessions
curl http://localhost:18790/api/sessions

# System status (gateway version, health, agents, cron, memory)
curl http://localhost:18790/api/status

# Run diagnostics (all checkups or filtered by category)
curl http://localhost:18790/api/doctor
curl http://localhost:18790/api/doctor?category=Configuration

# Graceful shutdown
curl -X POST http://localhost:18790/api/shutdown
```

### Logs

Logs are written to **both the console and files**:

**Console:** Gateway logs appear in real-time. Look for:

- `info: BotNexus[0] BotNexus home: ...` — Confirms home directory
- `info: Microsoft.Hosting.Lifetime[14] Now listening on: ...` — Confirms listening address
- OAuth flow messages from `BotNexus.Providers.Copilot.GitHubDeviceCodeFlow`
- Extension loading results at startup

**File:** Daily log files are written to `~/.botnexus/logs/botnexus-YYYYMMDD.log`. Each day gets a new file, and logs are retained for 14 days.

To view today's logs:

```bash
# Windows (PowerShell)
Get-Content $env:USERPROFILE\.botnexus\logs\botnexus-*.log -Tail 50

# macOS/Linux
tail -50 ~/.botnexus/logs/botnexus-*.log
```

> **Troubleshooting tip:** If the health check shows unhealthy status, check the log file for detailed error messages. Logs provide insight into extension loading failures, OAuth issues, and configuration problems.

### Common issues

| Symptom | Cause | Fix |
|---|---|---|
| Port already in use | Another process on 18790 | Change `Gateway.Port` in config.json |
| OAuth code expired | Took too long to authorize | Send another message to get a fresh code |
| "Unauthorized" on API calls | API key configured but not sent | Add `X-Api-Key` header (see [Security](#12-security)) |
| Agent not found | Agent not in `Named` config | Add agent to `Agents.Named` and restart |
| Extensions show warnings at startup | Missing extension folder (expected on first run) | Not an error — folders are created on-demand when needed |
| Provider configured but not loaded / "No LLM providers registered" | `ExtensionsPath` does not point to the directory containing provider DLLs | For development: set `ExtensionsPath` to `{repo-root}/extensions` in config.json and restart. For production: build and copy provider DLLs to `~/.botnexus/extensions/providers/` |
| Health check says unhealthy | An extension failed to load or a required component is missing | Check `~/.botnexus/logs/botnexus-*.log` for details; check `GET /api/extensions` for failures |

---

## 13. CLI Tool

BotNexus includes a command-line tool (`botnexus`) for managing configuration, agents, providers, diagnostics, and the Gateway lifecycle without editing JSON or using curl.

### Install

```bash
# Install as a global .NET tool (from local build)
dotnet tool install --global --add-source ./src/BotNexus.Cli/bin/Release/net10.0 botnexus

# Or run directly from the repo
dotnet run --project src/BotNexus.Cli -- --help
```

### Commands

| Command | Description |
|---|---|
| `botnexus config validate` | Validate config.json syntax and binding |
| `botnexus config show` | Show resolved config (defaults merged with overrides) |
| `botnexus config init` | Create default config.json interactively |
| `botnexus agent list` | List configured agents |
| `botnexus agent add` | Add an agent to config |
| `botnexus agent workspace <name>` | Show agent workspace path and files |
| `botnexus provider list` | List configured providers |
| `botnexus provider add` | Add a provider to config |
| `botnexus channel add` | Add a channel instance to config |
| `botnexus extension list` | List installed extensions |
| `botnexus doctor` | Run health checkups |
| `botnexus status` | Show Gateway and configuration status |
| `botnexus logs [-f] [--lines N]` | Tail Gateway logs |
| `botnexus start [--foreground]` | Start Gateway |
| `botnexus stop` | Stop Gateway |
| `botnexus restart` | Restart Gateway |

All commands support the `--home` option to override `BOTNEXUS_HOME`.

### Doctor

The `doctor` command runs health checkups across 6 categories:

```bash
# Run all checkups
botnexus doctor

# Filter by category
botnexus doctor --category Configuration
```

**Categories:** Configuration, Security, Connectivity, Extensions, Permissions, Resources.

13 checkups are included. Five of them support **auto-fix** (creating missing directories, fixing permissions, generating default config). Auto-fix runs automatically via the Gateway's `/api/doctor` endpoint and the `CheckupRunner.RunAndFixAsync` API.

| Auto-Fix Checkup | What It Fixes |
|---|---|
| ConfigValidCheckup | Creates default config.json if missing |
| TokenPermissionsCheckup | Fixes token directory permissions |
| ExtensionsFolderExistsCheckup | Creates missing extension folders |
| HomeDirWritableCheckup | Creates home directory if missing |
| LogDirWritableCheckup | Creates logs directory if missing |

---

## 14. Security

### Gateway API key

By default, the Gateway has **no API key** — all requests are allowed. To secure it, add an API key to your config:

```json
"Gateway": {
  "Host": "0.0.0.0",
  "Port": 18790,
  "ApiKey": "your-secret-api-key-here"
}
```

Once set, all requests to `/api/*` and `/ws` endpoints require authentication:

```bash
# Via header (recommended)
curl -H "X-Api-Key: your-secret-api-key-here" http://localhost:18790/api/agents

# Via query parameter
curl "http://localhost:18790/api/agents?apiKey=your-secret-api-key-here"
```

WebSocket connections also require the API key:

```
ws://localhost:18790/ws?apiKey=your-secret-api-key-here
```

> **Note:** The `/health` and `/ready` endpoints are **not** protected by the API key — they're always accessible for monitoring.

### OAuth token management

OAuth tokens are stored as JSON files in `~/.botnexus/tokens/`:

```
~/.botnexus/tokens/
└── copilot.json     # GitHub Copilot OAuth token
```

Tokens are cached in memory and refreshed automatically. If you need to force re-authentication, delete the token file and restart:

```bash
# Force re-authentication
rm ~/.botnexus/tokens/copilot.json
```

### Extension security

Extensions are loaded from `~/.botnexus/extensions/` as .NET assemblies. Only place trusted assemblies in this directory — extensions have full access to the BotNexus runtime.

### Channel allowlists

Each channel supports an `AllowFrom` list that restricts which users or chat IDs can interact with your agents. Always configure this for public-facing channels.

---

## 15. Next Steps

You're up and running! Here's where to go from here:

- **[Architecture Overview](architecture.md)** — Understand how Gateway, Agents, Providers, and Channels fit together
- **[Configuration Guide](configuration.md)** — Full reference for every configuration option
- **[Extension Development](extension-development.md)** — Build custom channels, providers, and tools
- **[Workspace & Memory](workspace-and-memory.md)** — Deep dive into workspace files and the memory system
- **[Cron & Scheduling](cron-and-scheduling.md)** — Complete cron job configuration and built-in actions

---

*Built with care. If something in this guide doesn't match your experience, check the [Configuration Guide](configuration.md) for the latest reference or file an issue.*
