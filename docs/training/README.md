# BotNexus training guide

Learn how the BotNexus agent system works — from LLM providers to the agent loop — so you can build your own coding-agent implementations.

## What this training covers

BotNexus is a modular AI agent execution platform built in C#/.NET. This guide walks through every layer of the architecture:

| Doc | Topic | You'll learn |
|-----|-------|-------------|
| [01 — Provider system](01-providers.md) | LLM providers | Streaming protocol, message types, provider registry, AnthropicProvider refactor (Phase 4) |
| [02 — Agent core](02-agent-core.md) | The agent loop | State management, tool execution, hooks, events, streaming behavior (Phase 4) |
| [03 — Coding agent](03-coding-agent.md) | Building a coding agent | Factory, tools, sessions, extensions, safety, EditTool/ShellTool improvements (Phase 4) |
| [04 — Building your own](04-building-your-own.md) | Tutorial | Step-by-step: custom agent, tool, provider, and extension |
| [06 — Context file discovery](06-context-file-discovery.md) | Project documentation | How the agent auto-discovers and injects README, copilot-instructions, and docs |
| [07 — Thinking levels](07-thinking-levels.md) | Extended reasoning | How thinking levels work from CLI to provider implementation |
| [09 — Tool development](09-tool-development.md) | Tool reference | Design and implement custom tools with full examples |
| [11 — Provider development guide](11-provider-development-guide.md) | Provider tutorial | Implement IApiProvider: SSE parsing, stop reason mapping (Phase 4), tool calls |
| [05 — Glossary](05-glossary.md) | Reference | All key terms and concepts in the codebase |

See also [Architecture overview](../architecture/overview.md) in the architecture folder.

### Focused deep dives

These standalone documents cover specific topics in depth with full code examples:

| Doc | Topic | You'll learn |
|-----|-------|-------------|
| [Agent event system](agent-events.md) | Agent lifecycle | All event types, subscribe/unsubscribe, hooks, steering/follow-up queues, error handling |
| [Tool security model](tool-security.md) | Security | Path containment, blocked paths/commands, file mutation queue, shell safety, audit logging |

## Prerequisites

- **C# / .NET 10** — You should be comfortable with records, async/await, `IAsyncEnumerable`, and `System.Threading.Channels`.
- **LLM API concepts** — Familiarity with chat completions, streaming (SSE), tool calling, and system prompts.
- **Git** — The codebase uses Git for version control and session management.

## Recommended reading order

**If you're building a custom agent:**
1. [Architecture overview](../architecture/overview.md) — Get the big picture
2. [Agent core](02-agent-core.md) — Understand the loop
3. [Building your own](04-building-your-own.md) — Hands-on tutorial

**If you're adding a new LLM provider:**
1. [Architecture overview](../architecture/overview.md) — Get the big picture
2. [Provider system](01-providers.md) — Understand the streaming protocol
3. [Building your own](04-building-your-own.md) — Provider tutorial in step 10

**If you're building extensions or tools:**
1. [Architecture overview](../architecture/overview.md) — Get the big picture
2. [Agent core](02-agent-core.md) — Understand tool execution and hooks
3. [Coding agent](03-coding-agent.md) — See how built-in tools work
4. [Building your own](04-building-your-own.md) — Tool and extension tutorials

**Full deep dive:** Read 01 through 05 in order, plus the architecture docs.

**If you want to understand the internals deeply:**
1. [Provider system](01-providers.md) — streaming protocol and provider implementation
2. [Agent event system](agent-events.md) — lifecycle events, hooks, and message queues
3. [Tool security model](tool-security.md) — how tools are sandboxed
4. [Architecture overview](../architecture/overview.md) — complete technical details

**Quick reference:** Jump straight to the [Glossary](05-glossary.md) for any term.

## Project repository structure

```
src/
├── providers/
│   ├── BotNexus.Agent.Providers.Core/       # Models, streaming, registry
│   ├── BotNexus.Agent.Providers.Anthropic/  # Anthropic Claude provider
│   ├── BotNexus.Agent.Providers.OpenAI/     # OpenAI provider
│   ├── BotNexus.Agent.Providers.Copilot/    # GitHub Copilot provider (static utility)
│   └── BotNexus.Agent.Providers.OpenAICompat/ # OpenAI-compatible endpoints
├── agent/
│   └── BotNexus.Agent.Core/            # Agent loop, tools, hooks, state
└── coding-agent/
    └── BotNexus.CodingAgent/          # Coding agent factory, tools, sessions
```
