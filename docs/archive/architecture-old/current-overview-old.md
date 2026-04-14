# BotNexus Architecture Overview (DDD Refactored)

**Last Updated:** 2024-12  
**Status:** Current after DDD refactoring and WebUI simplification

---

## Table of Contents

1. [System Overview](#system-overview)
2. [Architecture Layers](#architecture-layers)
3. [Component Dependencies](#component-dependencies)
4. [Core Flows](#core-flows)
5. [Domain Model](#domain-model)
6. [Extension Points](#extension-points)
7. [Deployment Models](#deployment-models)

---

## System Overview

BotNexus is a **domain-driven, multi-agent execution platform** for building AI assistants. After the DDD refactoring, the architecture is organized into clean layers with clear dependencies and responsibilities.

### Design Principles

- **Domain-Driven Design**: Core domain primitives are framework-agnostic
- **Clean Architecture**: Dependencies point inward (Gateways → Domain, not vice versa)
- **Channel-Centric Routing**: Messages route through channels, not direct agent calls
- **Session Isolation**: Each (agent, session) pair gets its own instance
- **Pluggable Execution**: Isolation strategies enable different deployment models
- **Stream-First**: All LLM interactions stream events to clients

### Key Characteristics

- **Multi-Agent**: Run multiple agents simultaneously with independent configurations
- **Multi-Channel**: Support SignalR, Telegram, TUI, cron triggers, and cross-world federation
- **Multi-Session**: Agents handle concurrent conversations via session isolation
- **Extensible**: Plugin-based tools, extensions, MCP servers, and skills
- **Observable**: OpenTelemetry integration with distributed tracing

---

## Architecture Layers

```
┌─────────────────────────────────────────────────────────────┐
│                      Presentation Layer                      │
│  BotNexus.WebUI, BotNexus.Cli, Channel Adapters            │
└────────────────────┬────────────────────────────────────────┘
                     │
┌────────────────────▼────────────────────────────────────────┐
│                     Gateway Layer                            │
│  BotNexus.Gateway, BotNexus.Gateway.Api                     │
│  - Message routing and dispatch                             │
│  - Agent supervisor (lifecycle management)                  │
│  - Session management                                       │
│  - Tool registry                                            │
│  - System prompt building                                   │
└────────────────────┬────────────────────────────────────────┘
                     │
┌────────────────────▼────────────────────────────────────────┐
│                    AgentCore Layer                           │
│  BotNexus.AgentCore                                         │
│  - Agent loop runner                                        │
│  - Tool execution engine                                    │
│  - Stream accumulation                                      │
│  - Hooks (before/after tool calls)                          │
└────────────────────┬────────────────────────────────────────┘
                     │
┌────────────────────▼────────────────────────────────────────┐
│                    Provider Layer                            │
│  BotNexus.Providers.Core, Anthropic, OpenAI, etc.          │
│  - LLM client abstraction                                   │
│  - Model registry                                           │
│  - Streaming protocol                                       │
│  - Message conversion                                       │
└────────────────────┬────────────────────────────────────────┘
                     │
┌────────────────────▼────────────────────────────────────────┐
│                     Domain Layer                             │
│  BotNexus.Domain                                            │
│  - Primitives (AgentId, SessionId, ChannelKey, etc.)       │
│  - SessionType, SessionStatus                               │
│  - AgentDescriptor                                          │
│  - World identity model                                     │
└─────────────────────────────────────────────────────────────┘
```

**Dependency Rules:**

1. **Domain** depends on nothing (pure domain models)
2. **Providers** depend on Domain (for model definitions)
3. **AgentCore** depends on Providers + Domain
4. **Gateway** depends on AgentCore + Providers + Domain
5. **Presentation** depends on Gateway (+ optionally AgentCore for direct use)

---

## Component Dependencies

### Domain Layer

**BotNexus.Domain:**
- No dependencies (framework-agnostic)
- Contains strongly-typed IDs (AgentId, SessionId, etc.)
- Session domain model (SessionType, SessionStatus, SessionParticipant)
- World identity and cross-world federation models

**BotNexus.Sessions.Common:**
- Session JSONL file format
- Session metadata sidecar
- Session compaction logic (shared between Gateway and CodingAgent)

### Provider Layer

**BotNexus.Providers.Core:**
- Message models (UserMessage, AssistantMessage, ToolResultMessage)
- Streaming protocol (LlmStream, AssistantMessageEvent)
- LlmClient and model registry
- Provider abstraction (IApiProvider)

**BotNexus.Providers.{Anthropic, OpenAI, OpenAICompat, Copilot}:**
- Provider-specific implementations
- API communication (HTTP/SSE)
- Response parsing and streaming

### AgentCore Layer

**BotNexus.AgentCore:**
- Agent class (stateful execution wrapper)
- AgentLoopRunner (LLM ↔ tool execution cycle)
- StreamAccumulator (event → message conversion)
- ToolExecutor (sequential/parallel tool execution)
- Hook system (BeforeToolCall, AfterToolCall)

### Gateway Layer

**BotNexus.Gateway.Contracts:**
- Interfaces for all gateway abstractions
- IAgentSupervisor, IAgentRegistry, IAgentHandle
- IChannelAdapter, IChannelDispatcher
- ISessionStore, ISessionCompactor
- IInternalTrigger, IToolRegistry

**BotNexus.Gateway:**
- DefaultAgentSupervisor (instance lifecycle)
- DefaultMessageRouter (agent resolution)
- InProcessIsolationStrategy (agent execution)
- SystemPromptBuilder (prompt construction)
- Tool implementations (session, agent_converse, subagent_*, etc.)
- Session stores (InMemory, File, SQLite)

**BotNexus.Gateway.Api:**
- REST API controllers (agents, sessions, config, etc.)
- GatewayHub (SignalR hub)
- SignalRChannelAdapter (WebUI channel)
- CronTrigger, SoulTrigger (internal triggers)
- Middleware (auth, rate limiting, correlation ID)

**BotNexus.Gateway.Sessions:**
- Session store implementations
- Session warmup service
- Session lifecycle events

### Channel Layer

**BotNexus.Channels.Core:**
- ChannelAdapterBase (base class for channels)
- CrossWorldChannelAdapter (federation)
- OutboundMessage, InboundMessage models

**BotNexus.Channels.{Telegram, Tui}:**
- External channel implementations
- Platform-specific message handling

### Prompt Layer

**BotNexus.Prompts:**
- PromptPipeline (section composition)
- IPromptSection, IPromptContributor
- PromptContext (runtime parameters)

### Presentation Layer

**BotNexus.WebUI:**
- Static web UI (HTML + JavaScript)
- SignalR client connection
- Multi-session DOM management
- Markdown rendering (marked.js + DOMPurify)

**BotNexus.Cli:**
- CLI commands (init, validate, config, agent)
- Configuration management
- Interactive prompts

---

## Core Flows

### 1. Message Routing Flow

```
Client → Channel Adapter → IChannelDispatcher → IMessageRouter
  → IAgentSupervisor → IAgentHandle → AgentLoopRunner
  → LlmClient → Provider → LLM API
```

**Detailed Documentation:** [message-flow.md](./message-flow.md)

### 2. Agent Execution Flow

```
AgentDescriptor → IAgentSupervisor.GetOrCreateAsync()
  → IIsolationStrategy.CreateAsync()
  → InProcessIsolationStrategy (default)
  → AgentCore.Agent (with tools, hooks, system prompt)
  → IAgentHandle (wrapped for Gateway consumption)
```

**Detailed Documentation:** [agent-execution.md](./agent-execution.md)

### 3. Session Lifecycle

```
SendMessage(agentId, channelType) → ResolveOrCreateSession()
  → SessionType: UserAgent, Status: Active
  → Participants: [User: connectionId]
  → Auto-join to session group
  → Dispatch message
```

**Session States:** Active → Suspended → Sealed  
**Session Types:** UserAgent, AgentSelf, AgentSubAgent, AgentAgent, Soul, Cron

**Detailed Documentation:** [message-flow.md#session-lifecycle](./message-flow.md#session-lifecycle)

### 4. Internal Triggers

**Cron Trigger:**
- Scheduled execution via cron expressions
- Creates `SessionType.Cron` sessions
- Batch execution (no streaming)

**Soul Trigger:**
- Daily soul session heartbeat
- Creates `SessionType.Soul` sessions
- Date-based session IDs: `soul:{agentId}:{yyyy-MM-dd}`
- Reflection on seal (end-of-day summary)

**Detailed Documentation:** [triggers-and-federation.md](./triggers-and-federation.md)

### 5. Agent-to-Agent Communication

**agent_converse Tool:**
- Peer conversations within same world
- Cycle detection via call chain tracking
- Authorization via `SubAgentIds` whitelist

**Cross-World Federation:**
- Format: `world:worldId:agentId`
- HTTP relay protocol
- Dual sessions (source + target)
- Authentication via `X-Cross-World-Key`

**Detailed Documentation:** [triggers-and-federation.md#agent-to-agent-communication](./triggers-and-federation.md#agent-to-agent-communication)

### 6. Prompt Pipeline

```
IPromptSection[] → PromptPipeline.Build(PromptContext)
  → Order sections → Compose → System Prompt
  → IContextBuilder → AgentCore.Agent
```

**Sections (Order):**
1. Identity (100)
2. Workspace (200)
3. Tools (300)
4. Context Files (400)
5. Guidelines (500)
6. Examples (600)

**Detailed Documentation:** [prompt-pipeline.md](./prompt-pipeline.md)

### 7. WebUI Connection

```
Browser → SignalR → GatewayHub.SubscribeAll()
  → Join all session groups
  → SendMessage(agentId, channelType, content)
  → Auto-create session
  → Streaming events → SignalRChannelAdapter
  → Broadcast to session group
  → DOM-swap switching (client-side)
```

**Detailed Documentation:** [webui-connection.md](./webui-connection.md)

---

## Domain Model

### Core Primitives

**Strongly-Typed IDs:**
- `AgentId`: Agent identifier (e.g., "gateway", "coding-agent")
- `SessionId`: Session identifier (auto-generated or soul-based)
- `ChannelKey`: Channel type (e.g., "signalr", "telegram", "cron")
- `SenderId`: Sender identifier (connectionId, user ID, etc.)
- `ConversationId`: Conversation identifier (alias for SessionId in some contexts)

**Smart Enums:**
- `SessionType`: UserAgent, AgentSelf, AgentSubAgent, AgentAgent, Soul, Cron
- `SessionStatus`: Active, Suspended, Sealed
- `MessageRole`: User, Assistant, System
- `ParticipantType`: User, Agent, System
- `ExecutionStrategy`: InProcess, Container, Remote, Sandbox

### Session Model

```csharp
public class GatewaySession
{
    public SessionId SessionId { get; set; }
    public AgentId AgentId { get; set; }
    public SessionType SessionType { get; set; }
    public SessionStatus Status { get; set; }
    public ChannelKey? ChannelType { get; set; }
    public string? CallerId { get; set; }
    public List<SessionParticipant> Participants { get; set; }
    public List<SessionEntry> History { get; set; }
    public Dictionary<string, object?> Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
```

### Agent Descriptor

```csharp
public record AgentDescriptor
{
    public AgentId AgentId { get; init; }
    public string DisplayName { get; init; }
    public string ApiProvider { get; init; }
    public string ModelId { get; init; }
    public ExecutionStrategy ExecutionStrategy { get; init; }
    public List<string> Tools { get; init; }
    public List<string> SubAgentIds { get; init; }
    public string? SystemPrompt { get; init; }
    public FileAccessPolicy FileAccess { get; init; }
    public SoulAgentConfig? Soul { get; init; }
    // ... more properties
}
```

---

## Extension Points

### 1. Channel Adapters

Implement `IChannelAdapter` to integrate external communication systems:

```csharp
public interface IChannelAdapter
{
    ChannelKey ChannelType { get; }
    string DisplayName { get; }
    bool SupportsStreaming { get; }
    
    Task StartAsync(IChannelDispatcher dispatcher, CancellationToken ct);
    Task SendAsync(OutboundMessage message, CancellationToken ct);
}
```

**Built-In Adapters:**
- SignalRChannelAdapter (WebUI)
- CronChannelAdapter (scheduled execution)
- CrossWorldChannelAdapter (federation)

**External Adapters:**
- TelegramChannelAdapter
- TuiChannelAdapter (terminal UI)

### 2. Isolation Strategies

Implement `IIsolationStrategy` to customize agent execution:

```csharp
public interface IIsolationStrategy
{
    string Name { get; }
    Task<IAgentHandle> CreateAsync(
        AgentDescriptor descriptor,
        AgentExecutionContext context,
        CancellationToken ct);
}
```

**Built-In Strategies:**
- InProcessIsolationStrategy (default, fastest)
- ContainerIsolationStrategy (planned)
- RemoteIsolationStrategy (planned)
- SandboxIsolationStrategy (planned)

### 3. Tools

Implement `IAgentTool` (from AgentCore) to add custom tools:

```csharp
public interface IAgentTool
{
    string Name { get; }
    Tool Definition { get; }
    
    Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct);
}
```

**Gateway Tools:**
- session, agent_converse, subagent_*, file_watcher, delay, cron

**Workspace Tools:**
- read, write, edit, exec, grep, glob

**Extension Tools:**
- MCP tools, web tools, skill tools, memory tools

### 4. Hooks

Implement `IHookHandler` to intercept tool execution:

```csharp
public interface IHookHandler
{
    Task<BeforeToolCallResult> BeforeAsync(BeforeToolCallContext context);
    Task<AfterToolCallResult> AfterAsync(AfterToolCallContext context);
}
```

**Use Cases:**
- Path validation (prevent escaping workspace)
- Tool policy enforcement (block dangerous operations)
- Audit logging (record all tool calls)
- Rate limiting (throttle expensive operations)

### 5. Prompt Sections

Implement `IPromptSection` to customize system prompts:

```csharp
public interface IPromptSection
{
    int Order { get; }
    bool ShouldInclude(PromptContext context);
    IReadOnlyList<string> Build(PromptContext context);
}
```

**Built-In Sections:**
- Identity, Workspace, Tools, ContextFiles, Guidelines, Examples

### 6. Session Stores

Implement `ISessionStore` for custom persistence:

```csharp
public interface ISessionStore
{
    Task<GatewaySession?> GetAsync(SessionId sessionId, CancellationToken ct);
    Task SaveAsync(GatewaySession session, CancellationToken ct);
    Task<IReadOnlyList<GatewaySession>> ListAsync(AgentId agentId, CancellationToken ct);
}
```

**Built-In Stores:**
- InMemorySessionStore (testing)
- FileSessionStore (simple deployments)
- SqliteSessionStore (production)

---

## Deployment Models

### 1. Single-Process (Default)

All components run in a single process:

```
Gateway.Api (Kestrel) → InProcess Agents → SQLite Sessions
```

**Characteristics:**
- Simplest deployment
- Lowest latency (<10ms agent startup)
- Shared memory (efficient but requires trust)
- Suitable for dev, testing, and small deployments

### 2. Multi-Process (Future)

Agents run in separate processes:

```
Gateway.Api → Remote Agents (via HTTP/gRPC) → Isolated Sessions
```

**Characteristics:**
- Process isolation (stronger security)
- Independent scaling (scale agents separately)
- Higher latency (~50-100ms per call)
- Suitable for untrusted or resource-intensive agents

### 3. Container-Based (Future)

Agents run in Docker containers:

```
Gateway.Api → Container Agents → Network-Isolated Sessions
```

**Characteristics:**
- Full isolation (network, filesystem, CPU, memory)
- Resource limits (prevent runaway agents)
- Slower startup (~1-3 seconds)
- Suitable for multi-tenancy or SaaS deployments

### 4. Distributed (Future)

Agents distributed across machines:

```
Gateway Cluster → Load Balancer → Agent Cluster → Distributed Store
```

**Characteristics:**
- Horizontal scaling (handle high loads)
- Fault tolerance (agent failover)
- Complex orchestration (Kubernetes, etc.)
- Suitable for enterprise deployments

---

## Summary

**Architecture Highlights:**

1. **Domain-Driven Design**: Clean separation of domain primitives from infrastructure
2. **Layered Dependencies**: Each layer depends only on layers below it
3. **Channel-Centric Routing**: Messages flow through channels, not direct agent calls
4. **Session Isolation**: Independent instances per (agent, session) pair
5. **Pluggable Everything**: Channels, isolation strategies, tools, hooks, stores, prompts
6. **Stream-First**: All LLM interactions stream events to clients
7. **Multi-Agent**: Support for concurrent agents with independent configurations

**Key Architectural Decisions:**

- **Gateway replaces CodingAgent**: Gateway is now the primary orchestrator
- **Subscribe-All model**: Clients subscribe to all sessions upfront (WebUI)
- **Auto-session on send**: Sessions created implicitly on first message
- **Soul sessions**: Daily persistent sessions for long-term memory
- **Cross-world federation**: Agents can talk to agents in other BotNexus instances
- **Isolation strategies**: Pluggable agent execution (in-process, container, remote)

**Related Documentation:**

- [Message Flow and Session Lifecycle](./message-flow.md)
- [Agent Execution Architecture](./agent-execution.md)
- [Internal Triggers and Federation](./triggers-and-federation.md)
- [Prompt Pipeline](./prompt-pipeline.md)
- [Session Stores and Persistence](./session-stores.md)
- [WebUI Connection and Multi-Session](./webui-connection.md)
