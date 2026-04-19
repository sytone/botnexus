# BotNexus Architecture Overview

**Last Updated:** 2026-04  
**Status:** Canonical high-level reference

---

## System Vision

BotNexus is a **domain-driven, multi-agent execution platform** for building AI assistants. A Gateway runs within a **World** — a runtime context that defines which agents, resources, and capabilities are available. Agents orchestrate LLM interactions, execute tools, and manage sessions across multiple channels.

### Design Principles

- **Domain-Driven Design**: Core domain primitives are framework-agnostic
- **World as Runtime Context**: The Gateway operates within a World that defines resources (Locations), agents, and boundaries
- **Library-Backed Platform**: AgentCore and Providers are reusable libraries; the Gateway composes them into a platform
- **Channel-Centric Routing**: Messages route through channels, not direct agent calls
- **Session Isolation**: Each (agent, channel) pair gets its own persistent conversation
- **Location-Based Resources**: All resource access is through named Locations, enabling validation and portability
- **Extension-First**: Tools, channels, hooks, and prompts are pluggable
- **Stream-First**: All LLM interactions stream events to clients

---

## Architecture

```text
┌─────────────────────────────────────────────────────────────────┐
│                         World                                    │
│  The runtime context — defines resources, agents, boundaries     │
│                                                                  │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │                    Gateway                                 │  │
│  │  Runs WITHIN the World. Manages agents, sessions, routing  │  │
│  │                                                            │  │
│  │  ┌─────────────┐  ┌──────────────┐  ┌──────────────────┐ │  │
│  │  │   Agents     │  │   Sessions   │  │   Channels       │ │  │
│  │  │  (per World) │  │  (per agent  │  │  SignalR, TG,    │ │  │
│  │  │             │  │   + channel) │  │  TUI, Cron       │ │  │
│  │  └──────┬──────┘  └──────────────┘  └──────────────────┘ │  │
│  │         │                                                  │  │
│  │         │ uses                                             │  │
│  │  ┌──────▼──────────────────────────────────────────────┐  │  │
│  │  │          Infrastructure Libraries                    │  │  │
│  │  │                                                      │  │  │
│  │  │  ┌──────────────┐    ┌───────────────────────────┐  │  │  │
│  │  │  │  AgentCore   │    │  Providers                │  │  │  │
│  │  │  │  Loop runner │    │  Anthropic, OpenAI,       │  │  │  │
│  │  │  │  Tool exec   │    │  Copilot, OpenAI-Compat   │  │  │  │
│  │  │  │  Streaming   │    │  LLM client abstraction   │  │  │  │
│  │  │  └──────────────┘    └───────────────────────────┘  │  │  │
│  │  └─────────────────────────────────────────────────────┘  │  │
│  └────────────────────────────────────────────────────────────┘  │
│                                                                  │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │                    Locations                                │  │
│  │  Named resources the World makes available to agents        │  │
│  │  📁 filesystem  🌐 api  🔌 mcp-server  🗄️ database        │  │
│  └────────────────────────────────────────────────────────────┘  │
│                                                                  │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │                    Extensions                               │  │
│  │  Skills, MCP, Tools, Web — loaded at startup                │  │
│  └────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘

Shared vocabulary (referenced by everything above):
┌─────────────────────────────────────────────────────────────────┐
│                    BotNexus.Domain                                │
│  Primitives, Value Objects, Smart Enums, Domain Models            │
│  AgentId, SessionId, ChannelKey, Location, WorldDescriptor        │
│  Zero dependencies — pure domain types                            │
└─────────────────────────────────────────────────────────────────┘
```

### Key Relationships

- **AgentCore** and **Providers** are **independent libraries** — they have no knowledge of BotNexus, its Domain, or its platform concepts. They are general-purpose agent execution and LLM client libraries that BotNexus happens to use.
- **Domain** is BotNexus's shared vocabulary — referenced by BotNexus platform projects, but NOT by AgentCore or Providers
- **Gateway** is the composition root — it brings AgentCore, Providers, and Domain together into the BotNexus platform
- **Gateway** runs **within a World** — the World defines what agents exist, what Locations are available, and what boundaries apply
- **Locations** are the World's resource registry — agents reference Locations by name (e.g., `@repo-botnexus`) rather than hardcoding paths/endpoints
- **Channels** connect the World to users — SignalR (WebUI), Telegram, TUI, cron triggers
- **Extensions** are dynamically loaded capabilities — tools, MCP servers, skills

---

## Project Structure

### Solution Map

