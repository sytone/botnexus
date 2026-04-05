# Building your own coding agent

> **Audience:** Developers who want to build a custom coding agent on top of BotNexus.
> **Prerequisites:** Read [provider architecture](providers.md), [agent events](agent-events.md), and [tool security](tool-security.md) first.
> **Source code:** `src/coding-agent/BotNexus.CodingAgent/`

## What you'll learn

1. Architecture overview — how CodingAgent composes AgentCore + Providers + Tools
2. System prompt construction
3. Tool registration and the IAgentTool interface
4. Session management
5. Extension system
6. Configuration
7. Step-by-step guide: building a minimal custom coding agent
8. Message flow diagram

---

## Architecture overview

The `CodingAgent` class is a factory — it doesn't subclass `Agent`. Instead, `CodingAgent.CreateAsync` wires together all the components and returns a configured `Agent` instance.

```
CodingAgent.CreateAsync(config, workingDir, authManager, llmClient, modelRegistry, ...)
│
├── 1. Resolve model (from registry or create default)
├── 2. Detect environment (git branch, package manager)
├── 3. Discover context files (README, copilot-instructions, docs)
├── 4. Build system prompt (via SystemPromptBuilder)
├── 5. Create tools (ReadTool, WriteTool, EditTool, ShellTool, GrepTool, GlobTool, ListDirectoryTool)
├── 6. Wire safety hooks (SafetyHooks + AuditHooks)
├── 7. Wire extension hooks (ExtensionRunner)
└── 8. Return new Agent(agentOptions)
```

### Layer composition

```
┌──────────────────────────────────────────────────────────┐
│  CodingAgent (Factory — src/coding-agent/)               │
│  ┌────────────────────────────────────────────────────┐  │
│  │  System Prompt + Tools + Hooks + Sessions          │  │
│  │  ┌──────────────────────────────────────────────┐  │  │
│  │  │  Agent (src/agent/BotNexus.AgentCore/)       │  │  │
│  │  │  ┌────────────────────────────────────────┐  │  │  │
│  │  │  │  LlmClient + Providers (src/providers/)│  │  │  │
│  │  │  └────────────────────────────────────────┘  │  │  │
│  │  └──────────────────────────────────────────────┘  │  │
│  └────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────┘
```

Each layer depends only on the one below it. You can replace any layer without affecting the others:

- Swap providers without touching the agent loop
- Swap tools without touching the providers
- Build a completely different application layer (not a coding agent) on top of `Agent`

---

## System prompt construction

`SystemPromptBuilder` (`SystemPromptBuilder.cs`) dynamically constructs the system prompt from runtime context.

### SystemPromptContext

All inputs to the prompt builder are packaged in a `SystemPromptContext` record:

```csharp
public sealed record SystemPromptContext(
    string WorkingDirectory,
    string? GitBranch,
    string? GitStatus,
    string PackageManager,
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<string> Skills,
    string? CustomInstructions,
    IReadOnlyList<ToolPromptContribution>? ToolContributions = null,
    IReadOnlyList<PromptContextFile>? ContextFiles = null
);
```

### Prompt structure

The generated prompt has these sections (in order):

```markdown
# Preamble
"You are a coding assistant with access to tools..."

## Environment
- Date: 2025-01-15
- OS: Windows
- Working directory: /home/user/project
- Git branch: main (3 modified files)
- Package manager: dotnet

## Available Tools
- read — Read files and directories
- write — Write complete files
- edit — Surgical text edits with fuzzy matching
- bash — Execute shell commands
- grep — Regex search across files
- find — Glob pattern file matching
- ls — List directory contents

## Tool Guidelines
- Use tools proactively
- Read files before editing
- Make precise edits
- Verify changes compile
(+ custom guidelines from tools)

## Project Context (optional)
[Contents of README.md, copilot-instructions.md, docs/*.md — 16 KB budget]

## Skills (optional)
[Skill documents with frontmatter]

## Custom Instructions (optional)
[User-provided instructions]
```

