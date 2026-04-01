# BotNexus Architecture Overview

**Version:** 1.0  
**Last Updated:** 2026-04-01  
**Lead Architect:** Leela

---

## Table of Contents

1. [System Overview](#system-overview)
2. [Component Diagram](#component-diagram)
3. [Message Flow](#message-flow)
4. [Dynamic Extension Loading](#dynamic-extension-loading)
5. [Dependency Injection](#dependency-injection)
6. [Core Abstractions](#core-abstractions)
7. [Multi-Agent Routing](#multi-agent-routing)
8. [Provider Architecture](#provider-architecture)
9. [Agent Workspace and Memory](#agent-workspace-and-memory)
10. [Session Management](#session-management)
11. [Cron and Scheduling](#cron-and-scheduling)
12. [Security Model](#security-model)
13. [Observability](#observability)
14. [Installation Layout](#installation-layout)

---

## 1. System Overview

**BotNexus** is a modular, extensible AI agent execution platform built in C#/.NET. It enables running multiple AI agents concurrently, each powered by configurable LLM providers, receiving messages from multiple channels (Discord, Slack, Telegram, WebSocket), and executing tools dynamically.

### Design Philosophy

- **Modular**: Core engine with pluggable channels, providers, and tools
- **Extensible**: Dynamic assembly loading with folder-based organization
- **Secure**: Extension validation, OAuth support, API key authentication
- **Observable**: Correlation IDs, health checks, activity stream for real-time monitoring
- **Resilient**: Retry logic with exponential backoff, error handling, graceful degradation

### Key Characteristics

- **Lean Core**: 17 class libraries, minimal dependencies
- **Contract-First**: Core module defines 13 interfaces; implementations in outer modules
- **Async-First**: All operations async (I/O, message processing, tool execution)
- **Configuration-Driven**: Extensions loaded only when configured; no automatic discovery
- **Session-Persistent**: Conversation history persisted to disk (JSONL format)

---

## 2. Component Diagram

```
┌─────────────────────────────────────────────────────────────────────────┐
│                          External Clients                               │
│   Discord  │  Slack  │  Telegram  │  WebSocket  │  REST API            │
└─────────────┬──────────┬────────────┬───────────┬────────────────────────┘
              │          │            │           │
              ▼          ▼            ▼           ▼
       ┌──────────────────────────────────────────────┐
       │           Channel Implementations            │
       │  (DiscordChannel, SlackChannel, etc.)        │
       └────────────────┬─────────────────────────────┘
                        │
                        ▼
            ┌────────────────────────┐
            │   Message Bus (IPC)    │
            │  (Bounded Channel)     │
            └────────────┬───────────┘
                         │
                         ▼
        ┌────────────────────────────────────┐
        │   Gateway (Orchestrator)            │
        │  - Reads from Message Bus           │
        │  - Manages Channels                 │
        │  - Broadcasts ActivityEvents        │
        └────────┬─────────────────────────────┘
                 │
       ┌─────────┴─────────┐
       │                   │
       ▼                   ▼
  ┌─────────────┐  ┌──────────────┐
  │ AgentRouter │  │ WebUI Events │
  │  (Routes to │  │ (Activity    │
  │   agents)   │  │  Stream)     │
  └─────┬───────┘  └──────────────┘
        │
        ├─────────────────┬──────────────────┐
        │                 │                  │
        ▼                 ▼                  ▼
  ┌──────────────┐  ┌──────────────┐  ┌─────────────┐
  │ AgentRunner1 │  │ AgentRunner2 │  │ AgentRunnerN│
  │  (per agent) │  │  (per agent) │  │ (per agent) │
  └────┬─────────┘  └────┬─────────┘  └────┬────────┘
       │                 │                  │
       ├─────────────────┼──────────────────┤
       ▼                 ▼                  ▼
  ┌──────────────────────────────────────────────┐
  │  CommandRouter (Handles /commands)           │
  └──────────────────────────────────────────────┘
       │
       ├─ /help, /reset, /list_agents
       │
       └─────────────────┬──────────────────────────┐
                         │                          │
                         ▼                          ▼
               ┌────────────────────────┐  ┌─────────────────────┐
               │    AgentLoop           │  │  ToolRegistry       │
               │  - Context Building    │  │  - FilesystemTool   │
               │  - LLM Calls           │  │  - ShellTool        │
               │  - Tool Execution      │  │  - WebTool          │
               │  - Session Persistence │  │  - GitHubTool       │
               │  - Hooks (Before/After)│  │  - CronTool         │
               └──────────┬─────────────┘  │  - McpTool          │
                          │                └─────────────────────┘
                          ▼
            ┌─────────────────────────────────┐
            │  Provider Registry              │
            │  ┌─────────────────────────────┐│
            │  │ • Copilot (OAuth)           ││
            │  │ • OpenAI (API Key)          ││
            │  │ • Anthropic (API Key)       ││
            │  │ • Custom Providers          ││
            │  └─────────────────────────────┘│
            └─────────────────────────────────┘
                         │
                         ▼
            ┌────────────────────────────┐
            │  SessionManager            │
            │  (JSONL Persistence)       │
            └────────────────────────────┘

┌──────────────────────────────────────────────────────────────┐
│ Core Services (DI Container)                                 │
│ - IMessageBus, IActivityStream, IBotNexusMetrics             │
│ - ISessionManager, ICommandRouter                            │
│ - IAgentRouter, ChannelManager, ToolRegistry                │
│ - ProviderRegistry, ExtensionLoadReport                      │
└──────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────┐
│ Configuration (~/.botnexus/config.json → BotNexusConfig)      │
│ - Agents, Providers, Channels, Tools                         │
│ - Gateway (host, port, authentication)                       │
│ - Extensions (loader path, security settings)                │
└──────────────────────────────────────────────────────────────┘
```

---

## 3. Message Flow

### End-to-End Message Processing

```
1. INBOUND (Channel → Message Bus)
   ┌─────────────┐
   │   Channel   │ (Discord, Slack, Telegram, WebSocket, etc.)
   └──────┬──────┘
          │ Receives message from external service
          ▼
   ┌──────────────────────────┐
   │ BaseChannel handler      │
   │ (OnMessageReceived)      │
   └──────┬───────────────────┘
          │ Creates InboundMessage
          │ {from: senderId, content, channel, sessionKey, ...}
          ▼
   ┌──────────────────────────┐
   │ IMessageBus.PublishAsync │ (Bounded channel, capacity 1000)
   └──────┬───────────────────┘
          │
          ▼
   ┌──────────────────────────┐
   │ Message Bus Queue        │
   │ (Async enumerable)       │
   └──────────────────────────┘

2. PROCESSING (Gateway Main Loop)
   ┌──────────────────────────┐
   │ Gateway.ExecuteAsync()   │
   │ (BackgroundService)      │
   └──────┬───────────────────┘
          │ ReadAllAsync from MessageBus
          ▼
   ┌──────────────────────────┐
   │ AgentRouter.RouteAsync   │
   │ - Parse agent metadata   │
   │ - Resolve runner(s)      │
   │ - Broadcast if needed    │
   └──────┬───────────────────┘
          │
          ▼
   ┌──────────────────────────┐
   │ AgentRunner.RunAsync     │
   │ (Per-agent coordinator)  │
   └──────┬───────────────────┘
          │
          ├─ Try CommandRouter first
          │  (for /commands)
          │
          ├─ Or AgentLoop if not command
          │
          ▼
   ┌──────────────────────────┐
   │ CommandRouter            │
   │ /help, /reset, etc.      │
   └──────────────────────────┘
          OR
   ┌──────────────────────────┐
   │ AgentLoop.RunAsync       │
   └──────┬───────────────────┘
          │
          ▼ (11-step cycle)
   ┌──────────────────────────────────────────────────────┐
   │ 1. Get/create session via SessionManager             │
   │ 2. Call IAgentHook.OnBeforeAsync hooks               │
   │ 3. Add user message to session history               │
   │ 4. Register additional tools if provided             │
   │ 5. Loop (max 40 iterations):                         │
   │    a. Build context via ContextBuilder               │
   │       (trim history to fit token window)             │
   │    b. Create ChatRequest with tools                  │
   │    c. Call ILlmProvider.ChatAsync                    │
   │    d. Record metrics                                 │
   │    e. Add response to session history                │
   │    f. If tool calls: execute via ToolRegistry        │
   │    g. Add tool results to history                    │
   │    h. Continue loop if more tools needed             │
   │ 6. Save session to disk                              │
   │ 7. Call IAgentHook.OnAfterAsync hooks                │
   │ 8. Return response to channel                        │
   └──────────────────────────────────────────────────────┘

3. RESPONSE (Outbound)
   ┌──────────────────────────┐
   │ AgentLoop returns        │
   │ OutboundMessage response │
   └──────┬───────────────────┘
          │
          ▼
   ┌──────────────────────────┐
   │ IChannel.SendAsync       │
   │ (Route back to origin)   │
   └──────┬───────────────────┘
          │ Discord, Slack, Telegram, WebSocket, etc.
          ▼
   ┌──────────────────────────┐
   │ External Channel API     │
   │ Message posted/sent      │
   └──────────────────────────┘

4. OBSERVABILITY (Parallel)
   ┌──────────────────────────┐
   │ Gateway publishes        │
   │ ActivityEvent            │
   └──────┬───────────────────┘
          │
          ▼
   ┌──────────────────────────┐
   │ IActivityStream          │
   │ (Event broadcast)        │
   └──────┬───────────────────┘
          │
          ▼
   ┌──────────────────────────┐
   │ WebUI WebSocket clients  │
   │ (Real-time monitoring)   │
   └──────────────────────────┘
```

### Correlation Flow

Each message is tagged with a correlation ID:
- Generated once at channel ingress
- Propagated through all downstream operations
- Attached to logs, metrics, and activity events
- Enables tracing entire request across all services

---

## 4. Dynamic Extension Loading

BotNexus loads extensions **only when explicitly configured**—nothing loads by default. This minimizes attack surface and keeps the deployment minimal.

### Extension Types

Three extension categories are supported:

1. **Providers** — LLM backends (OpenAI, Anthropic, Copilot)
2. **Channels** — Message sources (Discord, Slack, Telegram)
3. **Tools** — Agent capabilities (custom plugins)

### Folder Structure

```
extensions/
├── channels/
│   ├── discord/
│   │   ├── BotNexus.Channels.Discord.dll
│   │   └── dependencies/
│   ├── slack/
│   │   ├── BotNexus.Channels.Slack.dll
│   │   └── dependencies/
│   └── telegram/
│       ├── BotNexus.Channels.Telegram.dll
│       └── dependencies/
├── providers/
│   ├── copilot/
│   │   ├── BotNexus.Providers.Copilot.dll
│   │   └── dependencies/
│   ├── openai/
│   │   ├── BotNexus.Providers.OpenAI.dll
│   │   └── dependencies/
│   └── anthropic/
│       ├── BotNexus.Providers.Anthropic.dll
│       └── dependencies/
└── tools/
    ├── github/
    │   ├── BotNexus.Tools.GitHub.dll
    │   └── dependencies/
    └── custom_tool_name/
        ├── CustomTool.dll
        └── dependencies/
```

### Configuration Example

```json
{
  "BotNexus": {
    "ExtensionsPath": "~/.botnexus/extensions",
    "Extensions": {
      "RequireSignedAssemblies": false,
      "MaxAssembliesPerExtension": 50,
      "DryRun": false
    },
    "Providers": {
      "copilot": {
        "Auth": "oauth",
        "ApiBase": "https://api.githubcopilot.com"
      },
      "openai": {
        "Auth": "apikey",
        "ApiKey": "sk-..."
      }
    },
    "Channels": {
      "Instances": {
        "discord": {
          "Enabled": true,
          "BotToken": "discord_bot_token"
        },
        "slack": {
          "Enabled": true,
          "BotToken": "slack_bot_token",
          "SigningSecret": "slack_signing_secret"
        }
      }
    }
  }
}
```

### Loading Mechanism

**File:** `BotNexus.Core/Extensions/ExtensionLoaderExtensions.cs`

**Process:**

1. **Scan Configuration** — Read enabled extensions from `BotNexusConfig.Extensions`
2. **Load Assemblies** — For each extension:
   - Locate assembly DLL in `extensions/{type}/{name}/`
   - Validate signature (if `RequireSignedAssemblies` enabled)
   - Load via `AssemblyLoadContext` (isolated per extension)
3. **Register Services** — For each loaded assembly:
   - Check for `IExtensionRegistrar` implementation
     - If found: call `Register(services, config)` for full DI control
     - If not found: convention-based discovery
       - Scan for implementations of `IChannel`, `ILlmProvider`, `ITool`
       - Auto-register with sensible defaults
4. **Report Results** — Create `ExtensionLoadReport` with:
   - Success/failure for each extension
   - Any validation errors
   - Loaded type counts

### Registration Patterns

#### Pattern 1: IExtensionRegistrar (Full Control)

```csharp
public class DiscordExtension : IExtensionRegistrar
{
    public void Register(IServiceCollection services, 
                        ProviderConfig config)
    {
        services.AddSingleton<IChannel>(sp =>
            new DiscordChannel(config));
    }
}
```

#### Pattern 2: Convention-Based (Zero Config)

```csharp
public class OpenAiProvider : ILlmProvider
{
    // Auto-discovered and registered by ExtensionLoader
}
```

### AssemblyLoadContext Isolation

Each extension loads in its own `AssemblyLoadContext`:

- **Benefit 1**: Dependency conflicts isolated (extension A can use Newtonsoft 12.0, extension B can use 13.0)
- **Benefit 2**: Future hot-reload capability (unload context without process restart)
- **Benefit 3**: Reduced memory footprint (shared framework types only)

### Security Features

- **Signature Validation**: Optional requirement for signed assemblies
- **Allowed Shared Assemblies**: Whitelist of core types extensions can depend on
- **Max Assemblies Per Extension**: Limit to prevent DOS attacks
- **Dry-Run Mode**: Validate without actually loading

---

## 5. Dependency Injection

BotNexus uses Microsoft.Extensions.DependencyInjection (standard .NET DI container).

### Service Lifetimes

| Service | Lifetime | Reason |
|---------|----------|--------|
| `IMessageBus` | Singleton | Single queue for all messages |
| `IActivityStream` | Singleton | System-wide event broadcast |
| `IBotNexusMetrics` | Singleton | Aggregate metrics |
| `ISessionManager` | Singleton | Thread-safe persistent storage |
| `ProviderRegistry` | Singleton | Provider cache |
| `ChannelManager` | Singleton | Manages all channel lifecycles |
| `IChannel` implementations | Singleton | Long-lived connections |
| `ILlmProvider` implementations | Singleton | Connection pooling |
| `ITool` implementations | Singleton | Stateless tools |
| `Gateway` | Singleton, BackgroundService | Main orchestrator |
| `CronService` | Singleton, BackgroundService | Scheduled jobs |
| `IHeartbeatService` (`CronHeartbeatAdapter`) | Singleton | Thin adapter over CronService |
| `IAgentHook` implementations | Transient | Fresh per request |
| Per-request objects | Scoped | HTTP request context |

### Core Service Registration

**File:** `BotNexus.Core/Extensions/ServiceCollectionExtensions.cs`

```csharp
public static IServiceCollection AddBotNexusCore(
    this IServiceCollection services, 
    BotNexusConfig config)
{
    services.Configure<BotNexusConfig>(options =>
        // Bind from "BotNexus" config section (~/.botnexus/config.json overrides appsettings.json)
    );
    
    services.AddSingleton<IMessageBus>(_ =>
        new MessageBus(capacity: 1000));
    
    services.AddSingleton<IActivityStream>(_ =>
        new ActivityStream());
    
    services.AddSingleton<IBotNexusMetrics>(_ =>
        new BotNexusMetrics());
    
    return services;
}
```

### Gateway Service Registration

**File:** `BotNexus.Gateway/BotNexusServiceExtensions.cs`

```csharp
public static IServiceCollection AddBotNexus(
    this IServiceCollection services, 
    BotNexusConfig config)
{
    // Add core services
    services.AddBotNexusCore(config);
    
    // Add extensions (channels, providers, tools)
    services.AddBotNexusExtensions(config);
    
    // Add gateway-specific services
    services.AddSingleton(new ProviderRegistry(providers));
    services.AddSingleton<IAgentRouter, AgentRouter>();
    services.AddSingleton<ChannelManager>();
    services.AddSingleton<ICronService, CronService>();
    services.AddHostedService(sp => (CronService)sp.GetRequiredService<ICronService>());
    services.AddSingleton<IHeartbeatService, CronHeartbeatAdapter>();
    
    // Add Gateway as BackgroundService
    services.AddHostedService<Gateway>();
    
    // Add health checks
    services.AddHealthChecks()
        .AddCheck<MessageBusHealthCheck>("message_bus")
        .AddCheck<ProviderRegistrationHealthCheck>("provider_registration")
        .AddCheck<ExtensionLoaderHealthCheck>("extension_loader")
        .AddCheck<ChannelReadinessHealthCheck>("channel_readiness", tags: ["ready"])
        .AddCheck<ProviderReadinessHealthCheck>("provider_readiness", tags: ["ready"])
        .AddCheck<CronServiceHealthCheck>("cron_service");
    
    return services;
}
```

### Extension Service Registration

**File:** `BotNexus.Core/Extensions/ExtensionLoaderExtensions.cs`

```csharp
private static void AddBotNexusExtensions(
    this IServiceCollection services, 
    BotNexusConfig config)
{
    var loader = new ExtensionLoader(config);
    var report = loader.LoadExtensions(services);
    
    services.AddSingleton(report);
    
    // Convention-based or IExtensionRegistrar-based
    // registration happens inside loader
}
```

---

## 6. Core Abstractions

The Core module defines 13 interfaces that form the contract between the engine and all extensions:

| Interface | Location | Purpose |
|-----------|----------|---------|
| `IMessageBus` | `Abstractions/IMessageBus.cs` | Async message queue between channels and agents |
| `IChannel` | `Abstractions/IChannel.cs` | Messaging channel contract (Discord, Slack, etc.) |
| `IAgentRunner` | `Abstractions/IAgentRunner.cs` | Per-agent message processor |
| `ILlmProvider` | `Abstractions/ILlmProvider.cs` | LLM backend contract |
| `ITool` | `Abstractions/ITool.cs` | Executable tool callable by agents |
| `ICommandRouter` | `Abstractions/ICommandRouter.cs` | Routes /commands (not agent messages) |
| `ISessionManager` | `Abstractions/ISessionManager.cs` | Persistent conversation storage |
| `IExtensionRegistrar` | `Abstractions/IExtensionRegistrar.cs` | Optional DI registration hook for extensions |
| `IOAuthProvider` | `Abstractions/IOAuthProvider.cs` | OAuth token acquisition and validation |
| `IOAuthTokenStore` | `Abstractions/IOAuthTokenStore.cs` | Persistent OAuth token storage |
| `IActivityStream` | `Abstractions/IActivityStream.cs` | System-wide event publication |
| `IAgentHook` | `Abstractions/IAgentHook.cs` | Pipeline middleware (before/after/error) |
| `IMemoryStore` | `Abstractions/IMemoryStore.cs` | Persistent agent memory/notes |

### Key Design Principles

- **Small Surface**: Each interface ≤ 5 methods
- **Focused**: One responsibility per interface
- **Extendable**: All implemented outside Core
- **Async-First**: All I/O operations return `Task` or `ValueTask`
- **Testable**: No static dependencies, everything injected

---

## 7. Multi-Agent Routing

BotNexus supports multiple agents running concurrently, each with independent configurations.

### Agent Router

**File:** `BotNexus.Gateway/AgentRouter.cs`

The `AgentRouter` resolves which agent(s) should handle an inbound message:

```
InboundMessage
    ├─ Metadata:
    │  ├─ "agent" (exact name)
    │  ├─ "agent_name" (exact name)
    │  └─ "agentName" (camelCase)
    │
    ├─ If agent name specified
    │  └─ Route to that agent's runner
    │
    ├─ If broadcast token ("all", "*")
    │  └─ Route to all agent runners
    │
    └─ If unspecified
       ├─ If GatewayConfig.DefaultAgent set
       │  └─ Route to default
       └─ Else if BroadcastWhenAgentUnspecified
          └─ Route to all
       └─ Else error
```

### Agent Configuration

**File:** `BotNexus.Core/Configuration/AgentConfig.cs`

Each agent is independently configured:

```json
{
  "BotNexus": {
    "Agents": {
      "default": {
        "Name": "default",
        "SystemPrompt": "You are a helpful assistant",
        "Model": "gpt-4o",
        "Provider": "openai",
        "MaxTokens": 2000,
        "Temperature": 0.7,
        "MaxToolIterations": 40,
        "Timezone": "UTC",
        "EnableMemory": true,
        "McpServers": [
          {
            "Name": "filesystem",
            "Command": "mcp-filesystem"
          }
        ],
        "CronJobs": [
          {
            "Name": "daily_digest",
            "Schedule": "0 8 * * *",
            "Prompt": "Generate daily digest"
          }
        ]
      },
      "planner": {
        "Name": "planner",
        "SystemPrompt": "You are a project planner",
        "Model": "gpt-4o",
        "Provider": "openai",
        "MaxToolIterations": 50
      }
    }
  }
}
```

### Agent Runner

**File:** `BotNexus.Agent/AgentRunner.cs`

One `AgentRunner` per configured agent:

```csharp
public class AgentRunner : IAgentRunner
{
    public async Task RunAsync(InboundMessage message)
    {
        // 1. Try command router first
        if (await _commandRouter.TryHandleAsync(message))
            return; // Command handled
        
        // 2. Run agent loop
        var response = await _agentLoop.RunAsync(message);
        
        // 3. Send response through original channel
        await message.Channel.SendAsync(response);
    }
}
```

---

## 8. Provider Architecture

### ILlmProvider Interface

**File:** `BotNexus.Core/Abstractions/ILlmProvider.cs`

```csharp
public interface ILlmProvider
{
    string DefaultModel { get; }
    GenerationSettings Generation { get; }
    Task<ChatResponse> ChatAsync(ChatRequest request);
    Task<IAsyncEnumerable<StreamedChatDelta>> ChatStreamAsync(ChatRequest request);
}
```

### LlmProviderBase (Abstract Base)

**File:** `BotNexus.Providers.Base/LlmProviderBase.cs`

Provides common infrastructure:

- **Retry Logic**: Exponential backoff (configurable)
- **Error Handling**: Transient vs. permanent errors
- **Metrics**: Track latency and call counts
- **Streaming**: SSE handling for streaming responses

```csharp
public abstract class LlmProviderBase : ILlmProvider
{
    public async Task<ChatResponse> ChatAsync(ChatRequest request)
    {
        return await RetryAsync(
            () => ChatCoreAsync(request),
            maxRetries: Generation.MaxRetries,
            backoff: Generation.RetryBackoff
        );
    }
    
    protected abstract Task<ChatResponse> ChatCoreAsync(
        ChatRequest request);
}
```

### Provider Implementations

#### Copilot Provider (OAuth)

**File:** `BotNexus.Providers.Copilot/CopilotProvider.cs`

- **Auth**: GitHub OAuth device code flow
- **Endpoint**: `https://api.githubcopilot.com`
- **API Format**: OpenAI-compatible
- **Default Model**: Latest Copilot model

```
Device Code Flow:
1. Request device code from GitHub
2. Display code to user (e.g., "Enter code: ABCD-1234")
3. Poll GitHub while user authorizes
4. Receive access token on approval
5. Cache token locally (encrypted)
6. Use token for API calls
```

#### OpenAI Provider (API Key)

**File:** `BotNexus.Providers.OpenAI/OpenAiProvider.cs`

- **Auth**: API key authentication
- **Endpoint**: `https://api.openai.com/v1` (or custom via `apiBase`)
- **API Format**: Official OpenAI Chat Completions API
- **Default Model**: `gpt-4o`
- **Tooling**: Full support for tool calling

#### Anthropic Provider (API Key)

**File:** `BotNexus.Providers.Anthropic/AnthropicProvider.cs`

- **Auth**: API key authentication
- **Endpoint**: `https://api.anthropic.com`
- **API Format**: Anthropic Messages API (converted to/from ChatRequest)
- **Default Model**: `claude-3-5-sonnet-20241022`
- **Tooling**: Partial support (no streaming tool use)

### Provider Registry

**File:** `BotNexus.Providers.Base/ProviderRegistry.cs`

The provider registry maintains a map of available providers and resolves which provider should handle a request:

```csharp
public class ProviderRegistry
{
    public ILlmProvider Get(string providerName);
    public ILlmProvider GetDefault();
    public IEnumerable<string> GetProviderNames();
}
```

### Provider Resolution Strategy (from AgentLoop)

When processing a request, the agent selects a provider via this priority:

1. **Explicit Configuration**: `AgentConfig.Provider` name
2. **Model Prefix**: `"provider:model"` (e.g., `"anthropic:claude-3"`)
3. **Default Model Match**: Model name matches provider's `DefaultModel`
4. **Registry Default**: First registered provider
5. **Error**: No provider available

Example:

```csharp
// All of these resolve to OpenAI provider:
"openai:gpt-4o"      // Explicit prefix
"gpt-4o"             // Matches OpenAI's DefaultModel
```

---

## 9. Agent Workspace and Memory

Each agent has a persistent workspace for storing identity, personality, memory, and configuration. This enables agents to maintain state across deployments and builds system context from curated personality files and learned patterns.

### Workspace Overview

**Location**: `~/.botnexus/agents/{agentName}/`

An agent workspace contains:
- **Identity Files** (manually edited):
  - `SOUL.md` — Core personality, values, and boundaries
  - `IDENTITY.md` — Professional role, communication style, constraints
  - `USER.md` — User preferences and collaboration expectations
- **Auto-Generated Files** (regenerated each session):
  - `AGENTS.md` — List of configured agents and their roles
  - `TOOLS.md` — List of available tools
- **Memory Files**:
  - `MEMORY.md` — Long-term distilled learnings
  - `memory/daily/YYYY-MM-DD.md` — Daily timestamped notes (one per day)
- **Configuration**:
  - `HEARTBEAT.md` — Periodic tasks and consolidation cadence

### Context Builder

**File**: `AgentContextBuilder.cs`

The context builder assembles the full system prompt from workspace files at session start:

1. Auto-generated identity block (agent name, platform, workspace path, UTC time)
2. SOUL.md (if exists)
3. IDENTITY.md (if exists)
4. USER.md (if exists)
5. AGENTS.md (auto-generated)
6. TOOLS.md (auto-generated)
7. MEMORY.md (if exists)
8. Today's daily notes (if exists)
9. Yesterday's daily notes (if exists)

Each section is separated by `\n\n---\n\n` and truncated to `MaxContextFileChars` (default: 8000) per section.

**Key Methods**:
- `BuildSystemPromptAsync(agentName)` — Assembles full system prompt
- `BuildMessagesAsync(agentName, history, currentMessage, channel, chatId)` — Builds system prompt + trimmed history + runtime context

### Memory Store

**File**: `MemoryStore.cs`

Handles reading/writing memory files under the agent workspace:

- **Storage Path**: `~/.botnexus/agents/{agentName}/memory/`
- **Long-Term**: `MEMORY.md` (persistent learnings)
- **Daily**: `memory/daily/YYYY-MM-DD.md` (timestamped daily notes)
- **Format**: Markdown (.md), UTF-8 encoding
- **Backward Compatibility**: Falls back to legacy paths if new paths don't exist

### Memory Tools

Three tools enable agent memory interaction:

1. **memory_search(query, max_results=10)**
   - Keyword-based search across MEMORY.md and daily notes
   - Returns up to 10 results with context (2 lines before/after)
   - Ranks by recency (today first, then yesterday, then older, then long-term)

2. **memory_save(content, target="daily")**
   - Saves to daily notes (default): appends `[HH:mm] {content}` to today's file
   - Saves to long-term: appends `- {content}` to `MEMORY.md` under `## Notes`

3. **memory_get(file="memory", lines=null)**
   - Reads full file or line range
   - File targets: `"memory"` (MEMORY.md) or `"YYYY-MM-DD"` (daily notes)
   - Returns numbered output for easy reference

### Auto-Loading Strategy

At system prompt assembly:
- **Always included**: `MEMORY.md` (long-term memory)
- **Today's daily notes**: If exists (today's date in UTC)
- **Yesterday's daily notes**: If exists (yesterday's date in UTC)
- **Older notes**: Accessible via `memory_search` tool

This balances recent context with system prompt size.

### Workspace Initialization

First-run behavior (when workspace doesn't exist):

1. `AgentContextBuilder.BuildSystemPromptAsync()` calls `IAgentWorkspace.InitializeAsync()`
2. Creates directories: `~/.botnexus/agents/{agentName}/`, `memory/`, `memory/daily/`
3. Creates bootstrap files (if missing):
   - SOUL.md (with placeholder comment)
   - IDENTITY.md (with placeholder comment)
   - USER.md (with placeholder comment)
   - MEMORY.md (with placeholder comment)
   - HEARTBEAT.md (with placeholder comment)
4. Idempotent — safe to call multiple times
5. Human edits files between sessions — no restart required

### Configuration

Per-agent memory configuration:

```json
{
  "BotNexus": {
    "Agents": {
      "Named": {
        "agentName": {
          "EnableMemory": true,                         // Enable/disable memory system
          "MaxContextFileChars": 8000,                  // Max chars per file in system prompt
          "AutoLoadMemory": true,                       // Auto-load today+yesterday in system prompt
          "ConsolidationModel": "gpt-3.5-turbo",       // LLM for memory consolidation (optional)
          "MemoryConsolidationIntervalHours": 24       // Consolidation interval
        }
      }
    }
  }
}
```

### Memory Consolidation (Planned)

Phase 3 of the workspace/memory roadmap includes:

- **IMemoryConsolidator** interface for pluggable consolidation strategies
- **LLM-based consolidation**: Call a model to distill daily notes into long-term memory
- **Cron-based trigger**: Consolidation runs as a `maintenance` cron job (`consolidate-memory` action); see [Cron and Scheduling Guide](./cron-and-scheduling.md)
- **Configurable model**: `ConsolidationModel` can differ from agent's primary LLM

### Implementation

- **IAgentWorkspace**: `BotNexus.Core.Abstractions`
- **IContextBuilder**: `BotNexus.Core.Abstractions`
- **IMemoryStore**: `BotNexus.Core.Abstractions`
- **AgentWorkspace**: `BotNexus.Agent.AgentWorkspace.cs`
- **AgentContextBuilder**: `BotNexus.Agent.AgentContextBuilder.cs`
- **MemoryStore**: `BotNexus.Agent.MemoryStore.cs`
- **Memory Tools**: `BotNexus.Agent.Tools.Memory{Search,Save,Get}Tool.cs`
- **Config**: `BotNexus.Core.Configuration.AgentConfig.cs`

For detailed workspace and memory documentation, see [Agent Workspace and Memory Model](./workspace-and-memory.md).

---

## 10. Session Management

Conversations are persisted to disk in a structured format, enabling session recovery and history inspection.

### SessionManager

**File:** `BotNexus.Session/SessionManager.cs`

- **Storage**: File-backed JSONL (one file per session)
- **Location**: `~/.botnexus/workspace/sessions` (resolved from `AgentDefaults.Workspace`)
- **Thread-Safety**: Per-session `SemaphoreSlim` lock
- **Caching**: In-memory cache with weak references
- **Key Encoding**: URI escaping (`%` → `_`) to sanitize filesystem paths

### Session Model

**File:** `BotNexus.Core/Models/Session.cs`

```csharp
public class Session
{
    public string Key { get; set; }           // Unique identifier
    public string AgentName { get; set; }    // Which agent
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<SessionEntry> History { get; set; }
}

public class SessionEntry
{
    public MessageRole Role { get; set; }     // User, Assistant, Tool, System
    public string Content { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? ToolName { get; set; }     // If tool call
    public string? ToolCallId { get; set; }
}
```

### Session Key Format

Default format: `{Channel}:{ChatId}`

Example: `discord:12345` → Conversation between Discord user 12345 and the agent

Can be overridden via `InboundMessage.SessionKeyOverride` for custom session grouping.

### File Layout

```
sessions/
├── discord_12345.jsonl
├── slack_U123ABC.jsonl
├── telegram_999.jsonl
├── websocket_abc123_history.jsonl
└── custom_session_key.jsonl
```

### JSONL Format

Each line is a `SessionEntry` JSON object:

```jsonl
{"role":"System","content":"You are a helpful assistant","timestamp":"2026-04-01T10:00:00Z"}
{"role":"User","content":"What is 2+2?","timestamp":"2026-04-01T10:00:05Z"}
{"role":"Assistant","content":"2+2 equals 4","timestamp":"2026-04-01T10:00:06Z"}
{"role":"Tool","content":"{\"result\":true}","toolName":"Calculator","toolCallId":"call_123","timestamp":"2026-04-01T10:00:06Z"}
```

---

## 11. Cron and Scheduling

BotNexus provides a centralized **cron service** (`ICronService`) that schedules and executes jobs on a fixed tick interval, enabling automated agent prompts, system actions, and maintenance tasks.

### CronService Overview

**File:** `BotNexus.Cron/CronService.cs`

- **Lifecycle**: Hosted background service (starts with application)
- **Evaluation**: Ticks every N seconds (default 10) to check for due jobs
- **Execution**: Jobs run concurrently; scheduler does not block
- **Persistence**: In-memory execution history per job (configurable size, default 100 entries)
- **Correlation**: Every execution gets a unique correlation ID for tracing
- **Activity Events**: Publishes `cron.started`, `cron.completed`, `cron.failed` events

### Job Types

There are three job types, each with distinct execution models:

#### **Agent Jobs** (`type: "agent"`)

Execute a prompt through the agent runner pipeline.

- **Trigger**: Cron schedule
- **Execution**: Agent runner processes prompt, routes output to channels
- **Session Modes**: `new` (isolated), `persistent` (accumulated), or `named:<key>` (custom)
- **Output**: Agent response optionally routed to channels (Slack, Discord, email, etc.)

#### **System Jobs** (`type: "system"`)

Execute a built-in or custom system action (non-LLM).

- **Trigger**: Cron schedule
- **Execution**: System action registry resolves action by name and executes
- **Built-in Actions**: `check-updates`, `health-audit`, `extension-scan`
- **Output**: Action result optionally routed to channels

#### **Maintenance Jobs** (`type: "maintenance"`)

Execute internal housekeeping tasks.

- **Trigger**: Cron schedule
- **Built-in Actions**:
  - `consolidate-memory` — Consolidate agent memory files
  - `cleanup-sessions` — Delete old sessions (retention-based)
  - `rotate-logs` — Archive old log files
- **Configuration**: Per-action parameters (retention days, paths, agent lists)

### Central Registry

**File:** `BotNexus.Cron/CronJobFactory.cs`

All jobs are configured in a centralized section:

```json
{
  "BotNexus": {
    "Cron": {
      "Enabled": true,
      "TickIntervalSeconds": 10,
      "ExecutionHistorySize": 100,
      "Jobs": {
        "morning-briefing": { ... },
        "health-check": { ... },
        "memory-consolidation": { ... }
      }
    }
  }
}
```

The factory reads this configuration at startup and registers all enabled jobs with the cron service.

### Backwards Compatibility

Legacy `AgentConfig.CronJobs` entries are automatically migrated to the centralized `Cron.Jobs` section on startup. A deprecation warning is logged, but existing configs continue to work.

### Runtime Job Management

**Tool:** `CronTool`

Agents can schedule, remove, or list cron jobs dynamically:

```json
{
  "action": "schedule",
  "name": "dynamic-report",
  "agent": "analyst",
  "prompt": "Generate a real-time report",
  "schedule": "*/30 * * * *",
  "session": "persistent"
}
```

### See Also

For detailed configuration, examples, and troubleshooting, see [Cron and Scheduling Guide](./cron-and-scheduling.md).

---

## 12. Security Model

### Authentication

#### API Key Authentication (Channels & REST)

- **Mechanism**: `Authorization: Bearer {api_key}` header
- **Validation**: Middleware intercepts requests to `/api/*` and `/ws`
- **Configuration**: `GatewayConfig.ApiKey`
- **Scope**: Global—single key for entire Gateway

#### OAuth Authentication (Providers)

- **Mechanism**: GitHub device code flow (for Copilot)
- **Flow**:
  1. Request device code
  2. User authorizes on GitHub
  3. Exchange device code for access token
  4. Cache token locally (encrypted)
- **Scope**: Per-provider (each provider can use OAuth independently)

### Authorization

Currently not implemented. Future consideration:

- RBAC (role-based access control)
- ABAC (attribute-based access control)
- Per-channel permissions
- Per-agent permissions

### Extension Security

- **Signature Validation**: Optional requirement that extensions be signed
- **Sandboxing**: AssemblyLoadContext isolation reduces attack surface
- **Validation**: Pre-load dry-run to catch errors before live deployment
- **Whitelist**: Allowed shared assemblies list prevents dependency conflicts

### Data Security

- **Session Files**: Stored on disk in plaintext (configure file permissions)
- **OAuth Tokens**: Encrypted at rest (file-based store)
- **Logs**: Configurable log levels (prevent credential leakage)
- **Connections**: Configure TLS for WebSocket/REST via Kestrel

### API Key Security

- **Never log full keys**: Log last 4 characters only
- **Rotate regularly**: Manual process (update `~/.botnexus/config.json` + restart)
- **Secrets management**: Use OS environment variables or secrets manager

---

## 13. Observability

BotNexus provides multiple observability mechanisms to monitor health and behavior.

### Health Checks

**Endpoint**: `GET /health` (liveness), `GET /ready` (readiness)

| Check | Type | Purpose |
|-------|------|---------|
| `message_bus` | Liveness | IMessageBus alive |
| `provider_registration` | Liveness | At least one provider registered |
| `extension_loader` | Liveness | Extension load report available |
| `channel_readiness` | Readiness | All configured channels running |
| `provider_readiness` | Readiness | At least one provider ready |

### Metrics

**File:** `BotNexus.Core/Metrics/BotNexusMetrics.cs`

- **Provider Latency**: Per-provider response time histogram
- **Tool Execution**: Per-tool duration and success/failure rates
- **Message Throughput**: Messages per channel, per second
- **Session Counts**: Total sessions, active sessions
- **Error Rates**: Exceptions by type

### Correlation IDs

Every message receives a unique correlation ID at channel ingress:

- Propagated through Gateway, AgentRouter, AgentRunner, AgentLoop
- Attached to all logs and metrics
- Returned in responses for end-to-end tracing

### Activity Stream

**File:** `BotNexus.Core/Bus/ActivityStream.cs`

System-wide event broadcast for real-time WebUI monitoring:

```csharp
public class ActivityEvent
{
    public ActivityEventType Type { get; set; }
    public string Channel { get; set; }
    public string SessionId { get; set; }
    public string ChatId { get; set; }
    public string SenderId { get; set; }
    public string Content { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

public enum ActivityEventType
{
    MessageReceived,
    MessageSent,
    ToolExecuted,
    Error,
    SessionCreated,
    SessionReset
}
```

### Logging

- **Levels**: Debug, Information, Warning, Error, Critical
- **Scopes**: Correlation ID, Channel, SessionId
- **Targets**: Console (development), Event Log (production), Serilog (optional)

---

## 14. Installation Layout

### Runtime Directory Structure

BotNexus installs under `~/.botnexus/` (user home directory):

```
~/.botnexus/
├── config.json                  # User configuration (overrides appsettings.json defaults)
├── extensions/                  # Dynamic extensions
│   ├── providers/
│   │   ├── copilot/
│   │   ├── openai/
│   │   └── anthropic/
│   ├── channels/
│   │   ├── discord/
│   │   ├── slack/
│   │   └── telegram/
│   └── tools/
│       └── github/
├── tokens/                      # OAuth token storage (encrypted)
│   └── copilot.json
├── workspace/                   # Agent workspace and session data
│   └── sessions/
│       ├── discord_12345.jsonl
│       ├── slack_U123ABC.jsonl
│       └── ...
├── sessions/                    # (Legacy) top-level session data
└── logs/                        # Log files
```

### Configuration Resolution

Startup loads configuration in this order (later sources override earlier ones):

1. `appsettings.json` (project defaults in `src/BotNexus.Gateway/`)
2. `appsettings.{ASPNETCORE_ENV}.json` (environment-specific overrides)
3. `~/.botnexus/config.json` (user configuration — primary config location)
4. Environment variables (highest priority)

### First-Run Setup

On first run:

1. Create `~/.botnexus/` directory structure
2. Generate default `config.json` with Copilot provider preset
3. Scan `extensions/` folder
4. Initialize extension registry
5. Set up session storage
6. Create OAuth token store (if needed)

---

## 15. Component Reference

### Class Hierarchy

```
BotNexus.Core/
├── Abstractions/          (13 interfaces)
├── Bus/
│   ├── MessageBus.cs      (IMessageBus)
│   └── ActivityStream.cs  (IActivityStream)
├── Configuration/
│   ├── BotNexusConfig.cs
│   ├── AgentConfig.cs
│   └── ...
├── Models/
│   ├── InboundMessage.cs
│   ├── OutboundMessage.cs
│   ├── Session.cs
│   ├── ChatRequest.cs
│   ├── ChatResponse.cs
│   └── ...
├── Extensions/
│   ├── ServiceCollectionExtensions.cs
│   └── ExtensionLoaderExtensions.cs
└── Metrics/
    └── BotNexusMetrics.cs

BotNexus.Gateway/
├── Program.cs
├── BotNexusServiceExtensions.cs
├── Gateway.cs             (Main orchestrator)
├── AgentRouter.cs
├── ChannelManager.cs
├── WebSocketChannel.cs
├── GatewayWebSocketHandler.cs
└── Health/
    ├── MessageBusHealthCheck.cs
    └── ...

BotNexus.Agent/
├── AgentLoop.cs           (Core processing)
├── AgentRunner.cs
├── ContextBuilder.cs
├── Tools/
│   ├── ToolBase.cs
│   ├── ToolRegistry.cs
│   ├── FilesystemTool.cs
│   ├── ShellTool.cs
│   └── ...
└── Mcp/
    ├── McpTool.cs
    └── IMcpClient.cs

BotNexus.Channels.Base/
├── BaseChannel.cs
├── ChannelManager.cs
└── Models/
    └── ...

BotNexus.Channels.{Discord,Slack,Telegram}/
├── {Provider}Channel.cs

BotNexus.Providers.Base/
├── LlmProviderBase.cs
├── ProviderRegistry.cs
└── Models/
    └── ...

BotNexus.Providers.{Copilot,OpenAI,Anthropic}/
├── {Provider}Provider.cs
└── ...

BotNexus.Session/
└── SessionManager.cs
```

### Key File Locations

| Purpose | File |
|---------|------|
| Solution root | `BotNexus.slnx` |
| Gateway entry point | `src/BotNexus.Gateway/Program.cs` |
| Core contracts | `src/BotNexus.Core/Abstractions/` |
| DI setup | `src/BotNexus.Gateway/BotNexusServiceExtensions.cs` |
| Message processing | `src/BotNexus.Gateway/Gateway.cs` |
| Agent execution | `src/BotNexus.Agent/AgentLoop.cs` |
| Session storage | `src/BotNexus.Session/SessionManager.cs` |
| Extension loading | `src/BotNexus.Core/Extensions/ExtensionLoaderExtensions.cs` |
| WebUI | `src/BotNexus.WebUI/wwwroot/` |
| Tests | `tests/` |

---

## Summary

BotNexus is a modular, contract-first platform for running multiple AI agents concurrently. It emphasizes:

- **Extensibility**: Dynamic assembly loading with folder-based organization
- **Security**: Extension validation, OAuth support, configuration-driven loading
- **Observability**: Correlation IDs, health checks, activity stream
- **Resilience**: Retry logic, error handling, session persistence
- **Maintainability**: Clean separation of concerns, minimal core, SOLID principles

The architecture supports deploying to `~/.botnexus/` with configurable extensions, persistent sessions, and OAuth-backed providers.
