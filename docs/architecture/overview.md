# BotNexus Architecture Overview

**Version:** 1.1  
**Last Updated:** 2026-05-01  
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
9. [Response Normalization & Tool Calling](#response-normalization--tool-calling)
10. [Agent Loop & Loop Detection](#agent-loop--loop-detection)
11. [Agent Workspace and Memory](#agent-workspace-and-memory)
12. [Session Management](#session-management)
13. [Cron and Scheduling](#cron-and-scheduling)
14. [Diagnostics (Doctor)](#diagnostics-doctor)
15. [Configuration Hot Reload](#configuration-hot-reload)
16. [Security Model](#security-model)
17. [Observability](#observability)
18. [Installation Layout](#installation-layout)
19. [Component Reference](#component-reference)

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
- **Contract-First**: Core module defines 14 interfaces; implementations in outer modules
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
│ - ConfigReloadOrchestrator (live config reload)              │
│ - CheckupRunner, IHealthCheckup (diagnostics)               │
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
   │    f. If tool calls: validate args (ToolCallValidator),│
   │       then execute via ToolRegistry                   │
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
| `ConfigReloadOrchestrator` | Singleton, BackgroundService | Live config reload on file change |
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
    
    // Diagnostics (13 health checkups, CheckupRunner)
    services.AddBotNexusDiagnostics();
    
    // Config hot reload (watches config.json, reloads agents/providers/cron)
    services.AddHostedService<ConfigReloadOrchestrator>();
    
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

The Core module defines 14 interfaces that form the contract between the engine and all extensions:

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
| `IHealthCheckup` | `Abstractions/IHealthCheckup.cs` | Diagnostic health check with optional auto-fix |

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

## 8. Provider Architecture (Pi-Style)

BotNexus uses a **model-aware, handler-per-API-format** architecture inspired by Pi's type system. Each model is explicitly defined with its API requirements, and requests route to handlers based on API format, not provider name.

### Core Concepts

**ModelDefinition** — Explicit model metadata:
- **Id**: Model identifier (e.g., `"claude-opus-4.6"`, `"gpt-4o"`, `"gpt-5"`)
- **Name**: Human-readable name (e.g., `"Claude Opus 4.6"`)
- **Api**: API format handler type (e.g., `"anthropic-messages"`, `"openai-completions"`, `"openai-responses"`)
- **Provider**: Provider name (e.g., `"github-copilot"`, `"openai"`, `"anthropic"`)
- **BaseUrl**: API endpoint (e.g., `"https://api.individual.githubcopilot.com"`)
- **Headers**: Provider-specific HTTP headers (User-Agent, Editor-Version, etc.)
- **Reasoning**: Whether the model supports extended thinking/reasoning modes
- **Input**: Supported input types (`["text"]`, `["text", "image"]`)
- **ContextWindow**: Maximum context window in tokens
- **MaxTokens**: Maximum generated tokens

**IApiFormatHandler** — Protocol abstraction:
- Each handler implements the same interface but speaks a different API dialect
- Routes determined by model definition, not provider name
- Handlers share no implementation — each speaks its native API

### Model → API Format Mapping

| Model Family | API Format | Handler | Example Models |
|---|---|---|---|
| **Claude** | `anthropic-messages` | `AnthropicMessagesHandler` | claude-opus-4.6, claude-sonnet-4.6 |
| **GPT-4, GPT-4o, o1, o3, Gemini** | `openai-completions` | `OpenAiCompletionsHandler` | gpt-4o, gpt-4o-mini, o1, o3, gemini-3-pro-preview |
| **GPT-5** | `openai-responses` | `OpenAiResponsesHandler` | gpt-5, gpt-5.2, gpt-5.4 |

### CopilotModels Registry

**File:** `BotNexus.Providers.Base/CopilotModels.cs`

All GitHub Copilot models are pre-registered with their API format and capability metadata:

```csharp
public static class CopilotModels
{
    // Claude models (anthropic-messages)
    public static ModelDefinition Claude_Opus_4_6 = new(
        Id: "claude-opus-4.6",
        Name: "Claude Opus 4.6",
        Api: "anthropic-messages",
        Provider: "github-copilot",
        BaseUrl: "https://api.individual.githubcopilot.com",
        Headers: { /* Copilot headers */ },
        Reasoning: true,
        Input: ["text", "image"],
        ContextWindow: 1000000,
        MaxTokens: 64000
    );
    
    // GPT-4o models (openai-completions)
    public static ModelDefinition Gpt_4o = new(
        Id: "gpt-4o",
        Api: "openai-completions",
        ...
    );
    
    // GPT-5 models (openai-responses)
    public static ModelDefinition Gpt_5_4 = new(
        Id: "gpt-5.4",
        Api: "openai-responses",
        ...
    );
    
    public static IReadOnlyList<ModelDefinition> All { get; }
    public static ModelDefinition Resolve(string modelId);
    public static bool TryResolve(string modelId, out ModelDefinition? model);
}
```

### API Format Handler Interface

**File:** `BotNexus.Providers.Base/IApiFormatHandler.cs`

```csharp
public interface IApiFormatHandler
{
    string ApiFormat { get; }
    
    Task<LlmResponse> ChatAsync(
        ModelDefinition model, 
        ChatRequest request, 
        string apiKey, 
        CancellationToken cancellationToken);
    
    IAsyncEnumerable<StreamingChatChunk> ChatStreamAsync(
        ModelDefinition model, 
        ChatRequest request, 
        string apiKey, 
        CancellationToken cancellationToken);
}
```

### Handler Implementations

**AnthropicMessagesHandler** — Speaks Anthropic Messages API  
**OpenAiCompletionsHandler** — Speaks OpenAI Chat Completions API  
**OpenAiResponsesHandler** — Speaks OpenAI Responses API  

Each handler:
- Accepts normalized `ChatRequest` and `ModelDefinition`
- Translates to native API format
- Returns normalized `LlmResponse`

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

### Copilot Provider: Model-Aware Routing

**File:** `BotNexus.Providers.Copilot/CopilotProvider.cs`

The Copilot provider implements request routing based on model definition:

```csharp
public sealed class CopilotProvider : LlmProviderBase
{
    private readonly Dictionary<string, IApiFormatHandler> _handlers = new()
    {
        ["anthropic-messages"] = new AnthropicMessagesHandler(...),
        ["openai-completions"] = new OpenAiCompletionsHandler(...),
        ["openai-responses"] = new OpenAiResponsesHandler(...)
    };
    
    protected override async Task<LlmResponse> ChatCoreAsync(
        ChatRequest request, 
        CancellationToken cancellationToken)
    {
        // 1. Resolve model from request
        var model = CopilotModels.Resolve(request.Settings.Model);
        
        // 2. Get handler by API format
        var handler = GetHandler(model.Api);
        
        // 3. Get Copilot access token (via GitHub OAuth exchange)
        var apiKey = await GetCopilotAccessTokenAsync(cancellationToken);
        
        // 4. Route to handler
        return await handler.ChatAsync(model, request, apiKey, cancellationToken);
    }
}
```

### Copilot Headers

The Copilot API expects provider-specific headers for requests:

```json
{
    "User-Agent": "GitHubCopilotChat/0.35.0",
    "Editor-Version": "vscode/1.107.0",
    "Editor-Plugin-Version": "copilot-chat/0.35.0",
    "Copilot-Integration-Id": "vscode-chat"
}
```

These headers are embedded in each `ModelDefinition` and applied by handlers. They identify the client to Copilot and enable proper routing and rate limiting.

### LlmProviderBase (Abstract Base)

**File:** `BotNexus.Providers.Base/LlmProviderBase.cs`

Provides common infrastructure:

- **Retry Logic**: Exponential backoff (configurable)
- **Error Handling**: Transient vs. permanent errors
- **Metrics**: Track latency and call counts
- **Streaming**: SSE handling for streaming responses

### How to Add a New Model or Handler

#### 1. Add Model to CopilotModels

```csharp
// In CopilotModels.cs, add to the All list:
new ModelDefinition(
    Id: "new-model-id",
    Name: "New Model",
    Api: "api-format-name",  // Use existing or create new
    Provider: "github-copilot",
    BaseUrl: "https://api.individual.githubcopilot.com",
    Headers: CopilotHeaders,
    Reasoning: false,
    Input: ["text"],
    ContextWindow: 200000,
    MaxTokens: 100000
)
```

#### 2. If Adding a New API Format: Implement IApiFormatHandler

```csharp
public class NewApiFormatHandler : IApiFormatHandler
{
    public string ApiFormat => "new-api-format";
    
    public async Task<LlmResponse> ChatAsync(
        ModelDefinition model, 
        ChatRequest request, 
        string apiKey, 
        CancellationToken cancellationToken)
    {
        // Translate ChatRequest to native format
        // Call API
        // Translate response to LlmResponse
        return new LlmResponse(...);
    }
    
    public async IAsyncEnumerable<StreamingChatChunk> ChatStreamAsync(...)
    {
        // Stream implementation
    }
}
```

#### 3. Register Handler in CopilotProvider

```csharp
// In CopilotProvider constructor:
_handlers = new()
{
    ["anthropic-messages"] = new AnthropicMessagesHandler(...),
    ["openai-completions"] = new OpenAiCompletionsHandler(...),
    ["openai-responses"] = new OpenAiResponsesHandler(...),
    ["new-api-format"] = new NewApiFormatHandler(...)  // Add here
};
```

### Provider Registry

**File:** `BotNexus.Providers.Base/ProviderRegistry.cs`

The provider registry maintains a map of available providers:

```csharp
public class ProviderRegistry
{
    public ILlmProvider Get(string providerName);
    public ILlmProvider GetDefault();
    public IEnumerable<string> GetProviderNames();
}
```

---

## 9. Response Normalization & Tool Calling

Each LLM provider has a unique response format (OpenAI JSON, Anthropic blocks, GitHub Copilot proxy). The normalization layer ensures all responses convert to a canonical `LlmResponse` type that the agent loop consumes uniformly.

### Canonical Response Type: LlmResponse

**File:** `BotNexus.Core/Models/LlmResponse.cs`

```csharp
public record LlmResponse(
    string Content,                              // Text response
    FinishReason FinishReason,                  // Stop, ToolCalls, Length, ContentFilter, Other
    IReadOnlyList<ToolCallRequest>? ToolCalls = null,  // Parsed tool calls
    int? InputTokens = null,                    // Input token count
    int? OutputTokens = null);                  // Output token count

public enum FinishReason 
{ 
    Stop,           // Normal completion (model returned text, no tool calls)
    ToolCalls,      // Model requested tool execution
    Length,         // Hit max token limit
    ContentFilter,  // Blocked by safety policy
    Other           // Unknown/unexpected reason
}

public record ToolCallRequest(
    string Id,                                  // Unique ID from provider
    string ToolName,                            // Tool identifier
    IReadOnlyDictionary<string, object?> Arguments);  // Normalized arguments
```

### Copilot Provider: GitHub Responses API

**File:** `BotNexus.Providers.Copilot/CopilotProvider.cs`

GitHub Copilot uses event-driven SSE (Server-Sent Events) streaming via the GitHub Responses API.

#### Streaming Implementation

**Endpoint:** `POST https://api.githubcopilot.com/chat/completions`  
**Content-Type:** `application/json` (request), `text/event-stream` (response)

```csharp
public override async IAsyncEnumerable<string> ChatStreamAsync(
    ChatRequest request,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    var payload = BuildRequestPayload(request, stream: true);
    using var httpRequest = await CreateChatRequestAsync(payload, cancellationToken);
    using var response = await _httpClient
        .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    using var reader = new StreamReader(stream);

    while (!cancellationToken.IsCancellationRequested)
    {
        var line = await reader.ReadLineAsync(cancellationToken);
        if (line is null || !line.StartsWith("data: ", StringComparison.Ordinal))
            continue;

        var data = line["data: ".Length..];
        if (data == "[DONE]")
            yield break;

        // Parse SSE JSON event
        var jsonEvent = JsonDocument.Parse(data);
        if (jsonEvent.RootElement.TryGetProperty("choices", out var choices) &&
            choices.GetArrayLength() > 0)
        {
            var delta = choices[0].TryGetProperty("delta", out var d) ? d : default;
            if (delta.TryGetProperty("content", out var content) &&
                content.GetString() is { Length: > 0 } text)
            {
                yield return text;
            }
        }
    }
}
```

**Event Format:**
```
data: {"choices":[{"delta":{"content":"Hello"}}]}
data: {"choices":[{"delta":{"content":" world"}}]}
data: [DONE]
```

#### Non-Streaming Response Parsing

```csharp
public override async Task<LlmResponse> ChatAsync(ChatRequest request)
{
    var response = await ChatCoreAsync(request);
    
    // Merge multiple choices (Copilot proxy preserves backend-specific responses)
    string content = string.Empty;
    JsonElement? toolCallsElement = null;
    string? finishReason = null;
    
    foreach (var choice in response.Choices)
    {
        if (choice.Message.Content is { Length: > 0 } text && string.IsNullOrEmpty(content))
            content = text;
        
        if (toolCallsElement is null && choice.Message.ToolCalls?.Any() == true)
            toolCallsElement = choice.Message.ToolCalls;
        
        if (finishReason is null)
            finishReason = choice.FinishReason;
    }
    
    var toolCalls = toolCallsElement.HasValue
        ? ParseToolCalls(toolCallsElement.Value)
        : null;
    
    return new LlmResponse(
        Content: content,
        FinishReason: MapFinishReason(finishReason),
        ToolCalls: toolCalls,
        InputTokens: response.Usage?.PromptTokens,
        OutputTokens: response.Usage?.CompletionTokens);
}
```

### Dual Tool Call Argument Format Handling

**Critical Feature:** Copilot proxies requests to both OpenAI and Claude backends. Each returns tool call arguments in a different format, and both must normalize to `Dictionary<string, object?>`.

**File:** `BotNexus.Providers.Copilot/CopilotProvider.cs` (Lines 556-632)

```csharp
private IReadOnlyList<ToolCallRequest> ParseToolCalls(JsonElement toolCallsElement)
{
    var result = new List<ToolCallRequest>();

    foreach (var toolCall in toolCallsElement.EnumerateArray())
    {
        var id = toolCall.TryGetProperty("id", out var idEl) ? idEl.GetString() : "";
        var name = toolCall.TryGetProperty("function", out var funcEl) &&
                   funcEl.TryGetProperty("name", out var nameEl) 
                   ? nameEl.GetString() : "";

        // CRITICAL: Handle dual argument formats
        Dictionary<string, object?> arguments = new();
        if (toolCall.TryGetProperty("function", out var funcElement) &&
            funcElement.TryGetProperty("arguments", out var argsElement))
        {
            if (argsElement.ValueKind == JsonValueKind.String)
            {
                // OpenAI format: arguments is a JSON string
                var json = argsElement.GetString();
                arguments = string.IsNullOrEmpty(json)
                    ? new()
                    : JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? [];
            }
            else if (argsElement.ValueKind == JsonValueKind.Object)
            {
                // Claude format: arguments is already a JSON object
                arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                    argsElement.GetRawText()) ?? [];
            }
        }

        result.Add(new ToolCallRequest(id, name, arguments));
    }
    return result;
}
```

**Why Both Formats?**
- **OpenAI**: Returns `"arguments": "{\"param\":\"value\"}"` (JSON string)
- **Claude**: Returns `"arguments": {"param":"value"}` (JSON object)
- **Copilot Proxy**: Passes through backend response as-is, so both formats appear

### Provider Normalization: OpenAI vs Anthropic

#### OpenAI Provider

```csharp
// Using OpenAI SDK (Anthropic.Sdk)
var result = await _client.ChatAsync(new ChatCompletionCreateParams
{
    Model = request.Settings.Model,
    Messages = ToOpenAiMessages(request.Messages),
    Tools = ToOpenAiTools(request.Tools),
    Stream = false
});

// Normalize to LlmResponse
var content = result.Content.FirstOrDefault()?.Text ?? string.Empty;
var toolCalls = result.ToolCalls?.Select(tc => new ToolCallRequest(
    tc.Id,
    tc.Function?.Name ?? string.Empty,
    JsonSerializer.Deserialize<Dictionary<string, object?>>(tc.Function?.Arguments ?? "{}")
)).ToList();

return new LlmResponse(
    content,
    MapFinishReason(result.FinishReason),
    toolCalls,
    result.Usage?.InputTokenCount,
    result.Usage?.OutputTokenCount);
```

#### Anthropic Provider

```csharp
// Anthropic uses content blocks instead of single content field
var textBlock = response.Content
    .OfType<TextBlock>()
    .FirstOrDefault();

var toolUseBlocks = response.Content
    .OfType<ToolUseBlock>()
    .Select(block => new ToolCallRequest(
        block.Id,
        block.Name,
        JsonSerializer.Deserialize<Dictionary<string, object?>>(block.Input.ToString()) ?? []
    )).ToList();

return new LlmResponse(
    textBlock?.Text ?? string.Empty,
    MapFinishReason(response.StopReason),
    toolUseBlocks.Count > 0 ? toolUseBlocks : null,
    response.Usage?.InputTokens,
    response.Usage?.OutputTokens);
```

### Finish Reason Mapping

Each provider uses different string values for finish reasons. All normalize to the canonical `FinishReason` enum:

```csharp
private static FinishReason MapFinishReason(string? reason) => reason switch
{
    "stop" => FinishReason.Stop,                           // OpenAI, Copilot
    "end_turn" => FinishReason.Stop,                       // Anthropic
    "tool_calls" => FinishReason.ToolCalls,               // OpenAI, Copilot
    "tool_use" => FinishReason.ToolCalls,                 // Anthropic
    "length" => FinishReason.Length,
    "max_tokens" => FinishReason.Length,                  // Anthropic variant
    "content_filter" => FinishReason.ContentFilter,
    _ => FinishReason.Other
};
```

---

## 10. Agent Loop & Loop Detection

The agent loop orchestrates the multi-turn conversation with an LLM, executing tool calls and managing iteration limits. It includes built-in loop detection to prevent agents from getting stuck in infinite retry patterns.

### Agent Loop Overview

**File:** `BotNexus.Agent/AgentLoop.cs`

The loop runs until one of these conditions is met:
1. LLM returns `FinishReason == Stop` and content (normal completion)
2. LLM returns no tool calls and no content (agent ready to finalize)
3. Iteration limit reached (`MaxToolIterations`)
4. Cancellation requested

```csharp
public async Task<LlmResponse> RunAsync(InboundMessage message)
{
    var session = await _sessionManager.GetSessionAsync(sessionKey);
    var agentConfig = _agentRegistry.Get(agentName);
    
    int iteration = 0;
    while (iteration < _maxToolIterations)
    {
        // 1. Build context and messages
        var systemPrompt = await _contextBuilder.BuildSystemPromptAsync(agentName);
        var chatRequest = new ChatRequest(
            Messages: [new(MessageRole.System, systemPrompt), ...session.History],
            Settings: agentConfig.Generation,
            Tools: _toolRegistry.GetAvailableTools(agentName),
            SystemPrompt: systemPrompt);
        
        // 2. Call LLM provider
        var llmResponse = await provider.ChatAsync(chatRequest);
        session.AddEntry(new SessionEntry(MessageRole.Assistant, llmResponse.Content, ...));
        
        // 3. Check finish reason
        if (llmResponse.FinishReason != FinishReason.ToolCalls)
        {
            return llmResponse;  // Completion
        }
        
        // 4. Execute tool calls (with loop detection)
        foreach (var toolCall in llmResponse.ToolCalls)
        {
            var signature = ComputeToolCallSignature(toolCall);
            var callCount = _toolCallSignatures.GetValueOrDefault(signature, 0);
            
            if (callCount >= MaxRepeatedToolCalls)
            {
                // Loop detected: block execution
                session.AddEntry(new SessionEntry(
                    MessageRole.Tool,
                    $"Error: Loop detected. Tool '{toolCall.ToolName}' called {callCount + 1} times with identical arguments.",
                    ToolName: toolCall.ToolName,
                    ToolCallId: toolCall.Id));
                continue;
            }
            
            _toolCallSignatures[signature] = callCount + 1;
            var result = await _toolRegistry.ExecuteAsync(toolCall);
            session.AddEntry(new SessionEntry(MessageRole.Tool, result, ...));
        }
        
        iteration++;
    }
    
    return new LlmResponse("Max iterations reached", FinishReason.Stop);
}
```

### Loop Detection: Signature-Based Tracking

**Purpose:** Prevent agents from repeatedly calling the same tool with identical arguments.

**How It Works:**

1. **Compute Signature:** Hash tool name + normalized arguments
   ```csharp
   private string ComputeToolCallSignature(ToolCallRequest toolCall)
   {
       var argsJson = JsonSerializer.Serialize(
           toolCall.Arguments,
           new JsonSerializerOptions { WriteIndented = false });
       return $"{toolCall.ToolName}::{argsJson}";
   }
   ```

2. **Track Per-Session:** `Dictionary<string, int>` maps signature → call count
   ```csharp
   private readonly Dictionary<string, int> _toolCallSignatures = new();
   ```

3. **Block at Threshold:** When `callCount >= MaxRepeatedToolCalls`
   ```csharp
   if (callCount >= _settings.MaxRepeatedToolCalls)
   {
       // Return error to LLM instead of executing
       var errorMsg = $"Error: Loop detected. Tool '{toolCall.ToolName}' called {callCount + 1} times...";
       session.AddEntry(new(MessageRole.Tool, errorMsg, ...));
       continue;  // Skip execution
   }
   ```

### Configuration: Iteration & Repetition Limits

**File:** `BotNexus.Core/Models/GenerationSettings.cs`

```csharp
public class GenerationSettings
{
    public int MaxToolIterations { get; set; } = 40;           // Total loop iterations
    public int MaxRepeatedToolCalls { get; set; } = 2;         // Max identical calls
}
```

| Setting | Default | Purpose |
|---------|---------|---------|
| `MaxToolIterations` | 40 | Max number of loop iterations (LLM calls + tool execution cycles) |
| `MaxRepeatedToolCalls` | 2 | Max times the same tool can be called with identical arguments |

### Per-Agent Configuration Override

**File:** `BotNexus.Core/Configuration/AgentConfig.cs`

```json
{
  "Agents": {
    "Named": {
      "careful-agent": {
        "Model": "gpt-4o",
        "MaxToolIterations": 20,        // Limit this agent to 20 iterations
        "MaxRepeatedToolCalls": 1       // No repeated calls allowed
      },
      "explorative-agent": {
        "Model": "gpt-4o",
        "MaxToolIterations": 100,       // Allow more exploration
        "MaxRepeatedToolCalls": 5       // Allow up to 5 retries
      }
    }
  }
}
```

When not overridden, agents inherit defaults from `Agents` section (see [Configuration](#configuration-sections)).

### Iteration Flow Example

```
Iteration 0:
  ├─ Build ChatRequest (system prompt + history + tools)
  ├─ Call LLM → LlmResponse(content="Found 2 files", toolCalls=[list_files])
  ├─ Add to session: Assistant message
  ├─ Execute tool: list_files (signature: "list_files::{}")
  │  └─ Increment counter: {"list_files::{}" → 1}
  ├─ Add tool result to session
  └─ Continue loop

Iteration 1:
  ├─ Build ChatRequest (includes file list in history)
  ├─ Call LLM → LlmResponse(content="", toolCalls=[list_files])  ← SAME TOOL AGAIN
  ├─ Check signature: "list_files::{}" → counter = 1
  ├─ Check if 1 >= MaxRepeatedToolCalls (2) → false
  ├─ Increment counter: {"list_files::{}" → 2}
  ├─ Execute tool again
  └─ Continue loop

Iteration 2:
  ├─ Build ChatRequest
  ├─ Call LLM → LlmResponse(toolCalls=[list_files])  ← SAME TOOL YET AGAIN
  ├─ Check signature: "list_files::{}" → counter = 2
  ├─ Check if 2 >= MaxRepeatedToolCalls (2) → TRUE ✗
  ├─ Block execution, add error message to session
  ├─ Error: "Tool 'list_files' called 3 times with identical arguments"
  ├─ Next LLM call receives this error
  └─ Continue loop
```

### Best Practices

**Setting Iteration Limits:**
- **Default (40)**: Suitable for most multi-step workflows (file operations, analysis chains)
- **Conservative (10-20)**: For safety-critical agents or fast feedback loops
- **Permissive (50+)**: For complex research or planning tasks

**Repeated Call Limits:**
- **Default (2)**: Allow retry, block infinite loops
- **Strict (1)**: No retries; useful for read-only operations
- **Relaxed (3-5)**: For exploratory agents that may need multiple attempts

---

## 11. Agent Workspace and Memory

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

## 12. Session Management

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

## 13. Cron and Scheduling

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

## 14. Diagnostics (Doctor)

BotNexus includes a diagnostics system with 13 health checkups organized across 6 categories. These are used by the CLI `doctor` command and the `/api/doctor` Gateway endpoint.

### IHealthCheckup Interface

**File:** `BotNexus.Core/Abstractions/IHealthCheckup.cs`

```csharp
public interface IHealthCheckup
{
    string Name { get; }
    string Category { get; }
    string Description { get; }
    bool CanAutoFix => false;
    Task<CheckupResult> RunAsync(CancellationToken ct = default);
    Task<CheckupResult> FixAsync(CancellationToken ct = default) => RunAsync(ct);
}
```

- **CanAutoFix**: Indicates if the checkup can fix the issue automatically
- **FixAsync**: Attempts auto-repair (defaults to re-running the check)
- **CheckupResult**: Record of `(CheckupStatus Status, string Message, string? Advice)`
- **CheckupStatus**: `Pass`, `Warn`, `Fail`

### Checkup Categories

| Category | Checkups | Description |
|----------|----------|-------------|
| **Configuration** | ConfigValidCheckup, AgentConfigCheckup, ProviderConfigCheckup | Config file validity and required fields |
| **Security** | ApiKeyStrengthCheckup, TokenPermissionsCheckup, ExtensionSignedCheckup | Credential strength, file permissions, assembly signing |
| **Connectivity** | ProviderReachableCheckup, PortAvailableCheckup | API reachability and port availability |
| **Extensions** | ExtensionsFolderExistsCheckup, ExtensionAssembliesValidCheckup | Extension directory and DLL validity |
| **Permissions** | HomeDirWritableCheckup, LogDirWritableCheckup | Directory write access |
| **Resources** | DiskSpaceCheckup | Available disk space (warn at 500 MB, fail at 100 MB) |

### Auto-Fix Checkups

Five checkups support automatic repair (`CanAutoFix = true`):

| Checkup | What It Fixes |
|---------|---------------|
| `ConfigValidCheckup` | Creates default `config.json` if missing |
| `TokenPermissionsCheckup` | Fixes token directory permissions (platform-specific ACL/chmod) |
| `ExtensionsFolderExistsCheckup` | Creates missing extension folders (providers, channels, tools) |
| `HomeDirWritableCheckup` | Creates `~/.botnexus/` directory if missing |
| `LogDirWritableCheckup` | Creates `~/.botnexus/logs/` directory if missing |

### CheckupRunner

**File:** `BotNexus.Diagnostics/CheckupRunner.cs`

Orchestrates checkup execution:
- `RunAllAsync(category?)` — Run all (or filtered) checkups, return results
- `RunAndFixAsync(category?, force, promptUser?)` — Run checkups and auto-fix failures
- `GetCategories()` — List available categories

### Access Points

| Surface | Usage |
|---------|-------|
| CLI | `botnexus doctor [--category <name>]` |
| Gateway API | `GET /api/doctor[?category=<name>]` |
| Cron | Built-in `health-audit` system action |

---

## 15. Configuration Hot Reload

BotNexus applies most configuration changes live when `config.json` is saved — no Gateway restart required.

### ConfigReloadOrchestrator

**File:** `BotNexus.Gateway/ConfigReloadOrchestrator.cs`

A `BackgroundService` that monitors config changes via `IOptionsMonitor<BotNexusConfig>`:

1. **Detect** — `IOptionsMonitor.OnChange` fires when `config.json` is modified
2. **Debounce** — Waits 500 ms for rapid successive edits to settle
3. **Diff** — Compares previous and current config via JSON serialization
4. **Apply** — Reloads only affected subsystems:
   - **Agents**: Rebuilds agent runners for changed agent configurations
   - **Providers**: Refreshes the provider registry with updated keys
   - **Cron**: Reloads all cron job schedules
   - **API Key**: Middleware picks up the new key immediately
5. **Notify** — Publishes a `gateway.config.reloaded` activity event

### What Requires a Restart

- `Gateway.Host` / `Gateway.Port` — Kestrel bind address is set at startup
- `ExtensionsPath` — Extension assemblies are loaded once at startup

---

## 16. Security Model

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

## 17. Observability

BotNexus provides multiple observability mechanisms to monitor health and behavior.

### Health Checks

**Endpoint**: `GET /health` (liveness), `GET /ready` (readiness)

| Check | Type | Purpose |
|-------|------|---------|
| `message_bus` | Liveness | IMessageBus alive |
| `provider_registration` | Liveness | At least one provider registered |
| `extension_loader` | Liveness | Extension load report available |
| `cron_service` | Liveness | Cron service running |
| `channel_readiness` | Readiness | All configured channels running |
| `provider_readiness` | Readiness | At least one provider ready |

### Gateway REST API Endpoints

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/health` | GET | Liveness probe |
| `/ready` | GET | Readiness probe |
| `/api/sessions` | GET | List all sessions |
| `/api/sessions/{*key}` | GET | Get specific session details |
| `/api/channels` | GET | List configured channels |
| `/api/agents` | GET | List configured agents |
| `/api/providers` | GET | List LLM providers |
| `/api/tools` | GET | List available tools |
| `/api/extensions` | GET | Extension load summary |
| `/api/cron` | GET | List cron jobs |
| `/api/cron/history` | GET | Cron execution history |
| `/api/cron/{name}` | GET | Cron job details |
| `/api/cron/{name}/trigger` | POST | Manually trigger a cron job |
| `/api/cron/{name}/enable` | PUT | Enable/disable a cron job |
| `/api/status` | GET | Complete system status (version, health, agents, cron, sessions) |
| `/api/doctor` | GET | Run diagnostic checkups (optional `?category=` filter) |
| `/api/shutdown` | POST | Graceful Gateway shutdown (optional `{"reason": "..."}` body, returns 202) |

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

## 18. Installation Layout

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

## 19. Component Reference

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
├── ConfigReloadOrchestrator.cs  (Live config reload)
├── WebSocketChannel.cs
├── GatewayWebSocketHandler.cs
└── Health/
    ├── MessageBusHealthCheck.cs
    └── ...

BotNexus.Cli/
├── Program.cs             (CLI entry point, 16 commands)
└── Services/
    ├── ConfigFileManager.cs
    ├── ConsoleOutput.cs
    └── GatewayClient.cs

BotNexus.Diagnostics/
├── CheckupRunner.cs       (Orchestrates checkups + auto-fix)
├── DiagnosticsPaths.cs
├── ServiceCollectionExtensions.cs
└── Checkups/
    ├── Configuration/     (ConfigValid, AgentConfig, ProviderConfig)
    ├── Security/          (ApiKeyStrength, TokenPermissions, ExtensionSigned)
    ├── Connectivity/      (ProviderReachable, PortAvailable)
    ├── Extensions/        (ExtensionsFolderExists, ExtensionAssembliesValid)
    ├── Permissions/       (HomeDirWritable, LogDirWritable)
    └── Resources/         (DiskSpace)

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

channels/BotNexus.Channels.Base/
├── BaseChannel.cs
├── ChannelManager.cs
└── Models/
    └── ...

channels/BotNexus.Channels.{Discord,Slack,Telegram}/
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
| Config hot reload | `src/BotNexus.Gateway/ConfigReloadOrchestrator.cs` |
| Agent execution | `src/BotNexus.Agent/AgentLoop.cs` |
| Session storage | `src/BotNexus.Session/SessionManager.cs` |
| Extension loading | `src/BotNexus.Core/Extensions/ExtensionLoaderExtensions.cs` |
| CLI entry point | `src/BotNexus.Cli/Program.cs` |
| Diagnostics runner | `src/BotNexus.Diagnostics/CheckupRunner.cs` |
| Health checkup contract | `src/BotNexus.Core/Abstractions/IHealthCheckup.cs` |
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

---

## See Also

- [Developer Guide](dev-guide.md) — Local development setup and workflow
- [API Reference](api-reference.md) — REST and WebSocket endpoint documentation
- [Configuration Guide](configuration.md) — Complete configuration reference
- [Extension Development](extension-development.md) — Building custom providers, channels, and tools
