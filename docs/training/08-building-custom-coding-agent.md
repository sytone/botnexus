# Building Your Own Coding Agent

This guide teaches you to build a custom coding agent on top of BotNexus.AgentCore. A coding agent is an `Agent` configured with file tools, shell access, a system prompt, and safety guards. By the end of this guide, you'll understand how to wire all the pieces together.

**Prerequisites:** Familiarity with C#/.NET 10, async/await, records, and the [Architecture overview](00-overview.md) and [Agent Core](02-agent-core.md) docs.

---

## Step 1: Set up your project

Create a new console app targeting .NET 10+:

```bash
dotnet new console -n MyAgent
cd MyAgent
dotnet add package BotNexus.AgentCore
dotnet add package BotNexus.Providers.Core
dotnet add package BotNexus.Providers.Anthropic
```

Your project structure:

```
MyAgent/
├── MyAgent.csproj
├── Program.cs                    # Your entry point
└── bin/, obj/                    # Build artifacts
```

---

## Step 2: Initialize registries and create LlmClient

The `LlmClient` routes all LLM requests to the correct provider. First, create instance-based registries:

```csharp
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.Anthropic;

// 1. Create registries
var apiProviderRegistry = new ApiProviderRegistry();
var modelRegistry = new ModelRegistry();
var httpClient = new HttpClient();

// 2. Register a provider
apiProviderRegistry.Register(new AnthropicProvider(httpClient));

// 3. Register a model (or use a pre-registered one)
var model = new LlmModel(
    Id: "claude-sonnet-4",
    Name: "Claude 3.5 Sonnet",
    Api: "anthropic-messages",      // Routes to AnthropicProvider
    Provider: "anthropic",
    BaseUrl: "https://api.anthropic.com",
    Reasoning: true,
    Input: new[] { "text", "image" },
    Cost: new ModelCost(3m, 15m, 0.3m, 3.75m),
    ContextWindow: 200_000,
    MaxTokens: 64_000
);

modelRegistry.Register("anthropic", model);

// 4. Create the LLM client
var llmClient = new LlmClient(apiProviderRegistry, modelRegistry);
```

---

## Step 3: Create an AgentOptions configuration

`AgentOptions` is the immutable configuration record that defines how your agent behaves. It wires delegates for message conversion, API key resolution, hooks, and generation settings.

```csharp
using BotNexus.AgentCore;
using BotNexus.AgentCore.Configuration;
using BotNexus.Providers.Core;

// Resolve API key at runtime
string GetApiKey(string provider, CancellationToken ct) =>
    Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
    ?? throw new InvalidOperationException("ANTHROPIC_API_KEY not set");

// Use default message conversion
var convertToLlm = DefaultMessageConverter.Create();

// Identity context transformation (no compaction or filtering)
Task<AgentContext> TransformContext(AgentContext context, CancellationToken ct) =>
    Task.FromResult(context);

// No transformation on API key resolution
Task<string?> GetApiKeyAsync(string provider, CancellationToken ct) =>
    Task.FromResult(GetApiKey(provider, ct) as string?);

// Create the options
var agentOptions = new AgentOptions(
    InitialState: null,  // No pre-seeded state
    Model: model,
    LlmClient: llmClient,
    ConvertToLlm: convertToLlm,
    TransformContext: TransformContext,
    GetApiKey: GetApiKeyAsync,
    GetSteeringMessages: null,
    GetFollowUpMessages: null,
    ToolExecutionMode: ToolExecutionMode.Sequential,
    BeforeToolCall: null,
    AfterToolCall: null,
    GenerationSettings: new SimpleStreamOptions
    {
        MaxTokens = 8000,
        Temperature = 0.7f,
        Reasoning = null  // No extended thinking
    },
    SteeringMode: QueueMode.All,
    FollowUpMode: QueueMode.OneAtATime,
    SessionId: null
);
```

---

## Step 4: Build a system prompt

The system prompt tells the LLM what it should do. Use `SystemPromptBuilder` from CodingAgent, or write your own:

```csharp
var systemPrompt = """
You are a helpful coding assistant. You can read files, write code, and execute shell commands.
Be concise and focus on solving the user's problem.
Always verify your changes don't break existing tests.
""";
```

Or use the full `SystemPromptBuilder` from `BotNexus.CodingAgent`:

```csharp
using BotNexus.CodingAgent;

var contextFiles = await ContextFileDiscovery.DiscoverAsync(
    Environment.CurrentDirectory,
    CancellationToken.None);

var systemPrompt = SystemPromptBuilder.Build(new SystemPromptContext(
    WorkingDirectory: Environment.CurrentDirectory,
    ProjectName: "MyProject",
    EnvironmentContext: "Linux, C#",
    GitBranch: "main",
    PackageManagers: new[] { "dotnet" },
    Tools: new List<IAgentTool>(),  // Populated in next step
    Skills: new List<string>(),
    ContextFiles: contextFiles
));
```

---

## Step 5: Create tools

Tools are what the agent can invoke. Implement `IAgentTool` for each tool:

```csharp
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core.Models;
using System.Text.Json;

public sealed class DemoTool : IAgentTool
{
    public string Name => "demo_tool";
    public string Label => "Demo Tool";

    public Tool Definition => new(
        Name,
        "A simple demonstration tool",
        JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "message": { "type": "string" }
          },
          "required": ["message"]
        }
        """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var message = arguments.TryGetValue("message", out var msg) 
            ? msg?.ToString() 
            : "no message";
        
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?> { ["message"] = message });
    }

    public async Task<AgentToolResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> preparedArguments,
        AgentToolUpdateCallback? updateCallback = null,
        CancellationToken cancellationToken = default)
    {
        var message = preparedArguments["message"]?.ToString() ?? "";
        var result = $"Echo: {message}";
        
        return new AgentToolResult(
            Content: new List<AgentToolContent>
            {
                new AgentToolContent(
                    Type: AgentToolContentType.Text,
                    Text: result)
            },
            Details: null,
            IsError: false);
    }

    // Optional: contribute extra guidance to the system prompt
    public string? GetPromptSnippet() => null;
    public string? GetPromptGuidelines() => "Use this tool to echo messages.";
}
```

Register your tools with the agent state:

```csharp
var tools = new List<IAgentTool>
{
    new DemoTool()
};

agent.State.Tools = tools;
```

---

## Step 6: Create the Agent and run it

```csharp
using BotNexus.AgentCore;
using BotNexus.AgentCore.Types;

// Create the agent from options
var agent = new Agent(agentOptions);

// Set initial system prompt and tools
agent.State.SystemPrompt = systemPrompt;
agent.State.Tools = tools;
agent.State.Model = model;

// Subscribe to events for logging
agent.Subscribe(async (evt, ct) =>
{
    if (evt is AgentEndEvent endEvt)
    {
        Console.WriteLine($"Agent finished. Stop reason: {endEvt.StopReason}");
    }
    await Task.CompletedTask;
});

// Run a prompt
var messages = await agent.PromptAsync("Echo hello world", CancellationToken.None);

// Print the final response
foreach (var msg in messages.OfType<AssistantAgentMessage>().TakeLast(1))
{
    var textBlocks = msg.Content.OfType<TextContent>();
    foreach (var block in textBlocks)
    {
        Console.WriteLine(block.Text);
    }
}
```

---

## Step 7: Handle tool calls and results

When the LLM requests a tool call, the agent loop executes it automatically. You can hook into this process:

```csharp
// Hook before tool execution
async Task BeforeToolCall(BeforeToolCallContext ctx, CancellationToken ct)
{
    Console.WriteLine($"About to call: {ctx.ToolName}");
    await Task.CompletedTask;
}

// Hook after tool execution
async Task AfterToolCall(AfterToolCallContext ctx, CancellationToken ct)
{
    Console.WriteLine($"Tool {ctx.ToolName} returned: {ctx.Content}");
    await Task.CompletedTask;
}

// Wire into agent options
var agentOptions = new AgentOptions(
    // ... other fields ...
    BeforeToolCall: BeforeToolCall,
    AfterToolCall: AfterToolCall,
    // ... other fields ...
);
```

---

## Step 8: Manage sessions (optional)

Sessions persist conversation history. Use `SessionManager` to save and resume:

```csharp
using BotNexus.CodingAgent.Session;

var sessionManager = new SessionManager();

// Create a new session
var sessionInfo = await sessionManager.CreateSessionAsync(
    Environment.CurrentDirectory,
    "my-session");

// Save after a run
await sessionManager.SaveAsync(
    sessionInfo,
    agent.State.Messages,
    Environment.CurrentDirectory);

// Resume later
var resumed = await sessionManager.ResumeSessionAsync(
    sessionInfo.Id,
    Environment.CurrentDirectory);

agent.State.Messages = resumed.Messages;
```

