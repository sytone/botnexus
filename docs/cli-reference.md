# BotNexus CLI Reference

The `botnexus` command-line tool provides quick access to configuration and agent management without editing `config.json` manually.

## Table of Contents

1. [Global Options](#global-options)
2. [validate](#validate) — Validate configuration
3. [init](#init) — Initialize home directory
4. [agent list](#agent-list) — List configured agents
5. [agent add](#agent-add) — Add an agent
6. [agent remove](#agent-remove) — Remove an agent
7. [config get](#config-get) — Read a config value
8. [config set](#config-set) — Set a config value
9. [Examples](#examples)

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

```
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

```
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

```
Agents:
  assistant  provider=github-copilot  model=gpt-4.1  enabled=true
  coder      provider=openai           model=gpt-4    enabled=true
  reviewer   provider=anthropic        model=claude-3-sonnet  enabled=false
```

**Verbose output (shows config file path):**

```powershell
botnexus agent list --verbose
```

```
Agents:
  assistant  provider=github-copilot  model=gpt-4.1  enabled=true
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
| `--provider` | `github-copilot` | Agent provider name (e.g., `github-copilot`, `openai`, `anthropic`). |
| `--model` | `gpt-4.1` | Model name for this agent (e.g., `gpt-4o`, `claude-3-sonnet`). |
| `--enabled` | `true` | Whether the agent is enabled (`true` or `false`). |
| `--verbose` | — | Show the updated configuration. |

### Examples

**Add an agent with defaults:**

```powershell
botnexus agent add coder
```

Output:

```
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

```
Removed agent 'experimental'.
```

**Warning if removing the default agent:**

```powershell
botnexus agent remove assistant
```

Output (warning):

```
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

```
http://localhost:5005
```

**Get an agent's model:**

```powershell
botnexus config get agents.assistant.model
```

Output:

```
gpt-4.1
```

**Get a nested value:**

```powershell
botnexus config get gateway.defaultAgentId
```

Output:

```
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

```
Set gateway.defaultAgentId = coder
```

**Change the gateway listen URL:**

```powershell
botnexus config set gateway.listenUrl http://localhost:8080
```

Output:

```
Set gateway.listenUrl = http://localhost:8080
```

**Enable an agent:**

```powershell
botnexus config set agents.coder.enabled true
```

Output:

```
Set agents.coder.enabled = true
```

**Disable an agent:**

```powershell
botnexus config set agents.reviewer.enabled false
```

Output:

```
Set agents.reviewer.enabled = false
```

---

## Examples

### Quick Setup Flow

**1. Initialize:**

```powershell
botnexus init
```

**2. List default agents:**

```powershell
botnexus agent list
```

**3. Add a second agent:**

```powershell
botnexus agent add coder --provider openai --model gpt-4o
```

**4. Validate configuration:**

```powershell
botnexus validate
```

**5. Start the gateway:**

```powershell
.\scripts\start-gateway.ps1
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
- [Development Loop](dev-loop.md) — Dev workflow and scripts
