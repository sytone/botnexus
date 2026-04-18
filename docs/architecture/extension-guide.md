# Extension Guide

**Purpose:** Brief guide for extending BotNexus with custom tools, channels, providers, hooks, and MCP servers.

---

## Adding a Tool

**Interface:** `IAgentTool` (from `BotNexus.Agent.Core`)

**Steps:**

1. **Implement the interface:**

```csharp
public class WeatherTool : IAgentTool
{
    public string Name => "get_weather";
    
    public Tool Definition => new()
    {
        Name = "get_weather",
        Description = "Get current weather for a location",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                location = new { type = "string", description = "City name" },
                unit = new { type = "string", enum = new[] { "celsius", "fahrenheit" }, @default = "celsius" }
            },
            required = new[] { "location" }
        }
    };
    
    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct)
    {
        var location = arguments["location"]?.ToString() ?? "unknown";
        var unit = arguments.GetValueOrDefault("unit")?.ToString() ?? "celsius";
        
        // Call weather API
        var weather = await GetWeatherAsync(location, unit, ct);
        
        return new AgentToolResult([
            new AgentToolContent(AgentToolContentType.Text, 
                $"Weather in {location}: {weather.Temperature}°{unit[0]}, {weather.Condition}")
        ]);
    }
}
```

2. **Register in tool factory:**

```csharp
// In custom IAgentToolFactory implementation
public IReadOnlyList<IAgentTool> CreateTools(string workspacePath, IPathValidator pathValidator)
{
    return
    [
        new ReadTool(workspacePath, pathValidator),
        new WriteTool(workspacePath, pathValidator),
        // ... other tools
        new WeatherTool()  // Add custom tool
    ];
}
```

3. **Add to agent descriptor:**

```json
{
  "agentId": "weather-agent",
  "tools": ["read", "write", "get_weather"]
}
```

**Best Practices:**

