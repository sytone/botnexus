# Provider architecture

> **Audience:** Developers building custom LLM providers or integrating new LLM APIs into BotNexus.
> **Prerequisites:** C#/.NET, async/await, SSE (Server-Sent Events), familiarity with LLM chat APIs.
> **Source code:** `src/providers/BotNexus.Providers.Core/` and the four provider implementations.

## What you'll learn

1. The `IApiProvider` interface contract
2. How `LlmClient` routes requests to providers
3. Model registry and resolution
4. The streaming event protocol
5. Message transformation pipeline
6. How to implement a new provider step-by-step

---

## Provider interface contract

Every LLM provider implements `IApiProvider` (defined in `Registry/IApiProvider.cs`):

```csharp
public interface IApiProvider
{
    // Unique API identifier (e.g., "anthropic-messages", "openai-completions")
    string Api { get; }

    // Standard streaming — provider parses the request and pushes events into an LlmStream
    LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null);

    // Extended streaming with reasoning/thinking support
    LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null);
}
```

**Key design decisions:**

- **Streaming-first.** There is no synchronous `Complete()` method on the provider. One-shot completions are built on top of streams at the `LlmClient` level.
- **The `Api` property is the routing key.** When `LlmClient` receives a request for a model, it reads `model.Api` and looks up the provider whose `Api` matches.
- **`StreamSimple` adds reasoning.** Providers that support extended thinking (Anthropic, OpenAI o-series) implement `StreamSimple` to handle `ThinkingLevel`, budgets, and effort mapping. Providers that don't support reasoning can simply delegate to `Stream`.

### Built-in providers

| Provider class | `Api` value | LLM API |
|---|---|---|
| `AnthropicProvider` | `"anthropic-messages"` | Anthropic Messages API (`/v1/messages`) |
| `OpenAICompletionsProvider` | `"openai-completions"` | OpenAI Chat Completions (`/chat/completions`) |
| `OpenAIResponsesProvider` | `"openai-responses"` | OpenAI Responses API (`/responses`) |
| `OpenAICompatProvider` | `"openai-compat"` | Any OpenAI-compatible endpoint (Ollama, vLLM, LM Studio, SGLang) |

`CopilotProvider` is **not** an `IApiProvider`. It is a static utility class that provides OAuth helpers for GitHub Copilot. Copilot routes through the standard providers (Anthropic or OpenAI) using the `https://api.individual.githubcopilot.com` base URL.

---

## How LlmClient uses providers

`LlmClient` (`LlmClient.cs`) is the top-level entry point that consumers interact with. It owns an `ApiProviderRegistry` and a `ModelRegistry`.

```csharp
public sealed class LlmClient
{
    public ApiProviderRegistry ApiProviders { get; }
    public ModelRegistry Models { get; }

    // Standard streaming
    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null);

    // Await the final message (built on top of Stream)
    public Task<AssistantMessage> CompleteAsync(LlmModel model, Context context, StreamOptions? options = null);

    // Extended streaming with reasoning
    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null);

    // Await the final message with reasoning
    public Task<AssistantMessage> CompleteSimpleAsync(LlmModel model, Context context, SimpleStreamOptions? options = null);
}
```

### Request flow

```
LlmClient.Stream(model, context, options)
    │
    ├── Reads model.Api (e.g., "anthropic-messages")
    ├── Calls ApiProviders.Get(model.Api) to find the provider
    │     └── Throws InvalidOperationException if no provider registered
    └── Delegates to provider.Stream(model, context, options)
          └── Returns LlmStream (events pushed asynchronously)
```

### Complete vs Stream

`CompleteAsync` calls `Stream`, then awaits `stream.GetResultAsync()` to collect the final `AssistantMessage`:

```csharp
// Streaming — consume events as they arrive
LlmStream stream = client.Stream(model, context);
await foreach (var evt in stream)
{
    if (evt is TextDeltaEvent delta)
        Console.Write(delta.Delta);
}

// One-shot — wait for the final result
AssistantMessage result = await client.CompleteAsync(model, context);
Console.WriteLine(result.Content.First().Text);
```

