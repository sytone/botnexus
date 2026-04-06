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

Restore dependencies and build the entire solution:

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
- If the build fails with "project not found", ensure you're in the repo root directory

---

## 2. Run the Gateway

The quickest way to get started is to run the Gateway directly from the repository:

```powershell
.\scripts\start-gateway.ps1
```

This builds and starts the Gateway at `http://localhost:5005`.

**To run tests before starting:**

```powershell
.\scripts\dev-loop.ps1
```

This builds the solution, runs all Gateway tests, and then starts the Gateway. If tests fail, the Gateway won't start.

**For watch mode** — auto-rebuilds on source changes:

```powershell
.\scripts\dev-loop.ps1 -Watch
```

The Gateway will be ready when you see output like:

```
🚀 Starting Gateway API
   URL:        http://localhost:5005
   WebUI:      http://localhost:5005/webui
```

---

## 3. Initialize BotNexus Home

The Gateway automatically creates `~/.botnexus/` on first run with default configuration. The directory structure:

```
~/.botnexus/
├── config.json           # Platform configuration
├── auth.json             # Provider credentials (OAuth tokens)
├── agents/               # Agent workspace directories
│   └── assistant/
│       ├── SOUL.md       # Core personality
│       ├── IDENTITY.md   # Role and constraints
│       ├── USER.md       # User preferences
│       └── MEMORY.md     # Long-term knowledge
├── sessions/             # Conversation history (JSONL)
├── tokens/               # OAuth tokens
├── extensions/           # Extension DLLs (if customized)
└── logs/                 # Daily log files
```

Override the location with an environment variable:

```bash
# Windows (PowerShell)
$env:BOTNEXUS_HOME = "C:\custom\botnexus"

# macOS/Linux
export BOTNEXUS_HOME=/custom/botnexus
```

---

## 5. Run the Gateway in Dev Mode

Start the Gateway directly from the repository:

```bash
# Windows (PowerShell)
.\scripts\start-gateway.ps1

# macOS/Linux
./scripts/start-gateway.ps1
```

Or use the full dev loop (build + test + run):

```bash
.\scripts\dev-loop.ps1
```

The Gateway starts at `http://localhost:5005` by default.

**Custom port:**

```bash
.\scripts\start-gateway.ps1 -Port 8080
```

**Watch mode** — auto-recompiles on source changes:

```bash
.\scripts\dev-loop.ps1 -Watch
```

### Verify it's running

Check the health endpoint:

```bash
curl http://localhost:5005/health
```

Expected response:

```json
{
  "status": "ok"
}
```

---

## 6. Configure Your First Provider (Copilot)

Edit `~/.botnexus/config.json` to add the Copilot provider:

```json
{
  "providers": {
    "copilot": {
      "apiKey": "auth:copilot",
      "baseUrl": "https://api.githubcopilot.com"
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

### The OAuth device code flow

When you first send a message to the agent, you'll see:

```
Go to https://github.com/login/device and enter code: ABCD-1234
```

**Steps:**

1. Open **https://github.com/login/device** in your browser
2. Enter the code shown (e.g., `ABCD-1234`)
3. Click **Authorize** when prompted
4. BotNexus receives the token and saves it to `~/.botnexus/auth.json`

The token is cached and refreshed automatically.

---

## 7. Customize Your Agent (Optional)

BotNexus creates workspace files for each agent in `~/.botnexus/agents/{agent-name}/`:

```
~/.botnexus/agents/assistant/
├── SOUL.md           # Core personality and values
├── IDENTITY.md       # Role, style, and constraints
├── USER.md           # User preferences
└── MEMORY.md         # Long-term distilled knowledge
```

**Edit `~/.botnexus/agents/assistant/SOUL.md`:**

```markdown
# Soul

You are a helpful, thoughtful assistant. You value clarity and precision.
You admit when you don't know something rather than guessing.
```

**Edit `~/.botnexus/agents/assistant/IDENTITY.md`:**

```markdown
# Identity

