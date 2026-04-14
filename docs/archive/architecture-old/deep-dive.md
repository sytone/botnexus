# 10 — Architecture Deep Dive

This document provides detailed technical architecture information for BotNexus, including class hierarchies, sequence diagrams, and implementation patterns. It supplements the [Architecture Overview](00-overview.md) with deeper technical context and C# idiom details.

> **Prerequisites:** Familiarity with [Architecture Overview](00-overview.md), [Provider System](01-providers.md), [Agent Core](02-agent-core.md), and [Coding Agent](03-coding-agent.md).

---

## Part 1: Message model hierarchy

The message model is the heart of BotNexus. It's polymorphic, immutable, and designed for both provider APIs and internal agent state.

### Message hierarchy (provider layer)

```
Message (abstract record)
  ├─ UserMessage
  │   └─ Content: UserMessageContent (string | ContentBlock[])
  │
  ├─ AssistantMessage
  │   └─ Content: ContentBlock[]
  │
  └─ ToolResultMessage
      └─ Content: ContentBlock[]
```

All three inherit `Timestamp: long`. They use `[JsonPolymorphic]` with discriminator `"role"`:

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "role")]
[JsonDerivedType(typeof(UserMessage), "user")]
[JsonDerivedType(typeof(AssistantMessage), "assistant")]
[JsonDerivedType(typeof(ToolResultMessage), "toolResult")]
public abstract record Message(long Timestamp);

public sealed record UserMessage(
    UserMessageContent Content,                    // string or ContentBlock[]
    long Timestamp
) : Message(Timestamp);

public sealed record AssistantMessage(
    IReadOnlyList<ContentBlock> Content,           // May contain Text/Thinking/ToolCall
    string Api,                                    // "anthropic-messages", "openai-completions", etc.
    string Provider,                               // "anthropic", "openai", etc.
    string ModelId,                                // Model identifier
    Usage Usage,                                   // Token counts and costs
    StopReason StopReason,                        // Why generation stopped
    string? ErrorMessage,                          // Error details if StopReason is Error
    string? ResponseId,                            // Provider-specific response ID
    long Timestamp
) : Message(Timestamp);

public sealed record ToolResultMessage(
    string ToolCallId,                             // Linked to ToolCallContent.Id
    string ToolName,                               // e.g., "read", "bash"
    IReadOnlyList<ContentBlock> Content,           // Result content (TextContent, ImageContent)
    bool IsError,                                  // Distinguish success from failure
    long Timestamp,
    object? Details = null                         // Provider-specific metadata
) : Message(Timestamp);
```

### ContentBlock hierarchy

Content is polymorphic and extensible:

```
ContentBlock (abstract record)
  ├─ TextContent
  │   └─ Text: string
  │
  ├─ ThinkingContent
  │   └─ Thinking: string
  │
  ├─ ImageContent
  │   ├─ Format: string ("url", "base64", ...)
  │   ├─ Source: string (URL or base64 data)
  │   └─ MediaType: string? ("image/jpeg", etc.)
  │
  └─ ToolCallContent
      ├─ Id: string
      ├─ Name: string
      ├─ Arguments: Dictionary<string, object>
      └─ ThoughtSignature: string?
```

**Design rationale:**
- All subtypes are sealed records — no runtime subclassing.
- `ToolCallContent.Arguments` is a `Dictionary<string, object>` so JSON deserialization is flexible.
- `ImageContent.Format` allows providers to specify how the image is encoded.
- `ThinkingContent` is separate because thinking is never mixed with text or tool calls in an `AssistantMessage`.

### Agent-level message model

The agent layer has its own message abstraction that maps to the provider model:

```
AgentMessage (abstract record)
  ├─ UserAgentMessage
  │   └─ Content: string
  │
  ├─ AssistantAgentMessage
  │   └─ Content: AgentToolContent[] (computed from provider ContentBlock[])
  │
  └─ ToolResultAgentMessage
      └─ Content: AgentToolContent[]