- Return structured data in tool results (LLM can parse)
- Handle errors gracefully (return error in `AgentToolResult`, don't throw)
- Support cancellation via `CancellationToken`
- Use `ILogger` for debugging

---

## Adding a Channel Adapter

**Interface:** `IChannelAdapter` (from `BotNexus.Gateway.Contracts`)

**Steps:**

1. **Implement the interface:**

```csharp
public class SlackChannelAdapter : IChannelAdapter
{
    private readonly IChannelDispatcher _dispatcher;
    private readonly SlackApiClient _slackClient;
    
    public ChannelKey ChannelType => new("slack");
    public string DisplayName => "Slack";
    public bool SupportsStreaming => false;  // Slack doesn't support SSE
    
    public async Task StartAsync(IChannelDispatcher dispatcher, CancellationToken ct)
    {
        _dispatcher = dispatcher;
        
        // Subscribe to Slack events
        await _slackClient.ConnectAsync(ct);
        _slackClient.OnMessage += HandleSlackMessage;
    }
    
    public async Task StopAsync(CancellationToken ct)
    {
        _slackClient.OnMessage -= HandleSlackMessage;
        await _slackClient.DisconnectAsync(ct);
    }
    
    public async Task SendAsync(OutboundMessage message, CancellationToken ct)
    {
        // Send message to Slack
        await _slackClient.PostMessageAsync(
            channel: message.Metadata["slackChannel"].ToString(),
            text: message.Content,
            ct);
    }
    
    private async void HandleSlackMessage(SlackMessage slackMsg)
    {
        var inbound = new InboundMessage
        {
            ChannelType = ChannelType,
            SenderId = slackMsg.UserId,
            SessionId = null,  // Will be resolved
            Content = slackMsg.Text,
            Metadata = new Dictionary<string, object?> { ["slackChannel"] = slackMsg.Channel }
        };
        
        await _dispatcher.DispatchAsync(inbound, CancellationToken.None);
    }
}
```

2. **Register in DI:**

```csharp
// In Program.cs or Startup
services.AddSingleton<IChannelAdapter, SlackChannelAdapter>();
```

3. **Configure in platform-config.json:**

```json
{
  "channels": [
    {
      "type": "slack",
      "enabled": true,
      "config": {
        "apiToken": "${SLACK_BOT_TOKEN}"
      }
    }
  ]
}
```

**Best Practices:**

- Implement `SupportsStreaming` accurately (buffers-and-sends if false)
- Handle reconnections gracefully
- Use structured logging for debugging
- Include channel-specific metadata in messages

---

## Adding a Provider

**Interface:** `IApiProvider` (from `BotNexus.Agent.Providers.Core`)

**Steps:**

1. **Implement the interface:**

```csharp
public class GeminiProvider : IApiProvider
{
    private readonly HttpClient _httpClient;
    
    public string Name => "gemini";
    public IReadOnlyList<Transport> SupportedTransports => [Transport.Streaming];
    
    public async Task<LlmStream> StreamAsync(
        LlmModel model,
        Context context,
        StreamOptions options,
        CancellationToken ct)
    {
        var stream = new LlmStream();
        
        _ = Task.Run(async () =>
        {
            try
            {
                var request = BuildRequest(model, context, options);
                var response = await _httpClient.PostAsync(model.BaseUrl, request, ct);
                
                await foreach (var sseEvent in ParseSseStream(response.Content, ct))
                {
                    var msgEvent = ConvertToAssistantMessageEvent(sseEvent);
                    await stream.Channel.Writer.WriteAsync(msgEvent, ct);
                }
                
                await stream.Channel.Writer.WriteAsync(new DoneEvent(), ct);
            }
            catch (Exception ex)
            {
                await stream.Channel.Writer.WriteAsync(new ErrorEvent { Message = ex.Message }, ct);
            }
            finally
            {
                stream.Channel.Writer.Complete();
            }
        }, ct);
        
        return stream;
    }
}
```

2. **Register in `ApiProviderRegistry`:**

```csharp
// In Program.cs
var geminiProvider = new GeminiProvider(httpClient);
services.AddSingleton<IApiProvider>(geminiProvider);
```

3. **Register models:**

```csharp
var geminiModel = new LlmModel
{
    Id = "gemini-2.0-flash",
    DisplayName = "Gemini 2.0 Flash",
    ApiProvider = "gemini",
    ContextWindow = 1_000_000,
    MaxOutput = 8192,
    InputPricing = 0.075m / 1_000_000,
    OutputPricing = 0.30m / 1_000_000
};

ModelRegistry.Register(geminiModel);
```

4. **Use in agent config:**

```json
{
  "agentId": "gemini-agent",
  "apiProvider": "gemini",
  "modelId": "gemini-2.0-flash"
}
```

**Best Practices:**

- Parse SSE streams carefully (handle reconnections)
- Convert provider events to `AssistantMessageEvent` hierarchy
- Support caching if provider offers it
- Handle rate limits with exponential backoff

---

## Adding a Hook

**Interface:** `IHookHandler` (from `BotNexus.Agent.Core`)

**Steps:**

1. **Implement the interface:**

```csharp
public class AuditHookHandler : IHookHandler
{
    private readonly ILogger<AuditHookHandler> _logger;
    
    public async Task<BeforeToolCallResult> BeforeAsync(BeforeToolCallContext context)
    {
        _logger.LogInformation("Tool {ToolName} called with args: {Arguments}", 
            context.ToolName, JsonSerializer.Serialize(context.Arguments));
        
        // Allow all (audit only, no blocking)
        return BeforeToolCallResult.Allow();
    }
    
    public async Task<AfterToolCallResult> AfterAsync(AfterToolCallContext context)
    {
        _logger.LogInformation("Tool {ToolName} completed in {Duration}ms", 
            context.ToolName, context.Duration.TotalMilliseconds);
        
        // Don't modify result
        return AfterToolCallResult.NoModification();
    }
}
```

2. **Register in DI:**

```csharp
services.AddSingleton<IHookHandler, AuditHookHandler>();
```

3. **Wire into agent creation:**

```csharp
// In IsolationStrategy.CreateAsync()
var hooks = serviceProvider.GetServices<IHookHandler>().ToList();
var hookDispatcher = new HookDispatcher(hooks);

var agent = new Agent(
    model, systemPrompt, tools,
    options: new AgentOptions
    {
        BeforeToolCall = hookDispatcher.BeforeToolCallAsync,
        AfterToolCall = hookDispatcher.AfterToolCallAsync
    },
    llmClient);
```

**Best Practices:**

- Return `BeforeToolCallResult.Block(reason)` to prevent tool execution
- Return `AfterToolCallResult.ModifyResult(newResult)` to transform output
- Use `ILogger` for audit trails
- Keep hooks fast (they block tool execution)

---

## Adding an MCP Server

**MCP (Model Context Protocol):** Standard for tools/resources via JSON-RPC.

**Steps:**

1. **Install MCP server** (e.g., `@modelcontextprotocol/server-filesystem`):

```bash
npm install -g @modelcontextprotocol/server-filesystem
```

2. **Configure in `platform-config.json`:**

```json
{
  "mcpServers": [
    {
      "id": "filesystem",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "/home/user/docs"],
      "env": {
        "NODE_ENV": "production"
      }
    }
  ]
}
```

3. **Tools auto-discovered** and added to agent's tool registry

4. **Use in agent:**

```json
{
  "agentId": "docs-agent",
  "extensions": [
    {
      "type": "mcp",
      "enabled": true
    }
  ]
}
```

**Built-in MCP Servers:**

- `@modelcontextprotocol/server-filesystem` — File operations
- `@modelcontextprotocol/server-github` — GitHub API
- `@modelcontextprotocol/server-puppeteer` — Browser automation
- `@modelcontextprotocol/server-sqlite` — SQLite queries

**Best Practices:**

- Use `npx -y` for auto-install
- Set environment variables for API keys
- Monitor process health (MCP extension auto-restarts on crash)

---

## Adding a Prompt Section

**Interface:** `IPromptSection` (from `BotNexus.Prompts`)

**Steps:**

1. **Implement the interface:**

```csharp
public class CustomGuidelinesSection : IPromptSection
{
    public int Order => 550;  // After standard guidelines, before examples
    
    public bool ShouldInclude(PromptContext context)
    {
        return context.Extensions.ContainsKey("customGuidelines");
    }
    
    public IReadOnlyList<string> Build(PromptContext context)
    {
        var guidelines = (string)context.Extensions["customGuidelines"];
        
        return
        [
            "## Custom Guidelines",
            "",
            guidelines,
            ""
        ];
    }
}
```

2. **Register in pipeline:**

```csharp
var pipeline = new PromptPipeline()
    .Add(new IdentitySection())
    .Add(new WorkspaceSection())
    .Add(new ToolsSection())
    .Add(new CustomGuidelinesSection())  // Add custom section
    .Add(new ExamplesSection());
```

**Best Practices:**

- Use `Order` to control placement (100-600 range)
- Check `context.IsMinimal` to skip verbose sections
- Use extensions dictionary for dynamic data
- Keep sections scannable (tables, bullet lists, short paragraphs)

---

## Summary

BotNexus extension points:

| Extension | Interface | Use Case |
|-----------|-----------|----------|
| **Tool** | `IAgentTool` | Add capabilities (API calls, database queries) |
| **Channel** | `IChannelAdapter` | Integrate platforms (Slack, Discord, SMS) |
| **Provider** | `IApiProvider` | Support new LLM APIs (Gemini, Mistral) |
| **Hook** | `IHookHandler` | Intercept tool execution (audit, validation, rate limiting) |
| **MCP Server** | (external process) | Standard tool/resource providers |
| **Prompt Section** | `IPromptSection` | Customize system prompts |
| **Session Store** | `ISessionStore` | Custom persistence (PostgreSQL, CosmosDB) |
| **Isolation Strategy** | `IIsolationStrategy` | Custom execution environments |

**For detailed examples:**

- **[Extension Development Guide](../extension-development.md)** — Full walkthroughs
- **[Development Guide](../dev-guide.md)** — Code-level implementation
