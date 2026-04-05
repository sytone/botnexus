# Provider system

The provider system is the communication layer between BotNexus and LLM APIs. It defines how models are registered, how API keys are resolved, how requests are routed to the correct provider implementation, and how streaming responses flow back to the caller. This document is the single reference for everything in the `BotNexus.Providers.Core` namespace and the built-in provider packages.

> If you have read the [architecture overview](00-overview.md), you already know that
> the provider layer sits between the agent loop and the outside world. This document
> takes you inside that layer.

---

## IApiProvider — the provider contract

Every LLM provider implements `IApiProvider`. The interface is deliberately small:

```csharp
public interface IApiProvider
{
    string Api { get; }
    LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null);
    LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null);
}
```

- **`Api`** — a unique string that identifies the wire format the provider speaks (for example `"anthropic-messages"` or `"openai-completions"`). This value is the routing key used by `LlmClient` to find the right provider.
- **`Stream`** — the full streaming entry point. The provider opens an HTTP connection, pushes events into the returned `LlmStream`, and closes it when the response is complete.
- **`StreamSimple`** — a convenience variant that accepts `SimpleStreamOptions` (which adds reasoning/thinking budget knobs on top of the base `StreamOptions`).

### Built-in providers

| Provider class | `Api` value | Constructor parameters | Notes |
|---|---|---|---|
| `AnthropicProvider` | `"anthropic-messages"` | `HttpClient` | Anthropic Messages API (refactored into specialized components) |
| `OpenAICompletionsProvider` | `"openai-completions"` | `HttpClient`, `ILogger<OpenAICompletionsProvider>` | OpenAI Chat Completions API |
| `OpenAIResponsesProvider` | `"openai-responses"` | `HttpClient`, `ILogger<OpenAIResponsesProvider>` | OpenAI Responses API (GPT-5 series) |
| `OpenAICompatProvider` | `"openai-compat"` | `HttpClient` | Any OpenAI-compatible endpoint |
| `CopilotProvider` *(static)* | — | — | Utility class; provides `ResolveApiKey()` and `ApplyDynamicHeaders()` |

`CopilotProvider` is **not** an `IApiProvider` implementation. It is a static helper that the Copilot host uses to resolve API keys from the environment (`COPILOT_GITHUB_TOKEN`, `GH_TOKEN`, `GITHUB_TOKEN`) and to inject dynamic headers before requests are sent through one of the other providers.

```csharp
// CopilotProvider resolves keys with a cascading fallback
string? key = CopilotProvider.ResolveApiKey(configuredApiKey: null);

// Apply request headers for a given message list
CopilotProvider.ApplyDynamicHeaders(request.Headers, messages);
```

> **Key takeaway:** A provider is a thin adapter between BotNexus's internal message model and a vendor's HTTP API. Implement `IApiProvider`, return an `LlmStream`, and the rest of the system handles everything else.

---

## ApiProviderRegistry

`ApiProviderRegistry` is an instance-based, thread-safe registry that maps `Api` strings to `IApiProvider` instances. Internally it uses a `ConcurrentDictionary<string, Registration>`.

```csharp
public sealed class ApiProviderRegistry
{
    // Each entry tracks the provider and an optional source ID for bulk removal
    private sealed record Registration(IApiProvider Provider, string? SourceId);
    private readonly ConcurrentDictionary<string, Registration> _registry = new();

    public void Register(IApiProvider provider, string? sourceId = null);
    public IApiProvider? Get(string api);
    public IReadOnlyList<IApiProvider> GetAll();
    public void Unregister(string sourceId);
    public void Clear();
}
```

### Methods

| Method | Purpose |
|---|---|
| `Register(provider, sourceId?)` | Add a provider. Optional `sourceId` lets you group registrations for later bulk removal. |
| `Get(api)` | Look up a provider by its `Api` string. Returns `null` when nothing matches. |
| `GetAll()` | Return every registered provider. |
| `Unregister(sourceId)` | Remove all registrations that share a given `sourceId`. |
| `Clear()` | Drop everything. |

### Usage example