- Name: Assistant
- Role: General-purpose AI assistant
- Style: Conversational but efficient
- Constraints: Never execute destructive operations without confirmation
```

Changes take effect on your **next message** — no restart required.

---

## 8. Open the WebUI

Open your browser to:

```
http://localhost:5005/
```

Or directly to the WebUI:

```
http://localhost:5005/webui
```

You should see the BotNexus web interface.

### Send Your First Message

1. **Type your message** in the input box (e.g., "Hello! What can you do?")
2. **Press `Enter` or click Send**
3. **Watch the agent respond** — responses stream in real-time

The session is created automatically and persists across Gateway restarts.

### Explore the Interface

- **Sessions** — Browse past conversations
- **API Docs** — View REST API at `/swagger`
- **Health Check** — Test the gateway at `/health`

### Tips & Tricks

- **Multiple tabs supported** — open multiple browser tabs for parallel conversations
- **Shift+Enter** to add line breaks without sending
- **Copy session ID** — URL-safe session identifiers for sharing

---

## 9. Dev Workflow: Making Changes

When developing BotNexus, you'll iterate through this cycle:

### A. Changing Gateway Code

1. **Edit your code** in `src/gateway/BotNexus.Gateway.Api/` or `src/gateway/BotNexus.Gateway/`
2. **Stop the running Gateway** (Ctrl+C)
3. **Rebuild and run:**

```bash
.\scripts\start-gateway.ps1
```

Or with watch mode for continuous rebuilds:

```bash
.\scripts\dev-loop.ps1 -Watch
```

### B. Changing Agent Personality

1. **Edit workspace file** (e.g., `~/.botnexus/agents/assistant/SOUL.md`)
2. **No restart required** — changes take effect on the next message
3. **Send a new message** to see the updated behavior

### C. Changing Configuration

1. **Edit `~/.botnexus/config.json`**
2. **Changes hot-reload automatically** within 500ms (except `gateway.listenUrl`, which requires restart)
3. **Refresh the WebUI** to see the changes

---

## 10. Running Tests

Run the full test suite:

```bash
dotnet test
```

Run Gateway tests only:

```bash
dotnet test tests/BotNexus.Gateway.Tests
```

Verbose output:

```bash
dotnet test --verbosity detailed
```

Tests use an isolated `BOTNEXUS_HOME` so they don't interfere with your development environment.

---

## 11. Validate Configuration

Validate your `config.json` file before running the Gateway:

```bash
dotnet run --project src/gateway/BotNexus.Cli -- validate
```

This checks for syntax errors, missing required fields, and invalid provider configurations.

### Common Configuration Issues

| Symptom | Fix |
|---|---|
| "No agents configured" | Add an `agents` section to `config.json` |
| "Unknown provider" | Ensure the provider name exists in `providers` section |
| "Invalid JSON" | Check JSON syntax in `~/.botnexus/config.json` |
| "Port in use" | Use `--port` flag or change `gateway.listenUrl` |

---

## 12. Common Dev Tasks

### View Gateway logs

```bash
# Windows (PowerShell)
Get-Content $env:USERPROFILE\.botnexus\logs\botnexus-*.log -Tail 50

# macOS/Linux
tail ~/.botnexus/logs/botnexus-*.log
```

### List registered agents

```bash
curl http://localhost:5005/api/agents
```

### Check a specific agent

```bash
curl http://localhost:5005/api/agents/assistant
```

### Reset your dev environment

To start fresh (warning: deletes config, agents, and sessions):

```bash
# Windows (PowerShell)
Remove-Item $env:USERPROFILE\.botnexus -Recurse -Force

# macOS/Linux
rm -rf ~/.botnexus
```

Then reinitialize by running the Gateway again.

---

## 13. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `dotnet: command not found` | .NET SDK not installed | Install .NET 10+ from https://dotnet.microsoft.com/download |
| Build fails | Stale artifacts | `dotnet clean && dotnet build BotNexus.slnx` |
| Port 5005 already in use | Another process on the port | Use `-Port 8080` or stop the other process |
| Gateway won't start | Invalid config JSON | Run `dotnet run --project src/gateway/BotNexus.Cli -- validate` |
| WebUI shows "Disconnected" | Gateway crashed or not running | Restart: `.\scripts\start-gateway.ps1` |
| OAuth code expired | Took too long to authorize | Send another message to trigger a fresh code |
| Tests fail | Stale build or test isolation | `dotnet clean && dotnet build && dotnet test` |
| Config changes ignored | Watching limitation | Changes to `gateway.listenUrl` require a restart |

---

## Next Steps

- **[Development Loop](dev-loop.md)** — Quick reference for build, test, and run commands
- **[Configuration Guide](configuration.md)** — Full reference for config.json options
- **[Architecture Overview](architecture.md)** — Understand how BotNexus works
- **[Extension Development](extension-development.md)** — Build custom channels and tools

---

*For released versions, see [Getting Started (Release)](getting-started-release.md). For more information, see [README](../README.md).*
