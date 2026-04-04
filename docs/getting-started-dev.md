# Getting Started from Source — Developer Setup

> For developers building BotNexus from the cloned repository. If you're installing a released version, see [Install from Release](getting-started-release.md) instead.

---

## Prerequisites

| Requirement | Details |
|---|---|
| **.NET 10+ SDK** | [Download](https://dotnet.microsoft.com/download). Verify with `dotnet --version`. You need the SDK, not just the runtime. |
| **Git** | Any recent version. Verify with `git --version`. |
| **GitHub account** | Required for the Copilot provider's OAuth flow. You need an active GitHub Copilot subscription. |

Optional but recommended:

- **curl** — for testing API endpoints (built into modern Windows and macOS)
- A WebSocket-capable browser — for the built-in WebUI

---

## 1. Clone and Build

Clone the BotNexus repository:

```bash
git clone https://github.com/your-org/botnexus.git
cd botnexus
```

Build the entire solution:

```bash
dotnet build BotNexus.slnx
```

Expected output (last lines):

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

**Troubleshooting:**

- If you see "SDK version not found", run `dotnet --list-sdks` to verify .NET 10+ is installed
- If the build fails, check that all prerequisites are installed

---

## 2. Install the CLI Locally

The BotNexus CLI can be installed as a global .NET tool for convenient command-line access. Two options:

### Option A: Install from Local Build (Recommended)

Run the bootstrap script — it packs the CLI and installs it as a global dotnet tool:

```powershell
# Windows (PowerShell)
./scripts/install-cli.ps1
```

```bash
# macOS/Linux
./scripts/install-cli.sh
```

Verify installation:

```bash
botnexus --version
```

You should see `0.0.0-dev.{short-hash}` (e.g., `0.0.0-dev.d2b8527`).

> **Updating the CLI:** After code changes, run the same script again — it detects an existing install and uses `dotnet tool update` automatically.

### Option B: Run Directly from Repo

Alternatively, run the CLI directly without installing:

```bash
dotnet run --project src/BotNexus.Cli -- --help
```

(Replace `--help` with any `botnexus` command.)

---

## 3. Understanding Dev vs Release Builds

BotNexus uses semantic versioning with a development mode:

| Build Type | Version Format | Example |
|---|---|---|
| Release | SemVer from git tag | `0.1.0` (from tag `v0.1.0`) |
| Dev (clean) | `0.0.0-dev.{short-hash}` | `0.0.0-dev.d2b8527` |
| Dev (dirty) | `0.0.0-dev.{hash}.dirty` | `0.0.0-dev.d2b8527.dirty` |

To see the current dev version:

```bash
botnexus --version
```

---

## 4. Initialize BotNexus Home

Run the interactive setup to create `~/.botnexus/`:

```bash
botnexus install
```

Or install to a custom location:

```bash
botnexus install --install-path /custom/path
```

Override at runtime with the `BOTNEXUS_HOME` environment variable:

```bash
# Windows (PowerShell)
$env:BOTNEXUS_HOME = "C:\custom\botnexus"
botnexus start

# macOS/Linux
export BOTNEXUS_HOME=/custom/botnexus
botnexus start
```

This command:
1. Creates the home directory at `~/.botnexus/` (or custom path)
2. Copies extension assemblies from the repo to the install location
3. Generates a default `config.json`

**What gets created:**

```
~/.botnexus/
├── config.json              # Your configuration
├── extensions/
│   ├── providers/           # Provider DLLs
│   ├── channels/            # Channel DLLs
│   └── tools/               # Tool DLLs
├── agents/                  # Agent workspace directories
├── tokens/                  # OAuth tokens
├── sessions/                # Conversation history (JSONL)
└── logs/                    # Daily log files
```

Verify:

```bash
botnexus status
```

---

## 5. Run the Gateway in Dev Mode

You have two options for running the gateway during development:

### Option A: Use the Installed Gateway (Recommended for First-Time)

Start the installed gateway:

```bash
botnexus start
```

This runs the gateway as a background service and is good for general development.

### Option B: Run Direct from Source (For Live Development)

Run the Gateway directly with hot-reload capabilities:

```bash
dotnet run --project src/BotNexus.Gateway
```

This is useful when you're actively changing gateway code and want immediate feedback. Output appears in your console, and you can stop it with Ctrl+C.

**For development with live extension changes:**

```bash
dotnet run --project src/BotNexus.Gateway --configuration Debug
```

### Verify it's running

Check the health endpoint:

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

---

## 6. Configure Your First Provider (Copilot)

BotNexus doesn't ship with a pre-configured provider. Add the Copilot provider to your dev config:

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

**Note:** The `ExtensionsPath` should point to where you copied extension assemblies during `botnexus install`. This is typically `~/.botnexus/extensions` for development.

### The OAuth device code flow

When BotNexus first needs to call the Copilot API, you'll see:

```
info: BotNexus.Providers.Copilot.GitHubDeviceCodeFlow[0]
      Go to https://github.com/login/device and enter code: ABCD-1234
```

**Steps:**

1. Open **https://github.com/login/device** in your browser
2. Enter the code shown (e.g., `ABCD-1234`)
3. Click **Authorize** when prompted
4. BotNexus receives the token and saves it to `~/.botnexus/tokens/copilot.json`

The token is cached and refreshed automatically.

---

## 7. Create Your First Agent

Add an agent to your config:

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

BotNexus creates a workspace directory for your agent:

```
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

## 8. Open the WebUI

Open your browser to:

```
http://localhost:18790/
```

You should see the BotNexus web interface.

### The Web Interface Layout

#### **Left Sidebar** — Navigation & Extensions
- **💬 New Chat** — Create a new chat session instantly
- **📋 Sessions** — Browse past conversations
- **📡 Channels** — View connected messaging channels and their status
- **🧠 Agents** — See available agents with their models
- **🧩 Extensions** — Panel showing loaded providers, tools, and health status
- **📊 Activity Monitor** — Real-time feed of all messages
- **🌐 Connection Status** — Shows WebSocket connection state

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

### Tips & Tricks

- **Shift+Enter** to add line breaks without sending
- **Click a session** to reload and continue conversing
- **Refresh buttons** (↻) in each section reload that panel's data
- **Multiple tabs supported** — open multiple WebUI tabs for parallel conversations

---

## 9. Dev Workflow: Making Changes

When developing BotNexus, you'll iterate through this cycle:

### A. Changing Gateway or Provider Code

1. **Edit your code** (in `src/BotNexus.Gateway/` or `src/BotNexus.Providers.*/`)
2. **Rebuild**: `dotnet build` (or `dotnet build -c Release` for Release builds)
3. **Pack**: `./scripts/pack.ps1` — rebuilds all nupkg packages in `artifacts/`
4. **Install**: `botnexus install` — deploys fresh packages to the install location
5. **Restart the gateway**: `botnexus stop && botnexus start`
6. **Test**: Reload the WebUI or send a test message

> **Why pack?** `botnexus install` reads pre-built `.nupkg` files from `artifacts/`. If you skip `pack.ps1`, the install deploys stale binaries and your changes won't take effect.

### B. Changing Agent Personality

1. **Edit workspace file** (e.g., `~/.botnexus/agents/assistant/SOUL.md`)
2. **No restart required** — changes take effect on the next conversation
3. **Send a new message** to see the updated behavior

### C. Changing Extensions (Channels, Tools)

1. **Edit extension code** (in `src/channels/BotNexus.Channels.*/` or `src/BotNexus.Tools.*/`)
2. **Pack**: `./scripts/pack.ps1` — rebuilds all packages including extensions
3. **Install**: `botnexus install` — deploys fresh extension DLLs
4. **Restart the gateway**: `botnexus stop && botnexus start` to reload extensions

---

## 10. Building and Installing Updated CLI

After making changes to the CLI, rebuild and reinstall:

```bash
# Build in Release mode
dotnet build src/BotNexus.Cli -c Release

# Uninstall old version (if needed)
dotnet tool uninstall --global botnexus

# Install updated version
dotnet tool install --global --add-source .\src\BotNexus.Cli\bin\Release\net10.0 botnexus
```

---

## 11. Running Tests

Run the full test suite:

```bash
dotnet test
```

Run tests for a specific project:

```bash
dotnet test tests/BotNexus.Gateway.Tests
```

Run tests with verbose output:

```bash
dotnet test --verbosity detailed
```

### Test Isolation

Tests use an isolated `BOTNEXUS_HOME` to avoid interfering with your development environment. The `.runsettings` file defines this:

```xml
<RunSettings>
  <RunConfiguration>
    <EnvironmentVariables>
      <BOTNEXUS_HOME>{temp-test-home}</BOTNEXUS_HOME>
    </EnvironmentVariables>
  </RunConfiguration>
</RunSettings>
```

This ensures:
- Tests don't touch your real `~/.botnexus/` directory
- Each test run starts with a clean state
- Agent workspaces and tokens are isolated to the test session

---

## 12. Common Dev Tasks

### Update extension paths for development

If you're building extensions in the `extensions/` folder, make sure your config points to the repo extensions:

```json
{
  "BotNexus": {
    "ExtensionsPath": "C:\\repos\\botnexus\\extensions"
  }
}
```

Or use a relative path if running from the repo root:

```json
{
  "BotNexus": {
    "ExtensionsPath": "./extensions"
  }
}
```

### View dev logs in real-time

```bash
botnexus logs --follow
```

Or check the file directly:

```bash
# Windows (PowerShell)
Get-Content $env:USERPROFILE\.botnexus\logs\botnexus-*.log -Tail 50 -Wait

# macOS/Linux
tail -f ~/.botnexus/logs/botnexus-*.log
```

### Check what extensions are loaded

```bash
curl http://localhost:18790/api/extensions
```

### Reset your dev environment

To start fresh (warning: deletes config, agents, and tokens):

```bash
# Remove and recreate home directory
botnexus install --force
```

Or manually:

```bash
# Windows (PowerShell)
Remove-Item $env:USERPROFILE\.botnexus -Recurse -Force
botnexus install

# macOS/Linux
rm -rf ~/.botnexus
botnexus install
```

---

## 13. Next Steps

- **[Running Tests](testing.md)** — Set up and run the full test suite
- **[Extension Development](extension-development.md)** — Build custom channels, providers, and tools
- **[Workspace & Memory](workspace-and-memory.md)** — Deep dive into agent personality files
- **[Configuration Guide](configuration.md)** — Full reference for every config option
- **[Architecture Overview](architecture.md)** — Understand how BotNexus works

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| .NET SDK not found | .NET 10 SDK not installed | Run `dotnet --list-sdks` and install if missing |
| Build fails with unknown projects | File paths not resolved correctly | Run from repo root: `cd botnexus && dotnet build` |
| Port 18790 already in use | Another process using the port | Edit `~/.botnexus/config.json` and change `Gateway.Port`, or use a different `BOTNEXUS_HOME` |
| Extensions not loading | Extension path doesn't point to built DLLs | Verify `ExtensionsPath` in config.json and rebuild extensions |
| OAuth code expired | Took too long to authorize | Send another message to trigger a fresh code |
| WebUI shows "Disconnected" | Gateway crashed or not running | Check logs with `botnexus logs` or restart manually |
| Tests failing | Test isolation issue or stale build | Clean build: `dotnet clean && dotnet build` then `dotnet test` |

---

*Need help? Check [Getting Started](getting-started.md) or the [Configuration Guide](configuration.md).*
