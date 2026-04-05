# Provider System

The provider system is the communication layer between BotNexus and LLM APIs. It defines how models are registered, how API keys are resolved, and how requests are routed to the correct provider implementation.

## Core Concepts

### IApiProvider — The Provider Contract

Every LLM provider implements `IApiProvider`, which defines three things:

```csharp
// BotNexus.Providers.Core.Registry.IApiProvider
public interface IApiProvider
{
    // Unique API format identifier (e.g., "anthropic-messages", "openai-completions")
    string Api { get; }

    // Full streaming with all options
    LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null);

    // Simplified streaming with reasoning/thinking support
    LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null);
}
```

Each provider handles a specific API format:

| Provider | `Api` Value | Handles |
|----------|-------------|---------|
| `AnthropicProvider` | `"anthropic-messages"` | Anthropic Messages API |
| `OpenAICompletionsProvider` | `"openai-completions"` | OpenAI Chat Completions API |
| `CopilotProvider` | `"openai-completions"` | GitHub Copilot (uses OpenAI format) |
| `OpenAICompatProvider` | `"openai-compat"` | Any OpenAI-compatible endpoint |

### LlmModel — Model Definitions

Every model the system can use is described by an `LlmModel` record:

```csharp
// BotNexus.Providers.Core.Models.LlmModel
public record LlmModel(
    string Id,               // Model identifier (e.g., "gpt-4.1")
    string Name,             // Display name
    string Api,              // API format (routes to provider)
    string Provider,         // Provider identifier (e.g., "github-copilot")
    string BaseUrl,          // API endpoint base URL
    bool Reasoning,          // Supports extended reasoning
    IReadOnlyList<string> Input,  // Input modalities ("text", "image")
    ModelCost Cost,          // Per-million-token pricing
    int ContextWindow,       // Max context tokens
    int MaxTokens,           // Max output tokens
    IReadOnlyDictionary<string, string>? Headers = null,  // Custom HTTP headers
    OpenAICompletionsCompat? Compat = null  // Compatibility flags
);
```

The `Api` field is the routing key — it determines which `IApiProvider` handles requests for this model.

### ModelRegistry — Where Models Live

`ModelRegistry` is a thread-safe `ConcurrentDictionary<provider, ConcurrentDictionary<modelId, LlmModel>>`:

```csharp
// Register a model
ModelRegistry.Register("github-copilot", myModel);

// Look up a model
LlmModel? model = ModelRegistry.GetModel("github-copilot", "gpt-4.1");

// List all providers
IReadOnlyList<string> providers = ModelRegistry.GetProviders();

// List all models for a provider
IReadOnlyList<LlmModel> models = ModelRegistry.GetModels("github-copilot");
```

Models are registered at startup. Built-in models are defined in `BuiltInModels.cs`, and extensions can register additional models at runtime.

### ApiProviderRegistry — Provider Registration

`ApiProviderRegistry` maps API format strings to `IApiProvider` instances:

```csharp
// Register a provider
ApiProviderRegistry.Register(new AnthropicProvider(), sourceId: "built-in");

// Look up a provider
IApiProvider? provider = ApiProviderRegistry.Get("anthropic-messages");

// Unregister all providers from a source
ApiProviderRegistry.Unregister("my-extension");
```

The `sourceId` parameter supports extension cleanup — when an extension is unloaded, all its providers can be removed in one call.

## LlmClient — The Entry Point

`LlmClient` is the static entry point that ties registries together:

```csharp
public static class LlmClient
{
    // Streaming with full options
    public static LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null);

    // Non-streaming convenience (awaits the full response)
    public static async Task<AssistantMessage> CompleteAsync(
        LlmModel model, Context context, StreamOptions? options = null);

    // Streaming with SimpleStreamOptions (supports reasoning)
    public static LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null);

    // Non-streaming with SimpleStreamOptions
    public static async Task<AssistantMessage> CompleteSimpleAsync(
        LlmModel model, Context context, SimpleStreamOptions? options = null);
}
```

Internally, `LlmClient` resolves the provider from `model.Api`:

```csharp
private static IApiProvider ResolveProvider(string api)
{
    return ApiProviderRegistry.Get(api)
           ?? throw new InvalidOperationException($"No API provider registered for api: {api}");
}
```

## Request Context

Every LLM request is described by a `Context` record:

```csharp
public record Context(
    string? SystemPrompt,              // System message
    IReadOnlyList<Message> Messages,   // Conversation history
    IReadOnlyList<Tool>? Tools = null  // Available tools (JSON schema)
);
```

Messages use polymorphic serialization with a `role` discriminator:

```csharp
public abstract record Message(long Timestamp);
public sealed record UserMessage(UserMessageContent Content, long Timestamp) : Message(Timestamp);
public sealed record AssistantMessage(...) : Message(Timestamp);
public sealed record ToolResultMessage(string ToolCallId, string ToolName, ...) : Message(Timestamp);
```