```csharp
var registry = new ApiProviderRegistry();
registry.Register(new AnthropicProvider(httpClient), sourceId: "startup");
registry.Register(new OpenAICompletionsProvider(httpClient, logger), sourceId: "startup");

// Look up by Api string
IApiProvider? anthropic = registry.Get("anthropic-messages");

// Tear down a whole source group
registry.Unregister("startup");
```

> **Key takeaway:** The registry is the single place where providers are discovered at runtime. Keep `sourceId` consistent so you can tear down and replace provider sets without affecting other registrations.

---

## LlmModel

`LlmModel` is a record that describes a model's identity, capabilities, and cost structure. The `Api` field is the routing key that ties a model to its provider.

```csharp
public record LlmModel(
    string Id,                                       // e.g. "claude-sonnet-4-20250514"
    string Name,                                     // human-friendly name
    string Api,                                      // routing key → IApiProvider.Api
    string Provider,                                 // logical provider name
    string BaseUrl,                                  // API endpoint
    bool Reasoning,                                  // supports extended thinking?
    IReadOnlyList<string> Input,                     // modalities: "text", "image", …
    ModelCost Cost,                                  // per-token pricing
    int ContextWindow,                               // max context length in tokens
    int MaxTokens,                                   // max output tokens
    bool SupportsExtraHighThinking = false,          // supports xhigh thinking level?
    IReadOnlyDictionary<string, string>? Headers = null,  // extra HTTP headers
    OpenAICompletionsCompat? Compat = null            // compatibility overrides
);

public record ModelCost(
    decimal Input,
    decimal Output,
    decimal CacheRead,
    decimal CacheWrite
);
```

When you create an `LlmModel`, the `Api` value must match exactly one registered `IApiProvider.Api`. If it doesn't, `LlmClient.ResolveProvider` throws.

```csharp
var model = new LlmModel(
    Id: "claude-sonnet-4-20250514",
    Name: "Claude Sonnet 4",
    Api: "anthropic-messages",          // must match AnthropicProvider.Api
    Provider: "anthropic",
    BaseUrl: "https://api.anthropic.com/v1",
    Reasoning: true,
    Input: ["text", "image"],
    Cost: new ModelCost(3.0m, 15.0m, 0.3m, 3.75m),
    ContextWindow: 200_000,
    MaxTokens: 8_192
);
```

> **Key takeaway:** `LlmModel.Api` is the glue between models and providers. Get it wrong and the routing fails at runtime.

---

## ModelRegistry

`ModelRegistry` organises models into a hierarchical structure: **provider → modelId → LlmModel**. Like `ApiProviderRegistry`, it is instance-based and backed by `ConcurrentDictionary`.

```csharp
public sealed class ModelRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, LlmModel>> _registry = new();

    public void Register(string provider, LlmModel model);
    public LlmModel? GetModel(string provider, string modelId);
    public IReadOnlyList<string> GetProviders();
    public IReadOnlyList<LlmModel> GetModels(string provider);
    public static UsageCost CalculateCost(LlmModel model, Usage usage);
    public void Clear();
}
```

### Cost calculation

`CalculateCost` is a static utility that multiplies token counts by the model's `ModelCost` rates:

```csharp
UsageCost cost = ModelRegistry.CalculateCost(model, usage);
Console.WriteLine($"Total cost: ${cost.Total:F6}");
```

The returned `UsageCost` record breaks down input, output, cache-read, cache-write, and total costs:

```csharp
public record UsageCost(
    decimal Input,
    decimal Output,
    decimal CacheRead,
    decimal CacheWrite,
    decimal Total
);
```

> **Key takeaway:** `ModelRegistry` gives you a structured way to look up models and compute costs. Use `GetProviders()` and `GetModels(provider)` to enumerate what is available at runtime.

---

## LlmClient

`LlmClient` is the instance-based entry point that ties the provider and model registries together. You rarely need to call providers directly — `LlmClient` resolves the right provider for you.

```csharp
public sealed class LlmClient
{
    public ApiProviderRegistry ApiProviders { get; }
    public ModelRegistry Models { get; }

    public LlmClient(ApiProviderRegistry apiProviderRegistry, ModelRegistry modelRegistry)
    {
        ApiProviders = apiProviderRegistry ?? throw new ArgumentNullException(nameof(apiProviderRegistry));
        Models = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
    }
}
```