### Tool contributions

Tools can contribute to the system prompt via `GetPromptSnippet()` and `GetPromptGuidelines()`:

```csharp
public sealed record ToolPromptContribution(
    string Name,
    string? Snippet = null,                    // One-line tool description
    IReadOnlyList<string>? Guidelines = null   // Additional guidelines
);
```

### Context file discovery

`ContextFileDiscovery.DiscoverAsync()` scans the working directory for documentation files:

1. `.github/copilot-instructions.md` (highest priority)
2. `README.md`
3. `docs/*.md` (first 5 files, sorted)

Total budget: **16 KB** across all files. Files exceeding the remaining budget are truncated with `[truncated]`.

### Skill loading

`SkillsLoader.LoadSkills()` discovers skill documents from:

1. `./AGENTS.md`
2. `.botnexus-agent/AGENTS.md`
3. `.botnexus-agent/skills/*/SKILL.md`
4. `~/.botnexus/skills/*/SKILL.md`

Skills use YAML frontmatter:

```yaml
---
name: my-skill
description: "Brief description"
disable-model-invocation: false
---
Skill instructions go here...
```

---

## Tool registration and IAgentTool

### The IAgentTool interface

Every tool implements `IAgentTool` (defined in `AgentCore/Tools/IAgentTool.cs`):

```csharp
public interface IAgentTool
{
    string Name { get; }       // Unique name (case-sensitive)
    string Label { get; }      // Human-readable display name
    Tool Definition { get; }   // JSON Schema for parameters

    // Validate and prepare arguments before execution
    Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);

    // Execute the tool
    Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null);

    // Optional: one-line description for system prompt
    string? GetPromptSnippet() => null;

    // Optional: additional guidelines for system prompt
    IReadOnlyList<string> GetPromptGuidelines() => [];
}
```

### Built-in tools

`CodingAgent.CreateTools()` creates the standard tool set:

| Tool | Name | Purpose |
|---|---|---|
| `ReadTool` | `read` | Read files/directories (2000 line limit, image support) |
| `ListDirectoryTool` | `ls` | List directory contents (500 entries, flat listing) |
| `WriteTool` | `write` | Write complete files (creates parent dirs) |
| `EditTool` | `edit` | Surgical edits with exact + fuzzy matching |
| `ShellTool` | `bash` | Execute shell commands (120s timeout, process tree kill) |
| `GlobTool` | `find` | Glob pattern file matching (1000 results) |
| `GrepTool` | `grep` | Regex search with context lines (100 matches) |

All file tools are initialized with the working directory and use `PathUtils.ResolvePath` for containment.

### Implementing a custom tool

```csharp
public sealed class SearchTool : IAgentTool
{
    private readonly string _workingDirectory;

    public SearchTool(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    public string Name => "search";
    public string Label => "Search";

    public Tool Definition => new(
        Name: "search",
        Description: "Search for code symbols across the project",
        Parameters: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", description = "Search query" },
                language = new { type = "string", description = "Filter by language" }
            },
            required = new[] { "query" }
        })
    );

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        // Validate required parameters
        if (!arguments.ContainsKey("query") || string.IsNullOrEmpty(arguments["query"]?.ToString()))
            throw new ArgumentException("query is required");

        return Task.FromResult(arguments);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var query = arguments["query"]!.ToString()!;
        var results = await PerformSearch(query, cancellationToken);

        return new AgentToolResult([
            new AgentToolContent(AgentToolContentType.Text, results)
        ]);
    }

    public string? GetPromptSnippet() => "Search for code symbols across the project";

    public IReadOnlyList<string> GetPromptGuidelines() =>
        ["Use search to find symbol definitions before editing"];
}
```

---

## Session management

### SessionManager

`SessionManager` (`Session/SessionManager.cs`) provides persistent session storage using JSONL format with DAG-based branching.

**Core operations:**

