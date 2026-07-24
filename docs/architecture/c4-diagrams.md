# BotNexus C4 Diagrams

**Status:** Second slice — C4 model diagrams (partially addresses #220)
**Scope:** [C4 model](https://c4model.com/) System Context and Container views for the
BotNexus platform, expressed in [Mermaid](https://mermaid.js.org/) so they render in both
GitHub Markdown preview and the published MkDocs site. Every element below is grounded in
the real `src/` layout — no components are invented.

This complements the [arc42-lite overview](README.md) (§3 Context and §5 Building Blocks)
by providing the visual System Context (Level 1) and Container (Level 2) views the
overview refers to.

---

## Level 1 — System Context

Who and what interacts with BotNexus, and why.

```mermaid
C4Context
    title System Context — BotNexus

    Person(user, "User", "Interacts through a channel: WebUI chat, Telegram, TUI, or on-behalf-of via cron.")
    Person(operator, "Operator", "Runs and manages the Gateway daemon and connects channels.")

    System(botnexus, "BotNexus Platform", "Multi-agent execution platform. Routes messages, orchestrates LLM interactions, runs tools, and persists sessions.")

    System_Ext(llm, "LLM Providers", "Anthropic, OpenAI, OpenAI-compatible, GitHub Copilot, GitHub Models.")
    System_Ext(channels_ext, "External Channels", "Telegram, Azure Service Bus, Agent365.")
    System_Ext(mcp, "MCP Servers", "Model Context Protocol tool servers reached via the Mcp extension.")

    Rel(user, botnexus, "Sends messages / receives streamed replies", "SignalR, Telegram, TUI")
    Rel(operator, botnexus, "Manages", "botnexus gateway CLI")
    Rel(botnexus, llm, "Streams prompts / completions", "HTTPS")
    Rel(botnexus, channels_ext, "Sends & receives messages", "channel adapters")
    Rel(botnexus, mcp, "Invokes external tools", "MCP")

    UpdateLayoutConfig($c4ShapeInRow="3", $c4BoundaryInRow="1")
```

**Notes**

- The **User** reaches the platform only through a **channel** — routing is
  channel-centric, never a direct agent call.
- **LLM Providers** are pluggable; each provider is a separate
  `BotNexus.Agent.Providers.*` library depending only on `Providers.Core`.
- **MCP Servers** are external tool servers reached through the
  `BotNexus.Extensions.Mcp` / `.McpInvoke` extensions.

---

## Level 2 — Container

The runtime containers inside the BotNexus platform boundary and how they collaborate.
Each container maps to real projects under `src/`.

```mermaid
C4Container
    title Container — BotNexus Platform

    Person(user, "User", "Human or app")
    System_Ext(llm, "LLM Providers", "Anthropic, OpenAI, Copilot, ...")
    System_Ext(mcp, "MCP Servers", "External tool servers")

    Container_Boundary(platform, "BotNexus Platform") {
        Container(webui, "WebUI", "Blazor Server", "Real-time chat & agent management surface. src/extensions/.../SignalR.BlazorClient")
        Container(api, "Gateway API + SignalR Hub", "ASP.NET Core", "REST API and real-time hub host. src/gateway/BotNexus.Gateway.Api")
        Container(gateway, "Gateway Core / Supervisor", ".NET library", "Composition root: agent supervision, config, isolation. src/gateway/BotNexus.Gateway")
        Container(channels, "Channel Adapters", "Extensions", "SignalR, Telegram, TUI, ServiceBus, Agent365. src/extensions/BotNexus.Extensions.Channels.*")
        Container(dispatch, "Dispatching + Conversations + Sessions", ".NET libraries", "Routes messages, rehydrates history, isolates each agent+channel pair. src/gateway/BotNexus.Gateway.{Dispatching,Conversations,Sessions}")
        Container(prompts, "Prompt Pipeline", ".NET library", "Assembles system prompt & context. src/gateway/BotNexus.Gateway.Prompts")
        Container(agentcore, "Agent Core", ".NET library", "Runs the agent loop, tool execution, hooks. src/agent/BotNexus.Agent.Core")
        Container(providers, "Provider Clients", ".NET libraries", "LLM client abstraction + per-provider impls. src/agent/BotNexus.Agent.Providers.*")
        Container(tools, "Tools & Capability Extensions", "Extensions", "exec, process, web, MCP, skills, data store, memory. src/gateway/BotNexus.Tools + src/extensions/*")
        Container(cron, "Cron Scheduler", ".NET library", "Scheduled / recurring triggers. src/gateway/BotNexus.Cron")
        Container(webhooks, "Webhooks", ".NET library", "Inbound webhook handling. src/gateway/BotNexus.Gateway.Webhooks")
        ContainerDb(store, "SQLite Stores", "SQLite (WAL)", "Sessions, conversations, memory, usage telemetry. src/persistence/BotNexus.Persistence.Sqlite")
    }

    Rel(user, webui, "Uses", "HTTPS")
    Rel(webui, api, "Connects", "SignalR")
    Rel(channels, api, "Inbound/outbound messages", "adapter protocols")
    Rel(api, gateway, "Hosts / invokes")
    Rel(gateway, dispatch, "Routes messages")
    Rel(dispatch, prompts, "Requests assembled prompt")
    Rel(dispatch, agentcore, "Runs agent loop")
    Rel(agentcore, providers, "Streams prompts / completions")
    Rel(providers, llm, "Calls", "HTTPS")
    Rel(agentcore, tools, "Dispatches tool calls")
    Rel(tools, mcp, "Invokes", "MCP")
    Rel(cron, dispatch, "Triggers scheduled runs")
    Rel(webhooks, dispatch, "Delivers inbound events")
    Rel(dispatch, store, "Reads/writes sessions & conversations")
    Rel(agentcore, store, "Reads/writes memory")

    UpdateLayoutConfig($c4ShapeInRow="3", $c4BoundaryInRow="1")
```

**Container-to-project mapping (verified against `src/`)**

| Container | Real project(s) |
|-----------|-----------------|
| WebUI | `src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient(.Core/.Mobile)` |
| Gateway API + SignalR Hub | `src/gateway/BotNexus.Gateway.Api` |
| Gateway Core / Supervisor | `src/gateway/BotNexus.Gateway` |
| Channel Adapters | `src/extensions/BotNexus.Extensions.Channels.{SignalR,Telegram,Tui,ServiceBus,Agent365}` |
| Dispatching / Conversations / Sessions | `src/gateway/BotNexus.Gateway.{Dispatching,Conversations,Sessions}` |
| Prompt Pipeline | `src/gateway/BotNexus.Gateway.Prompts` |
| Agent Core | `src/agent/BotNexus.Agent.Core` |
| Provider Clients | `src/agent/BotNexus.Agent.Providers.{Core,Anthropic,OpenAI,OpenAICompat,Copilot,GitHubModels,IntegrationMock}` |
| Tools & Capabilities | `src/gateway/BotNexus.Tools`, `src/extensions/BotNexus.Extensions.{ExecTool,ProcessTool,WebTools,Mcp,McpInvoke,Skills,Qmd,DataStore,DebugTool,AudioTranscription}`, `src/gateway/BotNexus.Memory` |
| Cron Scheduler | `src/gateway/BotNexus.Cron` |
| Webhooks | `src/gateway/BotNexus.Gateway.Webhooks` |
| SQLite Stores | `src/persistence/BotNexus.Persistence.Sqlite` |

---

## Level 3 — Component (deferred)

Per-container component decompositions (e.g. the internals of the Dispatching or
Agent Core containers) are a follow-up slice. See the
[runtime view](runtime-view.md) for sequence-level detail in the meantime.

---

## Related

- [arc42-lite overview](README.md)
- [Runtime view — sequence diagrams](runtime-view.md)
- [ADR index](adr/README.md)
