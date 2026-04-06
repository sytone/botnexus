# Agent event system

> **Audience:** Developers building agents on `BotNexus.AgentCore` who need to understand the lifecycle, events, hooks, and message queues.
> **Prerequisites:** C#/.NET, async/await, familiarity with [provider system](01-providers.md).
> **Source code:** `src/agent/BotNexus.AgentCore/`

## What you'll learn

1. Agent lifecycle (create → prompt → loop → tool calls → complete)
2. All event types and when they fire
3. Subscribe/unsubscribe pattern
4. Hook system (BeforeToolCall, AfterToolCall)
5. Steering and follow-up message queues
6. Error handling and abort flow

---

## Agent lifecycle

The `Agent` class (`Agent.cs`) is a stateful wrapper around the `AgentLoopRunner`. It owns the conversation timeline, enforces single-run concurrency via `SemaphoreSlim`, and exposes the public API.

### Lifecycle phases

```
┌──────────┐   PromptAsync()   ┌──────────┐   AgentLoopRunner   ┌──────────┐
│   Idle   │──────────────────▶│ Running  │───────────────────▶ │   Loop   │
│          │                   │          │                      │          │
│          │◀──────────────────│          │◀─────────────────── │          │
│          │   AgentEndEvent   │          │   (loop complete)   │          │
└──────────┘                   └──────────┘                      └──────────┘
     │                              │
     │ AbortAsync()                 │ Cancellation
     │                              ▼
     │                         ┌──────────┐
     └────────────────────────▶│ Aborting │
                               └──────────┘
```

### Creating an agent

```csharp
var agent = new Agent(new AgentOptions
{
    Model = model,
    LlmClient = llmClient,
    GetApiKey = async (provider, ct) => Environment.GetEnvironmentVariable("MY_API_KEY"),
    GenerationSettings = new SimpleStreamOptions
    {
        MaxTokens = 4096,
        Temperature = 0.7f
    },
    InitialState = new AgentInitialState
    {
        SystemPrompt = "You are a helpful assistant.",
        Tools = [new MyCustomTool()]
    }
});
```

### Entry points

| Method | When to use |
|---|---|
| `PromptAsync(string)` | Start a new run with a text prompt |
| `PromptAsync(AgentMessage)` | Start a run with a structured message (text + images) |
| `PromptAsync(IReadOnlyList<AgentMessage>)` | Start a run with multiple messages |
| `ContinueAsync()` | Continue the loop without a new message (e.g., retry after error) |

All entry points return `Task<IReadOnlyList<AgentMessage>>` — the messages produced during the run.

### The agent loop

`AgentLoopRunner.RunAsync()` drives the core turn loop:

```
1. Emit AgentStartEvent
2. Append prompt messages to timeline
3. OUTER LOOP:
   │
   ├── 4. Drain steering messages (inject at turn boundary)
   │
   ├── 5. INNER LOOP (while tool calls pending or steering messages queued):
   │   │
   │   ├── 6. Emit TurnStartEvent
   │   ├── 7. Transform context (if TransformContext delegate provided)
   │   ├── 8. Convert agent messages → provider messages (ConvertToLlm)
   │   ├── 9. Call LLM via LlmClient → get LlmStream
   │   ├── 10. StreamAccumulator: consume stream → emit Message events
   │   ├── 11. If FinishReason is Error/Aborted → exit loop
   │   ├── 12. If tool calls present → ToolExecutor runs them
   │   ├── 13. Append tool results to timeline
   │   ├── 14. Emit TurnEndEvent
   │   └── 15. Drain steering messages again → back to step 5
   │
   ├── 16. Drain follow-up messages
   └── 17. If follow-ups exist → back to step 3; else exit
   
18. Emit AgentEndEvent
```

**Retry logic (step 9):** If the LLM returns a transient error (429 rate limit, 502/503/504 service error, timeout), the loop retries up to 4 times with exponential backoff (500ms, 1s, 2s). Context overflow errors trigger automatic compaction — the loop keeps the last ⅓ of messages (minimum 8) and retries.

---

## All event types

