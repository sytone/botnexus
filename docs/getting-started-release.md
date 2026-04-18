# Getting Started from Release — Installing BotNexus

> For users installing a released version of BotNexus. If you're building from source, see [Developer Setup](getting-started-dev.md) instead.

---

## Prerequisites

| Requirement | Details |
|---|---|
| **.NET 10+ Runtime** | [Download](https://dotnet.microsoft.com/download). Verify with `dotnet --version`. You only need the runtime, not the SDK. |
| **GitHub account** | Required for the Copilot provider's OAuth flow. You need an active GitHub Copilot subscription. |

Optional but recommended:

- **curl** — for testing API endpoints (built into modern Windows and macOS)
- A modern browser — for the built-in WebUI (SignalR client)

---

## 1. Install the CLI Tool

> ⚠️ **Coming soon:** GitHub Releases are not yet set up. For now, you can build and install locally (see [Developer Setup](getting-started-dev.md)). This section will be updated when releases are available.

When GitHub Releases are available, you'll be able to install the BotNexus CLI globally:

```bash
dotnet tool install --global BotNexus.Cli
```

Verify installation:

```bash
botnexus --version
```

You should see the semantic version (e.g., `0.1.0`).

---

## 2. Initialize BotNexus

Run the interactive setup:

```bash
botnexus install
```

This command:
1. Creates the BotNexus home directory at `~/.botnexus/`
2. Extracts the gateway and official extensions to `%LOCALAPPDATA%\BotNexus` (Windows) or a standard location (macOS/Linux)
3. Generates a default `config.json`

**What gets created:**

```text
~/.botnexus/
├── config.json              # Your configuration
├── extensions/
│   ├── providers/           # LLM provider assemblies
│   ├── channels/            # Channel assemblies (Telegram, Discord, Slack, etc.)
│   └── tools/               # Tool extension assemblies
├── agents/                  # Agent workspace directories
├── tokens/                  # OAuth tokens (encrypted)
├── sessions/                # Conversation history (JSONL)
└── logs/                    # Daily log files
```

Verify installation:

```bash
botnexus status
```

Expected output:

```text
Version:        0.1.0
Health:         Healthy
Gateway:        Not running
Config:         Valid
```

---

## 3. Start the Gateway

The **Gateway** is the core service that manages agents, handles messages, and loads extensions. Start it:

```bash
botnexus start
```

This runs the gateway in the background. You should see:

```text
info: BotNexus[0] BotNexus home: C:\Users\you\.botnexus
info: Microsoft.Hosting.Lifetime[14] Now listening on: http://0.0.0.0:18790
```

Check that it's running:

```bash
curl http://localhost:18790/health
```

Expected response:

```json
{
  "status": "Healthy",
  "checks": {
    "messageBus": { "status": "Healthy" },
    "providers": { "status": "Healthy" },
    "channels": { "status": "Healthy" },
    "extensionLoader": { "status": "Healthy" }
  }
}
```

**Tips:**

- Run in foreground for debugging: `botnexus start --foreground`
- View logs anytime: `botnexus logs`
- Stop the gateway: `botnexus stop`

---

## 4. Configure Your First Provider (Copilot)

BotNexus doesn't ship with a pre-configured provider. Add the Copilot provider to your config:

Edit `~/.botnexus/config.json`:

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
    }
  }
}
```

**Note:** The `ExtensionsPath` is automatically set during installation and should point to where provider DLLs are stored.

The config is loaded automatically — no restart required.

### The OAuth device code flow

When BotNexus first needs to call the Copilot API, you'll see:

```text
info: BotNexus.Agent.Providers.Copilot.GitHubDeviceCodeFlow[0]
      Go to https://github.com/login/device and enter code: ABCD-1234
```

**Steps:**

1. Open **https://github.com/login/device** in your browser
2. Enter the code shown (e.g., `ABCD-1234`)
3. Click **Authorize** when prompted
4. BotNexus receives the token and saves it to `~/.botnexus/tokens/copilot.json`

The token is cached and refreshed automatically. You only do this once (until the token expires).

**Troubleshooting:** If authorization times out (codes expire after 15 minutes), send another message to get a fresh code. If you see "access_denied", verify your GitHub account has an active Copilot subscription.

---

## 5. Create Your First Agent

Agents are named configurations with their own workspace, personality, and settings. Add one to your config:

Edit `~/.botnexus/config.json`:

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

The configuration is loaded automatically. BotNexus creates a workspace directory for your agent:

```text
~/.botnexus/agents/assistant/
├── SOUL.md           # Core personality and values
├── IDENTITY.md       # Role, style, and constraints
├── USER.md           # User preferences
├── MEMORY.md         # Long-term distilled knowledge
├── HEARTBEAT.md      # Periodic task instructions
└── memory/
    └── daily/        # Daily memory logs (YYYY-MM-DD.md)
```

### Customize your agent

Edit the workspace files to shape your agent's personality:

**`~/.botnexus/agents/assistant/SOUL.md`:**

```markdown
# Soul

You are a helpful, thoughtful assistant. You value clarity and precision.
You admit when you don't know something rather than guessing.
```

**`~/.botnexus/agents/assistant/IDENTITY.md`:**

```markdown
# Identity

