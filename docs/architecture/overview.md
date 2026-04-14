# BotNexus Architecture Overview

**Last Updated:** 2025-01  
**Status:** Canonical high-level reference

---

## System Vision

BotNexus is a **domain-driven, multi-agent execution platform** for building AI assistants. It provides a clean, layered architecture where agents orchestrate LLM interactions, execute tools, and manage sessions across multiple channels.

### Design Principles

- **Domain-Driven Design**: Core domain primitives are framework-agnostic
- **Clean Architecture**: Dependencies flow inward (Gateway → AgentCore → Providers → Domain)
- **Channel-Centric Routing**: Messages route through channels, not direct agent calls
- **Session Isolation**: Each (agent, session) pair gets its own instance
- **Pluggable Execution**: Isolation strategies enable different deployment models (in-process, container, remote)
- **Stream-First**: All LLM interactions stream events to clients
- **Extension-First**: Tools, channels, hooks, and prompts are pluggable

### Key Characteristics

| Capability | Description |
|------------|-------------|
| **Multi-Agent** | Run multiple agents simultaneously with independent configurations |
| **Multi-Channel** | Support SignalR, Telegram, TUI, cron triggers, and cross-world federation |
| **Multi-Session** | Agents handle concurrent conversations via session isolation |
| **Extensible** | Plugin-based tools, extensions, MCP servers, and skills |
| **Observable** | OpenTelemetry integration with distributed tracing |

---

## Architecture Layers

```
┌─────────────────────────────────────────────────────────────┐
│                   Presentation Layer                         │
│  WebUI, CLI, Channel Adapters (Telegram, TUI, etc.)        │
└────────────────────┬────────────────────────────────────────┘
                     │
┌────────────────────▼────────────────────────────────────────┐
│                   Gateway Layer                              │
│  BotNexus.Gateway, BotNexus.Gateway.Api                     │
│  • Message routing and dispatch                             │
│  • Agent supervisor (lifecycle management)                  │
│  • Session management                                       │
│  • Tool registry and execution                              │
│  • System prompt building                                   │
└────────────────────┬────────────────────────────────────────┘
                     │
┌────────────────────▼────────────────────────────────────────┐
│                   AgentCore Layer                            │
│  BotNexus.AgentCore                                         │
│  • Agent loop runner (LLM ↔ tool execution cycle)          │
│  • Tool execution engine (sequential/parallel)              │
│  • Stream accumulation                                      │
│  • Hooks (before/after tool calls)                          │
└────────────────────┬────────────────────────────────────────┘
                     │
┌────────────────────▼────────────────────────────────────────┐
│                   Provider Layer                             │
│  BotNexus.Providers.Core, Anthropic, OpenAI, Copilot       │
│  • LLM client abstraction                                   │
│  • Model registry                                           │
│  • Streaming protocol (SSE)                                 │
│  • Message conversion                                       │
└────────────────────┬────────────────────────────────────────┘
                     │
┌────────────────────▼────────────────────────────────────────┐
│                    Domain Layer                              │
│  BotNexus.Domain                                            │
│  • Primitives (AgentId, SessionId, ChannelKey)             │
│  • Value objects (SessionType, SessionStatus)               │
│  • Domain models (AgentDescriptor, GatewaySession)          │
└─────────────────────────────────────────────────────────────┘
```

**Dependency Rules:**

- **Domain** depends on nothing (pure domain models)
- **Providers** depend on Domain (for model definitions)
- **AgentCore** depends on Providers + Domain
- **Gateway** depends on AgentCore + Providers + Domain
- **Presentation** depends on Gateway

---

## Project Structure

### Solution Map

| Project | Responsibility | Dependencies |
|---------|---------------|--------------|
| **BotNexus.Domain** | Domain primitives and value objects | None |
| **BotNexus.Providers.Core** | LLM client abstraction, streaming | Domain |
| **BotNexus.Providers.{Anthropic,OpenAI,Copilot}** | Provider implementations | Providers.Core |
| **BotNexus.AgentCore** | Agent loop, tool execution, hooks | Providers, Domain |
| **BotNexus.Gateway** | Supervisor, router, session stores, tools | AgentCore, Providers, Domain |
| **BotNexus.Gateway.Api** | REST API, SignalR hub, triggers | Gateway |
| **BotNexus.WebUI** | Static web UI, SignalR client | Gateway (via SignalR) |
| **BotNexus.Cli** | CLI commands, config management | Gateway |
| **BotNexus.Channels.{Telegram,Tui}** | External channel adapters | Gateway |
| **BotNexus.Extensions.{Mcp,Skills,Memory}** | Extension implementations | Gateway, AgentCore |
| **BotNexus.Prompts** | Prompt pipeline and sections | Domain |

---

## Dependency Flow Diagram

```
Domain (no dependencies)
  ↑
Providers.Core
  ↑                ↑
Providers.*     AgentCore
  ↑                ↑
  └────── Gateway ─┘
            ↑
     ┌──────┴──────┐
  Gateway.Api   Channels.*
     ↑              ↑
  ┌──┴─┐        ┌──┴─┐
WebUI  CLI    Telegram TUI
```

---

## Extension Points

### 1. **Channel Adapters** (`IChannelAdapter`)