### Methods

| Method | Returns | Description |
|---|---|---|
| `Stream(model, context, options?)` | `LlmStream` | Start streaming. Returns immediately; events arrive asynchronously. |
| `CompleteAsync(model, context, options?)` | `Task<AssistantMessage>` | Stream to completion and return the final message. |
| `StreamSimple(model, context, options?)` | `LlmStream` | Simplified streaming with reasoning budget support. |
| `CompleteSimpleAsync(model, context, options?)` | `Task<AssistantMessage>` | Simplified stream to completion. |

### Provider resolution

Internally every method calls `ResolveProvider`, which performs a straightforward lookup:

```csharp
private IApiProvider ResolveProvider(string api)
{
    return ApiProviders.Get(api)
           ?? throw new InvalidOperationException($"No API provider registered for api: {api}");
}
```

If you see `InvalidOperationException: No API provider registered for api: …`, it means the model's `Api` value doesn't match any registered provider. Double-check your `ApiProviderRegistry.Register` calls.

### End-to-end example

```csharp
// Wire up registries
var apiProviders = new ApiProviderRegistry();
apiProviders.Register(new AnthropicProvider(httpClient));

var models = new ModelRegistry();
models.Register("anthropic", model);

// Create client
var client = new LlmClient(apiProviders, models);

// Stream a response
var context = new Context("You are a helpful assistant.", [new UserMessage("Hello!", 0)]);
await foreach (var evt in client.Stream(model, context))
{
    if (evt is TextDeltaEvent delta)
        Console.Write(delta.Delta);
}
```

> **Key takeaway:** `LlmClient` is the front door. Give it registries, hand it a model, and it routes to the correct provider automatically.

---

## Message types

Messages use `[JsonPolymorphic]` with a `"role"` discriminator so they serialize and deserialize cleanly across the wire.

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "role")]
[JsonDerivedType(typeof(UserMessage), "user")]
[JsonDerivedType(typeof(AssistantMessage), "assistant")]
[JsonDerivedType(typeof(ToolResultMessage), "toolResult")]
public abstract record Message(long Timestamp);
```

### UserMessage

```csharp
public sealed record UserMessage(
    UserMessageContent Content,   // text string or multi-modal content blocks
    long Timestamp
) : Message(Timestamp);
```

`UserMessageContent` supports implicit conversion from `string`, so you can write `new UserMessage("Hello", 0)` directly.

### AssistantMessage

```csharp
public sealed record AssistantMessage(
    IReadOnlyList<ContentBlock> Content,
    string Api,
    string Provider,
    string ModelId,
    Usage Usage,
    StopReason StopReason,
    string? ErrorMessage,
    string? ResponseId,
    long Timestamp
) : Message(Timestamp);
```

This is the richest message type. It records which provider and model produced it, token usage, the reason the model stopped, and the full content (text, thinking, tool calls).

### ToolResultMessage

```csharp
public sealed record ToolResultMessage(
    string ToolCallId,
    string ToolName,
    IReadOnlyList<ContentBlock> Content,
    bool IsError,
    long Timestamp,
    object? Details = null
) : Message(Timestamp);
```

Tool results are matched back to a tool call via `ToolCallId`. When the tool fails, set `IsError = true` and include the error text in `Content`.

> **Key takeaway:** The three message types form a closed conversation loop: user → assistant → tool result → assistant → …. The discriminator-based polymorphism means you can serialize an entire conversation to JSON and back without losing type information.

---

## Content blocks

Content blocks are the atoms inside messages. Like messages, they use `[JsonPolymorphic]` with a `"type"` discriminator.

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(ThinkingContent), "thinking")]
[JsonDerivedType(typeof(ImageContent), "image")]
[JsonDerivedType(typeof(ToolCallContent), "toolCall")]
public abstract record ContentBlock;
```

### TextContent

```csharp
public sealed record TextContent(
    string Text,
    string? TextSignature = null    // integrity signature for verified outputs
) : ContentBlock;
```

