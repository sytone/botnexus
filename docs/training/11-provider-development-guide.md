# 11 — Provider Development Guide

This document walks through implementing a new `IApiProvider` from scratch. By the end, you'll understand SSE parsing, message conversion, testing patterns, and provider-specific quirks.

> **Prerequisites:** [Provider System](01-providers.md), [Architecture Overview](../architecture/overview.md), familiarity with HTTP APIs and Server-Sent Events (SSE).

---

## Overview: what a provider must do

A provider is a thin HTTP adapter. At minimum, it must:

1. **Implement `IApiProvider`** — define `Api` property and `Stream()` method
2. **Translate BotNexus format to vendor format** — convert `Context` to vendor JSON
3. **Open HTTP connection** — POST request with proper auth headers
4. **Parse SSE stream** — read `event:` and `data:` lines from response
5. **Build `AssistantMessageEvent` instances** — push into `LlmStream`
6. **Emit final result** — `DoneEvent` with complete `AssistantMessage` or `ErrorEvent`

---

## Part 1: Core skeleton

Here's the minimum scaffold:

```csharp
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Providers.MyProvider;

public sealed class MyProvider(HttpClient httpClient) : IApiProvider
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public string Api => "my-api-format";

    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
    {
        var stream = new LlmStream();
        var ct = options?.CancellationToken ?? CancellationToken.None;

        // Start streaming asynchronously so we return immediately
        _ = Task.Run(async () =>
        {
            try
            {
                await StreamCoreAsync(stream, model, context, options, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                var msg = BuildErrorMessage(model, "Cancelled by caller");
                stream.Push(new ErrorEvent(StopReason.Aborted, msg));
                stream.End(msg);
            }
            catch (Exception ex)
            {
                var msg = BuildErrorMessage(model, ex.Message);
                stream.Push(new ErrorEvent(StopReason.Error, msg));
                stream.End(msg);
            }
        }, ct);

        return stream;
    }

    private async Task StreamCoreAsync(
        LlmStream stream, LlmModel model, Context context,
        StreamOptions? options, CancellationToken ct)
    {
        // 1. Validate API key
        // 2. Build request JSON
        // 3. POST and get HTTP response
        // 4. Parse SSE stream
        // 5. Push events into LlmStream
    }

    private AssistantMessage BuildErrorMessage(LlmModel model, string errorText)
    {
        return new AssistantMessage(
            Content: new[] { new TextContent(errorText) as ContentBlock },
            Api: Api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Error,
            ErrorMessage: errorText,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );
    }

    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        // For now, ignore reasoning and delegate to Stream()
        // Real providers map reasoning to their vendor format
        var baseOptions = options == null ? null : new StreamOptions
        {
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            CancellationToken = options.CancellationToken,
            ApiKey = options.ApiKey,
            // ... other fields ...
        };
        return Stream(model, context, baseOptions);
    }
}
```

---

## Part 2: Build the request JSON

Each vendor has a different schema. Here's how to convert BotNexus `Context` to your vendor's format.

### Example: Anthropic format