Integrate external communication systems:

- SignalR (WebUI)
- Telegram Bot API
- Terminal UI (TUI)
- Cron triggers
- Cross-world federation (HTTP relay)

### 2. **Isolation Strategies** (`IIsolationStrategy`)

Customize agent execution environment:

- **InProcess** (default): Fastest, shared memory, trusted agents
- **Container**: Docker isolation, resource limits
- **Remote**: Distributed execution, horizontal scaling
- **Sandbox**: Process-level isolation

### 3. **Tools** (`IAgentTool`)

Extend agent capabilities:

- **Workspace tools**: read, write, edit, exec, grep, glob
- **Gateway tools**: session, agent_converse, subagent_*, file_watcher, delay, cron
- **Extension tools**: MCP, web, skills, memory

### 4. **Hooks** (`IHookHandler`)

Intercept tool execution:

- Path validation (prevent escaping workspace)
- Tool policy enforcement (block dangerous operations)
- Audit logging (record all tool calls)
- Rate limiting (throttle expensive operations)

### 5. **Prompt Sections** (`IPromptSection`)

Customize system prompts:

- Identity, Workspace, Tools, ContextFiles, Guidelines, Examples
- Extension-based contributions via `IPromptContributor`

### 6. **Session Stores** (`ISessionStore`)

Custom persistence:

- InMemory (testing)
- File (simple deployments)
- SQLite (production default)
- Future: PostgreSQL, CosmosDB

---

## Location-Based Resource Management

BotNexus uses **Locations** as the fundamental unit for resource scoping:

```
Location = (WorldId, AgentId, SessionId?)
```

**Resource Scoping:**

| Resource | Scope | Example |
|----------|-------|---------|
| **Workspace** | Per-agent | `~/.botnexus/workspaces/{agentId}/` |
| **Session** | Per (agent, session) pair | `~/.botnexus/sessions/{sessionId}.jsonl` |
| **Memory** | Per agent or session | `~/.botnexus/memory/{agentId}/` |
| **Extensions** | Global or per-world | `~/.botnexus/extensions/` |
| **Logs** | Per agent, session, or world | `~/.botnexus/logs/{agentId}/` |

**Workspace Isolation:**

Each agent gets its own workspace directory. File tools (read, write, edit) operate relative to this workspace, preventing cross-agent file access without explicit permission.

**Session Isolation:**

Each (agent, session) pair gets its own agent instance, enabling:

- Independent conversation state
- Concurrent sessions with same agent
- Clean resource boundaries
- Session-specific memory

---

## Principles in Practice

### 1. SOLID Applied to BotNexus

| Principle | Application |
|-----------|-------------|
| **Single Responsibility** | Each layer has one job: Domain = models, Providers = LLM I/O, AgentCore = orchestration, Gateway = routing |
| **Open/Closed** | Extensions, hooks, and strategies enable new behavior without modifying core code |
| **Liskov Substitution** | All `IApiProvider` implementations are interchangeable; same for `IChannelAdapter`, `IIsolationStrategy` |
| **Interface Segregation** | Narrow interfaces: `IAgentTool`, `IHookHandler`, `IPromptSection` do one thing |
| **Dependency Inversion** | High-level code (Gateway) depends on abstractions (`ISessionStore`, `IAgentHandle`), not concrete implementations |

### 2. Extension-First Architecture

New capabilities are added via plugins, not code changes:

- **Add a tool**: Implement `IAgentTool`, register in `IToolRegistry`
- **Add a channel**: Implement `IChannelAdapter`, register in DI
- **Add a provider**: Implement `IApiProvider`, register in `ApiProviderRegistry`
- **Add an MCP server**: Configure in `platform-config.json`, auto-loads on startup
- **Add a hook**: Implement `IHookHandler`, register in hook dispatcher

### 3. Additive Migration Strategy

When adding features, we:

1. **Add new abstractions** without breaking existing code
2. **Keep old code working** during transition
3. **Deprecate gradually** with warnings
4. **Remove only when safe** (major version bumps)

**Example:** `SubscribeAll()` + `SendMessage()` replaced `JoinSession()` + `Prompt()`, but both models coexist for backwards compatibility.

### 4. Per-Channel Isolation in WebUI

WebUI uses a **subscribe-all model**:

1. Client connects to SignalR hub
2. Calls `SubscribeAll()` → joins all session groups
3. Receives session metadata (AgentId, SessionId, SessionType, Status)
4. Client-side DOM switching selects which session to display
5. All streaming events broadcast to session groups: `session:{sessionId}`

**Benefits:**

- Multi-session UI without manual join/leave
- Real-time updates across all sessions
- Supports future multi-client collaboration

---

## For More Details

This overview provides the 10,000-foot view. For deeper dives:

- **[Domain Model](domain-model.md)** — Core domain objects and rules
- **[System Flows](system-flows.md)** — Key runtime flows (message routing, agent execution, session lifecycle)
- **[Principles](principles.md)** — Design principles and architectural decisions
- **[Extension Guide](extension-guide.md)** — How to extend the platform

For detailed implementation:

- **[Development Guide](../development/README.md)** — Building and debugging BotNexus
- **[API Reference](../api-reference.md)** — REST API and SignalR hub documentation