### ThinkingContent

```csharp
public sealed record ThinkingContent(
    string Thinking,
    string? ThinkingSignature = null,
    bool? Redacted = null           // true when the model's reasoning was filtered
) : ContentBlock;
```

### ImageContent

```csharp
public sealed record ImageContent(
    string Data,                    // base64-encoded image data
    string MimeType                 // e.g. "image/png"
) : ContentBlock;
```

### ToolCallContent

```csharp
public sealed record ToolCallContent(
    string Id,                      // unique call ID
    string Name,                    // tool name
    Dictionary<string, object?> Arguments,
    string? ThoughtSignature = null
) : ContentBlock;
```

> **Key takeaway:** Content blocks are polymorphic and self-describing. Pattern-match on the concrete type when consuming an `AssistantMessage.Content` list.

---

## LlmStream

`LlmStream` is a channel-based `IAsyncEnumerable<AssistantMessageEvent>`. It is the primary streaming primitive: providers **push** events into it; consumers **pull** them with `await foreach`.

```csharp
public sealed class LlmStream : IAsyncEnumerable<AssistantMessageEvent>
{
    private readonly Channel<AssistantMessageEvent> _channel =
        Channel.CreateUnbounded<AssistantMessageEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });

    private readonly TaskCompletionSource<AssistantMessage> _resultTcs = new();
}
```

### Channel configuration

| Option | Value | Why |
|---|---|---|
| `SingleWriter` | `true` | Only the owning provider writes events. |
| `SingleReader` | `false` | Multiple consumers can observe the same stream (though in practice there is usually one). |

### Push — emit an event

```csharp
public void Push(AssistantMessageEvent evt)
{
    switch (evt)
    {
        case DoneEvent done:
            _resultTcs.TrySetResult(done.Message);
            break;
        case ErrorEvent error:
            _resultTcs.TrySetResult(error.Error);
            break;
    }

    _channel.Writer.TryWrite(evt);
}
```

When the provider pushes a `DoneEvent` or `ErrorEvent`, the result is captured in a `TaskCompletionSource<AssistantMessage>` so callers that only want the final message can `await GetResultAsync()` without iterating events.

### End — close the channel

```csharp
public void End(AssistantMessage? result = null)
{
    if (result is not null)
        _resultTcs.TrySetResult(result);

    _channel.Writer.TryComplete();
}
```

### Consuming events

```csharp
public async IAsyncEnumerator<AssistantMessageEvent> GetAsyncEnumerator(
    CancellationToken cancellationToken = default)
{
    await foreach (var evt in _channel.Reader.ReadAllAsync(cancellationToken))
    {
        yield return evt;
    }
}
```

### Getting the final message

```csharp
public Task<AssistantMessage> GetResultAsync() => _resultTcs.Task;
```

### Provider-side pattern

```csharp
// Inside a provider's Stream method
var stream = new LlmStream();
_ = Task.Run(async () =>
{
    try
    {
        stream.Push(new StartEvent(partial));
        // … push text/thinking/tool events …
        stream.Push(new DoneEvent(StopReason.Stop, finalMessage));
    }
    catch (Exception ex)
    {
        stream.Push(new ErrorEvent(StopReason.Error, errorMessage));
    }
    finally
    {
        stream.End();
    }
});
return stream;
```

> **Key takeaway:** `LlmStream` decouples the HTTP response lifecycle from the consumer. The provider writes events at its own pace; the consumer reads them at its own pace. The `TaskCompletionSource` bridge lets you choose between event-by-event streaming and one-shot completion.

---

## AssistantMessageEvent hierarchy

All streaming events inherit from `AssistantMessageEvent(string Type)`. The `Type` property is a machine-readable discriminator.

```csharp
public abstract record AssistantMessageEvent(string Type);
```

### Event reference