```csharp
// Create a new session
SessionInfo session = await sessionManager.CreateSessionAsync(workingDir, "my-session");

// Save messages
await sessionManager.SaveSessionAsync(session, agent.State.Messages);

// Resume a session
var (resumedSession, messages) = await sessionManager.ResumeSessionAsync(sessionId, workingDir);

// List all sessions
IReadOnlyList<SessionInfo> sessions = await sessionManager.ListSessionsAsync(workingDir);
```

### Session file format

Sessions are stored as `.jsonl` files (one JSON object per line) in `.botnexus-agent/sessions/`:

```jsonl
{"type":"session_header","id":"abc123","name":"my-session","createdAt":"...","version":2}
{"type":"message","role":"user","content":"Fix the bug","parentEntryId":"abc123","entryId":"e1"}
{"type":"message","role":"assistant","content":"I'll look...","parentEntryId":"e1","entryId":"e2"}
{"type":"tool_result","toolCallId":"tc1","toolName":"read","parentEntryId":"e2","entryId":"e3"}
{"type":"metadata","key":"leaf","value":"e3"}
```

### Branching model

Every entry has a `ParentEntryId`, forming a directed acyclic graph (DAG). Multiple entries can share the same parent, creating branches:

```
         header
         ├── user msg (e1)
         │   ├── assistant (e2) ◄── branch A
         │   │   └── tool result (e3)
         │   └── assistant (e4) ◄── branch B
         │       └── tool result (e5)
```

Switch between branches:

```csharp
// List branches
var branches = await sessionManager.ListBranchesAsync(sessionId, workingDir);

// Switch to a different branch
await sessionManager.SwitchBranchAsync(sessionId, workingDir, leafEntryId: "e5");
```

### Session compaction

`SessionCompactor` reduces context size when approaching token limits:

```csharp
var compactor = new SessionCompactor();
var compacted = await compactor.CompactAsync(messages, new SessionCompactionOptions
{
    MaxContextTokens = 100_000,
    ReserveTokens = 16_384,
    KeepRecentTokens = 20_000,
    KeepRecentCount = 10,
    LlmClient = llmClient,
    Model = model
});
```

Compaction uses the LLM to generate a structured summary of older messages, preserving:
- Goal and constraints
- Progress and decisions
- Critical context
- Read and modified files

---

## Extension system

### IExtension interface

Extensions (`Extensions/IExtension.cs`) are assembly-based plugins that add tools and intercept lifecycle events:

```csharp
public interface IExtension
{
    string Name { get; }

    // Provide custom tools
    IReadOnlyList<IAgentTool> GetTools();

    // Pre-tool-call hook (can block)
    ValueTask<BeforeToolCallResult?> OnToolCallAsync(
        ToolCallLifecycleContext context, CancellationToken ct = default);

    // Post-tool-call hook (can transform)
    ValueTask<AfterToolCallResult?> OnToolResultAsync(
        ToolResultLifecycleContext context, CancellationToken ct = default);

    // Session lifecycle
    ValueTask OnSessionStartAsync(SessionLifecycleContext context, CancellationToken ct = default);
    ValueTask OnSessionEndAsync(SessionLifecycleContext context, CancellationToken ct = default);

    // Compaction hook (custom summary)
    ValueTask<string?> OnCompactionAsync(
        CompactionLifecycleContext context, CancellationToken ct = default);

    // Model request interception
    ValueTask<object?> OnModelRequestAsync(
        ModelRequestLifecycleContext context, CancellationToken ct = default);
}
```

### Extension loading

`ExtensionLoader` scans for DLLs in three directories:

1. `.botnexus-agent/extensions/` (project-level)
2. `~/.botnexus/extensions/` (user-level)
3. `~/.botnexus-agent/extensions/` (user-level alternative)

It loads each assembly, finds classes implementing `IExtension` with parameterless constructors, and instantiates them.