```

The reason for this separation: the agent cares about tool names and execution state, not about every `ContentBlock` type. `MessageConverter` bridges between them:

```csharp
public static class MessageConverter
{
    public static IReadOnlyList<Message> ToProviderMessages(
        IReadOnlyList<AgentMessage> agentMessages)
    {
        // Convert each AgentMessage to provider Message
        // UserAgentMessage → UserMessage with string content
        // AssistantAgentMessage → AssistantMessage with ContentBlock[]
        // ToolResultAgentMessage → ToolResultMessage
    }

    public static IReadOnlyList<AgentMessage> FromProviderMessages(
        IReadOnlyList<Message> providerMessages)
    {
        // Reverse conversion
        // Extract relevant metadata to produce agent-level messages
    }
}
```

---

## Part 2: The agent loop — detailed sequence

The agent loop is the heart of the system. Here's a detailed sequence diagram showing all major steps:

```
┌─────────────────────────────────────────────────────────────────────────────────────────┐
│ User calls agent.PromptAsync("user input")                                              │
└─────────────────────────────────────────────────────────────────────────────────────────┘

1. Agent acquires semaphore (single-run concurrency)
   Agent.PendingMessageQueue.Add(new UserAgentMessage("user input"))
   Emit: AgentStartEvent

2. AgentLoopRunner.RunAsync() appends prompts to timeline
   └─ Creates AgentContext(systemPrompt, messages, tools)
   Emit: TurnStartEvent, MessageStartEvent, MessageEndEvent

3. Enter main loop: while (!abort && !done) {

   ┌─ Step 3a: Drain steering messages
   │ if (pendingMessages.Count > 0) {
   │     Dequeue message, append to timeline
   │     Emit: MessageStartEvent, MessageEndEvent
   │ }
   │
   ├─ Step 3b: Build LLM request
   │ var context = new AgentContext(systemPrompt, messages, tools)
   │ var providerMessages = MessageConverter.ToProviderMessages(messages)
   │ // Convert to provider format (Anthropic, OpenAI, etc.)
   │
   ├─ Step 3c: Call LLM (stream events)
   │ llmStream = llmClient.Stream(model, context, options)
   │ await foreach (var evt in llmStream) {
   │     switch (evt) {
   │         case StartEvent:
   │             Emit: MessageStartEvent(AssistantMessage, ...)
   │         case TextDeltaEvent:
   │             Accumulate content
   │             Emit: MessageUpdateEvent
   │         case ToolCallStartEvent:
   │             Track pending tool call
   │         case ToolCallEndEvent:
   │             Complete tool call
   │         case DoneEvent:
   │             Emit: MessageEndEvent
   │             message = evt.Message
   │     }
   │ }
   │
   ├─ Step 3d: Append assistant response
   │ timeline.Add(message)  // AssistantMessage
   │
   ├─ Step 3e: Execute tool calls (if any)
   │ if (toolCalls.Count > 0) {
   │     Emit: ToolExecutionStartEvent
   │     for (or in parallel):
   │         beforeHook(toolCall)  // Can block
   │         tool.ExecuteAsync(...)
   │         afterHook(toolCall, result)
   │         Emit: ToolExecutionUpdateEvent
   │     Emit: ToolExecutionEndEvent
   │     timeline.Add(ToolResultMessage(...))
   │ } else {
   │     done = true  // No more tool calls
   │ }
   │
   └─ Loop condition: if (!done && !tooManyIterations)
       goto Step 3a else exit

4. Emit: AgentEndEvent
   Release semaphore
   Return all messages produced

}
```

**Key observations:**
1. The loop is **stateless** — all state is passed as immutable `AgentContext`.
2. **Steering** happens at turn boundaries via `PendingMessageQueue`.
3. **Tool execution** is interruptible via before hooks.
4. **Events** are emitted continuously so the UI/CLI can render real-time updates.
5. **Context** is immutable — each turn computes a new snapshot.

### Steering vs follow-up

Two different queuing modes control how pending messages drain:

| Mode | Behavior | Use case |
|------|----------|----------|
| `QueueMode.All` | All pending messages drain at the next boundary | Rapid corrections before LLM call |
| `QueueMode.OneAtATime` | One message per turn boundary | Interactive prompting; pause between LLM calls |

Example:

```csharp
// Add steering messages (execute immediately at turn boundary)
agent.Steer(new UserAgentMessage("Wait, actually..."));