| Project | Role | What It Is |
|---------|------|------------|
| | **Independent Libraries (no BotNexus dependency)** | |
| **BotNexus.Agent.Core** | Agent execution library | Agent loop runner, tool execution engine, hooks. Generic — usable outside BotNexus. |
| **BotNexus.Agent.Providers.Core** | LLM client library | LLM client abstraction, streaming, model registry. Generic — usable outside BotNexus. |
| **BotNexus.Providers.{Anthropic,OpenAI,Copilot}** | LLM provider implementations | Provider-specific LLM implementations. Depend only on Providers.Core. |
| **BotNexus.Tools** | File tool library | read, write, edit, grep, glob, ls. Generic — usable outside BotNexus. |
| | **BotNexus Platform** | |
| **BotNexus.Domain** | Platform vocabulary | Primitives, value objects, smart enums, domain models. Zero dependencies. |
| **BotNexus.Prompts** | Prompt pipeline | Pluggable prompt sections. Depends on Domain. |
| **BotNexus.Sessions.Common** | Session primitives | Shared JSONL/metadata formats. Depends on Domain. |
| **BotNexus.Gateway.Contracts** | Platform contracts | Gateway interfaces (ISessionStore, IChannelAdapter, etc.). Depends on Domain. |
| **BotNexus.Gateway** | Platform core | Supervisor, router, session stores, isolation, config. Composes AgentCore + Providers + Domain. |
| **BotNexus.Gateway.Api** | Platform host | REST API, SignalR hub, triggers, middleware. Hosts the Gateway. |
| **BotNexus.WebUI** | Presentation | Blazor Server app with chat, configuration, and agent management |
| **BotNexus.Cli** | Presentation | CLI commands, config management, doctor |
| **BotNexus.Channels.{Telegram,Tui}** | Presentation | External channel adapters |
| **BotNexus.Extensions.{Mcp,Skills,*}** | Extensions | Dynamically loaded capabilities |

### Dependency Flow

```text
Independent libraries (no BotNexus knowledge):
┌──────────────┐    ┌─────────────────────────────────┐
│  AgentCore   │    │  Providers.Core                  │
│  (agent loop,│    │  (LLM abstraction, streaming)    │
│   tools,     │    │    ↑                             │
│   hooks)     │    │  Providers.Anthropic/OpenAI/...  │
└──────────────┘    └─────────────────────────────────┘

BotNexus platform (uses the libraries above):
┌──────────────────────────────────────────────────┐
│  BotNexus.Domain (shared vocabulary, no deps)    │
│    ↑                                             │
│  Gateway.Contracts, Prompts, Sessions.Common     │
│    ↑                                             │
│  Gateway (composition root — USES AgentCore +    │
│           Providers + Domain together)           │
│    ↑                                             │
│  Gateway.Api (ASP.NET host)                      │
│    ↑                                             │
│  WebUI, CLI, Channels, Extensions                │
└──────────────────────────────────────────────────┘
```

**Critical distinction:** AgentCore and Providers do NOT depend on BotNexus.Domain. The Gateway bridges between them — it maps BotNexus domain types (AgentId, SessionId, etc.) to the generic types that AgentCore and Providers expect.

---

## World and Locations

A **World** is the runtime context for a Gateway instance. It defines:

| Concept | What It Is |
|---------|------------|
| **WorldIdentity** | Name, ID, emoji — displayed in WebUI and logs |
| **Locations** | Named resources (filesystems, APIs, databases, MCP servers) |
| **Agents** | The agents hosted in this World |
| **Execution Strategies** | How agents run (in-process, container, remote) |
| **Cross-World Permissions** | Which agents can communicate with other Worlds |

**Locations** are the fundamental unit for resource access:

```json
{
  "gateway": {
    "locations": {
      "repo-botnexus": { "type": "filesystem", "path": "Q:/repos/botnexus" },
      "copilot-api": { "type": "api", "endpoint": "https://api.enterprise.githubcopilot.com" }
    },
    "fileAccess": {
      "allowedReadPaths": ["@repo-botnexus"]
    }
  }
}
```

Agents reference Locations by name (`@repo-botnexus`) rather than raw paths. This enables:
- **Validation** via `botnexus doctor locations`
- **UI management** via the WebUI Locations view
- **Portability** — change the path in one place, all references update
- **Consistent security** — file access policies reference well-known Locations

---

## Extension Points

| Extension Point | Interface | Purpose |
|----------------|-----------|---------|
| **Channel Adapters** | `IChannelAdapter` | Connect to communication systems (SignalR, Telegram, TUI) |
| **Isolation Strategies** | `IIsolationStrategy` | Agent execution environments (in-process, container, remote) |
| **Tools** | `IAgentTool` | Agent capabilities (file ops, web, MCP, skills, memory) |
| **Hooks** | `IHookHandler` | Intercept tool execution (validation, audit, policy) |
| **Prompt Sections** | `IPromptSection` | Customize system prompts |
| **Session Stores** | `ISessionStore` | Persistence backends (InMemory, File, SQLite) |
| **Internal Triggers** | `IInternalTrigger` | Non-channel session creation (Cron, Soul) |

---

## For More Details

- **[Domain Model](domain-model.md)** — Core domain objects, value objects, and domain rules
- **[System Flows](system-flows.md)** — Message routing, agent execution, session lifecycle
- **[Principles](principles.md)** — Design principles and architectural decisions
- **[Extension Guide](extension-guide.md)** — How to extend the platform
- **[Development Guide](../development/README.md)** — Detailed implementation docs
- **[User Guide](../user-guide/configuration.md)** — Configuration reference