---

## Model registry and resolution

### LlmModel

Every request requires an `LlmModel` record that fully describes the target model:

```csharp
public record LlmModel(
    string Id,                                    // "claude-sonnet-4.5"
    string Name,                                  // "Claude Sonnet 4.5"
    string Api,                                   // "anthropic-messages" (routing key)
    string Provider,                              // "github-copilot" or "anthropic"
    string BaseUrl,                               // "https://api.individual.githubcopilot.com"
    bool Reasoning,                               // true if model supports extended thinking
    IReadOnlyList<string> Input,                  // ["text", "image"]
    ModelCost Cost,                               // per-million-token pricing
    int ContextWindow,                            // max context tokens (e.g., 200000)
    int MaxTokens,                                // max output tokens (e.g., 32000)
    IReadOnlyDictionary<string, string>? Headers, // custom HTTP headers
    OpenAICompletionsCompat? Compat               // compat settings for OpenAI-like APIs
);
```

### ModelRegistry

`ModelRegistry` stores models in a thread-safe `ConcurrentDictionary` organized by provider → model ID:

```csharp
var registry = new ModelRegistry();

// Register a model
registry.Register("anthropic", new LlmModel(
    Id: "claude-sonnet-4.5",
    Name: "Claude Sonnet 4.5",
    Api: "anthropic-messages",
    Provider: "anthropic",
    BaseUrl: "https://api.anthropic.com",
    // ...
));

// Look up a model
LlmModel? model = registry.GetModel("anthropic", "claude-sonnet-4.5");

// List all providers
IReadOnlyList<string> providers = registry.GetProviders();

// List all models for a provider
IReadOnlyList<LlmModel> models = registry.GetModels("anthropic");
```

### Built-in models

`BuiltInModels.RegisterAll()` pre-registers 20+ models for GitHub Copilot, including:

- **Claude:** claude-haiku-4.5, claude-sonnet-4, claude-sonnet-4.5, claude-sonnet-4.6, claude-opus-4.5, claude-opus-4.6
- **GPT:** gpt-4.1, gpt-4o, gpt-5, gpt-5-mini, gpt-5.1, gpt-5.2, gpt-5.2-codex, gpt-5.3-codex, gpt-5.4
- **Gemini:** gemini-2.5-pro, gemini-3-flash-preview, gemini-3-pro-preview, gemini-3.1-pro-preview
- **Grok:** grok-code-fast-1

All built-in models use the `https://api.individual.githubcopilot.com` base URL with Copilot-specific headers (`User-Agent`, `Editor-Version`, `Editor-Plugin-Version`, `Copilot-Integration-Id`).

### Cost calculation

```csharp
UsageCost cost = ModelRegistry.CalculateCost(model, usage);
// cost.Input, cost.Output, cost.CacheRead, cost.CacheWrite, cost.Total
// Calculated as: (tokens / 1,000,000) * model.Cost.{Input|Output|...}
```

---

## Streaming event protocol

The streaming system uses `LlmStream` backed by `System.Threading.Channels`. Providers push `AssistantMessageEvent` records; consumers iterate with `await foreach`.

### LlmStream

```csharp
public sealed class LlmStream : IAsyncEnumerable<AssistantMessageEvent>
{
    void Push(AssistantMessageEvent evt);          // Provider pushes events
    void End(AssistantMessage? result = null);     // Provider signals completion
    Task<AssistantMessage> GetResultAsync();       // Consumer awaits final result
}
```

- **Channel:** Unbounded, single-writer, multi-reader.
- **Completion:** The stream ends when `DoneEvent` or `ErrorEvent` is pushed, or `End()` is called.

### Event hierarchy

All events derive from `AssistantMessageEvent(string Type)`. Each carries a `Partial` snapshot of the in-progress `AssistantMessage`.