| Event class | `Type` value | Key properties | Description |
|---|---|---|---|
| `StartEvent` | `"start"` | `Partial` | Stream has begun. `Partial` is the initial (empty) `AssistantMessage`. |
| `TextStartEvent` | `"text_start"` | `ContentIndex`, `Partial` | A new text block is starting at the given index. |
| `TextDeltaEvent` | `"text_delta"` | `ContentIndex`, `Delta`, `Partial` | Incremental text chunk. |
| `TextEndEvent` | `"text_end"` | `ContentIndex`, `Content`, `Partial` | Text block is complete. `Content` is the full text. |
| `ThinkingStartEvent` | `"thinking_start"` | `ContentIndex`, `Partial` | A thinking block is starting. |
| `ThinkingDeltaEvent` | `"thinking_delta"` | `ContentIndex`, `Delta`, `Partial` | Incremental thinking chunk. |
| `ThinkingEndEvent` | `"thinking_end"` | `ContentIndex`, `Content`, `Partial` | Thinking block is complete. |
| `ToolCallStartEvent` | `"toolcall_start"` | `ContentIndex`, `Partial` | A tool call block is starting. |
| `ToolCallDeltaEvent` | `"toolcall_delta"` | `ContentIndex`, `Delta`, `Partial` | Incremental tool call argument chunk. |
| `ToolCallEndEvent` | `"toolcall_end"` | `ContentIndex`, `ToolCall`, `Partial` | Tool call is complete. `ToolCall` holds the parsed `ToolCallContent`. |
| `DoneEvent` | `"done"` | `Reason`, `Message` | Stream completed successfully. |
| `ErrorEvent` | `"error"` | `Reason`, `Error` | Stream completed with an error. |

Every event except `DoneEvent` and `ErrorEvent` carries a `Partial` — the `AssistantMessage` as it has been assembled so far. This lets consumers render or inspect the message at any point during streaming.

> **Key takeaway:** Events follow a strict start → delta* → end lifecycle per content block, bookended by `StartEvent` and `DoneEvent`/`ErrorEvent` for the overall stream.

---

## Event sequences

### Simple text response

```
StartEvent
  TextStartEvent       (index 0)
  TextDeltaEvent       "Hello"
  TextDeltaEvent       ", world!"
  TextEndEvent         "Hello, world!"
DoneEvent
```

### Thinking followed by text

```
StartEvent
  ThinkingStartEvent   (index 0)
  ThinkingDeltaEvent   "Let me reason…"
  ThinkingEndEvent
  TextStartEvent       (index 1)
  TextDeltaEvent       "The answer is 42."
  TextEndEvent
DoneEvent
```

### Single tool call

```
StartEvent
  ToolCallStartEvent   (index 0)
  ToolCallDeltaEvent   "{\"path\":\"/src\"}"
  ToolCallEndEvent     ToolCallContent { Name = "list_files", … }
DoneEvent(Reason = ToolUse)
```

### Multiple tool calls

```
StartEvent
  ToolCallStartEvent   (index 0)
  ToolCallDeltaEvent   …
  ToolCallEndEvent
  ToolCallStartEvent   (index 1)
  ToolCallDeltaEvent   …
  ToolCallEndEvent
DoneEvent(Reason = ToolUse)
```

> **Key takeaway:** Consumers can pattern-match on event types and use `ContentIndex` to track which content block each delta belongs to.

---

## StreamOptions and SimpleStreamOptions

`StreamOptions` is the base configuration for every streaming call:

```csharp
public record class StreamOptions
{
    public float? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public CancellationToken CancellationToken { get; init; }
    public string? ApiKey { get; init; }
    public Transport Transport { get; init; } = Transport.Sse;
    public CacheRetention CacheRetention { get; init; } = CacheRetention.Short;
    public string? SessionId { get; init; }
    public Func<object, LlmModel, Task<object?>>? OnPayload { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public int MaxRetryDelayMs { get; init; } = 60000;
    public Dictionary<string, object>? Metadata { get; init; }
}
```

`SimpleStreamOptions` extends it with reasoning controls:

```csharp
public record class SimpleStreamOptions : StreamOptions
{
    public ThinkingLevel? Reasoning { get; init; }
    public ThinkingBudgets? ThinkingBudgets { get; init; }
}
```

### Supporting enums

```csharp
public enum Transport    { Sse, WebSocket, Auto }
public enum CacheRetention { None, Short, Long }
public enum ThinkingLevel  { Minimal, Low, Medium, High, ExtraHigh }
```