// Add follow-ups (wait for turn completion first)
agent.FollowUp(new UserAgentMessage("Now, let's continue"));
```

---

## Part 3: Provider abstraction layer

The provider layer abstracts away the differences between Anthropic, OpenAI, and other LLM APIs.

### Provider interface and implementations

```csharp
public interface IApiProvider
{
    string Api { get; }  // Routing key: "anthropic-messages", "openai-completions", etc.
    LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null);
    LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null);
}
```

Each provider is a thin adapter:

```
BotNexus Context (agnostic)
    ↓
Provider (Anthropic, OpenAI, etc.)
    ├─ Convert Context → Vendor request JSON
    ├─ Call vendor HTTP API
    ├─ Parse SSE stream
    └─ Emit AssistantMessageEvent instances
    ↓
LlmStream (unified event stream)
```

Example provider flow (Anthropic):

```
1. AnthropicProvider.Stream(model, context, options)
   ├─ Extract API key from options or environment
   ├─ Build Anthropic request:
   │  {
   │      "model": "claude-sonnet-4",
   │      "max_tokens": 4096,
   │      "system": context.SystemPrompt,
   │      "messages": [ { "role": "user", "content": "..." }, ... ],
   │      "tools": [ { "name": "read", "input_schema": {...} }, ... ]
   │  }
   ├─ POST to https://api.anthropic.com/v1/messages with stream=true
   ├─ Parse Server-Sent Events (SSE)
   └─ For each event:
       content_block_start → StartEvent
       content_block_delta → TextDeltaEvent / ThinkingDeltaEvent / ToolCallDeltaEvent
       content_block_stop → TextEndEvent / ThinkingEndEvent / ToolCallEndEvent
       message_stop → DoneEvent
       Error → ErrorEvent

2. Push each event into LlmStream

3. Caller awaits stream:
   await foreach (var evt in stream) { ... }
```

### Message conversion patterns

Each provider must translate between:
- **BotNexus format:** `Message[]` (polymorphic, provider-agnostic)
- **Vendor format:** JSON specific to that API

Example (Anthropic):

```csharp
// Incoming: BotNexus Message[]
public static class MessageConverter
{
    public static List<(string Role, object Content)> ToAnthropicMessages(
        IReadOnlyList<Message> messages)
    {
        var result = new List<(string, object)>();
        foreach (var msg in messages)
        {
            switch (msg)
            {
                case UserMessage um:
                    // Convert to { "role": "user", "content": [...] }
                    result.Add(("user", ConvertUserContent(um.Content)));
                    break;

                case AssistantMessage am:
                    // Convert to { "role": "assistant", "content": [...] }
                    // Anthropic format: [{ "type": "text", "text": "..." }, ...]
                    result.Add(("assistant", ConvertContentBlocks(am.Content)));
                    break;

                case ToolResultMessage tm:
                    // Convert to { "role": "user", "content": [{ "type": "tool_result", ... }] }
                    result.Add(("user", ConvertToolResult(tm)));
                    break;
            }
        }
        return result;
    }
}
```

---

## Part 4: Tool execution model

Tools are executed sequentially or in parallel, with before/after hooks at each step.

### Sequential execution (default)

```
┌────────────────────────────────────────────────────────────┐
│ ToolCalls: [read("file.cs"), bash("ls"), write("new.cs")] │
└────────────────────────────────────────────────────────────┘

1. BeforeToolCall(read)   → allowExecution? → YES
   ExecuteAsync(read)     → result
   AfterToolCall(read)    → maybeTransform(result)
   Emit: ToolExecutionUpdateEvent
   ↓
