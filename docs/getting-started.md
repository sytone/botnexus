# Getting Started with BotNexus

Welcome to BotNexus! This page will help you get up and running. Choose the guide that matches your path:

## Prerequisites (Both Paths)

Before you start, you'll need:

| Requirement | Details |
|---|---|
| **GitHub account** | Required for the Copilot provider's OAuth flow. You need an active GitHub Copilot subscription. |
| **.NET 10 runtime or SDK** | See the guide for your path below. |

Optional but recommended:

- **curl** — for testing API endpoints (built into modern Windows and macOS)
- A modern browser — for the built-in WebUI (SignalR client)

## Choose Your Path

### 👤 I Want to Install and Run BotNexus

You're an **end user** who wants to install a released version of BotNexus.

👉 **[Install from Release](getting-started-release.md)**

This guide covers:
- Installing the `botnexus` CLI tool from GitHub Releases (if available)
- Setting up the gateway and extensions
- Configuring your first provider (Copilot)
- Creating and running your first agent

**Requires:** .NET 10 runtime (not the full SDK)

### I want to build and develop BotNexus

You're a **developer** who cloned the repository and wants to build from source.

👉 **[Developer guide](getting-started-dev.md)**

This guide covers everything: clone, build, run, configure, dev workflow, testing, and reference.

**Requires:** .NET 10 SDK, PowerShell 7+, Git, and a GitHub Copilot subscription

---

## What is BotNexus?

BotNexus is a platform for building and running AI agents. It provides:

- **A multi-agent framework** — Run multiple independent agents with different personalities and skills
- **Multiple channels** — Connect your agents to Telegram, Discord, Slack, or your own custom interfaces
- **Pluggable providers** — Use Copilot, OpenAI, or other LLM backends
- **A web interface** — Chat with your agents from your browser
- **Persistent memory** — Agents remember conversations and learn from long-term interactions
- **Cron scheduling** — Automate tasks and regular briefings
- **Developer-friendly** — Build custom extensions and integrate with your own tools

This guide will get you from zero to your first conversation with an AI agent in about 10 minutes.

---

## Quick Overview

Here's what happens when you start BotNexus:

1. **Initialize** — BotNexus creates a home directory (`~/.botnexus/`) with configuration, agent workspaces, and logs (use `botnexus init`)
2. **Configure** — You add a provider (like Copilot) to your config
3. **Create an agent** — Add a named agent to your config with its own personality and memory
4. **Chat** — Open the WebUI and start chatting with your agent
5. **Extend** — Add channels, tools, cron jobs, or custom providers as needed

The whole system is driven by a single `config.json` file and workspace Markdown files for each agent.

### Using the CLI

You can manage configuration from the command line without editing JSON:

```powershell
# Initialize home directory
botnexus init

# List agents
botnexus agent list

# Add an agent
botnexus agent add coder --provider openai --model gpt-4o

# View a config value
botnexus config get gateway.listenUrl

# Validate configuration
botnexus validate
```

See the [CLI Reference](cli-reference.md) for all available commands.

---

## Directory Layout

| Path | Purpose | Used by |
|---|---|---|
| `%LOCALAPPDATA%\BotNexus` (Windows) or `/usr/local/bin` (macOS/Linux) | Installed binaries (CLI, gateway, extensions) | Release guide |
| `~/.botnexus/` | User data (config, agents, tokens, logs) | Both paths |
| `~/.botnexus-backups/` | Backup archives | Both paths |

---

## Next Steps

Once you finish your setup guide, check out:

- **[Workspace & Memory](development/workspace-and-memory.md)** — Customize your agents with personality files
- **[Configuration Guide](configuration.md)** — Reference for every config option
- **[Extension Development](extension-development.md)** — Build custom channels and tools
- **[Architecture Overview](architecture/overview.md)** — Understand how the system works

---

*If something isn't working, see the troubleshooting sections in your setup guide or check the [Configuration Guide](configuration.md) for reference.*