### Default thinking budgets

When no custom `ThinkingBudgets` are provided, `SimpleOptionsHelper` applies these per-level defaults (token-based providers only):

| ThinkingLevel | Default budget (tokens) |
|---|---|
| `Minimal` | 1,024 |
| `Low` | 2,048 |
| `Medium` | 8,192 |
| `High` | 16,384 |
| `ExtraHigh` | 16,384 (clamped to `High` for non-adaptive models) |

> **Note:** Adaptive thinking models (e.g., Opus 4.6) use effort strings (`"low"`, `"medium"`, `"high"`, `"max"`) instead of token budgets. For these models, `ExtraHigh` maps to `"max"` effort.

| Property | Default | Notes |
|---|---|---|
| `Temperature` | `null` (provider default) | Sampling temperature. |
| `MaxTokens` | `null` — `StreamSimple` caps at `min(model.MaxTokens, 32000)` | Override the model's max output tokens. |
| `Transport` | `Sse` | SSE, WebSocket, or Auto. |
| `CacheRetention` | `Short` | Prompt cache retention hint. |
| `MaxRetryDelayMs` | `60000` | Max backoff delay for retries. |
| `OnPayload` | `null` | Hook to inspect/modify the outgoing HTTP payload. |

> **Key takeaway:** Pass `StreamOptions` for full control or `SimpleStreamOptions` when you want reasoning budget support without managing raw thinking events yourself.

---

## Context

`Context` packages everything the model needs to produce a response:

```csharp
public record Context(
    string? SystemPrompt,
    IReadOnlyList<Message> Messages,
    IReadOnlyList<Tool>? Tools = null
);
```

- **`SystemPrompt`** — the system-level instruction. Can be `null` for prompt-less calls.
- **`Messages`** — the conversation history as a list of `Message` objects.
- **`Tools`** — optional tool definitions. When provided, the model may produce `ToolCallContent` blocks.

```csharp
var tools = new List<Tool>
{
    new Tool("read_file", "Read a file from disk", parametersSchema)
};

var context = new Context(
    SystemPrompt: "You are a coding assistant.",
    Messages: [new UserMessage("Read main.cs for me.", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())],
    Tools: tools
);
```

> **Key takeaway:** `Context` is immutable — build it once, pass it to `Stream` or `CompleteAsync`, and never worry about mutation.

---

## API key resolution

BotNexus resolves API keys through two complementary mechanisms.

### EnvironmentApiKeys (static, provider layer)

`EnvironmentApiKeys.GetApiKey(provider)` reads environment variables based on the provider name:

| Provider | Environment variable(s) |
|---|---|
| `"github-copilot"` | `COPILOT_GITHUB_TOKEN` → `GH_TOKEN` → `GITHUB_TOKEN` |
| `"anthropic"` | `ANTHROPIC_OAUTH_TOKEN` → `ANTHROPIC_API_KEY` |
| `"openai"` | `OPENAI_API_KEY` |
| `"azure-openai-responses"` | `AZURE_OPENAI_API_KEY` |
| `"google"` | `GEMINI_API_KEY` |
| `"groq"` | `GROQ_API_KEY` |
| `"cerebras"` | `CEREBRAS_API_KEY` |
| `"xai"` | `XAI_API_KEY` |
| `"openrouter"` | `OPENROUTER_API_KEY` |
| `"vercel-ai-gateway"` | `AI_GATEWAY_API_KEY` |
| `"zai"` | `ZAI_API_KEY` |
| `"mistral"` | `MISTRAL_API_KEY` |
| `"minimax"` | `MINIMAX_API_KEY` |
| `"minimax-cn"` | `MINIMAX_CN_API_KEY` |
| `"huggingface"` | `HF_TOKEN` |
| `"opencode"` / `"opencode-go"` | `OPENCODE_API_KEY` |
| `"kimi-coding"` | `KIMI_API_KEY` |

Note the cascading fallback for Copilot and Anthropic — this lets the same binary work in CI environments, local dev, and OAuth-based flows without code changes.

### GetApiKeyDelegate (dynamic, agent layer)