```
AssistantMessageEvent
├── StartEvent("start")                  — First event; contains initial partial message
│
├── TextStartEvent("text_start")         — New text content block begins
├── TextDeltaEvent("text_delta")         — Chunk of text content
├── TextEndEvent("text_end")             — Text block complete
│
├── ThinkingStartEvent("thinking_start") — Extended thinking block begins
├── ThinkingDeltaEvent("thinking_delta") — Chunk of thinking content
├── ThinkingEndEvent("thinking_end")     — Thinking block complete
│
├── ToolCallStartEvent("toolcall_start") — Tool call begins (name, ID known)
├── ToolCallDeltaEvent("toolcall_delta") — Chunk of JSON arguments
├── ToolCallEndEvent("toolcall_end")     — Tool call complete (full ToolCallContent)
│
├── DoneEvent("done")                    — Streaming complete; carries final AssistantMessage
└── ErrorEvent("error")                  — Error occurred; carries StopReason and error details
```

### Event lifecycle

A typical successful response produces this event sequence:

```
StartEvent
├── TextStartEvent(index=0)
│   ├── TextDeltaEvent(index=0, delta="Here is ")
│   ├── TextDeltaEvent(index=0, delta="the code...")
│   └── TextEndEvent(index=0, content="Here is the code...")
├── ToolCallStartEvent(index=1)
│   ├── ToolCallDeltaEvent(index=1, delta='{"path":')
│   ├── ToolCallDeltaEvent(index=1, delta='"src/main.cs"}')
│   └── ToolCallEndEvent(index=1, toolCall=ToolCallContent{...})
└── DoneEvent(reason=ToolUse, message=AssistantMessage{...})
```

### StopReason

The `DoneEvent` and `ErrorEvent` carry a `StopReason` that explains why streaming ended:

| StopReason | Meaning |
|---|---|
| `Stop` | Model finished naturally |
| `Length` | Hit max output tokens |
| `ToolUse` | Model requested tool calls |
| `Error` | Provider or network error |
| `Aborted` | Request was cancelled |
| `Refusal` | Model refused the request |
| `Sensitive` | Content policy violation |

---

## Message transformation pipeline

### Core message types

Messages flow through two layers with different type systems:

**Provider layer** (in `BotNexus.Providers.Core/Models/Messages.cs`):

```csharp
// Base: Message(long Timestamp) with "role" discriminator
UserMessage          // role="user", Content: string or ContentBlock[]
AssistantMessage     // role="assistant", Content: ContentBlock[], Usage, StopReason
ToolResultMessage    // role="toolResult", ToolCallId, ToolName, Content, IsError
```

**Content blocks** represent the building blocks of messages:

```csharp
TextContent        // Plain text, optional signature
ThinkingContent    // Extended thinking, optional signature, optional redacted flag
ImageContent       // Base64 data + MIME type
ToolCallContent    // Tool call ID, name, and parsed arguments
```

### Provider-specific conversion

Each provider converts the common `Context` (system prompt + messages + tools) into its own API format. For example:

**Anthropic** (`AnthropicMessageConverter.ConvertMessages`):
1. Runs `MessageTransformer.TransformMessages()` for cross-provider normalization
2. Converts `UserMessage` → `{role: "user", content: [...]}`
3. Converts `AssistantMessage` → `{role: "assistant", content: [...]}` with thinking/signature handling
4. Merges consecutive `ToolResultMessage` into a single `{role: "user", content: [{type: "tool_result", ...}]}`

**OpenAI Completions** (`OpenAICompletionsProvider.ConvertMessages`):
1. Runs `MessageTransformer.TransformMessages()`
2. System prompt uses `"developer"` role if reasoning is enabled
3. Converts `ToolResultMessage` → `{role: "tool", tool_call_id, content}`
4. Handles compat-specific requirements (e.g., `RequiresAssistantAfterToolResult` for DeepSeek)

### MessageTransformer

`MessageTransformer.TransformMessages()` handles cross-provider normalization before provider-specific conversion:

1. **Thinking conversion:** If switching models (different Provider+Api+ModelId), converts `ThinkingContent` blocks to `TextContent` so the new model can read them.
2. **Tool call ID normalization:** Applies a provider-specific normalizer (e.g., Anthropic strips non-alphanumeric characters).
3. **Orphaned tool call cleanup:** If an `AssistantMessage` has tool calls but no matching `ToolResultMessage` follows, inserts a synthetic error result.
4. **Error message filtering:** Skips assistant messages with `StopReason` of Error, Aborted, Refusal, or Sensitive.

---

## How to implement a new provider

### Step 1: Create the project

```
src/providers/BotNexus.Providers.MyApi/
├── BotNexus.Providers.MyApi.csproj
├── MyApiProvider.cs
└── MyApiOptions.cs (optional)
```

Add a reference to `BotNexus.Providers.Core`:

```xml
<ProjectReference Include="..\BotNexus.Providers.Core\BotNexus.Providers.Core.csproj" />
```

### Step 2: Implement IApiProvider

```csharp
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.Core.Streaming;

public sealed class MyApiProvider(HttpClient httpClient) : IApiProvider
{
    public string Api => "my-api";

    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
    {
        var stream = new LlmStream();

        _ = Task.Run(async () =>
        {
            try
            {
                await StreamCoreAsync(model, context, options, stream, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                var error = AnthropicProvider.BuildMessage(model, [], Usage.Empty(),
                    StopReason.Aborted, "Request cancelled", null);
                stream.Push(new ErrorEvent(StopReason.Aborted, error));
                stream.End(error);
            }
            catch (Exception ex)
            {
                var error = AnthropicProvider.BuildMessage(model, [], Usage.Empty(),
                    StopReason.Error, ex.Message, null);
                stream.Push(new ErrorEvent(StopReason.Error, error));
                stream.End(error);
            }
        });

        return stream;
    }

    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        // Map SimpleStreamOptions to your provider's options
        var baseOptions = SimpleOptionsHelper.BuildBaseOptions(model, options, options?.ApiKey ?? "");
        return Stream(model, context, baseOptions);
    }
}
```

### Step 3: Implement the streaming core

Every provider follows the same pattern: build an HTTP request, send it, parse the SSE response line by line, and push events into the `LlmStream`.

```csharp
private async Task StreamCoreAsync(
    LlmModel model,
    Context context,
    StreamOptions? options,
    LlmStream stream,
    CancellationToken ct)
{
    // 1. Resolve API key
    string apiKey = options?.ApiKey
        ?? EnvironmentApiKeys.GetApiKey(model.Provider)
        ?? throw new InvalidOperationException($"No API key for {model.Provider}");

    // 2. Build request body
    var payload = BuildRequestBody(model, context, options);

    // 3. Send HTTP request with SSE streaming
    using var request = new HttpRequestMessage(HttpMethod.Post, $"{model.BaseUrl}/v1/chat");
    request.Content = JsonContent.Create(payload);
    request.Headers.Add("Authorization", $"Bearer {apiKey}");
    request.Headers.Add("Accept", "text/event-stream");

    using var response = await httpClient.SendAsync(request,
        HttpCompletionOption.ResponseHeadersRead, ct);
    response.EnsureSuccessStatusCode();

    // 4. Parse SSE stream and push events
    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
    var contentBlocks = new List<ContentBlock>();
    var usage = Usage.Empty();
    var stopReason = StopReason.Stop;

    // Push the StartEvent
    var partialMessage = BuildPartialMessage(model, contentBlocks, usage);
    stream.Push(new StartEvent(partialMessage));

    while (!reader.EndOfStream)
    {
        var line = await reader.ReadLineAsync(ct);
        if (line is null || !line.StartsWith("data: ")) continue;

        var data = line[6..];
        if (data == "[DONE]") break;

        // Parse JSON and dispatch to appropriate events
        var json = JsonDocument.Parse(data);
        // ... emit TextDeltaEvent, ToolCallDeltaEvent, etc.
    }

    // 5. Build and push the final message
    var finalMessage = BuildFinalMessage(model, contentBlocks, usage, stopReason);
    stream.Push(new DoneEvent(stopReason, finalMessage));
    stream.End(finalMessage);
}
```

### Step 4: Map stop reasons