```csharp
var loader = new ExtensionLoader();
var loadResult = loader.LoadExtensions(config.ExtensionsDirectory);
// loadResult.Extensions — loaded IExtension instances
// loadResult.Tools — all tools from all extensions
```

### ExtensionRunner

`ExtensionRunner` orchestrates extension lifecycle callbacks:

```csharp
var runner = new ExtensionRunner(
    loadResult.Extensions,
    workingDirectory: workingDir,
    sessionId: session.Id,
    modelId: model.Id
);

// Called in hook chain
var result = await runner.OnToolCallAsync(context, ct);
```

Extension errors are caught and logged — a failing extension doesn't crash the agent.

### Implementing an extension

```csharp
public sealed class MyExtension : IExtension
{
    public string Name => "my-extension";

    public IReadOnlyList<IAgentTool> GetTools() =>
        [new MyCustomTool()];

    public ValueTask<BeforeToolCallResult?> OnToolCallAsync(
        ToolCallLifecycleContext context, CancellationToken ct = default)
    {
        // Log or validate
        Console.WriteLine($"Tool call: {context.ToolName}");
        return ValueTask.FromResult<BeforeToolCallResult?>(null);
    }

    // ... other methods use default implementations
}
```

---

## Configuration

### CodingAgentConfig

`CodingAgentConfig` (`CodingAgentConfig.cs`) loads configuration with a cascading hierarchy:

```
1. Default values (in-memory)
2. Global: ~/.botnexus/coding-agent.json
3. Local: .botnexus-agent/config.json (auto-created on startup)
4. CLI overrides (--model, --provider, --thinking, --verbose)
```

**Key properties:**

```csharp
public class CodingAgentConfig
{
    string ConfigDirectory     // .botnexus-agent/
    string SessionsDirectory   // .botnexus-agent/sessions/
    string ExtensionsDirectory // .botnexus-agent/extensions/
    string SkillsDirectory     // .botnexus-agent/skills/
    string? Model              // Model ID (default: "gpt-4.1")
    string? Provider           // Provider ID (default: "github-copilot")
    string? ApiKey             // Explicit API key
    int MaxToolIterations      // Loop limit (default: 40)
    int MaxContextTokens       // Token budget (default: 100,000)
    List<string> AllowedCommands  // Shell command whitelist
    List<string> BlockedPaths     // File path denylist
    Dictionary<string, object?> Custom  // Arbitrary settings (verbose, thinking)
}
```

### CLI arguments

`CommandParser` handles command-line arguments:

```
Usage: botnexus-coding-agent [options] [prompt]

Options:
  --model <model>          Override model id
  --provider <provider>    Override provider id
  --resume <session-id>    Resume an existing session
  --thinking <level>       Set reasoning level: off|minimal|low|medium|high|xhigh
  --non-interactive        Run one prompt and exit
  --verbose                Enable verbose logs
  --help                   Show help
```

### Auth management

`AuthManager` handles OAuth credential persistence:

```csharp
var authManager = new AuthManager(config.ConfigDirectory);

// Login (interactive device code flow)
await authManager.LoginAsync(ct);

// Get API key (auto-refreshes if needed)
string? key = await authManager.GetApiKeyAsync(config, "github-copilot", ct);

// Resolution order:
// 1. config.ApiKey (explicit)
// 2. Environment variables (COPILOT_GITHUB_TOKEN → GH_TOKEN → GITHUB_TOKEN)
// 3. Saved credentials (auto-refreshes when <60 seconds to expiry)
// 4. null (prompt user to /login)
```

---

## Step-by-step: building a minimal coding agent

Here's a complete example that creates a functional coding agent from scratch.

### Step 1: Create the project

```bash
dotnet new console -n MyCodingAgent
cd MyCodingAgent
dotnet add reference ../BotNexus.Providers.Core/BotNexus.Providers.Core.csproj
dotnet add reference ../BotNexus.Providers.Anthropic/BotNexus.Providers.Anthropic.csproj
dotnet add reference ../BotNexus.AgentCore/BotNexus.AgentCore.csproj
```