- Name: Assistant
- Role: General-purpose AI assistant
- Style: Conversational but efficient. Use bullet points for complex answers.
- Constraints: Never execute destructive operations without confirmation.
```

**`~/.botnexus/agents/assistant/USER.md`:**

```markdown
# User

- Name: You
- Timezone: Pacific Time
- Preferences: Prefers concise answers. Values working code over pseudocode.
```

Changes take effect on your **next conversation** — no restart required.

---

## 6. Open the WebUI

The **WebUI** is the easiest way to chat with your agents.

Open your browser to:

```text
http://localhost:18790/
```

You should see the BotNexus web interface. The UI connects to the Gateway automatically via SignalR.

### The Web Interface Layout

#### **Left Sidebar** — Navigation & Extensions
- **💬 New Chat** — Create a new chat session instantly
- **📋 Sessions** — Browse past conversations
- **📡 Channels** — View connected messaging channels and their status
- **🧠 Agents** — See available agents with their models
- **🧩 Extensions** — Panel showing loaded providers, tools, and health status
- **📊 Activity Monitor** — Real-time feed of all messages
- **🌐 Connection Status** — Shows SignalR connection state

#### **Main Chat Area** — Your Conversation
- **Welcome Screen** — Displays when no session is selected
- **Chat Messages** — Conversation history with timestamps
- **Input Area** — Type your message and press `Enter` or click **Send**

### Send Your First Message

1. **Click "💬 Start New Chat"** in the sidebar
2. **Type your message** (e.g., "Hello! What can you do?")
3. **Press `Enter` or click Send**
4. **Watch the agent respond** — responses stream in real-time

The session is created automatically and persists across Gateway restarts.

### Understanding Sessions

**Sessions** are persistent conversations tied to a specific agent:
- Have a unique key (format: `channel:connection-id:agent-name`)
- Store full message history
- Can be paused and resumed anytime
- Persist across Gateway restarts and browser closes

Click any session in the sidebar to reload it and continue.

### Viewing Extensions

The **Extensions** panel shows all loaded components:

```text
✅ 4 loaded
❌ 0 failed
📡 1 channel
🧠 1 provider
🔧 15 tools
```

Expand **Providers** and **Tools** to see what's available.

### Tips & Tricks

- **Shift+Enter** to add line breaks without sending
- **Click a session** to reload and continue conversing
- **Refresh buttons** (↻) in each section reload that panel's data
- **Multiple tabs supported** — open multiple WebUI tabs for parallel conversations

---

## 7. Add More Agents (Optional)

Add more agents with different personalities or specialized roles:

Edit `~/.botnexus/config.json`:

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
      "Temperature": 0.3,
      "MaxTokens": 16384
    },
    "note-taker": {
      "Name": "note-taker",
      "Provider": "copilot",
      "Model": "gpt-4o",
      "Temperature": 0.0
    }
  }
}
```

Each agent gets its own workspace directory and appears in the WebUI **Agents** panel. You can target specific agents in the chat or via the API.

---

## 8. Manage Your System

### Check status

```bash
botnexus status
```

Shows version, health, and configuration status.

### View logs

```bash
botnexus logs --follow
```

View logs in real-time. Use `--lines N` to limit output.

### Stop the gateway

```bash
botnexus stop
```

### Restart the gateway

```bash
botnexus restart
```

### Update to a new version

When a new release is available:

```bash
botnexus update
```

This updates the CLI tool and gateway to the latest released version. Your configuration and agents are preserved.

### Health diagnostics

```bash
botnexus doctor
```

Runs health checkups across configuration, security, connectivity, extensions, permissions, and resources. Issues are reported with fixes.

---

## 9. Back Up Your Data

Your agents, conversation history, and configuration are stored in `~/.botnexus/`. Back it up:

### Create a backup

```bash
botnexus backup create
```

Creates an archive at `~/.botnexus-backups/` with a timestamp.

### List backups

```bash
botnexus backup list
```

### Restore from a backup

```bash
botnexus backup restore <backup-name>
```

---

## 10. Next Steps

- **[Using Channels](user-guide/extensions.md)** — Connect your agents to Telegram, Discord, Slack, or your own interfaces
- **[Configuring Cron Jobs](cron-and-scheduling.md)** — Automate recurring tasks
- **[Workspace & Memory](development/workspace-and-memory.md)** — Deep dive into agent personality files
- **[Configuration Guide](configuration.md)** — Full reference for every config option
- **[Architecture Overview](architecture/overview.md)** — Understand how BotNexus works

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Port already in use | Another process on 18790 | Edit `~/.botnexus/config.json` and change `Gateway.Port` |
| OAuth code expired | Took too long to authorize | Send another message to get a fresh code |
| WebUI shows "Disconnected" | Gateway isn't running | Run `botnexus start` |
| "No providers found" in health check | Provider DLLs not in extensions directory | Verify `ExtensionsPath` in config.json points to the correct location |
| Agent not appearing | Agent not in `Named` config section | Add agent to `Agents.Named` and reload config |
| Extension loading warnings | Missing extension folders on first run | Expected — folders are created on-demand |

---

*Need help? Check [Getting Started](getting-started.md) or the [Configuration Guide](configuration.md).*