## API Key Resolution

API keys are resolved through two mechanisms:

### 1. Environment Variables (EnvironmentApiKeys)

```csharp
// Maps provider identifiers to environment variables
"openai"         → OPENAI_API_KEY
"anthropic"      → ANTHROPIC_OAUTH_TOKEN ?? ANTHROPIC_API_KEY
"github-copilot" → COPILOT_GITHUB_TOKEN ?? GH_TOKEN ?? GITHUB_TOKEN
"groq"           → GROQ_API_KEY
// ... etc
```

### 2. Runtime GetApiKey Delegate

The agent loop calls a `GetApiKeyDelegate` before each LLM invocation, allowing runtime resolution (OAuth refresh, config file reads, etc.):

```csharp
public delegate Task<string?> GetApiKeyDelegate(string provider, CancellationToken cancellationToken);
```

## Stream Options

Two option classes control generation behavior:

```csharp
public class StreamOptions
{
    public float? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public CancellationToken CancellationToken { get; set; }
    public string? ApiKey { get; set; }
    public Transport Transport { get; set; } = Transport.Sse;
    public CacheRetention CacheRetention { get; set; } = CacheRetention.Short;
    public string? SessionId { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public int MaxRetryDelayMs { get; set; } = 60000;
}

public class SimpleStreamOptions : StreamOptions
{
    public ThinkingLevel? Reasoning { get; set; }
    public ThinkingBudgets? ThinkingBudgets { get; set; }
}
```

The agent loop uses `SimpleStreamOptions` to support reasoning/thinking models.

## Full Request Flow

```mermaid
sequenceDiagram
    participant Agent as Agent
    participant ALR as AgentLoopRunner
    participant CC as ContextConverter
    participant LC as LlmClient
    participant APR as ApiProviderRegistry
    participant Provider as IApiProvider
    participant API as LLM API
    participant LS as LlmStream
    participant SA as StreamAccumulator

    Agent->>ALR: RunAsync(messages)
    ALR->>CC: ToProviderContext(agentContext)
    CC-->>ALR: Context (system prompt, messages, tools)
    ALR->>ALR: BuildStreamOptions (resolve API key)
    ALR->>LC: StreamSimple(model, context, options)
    LC->>APR: Get(model.Api)
    APR-->>LC: IApiProvider
    LC->>Provider: StreamSimple(model, context, options)
    Provider->>API: HTTP POST (SSE)
    API-->>Provider: SSE event stream

    loop For each SSE event
        Provider->>LS: Push(event)
        LS-->>SA: yield event
        SA->>SA: Accumulate delta
        SA-->>Agent: emit AgentEvent
    end

    Provider->>LS: Push(DoneEvent) + End()
    LS-->>SA: DoneEvent
    SA-->>ALR: AssistantAgentMessage
```

## Provider Class Hierarchy

```mermaid
classDiagram
    class IApiProvider {
        <<interface>>
        +Api: string
        +Stream(model, context, options): LlmStream
        +StreamSimple(model, context, options): LlmStream
    }

    class AnthropicProvider {
        +Api = "anthropic-messages"
        +Stream()
        +StreamSimple()
    }

    class OpenAICompletionsProvider {
        +Api = "openai-completions"
        +Stream()
        +StreamSimple()
    }

    class CopilotProvider {
        +Api = "openai-completions"
        +Stream()
        +StreamSimple()
    }

    class OpenAICompatProvider {
        +Api = "openai-compat"
        +Stream()
        +StreamSimple()
    }

    IApiProvider <|.. AnthropicProvider
    IApiProvider <|.. OpenAICompletionsProvider
    IApiProvider <|.. CopilotProvider
    IApiProvider <|.. OpenAICompatProvider

    class LlmClient {
        +Stream(model, context, options)$: LlmStream
        +CompleteAsync(model, context, options)$: Task~AssistantMessage~
        +StreamSimple(model, context, options)$: LlmStream
        +CompleteSimpleAsync(model, context, options)$: Task~AssistantMessage~
    }

    class ApiProviderRegistry {
        +Register(provider, sourceId?)
        +Get(api): IApiProvider?
        +GetAll(): IReadOnlyList~IApiProvider~
        +Unregister(sourceId)
    }

    class ModelRegistry {
        +Register(provider, model)
        +GetModel(provider, modelId): LlmModel?
        +GetProviders(): IReadOnlyList~string~
        +GetModels(provider): IReadOnlyList~LlmModel~
        +CalculateCost(model, usage)$: UsageCost
    }

    LlmClient ..> ApiProviderRegistry : resolves via
    LlmClient ..> IApiProvider : delegates to
    ModelRegistry ..> LlmModel : stores
```

## Next Steps

- [Streaming →](02-streaming.md) — how SSE parsing and the event pipeline work
- [Agent Loop →](03-agent-loop.md) — how the agent loop uses providers
- [Architecture Overview](00-overview.md) — back to the big picture