Events are defined in `Types/AgentEvent.cs` and `Types/AgentEventType.cs`. Every event extends `AgentEvent(AgentEventType Type, DateTimeOffset Timestamp)`.

### Event reference

| Event | Type enum | Fired when | Key data |
|---|---|---|---|
| `AgentStartEvent` | `AgentStart` | First event in every `PromptAsync`/`ContinueAsync` run | — |
| `AgentEndEvent` | `AgentEnd` | Final event; agent becomes idle | `Messages` — all messages produced during this run |
| `TurnStartEvent` | `TurnStart` | Before each LLM call | — |
| `TurnEndEvent` | `TurnEnd` | After assistant message + tool results finalized | `Message` (assistant), `ToolResults` (may be empty) |
| `MessageStartEvent` | `MessageStart` | When message processing starts | `Message` — the in-progress message |
| `MessageUpdateEvent` | `MessageUpdate` | Streaming deltas during generation | `ContentDelta`, `IsThinking`, `ToolCallId`, `ToolName`, `ArgumentsDelta`, `FinishReason`, token counts |
| `MessageEndEvent` | `MessageEnd` | Message complete | `Message` — final complete message |
| `ToolExecutionStartEvent` | `ToolExecutionStart` | Before argument validation and hooks | `ToolCallId`, `ToolName`, `Args` (raw) |
| `ToolExecutionUpdateEvent` | `ToolExecutionUpdate` | During long-running tool execution | `ToolCallId`, `ToolName`, `Args`, `PartialResult` |
| `ToolExecutionEndEvent` | `ToolExecutionEnd` | After tool completes and hooks run | `ToolCallId`, `ToolName`, `Result`, `IsError` |

### Event sequence for a typical run

```
AgentStartEvent
│
├── TurnStartEvent (turn 1: user prompt → LLM)
│   ├── MessageStartEvent (user message)
│   ├── MessageEndEvent (user message)
│   ├── MessageStartEvent (assistant response)
│   ├── MessageUpdateEvent (content delta: "I'll read that file...")
│   ├── MessageUpdateEvent (content delta: more text)
│   ├── MessageUpdateEvent (tool call arguments streaming)
│   ├── MessageEndEvent (assistant response with tool call)
│   ├── ToolExecutionStartEvent (tool: "read", args: {path: "main.cs"})
│   ├── ToolExecutionEndEvent (result: file contents)
│   ├── MessageStartEvent (tool result message)
│   ├── MessageEndEvent (tool result message)
│   └── TurnEndEvent (assistant message + tool results)
│
├── TurnStartEvent (turn 2: tool results → LLM)
│   ├── MessageStartEvent (assistant response)
│   ├── MessageUpdateEvent (content: "Here's the fix...")
│   ├── MessageEndEvent (assistant response, FinishReason=Stop)
│   └── TurnEndEvent (no tool results)
│
└── AgentEndEvent (messages=[user, assistant, tool_result, assistant])
```

### MessageUpdateEvent in detail

`MessageUpdateEvent` is the most complex event. It carries incremental streaming data:

```csharp
public sealed record MessageUpdateEvent(
    AssistantAgentMessage Message,  // Current snapshot of the full message
    string? ContentDelta,           // New text since last update (null if not text)
    bool IsThinking,                // True if ContentDelta is thinking content
    string? ToolCallId,             // Set when streaming tool call arguments
    string? ToolName,               // Set when streaming tool call arguments
    string? ArgumentsDelta,         // New JSON arguments since last update
    StopReason? FinishReason,       // Set on the final update
    int? InputTokens,               // Set when usage becomes available
    int? OutputTokens,              // Set when usage becomes available
    DateTimeOffset Timestamp
) : AgentEvent(AgentEventType.MessageUpdate, Timestamp);
```

Use `ContentDelta` for real-time text display. Use `ArgumentsDelta` to show tool call arguments being constructed. Use `IsThinking` to differentiate thinking content from visible response text.

---

## Subscribe/unsubscribe pattern

### Subscribing to events