Each API returns its own stop/finish reason strings. Map them to `StopReason`:

```csharp
private static StopReason MapStopReason(string? reason) => reason switch
{
    "stop" or "end_turn" => StopReason.Stop,
    "length" or "max_tokens" => StopReason.Length,
    "tool_calls" or "tool_use" => StopReason.ToolUse,
    "content_filter" => StopReason.Sensitive,
    _ => StopReason.Stop
};
```

### Step 5: Register the provider

In your application startup:

```csharp
var httpClient = new HttpClient();
var apiProviders = new ApiProviderRegistry();
var modelRegistry = new ModelRegistry();

// Register the provider
apiProviders.Register(new MyApiProvider(httpClient));

// Register models for this provider
modelRegistry.Register("my-provider", new LlmModel(
    Id: "my-model-v1",
    Name: "My Model v1",
    Api: "my-api",           // Must match MyApiProvider.Api
    Provider: "my-provider",
    BaseUrl: "https://api.myprovider.com",
    Reasoning: false,
    Input: ["text"],
    Cost: new ModelCost(0.001m, 0.002m, 0m, 0m),
    ContextWindow: 128000,
    MaxTokens: 4096,
    Headers: null,
    Compat: null
));

// Create client
var client = new LlmClient(apiProviders, modelRegistry);
```

### Step 6: Handle provider-specific options (optional)

If your provider has custom options, extend `StreamOptions`:

```csharp
public record class MyApiOptions : StreamOptions
{
    public string? CustomParam { get; set; }
    public bool EnableFeatureX { get; set; }
}
```

Then in `StreamSimple`, map from `SimpleStreamOptions` to your custom options before calling `Stream`.

---

## Environment API keys

`EnvironmentApiKeys.GetApiKey(provider)` resolves API keys from environment variables:

| Provider | Environment variable(s) |
|---|---|
| `anthropic` | `ANTHROPIC_OAUTH_TOKEN` → `ANTHROPIC_API_KEY` |
| `github-copilot` | `COPILOT_GITHUB_TOKEN` → `GH_TOKEN` → `GITHUB_TOKEN` |
| `openai` | `OPENAI_API_KEY` |
| `groq` | `GROQ_API_KEY` |
| `cerebras` | `CEREBRAS_API_KEY` |
| `xai` | `XAI_API_KEY` |
| `openrouter` | `OPENROUTER_API_KEY` |
| `mistral` | `MISTRAL_API_KEY` |
| `huggingface` | `HF_TOKEN` |

Anthropic tries the OAuth token first, then falls back to the standard API key. GitHub Copilot tries three environment variables in priority order.

---

## OpenAI compatibility layer

`OpenAICompletionsCompat` controls how the OpenAI Completions provider adapts to different API servers. Each model can carry a `Compat` record:

```csharp
public record OpenAICompletionsCompat(
    bool? SupportsStoreParam,                      // Can we send "store": false?
    bool? SupportsDeveloperRole,                   // Use "developer" vs "system" role?
    bool? SupportsReasoningEffort,                 // Send "reasoning_effort" field?
    string MaxTokensField,                         // "max_tokens" or "max_completion_tokens"
    bool? RequiresAssistantAfterToolResult,        // Insert synthetic assistant message?
    bool? RequiresThinkingAsText,                  // Wrap thinking in text blocks?
    string ThinkingFormat,                         // "openai", "zai", "openrouter"
    // ... more fields
);
```

`CompatDetector.Detect()` auto-detects provider capabilities from the model's base URL, looking for patterns like `ollama`, `vllm`, `lm-studio`, and `sglang`.

Pre-configured compat profiles exist for: Cerebras, xAI (Grok), ZAI, DeepSeek, Chutes, Groq, and OpenRouter.

---

## Further reading

- [Agent event system](agent-events.md) — how the agent loop consumes streaming events
- [Tool security model](tool-security.md) — how tools are sandboxed
- [Building a coding agent](building-a-coding-agent.md) — wiring providers into a complete agent
- [Glossary](05-glossary.md) — all key terms