```csharp
private Dictionary<string, object?> BuildRequestBody(
    LlmModel model, Context context, StreamOptions? options, AnthropicOptions? anthropicOpts)
{
    var requestBody = new Dictionary<string, object?>
    {
        ["model"] = model.Id,
        ["max_tokens"] = options?.MaxTokens ?? 4096,
        ["stream"] = true,
    };

    // System prompt
    if (!string.IsNullOrWhiteSpace(context.SystemPrompt))
    {
        requestBody["system"] = context.SystemPrompt;
    }

    // Messages: convert BotNexus Message[] to Anthropic format
    var messages = ConvertMessages(context.Messages);
    requestBody["messages"] = messages;

    // Tools: convert BotNexus Tool[] to Anthropic format
    if (context.Tools.Count > 0)
    {
        requestBody["tools"] = ConvertTools(context.Tools);
    }

    // Temperature
    if (options?.Temperature.HasValue == true)
    {
        requestBody["temperature"] = options.Temperature.Value;
    }

    // Thinking (Anthropic-specific)
    if (anthropicOpts?.ThinkingEnabled == true)
    {
        requestBody["thinking"] = new
        {
            type = "enabled",
            budget_tokens = anthropicOpts.ThinkingBudgetTokens,
        };
    }

    // Tool choice (Anthropic-specific): string shorthand or full object
    if (anthropicOpts?.ToolChoice is { } toolChoice)
    {
        requestBody["tool_choice"] = BuildToolChoiceNode(toolChoice);
        // Accepts: "auto", "any", "none", a tool name,
        //   or a Dictionary/JsonNode/JsonElement for full control
        //   (e.g., { "type": "auto", "disable_parallel_tool_use": true })
    }

    return requestBody;
}

// Convert BotNexus Message[] to Anthropic format:
// [{ "role": "user", "content": "..." }, { "role": "assistant", "content": [...] }, ...]
private List<object> ConvertMessages(IReadOnlyList<Message> messages)
{
    var result = new List<object>();

    foreach (var msg in messages)
    {
        switch (msg)
        {
            case UserMessage um:
                result.Add(new
                {
                    role = "user",
                    content = ConvertUserContent(um.Content)
                });
                break;

            case AssistantMessage am:
                var assistantContent = am.Content
                    .Select(block => ConvertContentBlock(block))
                    .ToList();
                result.Add(new
                {
                    role = "assistant",
                    content = assistantContent
                });
                break;

            case ToolResultMessage tm:
                var toolContent = new List<object>
                {
                    new
                    {
                        type = "tool_result",
                        tool_use_id = tm.ToolCallId,
                        content = ConvertToolResultContent(tm.Content),
                        is_error = tm.IsError
                    }
                };
                result.Add(new
                {
                    role = "user",
                    content = toolContent
                });
                break;
        }
    }

    return result;
}

private object? ConvertUserContent(UserMessageContent content)
{
    // content can be a string or ContentBlock[]
    if (content is string str)
    {
        return new[] { new { type = "text", text = str } };
    }

    if (content is ContentBlock[] blocks)
    {
        return blocks.Select(b => ConvertContentBlock(b)).ToArray();
    }

    return null;
}

private object ConvertContentBlock(ContentBlock block)
{
    return block switch
    {
        TextContent tc => new { type = "text", text = tc.Text },
        
        ImageContent ic => new
        {
            type = "image",
            source = new
            {
                type = ic.Format switch
                {
                    "base64" => "base64",
                    "url" => "url",
                    _ => "url"
                },
                media_type = ic.MediaType ?? "image/jpeg",
                data = ic.Source
            }
        },
        
        ThinkingContent thc => new { type = "thinking", thinking = thc.Thinking },
        
        ToolCallContent tcc => new
        {
            type = "tool_use",
            id = tcc.Id,
            name = tcc.Name,
            input = tcc.Arguments,
            signature = tcc.ThoughtSignature  // included when present (Anthropic thinking continuations)
        },

        _ => new { type = "text", text = block.ToString() }
    };
}

// Convert BotNexus Tool[] to Anthropic tool schema
private List<object> ConvertTools(IReadOnlyList<Tool> tools)
{
    var result = new List<object>();

    foreach (var tool in tools)
    {
        result.Add(new
        {
            name = tool.Name,
            description = tool.Description,
            input_schema = tool.Parameters // Anthropic uses raw JSON Schema
        });
    }

    return result;
}
```

---

## Part 3: HTTP request and headers

```csharp
private async Task StreamCoreAsync(
    LlmStream stream, LlmModel model, Context context,
    StreamOptions? options, CancellationToken ct)
{
    // 1. Get API key
    var apiKey = options?.ApiKey 
        ?? EnvironmentApiKeys.GetApiKey(model.Provider) 
        ?? throw new InvalidOperationException($"No API key for {model.Provider}");

    // 2. Build request body
    var requestBody = BuildRequestBody(model, context, options);

    // 3. Serialize to JSON
    var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    });

    // 4. Create HTTP request
    using var httpRequest = new HttpRequestMessage(HttpMethod.Post, model.BaseUrl);
    httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

    // 5. Set headers
    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
    
    if (model.Headers is not null)
    {
        foreach (var (key, value) in model.Headers)
        {
            httpRequest.Headers.TryAddWithoutValidation(key, value);
        }
    }

    // 6. Send request with streaming response
    using var response = await _httpClient.SendAsync(
        httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

    if (!response.IsSuccessStatusCode)
    {
        var errorBody = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"API returned {(int)response.StatusCode}: {errorBody}");
    }

    // 7. Parse SSE stream (see Part 4)
    await ParseSseStreamAsync(stream, response, model, ct);
}
```