### Step 2: Register providers and models

```csharp
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.Anthropic;

var httpClient = new HttpClient();
var apiProviders = new ApiProviderRegistry();
var modelRegistry = new ModelRegistry();

// Register the Anthropic provider
apiProviders.Register(new AnthropicProvider(httpClient));

// Register a model
var model = new LlmModel(
    Id: "claude-sonnet-4",
    Name: "Claude Sonnet 4",
    Api: "anthropic-messages",
    Provider: "anthropic",
    BaseUrl: "https://api.anthropic.com",
    Reasoning: false,
    Input: ["text", "image"],
    Cost: new ModelCost(0.003m, 0.015m, 0.0003m, 0.00375m),
    ContextWindow: 200000,
    MaxTokens: 16384,
    Headers: null,
    Compat: null
);
modelRegistry.Register("anthropic", model);

var llmClient = new LlmClient(apiProviders, modelRegistry);
```

### Step 3: Create tools

```csharp
using BotNexus.AgentCore.Tools;
using BotNexus.CodingAgent.Tools;

var workingDir = Directory.GetCurrentDirectory();

IAgentTool[] tools =
[
    new ReadTool(workingDir),
    new WriteTool(workingDir),
    new EditTool(workingDir),
    new ShellTool(workingDir),
    new GrepTool(workingDir),
    new GlobTool(workingDir),
    new ListDirectoryTool(workingDir)
];
```

### Step 4: Build the system prompt

```csharp
using BotNexus.CodingAgent;

var promptContext = new SystemPromptContext(
    WorkingDirectory: workingDir,
    GitBranch: "main",
    GitStatus: null,
    PackageManager: "dotnet",
    ToolNames: tools.Select(t => t.Name).ToList(),
    Skills: [],
    CustomInstructions: "Focus on clean, well-tested C# code."
);

var systemPrompt = new SystemPromptBuilder().Build(promptContext);
```

### Step 5: Wire hooks

```csharp
using BotNexus.CodingAgent.Hooks;
using BotNexus.CodingAgent.Utils;

var safetyHooks = new SafetyHooks();
var auditHooks = new AuditHooks(verbose: true);
var config = CodingAgentConfig.Load(workingDir);
```

### Step 6: Create the agent

```csharp
using BotNexus.AgentCore;
using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Types;

var agent = new Agent(new AgentOptions
{
    Model = model,
    LlmClient = llmClient,
    GetApiKey = async (provider, ct) =>
        Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
    GenerationSettings = new SimpleStreamOptions
    {
        MaxTokens = 8192,
        Temperature = 0f
    },
    InitialState = new AgentInitialState
    {
        SystemPrompt = systemPrompt,
        Tools = tools
    },
    ToolExecutionMode = ToolExecutionMode.Sequential,
    BeforeToolCall = async (context, ct) =>
        await safetyHooks.ValidateAsync(context, config),
    AfterToolCall = async (context, ct) =>
        await auditHooks.AuditAsync(context)
});
```

### Step 7: Subscribe to events and run

```csharp
agent.Subscribe(async (evt, ct) =>
{
    switch (evt)
    {
        case MessageUpdateEvent { ContentDelta: not null } update:
            Console.Write(update.ContentDelta);
            break;
        case ToolExecutionStartEvent toolStart:
            Console.WriteLine($"\n🔧 {toolStart.ToolName}");
            break;
        case ToolExecutionEndEvent toolEnd:
            Console.WriteLine(toolEnd.IsError ? "  ❌ Failed" : "  ✅ Done");
            break;
    }
});

var result = await agent.PromptAsync("Read the README.md and summarize the project");

Console.WriteLine($"\n\n--- {result.Count} messages produced ---");
```

### Step 8: Add session persistence (optional)

