# BotNexus training guide

Learn how the BotNexus agent system works — from LLM providers to the agent loop — so you can build your own coding-agent implementations.

## What this training covers

BotNexus is a modular AI agent execution platform built in C#/.NET. This guide walks through every layer of the architecture:

| Doc | Topic | You'll learn |
|-----|-------|-------------|
| [00 — Architecture overview](00-overview.md) | System architecture | How the layers connect, project structure, dependency flow |
| [01 — Provider system](01-providers.md) | LLM providers | Streaming protocol, message types, provider registry |
| [02 — Agent core](02-agent-core.md) | The agent loop | State management, tool execution, hooks, events |
| [03 — Coding agent](03-coding-agent.md) | Building a coding agent | Factory, tools, sessions, extensions, safety |
| [04 — Building your own](04-building-your-own.md) | Tutorial | Step-by-step: custom agent, tool, provider, and extension |
| [06 — Context file discovery](06-context-file-discovery.md) | Project documentation | How the agent auto-discovers and injects README, copilot-instructions, and docs |
| [07 — Thinking levels](07-thinking-levels.md) | Extended reasoning | How thinking levels work from CLI to provider implementation |
| [08 — Building custom coding agent](08-building-custom-coding-agent.md) | Hands-on guide | Create a coding agent: AgentOptions, tools, system prompt, full example |
| [09 — Tool development](09-tool-development.md) | Tool reference | Design and implement custom tools with full examples |
| [05 — Glossary](05-glossary.md) | Reference | All key terms and concepts in the codebase |

## Prerequisites

- **C# / .NET 10** — You should be comfortable with records, async/await, `IAsyncEnumerable`, and `System.Threading.Channels`.
- **LLM API concepts** — Familiarity with chat completions, streaming (SSE), tool calling, and system prompts.
- **Git** — The codebase uses Git for version control and session management.

## Recommended reading order

**If you're building a custom agent:**
1. [Architecture overview](00-overview.md) — Get the big picture
2. [Agent core](02-agent-core.md) — Understand the loop
3. [Building your own](04-building-your-own.md) — Hands-on tutorial

**If you're adding a new LLM provider:**
1. [Architecture overview](00-overview.md) — Get the big picture
2. [Provider system](01-providers.md) — Understand the streaming protocol
3. [Building your own](04-building-your-own.md) — Provider tutorial in step 10

**If you're building extensions or tools:**
1. [Architecture overview](00-overview.md) — Get the big picture
2. [Agent core](02-agent-core.md) — Understand tool execution and hooks
3. [Coding agent](03-coding-agent.md) — See how built-in tools work
4. [Building your own](04-building-your-own.md) — Tool and extension tutorials

**Full deep dive:** Read 00 through 05 in order.

**Quick reference:** Jump straight to the [Glossary](05-glossary.md) for any term.

## Project repository structure

```
src/
├── providers/
│   ├── BotNexus.Providers.Core/       # Models, streaming, registry
│   ├── BotNexus.Providers.Anthropic/  # Anthropic Claude provider
│   ├── BotNexus.Providers.OpenAI/     # OpenAI provider
│   ├── BotNexus.Providers.Copilot/    # GitHub Copilot provider (static utility)
│   └── BotNexus.Providers.OpenAICompat/ # OpenAI-compatible endpoints
├── agent/
│   └── BotNexus.AgentCore/            # Agent loop, tools, hooks, state
└── coding-agent/
    └── BotNexus.CodingAgent/          # Coding agent factory, tools, sessions
```