---

## Part 4: Parse Server-Sent Events (SSE)

SSE is a simple line-based format:

```
event: message_start
data: {"type":"message_start","message":{"id":"msg_1"}}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text"}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" world"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":13}}

event: message_stop
data: {"type":"message_stop"}
```

Here's how to parse it:

```csharp
private async Task ParseSseStreamAsync(
    LlmStream stream, HttpResponseMessage response,
    LlmModel model, CancellationToken ct)
{
    using var responseStream = await response.Content.ReadAsStreamAsync(ct);
    using var reader = new StreamReader(responseStream, Encoding.UTF8);

    var contentBlocks = new List<ContentBlock>();
    var usage = Usage.Empty();
    var stopReason = StopReason.Stop;
    string? responseId = null;
    string? currentEventType = null;

    // Emit start event
    stream.Push(new StartEvent(DateTimeOffset.UtcNow));

    while (!ct.IsCancellationRequested)
    {
        var line = await reader.ReadLineAsync(ct);
        if (line is null) break;

        // SSE format: lines start with "event:" or "data:"
        if (line.StartsWith("event:", StringComparison.Ordinal))
        {
            currentEventType = line[6..].Trim();
            continue;
        }

        if (line.StartsWith("data:", StringComparison.Ordinal))
        {
            var data = line[5..].Trim();
            if (string.IsNullOrWhiteSpace(data)) continue;

            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(data); }
            catch { continue; } // Malformed JSON, skip

            using (doc)
            {
                ProcessSseEvent(stream, currentEventType, doc.RootElement, 
                    ref contentBlocks, ref usage, ref stopReason, ref responseId);
            }
        }
    }

    // Emit final message
    var final = new AssistantMessage(
        Content: contentBlocks,
        Api: Api,
        Provider: model.Provider,
        ModelId: model.Id,
        Usage: usage,
        StopReason: stopReason,
        ErrorMessage: null,
        ResponseId: responseId,
        Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    );

    stream.Push(new DoneEvent(stopReason, final));
    stream.End(final);
}

private void ProcessSseEvent(
    LlmStream stream, string? eventType, JsonElement data,
    ref List<ContentBlock> contentBlocks, ref Usage usage,
    ref StopReason stopReason, ref string? responseId)
{
    if (eventType == "content_block_start")
    {
        // Anthropic: { "type":"content_block_start", "index":0, "content_block":{"type":"text"} }
        var index = data.GetProperty("index").GetInt32();
        var blockType = data.GetProperty("content_block").GetProperty("type").GetString();

        stream.Push(new TextStartEvent(DateTimeOffset.UtcNow));
    }

    if (eventType == "content_block_delta")
    {
        // Anthropic: { "type":"content_block_delta", "index":0, "delta":{"type":"text_delta","text":"..."} }
        var deltaType = data.GetProperty("delta").GetProperty("type").GetString();

        if (deltaType == "text_delta")
        {
            var text = data.GetProperty("delta").GetProperty("text").GetString() ?? "";
            stream.Push(new TextDeltaEvent(text, DateTimeOffset.UtcNow));
        }

        if (deltaType == "thinking_delta")
        {
            var thinking = data.GetProperty("delta").GetProperty("thinking").GetString() ?? "";
            stream.Push(new ThinkingDeltaEvent(thinking, DateTimeOffset.UtcNow));
        }

        if (deltaType == "tool_use_delta")
        {
            var args = data.GetProperty("delta").GetProperty("input").GetString() ?? "";
            // Accumulate tool call arguments
            stream.Push(new ToolCallDeltaEvent(args, DateTimeOffset.UtcNow));
        }
    }

    if (eventType == "message_delta")
    {
        // Anthropic: { "type":"message_delta", "delta":{"stop_reason":"end_turn"}, "usage":{...} }
        var stopReasonStr = data
            .GetProperty("delta")
            .GetProperty("stop_reason")
            .GetString() ?? "stop";

        stopReason = stopReasonStr switch
        {
            "end_turn" => StopReason.Stop,
            "max_tokens" => StopReason.Length,
            "tool_use" => StopReason.ToolUse,
            "refusal" => StopReason.Refusal,        // Phase 4: explicit refusal mapping
            "content_policy" => StopReason.Sensitive, // Phase 4: explicit content sensitivity
            "safety" => StopReason.Sensitive,         // Phase 4: explicit content sensitivity
            "sensitive" => StopReason.Sensitive,      // Phase 4: explicit content sensitivity
            _ => StopReason.Stop
        };

        if (data.TryGetProperty("usage", out var usageElem))
        {
            usage = ParseUsage(usageElem);
        }
    }
}

private Usage ParseUsage(JsonElement usageElem)
{
    return new Usage(
        Input: usageElem.GetProperty("input_tokens").GetInt32(),
        Output: usageElem.GetProperty("output_tokens").GetInt32(),
        CacheRead: usageElem.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : 0,
        CacheWrite: usageElem.TryGetProperty("cache_creation_input_tokens", out var cw) ? cw.GetInt32() : 0,
        Cost: 0m // Cost is calculated separately
    );
}
```

