# BotNexus CLI Reference

The `botnexus` command-line tool provides quick access to configuration and agent management without editing `config.json` manually.

## Table of Contents

1. [Global Options](#global-options)
2. [install](#install) — Clone the repository
3. [build](#build) — Build the solution
4. [serve](#serve) — Start a service (gateway or probe)
5. [validate](#validate) — Validate configuration
6. [init](#init) — Initialize home directory
7. [agent list](#agent-list) — List configured agents
8. [agent add](#agent-add) — Add an agent
9. [agent remove](#agent-remove) — Remove an agent
10. [config get](#config-get) — Read a config value
11. [config set](#config-set) — Set a config value
12. [config schema](#config-schema) — Generate JSON schema
13. [Examples](#examples)

---

## Global Options

All commands support these options:

### `--verbose` (or `-v`)

Show additional command output, including file paths and full JSON responses.

```powershell
botnexus init --verbose
botnexus agent list --verbose
```

---

## install

Clone the BotNexus repository and optionally build it. There is no separate install process — BotNexus runs directly from a cloned repository.

### Usage

```powershell
botnexus install [OPTIONS]
```

### Options

| Option | Default | Description |
|---|---|---|
| `--path <DIR>` | `%USERPROFILE%\botnexus` | Target directory for the clone. |
| `--repo <URL>` | GitHub repo URL | Git repository URL to clone. |
| `--build` | off | Build the solution in Release configuration after cloning. |
| `--verbose` | — | Show detailed output from git and build. |

### Examples

**Clone to the default location:**

```powershell
botnexus install
```

**Clone and build in one step:**

```powershell
botnexus install --build
```

**Clone to a custom directory:**

```powershell
botnexus install --path D:\projects\botnexus
```

If the repository already exists at the target path, the command prints a message and skips the clone.

---

## build

Build the BotNexus solution in Release configuration. Always produces a Release build so that output assemblies don't conflict with Debug builds used during local development and testing.

### Usage

```powershell
botnexus build [OPTIONS]
```

### Options

| Option | Default | Description |
|---|---|---|
| `--path <DIR>` | Install location | Path to the repository root. |
| `--dev` | off | Use the current working directory as the repo root instead of the install location. |
| `--verbose` | — | Show full build output. |

### Repo resolution

The build command resolves the repo root in this order:

1. `--path` — explicit path always wins
2. `--dev` — uses the current working directory (for working in a separate dev clone)
3. Default — `%USERPROFILE%\botnexus` (the install location)

### Examples

**Build from the default install location:**

```powershell
botnexus build
```

**Build from a dev clone (current directory):**

```powershell
cd D:\repos\botnexus
botnexus build --dev
```

**Build a specific repo path:**

```powershell
botnexus build --path D:\repos\botnexus
```

---

## serve

Start a BotNexus service. Defaults to the gateway if no subcommand is specified. The serve command verifies a Release build exists, deploys extensions to `~/.botnexus/extensions/`, checks port availability, and starts the process.

If the process exits or crashes, serve waits 5 seconds and restarts automatically. Press `q` during the countdown to quit instead.

### Usage

```powershell
botnexus serve [OPTIONS]
botnexus serve gateway [OPTIONS]
botnexus serve probe [OPTIONS]
```

### serve / serve gateway

Start the BotNexus Gateway.

| Option | Default | Description |
|---|---|---|
| `--port <PORT>` | `5005` | Port to listen on. |
| `--path <DIR>` | Install location | Path to the repository root. |
| `--dev` | off | Use the current working directory as the repo root. |
| `--verbose` | — | Show detailed output. |

### serve probe

Start the BotNexus Probe diagnostic tool.

| Option | Default | Description |
|---|---|---|
| `--port <PORT>` | `5050` | Port for the Probe web UI. |
| `--path <DIR>` | Install location | Path to the repository root. |
| `--dev` | off | Use the current working directory as the repo root. |
| `--gateway-url <URL>` | `http://localhost:5005` | URL of a running BotNexus Gateway. |
| `--verbose` | — | Show detailed output. |

### Examples

**Start the gateway from the install location:**

```powershell
botnexus serve
```

**Start the gateway from a dev clone:**

```powershell
cd D:\repos\botnexus
botnexus serve --dev
```

**Start the gateway on a custom port:**

```powershell
botnexus serve gateway --port 8080
```

**Start the probe connected to a running gateway:**

```powershell
botnexus serve probe --gateway-url http://localhost:5005
```

### Production vs. development

| Scenario | Command |
|---|---|
| Run from the default install clone | `botnexus serve` |
| Run from your active dev repo | `botnexus serve --dev` |
| Build and serve in one flow | `botnexus build --dev && botnexus serve --dev` |

Both modes produce Release builds so the gateway DLLs don't collide with Debug builds from your IDE or test runner.

---

## validate

Validate the BotNexus configuration file.

### Usage

```powershell
botnexus validate [OPTIONS]
```

### Options

| Option | Description |
|---|---|
| `--remote` | Validate using the running gateway `/api/config/validate` endpoint instead of local files. |
| `--gateway-url <URL>` | Override the gateway base URL for remote validation (default: `http://localhost:5005`). |
| `--verbose` | Show detailed validation output. |

### Examples

**Local validation (offline):**

```powershell
botnexus validate
```

Expected output (success):

```text
Configuration is valid.
```

**Remote validation (requires running gateway):**

```powershell
botnexus validate --remote
```

**Custom gateway URL:**

```powershell
botnexus validate --remote --gateway-url http://api.example.com:8080
```

---

## init

Initialize `~/.botnexus/` with a default configuration and required directories.

Creates:
- `~/.botnexus/config.json` — default platform configuration
- `~/.botnexus/agents/` — agent workspace directories
- `~/.botnexus/sessions/` — session storage
- `~/.botnexus/tokens/` — OAuth token storage
- `~/.botnexus/logs/` — log directory

### Usage

```powershell
botnexus init [OPTIONS]
```

### Options

| Option | Description |
|---|---|
| `--force` | Overwrite existing `config.json`. Use with caution. |
| `--verbose` | Show the full default configuration in JSON format. |

### Examples

**First-time initialization:**

```powershell
botnexus init
```

Expected output:

```text
Initialized BotNexus home at: C:\Users\<YourName>\AppData\Local\BotNexus
Created config: C:\Users\<YourName>\AppData\Local\BotNexus\config.json
Next steps:
  - botnexus validate
  - botnexus agent list
```

**See the default config:**

```powershell
botnexus init --verbose
```

Displays the JSON configuration that was created (or would be created if `--force` is used).

**Reinitialize (overwrite existing):**

```powershell
botnexus init --force
```

---

## agent list

List all configured agents from `config.json`.

### Usage

```powershell
botnexus agent list [OPTIONS]
```

### Options

| Option | Description |
|---|---|
| `--verbose` | Show the full config file path. |

### Examples

**List agents:**

```powershell
botnexus agent list
```

Expected output:

```text
Agents:
  assistant  provider=copilot  model=gpt-4.1  enabled=true
  coder      provider=openai           model=gpt-4    enabled=true
  reviewer   provider=anthropic        model=claude-3-sonnet  enabled=false
```

**Verbose output (shows config file path):**

```powershell
botnexus agent list --verbose
```

```text
Agents:
  assistant  provider=copilot  model=gpt-4.1  enabled=true
Loaded from: C:\Users\<YourName>\AppData\Local\BotNexus\config.json
```

---

## agent add

Add a new agent to the configuration.

### Usage

```powershell
botnexus agent add <ID> [OPTIONS]
```

### Arguments

| Argument | Description |
|---|---|
| `<ID>` | Unique agent identifier (e.g., `assistant`, `coder`, `reviewer`). |

### Options

| Option | Default | Description |
|---|---|---|
| `--provider` | `copilot` | Agent provider name (e.g., `copilot`, `openai`, `anthropic`). |
| `--model` | `gpt-4.1` | Model name for this agent (e.g., `gpt-4o`, `claude-3-sonnet`). |
| `--enabled` | `true` | Whether the agent is enabled (`true` or `false`). |
| `--verbose` | — | Show the updated configuration. |

### Examples

**Add an agent with defaults:**

```powershell
botnexus agent add coder
```

Output:

```text
Added agent 'coder'.
```

**Add an agent with custom provider and model:**

```powershell
botnexus agent add researcher --provider openai --model gpt-4o
```

**Add a disabled agent:**

```powershell
botnexus agent add experimental --provider anthropic --model claude-3-sonnet --enabled false
```

**Verbose output (see updated config):**

```powershell
botnexus agent add assistant --verbose
```

---

## agent remove

Remove an agent from the configuration.

### Usage

```powershell
botnexus agent remove <ID> [OPTIONS]
```

### Arguments

| Argument | Description |
|---|---|
| `<ID>` | Agent identifier to remove. |

### Options

| Option | Description |
|---|---|
| `--verbose` | Show the updated configuration. |

### Examples

**Remove an agent:**

```powershell
botnexus agent remove experimental
```

Output:

```text
Removed agent 'experimental'.
```

**Warning if removing the default agent:**

```powershell
botnexus agent remove assistant
```

Output (warning):

```text
Warning: removing default agent 'assistant'. Update gateway.defaultAgentId if needed.
Removed agent 'assistant'.
```

---

## config get

Read a configuration value by its dotted key path.

### Usage

```powershell
botnexus config get <KEY> [OPTIONS]
```

### Arguments

| Argument | Description |
|---|---|
| `<KEY>` | Dotted path to config value (e.g., `gateway.listenUrl`, `agents.assistant.model`). |

### Options

| Option | Description |
|---|---|
| `--verbose` | Show additional context. |

### Examples

**Get the gateway listen URL:**

```powershell
botnexus config get gateway.listenUrl
```

Output:

```text
http://localhost:5005
```

**Get an agent's model:**

```powershell
botnexus config get agents.assistant.model
```

Output:

```text
gpt-4.1
```

**Get a nested value:**

```powershell
botnexus config get gateway.defaultAgentId
```

Output:

```text
assistant
```

---

## config set

Set a configuration value by its dotted key path.

### Usage

```powershell
botnexus config set <KEY> <VALUE> [OPTIONS]
```

### Arguments

| Argument | Description |
|---|---|
| `<KEY>` | Dotted path to config value (e.g., `gateway.listenUrl`). |
| `<VALUE>` | New value (as a string). For booleans, use `true` or `false`. |

### Options

| Option | Description |
|---|---|
| `--verbose` | Show the updated value after setting. |

### Examples

**Change the default agent:**

```powershell
botnexus config set gateway.defaultAgentId coder
```

Output:

```text
Set gateway.defaultAgentId = coder
```

**Change the gateway listen URL:**

```powershell
botnexus config set gateway.listenUrl http://localhost:8080
```

Output:

```text
Set gateway.listenUrl = http://localhost:8080
```

**Enable an agent:**

```powershell
botnexus config set agents.coder.enabled true
```

Output:

```text
Set agents.coder.enabled = true
```

**Disable an agent:**

```powershell
botnexus config set agents.reviewer.enabled false
```

Output:

```text
Set agents.reviewer.enabled = false
```

---

## config schema

Generate a JSON schema file for the platform configuration model.

### Usage

```powershell
botnexus config schema [OPTIONS]
```

### Options

| Option | Default | Description |
|---|---|---|
| `--output` | `docs\botnexus-config.schema.json` | Output file path for the generated schema. |
| `--verbose` | — | Show the generated schema content. |

### Examples

**Generate schema (default path):**

```powershell
botnexus config schema
```

Output:

```text
Generated schema: docs\botnexus-config.schema.json
```

**Custom output path:**

```powershell
botnexus config schema --output my-schema.json
```

---

## Examples

### Quick Setup Flow

**1. Clone and build:**

```powershell
botnexus install --build
```

**2. Initialize home directory:**

```powershell
botnexus init
```

**3. List default agents:**

```powershell
botnexus agent list
```

**4. Validate configuration:**

```powershell
botnexus validate
```

**5. Start the gateway:**

```powershell
botnexus serve
```

### Configuration Workflow

**Change the listening port:**

```powershell
# Update config
botnexus config set gateway.listenUrl http://localhost:8080

# Validate
botnexus validate

# Restart gateway (required for port changes)
.\scripts\start-gateway.ps1 -Port 8080
```

**Manage multiple agents:**

```powershell
# List current agents
botnexus agent list

# Add a new agent
botnexus agent add researcher --provider openai --model gpt-4o

# Switch the default agent
botnexus config set gateway.defaultAgentId researcher

# Verify
botnexus agent list
botnexus validate
```

---

## Tips & Tricks

### Dotted Key Paths

Configuration keys use dotted notation: `section.subsection.key`

```powershell
# Gateway settings
botnexus config get gateway.listenUrl
botnexus config get gateway.defaultAgentId

# Agent settings
botnexus config get agents.assistant.model
botnexus config get agents.assistant.enabled
```

### Hot Reload

Most config changes are applied immediately when the Gateway is running:

- Agent properties (enabled, model, provider)
- Provider settings
- Default agent ID

⚠️ **Requires restart:**
- `gateway.listenUrl` (port binding)

### Config File Location

| OS | Default Path |
|---|---|
| Windows | `%LOCALAPPDATA%\BotNexus\config.json` |
| macOS/Linux | `~/.botnexus/config.json` |
| Custom | `$env:BOTNEXUS_HOME/config.json` |

Override with environment variable:

```powershell
$env:BOTNEXUS_HOME = "C:\custom\botnexus"
botnexus agent list
```

### Exit Codes

All commands return:

- `0` — Success
- `1` — Error (check console output for details)

---

## See Also

- [Configuration Guide](configuration.md) — Complete configuration reference
- [Getting Started](getting-started.md) — Onboarding guide
- [Developer Guide](getting-started-dev.md) — Dev workflow and scripts