```csharp
using BotNexus.CodingAgent.Session;

var sessionManager = new SessionManager();
var session = await sessionManager.CreateSessionAsync(workingDir, "my-session");

// After each turn, save
await sessionManager.SaveSessionAsync(session, agent.State.Messages);

// Resume later
var (resumed, messages) = await sessionManager.ResumeSessionAsync(session.Id, workingDir);
```

---

## Message flow diagram

```
User types: "Fix the bug in auth.cs"
│
▼
┌──────────────────────────────────────────────────────────────┐
│  CodingAgent / Interactive Loop                              │
│  Creates UserMessage("Fix the bug in auth.cs")               │
└──────────────────────┬───────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────┐
│  Agent.PromptAsync(userMessage)                              │
│  ├── Acquires SemaphoreSlim (single-run lock)                │
│  ├── Appends userMessage to State.Messages                   │
│  └── Delegates to AgentLoopRunner.RunAsync()                 │
└──────────────────────┬───────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────┐
│  AgentLoopRunner — Turn 1                                    │
│  ├── Drain steering queue (empty)                            │
│  ├── TransformContext (optional)                             │
│  ├── ConvertToLlm: AgentMessage[] → provider Message[]       │
│  ├── Build Context(systemPrompt, messages, tools)            │
│  └── LlmClient.StreamSimple(model, context, options)         │
└──────────────────────┬───────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────┐
│  Provider (e.g., AnthropicProvider)                           │
│  ├── Build HTTP request body (JSON)                          │
│  ├── POST to https://api.anthropic.com/v1/messages           │
│  ├── Parse SSE response line by line                         │
│  └── Push events into LlmStream:                             │
│      StartEvent → TextDelta("Let me read") → TextDelta(...)  │
│      → ToolCallStart(read) → ToolCallDelta({path:auth.cs})   │
│      → ToolCallEnd → DoneEvent(ToolUse)                      │
└──────────────────────┬───────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────┐
│  StreamAccumulator                                           │
│  ├── Consumes LlmStream events                               │
│  ├── Emits MessageStart → MessageUpdate → MessageEnd          │
│  └── Returns AssistantAgentMessage (text + tool calls)        │
└──────────────────────┬───────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────┐
│  ToolExecutor (FinishReason = ToolUse)                       │
│  ├── Find ReadTool by name "read"                            │
│  ├── PrepareArgumentsAsync({path: "auth.cs"})                │
│  ├── BeforeToolCall hook → SafetyHooks.ValidateAsync() ✅     │
│  ├── ReadTool.ExecuteAsync() → returns file contents          │
│  ├── AfterToolCall hook → AuditHooks.AuditAsync()            │
│  └── Create ToolResultAgentMessage                            │
└──────────────────────┬───────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────┐
│  AgentLoopRunner — Turn 2                                    │
│  ├── Timeline: [user, assistant(tool_use), tool_result]       │
│  ├── ConvertToLlm → send to provider                         │
│  ├── Provider returns: "Here's the fix..." + edit tool call   │
│  └── ToolExecutor: EditTool fixes auth.cs                     │
└──────────────────────┬───────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────┐
│  AgentLoopRunner — Turn 3                                    │
│  ├── Provider returns: "Done. I've fixed the auth bug."       │
│  ├── FinishReason = Stop (no more tool calls)                 │
│  └── Emit AgentEndEvent                                       │
└──────────────────────┬───────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────┐
│  Return to caller                                             │
│  ├── agent.PromptAsync returns all produced messages           │
│  ├── Session saved via SessionManager                          │
│  └── UI displays final response                                │
└──────────────────────────────────────────────────────────────┘
```

---

## Further reading

- [Provider architecture](providers.md) — deep dive into LLM communication
- [Agent event system](agent-events.md) — lifecycle events and hooks
- [Tool security model](tool-security.md) — safety enforcement
- [Architecture overview](00-overview.md) — high-level system design
- [Glossary](05-glossary.md) — all key terms
