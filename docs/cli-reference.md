# BotNexus CLI Reference

The `botnexus` command-line tool provides quick access to configuration and agent management without editing `config.json` manually.

## Setting up the `botnexus` alias

There is no globally installed `botnexus` binary yet. To use the CLI, create a shell alias that runs `dotnet run` against the CLI project and forwards your arguments.

### PowerShell (Windows / cross-platform)

Add this to your PowerShell profile (`$PROFILE`):

```powershell
function botnexus { dotnet run --project D:\repos\botnexus\src\gateway\BotNexus.Cli -- @args }
```

> Replace `D:\repos\botnexus` with the path where you cloned the repository.

Reload the profile:

```powershell
. $PROFILE
```

### Bash / Zsh (macOS / Linux)

Add this to `~/.bashrc`, `~/.zshrc`, or equivalent:

```bash
alias botnexus='dotnet run --project ~/repos/botnexus/src/gateway/BotNexus.Cli --'
```

Reload:

```bash
source ~/.bashrc   # or source ~/.zshrc
```

### Verify

```powershell
botnexus --help
```

You should see the root command help listing all available subcommands.

> **Tip:** The `--` separator is required so that arguments like `--verbose` are passed to the CLI app and not interpreted by `dotnet run`.

---

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
13. [gateway](#gateway) — Manage the gateway lifecycle
14. [provider](#provider) — Show or set up providers
15. [provider setup](#provider-setup) — Interactive provider setup wizard
16. [provider list](#provider-list) — List configured providers
17. [provider add](#provider-add) — Add or update a provider non-interactively (scripts and CI)
18. [provider remove](#provider-remove) — Remove a provider non-interactively
19. [provider ollama](#provider-ollama) — Ollama local model diagnostics
20. [prompt](#prompt) — Manage prompt templates
21. [prompt list](#prompt-list) — List available prompt templates
22. [prompt render](#prompt-render) — Render a prompt template
23. [prompt run](#prompt-run) — Render and execute a prompt template
24. [satellite](#satellite) — Manage satellite nodes
25. [doctor](#doctor) — Diagnose configuration issues
26. [locations](#locations) — Show resolved file paths
27. [update](#update) — Check for and apply updates
28. [memory](#memory) — Manage agent memory
29. [cron](#cron-command) — Manage cron jobs from the CLI
30. [debug sessions](#debug-sessions) — Inspect session SQLite database
31. [debug logs](#debug-logs) — Inspect log files
32. [debug db](#debug-db) — Inspect raw databases
33. [debug gateway](#debug-gateway) — Live gateway diagnostics
34. [debug cron](#debug-cron) — Cron scheduler diagnostics
35. [Examples](#examples)

---

## Global Options

All commands support these options:

### `--target <DIR>`

Override the BotNexus home directory (where config, workspaces, and extensions live). Defaults to `~/.botnexus` or the `BOTNEXUS_HOME` environment variable.

This enables managing multiple BotNexus instances from a single CLI installation.

```powershell
# Use a custom home directory
botnexus --target D:\my-botnexus agent list

# Validate config for a different instance
botnexus --target /opt/botnexus-prod validate
```

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

Build the BotNexus source projects in Release configuration. Test projects are skipped to keep the build fast. Always produces a Release build so that output assemblies don't conflict with Debug builds used during local development and testing.

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

Start a BotNexus service. Defaults to the gateway if no subcommand is specified. The serve command builds the source projects (Release, skipping tests), deploys extensions to `~/.botnexus/extensions/`, checks port availability, and starts the process.

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
| `--provider` | `github-copilot` | Agent provider name (must match a configured provider; e.g. `github-copilot`, `openai`, `anthropic`, or any provider added via `botnexus provider add`). |
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

## agent show

Show the resolved configuration for a single agent.

### Usage

```powershell
botnexus agent show <ID> [OPTIONS]
```

### Arguments

| Argument | Description |
|---|---|
| `<ID>` | Agent ID to inspect. |

### Options

| Option | Description |
|---|---|
| `--json` | Emit raw JSON instead of a formatted table. Useful for scripts and CI. |
| `--target <DIR>` | BotNexus home directory. Defaults to `~/.botnexus`. |
| `--verbose` | Print the source config path under the table. |

### Examples

**Inspect an agent in table form:**

```powershell
botnexus agent show assistant
```

**Pipe agent config to jq:**

```powershell
botnexus agent show assistant --json | jq .model
```

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Agent found and printed. |
| `1` | Config missing/invalid or agent ID not found. |

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

## gateway

Manage the BotNexus Gateway lifecycle: start, stop, status, restart. For foreground/development mode, use `serve` or `serve gateway` instead.

### Usage

```powershell
botnexus gateway <COMMAND> [OPTIONS]
```

### Subcommands

| Command | Description |
|---------|-------------|
| `start` | Start the gateway process (detached by default) |
| `stop` | Stop the gateway process |
| `status` | Check gateway process status |
| `restart` | Restart the gateway process |
| `install` | Install the gateway as an OS service |
| `uninstall` | Remove the OS service registration |

### gateway start

Start the gateway process in detached (background) mode. Builds the solution first unless `--skip-build` is passed.

| Option | Default | Description |
|--------|---------|-------------|
| `--port <PORT>` | `5005` | Port to listen on |
| `--source <DIR>` | `~/botnexus` | Path to the BotNexus repository root |
| `--attached` | off | Run in foreground instead of detached mode |
| `--skip-build` | off | Skip the implicit solution rebuild before starting |

```powershell
# Start detached on default port
botnexus gateway start

# Start on custom port, skip build
botnexus gateway start --port 8080 --skip-build

# Start in foreground (like serve)
botnexus gateway start --attached
```

### gateway stop

Stop the running gateway process.

```powershell
botnexus gateway stop
```

### gateway status

Check whether the gateway process is running.

```powershell
botnexus gateway status
```

### gateway restart

Stop and restart the gateway process.

| Option | Default | Description |
|--------|---------|-------------|
| `--port <PORT>` | `5005` | Port to listen on |
| `--source <DIR>` | `~/botnexus` | Path to the BotNexus repository root |

```powershell
botnexus gateway restart
botnexus gateway restart --port 8080
```

### gateway install

Install the gateway as an OS-managed service for automatic startup. Supports:

- **Windows** — Windows Service (via `sc.exe`)
- **Linux** — systemd unit file
- **macOS** — launchd plist

| Option | Default | Description |
|--------|---------|-------------|
| `--port <PORT>` | `5005` | Port for the service to listen on |
| `--source <DIR>` | `~/botnexus` | Path to the BotNexus repository root |

```powershell
# Install as Windows Service
botnexus gateway install

# Install with custom port
botnexus gateway install --port 8080
```

After installation, manage the service with standard OS tools (`sc`, `systemctl`, `launchctl`).

### gateway uninstall

Remove the OS service registration.

```powershell
botnexus gateway uninstall
```

---

## provider

Show provider status or start the setup wizard. When run without a subcommand, shows configured providers if any exist, otherwise launches the setup wizard.

### Usage

```powershell
botnexus provider [OPTIONS]
```

### Options

| Option | Description |
|---|---|
| `--verbose` | Show full provider configuration JSON. |

### Examples

**Check provider status:**

```powershell
botnexus provider
```

If no providers are configured, this automatically starts the setup wizard.

---

## provider setup

Interactive wizard that walks you through adding and authenticating a new LLM provider.

The wizard:

1. Asks which provider to configure (GitHub Copilot, OpenAI, or Anthropic)
2. Authenticates — OAuth device code flow for Copilot, API key prompt for others
3. Presents available models and lets you pick a default
4. Saves the provider to `config.json` (and OAuth tokens to `auth.json`)

### Usage

```powershell
botnexus provider setup [OPTIONS]
```

### Options

| Option | Description |
|---|---|
| `--target <DIR>` | BotNexus home directory (config, workspace, extensions). Defaults to `~/.botnexus`. |
| `--provider <NAME>` | Pre-select the provider (`github-copilot`, `openai`, or `anthropic`) and skip the interactive provider-selection prompt. Useful for scripting and integration tests where the rest of the flow (API-key prompt, OAuth device-code flow) is still exercised but the first prompt is suppressed. |
| `--verbose` | Show the saved provider configuration in JSON. |

### Examples

**Set up GitHub Copilot (OAuth):**

```powershell
botnexus provider setup
```

Example session:

```text
? Which provider do you want to configure?
> GitHub Copilot (OAuth — free with GitHub account)
  OpenAI (API key required)
  Anthropic (API key required)

Configuring github-copilot...

──────────── GitHub Authorization Required ────────────
  1. Open: https://github.com/login/device
  2. Enter code: ABCD-1234
────────────────────────────────────────────────────────

✓ OAuth credentials saved to auth.json

? Select a default model:
> gpt-4.1 — GPT-4.1
  claude-sonnet-4.5 — Claude Sonnet 4.5
  gpt-5.4 — GPT-5.4
  ...

Default model: gpt-4.1

✓ Provider github-copilot configured successfully.
  Config saved to: C:\Users\<YourName>\.botnexus\config.json
```

**Set up OpenAI (API key):**

```powershell
botnexus provider setup
```

Select "OpenAI" and enter your API key when prompted. The key is stored directly in `config.json`.

---

## provider list

List all configured providers in a table.

### Usage

```powershell
botnexus provider list [OPTIONS]
```

### Options

| Option | Description |
|---|---|
| `--verbose` | Show additional detail. |

### Examples

**List providers:**

```powershell
botnexus provider list
```

Example output:

```text
┌─────────────────┬─────────┬───────┬───────────────┬─────────┐
│ Provider        │ Enabled │ Auth  │ Default Model │ Base URL│
├─────────────────┼─────────┼───────┼───────────────┼─────────┤
│ github-copilot  │ Yes     │ OAuth │ gpt-4.1       │ default │
│ openai          │ Yes     │ sk-…  │ gpt-4o        │ default │
└─────────────────┴─────────┴───────┴───────────────┴─────────┘
```

---

## provider add

Add or update a provider entry in `config.json` non-interactively. Designed for scripts, CI, and integration tests that need to configure providers without the interactive wizard.

When a provider with the given `--name` already exists, only the flags you pass are updated; unspecified fields preserve their previous values. To clear a previously-set value, pass an empty string explicitly.

### Usage

```powershell
botnexus provider add --name <NAME> [OPTIONS]
```

### Options

| Option | Description |
|---|---|
| `--name <NAME>` | **Required.** Provider name (e.g. `openai`, `integration-mock`). |
| `--api <API>` | API contract this provider handles. One of `openai-completions` (default), `openai-responses`, `anthropic-messages`, `integration-mock`. |
| `--api-key <KEY>` | API key value, or `auth:<name>` to reference an OAuth entry in `auth.json`. |
| `--base-url <URL>` | Base URL for OpenAI-compatible endpoints, or catalog file path for `integration-mock`. |
| `--default-model <ID>` | Default model id for this provider. |
| `--model <ID>` | Allowed model id. Repeatable. Omit to allow all models registered for this provider. |
| `--disabled` | Add the provider in disabled state. Disabled providers are hidden from the API. |
| `--target <PATH>` | BotNexus home directory. Defaults to `~/.botnexus`. |
| `--verbose` | Print the serialized provider entry after save. |

### Examples

**Add an OpenAI provider:**

```powershell
botnexus provider add --name openai --api-key sk-... --default-model gpt-4o
```

**Add the integration-mock provider for tests (uses built-in `HELLO_WORLD` catalog):**

```powershell
botnexus provider add --name integration-mock --api integration-mock --default-model integration-mock-echo
```

**Add an OpenAI-compatible local endpoint with a restricted model list:**

```powershell
botnexus provider add --name local-vllm `
    --api openai-completions `
    --base-url http://localhost:8000/v1 `
    --api-key not-needed `
    --model llama-3-8b --model llama-3-70b `
    --default-model llama-3-8b
```

---

## provider remove

Remove a provider entry from `config.json` non-interactively. Returns exit code 0 even if the named provider does not exist (idempotent).

### Usage

```powershell
botnexus provider remove --name <NAME> [OPTIONS]
```

### Options

| Option | Description |
|---|---|
| `--name <NAME>` | **Required.** Provider name to remove. |
| `--target <PATH>` | BotNexus home directory. Defaults to `~/.botnexus`. |
| `--verbose` | Print remaining provider count after removal. |

### Examples

```powershell
botnexus provider remove --name integration-mock
```

---

## provider ollama

Diagnostic subcommands for local Ollama instances. Verifies connectivity, lists pulled models, and tests inference without requiring a running gateway.

### Usage

```powershell
botnexus provider ollama <COMMAND> [OPTIONS]
```

### Subcommands

| Command | Description |
|---------|-------------|
| `status` | Check Ollama server connectivity and version |
| `models` | List models available on the local instance |
| `test` | Send a test prompt to verify model inference |

### provider ollama status

```powershell
botnexus provider ollama status
botnexus provider ollama status --url http://192.168.1.100:11434
```

| Option | Default | Description |
|--------|---------|-------------|
| `--url <URL>` | `http://localhost:11434` | Ollama server URL |

### provider ollama models

```powershell
botnexus provider ollama models
```

| Option | Default | Description |
|--------|---------|-------------|
| `--url <URL>` | `http://localhost:11434` | Ollama server URL |

### provider ollama test

Send a simple chat completion request to verify end-to-end inference.

```powershell
botnexus provider ollama test --model llama3
```

| Option | Default | Description |
|--------|---------|-------------|
| `--url <URL>` | `http://localhost:11434` | Ollama server URL |
| `--model <ID>` | (required) | Model to test |

See [Ollama Provider](providers/ollama.md) for full setup and configuration details.

---

## prompt

Manage prompt templates — define reusable, parameterized prompts in configuration and execute them through the CLI or cron scheduler.

**Getting Started:** Run `botnexus prompt create samples` to copy bundled sample templates into `~/.botnexus/prompts/`, then customize them for your workflows.

**Format Guide:**

- **`.prompt.md`** (recommended for multi-line prompts) — YAML front matter + Markdown body for readable, maintainable templates
- **`.prompt.json`** (supported for compatibility and machine-generated) — Single-file JSON format for simple prompts

### Usage

```powershell
botnexus prompt [COMMAND] [OPTIONS]
```

### Subcommands

- `list` — List available prompt templates
- `render` — Render a template to stdout (substitute parameters)
- `run` — Render and execute a template against the gateway
- `create samples` — Copy bundled sample templates into `~/.botnexus/prompts/`

---

## prompt list

List all available prompt templates for an agent.

Displays templates from two sources:
1. **Configuration-based templates** — Defined in `config.json` under `promptTemplates`
2. **File-based templates** — Stored in `~/.botnexus/prompts/` directory as `.prompt.md` or `.prompt.json` files

### Usage

```powershell
botnexus prompt list [OPTIONS]
```

### Options

| Option | Description |
|---|---|
| `--agent <ID>` | Target agent ID. Falls back to `gateway.defaultAgentId` if not specified. |
| `--config <PATH>` | Explicit path to `config.json`. Defaults to `~/.botnexus/config.json`. |
| `--target <DIR>` | BotNexus home directory (config, workspace, extensions). Defaults to `~/.botnexus/`. |
| `--verbose` | Show full paths and template metadata. |

### Examples

**List templates for the default agent:**

```powershell
botnexus prompt list
```

Output:

```text
daily-standup
weekly-status
code-review-summary
customer-feedback-analysis
```

**List templates for a specific agent:**

```powershell
botnexus prompt list --agent analyst
```

**Verbose output with descriptions:**

```powershell
botnexus prompt list --verbose
```

---

## prompt render

Render a template to stdout, substituting parameters with caller-provided values or defaults.

Use this to preview what a template will produce before executing it through the gateway.

### Usage

```powershell
botnexus prompt render <TEMPLATE> [OPTIONS]
```

### Arguments

| Argument | Description |
|---|---|
| `<TEMPLATE>` | Template name to render. |

### Options

| Option | Description |
|---|---|
| `--param <KEY=VALUE>` | Template parameter as `key=value`. Repeat for multiple values. |
| `--agent <ID>` | Target agent ID. Falls back to `gateway.defaultAgentId` if not specified. |
| `--config <PATH>` | Explicit path to `config.json`. Defaults to `~/.botnexus/config.json`. |
| `--target <DIR>` | BotNexus home directory (config, workspace, extensions). Defaults to `~/.botnexus/`. |
| `--verbose` | Show rendering metadata (agent, parameters used). |

### Examples

**Render a template with default parameters:**

```powershell
botnexus prompt render daily-standup
```

Output:

```text
Provide a brief status update for the engineering team.
Project: BotNexus
Owner: Development Team
Format: Markdown
```

**Render with custom parameter values:**

```powershell
botnexus prompt render weekly-status --param project=Infrastructure --param owner="Leela"
```

Output:

```text
Provide a weekly status update for the Infrastructure team.
Project: Infrastructure
Owner: Leela
```

**Multiple parameters:**

```powershell
botnexus prompt render code-review-summary `
  --param repo=botnexus `
  --param prNumber=242 `
  --param reviewer=Hermes
```

**Capture rendered template to a file:**

```powershell
botnexus prompt render daily-standup > prompt.txt
```

---

## prompt run

Render a template and send the result to the gateway for agent execution.

Combines template rendering with agent invocation in a single command. Useful for triggering agent workflows from scripts or cron jobs.

### Usage

```powershell
botnexus prompt run <TEMPLATE> [OPTIONS]
```

### Arguments

| Argument | Description |
|---|---|
| `<TEMPLATE>` | Template name to render and execute. |

### Options

| Option | Description |
|---|---|
| `--param <KEY=VALUE>` | Template parameter as `key=value`. Repeat for multiple values. |
| `--agent <ID>` | Target agent ID. Falls back to `gateway.defaultAgentId` if not specified. |
| `--session <ID>` | Optional session ID for conversation continuity. If omitted, a new session is created. |
| `--config <PATH>` | Explicit path to `config.json`. Defaults to `~/.botnexus/config.json`. |
| `--target <DIR>` | BotNexus home directory (config, workspace, extensions). Defaults to `~/.botnexus/`. |
| `--gateway-url <URL>` | Override gateway URL. Defaults to `gateway.listenUrl` from config (or `http://localhost:5005`). |
| `--verbose` | Show rendering and execution details. |

### Examples

**Execute a template with default parameters:**

```powershell
botnexus prompt run daily-standup
```

Output:

```text
[Agent response...]
Engineering team is on track with all Q1 deliverables. 
Three items in progress, two completed this week.
```

**Execute with custom parameters:**

```powershell
botnexus prompt run weekly-status --param project=Gateway --param owner=Bender
```

**Execute within an existing session (conversation continuity):**

```powershell
botnexus prompt run daily-standup --session my-session-123
```

**Execute against a non-default gateway:**

```powershell
botnexus prompt run daily-standup --gateway-url http://production.example.com:5005
```

**Verbose execution:**

```powershell
botnexus prompt run code-review-summary --param repo=botnexus --verbose
```

Output includes:

```text
[dim]Rendering template 'code-review-summary' with agent 'assistant'...[/]
[dim]Rendered template and invoked http://localhost:5005/api/chat[/]
[Agent response...]
```

---

## satellite

Manage satellite nodes — remote presence points that extend BotNexus to additional machines (desktop notifications, canvas windows, remote command execution).

### Usage

```powershell
botnexus satellite <COMMAND> [OPTIONS]
```

### Subcommands

| Command | Description |
|---------|-------------|
| `list` | List all registered satellites |
| `register` | Register a new satellite and generate its API key |
| `remove` | Remove a satellite registration |

### satellite list

List all registered satellites with their status and capabilities.

```powershell
botnexus satellite list
```

### satellite register

Register a new satellite and generate a unique API key (prefixed `sat_`).

```powershell
botnexus satellite register <NAME> --owner <USER_ID> [OPTIONS]
```

| Argument/Option | Required | Description |
|-----------------|----------|-------------|
| `<NAME>` | Yes | Satellite ID (e.g., `sat_desktop_home`) |
| `--owner <ID>` | Yes | Owner user ID |
| `--display-name <NAME>` | No | Human-readable display name |
| `--platform <OS>` | No | Platform: `windows`, `macos`, `linux` (default: `windows`) |
| `--capabilities <LIST>` | No | Comma-separated capabilities: `notify`, `canvas`, `exec` (default: `notify,canvas`) |

```powershell
# Register a Windows desktop satellite
botnexus satellite register sat_desktop_home --owner jon --display-name "Home Desktop"

# Register with exec capability
botnexus satellite register sat_workstation --owner jon --capabilities notify,canvas,exec
```

The generated API key is displayed once after registration. Store it securely — it cannot be retrieved later.

### satellite remove

Remove a satellite registration. The satellite's API key is immediately invalidated.

```powershell
botnexus satellite remove <NAME>
```

---

## doctor

Run diagnostic checks against your BotNexus configuration, providers, and environment. Reports issues with actionable fix suggestions.

### Usage

```powershell
botnexus doctor [OPTIONS]
```

### Options

| Option | Description |
|--------|-------------|
| `--target <DIR>` | BotNexus home directory. Defaults to `~/.botnexus`. |
| `--verbose` | Show detailed check output. |

### Examples

```powershell
# Run all diagnostics
botnexus doctor

# Check a specific instance
botnexus doctor --target /opt/botnexus-prod
```

Checks include: config validity, provider reachability, directory permissions, extension loading, and port availability.

---

## locations

Show resolved paths for all BotNexus directories and files (config, logs, sessions, agents, extensions).

### Usage

```powershell
botnexus locations [OPTIONS]
```

### Options

| Option | Description |
|--------|-------------|
| `--target <DIR>` | BotNexus home directory. Defaults to `~/.botnexus`. |

### Examples

```powershell
botnexus locations
```

Example output:

```text
Home:       C:\Users\you\.botnexus
Config:     C:\Users\you\.botnexus\config.json
Logs:       C:\Users\you\.botnexus\logs
Sessions:   C:\Users\you\.botnexus\sessions
Agents:     C:\Users\you\.botnexus\agents
Extensions: C:\Users\you\.botnexus\extensions
```

---

## update

Check for available updates and optionally apply them.

### Usage

```powershell
botnexus update [COMMAND] [OPTIONS]
```

### Subcommands

| Command | Description |
|---------|-------------|
| `check` | Check if updates are available (default) |
| `apply` | Pull latest changes and rebuild |

### Options

| Option | Description |
|--------|-------------|
| `--path <DIR>` | Repository root. Defaults to install location. |
| `--verbose` | Show detailed update output. |

### Exit Codes (for `update check`)

| Code | Meaning |
|------|--------|
| `0` | Up to date |
| `1` | Updates available |
| `2` | Check failed |

### Examples

```powershell
# Check for updates
botnexus update check

# Apply updates
botnexus update apply
```

---

## memory

Manage agent memory files from the CLI.

### Usage

```powershell
botnexus memory <COMMAND> [OPTIONS]
```

### Subcommands

| Command | Description |
|---------|-------------|
| `list` | List memory entries for an agent |
| `search` | Search memory content |
| `consolidate` | Trigger memory consolidation |

### Options

| Option | Description |
|--------|-------------|
| `--agent <ID>` | Target agent ID. |
| `--target <DIR>` | BotNexus home directory. |

### Examples

```powershell
# List memory files for an agent
botnexus memory list --agent assistant

# Search memory content
botnexus memory search --agent assistant --query "deployment"
```

---

## cron (command) {#cron-command}

Manage cron jobs from the CLI.

### Usage

```powershell
botnexus cron <COMMAND> [OPTIONS]
```

### Subcommands

| Command | Description |
|---------|-------------|
| `list` | List all configured cron jobs |
| `status` | Show job status and last run time |
| `run` | Manually trigger a job |
| `enable` | Enable a disabled job |
| `disable` | Disable a job |

### Examples

```powershell
# List all cron jobs
botnexus cron list

# Check status
botnexus cron status

# Trigger a job manually
botnexus cron run morning-briefing
```

---

## debug sessions

Directly inspect the sessions SQLite database without requiring a running gateway. Useful for offline diagnostics.

### Usage

```powershell
botnexus debug sessions <COMMAND> [OPTIONS]
```

### Subcommands

| Command | Description |
|---------|-------------|
| `list` | List all sessions with summary info |
| `get` | Show details for a specific session |
| `compaction` | Show compaction history for a session |
| `stats` | Database-wide statistics |

### Options

| Option | Default | Description |
|--------|---------|-------------|
| `--target <DIR>` | `~/.botnexus` | BotNexus home directory |
| `--format` | `table` | Output format: `table` or `json` |

### Examples

```powershell
# List sessions
botnexus debug sessions list

# Get session details
botnexus debug sessions get --id "session-abc123"

# Show compaction history
botnexus debug sessions compaction --id "session-abc123"

# Database statistics
botnexus debug sessions stats

# JSON output for scripting
botnexus debug sessions list --format json
```

---

## debug logs

Directly inspect log files without requiring a running gateway. Reads the hourly Serilog structured log files.

### Usage

```powershell
botnexus debug logs <COMMAND> [OPTIONS]
```

### Subcommands

| Command | Description |
|---------|-------------|
| `tail` | Show the most recent log entries |
| `errors` | Filter to ERROR and FATAL entries |
| `search` | Search log content by text |
| `session` | Filter logs for a specific session |

### Options

| Option | Default | Description |
|--------|---------|-------------|
| `--target <DIR>` | `~/.botnexus` | BotNexus home directory |
| `--format` | `table` | Output format: `table` or `json` |
| `--lines <N>` | `50` | Number of entries to show |

### Examples

```powershell
# Tail recent logs
botnexus debug logs tail

# Show recent errors
botnexus debug logs errors

# Search for a pattern
botnexus debug logs search --query "timeout"

# Filter by session
botnexus debug logs session --id "session-abc123"
```

---

## debug db

Directly inspect raw SQLite databases in the BotNexus home directory. Useful for understanding schema and diagnosing storage issues.

### Usage

```powershell
botnexus debug db <COMMAND> [OPTIONS]
```

### Subcommands

| Command | Description |
|---------|-------------|
| `tables` | List tables in a database |
| `schema` | Show column definitions for a table |
| `size` | Show database file sizes |

### Options

| Option | Default | Description |
|--------|---------|-------------|
| `--target <DIR>` | `~/.botnexus` | BotNexus home directory |
| `--db <NAME>` | (all) | Filter to a specific database file |
| `--format` | `table` | Output format: `table` or `json` |

### Examples

```powershell
# List all databases and their sizes
botnexus debug db size

# Show tables in sessions database
botnexus debug db tables --db sessions

# Inspect table schema
botnexus debug db schema --db sessions --table SessionEntries
```

---

## debug gateway

Connect to a running BotNexus gateway and query live diagnostics via its REST API.

### Usage

```powershell
botnexus debug gateway <COMMAND> [OPTIONS]
```

### Subcommands

| Command | Description |
|---------|-------------|
| `status` | Show gateway health and uptime |
| `sessions` | List active sessions |
| `providers` | List configured providers and status |
| `config` | Dump running configuration |

### Options

| Option | Default | Description |
|--------|---------|-------------|
| `--url <URL>` | `http://localhost:5000` | Gateway base URL |
| `--format` | `table` | Output format: `table` or `json` |

### Examples

```powershell
# Check if the gateway is reachable and healthy
botnexus debug gateway status

# List active sessions in JSON
botnexus debug gateway sessions --format json

# Query a remote gateway
botnexus debug gateway providers --url http://192.168.1.100:5000
```

---

## debug cron

Inspect the cron scheduler state including job status, execution history, and missed runs.

### Usage

```powershell
botnexus debug cron <COMMAND> [OPTIONS]
```

### Subcommands

| Command | Description |
|---------|-------------|
| `status` | Show scheduler state and per-job next/last run |
| `history` | Show execution history for a job |
| `missed` | List runs detected as missed on startup |

### Options

| Option | Default | Description |
|--------|---------|-------------|
| `--target <DIR>` | `~/.botnexus` | BotNexus home directory |
| `--job <ID>` | (all) | Filter to a specific job |
| `--limit <N>` | 20 | Maximum history entries |
| `--format` | `table` | Output format: `table` or `json` |

### Examples

```powershell
# Show all job status
botnexus debug cron status

# View history for a specific job
botnexus debug cron history --job morning-briefing --limit 10

# List missed runs
botnexus debug cron missed
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

**3. Set up a provider:**

```powershell
botnexus provider setup
```

**4. List default agents:**

```powershell
botnexus agent list
```

**5. Validate configuration:**

```powershell
botnexus validate
```

**6. Start the gateway:**

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

Most commands return:

- `0` — Success
- `1` — Error (check console output for details)

`botnexus update check` uses status-style exit codes for automation:

- `0` — Up to date
- `1` — Updates available
- `2` — Check failed (for example, git fetch error)

---

## See Also

- [Configuration Guide](configuration.md) — Complete configuration reference
- [Getting Started](getting-started.md) — Onboarding guide
- [Developer Guide](getting-started-dev.md) — Dev workflow and scripts
