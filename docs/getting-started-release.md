# Getting Started from Release — Installing BotNexus

> Install the released BotNexus CLI, then use it to build and run the gateway from a checkout. For an edit/build workflow in your own development clone, see [Developer Setup](getting-started-dev.md).

---

## Install

```bash
# Install the CLI as a global dotnet tool
dotnet tool install -g BotNexus.Cli

# Clone and build the platform (one-time setup; requires the SDK and Git)
botnexus install --build

# Set up your home directory and configure a provider
botnexus init
botnexus provider setup

# Start the gateway
botnexus gateway start

# Open the portal at http://localhost:5005
```

> **No access to nuget.org?** If your network blocks the public NuGet feed, see [Installing Without nuget.org Access](guides/offline-install.md) for source-build, internal-mirror, and run-from-source alternatives.

---

## Prerequisites

| Requirement | Details |
|---|---|
| **.NET SDK** | [Download](https://dotnet.microsoft.com/download). The current source targets .NET 10; use an SDK compatible with the cloned repository's `global.json`. Check installed SDKs with `dotnet --list-sdks`. The runtime alone cannot build the gateway. |
| **Git** | Required by `botnexus install`. Verify with `git --version`. |
| **GitHub account** | Required for the Copilot provider's OAuth flow. You need an active GitHub Copilot subscription. |

Optional but recommended:

- **curl** — for testing API endpoints (built into modern Windows and macOS)
- A modern browser — for the built-in WebUI (SignalR client)

---

## 1. Clone and Build

The released global tool supplies the CLI. The gateway and extensions in this workflow are built from a source checkout; installing the CLI does not install a prebuilt gateway bundle. The default clone follows the repository's default branch, not necessarily the version of the installed CLI package.

### Clone the repository

After installing the global tool above, run:

```powershell
botnexus install --build
```

This clones to `~/botnexus` and builds in Release configuration. Without `--build`, `install` only clones. Do not use `dotnet run --project src/...` to bootstrap an installation before that source tree exists.

To clone to a custom location, use `--source`, not `--path`:

```powershell
# Windows example; use an appropriate absolute path on Linux
botnexus install --source D:/botnexus --build
```

For a non-default checkout, pass its path as `--source` to `gateway start` and `update`, but as `--path` to `build`. These commands do not share a single path-option name. If a checkout already exists, `install` keeps it rather than pulling updates; `--build` still builds it. To build the default existing checkout directly:

```powershell
botnexus build
```

---

## 2. Initialize BotNexus

Run the CLI to set up the home directory:

```powershell
botnexus init
```

This command creates the default home and required directories and writes a default `config.json`. An existing file is preserved unless you explicitly pass `--force`. Fresh configuration binds the gateway to loopback, selects SQLite session storage, and includes an `assistant` agent plus the bundled agents. Provider credentials still need setup.

`init` does **not** extract gateway or extension binaries. `gateway start` uses the source checkout's build and deploys extensions from it.

The default locations used as setup proceeds are:

| Path | Purpose / creation boundary |
|---|---|
| `~/.botnexus/config.json` | Initial configuration written by `init` |
| `~/.botnexus/auth.json` | OAuth credentials written by provider setup; protect this file as a secret |
| `~/.botnexus/agents/<agentId>/workspace/` | Agent instruction files and memory notes, scaffolded when its workspace is initialized |
| `~/.botnexus/sessions.sqlite` | Session-store path selected by fresh `init` configuration; created when the store opens |
| `~/.botnexus/extensions/` | Extension deployment under the home; not a prebuilt payload extracted by `init` |
| `~/.botnexus/logs/` | Runtime logs |

These are defaults, not a complete backup manifest. `--target` / `BOTNEXUS_HOME`, the data-directory override, and configured store paths can change where data resides. See [Configuration](configuration.md).

Check configuration and gateway state separately:

```bash
botnexus validate
botnexus gateway status
```

Before the first start, a stopped gateway is expected. There is no top-level `botnexus status` command.

---

## 3. Start the Gateway

The **Gateway** is the core service that manages agents, handles messages, and loads extensions. Start it:

```powershell
botnexus gateway start
```

Before this first start, complete [provider setup](#4-configure-your-first-provider-copilot) below. Starting builds the default checkout, deploys its extensions and launches a detached gateway. For foreground development instead, `botnexus serve` builds and runs with its restart loop; see [Developer Setup](getting-started-dev.md). Do not launch a second gateway against a home already in use.

Check that it's running:

```bash
curl http://localhost:5005/health
```

Expected response:

```json
{
  "status": "ok"
}
```

**Custom port:**

```powershell
botnexus config set gateway.listenUrl http://localhost:8080
botnexus gateway restart --port 8080
```

Set `gateway.listenUrl` as well: a configured listen URL takes precedence over the process URL argument. Restart interrupts active work, so choose an appropriate time.

---

## 4. Configure Your First Provider (Copilot)

BotNexus doesn't ship with a pre-configured provider. Configure GitHub Copilot with the interactive CLI wizard:

```powershell
botnexus provider setup --provider github-copilot
```

The wizard completes the OAuth setup and writes the provider through the active configuration backend. The same command works for JSON-backed and SQLite-backed homes.

### The OAuth device code flow

During provider setup, the wizard displays a verification URL and a device code. Follow the values in that prompt; the code is specific to your login attempt.

**Steps:**

1. Open **https://github.com/login/device** in your browser
2. Enter the code shown (e.g., `ABCD-1234`)
3. Click **Authorize** when prompted
4. The setup wizard saves OAuth credentials in the `github-copilot` entry of `~/.botnexus/auth.json`, with owner-only file permissions.

The token is cached and refreshed automatically. You only do this once (until the token expires).

**Troubleshooting:** Device codes expire according to the OAuth response. If authorization times out, rerun `botnexus provider setup --provider github-copilot` to request a fresh code. If you see "access_denied", verify your account's Copilot access.

---

## 5. Create Your First Agent

Agents are named configurations with their own workspace, personality, and settings. A fresh `init` already creates `assistant`; inspect it rather than trying to add the same id again:

```powershell
botnexus agent list
botnexus config set agents.assistant.provider github-copilot
botnexus config set agents.assistant.model gpt-4.1
botnexus config set gateway.defaultAgentId assistant
botnexus config set agents.defaults.toolTimeoutSeconds 300
botnexus config set agents.assistant.memory.enabled true
botnexus validate
```

The CLI writes through the active configuration backend. BotNexus initializes the agent workspace on first use. An example layout after instruction and memory files have been created is:

```text
~/.botnexus/agents/assistant/workspace/
├── SOUL.md           # Core personality and values
├── IDENTITY.md       # Role, style, and constraints
├── USER.md           # User preferences
├── MEMORY.md         # Long-term distilled knowledge
├── HEARTBEAT.md      # Periodic task instructions
└── memory/
    ├── 2026-04-01.md    # Daily memory notes (YYYY-MM-DD.md)
    └── ...            (one per day)
```

### Customize your agent

Edit the workspace files to shape your agent's personality:

**`~/.botnexus/agents/assistant/workspace/SOUL.md`:**

```markdown
# Soul

You are a helpful, thoughtful assistant. You value clarity and precision.
You admit when you don't know something rather than guessing.
```

**`~/.botnexus/agents/assistant/workspace/IDENTITY.md`:**

```markdown
# Identity

- Name: Assistant
- Role: General-purpose AI assistant
- Style: Conversational but efficient. Use bullet points for complex answers.
- Constraints: Never execute destructive operations without confirmation.
```

**`~/.botnexus/agents/assistant/workspace/USER.md`:**

```markdown
# User

- Name: You
- Timezone: Pacific Time
- Preferences: Prefers concise answers. Values working code over pseudocode.
```

For the current in-process strategy, configured prompt files are read when a new agent handle is created. An existing handle reuses its assembled prompt: saving a file does not guarantee a refresh on the next message or merely by navigating to another conversation. Recreate the handle through the normal lifecycle, or restart the gateway when safe, before relying on the edited prompt.

---

## 6. Open the WebUI

The **WebUI** is the easiest way to chat with your agents.

Open your browser to:

```text
http://localhost:5005/
```

You should see the BotNexus web interface. The UI connects to the Gateway automatically via SignalR.

> If you changed the listen address during setup, ask the Gateway rather than guessing:
> `botnexus config get gateway.listenUrl` prints the configured URL; `botnexus gateway status` checks the running process. A pending configuration change is not proof of the current bind.

### The Web Interface Layout

#### **Left Sidebar** — Navigation
- **Chat** — Select an agent and browse its conversations; **New** starts a conversation for the selected agent
- **Agents** — Inspect available agents and their configuration
- **Tools**, **Skills**, and **Plugins** — Browse the corresponding platform surfaces
- **Configuration**, **Cron**, and **Activity** — Inspect settings, schedules, and activity

Navigation order can be customized. The connection-status dot is in the top bar beside the logo, not a dedicated sidebar row.

#### **Main Chat Area** — Your Conversation
- **Welcome Screen** — Displays before a conversation is selected
- **Chat Messages** — Conversation history with timestamps
- **Input Area** — Type your message and press `Enter` or click **Send**

### Send Your First Message

1. Open **Chat**, select your agent, and click **New** beside **Conversations**
2. **Type your message** (e.g., "Hello! What can you do?")
3. **Press `Enter` or click Send**
4. **Watch the agent respond** — responses stream in real-time

Messages run within the selected conversation's session. The persistent conversation remains available across gateway restarts.

### Understanding Sessions

A **conversation** is the persistent named thread owned by an agent. A **session** is an execution/context lifetime within that conversation; the conversation can span multiple sessions and channels. Conversation and session ids are distinct opaque identifiers, not a `channel:connection-id:agent-name` routing recipe.

Reopen the conversation to continue its history. Starting a new session changes the agent's active context without making the conversation a different thread. See [Conversations](user-guide/conversations.md) for lifecycle and channel-binding details.

### Viewing Extensions

The **Extensions** panel shows the components loaded by this installation, grouped by type and health.
Counts vary with the installed extension set and enabled providers, so use the panel's live totals rather
than expecting a fixed number of channels, providers, or tools.

Expand **Providers** and **Tools** to see what's available.

### Tips & Tricks

- **Shift+Enter** to add line breaks without sending
- **Select a conversation** to reopen its history and continue conversing
- **Refresh buttons** (↻) in each section reload that panel's data
- **Multiple tabs supported** — open multiple WebUI tabs for parallel conversations

---

## 7. Add More Agents (Optional)

Add more agents with different personalities or specialized roles:

```powershell
botnexus agent add researcher --provider github-copilot --model gpt-4o --display-name Researcher
botnexus agent add note-taker --provider github-copilot --model gpt-4o --display-name "Note Taker"

# Add optional per-agent settings by dotted path
botnexus config set agents.researcher.toolIds '["read","web_search","web_fetch"]'
botnexus config set agents.note-taker.systemPromptFiles '["SOUL.md","IDENTITY.md"]'

botnexus agent list
botnexus validate
```

Each agent gets its own workspace directory and appears in the WebUI **Agents** panel. You can target specific agents in the chat or via the API.

---

## 8. Manage Your System

### Check status

```bash
botnexus gateway status
botnexus validate
```

The first checks the gateway process; the second validates configuration.

### View logs

```bash
botnexus debug logs tail --limit 200
```

Read the log files directly - no running gateway required. Use `botnexus debug logs errors` for
recent errors only, or `botnexus debug logs search --term "<text>"` to search across log files.

### Stop the gateway

```bash
botnexus gateway stop
```

### Restart the gateway

```bash
botnexus gateway restart
```

### Update to a new version

When a new release is available:

```bash
botnexus update
```

This pulls source, builds, deploys extensions and restarts the gateway when an update is needed; it is not a global-tool package update and does not select a release tag. It refuses a dirty checkout by default. Choose a maintenance window and review [the CLI reference](cli-reference.md#update) before updating. Update the global CLI package separately with `dotnet tool update -g BotNexus.Cli`.

### Health diagnostics

```bash
botnexus doctor
```

Runs the registered diagnostic checks and reports findings. See the [CLI reference](cli-reference.md#doctor) for the current check inventory and the distinction between repairs and advisory findings.

---

## 9. Back Up Your Data

The default home is `~/.botnexus/`, but home, data-directory, workspace and store settings can place data elsewhere. Inventory the effective paths before backing up. The CLI configuration-backup commands cover JSON configuration snapshots, not a complete installation or database backup.

### What the CLI backs up: `config.json`, automatically

The JSON writer copies an existing `config.json` before replacing a changed document. No-op writes do not create a backup, and there is nothing to copy when the file is absent. The backup service retains at most 50 copies; its directory is normally `backups/` beside configuration, with some initialization paths honoring the data-directory override. These copies do not constitute a snapshot of a SQLite-only configuration store.

### List backups

```bash
botnexus config backups list
```

Lists the retained `config.json` backups with a validity verdict for each — so you can see whether a
backup still loads against the current schema before you rely on it.

### Restore from a backup

```bash
botnexus config restore <id>
```

`<id>` is a backup id from `botnexus config backups list`. **Restore previews by default and writes
nothing.** Add `--commit` when you have read the preview and want it applied:

```bash
botnexus config restore <id> --commit
```

### What the CLI does NOT back up

The configuration-backup surface does not include the following. Back them up at their actual configured locations; for SQLite, use a database-consistent backup or stop all writers before a filesystem copy rather than copying an active database without its transactional state:

- agent workspaces (`~/.botnexus/agents/<agentId>/workspace/`), including `SOUL.md`, `AGENTS.md` and
  the `memory/` notes
- conversation and session stores, including the `sessions.sqlite` path selected by fresh `init` and any separately configured databases
- `cron.sqlite` and any SQLite configuration store
- stored secrets and provider credentials

---

## 10. Next Steps

- **[Using Channels](user-guide/extensions.md)** — Connect agents through the shipped channels or your own interfaces
- **[Configuring Cron Jobs](cron-and-scheduling.md)** — Automate recurring tasks
- **[Workspace & Memory](development/workspace-and-memory.md)** — Deep dive into agent personality files
- **[Configuration Guide](configuration.md)** — Full reference for every config option
- **[Architecture Overview](architecture/overview.md)** — Understand how BotNexus works

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Port already in use | Another process on 5005 | Run `botnexus config set gateway.listenUrl http://localhost:<PORT>` and restart the gateway |
| OAuth code expired | Took too long to authorize | Run `botnexus provider setup --provider github-copilot` to get a fresh code |
| WebUI shows "Disconnected" | Gateway isn't running | Run `botnexus gateway start` |
| "No providers found" in health check | No provider is configured | Run `botnexus provider setup` and then `botnexus validate` |
| Agent not appearing | Agent is absent or disabled | Run `botnexus agent list`, then add or enable it through the CLI |
| Extension loading warnings | Missing extension folders on first run | Expected — folders are created on-demand |

---

*Need help? Check [Getting Started](getting-started.md) or the [Configuration Guide](configuration.md).*
