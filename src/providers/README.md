# BotNexus.Providers

Unified LLM API with provider abstraction, streaming, tool calling, thinking/reasoning, and cross-provider handoffs. C#/.NET 10 port inspired by [pi-mono's AI package](https://github.com/badlogic/pi-mono/tree/main/packages/ai).

> **Note:** This library only includes models that support tool calling (function calling), as this is essential for agentic workflows.

## Table of Contents

- [Supported Providers](#supported-providers)
- [Quick Start](#quick-start)
- [Tools](#tools)
  - [Defining Tools](#defining-tools)
  - [Handling Tool Calls](#handling-tool-calls)
  - [Streaming Tool Calls with Partial JSON](#streaming-tool-calls-with-partial-json)
  - [Complete Event Reference](#complete-event-reference)
- [Image Input](#image-input)
- [Thinking/Reasoning](#thinkingreasoning)
  - [Unified Interface](#unified-interface-streamsimplecompletesimple)
  - [Provider-Specific Options](#provider-specific-options-streamcomplete)
  - [Streaming Thinking Content](#streaming-thinking-content)
- [Stop Reasons](#stop-reasons)
- [Error Handling](#error-handling)
  - [Cancellation with CancellationToken](#cancellation-with-cancellationtoken)
  - [Continuing After Cancellation](#continuing-after-cancellation)
  - [Debugging Provider Payloads](#debugging-provider-payloads)
- [APIs, Models, and Providers](#apis-models-and-providers)
  - [Providers and Models](#providers-and-models)
  - [Querying Providers and Models](#querying-providers-and-models)
  - [Custom Models](#custom-models)
  - [OpenAI Compatibility Settings](#openai-compatibility-settings)
- [Cross-Provider Handoffs](#cross-provider-handoffs)
- [Context Serialization](#context-serialization)
- [OAuth Providers](#oauth-providers)
- [Environment Variables](#environment-variables)
- [Project Structure](#project-structure)
- [Adding a New Provider](#adding-a-new-provider)

## Supported Providers

| Provider | API | Description |
|----------|-----|-------------|
| OpenAI | `openai-completions` | Chat Completions API — GPT-4o, o3, etc. |
| OpenAI | `openai-responses` | Responses API — GPT-5 series |
| Anthropic | `anthropic-messages` | Messages API — Claude Sonnet 4, Opus 4, etc. |
| GitHub Copilot | *(static utility)* | OAuth device flow, proxies to OpenAI/Anthropic models |
| OpenAI-Compatible | `openai-compat` | Ollama, vLLM, LM Studio, SGLang, Cerebras, xAI, DeepSeek, etc. |

## Quick Start

```csharp
using System.Text.Json;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.Core.Streaming;

// Get a model from the registry (fully typed with known provider/model IDs)
var modelRegistry = new ModelRegistry();
BuiltInModels.Register(modelRegistry);
var model = modelRegistry.Get("openai", "gpt-4o-mini")
    ?? throw new InvalidOperationException("Model not found");

// Define tools with JSON Schema for type safety and validation
var tools = new List<Tool>
{
    new(
        Name: "get_time",
        Description: "Get the current time",
        Parameters: JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "timezone": {
                    "type": "string",
                    "description": "Optional timezone (e.g., America/New_York)"
                }
            }
        }
        """).RootElement
    )
};

// Build a conversation context (easily serializable and transferable between models)
var context = new Context(
    SystemPrompt: "You are a helpful assistant.",
    Messages: [new UserMessage("What time is it?", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())],
    Tools: tools
);

// Create the client (instance-based)
// Note: Register() wraps each provider in a GuardedProvider that validates
// the model's Api field matches the provider's expected API before delegating.
var apiProviderRegistry = new ApiProviderRegistry();
apiProviderRegistry.Register(new OpenAICompletionsProvider(httpClient, logger));
var llmClient = new LlmClient(apiProviderRegistry, modelRegistry);

// Option 1: Stream events as they arrive
var stream = llmClient.Stream(model, context);

await foreach (var evt in stream)
{
    switch (evt)
    {
        case StartEvent start:
            Console.WriteLine($"Starting with {start.Partial.ModelId}");
            break;
        case TextStartEvent:
            Console.WriteLine("\n[Text started]");
            break;
        case TextDeltaEvent delta:
            Console.Write(delta.Delta);
            break;
        case TextEndEvent textEnd:
            Console.WriteLine("\n[Text ended]");
            break;
        case ThinkingStartEvent:
            Console.WriteLine("[Model is thinking...]");
            break;
        case ThinkingDeltaEvent thinkDelta:
            Console.Write(thinkDelta.Delta);
            break;
        case ThinkingEndEvent:
            Console.WriteLine("[Thinking complete]");
            break;
        case ToolCallStartEvent:
            Console.WriteLine($"\n[Tool call started: index {evt}]");
            break;
        case ToolCallEndEvent toolEnd:
            Console.WriteLine($"Tool called: {toolEnd.ToolCall.Name}");
            break;
        case DoneEvent done:
            Console.WriteLine($"\nFinished: {done.Reason}");
            break;
        case ErrorEvent error:
            Console.Error.WriteLine($"Error: {error.Error.ErrorMessage}");
            break;
    }
}

// Get the final message after streaming, add it to the context
var finalMessage = await stream.GetResultAsync();
context = context with
{
    Messages = [.. context.Messages, finalMessage]
};

// Handle tool calls if any
var toolCalls = finalMessage.Content.OfType<ToolCallContent>().ToList();
foreach (var call in toolCalls)
{
    // Execute the tool
    var result = call.Name == "get_time"
        ? DateTime.UtcNow.ToString("F")
        : "Unknown tool";

    // Add tool result to context (supports text and images)
    context = context with
    {
        Messages = [.. context.Messages, new ToolResultMessage(
            ToolCallId: call.Id,
            ToolName: call.Name,
            Content: [new TextContent(result)],
            IsError: false,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        )]
    };
}

// Continue if there were tool calls
if (toolCalls.Count > 0)
{
    var continuation = await LlmClient.CompleteAsync(model, context);
    context = context with
    {
        Messages = [.. context.Messages, continuation]
    };
    Console.WriteLine("After tool execution:");
    foreach (var block in continuation.Content)
    {
        if (block is TextContent text)
            Console.WriteLine(text.Text);
    }
}

Console.WriteLine($"Total tokens: {finalMessage.Usage.Input} in, {finalMessage.Usage.Output} out");
Console.WriteLine($"Cost: ${finalMessage.Usage.Cost.Total:F4}");

// Option 2: Get complete response without streaming
var response = await LlmClient.CompleteAsync(model, context);

foreach (var block in response.Content)
{
    switch (block)
    {
        case TextContent text:
            Console.WriteLine(text.Text);
            break;
        case ToolCallContent toolCall:
            Console.WriteLine($"Tool: {toolCall.Name}({System.Text.Json.JsonSerializer.Serialize(toolCall.Arguments)})");
            break;
    }
}
```

## Tools

Tools enable LLMs to interact with external systems. Tool parameters use `System.Text.Json.JsonElement` for JSON Schema definitions, making them easily serializable and transferable.

### Defining Tools

```csharp
using System.Text.Json;
using BotNexus.Providers.Core.Models;

var weatherTool = new Tool(
    Name: "get_weather",
    Description: "Get current weather for a location",
    Parameters: JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "location": {
                "type": "string",
                "description": "City name or coordinates"
            },
            "units": {
                "type": "string",
                "enum": ["celsius", "fahrenheit"],
                "default": "celsius"
            }
        },
        "required": ["location"]
    }
    """).RootElement
);

var timeTool = new Tool(
    Name: "get_time",
    Description: "Get the current time",
    Parameters: JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "timezone": {
                "type": "string",
                "description": "Optional timezone (e.g., America/New_York)"
            }
        }
    }
    """).RootElement
);
```

### Handling Tool Calls

```csharp
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;

var context = new Context(
    SystemPrompt: "You are a helpful assistant.",
    Messages: [new UserMessage("What's the weather in London?", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())],
    Tools: [weatherTool]
);

var response = await LlmClient.CompleteAsync(model, context);
var messages = context.Messages.ToList();
messages.Add(response);

// Check for tool calls in the response
foreach (var block in response.Content)
{
    if (block is ToolCallContent toolCall)
    {
        // Execute your tool with the arguments
        var result = await ExecuteWeatherApi(toolCall.Arguments);

        // Add tool result with text content
        messages.Add(new ToolResultMessage(
            ToolCallId: toolCall.Id,
            ToolName: toolCall.Name,
            Content: [new TextContent(JsonSerializer.Serialize(result))],
            IsError: false,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        ));
    }
}

// Tool results can also include images (for vision-capable models)
messages.Add(new ToolResultMessage(
    ToolCallId: "tool_xyz",
    ToolName: "generate_chart",
    Content: [
        new TextContent("Generated chart showing temperature trends"),
        new ImageContent(
            Data: Convert.ToBase64String(File.ReadAllBytes("chart.png")),
            MimeType: "image/png"
        )
    ],
    IsError: false,
    Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
));

// Continue the conversation with tool results
var continuation = await LlmClient.CompleteAsync(
    model,
    context with { Messages = messages }
);
```

### Multi-Turn Tool Loop

A common pattern is a loop that runs until the model stops calling tools:

```csharp
var messages = new List<Message>
{
    new UserMessage("What's the weather in London and New York?",
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
};

while (true)
{
    var ctx = new Context("You are a helpful assistant.", messages, tools);
    var result = await LlmClient.CompleteAsync(model, ctx);
    messages.Add(result);

    if (result.StopReason != StopReason.ToolUse)
        break;

    // Process each tool call
    foreach (var block in result.Content)
    {
        if (block is ToolCallContent toolCall)
        {
            var toolResult = await ExecuteTool(toolCall.Name, toolCall.Arguments);
            messages.Add(new ToolResultMessage(
                ToolCallId: toolCall.Id,
                ToolName: toolCall.Name,
                Content: [new TextContent(toolResult)],
                IsError: false,
                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            ));
        }
    }
}
```

### Streaming Tool Calls with Partial JSON

During streaming, tool call arguments are progressively parsed as they arrive. This enables real-time UI updates before the complete arguments are available:

```csharp
var stream = LlmClient.Stream(model, context);

await foreach (var evt in stream)
{
    if (evt is ToolCallDeltaEvent delta)
    {
        var partial = delta.Partial.Content[delta.ContentIndex];
        if (partial is ToolCallContent tc)
        {
            // tc.Arguments contains partially parsed JSON during streaming
            // Fields may be missing or incomplete — always check before use
            Console.WriteLine($"[Streaming args for {tc.Name}]");
        }
    }

    if (evt is ToolCallEndEvent toolEnd)
    {
        // Here toolCall.Arguments is complete
        Console.WriteLine($"Tool completed: {toolEnd.ToolCall.Name}");
        Console.WriteLine($"Arguments: {JsonSerializer.Serialize(toolEnd.ToolCall.Arguments)}");
    }
}
```

> **Important:** During `ToolCallDeltaEvent`, `Arguments` contains a best-effort parse of partial JSON. Fields may be missing or incomplete — always check for existence before use. At minimum, `Arguments` will be an empty dictionary.

### Complete Event Reference

All streaming events emitted during assistant message generation:

| Event | When | Key Properties |
|-------|------|----------------|
| `StartEvent` | Stream begins | `Partial`: Initial assistant message structure |
| `TextStartEvent` | Text block starts | `ContentIndex`: Position in content array |
| `TextDeltaEvent` | Text chunk received | `Delta`: New text, `ContentIndex`: Position |
| `TextEndEvent` | Text block complete | `Content`: Full text, `ContentIndex`: Position |
| `ThinkingStartEvent` | Thinking block starts | `ContentIndex`: Position in content array |
| `ThinkingDeltaEvent` | Thinking chunk received | `Delta`: New text, `ContentIndex`: Position |
| `ThinkingEndEvent` | Thinking block complete | `Content`: Full thinking, `ContentIndex`: Position |
| `ToolCallStartEvent` | Tool call begins | `ContentIndex`: Position in content array |
| `ToolCallDeltaEvent` | Tool arguments streaming | `Delta`: JSON chunk, `Partial.Content[ContentIndex]`: Partial parsed args |
| `ToolCallEndEvent` | Tool call complete | `ToolCall`: Complete `ToolCallContent` with `Id`, `Name`, `Arguments` |
| `DoneEvent` | Stream complete | `Reason`: Stop reason, `Message`: Final `AssistantMessage` |
| `ErrorEvent` | Error occurred | `Reason`: Error type, `Error`: `AssistantMessage` with partial content |

## Image Input

Models with vision capabilities can process images. Check if a model supports images via its `Input` property. Images are included as `ImageContent` blocks within a `UserMessageContent`:

```csharp
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;

var model = ModelRegistry.GetModel("openai", "gpt-4o-mini")!;

// Check if model supports images
if (model.Input.Contains("image"))
    Console.WriteLine("Model supports vision");

var imageBytes = File.ReadAllBytes("image.png");
var base64Image = Convert.ToBase64String(imageBytes);

var context = new Context(
    SystemPrompt: null,
    Messages: [
        new UserMessage(
            Content: new UserMessageContent([
                new TextContent("What is in this image?"),
                new ImageContent(Data: base64Image, MimeType: "image/png")
            ]),
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        )
    ]
);

var response = await LlmClient.CompleteAsync(model, context);

foreach (var block in response.Content)
{
    if (block is TextContent text)
        Console.WriteLine(text.Text);
}
```

`UserMessageContent` supports both plain string and mixed content blocks:

```csharp
// Plain text (implicit conversion from string)
UserMessageContent textOnly = "Hello, what time is it?";

// Mixed content with text and images
var mixed = new UserMessageContent([
    new TextContent("Describe these two images"),
    new ImageContent(Data: base64Image1, MimeType: "image/png"),
    new ImageContent(Data: base64Image2, MimeType: "image/jpeg")
]);
```

## Thinking/Reasoning

Many models support thinking/reasoning capabilities where they can show their internal thought process. Check if a model supports reasoning via the `Reasoning` property. If you pass reasoning options to a non-reasoning model, they are silently ignored.

### Unified Interface (StreamSimple/CompleteSimple)

```csharp
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;

// Many models across providers support thinking/reasoning
var model = ModelRegistry.GetModel("anthropic", "claude-sonnet-4-20250514")!;
// or ModelRegistry.GetModel("openai", "o3-mini")

// Check if model supports reasoning
if (model.Reasoning)
    Console.WriteLine("Model supports reasoning/thinking");

// Use the simplified reasoning option
var response = await LlmClient.CompleteSimpleAsync(model, context,
    new SimpleStreamOptions
    {
        Reasoning = ThinkingLevel.Medium
        // ThinkingLevel: Minimal, Low, Medium, High, ExtraHigh
        // ExtraHigh maps to High on non-OpenAI providers
    });

// Access thinking and text blocks
foreach (var block in response.Content)
{
    switch (block)
    {
        case ThinkingContent thinking:
            Console.WriteLine($"Thinking: {thinking.Thinking}");
            break;
        case TextContent text:
            Console.WriteLine($"Response: {text.Text}");
            break;
    }
}
```

Custom token budgets per thinking level:

```csharp
var response = await LlmClient.CompleteSimpleAsync(model, context,
    new SimpleStreamOptions
    {
        Reasoning = ThinkingLevel.High,
        ThinkingBudgets = new ThinkingBudgets
        {
            Minimal = 1024,
            Low = 2048,
            Medium = 4096,
            High = 8192,
            ExtraHigh = 16384,
        }
    });
```

### Provider-Specific Options (Stream/Complete)

For fine-grained control, use provider-specific option types:

```csharp
using BotNexus.Providers.Anthropic;
using BotNexus.Providers.OpenAI;

// Anthropic Thinking (Claude Sonnet 4, Opus 4)
var anthropicModel = ModelRegistry.GetModel("anthropic", "claude-sonnet-4-20250514")!;
var anthropicResult = await LlmClient.CompleteAsync(anthropicModel, context,
    new AnthropicOptions
    {
        ThinkingEnabled = true,
        ThinkingBudgetTokens = 8192, // Optional token limit
        Effort = "high",             // Adaptive thinking: "low", "medium", "high", "max"
        InterleavedThinking = true,  // Thinking blocks between text/tool blocks
        ToolChoice = "auto",         // String shorthand: "auto", "any", "none", or a tool name
        // Or pass a full Anthropic tool_choice object for parallel control:
        // ToolChoice = new Dictionary<string, object?> { ["type"] = "auto", ["disable_parallel_tool_use"] = true }
    });

// OpenAI Reasoning (o3, GPT-4o)
var openaiModel = ModelRegistry.GetModel("openai", "o3-mini")!;
var openaiResult = await LlmClient.CompleteAsync(openaiModel, context,
    new OpenAICompletionsOptions
    {
        ReasoningEffort = "medium" // "low", "medium", "high"
    });
```

### Streaming Thinking Content

When streaming, thinking content is delivered through specific events:

```csharp
var stream = LlmClient.StreamSimple(model, context,
    new SimpleStreamOptions { Reasoning = ThinkingLevel.High });

await foreach (var evt in stream)
{
    switch (evt)
    {
        case ThinkingStartEvent:
            Console.WriteLine("[Model started thinking]");
            break;
        case ThinkingDeltaEvent delta:
            Console.Write(delta.Delta); // Stream thinking content
            break;
        case ThinkingEndEvent:
            Console.WriteLine("\n[Thinking complete]");
            break;
    }
}
```

## Stop Reasons

Every `AssistantMessage` includes a `StopReason` field that indicates how generation ended:

| Value | Description |
|-------|-------------|
| `StopReason.Stop` | Normal completion — the model finished its response |
| `StopReason.Length` | Output hit the maximum token limit |
| `StopReason.ToolUse` | Model is calling tools and expects tool results |
| `StopReason.Error` | An error occurred during generation |
| `StopReason.Aborted` | Request was cancelled via `CancellationToken` |
| `StopReason.Refusal` | Model refused to generate content |
| `StopReason.Sensitive` | Content flagged as sensitive |

`AssistantMessage` may also include `ResponseId`, a provider-specific upstream response or message identifier when the underlying API exposes one. Do not assume it is always present across providers.

## Error Handling

When a request ends with an error (including cancellations), the streaming API emits an `ErrorEvent`:

```csharp
var stream = LlmClient.Stream(model, context);

await foreach (var evt in stream)
{
    if (evt is ErrorEvent error)
    {
        // error.Reason is StopReason.Error or StopReason.Aborted
        Console.Error.WriteLine($"Error ({error.Reason}): {error.Error.ErrorMessage}");
        Console.WriteLine($"Partial content: {error.Error.Content.Count} blocks");
    }
}

// The final message will have the error details
var message = await stream.GetResultAsync();
if (message.StopReason is StopReason.Error or StopReason.Aborted)
{
    Console.Error.WriteLine($"Request failed: {message.ErrorMessage}");
    // message.Content contains any partial content received before the error
    // message.Usage contains partial token counts and costs
}
```

### Cancellation with CancellationToken

Use `CancellationToken` to cancel in-progress requests. Cancelled requests have `StopReason.Aborted`:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

var stream = LlmClient.Stream(model, context,
    new StreamOptions { CancellationToken = cts.Token });

await foreach (var evt in stream)
{
    if (evt is TextDeltaEvent delta)
        Console.Write(delta.Delta);
    else if (evt is ErrorEvent error)
        Console.WriteLine($"{(error.Reason == StopReason.Aborted ? "Aborted" : "Error")}: {error.Error.ErrorMessage}");
}

// Get results (may be partial if aborted)
var result = await stream.GetResultAsync();
if (result.StopReason == StopReason.Aborted)
{
    Console.WriteLine($"Request was aborted: {result.ErrorMessage}");
    Console.WriteLine($"Partial content received: {result.Content.Count} blocks");
    Console.WriteLine($"Tokens used: {result.Usage.TotalTokens}");
}
```

### Continuing After Cancellation

Aborted messages can be added to the conversation context and continued in subsequent requests:

```csharp
var messages = new List<Message>
{
    new UserMessage("Explain quantum computing in detail",
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
};

// First request gets cancelled after 2 seconds
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
var partial = await LlmClient.CompleteAsync(model,
    new Context(null, messages),
    new StreamOptions { CancellationToken = cts.Token });

// Add the partial response to context
messages.Add(partial);
messages.Add(new UserMessage("Please continue",
    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

// Continue the conversation
var continuation = await LlmClient.CompleteAsync(model, new Context(null, messages));
```

### Debugging Provider Payloads

Use the `OnPayload` callback to inspect the raw JSON payload sent to the provider:

```csharp
var response = await LlmClient.CompleteAsync(model, context,
    new StreamOptions
    {
        OnPayload = async (payload, model) =>
        {
            Console.WriteLine($"Provider payload: {payload}");
            return null; // Return modified payload or null to keep original
        }
    });
```

The callback is supported by `Stream`, `CompleteAsync`, `StreamSimple`, and `CompleteSimpleAsync`.

## APIs, Models, and Providers

The library uses a registry of API implementations. Built-in APIs include:

| API | Namespace | Provider-specific options |
|-----|-----------|--------------------------|
| `openai-completions` | `BotNexus.Providers.OpenAI` | `OpenAICompletionsOptions` |
| `openai-responses` | `BotNexus.Providers.OpenAI` | `OpenAIResponsesOptions` |
| `anthropic-messages` | `BotNexus.Providers.Anthropic` | `AnthropicOptions` |
| `openai-compat` | `BotNexus.Providers.OpenAICompat` | `OpenAICompatOptions` |

> **Note:** `CopilotProvider` (in `BotNexus.Providers.Copilot`) is a **static utility** — not an `IApiProvider` implementation. Copilot models route through `anthropic-messages` or `openai-completions`/`openai-responses` depending on the model.

### Providers and Models

A provider offers models through a specific API. For example:

- **Anthropic** models use the `anthropic-messages` API
- **OpenAI** models use the `openai-completions` API
- **GitHub Copilot** models route through `anthropic-messages` or `openai-completions`/`openai-responses` (Copilot is a static utility, not a separate API)
- **Ollama, vLLM, LM Studio, SGLang** models use the `openai-compat` API

### Querying Providers and Models

```csharp
using BotNexus.Providers.Core.Registry;

// Get all registered providers
var providers = ModelRegistry.GetProviders();
Console.WriteLine(string.Join(", ", providers)); // openai, anthropic, ...

// Get all models from a provider
var anthropicModels = ModelRegistry.GetModels("anthropic");
foreach (var model in anthropicModels)
{
    Console.WriteLine($"{model.Id}: {model.Name}");
    Console.WriteLine($"  API: {model.Api}");               // "anthropic-messages"
    Console.WriteLine($"  Context: {model.ContextWindow} tokens");
    Console.WriteLine($"  Vision: {model.Input.Contains("image")}");
    Console.WriteLine($"  Reasoning: {model.Reasoning}");
}

// Get a specific model
var model = ModelRegistry.GetModel("openai", "gpt-4o-mini");
if (model is not null)
    Console.WriteLine($"Using {model.Name} via {model.Api} API");

// Calculate cost from usage
var cost = ModelRegistry.CalculateCost(model!, usage);
Console.WriteLine($"Cost: ${cost.Total:F6}");
```

### Custom Models

Create custom `LlmModel` instances for local inference servers or custom endpoints:

```csharp
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Compatibility;

// Ollama using OpenAI-compatible API
var ollamaModel = new LlmModel(
    Id: "llama-3.1-8b",
    Name: "Llama 3.1 8B (Ollama)",
    Api: "openai-compat",
    Provider: "ollama",
    BaseUrl: "http://localhost:11434/v1",
    Reasoning: false,
    Input: ["text"],
    Cost: new ModelCost(0, 0, 0, 0),
    ContextWindow: 128000,
    MaxTokens: 32000,
    Compat: new OpenAICompletionsCompat
    {
        SupportsDeveloperRole = false,
        SupportsReasoningEffort = false,
        SupportsStore = false,
        SupportsUsageInStreaming = false,
        MaxTokensField = "max_tokens",
        SupportsStrictMode = false,
        RequiresToolResultName = true,
    }
);

// Custom endpoint with headers
var proxyModel = new LlmModel(
    Id: "claude-sonnet-4",
    Name: "Claude Sonnet 4 (Proxied)",
    Api: "anthropic-messages",
    Provider: "custom-proxy",
    BaseUrl: "https://proxy.example.com/v1",
    Reasoning: true,
    Input: ["text", "image"],
    Cost: new ModelCost(Input: 3, Output: 15, CacheRead: 0.3m, CacheWrite: 3.75m),
    ContextWindow: 200000,
    MaxTokens: 8192,
    Headers: new Dictionary<string, string>
    {
        ["User-Agent"] = "Mozilla/5.0",
        ["X-Custom-Auth"] = "bearer-token-here"
    }
);

// Use the custom model — no registration required
var response = await LlmClient.CompleteAsync(ollamaModel, context,
    new StreamOptions { ApiKey = "dummy" }); // Ollama doesn't need a real key
```

#### PreConfiguredModels Helpers

For common local servers, use the `PreConfiguredModels` factory methods — they set compat flags automatically:

```csharp
using BotNexus.Providers.OpenAICompat;

var ollama = PreConfiguredModels.Ollama("llama-3.1-8b");
var vllm = PreConfiguredModels.VLlm("mistral-7b", baseUrl: "http://localhost:8000/v1");
var lmStudio = PreConfiguredModels.LMStudio("phi-3-mini");
var sglang = PreConfiguredModels.SGLang("llama-3.1-70b", baseUrl: "http://localhost:30000/v1");

// Each returns a fully configured LlmModel with correct compat settings
var response = await LlmClient.CompleteAsync(ollama, context);
```

### OpenAI Compatibility Settings

The `openai-compat` API is implemented by many servers with minor differences. The library auto-detects compatibility settings based on `BaseUrl` and `Provider` via `CompatDetector`. For custom proxies or unknown endpoints, override via the `Compat` field on the model:

```csharp
using BotNexus.Providers.Core.Compatibility;

// All available compat settings
var compat = new OpenAICompletionsCompat
{
    SupportsStore = true,                // Whether provider supports the `store` field
    SupportsDeveloperRole = true,        // Whether provider supports `developer` role vs `system`
    SupportsReasoningEffort = true,      // Whether provider supports `reasoning_effort`
    SupportsUsageInStreaming = true,      // Whether streaming includes usage data
    SupportsStrictMode = true,           // Whether provider supports `strict` in tool definitions
    MaxTokensField = "max_completion_tokens", // or "max_tokens" for older APIs
    RequiresToolResultName = false,      // Whether tool results require the `name` field
    RequiresAssistantAfterToolResult = false, // Whether tool results must be followed by assistant
    RequiresThinkingAsText = false,      // Whether thinking blocks must be converted to text
    ThinkingFormat = "openai",           // "openai" uses reasoning_effort
    ReasoningEffortMap = null,           // Custom ThinkingLevel → string mapping
};
```

Auto-detected servers include: Ollama, vLLM, LM Studio, SGLang, Cerebras, xAI, DeepSeek, and more. If `Compat` is not set on the model, `CompatDetector.Detect()` infers settings from the `BaseUrl` and `Provider`.

## Cross-Provider Handoffs

The library supports seamless handoffs between different LLM providers within the same conversation. `MessageTransformer` automatically transforms messages for compatibility when switching providers.

### How It Works

- **User and tool result messages** are passed through unchanged
- **Assistant messages from the same provider/API** are preserved as-is
- **Assistant messages from different providers** have their thinking blocks converted to text with `<thinking>` tags
- **Tool calls and regular text** are preserved unchanged
- **Errored/aborted assistant messages** are skipped
- **Orphaned tool calls** (no matching tool result) get synthetic error results inserted

### Example: Multi-Provider Conversation

```csharp
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;

var claude = ModelRegistry.GetModel("anthropic", "claude-sonnet-4-20250514")!;
var gpt4o = ModelRegistry.GetModel("openai", "gpt-4o-mini")!;

var messages = new List<Message>();
var ts = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

// Start with Claude
messages.Add(new UserMessage("What is 25 * 18?", ts()));
var claudeResponse = await LlmClient.CompleteSimpleAsync(claude,
    new Context(null, messages),
    new SimpleStreamOptions { Reasoning = ThinkingLevel.Medium });
messages.Add(claudeResponse);

// Switch to GPT-4o — it will see Claude's thinking as <thinking> tagged text
messages.Add(new UserMessage("Is that calculation correct?", ts()));
var gptResponse = await LlmClient.CompleteAsync(gpt4o, new Context(null, messages));
messages.Add(gptResponse);
```

The transformation is handled automatically by `MessageTransformer.TransformMessages()`, which each provider calls internally when building requests.

## Context Serialization

The `Context` record and all message types use `System.Text.Json` polymorphic serialization, making them easy to persist:

```csharp
using System.Text.Json;
using BotNexus.Providers.Core.Models;

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

// Build and use a context
var context = new Context(
    SystemPrompt: "You are a helpful assistant.",
    Messages: [new UserMessage("What is TypeScript?", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]
);

var response = await LlmClient.CompleteAsync(model, context);

// Add response, creating new context (records are immutable)
var updatedContext = context with
{
    Messages = [..context.Messages, response]
};

// Serialize the entire context
var serialized = JsonSerializer.Serialize(updatedContext, jsonOptions);
Console.WriteLine($"Serialized context size: {serialized.Length} bytes");

// Save to database, file, etc.
File.WriteAllText("conversation.json", serialized);

// Later: deserialize and continue the conversation
var restored = JsonSerializer.Deserialize<Context>(
    File.ReadAllText("conversation.json"), jsonOptions)!;

var continued = restored with
{
    Messages = [..restored.Messages,
        new UserMessage("Tell me more about its type system",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]
};

// Continue with any model — cross-provider handoffs are automatic
var continuation = await LlmClient.CompleteAsync(
    ModelRegistry.GetModel("anthropic", "claude-sonnet-4-20250514")!,
    continued);
```

> **Note:** Messages use `[JsonPolymorphic]` with a `"role"` discriminator (`"user"`, `"assistant"`, `"toolResult"`), and content blocks use `"type"` (`"text"`, `"thinking"`, `"image"`, `"toolCall"`). This ensures round-trip fidelity including thinking blocks, tool calls, and images.

## OAuth Providers

### GitHub Copilot (Device Code Flow)

`CopilotOAuth` provides GitHub device code OAuth flow for Copilot access:

```csharp
using BotNexus.Providers.Copilot;

// Step 1: Login via device code flow
var credentials = await CopilotOAuth.LoginAsync(
    onAuth: async (verificationUri, userCode) =>
    {
        Console.WriteLine($"Open: {verificationUri}");
        Console.WriteLine($"Enter code: {userCode}");
    },
    onProgress: message => Console.WriteLine(message)
);

// Step 2: Store credentials (OAuthCredentials is a record)
// credentials.AccessToken — the GitHub OAuth token
// credentials.ExpiresAt — Unix epoch seconds

// Step 3: Refresh when expired
if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= credentials.ExpiresAt - 60)
{
    credentials = await CopilotOAuth.RefreshAsync(credentials);
}

// Step 4: Use with CopilotProvider
var model = ModelRegistry.GetModel("github-copilot", "gpt-4o")!;
var response = await LlmClient.CompleteAsync(model, context,
    new StreamOptions { ApiKey = credentials.AccessToken });
```

For credential management with a stored credentials map:

```csharp
// GetApiKeyAsync handles auto-refresh internally
var result = await CopilotOAuth.GetApiKeyAsync(
    "github-copilot", credentialsMap);

if (result is { } r)
{
    credentialsMap["github-copilot"] = r.NewCredentials; // Save refreshed
    var response = await LlmClient.CompleteAsync(model, context,
        new StreamOptions { ApiKey = r.ApiKey });
}
```

## Environment Variables

API keys are resolved from environment variables via `EnvironmentApiKeys.GetApiKey()`:

| Provider | Environment Variable(s) |
|----------|------------------------|
| `openai` | `OPENAI_API_KEY` |
| `anthropic` | `ANTHROPIC_OAUTH_TOKEN` (preferred), `ANTHROPIC_API_KEY` |
| `github-copilot` | `COPILOT_GITHUB_TOKEN`, `GH_TOKEN`, `GITHUB_TOKEN` (first found) |
| `google` | `GEMINI_API_KEY` |
| `groq` | `GROQ_API_KEY` |
| `cerebras` | `CEREBRAS_API_KEY` |
| `xai` | `XAI_API_KEY` |
| `openrouter` | `OPENROUTER_API_KEY` |
| `mistral` | `MISTRAL_API_KEY` |
| `azure-openai-responses` | `AZURE_OPENAI_API_KEY` |
| `vercel-ai-gateway` | `AI_GATEWAY_API_KEY` |
| `zai` | `ZAI_API_KEY` |
| `minimax` | `MINIMAX_API_KEY` |
| `huggingface` | `HF_TOKEN` |

When set, the library automatically uses these keys:

```csharp
// Uses OPENAI_API_KEY from environment
var model = ModelRegistry.GetModel("openai", "gpt-4o-mini")!;
var response = await LlmClient.CompleteAsync(model, context);

// Or override with an explicit key
var response2 = await LlmClient.CompleteAsync(model, context,
    new StreamOptions { ApiKey = "sk-different-key" });
```

## Project Structure

```
src/providers/
  BotNexus.Providers.Core/               — Types, registries, streaming, utilities
    Models/
      Context.cs                          — Context record (SystemPrompt, Messages, Tools)
      Messages.cs                         — UserMessage, AssistantMessage, ToolResultMessage
      ContentBlock.cs                     — TextContent, ThinkingContent, ImageContent, ToolCallContent
      LlmModel.cs                         — LlmModel, ModelCost records
      Tool.cs                             — Tool definition with JSON Schema
      Enums.cs                            — StopReason, ThinkingLevel, CacheRetention, Transport
      Usage.cs                            — Usage, UsageCost tracking
      UserMessageContent.cs               — String-or-blocks union type
      ThinkingBudgets.cs                  — Per-level thinking token budgets
    Registry/
      IApiProvider.cs                     — Provider contract (Stream, StreamSimple)
      ApiProviderRegistry.cs              — Global API provider registry
      ModelRegistry.cs                    — Global model registry with cost calculation
    Streaming/
      LlmStream.cs                        — IAsyncEnumerable stream primitive
      AssistantMessageEvent.cs            — All streaming event types
    Compatibility/
      OpenAICompletionsCompat.cs          — Compat flags for OpenAI-compatible servers
    Utilities/
      MessageTransformer.cs               — Cross-provider message transformation
      SimpleOptionsHelper.cs              — Thinking budget resolution
      CopilotHeaders.cs                   — GitHub Copilot dynamic headers
      ContextOverflowDetector.cs          — Regex-based context overflow detection
      StreamingJsonParser.cs              — Partial JSON parsing for tool arg streaming
      UnicodeSanitizer.cs                 — Unpaired surrogate sanitization
    LlmClient.cs                          — Top-level Stream/Complete/StreamSimple/CompleteSimple
    StreamOptions.cs                      — StreamOptions, SimpleStreamOptions
    EnvironmentApiKeys.cs                 — Provider → env var resolution

  BotNexus.Providers.OpenAI/             — OpenAI Chat Completions + Responses API
    OpenAICompletionsProvider.cs          — IApiProvider implementation (Chat Completions)
    OpenAICompletionsOptions.cs           — ToolChoice, ReasoningEffort
    OpenAIResponsesProvider.cs            — IApiProvider implementation (Responses API)
    OpenAIResponsesOptions.cs             — Responses API options

  BotNexus.Providers.Anthropic/          — Anthropic Messages API
    AnthropicProvider.cs                  — IApiProvider implementation
    AnthropicOptions.cs                   — ThinkingEnabled, Effort, InterleavedThinking, ToolChoice

  BotNexus.Providers.Copilot/            — GitHub Copilot (OAuth)
    CopilotProvider.cs                    — Static utility with ResolveApiKey/ApplyDynamicHeaders
    CopilotOAuth.cs                       — Device code flow, token exchange, refresh

  BotNexus.Providers.OpenAICompat/       — Ollama, vLLM, LM Studio, SGLang, etc.
    OpenAICompatProvider.cs               — IApiProvider with auto-detected compat
    OpenAICompatOptions.cs                — ToolChoice, ReasoningEffort
    CompatDetector.cs                     — URL/provider-based compat detection
    PreConfiguredModels.cs                — Factory methods: Ollama(), VLlm(), LMStudio(), SGLang()
```

## Adding a New Provider

Adding a new LLM provider requires changes across multiple files. This checklist covers all steps:

### 1. Provider Implementation

Create a new project `BotNexus.Providers.<Name>/` with a class implementing `IApiProvider`:

```csharp
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.Core.Streaming;

public sealed class MyProvider : IApiProvider
{
    public string Api => "my-api";

    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
    {
        var stream = new LlmStream();
        _ = Task.Run(async () =>
        {
            // Build request, make HTTP call, parse SSE, push events
            // stream.Push(new StartEvent(...));
            // stream.Push(new TextDeltaEvent(...));
            // stream.Push(new DoneEvent(...));
            // stream.End(finalMessage);
        });
        return stream;
    }

    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        var apiKey = options?.ApiKey ?? EnvironmentApiKeys.GetApiKey(model.Provider) ?? "";
        var baseOptions = SimpleOptionsHelper.BuildBaseOptions(model, options, apiKey);
        // Map SimpleStreamOptions.Reasoning to provider-specific fields
        return Stream(model, context, baseOptions);
    }
}
```

### 2. Register the Provider

```csharp
ApiProviderRegistry.Register(new MyProvider());
```

### 3. Register Models

```csharp
ModelRegistry.Register("my-provider", new LlmModel(
    Id: "my-model-v1",
    Name: "My Model v1",
    Api: "my-api",
    Provider: "my-provider",
    BaseUrl: "https://api.my-provider.com/v1",
    Reasoning: false,
    Input: ["text", "image"],
    Cost: new ModelCost(Input: 1.0m, Output: 2.0m, CacheRead: 0, CacheWrite: 0),
    ContextWindow: 128000,
    MaxTokens: 8192
));
```

### 4. Add Environment Variable

Add the provider's API key env var to `EnvironmentApiKeys.cs`:

```csharp
["my-provider"] = "MY_PROVIDER_API_KEY",
```

### 5. Message Transformation

Use `MessageTransformer.TransformMessages()` when building requests to handle cross-provider context:

```csharp
var messages = MessageTransformer.TransformMessages(context.Messages, model);
```

### 6. Tests

Create tests covering:

- Basic streaming and tool use
- Token usage reporting
- Request cancellation
- Cross-provider handoff
- Error handling

## License

See the root [README.md](../../README.md) for license information.