2. BeforeToolCall(bash)   → allowExecution? → YES
   ExecuteAsync(bash)     → result
   AfterToolCall(bash)    → maybeTransform(result)
   Emit: ToolExecutionUpdateEvent
   ↓
3. BeforeToolCall(write)  → allowExecution? → YES
   ExecuteAsync(write)    → result
   AfterToolCall(write)   → maybeTransform(result)
   Emit: ToolExecutionUpdateEvent
   ↓
All results appended to timeline, loop repeats
```

### Parallel execution

If `config.ToolExecutionMode == ToolExecutionMode.Parallel`:

```
Prepare phase (SEQUENTIAL):
  1. For each tool call: BeforeToolCall hook (can block)
  2. Accumulate approved tool calls

Execution phase (PARALLEL):
  1. Start tasks for all approved tools
  2. As each completes: AfterToolCall, Emit ToolExecutionUpdateEvent

Results phase (SEQUENTIAL):
  1. Collect all results
  2. Append to timeline
  3. Loop repeats
```

### Before/After hooks

```csharp
public delegate Task<BeforeToolCallResult> BeforeToolCallDelegate(
    BeforeToolCallContext context);

public record BeforeToolCallContext(
    string ToolCallId,
    string ToolName,
    Dictionary<string, object> Arguments,
    IReadOnlyList<AgentMessage> Timeline);

public record BeforeToolCallResult(
    bool Block = false,                           // true → skip this tool call
    string? BlockReason = null);

// Used by SafetyHooks to validate paths/commands
public static class SafetyHooks
{
    public static async Task<BeforeToolCallResult> ValidateBeforeToolCall(
        BeforeToolCallContext ctx,
        CodingAgentConfig config)
    {
        switch (ctx.ToolName)
        {
            case "bash":
                return ValidateBashCommand(ctx.Arguments, config);
            case "read":
                return ValidatePath(ctx.Arguments["path"]?.ToString(), config);
            case "write":
                return ValidatePath(ctx.Arguments["path"]?.ToString(), config);
            // ...
        }
        return new BeforeToolCallResult();
    }
}
```

---

## Part 5: Session model — DAG branching

Sessions are stored as JSONL (JSON Lines) with a directed acyclic graph (DAG) for branching:

```
.botnexus-agent/sessions/
├── main.jsonl                           # Trunk branch
│   Line 1: {"type":"UserMessage", "content":"help me..."}
│   Line 2: {"type":"AssistantMessage", "content":[...], "toolCalls":[...]}
│   Line 3: {"type":"ToolResultMessage", "toolName":"read", "content":[...]}
│   ...
│
├── experiment-v1.jsonl                  # Branch off main at line 5
│   Line 1-4: [copy from main]
│   Line 5: {"type":"UserMessage", "content":"actually, try a different approach"}
│   ...
│
└── debug-attempt.jsonl                  # Branch off experiment-v1 at line 10
    Line 1-9: [copy from experiment-v1]
    Line 10: {"type":"UserMessage", "content":"let me debug this"}
    ...
```

### Metadata stored in `.botnexus-agent/sessions/`

```
.botnexus-agent/sessions/
├── main.json                            # SessionInfo for main
│   {
│       "name": "main",
│       "parent": null,
│       "createdAt": "2025-04-05T...",
│       "messageCount": 42,
│       "currentLeaf": "main",
│       "model": "claude-sonnet-4",
│       "provider": "anthropic"
│   }
│
├── experiment-v1.json
│   {
│       "name": "experiment-v1",
│       "parent": "main",
│       "parentMessageIndex": 5,
│       "createdAt": "2025-04-05T...",
│       "messageCount": 10,
│       "currentLeaf": "experiment-v1",
│       "model": "claude-sonnet-4",
│       "provider": "anthropic"
│   }
│
└── debug-attempt.json
    {
        "name": "debug-attempt",
        "parent": "experiment-v1",
        "parentMessageIndex": 10,
        "createdAt": "2025-04-05T...",
        "messageCount": 5,
        "currentLeaf": "debug-attempt",
        "model": "claude-sonnet-4",
        "provider": "anthropic"
    }