---

## Step 9: Add safety and audit hooks

The CodingAgent provides `SafetyHooks` and `AuditHooks` to validate and log operations:

```csharp
using BotNexus.CodingAgent.Hooks;

// Create safety hooks (validates paths, commands, sizes)
var safetyHooks = new SafetyHooks(
    workingDirectory: Environment.CurrentDirectory,
    blockedPaths: new[] { "/etc", "/sys" },
    allowedCommands: new[] { "ls", "cat", "grep" },
    maxWriteSize: 1_000_000);

// Before-tool hook: check safety
var agentOptions = new AgentOptions(
    // ... other fields ...
    BeforeToolCall: async (ctx, ct) =>
    {
        var result = await safetyHooks.ValidateBeforeToolCallAsync(ctx, ct);
        return result;
    },
    // ... other fields ...
);
```

---

## Step 10: Complete minimal example

Here's a working example that puts it all together:

```csharp
using BotNexus.AgentCore;
using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.Anthropic;
using System.Text.Json;

// 1. Set up registries
var apiProviderRegistry = new ApiProviderRegistry();
var modelRegistry = new ModelRegistry();
var httpClient = new HttpClient();

apiProviderRegistry.Register(new AnthropicProvider(httpClient));

var model = new LlmModel(
    Id: "claude-sonnet-4",
    Name: "Claude 3.5 Sonnet",
    Api: "anthropic-messages",
    Provider: "anthropic",
    BaseUrl: "https://api.anthropic.com",
    Reasoning: false,
    Input: new[] { "text" },
    Cost: new ModelCost(3m, 15m, 0m, 0m),
    ContextWindow: 200_000,
    MaxTokens: 64_000);

modelRegistry.Register("anthropic", model);
var llmClient = new LlmClient(apiProviderRegistry, modelRegistry);

// 2. Create AgentOptions
var agentOptions = new AgentOptions(
    InitialState: null,
    Model: model,
    LlmClient: llmClient,
    ConvertToLlm: DefaultMessageConverter.Create(),
    TransformContext: (ctx, ct) => Task.FromResult(ctx),
    GetApiKey: (provider, ct) =>
        Task.FromResult(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")),
    GetSteeringMessages: null,
    GetFollowUpMessages: null,
    ToolExecutionMode: ToolExecutionMode.Sequential,
    BeforeToolCall: null,
    AfterToolCall: null,
    GenerationSettings: new SimpleStreamOptions
    {
        MaxTokens = 8000,
        Temperature = 0.7f
    },
    SteeringMode: QueueMode.All,
    FollowUpMode: QueueMode.OneAtATime);

// 3. Create simple system prompt
var systemPrompt = "You are a helpful assistant. Answer questions concisely.";

// 4. Create the agent
var agent = new Agent(agentOptions);
agent.State.SystemPrompt = systemPrompt;
agent.State.Tools = new List<IAgentTool>();

// 5. Run a prompt
var messages = await agent.PromptAsync("What is 2+2?");

// 6. Print response
foreach (var msg in messages.OfType<AssistantAgentMessage>().TakeLast(1))
{
    foreach (var block in msg.Content.OfType<TextContent>())
    {
        Console.WriteLine(block.Text);
    }
}

await httpClient.DisposeAsync();
```

---

## Extending your agent

**Add more providers:** Register OpenAI, Anthropic, or any LLM that implements `IApiProvider`.

**Add custom tools:** Implement `IAgentTool` for database queries, API calls, file operations, etc.

**Add hooks:** Use before/after tool call hooks for validation, logging, or result transformation.

**Add extensions:** Build `IExtension` plugins that hook into tool execution, session lifecycle, and model requests.

**Add sessions:** Use `SessionManager` to persist conversations and support resuming.

**Add safety guards:** Use `SafetyHooks` to validate paths, commands, and output sizes.

---

## Related documentation

- **[Architecture Overview](00-overview.md)** — System design and layer separation
- **[Agent Core](02-agent-core.md)** — Deep dive into the agent loop, state, and hooks
- **[Coding Agent](03-coding-agent.md)** — How BotNexus wires a full coding agent
- **[Tool Development](08-tool-development.md)** — In-depth tool implementation guide
- **[Building Your Own — Provider](04-building-your-own.md#step-10-adding-a-new-llm-provider)** — Implementing a custom provider