---

## Part 5: Handle tool calls

When the LLM returns tool calls, parse them into `ToolCallContent` blocks:

```csharp
private void ProcessToolCall(LlmStream stream, JsonElement toolUseElement)
{
    // Anthropic: { "type":"tool_use", "id":"...", "name":"read", "input":{...} }
    var id = toolUseElement.GetProperty("id").GetString() ?? "";
    var name = toolUseElement.GetProperty("name").GetString() ?? "";
    var input = toolUseElement.GetProperty("input"); // Already a JSON object

    // Convert JSON object to Dictionary<string, object>
    var args = JsonElementToDictionary(input);

    stream.Push(new ToolCallStartEvent(id, name, DateTimeOffset.UtcNow));
    stream.Push(new ToolCallEndEvent(
        id, name, args,
        ThoughtSignature: null,
        Timestamp: DateTimeOffset.UtcNow
    ));
}

private Dictionary<string, object> JsonElementToDictionary(JsonElement element)
{
    var result = new Dictionary<string, object>();

    foreach (var prop in element.EnumerateObject())
    {
        result[prop.Name] = JsonElementToObject(prop.Value);
    }

    return result;
}

private object JsonElementToObject(JsonElement element)
{
    return element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.TryGetInt32(out var i) ? i : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null!,
        JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
        JsonValueKind.Object => JsonElementToDictionary(element),
        _ => element.GetRawText()
    };
}
```

---

## Part 6: Provider-specific quirks

Different providers have different APIs, auth modes, and edge cases. Document yours.

### Example: Anthropic quirks

| Quirk | Handling |
|-------|----------|
| Three auth modes: API key, OAuth, Copilot | `DetectAuthMode()` checks env vars |
| `thinking` feature only on recent models | Check model ID; skip for older models |
| Tool names must match Claude Code tool lookup | Normalize with `ClaudeCodeToolLookup` |
| `max_tokens` auto-calculated based on thinking budget | `SimpleOptionsHelper.AdjustMaxTokensForThinking()` |
| SSE stream closes mid-message on timeout | Emit error with partial content |
| Cache headers reduce latency on repeated requests | Use `cache_control: {"type": "ephemeral"}` |

### Example: OpenAI quirks

| Quirk | Handling |
|-------|----------|
| Different models support different features | Load `CompatFlags` from `CompatProfiles` |
| Reasoning models use different stop reason format | Map `"stop"` → `StopReason.Stop` |
| `function_calling` vs `tools` modes (legacy vs new) | Assume `tools` mode, emit error if unsupported |
| Stream may include `usage` only at the end | Accumulate partial usage, emit final `Usage` at done |
| Tool call arguments are JSON strings, not objects | Parse `arguments` as JSON after accumulation |