```

### SessionManager operations

```csharp
public sealed class SessionManager
{
    // Create a new session
    public async Task<SessionInfo> CreateAsync(string name);

    // Save messages to current session
    public async Task SaveAsync(string sessionName, IReadOnlyList<AgentMessage> messages);

    // Load messages from session
    public async Task<IReadOnlyList<AgentMessage>> ResumeAsync(string sessionName);

    // Branch: create new session, copy parent's messages
    public async Task<SessionInfo> BranchAsync(
        string newName,
        string? parentName = null,
        int? branchAtIndex = null);

    // Switch active session
    public async Task SwitchAsync(string sessionName);

    // List all sessions with parent info
    public async Task<IReadOnlyList<SessionInfo>> ListAsync();

    // Compact old messages when context exceeds limit
    public async Task CompactAsync(
        string sessionName,
        int targetTokens,
        ILlmClient llmClient);
}
```

---

## Part 6: C# idiom mapping from pi-mono

BotNexus is a C# port of pi-mono (TypeScript). Here are the key pattern mappings:

| pi-mono (TypeScript) | BotNexus (C#) | Notes |
|---|---|---|
| `Promise<T>` | `Task<T>` | Async models differ |
| `AsyncGenerator<T>` | `IAsyncEnumerable<T>` | Streaming protocol |
| `Queue<T>` + polling | `System.Threading.Channels.Channel<T>` | Efficient async queuing |
| `class` with private fields | `sealed record` or `sealed class` | Immutability-first |
| `extends` (inheritance) | Records with composition | Hooks instead of subclassing |
| `interface` | `interface` | Same concept |
| `const` object + methods | `static class` with static methods | Stateless utilities |
| Union types + discriminator | `[JsonPolymorphic]` with derived types | Discriminated unions |
| Event emitters | `Func<T, Task>` callbacks | Functional event emission |

### Streaming patterns

**pi-mono (TypeScript):**
```typescript
async function* stream(context) {
    for await (const event of httpStream) {
        yield event;
    }
}
```

**BotNexus (C#):**
```csharp
public class LlmStream : IAsyncEnumerable<AssistantMessageEvent>
{
    private readonly Channel<AssistantMessageEvent> _channel = ...;