```csharp
var agent = new Agent(options);

// Subscribe — returns IDisposable for cleanup
IDisposable subscription = agent.Subscribe(async (agentEvent, cancellationToken) =>
{
    switch (agentEvent)
    {
        case MessageUpdateEvent update when update.ContentDelta is not null:
            Console.Write(update.IsThinking ? $"[thinking] {update.ContentDelta}" : update.ContentDelta);
            break;

        case ToolExecutionStartEvent toolStart:
            Console.WriteLine($"\n🔧 {toolStart.ToolName}({toolStart.Args})");
            break;

        case ToolExecutionEndEvent toolEnd:
            Console.WriteLine(toolEnd.IsError ? "❌ Failed" : "✅ Done");
            break;

        case AgentEndEvent end:
            Console.WriteLine($"\n--- Run complete: {end.Messages.Count} messages ---");
            break;
    }
});

// Run the agent
await agent.PromptAsync("Fix the bug in auth.cs");

// Unsubscribe when done
subscription.Dispose();
```

### Subscription rules

- **Listeners are awaited in subscription order.** If listener A was subscribed before listener B, A's handler completes before B's handler is called for each event.
- **Listeners receive the active abort signal.** The `CancellationToken` passed to the listener is tied to the current run — it cancels when `AbortAsync()` is called.
- **Thread-safe subscription.** You can subscribe/unsubscribe from any thread. New listeners take effect from the next turn boundary.
- **Listener errors don't crash the agent.** Exceptions from listeners are caught and logged.

### Multiple subscriptions

```csharp
// UI subscriber — displays text
var uiSub = agent.Subscribe(async (evt, ct) =>
{
    if (evt is MessageUpdateEvent { ContentDelta: not null } update)
        await RenderToUI(update.ContentDelta);
});

// Logging subscriber — records everything
var logSub = agent.Subscribe(async (evt, ct) =>
{
    logger.LogDebug("Event: {Type} at {Timestamp}", evt.Type, evt.Timestamp);
});

// Run agent...
await agent.PromptAsync("Hello");

// Clean up
uiSub.Dispose();
logSub.Dispose();
```

---

## Hook system

Hooks intercept tool execution at two points: **before** (to validate/block) and **after** (to transform/audit).

### BeforeToolCall

Runs after argument validation (`PrepareArgumentsAsync`) and before `ExecuteAsync`. Can block the tool call.

**Context record:**

```csharp
public sealed record BeforeToolCallContext(
    AssistantAgentMessage AssistantMessage,             // The message requesting the call
    ToolCallContent ToolCallRequest,                    // ID, name, and arguments
    IReadOnlyDictionary<string, object?> ValidatedArgs, // Args after PrepareArgumentsAsync
    AgentContext AgentContext                            // Full agent context snapshot
);
```

**Result record:**

```csharp
public sealed record BeforeToolCallResult(
    bool Block,          // true → tool call is prevented
    string? Reason       // Error message returned to the LLM
);
```

**Example — block dangerous shell commands:**

```csharp
var agent = new Agent(new AgentOptions
{
    // ... other options ...
    BeforeToolCall = async (context, ct) =>
    {
        if (context.ToolCallRequest.Name == "bash")
        {
            var command = context.ValidatedArgs["command"]?.ToString() ?? "";
            if (command.Contains("rm -rf /"))
                return new BeforeToolCallResult(Block: true, Reason: "Blocked: destructive command");
        }
        return null; // Allow the call
    }
});
```

### AfterToolCall

Runs after `ExecuteAsync` completes (or fails). Can transform results, redact content, or override error status.

**Context record:**

```csharp
public sealed record AfterToolCallContext(
    AssistantAgentMessage AssistantMessage,
    ToolCallContent ToolCallRequest,
    IReadOnlyDictionary<string, object?> ValidatedArgs,
    AgentToolResult Result,         // Execution result (before transformation)
    bool IsError,                   // Whether execution failed
    AgentContext AgentContext
);
```

**Result record:**

```csharp
public sealed record AfterToolCallResult(
    IReadOnlyList<AgentToolContent>? Content = null,  // Replace result content
    object? Details = null,                            // Replace metadata
    bool? IsError = null                               // Override error flag
);
// Only non-null fields are applied; null leaves the original value unchanged.
```

