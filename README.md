# BotNexus

```text

░░▒▒▓▓█████████████████████████████████████████████████████████████████████▓▓▒▒░░         
░▒▓                                                                           ▓▒░
░▒▓   ██████╗  ██████╗ ████████╗███╗   ██╗███████╗██╗  ██╗██╗   ██╗███████╗   ▓▒░ 
░▒▓   ██╔══██╗██╔═══██╗╚══██╔══╝████╗  ██║██╔════╝╚██╗██╔╝██║   ██║██╔════╝   ▓▒░ 
░▒▓   ██████╔╝██║   ██║   ██║   ██╔██╗ ██║█████╗   ╚███╔╝ ██║   ██║███████╗   ▓▒░ 
░▒▓   ██╔══██╗██║   ██║   ██║   ██║╚██╗██║██╔══╝   ██╔██╗ ██║   ██║╚════██║   ▓▒░ 
░▒▓   ██████╔╝╚██████╔╝   ██║   ██║ ╚████║███████╗██╔╝ ██╗╚██████╔╝███████║   ▓▒░ 
░▒▓   ╚═════╝  ╚═════╝    ╚═╝   ╚═╝  ╚═══╝╚══════╝╚═╝  ╚═╝ ╚═════╝ ╚══════╝   ▓▒░ 
░▒▓                                                                           ▓▒░ 
░▒▓  BotNexus :: LLM ORCHESTRATION LAB :: BAD IDEA DETECTOR :: TOOL WRANGLER  ▓▒░ 
░▒▓          "Mostly harmless" until someone enables shell access.            ▓▒░ 
░▒▓                                                                           ▓▒░ 
░▒▓                ▄▄                 +-- SHELL ACCESS --+                    ▓▒░   
░▒▓          ╭────────────╮           |        ON        |                    ▓▒░   
░▒▓       ╭──┤   ▣    ▣  ├──╮        +------------------+                    ▓▒░  
░▒▓       │  │            │  │     questionable choices enabled               ▓▒░   
░▒▓       ╰──┤   ╰────╯   ├──╯     error budget: lightly smoking              ▓▒░   
░▒▓          ╰────────────╯        no body, just terminal confidence          ▓▒░   
░▒▓                                tiny chaos, pocket-sized                   ▓▒░   
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
                  +-------------v-------------+
                  |         BotNexus           |
                  |  gateway + routing + logs  |
                  +------+------+------+------+
                         |      |      |
                  +------v+ +---v---+ +v------+
                  | Agent | |Agent | | Agent  |
                  +---+---+ +---+--+ +---+---+
                      |         |        |
                  +---v---------v--------v---+
                  | providers, tools, memory  |
                  +--------------------------+
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

## Install And Run From Source

BotNexus is currently easiest to run from source. You will need:

| Requirement | Notes |
|---|---|
| .NET SDK 10.0+ | Required to build and run the gateway. |
| PowerShell 7+ | Used by the repo scripts on Windows and Linux. |
| Git | For cloning the repo and worktree shenanigans. |
| GitHub Copilot subscription | Required only if you use the default Copilot provider. |
| Node.js + npm | Needed for the documentation site. |

Clone the repo:

```powershell
git clone https://github.com/sytone/botnexus.git
cd botnexus
```

Install local repo dependencies:

```powershell
pwsh ./scripts/repo/init.ps1
```

Build the solution:

```powershell
pwsh ./scripts/repo/build.ps1
```

Start the gateway and WebUI:

```powershell
pwsh ./scripts/dev-loop.ps1
```

Open:

```text
http://localhost:5005
```

If the health endpoint answers, the machine is at least awake:

```powershell
curl http://localhost:5005/health
```

## First Configuration

On first run, BotNexus creates a home folder at `~/.botnexus/`. That folder is
where configuration, tokens, agent workspaces, session history, and logs live.

For provider setup, use the CLI directly from source:

```powershell
dotnet run --project src/gateway/BotNexus.Cli -- provider setup
```

The Copilot provider uses GitHub device login. The first time an agent needs a
Copilot token, BotNexus will show a code and ask you to authorize it in GitHub.
This is the part where the computer says, in a calm voice, that it cannot open
the pod bay doors until OAuth is complete.

You can also inspect and validate configuration with:

```powershell
dotnet run --project src/gateway/BotNexus.Cli -- validate
dotnet run --project src/gateway/BotNexus.Cli -- doctor
dotnet run --project src/gateway/BotNexus.Cli -- agent list
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

## A Small Warning Label

```text
 +-------------------------------------------------------+
 | EXPERIMENTAL: may contain agents, opinions, and       |
 | automation ideas that seemed better before testing.   |
 +-------------------------------------------------------+
```

Use BotNexus to explore. Use judgment before wiring it to anything expensive,
sharp, regulated, or capable of sending messages to your boss at 3 AM.