    public async IAsyncEnumerator<AssistantMessageEvent> GetAsyncEnumerator(...)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return evt;
        }
    }
}
```

### State management

**pi-mono:** Immutable state objects passed through functions.
**BotNexus:** Same pattern using `record` types for immutability and `AgentContext` for snapshots.

---

## Part 7: Cross-layer data flow

Complete end-to-end flow with all transformations:

```
INPUT: User calls agent.PromptAsync("Fix the bug in auth.cs")
│
├─ Agent.PromptAsync()
│  └─ Append UserAgentMessage to PendingMessageQueue
│
├─ AgentLoopRunner.RunAsync()
│  ├─ Pop UserAgentMessage from queue
│  ├─ Append to timeline: timeline.Add(UserAgentMessage("Fix the bug..."))
│  │
│  ├─ Create AgentContext(systemPrompt, timeline, tools)
│  │  └─ timeline: [UserAgentMessage, ...]
│  │
│  ├─ MessageConverter.ToProviderMessages()
│  │  └─ UserAgentMessage → UserMessage(Content: "Fix the bug...")
│  │
│  ├─ LlmClient.Stream(model, context, options)
│  │  ├─ Resolve provider: registry.Get("anthropic-messages") → AnthropicProvider
│  │  │
│  │  ├─ AnthropicProvider.Stream()
│  │  │  ├─ Build JSON request
│  │  │  ├─ POST to https://api.anthropic.com/v1/messages?stream=true
│  │  │  ├─ Parse SSE: content_block_delta events
│  │  │  └─ Push into LlmStream
│  │  │     ├─ TextDeltaEvent("The issue is...")
│  │  │     ├─ ToolCallStartEvent(id="tcall_1", name="read")
│  │  │     ├─ ToolCallDeltaEvent(arguments: {"path": "auth.cs"})
│  │  │     ├─ ToolCallEndEvent
│  │  │     └─ DoneEvent(AssistantMessage)
│  │  │
│  │  └─ Return LlmStream
│  │
│  ├─ StreamAccumulator consumes LlmStream
│  │  ├─ TextDeltaEvent → Accumulate
│  │  ├─ ToolCallEndEvent → Build ToolCallContent
│  │  └─ DoneEvent → Emit MessageEndEvent + AssistantMessage
│  │
│  ├─ Append AssistantMessage to timeline
│  │  └─ timeline: [UserAgentMessage, AssistantAgentMessage(toolCalls=[read("auth.cs")])]
│  │
│  ├─ ToolExecutor.ExecuteAsync()
│  │  ├─ Find tool: registry.Get("read")
│  │  ├─ BeforeToolCall hook (SafetyHooks validates path)
│  │  ├─ ReadTool.ExecuteAsync(path="auth.cs")
│  │  │  └─ File.ReadAllLines("auth.cs") → string[]
│  │  ├─ AfterToolCall hook (AuditHooks logs timing)
│  │  └─ Build ToolResultMessage(toolName="read", content=[TextContent("...")])
│  │
│  ├─ Append ToolResultMessage to timeline
│  │  └─ timeline: [UserAgentMessage, AssistantAgentMessage, ToolResultAgentMessage]
│  │
│  └─ Loop again: goto step "Drain steering messages"
│     (AgentLoopRunner calls Stream() again with full timeline)
│
├─ When LLM returns with StopReason=Stop (no more tool calls)
│  └─ Exit loop, Emit AgentEndEvent
│
OUTPUT: All messages produced during the run
```

---

## Part 8: Concurrency and thread safety

### Single-run concurrency

```csharp
public sealed class Agent
{
    private readonly SemaphoreSlim _runSemaphore = new(1, 1);

    public async Task PromptAsync(string userMessage)
    {
        await _runSemaphore.WaitAsync();
        try
        {
            // Only one PromptAsync/ContinueAsync can run at a time
            await AgentLoopRunner.RunAsync(...);
        }
        finally
        {
            _runSemaphore.Release();
        }
    }
}
```

### Registry thread safety

```csharp
public sealed class ApiProviderRegistry
{
    private readonly ConcurrentDictionary<string, Registration> _registry = new();

    public void Register(IApiProvider provider, string? sourceId = null)
    {
        _registry.TryAdd(provider.Api, new Registration(provider, sourceId));
    }

    public IApiProvider? Get(string api)
    {
        return _registry.TryGetValue(api, out var registration)
            ? registration.Provider
            : null;
    }
}
```

### Message queue thread safety

```csharp
public sealed class PendingMessageQueue
{
    private readonly Queue<AgentMessage> _steering = new();
    private readonly Queue<AgentMessage> _followUp = new();
    private readonly Lock _lock = new();

    public void Queue(AgentMessage message, QueueMode mode)
    {
        lock (_lock)
        {
            if (mode == QueueMode.FollowUp)
                _followUp.Enqueue(message);
            else
                _steering.Enqueue(message);
        }
    }

    public IReadOnlyList<AgentMessage> Drain(QueueMode mode)
    {
        lock (_lock)
        {
            var queue = mode == QueueMode.FollowUp ? _followUp : _steering;
            var result = queue.ToList();
            queue.Clear();
            return result;
        }
    }
}
```

---

## What's next

- **[Provider System](01-providers.md)** — Message model and routing
- **[Provider Development Guide](11-provider-development-guide.md)** — Implement a new IApiProvider
- **[Agent Core](02-agent-core.md)** — Hook system and event model
- **[Coding Agent](03-coding-agent.md)** — Built-in tools and extensions