---

## Part 7: Testing patterns

Use a mock provider to test the agent loop without hitting a real API.

### Mock provider

```csharp
public sealed class MockProvider : IApiProvider
{
    private readonly Func<Context, IAsyncEnumerable<AssistantMessageEvent>> _emit;

    public string Api => "mock";

    public MockProvider(Func<Context, IAsyncEnumerable<AssistantMessageEvent>> emit)
    {
        _emit = emit;
    }

    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
    {
        var stream = new LlmStream();
        var ct = options?.CancellationToken ?? CancellationToken.None;

        _ = Task.Run(async () =>
        {
            try
            {
                var contentBlocks = new List<ContentBlock>();
                await foreach (var evt in _emit(context))
                {
                    stream.Push(evt);
                    if (evt is ToolCallEndEvent tcEnd)
                    {
                        contentBlocks.Add(new ToolCallContent(
                            tcEnd.ToolCallId, tcEnd.ToolName, tcEnd.Arguments, null));
                    }
                    if (evt is TextDeltaEvent textDelta)
                    {
                        // Accumulate...
                    }
                }

                var final = new AssistantMessage(
                    Content: contentBlocks,
                    Api: Api,
                    Provider: "mock",
                    ModelId: model.Id,
                    Usage: Usage.Empty(),
                    StopReason: StopReason.Stop,
                    ErrorMessage: null,
                    ResponseId: null,
                    Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                );

                stream.Push(new DoneEvent(StopReason.Stop, final));
                stream.End(final);
            }
            catch (Exception ex)
            {
                var errorMsg = new AssistantMessage(
                    Content: new[] { new TextContent(ex.Message) as ContentBlock },
                    Api: Api,
                    Provider: "mock",
                    ModelId: model.Id,
                    Usage: Usage.Empty(),
                    StopReason: StopReason.Error,
                    ErrorMessage: ex.Message,
                    ResponseId: null,
                    Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                );
                stream.Push(new ErrorEvent(StopReason.Error, errorMsg));
                stream.End(errorMsg);
            }
        }, ct);

        return stream;
    }

    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        => Stream(model, context, options);
}

// Usage in tests
[Test]
public async Task Agent_Executes_Tool_On_Mock_Provider()
{
    var registry = new ApiProviderRegistry();

    registry.Register(new MockProvider(async (context) =>
    {
        // Emit tool call for "read"
        yield return new ToolCallStartEvent("tc1", "read", DateTimeOffset.UtcNow);
        yield return new ToolCallEndEvent(
            "tc1", "read",
            new Dictionary<string, object> { ["path"] = "file.txt" },
            null, DateTimeOffset.UtcNow);
        yield return new DoneEvent(StopReason.ToolUse,
            new AssistantMessage(/* ... */));
    }));

    var modelRegistry = new ModelRegistry();
    modelRegistry.Register("mock", new LlmModel(
        Id: "mock-model",
        Name: "Mock",
        Api: "mock",
        Provider: "mock",
        BaseUrl: "http://localhost",
        Reasoning: false,
        Input: new[] { "text" },
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 1000,
        MaxTokens: 100
    ));

    var llmClient = new LlmClient(registry, modelRegistry);
    var agent = new Agent(new AgentOptions(
        LlmClient: llmClient,
        Model: modelRegistry.Get("mock", "mock-model"),
        Tools: new[] { new ReadTool(".", 10) },
        // ...
    ));

    var messages = await agent.PromptAsync("read file.txt");
    // Assert tool was called
}
```

---

## Part 8: SimpleStreamOptions and reasoning

If your provider supports extended thinking, implement `StreamSimple`:

```csharp
public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
{
    if (options?.Reasoning is null)
    {
        // No reasoning requested, use base Stream
        var baseOptions = options == null ? null : new StreamOptions { /* ... */ };
        return Stream(model, context, baseOptions);
    }

    // Map reasoning level to vendor budget
    var thinkingBudget = SimpleOptionsHelper.GetBudgetForLevel(
        options.Reasoning, options.ThinkingBudgets);

    var adjustedTokens = SimpleOptionsHelper.AdjustMaxTokensForThinking(
        model, options.MaxTokens, thinkingBudget?.ThinkingBudget ?? 1024);

    // Create provider-specific options with thinking enabled
    var providerOpts = new MyProviderOptions
    {
        Temperature = options.Temperature,
        MaxTokens = adjustedTokens.adjustedMax,
        ThinkingEnabled = true,
        ThinkingBudget = adjustedTokens.adjustedBudget,
        // ...
    };

    return Stream(model, context, providerOpts);
}
```

---

## Part 9: Error handling

Providers must handle various failure modes gracefully:

```csharp
private async Task StreamCoreAsync(...)
{
    var contentBlocks = new List<ContentBlock>();
    var usage = Usage.Empty();
    var stopReason = StopReason.Stop;

    try
    {
        // Make request, parse stream
    }
    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
    {
        stopReason = StopReason.Error;
        contentBlocks.Add(new TextContent("Authentication failed. Check your API key."));
    }
    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.RateLimited)
    {
        stopReason = StopReason.Error;
        contentBlocks.Add(new TextContent("Rate limited. Please try again later."));
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        stopReason = StopReason.Aborted;
    }
    catch (Exception ex)
    {
        stopReason = StopReason.Error;
        contentBlocks.Add(new TextContent($"Streaming error: {ex.Message}"));
    }
    finally
    {
        var final = new AssistantMessage(
            Content: contentBlocks,
            Api: Api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: usage,
            StopReason: stopReason,
            ErrorMessage: stopReason == StopReason.Error ? "See content for details" : null,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );

        if (stopReason == StopReason.Error)
        {
            stream.Push(new ErrorEvent(stopReason, final));
        }
        else
        {
            stream.Push(new DoneEvent(stopReason, final));
        }

        stream.End(final);
    }
}
```

---

## Part 10: Registration and usage

Once your provider is implemented, register it and use it:

```csharp
// Setup
var httpClient = new HttpClient();
var apiRegistry = new ApiProviderRegistry();
var modelRegistry = new ModelRegistry();

// Register your provider
apiRegistry.Register(new MyProvider(httpClient));

// Register a model that uses your provider
var model = new LlmModel(
    Id: "my-model-1",
    Name: "My Model",
    Api: "my-api-format",  // Must match MyProvider.Api
    Provider: "myprovider",
    BaseUrl: "https://api.myprovider.com/v1",
    Reasoning: true,
    Input: new[] { "text", "image" },
    Cost: new ModelCost(0.001m, 0.002m, 0m, 0m),
    ContextWindow: 100_000,
    MaxTokens: 8_000
);
modelRegistry.Register("myprovider", model);

// Create LLM client
var llmClient = new LlmClient(apiRegistry, modelRegistry);

// Use in your agent
var agentOptions = new AgentOptions(
    LlmClient: llmClient,
    Model: model,
    // ...
);
var agent = new Agent(agentOptions);

await foreach (var evt in agent.PromptAsync("Hello"))
{
    Console.WriteLine(evt);
}
```

---

## Checklist

Before shipping your provider:

- [ ] `IApiProvider` implemented with `Api` property and `Stream()` method
- [ ] `StreamSimple()` implemented (even if it just delegates)
- [ ] Message conversion: `UserMessage` → vendor format
- [ ] Message conversion: vendor format → `AssistantMessage`
- [ ] Tool call parsing: vendor format → `ToolCallContent`
- [ ] SSE parsing: handle all event types
- [ ] Error handling: auth failures, rate limits, timeouts
- [ ] Usage tracking: input/output/cache tokens
- [ ] Reasoning support (if model supports it)
- [ ] Unit tests with mock provider
- [ ] Integration test with real API (optional but recommended)
- [ ] Documentation of provider-specific quirks

---

## What's next

- **[Provider System](01-providers.md)** — Provider registry, model registry
- **[Architecture Overview](../architecture/overview.md)** — Provider abstraction layer
- **[Building Your Own](04-building-your-own.md)** — Full end-to-end agent example