**Example — redact secrets from tool output:**

```csharp
AfterToolCall = async (context, ct) =>
{
    if (context.ToolCallRequest.Name == "bash")
    {
        var output = context.Result.Content.FirstOrDefault()?.Value ?? "";
        if (output.Contains("SECRET_KEY"))
        {
            var redacted = output.Replace("SECRET_KEY=abc123", "SECRET_KEY=***");
            return new AfterToolCallResult(
                Content: [new AgentToolContent(AgentToolContentType.Text, redacted)]
            );
        }
    }
    return null; // No transformation
};
```

### Hook execution order in ToolExecutor

```
For each tool call in the assistant message:
│
├── 1. Emit ToolExecutionStartEvent (with raw args)
├── 2. Find tool by name (case-sensitive)
├── 3. PrepareArgumentsAsync (validate/coerce arguments)
├── 4. BeforeToolCall hook
│     ├── Block=true → return error result, skip execution
│     └── Block=false or null → proceed
├── 5. tool.ExecuteAsync(toolCallId, validatedArgs, ct, updateCallback)
├── 6. AfterToolCall hook (may transform result)
├── 7. Emit ToolExecutionEndEvent
└── 8. Create ToolResultAgentMessage
```

### Sequential vs parallel execution

Configure via `AgentOptions.ToolExecutionMode`:

- **`Sequential`** (default): Tools execute one at a time in assistant message order. Safer for tools with side effects (file writes, shell commands).
- **`Parallel`**: Tools execute concurrently. Preparation phase (steps 1-4) is still sequential; only `ExecuteAsync` runs in parallel. Results are sorted back into original order before emitting events.

```csharp
var agent = new Agent(new AgentOptions
{
    ToolExecutionMode = ToolExecutionMode.Parallel,
    // ...
});
```

---

## Steering and follow-up message queues

The agent supports injecting messages at two different points in the loop.

### Steering messages

**When:** Drained at every turn boundary (before each LLM call).
**Use case:** User interruptions, context updates, mid-run corrections.

```csharp
// From another thread, while agent is running:
agent.Steer(new UserMessage("Actually, use Python instead of C#"));
```

The steering message is injected before the next LLM call. The model sees it as a new user message and adjusts its behavior.

### Follow-up messages

**When:** Drained after the current run completes (no more tool calls pending).
**Use case:** Chained workflows, continuation prompts.

```csharp
// Queue a follow-up that runs after the current task finishes
agent.FollowUp(new UserMessage("Now run the tests"));
```

### Queue modes

Both queues support two drain modes via `QueueMode`:

| Mode | Behavior |
|---|---|
| `OneAtATime` | Drain one message per loop iteration (spreads across turns) |
| `All` | Drain all queued messages in a single batch |

```csharp
agent.SteeringMode = QueueMode.All;      // Inject all steering messages at once
agent.FollowUpMode = QueueMode.OneAtATime; // Process follow-ups one at a time
```

### Queue management

```csharp
agent.ClearSteeringQueue();    // Drop all pending steering messages
agent.ClearFollowUpQueue();    // Drop all pending follow-up messages
agent.ClearAllQueues();        // Drop everything

bool hasQueued = agent.HasQueuedMessages; // Check if anything is pending
```

### PendingMessageQueue internals

`PendingMessageQueue` (`PendingMessageQueue.cs`) is a thread-safe internal class that backs both queues. It uses a `lock` for concurrency and supports both `OneAtATime` and `All` drain modes.

---

## Error handling and abort flow

### Transient errors

The loop runner handles transient errors with automatic retry:

- **Rate limits (HTTP 429):** Retry with exponential backoff.
- **Service errors (502, 503, 504):** Retry with exponential backoff.
- **Timeouts:** Retry with exponential backoff.
- **Max retries:** 4 attempts, delays of 500ms, 1s, 2s (capped by `MaxRetryDelayMs`).

### Context overflow

When the LLM returns a context-window-exceeded error (detected by `ContextOverflowDetector`), the loop compacts the message timeline:

1. Keeps the most recent ⅓ of messages (minimum 8).
2. Retries the LLM call with the compacted context.
3. If compaction fails, the error propagates.