The agent loop calls a `GetApiKeyDelegate` before each LLM invocation, allowing runtime resolution such as OAuth token refresh or config file reads:

```csharp
public delegate Task<string?> GetApiKeyDelegate(string provider, CancellationToken cancellationToken);
```

Return `null` when no key is available or the provider does not require authentication. **This delegate must not throw.**

```csharp
// Example: resolve from environment, then fall back to a vault
GetApiKeyDelegate getApiKey = async (provider, ct) =>
{
    return EnvironmentApiKeys.GetApiKey(provider)
           ?? await vault.GetSecretAsync($"llm/{provider}/key", ct);
};
```

> **Key takeaway:** Use `EnvironmentApiKeys` for static setups and `GetApiKeyDelegate` for dynamic resolution. The two compose naturally — the delegate can call `EnvironmentApiKeys` internally.

---

## Error handling rule

**Never throw from a stream.** Always encode errors as an `ErrorEvent` so consumers see a clean shutdown instead of an unobserved exception.

### Correct

```csharp
public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
{
    var stream = new LlmStream();
    _ = Task.Run(async () =>
    {
        try
        {
            // … push events …
            stream.Push(new DoneEvent(StopReason.Stop, finalMessage));
        }
        catch (Exception ex)
        {
            var error = new AssistantMessage(
                Content: [new TextContent(ex.Message)],
                Api: Api, Provider: "my-provider", ModelId: model.Id,
                Usage: Usage.Empty(), StopReason: StopReason.Error,
                ErrorMessage: ex.Message, ResponseId: null,
                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            );
            stream.Push(new ErrorEvent(StopReason.Error, error));
        }
        finally
        {
            stream.End();
        }
    });
    return stream;
}
```

### Incorrect

```csharp
// ❌ DON'T DO THIS — the exception escapes and the stream is never closed
public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
{
    var response = await _httpClient.PostAsync(url, payload);  // throws!
    // Consumer never gets the stream, or gets a stream that hangs forever.
}
```

> **Key takeaway:** The stream is the error channel. Wrap the entire provider body in try/catch, push an `ErrorEvent`, and always call `End()` in a `finally` block.

---

## Creating a minimal custom provider

Here is a skeleton you can use as a starting point. See [Building your own](04-building-your-own.md) for the full tutorial.

```csharp
public sealed class MyCustomProvider : IApiProvider
{
    private readonly HttpClient _httpClient;

    public MyCustomProvider(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public string Api => "my-custom-api";

    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
    {
        var stream = new LlmStream();
        _ = Task.Run(async () =>
        {
            try
            {
                // 1. Build the HTTP request from model, context, and options
                var request = BuildRequest(model, context, options);

                // 2. Send with streaming
                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                // 3. Parse the response stream and push events
                var partial = CreateEmptyPartial(model);
                stream.Push(new StartEvent(partial));

                await foreach (var chunk in ParseResponseStream(response))
                {
                    stream.Push(new TextDeltaEvent(0, chunk, partial));
                }

                var finalMessage = BuildFinalMessage(model, partial);
                stream.Push(new TextEndEvent(0, ((TextContent)finalMessage.Content[0]).Text, finalMessage));
                stream.Push(new DoneEvent(StopReason.Stop, finalMessage));
            }
            catch (Exception ex)
            {
                var error = BuildErrorMessage(model, ex);
                stream.Push(new ErrorEvent(StopReason.Error, error));
            }
            finally
            {
                stream.End();
            }
        });
        return stream;
    }

    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        // Delegate to Stream — SimpleStreamOptions extends StreamOptions
        return Stream(model, context, options);
    }

    // … helper methods: BuildRequest, ParseResponseStream, etc.
}
```

Register it like any other provider:

```csharp
apiProviders.Register(new MyCustomProvider(httpClient));
```

> **Key takeaway:** A custom provider is roughly 100 lines of code. The pattern is always: create `LlmStream`, start a background task, push events, handle errors, call `End()`.

---

## AnthropicProvider — Implementation structure (Phase 4)

As of Port Audit Phase 4, `AnthropicProvider` is refactored into specialized components for clarity and maintainability:

| Component | Purpose | Responsibility |
|-----------|---------|-----------------|
| `AnthropicProvider.cs` | Main provider class | Orchestrates streaming, routes to specialized components |
| `AnthropicRequestBuilder.cs` | Request construction | Converts `Context` + `StreamOptions` → Anthropic JSON payload |
| `AnthropicMessageConverter.cs` | Message conversion | Maps BotNexus `Message[]` → Anthropic message format |
| `AnthropicStreamParser.cs` | SSE parsing | Consumes HTTP stream → `AssistantMessageEvent` instances |

All components handle:
- **Authentication:** Cascading fallback from config → `ANTHROPIC_OAUTH_TOKEN` → `ANTHROPIC_API_KEY`
- **Thinking levels:** Maps `ThinkingLevel` enum to Anthropic `budget_tokens` parameter
- **Tool use:** Parses tool call events and integrates into `ContentBlock` stream
- **StopReason mapping:** Converts Anthropic `stop_reason` to standardized `StopReason` enum (including new `Refusal` and `Sensitive` mappings)

### Model identity (Phase 4)

Model equality changed: `ModelsAreEqual` now compares only **`Id` and `Provider`**, not `BaseUrl`. This allows multiple regions/deployments of the same model to be treated as equivalent:

```csharp
// Before Phase 4: distinct because of different BaseUrl
var model1 = new LlmModel(Id: "claude-3.5-sonnet", Provider: "anthropic", 
    BaseUrl: "https://api.anthropic.com/v1", ...);
var model2 = new LlmModel(Id: "claude-3.5-sonnet", Provider: "anthropic", 
    BaseUrl: "https://api-eu.anthropic.com/v1", ...);
// model1 != model2  ❌

// Phase 4: same model, different region, now equivalent
// ModelsAreEqual(model1, model2) == true  ✓
```

This is useful for:
- Regional failover without changing agent configuration
- Multi-tenant deployments where different tenants use different endpoints
- Testing with mocked endpoints

---


`StopReason` tells you **why** the model stopped generating:

```csharp
[JsonConverter(typeof(JsonStringEnumConverter<StopReason>))]
public enum StopReason
{
    Stop,        // Natural completion
    Length,      // Hit max token limit
    ToolUse,     // Model wants to call a tool
    Error,       // Something went wrong
    Aborted,     // Caller cancelled (CancellationToken)
    Refusal,     // Model declined (safety filter)
    Sensitive    // Content flagged as sensitive
}
```

| Value | When you see it | What to do | Provider mapping |
|---|---|---|---|
| `Stop` | Model finished naturally | Display the response. | Anthropic/OpenAI: `"end_turn"` |
| `Length` | Output truncated | Warn the user or continue in a follow-up turn. | Anthropic: `"max_tokens"`, OpenAI: `"length"` |
| `ToolUse` | Model returned tool calls | Execute the tools and send results back. | Anthropic: `"tool_use"`, OpenAI: `"tool_calls"` |
| `Error` | Provider or API failure | Log, retry, or surface the error. | Internal error condition |
| `Aborted` | `CancellationToken` fired | Clean up gracefully. | Cancellation signal |
| `Refusal` | Safety filter triggered | Inform the user the request was declined. | Anthropic/OpenAI: `"refusal"` (new in Phase 4) |
| `Sensitive` | Content flagged as sensitive | Handle per your application's policy. | Anthropic: `"content_policy"`, `"safety"`, `"sensitive"` (new in Phase 4) |

> **Note (Phase 4):** `Refusal` and `Sensitive` are now properly mapped by providers. Previously, refusals and content flags were treated as generic stop reasons. Now they're explicitly distinguished so applications can provide context-specific handling.

> **Key takeaway:** Always check `StopReason` before assuming the response is complete. `ToolUse` is not an error — it is a control flow signal for the [agent core](02-agent-core.md).

---

## See also

- [Architecture overview](00-overview.md) — where the provider layer sits in the stack
- [Agent core](02-agent-core.md) — the agent loop that consumes providers
- [Building your own](04-building-your-own.md) — full provider implementation tutorial
