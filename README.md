# BotNexus

[![CI: Build & Test](https://github.com/sytone/botnexus/actions/workflows/ci-build-test.yml/badge.svg)](https://github.com/sytone/botnexus/actions/workflows/ci-build-test.yml)
[![Docs](https://github.com/sytone/botnexus/actions/workflows/deploy-docs.yml/badge.svg)](https://sytone.github.io/botnexus/)

📖 **Documentation site:** <https://sytone.github.io/botnexus/> · 🤝 **Contributing:** [CONTRIBUTING.md](CONTRIBUTING.md)

```text
░░▒▒▓▓█████████████████████████████████████████████████████████████████████▓▓▒▒░░
░▒▓                                                                           ▓▒░
░▒▓  ██████╗  ██████╗ ████████╗███╗   ██╗███████╗██╗  ██╗██╗   ██╗███████╗    ▓▒░
░▒▓  ██╔══██╗██╔═══██╗╚══██╔══╝████╗  ██║██╔════╝╚██╗██╔╝██║   ██║██╔════╝    ▓▒░
░▒▓  ██████╔╝██║   ██║   ██║   ██╔██╗ ██║█████╗   ╚███╔╝ ██║   ██║███████╗    ▓▒░
░▒▓  ██╔══██╗██║   ██║   ██║   ██║╚██╗██║██╔══╝   ██╔██╗ ██║   ██║╚════██║    ▓▒░
░▒▓  ██████╔╝╚██████╔╝   ██║   ██║ ╚████║███████╗██╔╝ ██╗╚██████╔╝███████║    ▓▒░
░▒▓  ╚═════╝  ╚═════╝    ╚═╝   ╚═╝  ╚═══╝╚══════╝╚═╝  ╚═╝ ╚═════╝ ╚══════╝    ▓▒░
░▒▓                                                                           ▓▒░
░▒▓  BotNexus :: LLM ORCHESTRATION LAB :: BAD IDEA DETECTOR :: TOOL WRANGLER  ▓▒░
░▒▓         "Mostly harmless" until someone enables shell access.             ▓▒░
░▒▓                                                                           ▓▒░
░▒▓                ▄▄                   +-- SHELL ACCESS --+                  ▓▒░
░▒▓          ╭────────────╮             |        ON        |                  ▓▒░
░▒▓       ╭──┤   ■    ■   ├──╮          +------------------+                  ▓▒░
░▒▓       │  │            │  │       questionable choices enabled             ▓▒░
░▒▓       ╰──┤   ╰────╯   ├──╯       error budget: lightly smoking            ▓▒░
░▒▓          ╰────────────╯        no body, just terminal confidence          ▓▒░
░▒▓                                   tiny chaos, pocket-sized                ▓▒░
░░▒▒▓▓█████████████████████████████████████████████████████████████████████▓▓▒▒░░        
```

BotNexus is an experimental platform for playing with LLMs, agents, tools,
channels, memory, prompts, and the occasional "should we really automate this?"
moment. It is built in C#/.NET and exists to explore where AI agents are useful,
where they are merely confident, and where the correct answer is still a human
with coffee and a raised eyebrow.

This is not a mission-critical, ISO-certified, slide-deck-polished enterprise
brain. It is a sandbox for trying different interaction patterns with LLMs:
chatting through a WebUI, routing messages through channels, giving agents tools,
persisting sessions, testing memory, and seeing which ideas survive contact with
reality. Think "Mostly harmless", with build warnings treated as errors.

## What It Does

```text
                      +------------------+
                      |   Humans / Apps  |
                      +---------+--------+
                                |
                  +-------------v--------------+
                  |         BotNexus           |
                  |  gateway + routing + logs  |
                  +---+---------+---------+----+
                      |         |         |
                  +---v---+ +---v---+ +---v----+
                  | Agent | | Agent | | Agent  |
                  +---+---+ +---+---+ +---+----+
                      |         |         |
                  +---v---------v---------v----+
                  | providers, tools, memory   |
                  +----------------------------+
```

BotNexus gives you a local playground for running and observing agents:

- **Multi-agent orchestration** - run multiple agents with separate providers,
  models, prompts, tools, and workspaces.
- **Provider flexibility** - use GitHub Copilot, OpenAI, Anthropic, Azure OpenAI,
  OpenAI-compatible endpoints, and model-aware routing.
- **Channels** - connect agents through the WebUI, SignalR, REST APIs, Telegram,
  Slack, Discord, Azure Service Bus, or your own adapter.
- **WebUI** - chat in the browser, watch streaming responses, switch models, and
  poke the machine while it pretends everything is fine.
- **Tools and skills** - give agents shell tools, web tools, MCP servers, skills,
  and other sharp objects with approval gates where appropriate.
- **Memory and sessions** - persist conversations, agent workspace files, and
  long-running context so every run does not begin with "new phone, who dis?".
- **Scheduling** - use cron-style triggers and heartbeats for background agent
  tasks, status checks, and periodic nonsense detection.
- **Diagnostics** - run `doctor`, inspect health checks, and watch correlation IDs
  when the robots insist they are feeling perfectly normal.
- **Extension system** - load providers, channels, and tools dynamically instead
  of welding every experiment into the gateway.

## Quick Start

Get BotNexus running from the published CLI tool in three steps (requires the
.NET 10 SDK — see [Prerequisites](#prerequisites)):

```bash
# 1. Install the BotNexus CLI (global .NET tool) and build the platform
dotnet tool install -g BotNexus.Cli
botnexus install --build

# 2. Initialize config and configure your first provider
botnexus init
botnexus provider setup

# 3. Start the gateway
botnexus gateway start
```

Open the WebUI at <http://localhost:5005>. Prefer building from source? See
[Install And Run From Source](#install-and-run-from-source) and
[CONTRIBUTING.md](CONTRIBUTING.md).

## Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| **.NET SDK** | **10.0 or later** | Required. Earlier SDK versions (including .NET 9) are **not supported**. |
| **GitHub account** | — | Required for the default `github-copilot` provider (active Copilot subscription). |

Verify your SDK version:

```bash
dotnet --version
# Must output 10.0.x or later
```

Download .NET 10 SDK: <https://dotnet.microsoft.com/download/dotnet/10.0>

> **Troubleshooting:** If `dotnet tool install` fails with
> `DotnetToolSettings.xml was not found`, your .NET SDK is too old. BotNexus
> targets `net10.0` exclusively — install the .NET 10 SDK and try again.

## Quick Install

BotNexus ships as a global .NET CLI tool. You need the .NET 10 SDK or Runtime
installed, then one command installs the `botnexus` CLI:

```bash
dotnet tool install -g BotNexus.Cli
```

Once installed, run the setup sequence:

```bash
# 1. Clone the BotNexus platform to ~/botnexus and build it
botnexus install --build

# 2. Initialize ~/.botnexus with a default config and required directories
botnexus init

# 3. Configure your first LLM provider (interactive wizard)
botnexus provider setup

# 4. Validate configuration
botnexus validate

# 5. Start the gateway
botnexus gateway start
```

Open the WebUI at `http://localhost:5005`.

## First Provider Setup

The `provider setup` wizard guides you through adding a provider interactively.
For the default GitHub Copilot provider it runs an OAuth device code flow:

```text
  1. Open: https://github.com/login/device
  2. Enter code: ABCD-1234
```

Authorize in your browser. The token is saved to `~/.botnexus/auth.json` and
refreshed automatically. You only do this once.

Other supported providers:

| Provider | Auth |
|---|---|
| `github-copilot` | OAuth (device code) |
| `openai` | API key |
| `anthropic` | API key |

To add a provider non-interactively (useful for scripts):

```bash
botnexus provider add --name openai --api-key sk-... --default-model gpt-4o
```

## First Configuration

On first run, `botnexus init` creates `~/.botnexus/` with a `config.json` and
the required directory layout:

```text
~/.botnexus/
├── config.json          # Your configuration
├── auth.json            # OAuth tokens
├── agents/              # Agent workspace directories
├── sessions.sqlite      # Conversation history
├── backups/             # Config backups
└── logs/                # Gateway logs
```

Inspect or edit configuration via the CLI:

```bash
botnexus config get
botnexus config set agents.assistant.model gpt-4.1
botnexus validate
botnexus doctor
```

## Gateway Lifecycle

```bash
botnexus gateway start      # Start in background (default port 5005)
botnexus gateway status     # Check if running and show PID
botnexus gateway stop       # Stop the gateway
botnexus gateway restart    # Stop then start
```

The gateway serves the WebUI and REST API at `http://localhost:5005`.

## Agents

Agents are named configurations with their own workspace, model, and settings.
BotNexus creates an `assistant` agent by default. Add more:

```bash
botnexus agent wizard       # Interactive wizard
botnexus agent add <id>     # Add an agent non-interactively
botnexus agent list         # List configured agents
```

Each agent gets a workspace directory at `~/.botnexus/agents/<name>/` with
markdown files that shape its personality and memory:

```text
~/.botnexus/agents/assistant/
├── SOUL.md       # Core personality and values
├── IDENTITY.md   # Role, style, and constraints
├── USER.md       # User preferences
├── MEMORY.md     # Long-term distilled knowledge
├── HEARTBEAT.md  # Periodic task instructions
└── memory/
    ├── 2026-04-01.md    # Daily memory notes (YYYY-MM-DD.md)
    └── ...
```

Edit these files directly to customize behavior. Changes take effect on the next
conversation — no restart required.

## Keeping Up to Date

**NuGet tool install:**

```bash
dotnet tool update -g BotNexus.Cli
botnexus gateway restart
```

**Source build:**

```bash
botnexus update   # git pull → build → redeploy extensions → restart gateway
```

## Diagnostics

```bash
botnexus doctor     # Health checks across config, connectivity, extensions
botnexus validate   # Validate config.json structure and provider settings
botnexus locations  # Show all resolved paths (home, config, logs, etc.)
```

## Install And Run From Source

Prefer building from source? You need:

| Requirement | Notes |
|---|---|
| .NET SDK 10.0+ | Required to build and run the gateway. |
| Git | For cloning the repo. |
| PowerShell 7+ | Optional — used by some repo helper scripts. |

Clone the repo:

```bash
git clone https://github.com/sytone/botnexus.git
cd botnexus
```

Build and run the CLI directly:

```bash
dotnet run --project src/gateway/BotNexus.Cli -- init
dotnet run --project src/gateway/BotNexus.Cli -- provider setup
dotnet run --project src/gateway/BotNexus.Cli -- gateway start
```

Or use `serve` to run the gateway in the foreground (restarts on exit):

```bash
dotnet run --project src/gateway/BotNexus.Cli -- serve
```

## Repository Map

```text
botnexus/
|-- src/          gateway, agents, providers, tools, memory, cron
|-- tests/        unit, integration, architecture, scenario, component tests
|-- docs/         published documentation site
|-- scripts/      build, test, repo, and local development scripts
|-- examples/     experiments and sample integrations
`-- tools/        supporting utilities
```

The short version: code lives in `src/`, proof that code still works lives in
`tests/`, and the explanation of why any of this seemed reasonable lives in
`docs/`.

## Documentation

Start here when you want more than a README can responsibly contain:

| Page | Use it for |
|---|---|
| [Getting Started](docs/getting-started.md) | Pick the right setup path. |
| [Developer Setup](docs/getting-started-dev.md) | Build, run, test, and develop from source. |
| [Configuration Guide](docs/configuration.md) | Configure providers, agents, channels, and overrides. |
| [CLI Reference](docs/cli-reference.md) | Use `botnexus` commands without spelunking through JSON. |
| [API Reference](docs/api-reference.md) | Call the REST and SignalR endpoints. |
| [Architecture Overview](docs/architecture/overview.md) | Understand the gateway, agents, extensions, and message flow. |
| [Extension Development](docs/extension-development.md) | Build custom providers, channels, tools, and integrations. |
| [Workspace And Memory](docs/development/workspace-and-memory.md) | Shape agent workspaces and memory behavior. |
| [Cron And Scheduling](docs/cron-and-scheduling.md) | Schedule background agent tasks. |
| [Skills Guide](docs/skills.md) | Package reusable agent knowledge. |
| [Observability](docs/observability.md) | Trace, log, and inspect the weird bits. |

## Contributing

Want to build from source, fix a bug, or add a provider/channel/tool? Start with
[CONTRIBUTING.md](CONTRIBUTING.md) — it covers prerequisites, dev setup, the
worktree/branch workflow, Conventional Commits, code style (IFileSystem, Vogen
value objects, XML docs, warnings-as-errors), running impacted tests, and the
pre-commit validation gate. Planning is tracked in
[GitHub Issues](https://github.com/sytone/botnexus/issues).

## A Small Warning Label

```text
 +-------------------------------------------------------+
 | EXPERIMENTAL: may contain agents, opinions, and       |
 | automation ideas that seemed better before testing.   |
 +-------------------------------------------------------+
```

Use BotNexus to explore. Use judgment before wiring it to anything expensive,
sharp, regulated, or capable of sending messages to your boss at 3 AM.