### Abort flow

```csharp
// Start a long-running task
var runTask = agent.PromptAsync("Refactor the entire codebase");

// Cancel from another thread
await agent.AbortAsync();
// AbortAsync:
//   1. Sets Status to Aborting
//   2. Cancels the internal CancellationTokenSource
//   3. Waits for the active run to settle
//   4. Swallows OperationCanceledException

// After abort, agent is Idle again
Debug.Assert(agent.Status == AgentStatus.Idle);
```

### Reset

```csharp
agent.Reset();
// Cancels any active run
// Clears all message queues
// Clears the entire conversation timeline
// Resets error/streaming/pending state
// Sets Status to Idle
// WARNING: Discards all conversation history
```

---

## AgentOptions reference

`AgentOptions` is a record that configures the agent at creation time. Once set, it is frozen for the agent's lifetime.

```csharp
public record AgentOptions(
    AgentInitialState? InitialState,            // Optional initial state seed
    LlmModel Model,                             // Model for provider calls
    LlmClient LlmClient,                        // LLM client for streaming
    ConvertToLlmDelegate? ConvertToLlm,          // Message converter (default: built-in)
    TransformContextDelegate? TransformContext,   // Context transformer before LLM
    GetApiKeyDelegate GetApiKey,                  // API key resolver
    GetMessagesDelegate? GetSteeringMessages,     // Steering message provider
    GetMessagesDelegate? GetFollowUpMessages,     // Follow-up message provider
    ToolExecutionMode ToolExecutionMode,          // Sequential or Parallel
    BeforeToolCallDelegate? BeforeToolCall,        // Pre-tool-call hook
    AfterToolCallDelegate? AfterToolCall,          // Post-tool-call hook
    SimpleStreamOptions GenerationSettings,       // Temperature, maxTokens, etc.
    QueueMode SteeringMode,                       // Queue drain mode for steering
    QueueMode FollowUpMode,                       // Queue drain mode for follow-ups
    string? SessionId,                            // Caller-provided session ID
    Action<string>? OnDiagnostic,                 // Non-fatal diagnostic callback
    int? MaxRetryDelayMs                          // Max retry backoff delay (ms); null = uncapped
);
```

### Key delegates

| Delegate | Signature | Purpose |
|---|---|---|
| `ConvertToLlmDelegate` | `(AgentMessage[], ct) → Message[]` | Convert agent messages to provider format |
| `TransformContextDelegate` | `(AgentMessage[], ct) → AgentMessage[]` | Transform messages before LLM call |
| `GetApiKeyDelegate` | `(provider, ct) → string?` | Resolve API key on demand |
| `GetMessagesDelegate` | `(ct) → AgentMessage[]` | Produce steering or follow-up messages |
| `BeforeToolCallDelegate` | `(BeforeToolCallContext, ct) → BeforeToolCallResult?` | Pre-tool-call interception |
| `AfterToolCallDelegate` | `(AfterToolCallContext, ct) → AfterToolCallResult?` | Post-tool-call interception |

---

## AgentState

`AgentState` is the mutable runtime state of the agent, accessible via `agent.State`:

```csharp
AgentState state = agent.State;

state.SystemPrompt       // Active system prompt
state.Model              // Active LlmModel
state.ThinkingLevel      // Extended reasoning level (if set)
state.Tools              // Currently registered tools
state.Messages           // Conversation timeline
state.IsRunning          // Whether a run is active
state.IsStreaming         // True between MessageStart and MessageEnd
state.StreamingMessage   // Current in-progress assistant message (during streaming)
state.PendingToolCalls   // Set of tool call IDs currently executing
state.ErrorMessage       // Latest runtime error
```

You can mutate `Tools`, `Model`, `SystemPrompt`, and `ThinkingLevel` between runs. Changes take effect on the next `PromptAsync` call.

---

## Further reading

- [Provider system](01-providers.md) — how LLM communication works
- [Tool security model](tool-security.md) — safety hooks and path containment
- [Building your own agent](04-building-your-own.md) — wiring everything together
- [Glossary](05-glossary.md) — all key terms
