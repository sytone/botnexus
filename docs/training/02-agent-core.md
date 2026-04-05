# Agent core

The agent core is the orchestration engine of BotNexus. It manages the cycle of sending context to an LLM, parsing the response, executing tools, and repeating until the model completes its work. Everything above the [provider layer](01-providers.md) and below the [coding agent](03-coding-agent.md) lives here — state management, the turn loop, streaming accumulation, tool execution, event dispatch, and hook-based extensibility.

This document consolidates the three areas that make up the agent core: the `Agent` class and its state model, the `AgentLoopRunner` turn engine, and the `ToolExecutor` pipeline.

## Agent class — State and lifecycle

`Agent` is the top-level stateful wrapper that consumers interact with. It owns the runtime state, serializes access to the loop runner, and dispatches events to subscribers.

### Key properties

| Property | Type | Description |
|----------|------|-------------|
| `State` | `AgentState` | Mutable runtime state — tools, messages, model, streaming status |
| `Status` | `AgentStatus` | Current execution phase: `Idle`, `Running`, or `Aborting` |

### Constructor

```csharp
public Agent(AgentOptions options)
```

The constructor snapshots configuration from `AgentOptions` and initializes internal queues and locks. Options are frozen for the lifetime of the agent — runtime changes go through `AgentState`.

### Internal concurrency primitives

| Field | Type | Purpose |
|-------|------|---------|
| `_runLock` | `SemaphoreSlim(1, 1)` | Enforces single-run concurrency — only one `PromptAsync`/`ContinueAsync` executes at a time |
| `_steeringQueue` | `PendingMessageQueue` | Queue for steering messages injected mid-run |
| `_followUpQueue` | `PendingMessageQueue` | Queue for follow-up messages injected after run settles |
| `_lifecycleLock` | `object` | Protects lifecycle state (`_status`, `_cts`, `_activeRun`) |
| `_stateLock` | `object` | Protects `_state` mutations |
| `_listenersLock` | `object` | Protects listener list mutations |

### Public methods

```csharp
// Subscribe to agent events. Returns IDisposable to unsubscribe.
IDisposable Subscribe(Func<AgentEvent, CancellationToken, Task> listener)

// Start a new run with a text prompt.
Task<IReadOnlyList<AgentMessage>> PromptAsync(
    string text,
    CancellationToken cancellationToken = default)

// Start a new run with a single message.
Task<IReadOnlyList<AgentMessage>> PromptAsync(
    AgentMessage message,
    CancellationToken cancellationToken = default)

// Start a new run with multiple messages.
Task<IReadOnlyList<AgentMessage>> PromptAsync(
    IReadOnlyList<AgentMessage> messages,
    CancellationToken cancellationToken = default)

// Continue the current conversation without adding a new user message.
Task<IReadOnlyList<AgentMessage>> ContinueAsync(
    CancellationToken cancellationToken = default)

// Inject a steering message into the current run (consumed at next turn boundary).
void Steer(AgentMessage message)

// Inject a follow-up message (consumed after the current run settles).
void FollowUp(AgentMessage message)

// Signal cancellation. The agent finishes its current streaming/tool call, then stops.
async Task AbortAsync()

// Block until the agent reaches Idle status.
async Task WaitForIdleAsync(CancellationToken cancellationToken = default)

// Clear state: remove all messages, reset error, return to initial state.
void Reset()

// Queue management.
void ClearSteeringQueue()
void ClearFollowUpQueue()
void ClearAllQueues()

// Check if steering or follow-up messages are queued (Phase 4)
bool HasQueuedMessages { get; }
```

All three `PromptAsync` overloads acquire `_runLock`, set status to `Running`, enter the loop, and release the lock on exit. `ContinueAsync` does the same but skips adding a new user message.

> **Key takeaway:** `Agent` is a thin stateful shell. It serializes access, manages queues, and delegates the actual LLM interaction to `AgentLoopRunner`.

## AgentState

`AgentState` is the mutable runtime state that changes between and during runs. It lives on the `Agent.State` property.

```csharp
public class AgentState
{
    public string? SystemPrompt { get; set; }
    public required LlmModel Model { get; set; }
    public ThinkingLevel? ThinkingLevel { get; set; }
    public IReadOnlyList<IAgentTool> Tools { get; set; }
    public IReadOnlyList<AgentMessage> Messages { get; set; }
    public bool IsStreaming { get; }
    public AssistantAgentMessage? StreamingMessage { get; }
    public IReadOnlySet<string> PendingToolCalls { get; }
    public string? ErrorMessage { get; }
}
```

| Property | Writable? | When it changes |
|----------|-----------|-----------------|
| `SystemPrompt` | Yes | Set before next `PromptAsync` |
| `Model` | Yes | Set before next `PromptAsync` — hot-swap models between runs |
| `ThinkingLevel` | Yes | Set before next `PromptAsync` — controls extended reasoning |
| `Tools` | Yes | Setter copies internally to prevent external mutation |
| `Messages` | Yes | Setter copies internally; grows during runs as turns complete |
| `IsStreaming` | No | `true` between `MessageStartEvent` and `MessageEndEvent` |
| `StreamingMessage` | No | The in-progress `AssistantAgentMessage` while streaming |
| `PendingToolCalls` | No | Set of tool call IDs currently executing |
| `ErrorMessage` | No | Latest runtime error message, if any |

Changes to writable properties take effect on the next `PromptAsync` call. The agent snapshots state into an `AgentContext` at the start of each run.

> **Key takeaway:** `AgentState` is the single source of truth for what the agent knows and can do at any point in time. Mutate it freely between runs; the loop snapshots it when entering.

## AgentOptions

`AgentOptions` is the immutable configuration record passed to the `Agent` constructor. It defines wiring — how messages convert, how keys resolve, which hooks fire.

```csharp
public record AgentOptions(
    AgentInitialState? InitialState,
    LlmModel Model,
    LlmClient LlmClient,
    ConvertToLlmDelegate? ConvertToLlm,
    TransformContextDelegate? TransformContext,
    GetApiKeyDelegate GetApiKey,
    GetMessagesDelegate? GetSteeringMessages,
    GetMessagesDelegate? GetFollowUpMessages,
    ToolExecutionMode ToolExecutionMode,
    BeforeToolCallDelegate? BeforeToolCall,
    AfterToolCallDelegate? AfterToolCall,
    SimpleStreamOptions GenerationSettings,
    QueueMode SteeringMode,
    QueueMode FollowUpMode,
    string? SessionId = null,
    Action<string>? OnDiagnostic = null,
    int? MaxRetryDelayMs = null
);
```

| Field | Purpose |
|-------|---------|
| `InitialState` | Optional initial state (system prompt, model override, tools, pre-seeded messages) |
| `Model` | Default LLM model for this agent |
| `LlmClient` | The client used to call providers (routes to correct `IApiProvider`) |
| `ConvertToLlm` | Converts `AgentMessage` list → provider-level `Message` list (auto-defaults to `DefaultMessageConverter` if not provided — Phase 4) |
| `TransformContext` | Optional context window compaction — trim, summarize, or rewrite messages before each LLM call (defaults to identity passthrough if not provided — Phase 4) |
| `GetApiKey` | Resolves API key by provider name |
| `GetSteeringMessages` | Optional delegate that produces steering messages at turn boundaries |
| `GetFollowUpMessages` | Optional delegate that produces follow-up messages after runs |
| `ToolExecutionMode` | `Sequential` or `Parallel` tool execution |
| `BeforeToolCall` | Hook that fires before each tool executes — can block execution |
| `AfterToolCall` | Hook that fires after each tool executes — can transform results |
| `GenerationSettings` | `SimpleStreamOptions` for controlling temperature, max tokens, etc. |
| `SteeringMode` | `QueueMode.All` (drain all at once) or `QueueMode.OneAtATime` (Phase 4: configurable via setter) |
| `FollowUpMode` | `QueueMode.All` or `QueueMode.OneAtATime` (Phase 4: configurable via setter) |
| `SessionId` | Optional session identifier for logging and persistence |
| `OnDiagnostic` | Optional callback for non-fatal runtime diagnostics (e.g., swallowed listener exceptions — Phase 4) |
| `MaxRetryDelayMs` | Optional max delay (ms) for transient retry backoff; null means uncapped |

> **Key takeaway:** `AgentOptions` is set-once wiring. To change behavior at runtime, modify `AgentState` properties instead.

## AgentLoopRunner — Step by step

`AgentLoopRunner` is a static class that contains the core turn loop. It has no state of its own — everything it needs is passed in as parameters.

### Signatures

```csharp
// Start a new run: emit AgentStartEvent, add prompt messages, enter turn loop.
static async Task<IReadOnlyList<AgentMessage>> RunAsync(
    IReadOnlyList<AgentMessage> prompts,
    AgentContext context,
    AgentLoopConfig config,
    Func<AgentEvent, Task> emit,
    CancellationToken cancellationToken)

// Continue from the current context without adding a new user message.
static async Task<IReadOnlyList<AgentMessage>> ContinueAsync(
    AgentContext context,
    AgentLoopConfig config,
    Func<AgentEvent, Task> emit,
    CancellationToken cancellationToken)
```

### The loop

```
TURN LOOP:
  ┌─────────────────────────────────────────────────────────────────┐
  │ 1. Drain steering messages → add to timeline                   │
  │ 2. Transform context (compaction via TransformContextDelegate)  │
  │ 3. Convert agent messages → provider messages (ConvertToLlm)   │
  │ 4. Call LLM via StreamSimple()                                 │
  │ 5. Accumulate stream → AssistantAgentMessage (StreamAccumulator)│
  │ 6. Add assistant message to timeline                           │
  │ 7. If Error/Aborted/Refusal/Sensitive                          │
  │    → emit TurnEnd, AgentEnd, STOP                              │
  │ 8. If ToolCalls                                                │
  │    → execute tools, add results, emit TurnEnd,                 │
  │      drain steering, GOTO step 1                               │
  │ 9. If no ToolCalls                                             │
  │    → emit TurnEnd, check follow-ups                            │
  └─────────────────────────────────────────────────────────────────┘
                            │
                            ▼
FOLLOW-UP LOOP:
  ┌─────────────────────────────────────────────────────────────────┐
  │ If follow-ups exist → drain, GOTO TURN LOOP                   │
  │ Otherwise → emit AgentEnd, STOP                                │
  └─────────────────────────────────────────────────────────────────┘
```

**Step 1 — Drain steering:** Any messages queued via `Agent.Steer()` or `GetSteeringMessages` are pulled from the queue and appended to the timeline. This happens at the top of every turn, so steering messages are visible to the LLM on the next call.

**Step 2 — Transform context:** The `TransformContextDelegate` runs over the full message list. This is where context window compaction happens — old messages can be summarized, trimmed, or removed entirely.

**Step 3 — Convert to LLM format:** `ConvertToLlmDelegate` maps `AgentMessage` instances to provider-level `Message` objects. Each provider has its own message format, and this delegate handles the translation.

**Step 4 — Call LLM:** `StreamSimple()` on the `LlmClient` sends the converted context to the provider. This returns an `LlmStream` — an async enumerable of provider-level streaming events.

**Step 5 — Accumulate stream:** `StreamAccumulator` consumes the `LlmStream` and builds an `AssistantAgentMessage`. Along the way, it emits `MessageStartEvent`, `MessageUpdateEvent`, and `MessageEndEvent` to subscribers. See the [StreamAccumulator](#streamaccumulator) section for the full event mapping.

**Step 6 — Add to timeline:** The completed assistant message is appended to `AgentState.Messages`.

**Step 7 — Terminal conditions:** If the assistant's `FinishReason` is `Error`, `Aborted`, `Refusal`, or `Sensitive`, the loop emits `TurnEndEvent` and `AgentEndEvent`, then stops. No further turns execute.

**Step 8 — Tool calls:** If the assistant message contains `ToolCalls`, the `ToolExecutor` runs them (see [Tool execution pipeline](#tool-execution-pipeline)). Results are appended to the timeline. Steering messages are drained again, and the loop repeats from step 1.

**Step 9 — Natural completion:** If no tool calls and no terminal condition, the turn ends naturally. The runner checks the follow-up queue. If follow-ups exist, they're drained and a new turn loop begins. Otherwise, `AgentEndEvent` fires and the run completes.

> **Key takeaway:** The loop is a simple state machine — call LLM, check result, execute tools if needed, repeat. Steering messages inject context between turns; follow-ups restart the loop after it settles.

## StreamAccumulator

`StreamAccumulator` is a static class that consumes provider-level streaming events and converts them into agent-level events, while building the final `AssistantAgentMessage`.

```csharp
public static async Task<AssistantAgentMessage> AccumulateAsync(
    LlmStream stream,
    Func<AgentEvent, Task> emit,
    CancellationToken cancellationToken)
```

### Event mapping

| Provider event | Agent event emitted | Key data |
|----------------|---------------------|----------|
| `StartEvent` | `MessageStartEvent` | Initializes the message snapshot (NOT added to state.Messages — Phase 4 change) |
| `TextDeltaEvent` | `MessageUpdateEvent` | `ContentDelta` = text chunk, `IsThinking` = false |
| `ThinkingDeltaEvent` | `MessageUpdateEvent` | `ContentDelta` = thinking chunk, `IsThinking` = true |
| `ToolCallStartEvent` | `MessageUpdateEvent` | `ToolCallId`, `ToolName` from provider state |
| `ToolCallDeltaEvent` | `MessageUpdateEvent` | `ArgumentsDelta` = partial JSON arguments |
| `ToolCallEndEvent` | `MessageUpdateEvent` | Final tool call info |
| `DoneEvent` | `MessageEndEvent` | Streaming complete, `FinishReason` set, message added to state.Messages |
| `ErrorEvent` | `MessageEndEvent` | `FinishReason` = `Error`, error details captured |

Additional provider events (`TextStartEvent`, `TextEndEvent`, `ThinkingStartEvent`, `ThinkingEndEvent`) also emit `MessageUpdateEvent` with `ContentDelta` = null, marking structural boundaries in the stream.

The accumulator maintains a running snapshot of the `AssistantAgentMessage` as deltas arrive. Each `MessageUpdateEvent` carries the current snapshot plus the incremental delta, so subscribers can render either progressively or from the snapshot.

> **Phase 4 change:** `MessageStartEvent` no longer adds the assistant message to `state.Messages` during streaming. The message is only added when `MessageEndEvent` fires. This prevents duplicate messages when streaming is interrupted or replayed, and ensures event ordering consistency.

> **Key takeaway:** `StreamAccumulator` is a pure transform — provider stream in, agent events out. It produces exactly one `MessageStartEvent`, zero or more `MessageUpdateEvent`s, and exactly one `MessageEndEvent` per LLM call.

## Tool execution pipeline

`ToolExecutor` is a static class that runs tool calls from an assistant message. The pipeline has five stages regardless of execution mode.

### Pipeline stages

```
Tool Lookup (case-sensitive by Name)
    → PrepareArgumentsAsync (validate/transform arguments)
        → BeforeToolCall hook (can block execution)
            → ExecuteAsync (run the tool)
                → AfterToolCall hook (can transform result)
                    → ToolResultAgentMessage
```

### ToolExecutor signature

```csharp
public static async Task<IReadOnlyList<ToolResultAgentMessage>> ExecuteAsync(
    AgentContext context,
    AssistantAgentMessage assistantMessage,
    AgentLoopConfig config,
    Func<AgentEvent, Task> emit,
    CancellationToken cancellationToken)
```

### Sequential execution

Tools execute one at a time in the order they appear in the assistant message. For each tool:

1. Emit `ToolExecutionStartEvent` (with raw arguments)
2. Look up tool by name (case-sensitive)
3. Call `PrepareArgumentsAsync` — validate and transform raw arguments
4. Call `BeforeToolCall` hook — if it returns `Block = true`, skip execution and produce an error result
5. Call `ExecuteAsync` — run the tool
6. Call `AfterToolCall` hook — optionally transform the result
7. Emit `ToolExecutionEndEvent`
8. Build `ToolResultAgentMessage`

Events are emitted in deterministic order matching the assistant message.

### Parallel execution

Parallel mode executes tools concurrently, but with an important constraint: **preparation is always sequential**.

1. **Preparation phase (sequential):** For each tool in order, look up the tool, call `PrepareArgumentsAsync`, and call the `BeforeToolCall` hook. Emit `ToolExecutionStartEvent` for all tools.
2. **Execution phase (concurrent):** All prepared tools execute simultaneously via `Task.WhenAll`.
3. **Finalization phase (ordered):** `ToolExecutionEndEvent`s are emitted in the original assistant message order, regardless of which tool finished first.

Sequential preparation prevents race conditions in argument validation. Ordered finalization ensures deterministic event streams.

> **Key takeaway:** The tool pipeline is always: lookup → prepare → before-hook → execute → after-hook → result. Parallel mode only parallelizes the `ExecuteAsync` step.

## IAgentTool interface

Every tool the agent can invoke implements `IAgentTool`:

```csharp
public interface IAgentTool
{
    // Unique tool name. Used for case-insensitive lookup when the LLM calls a tool.
    string Name { get; }

    // Human-readable label for display in UIs and logs.
    string Label { get; }

    // JSON Schema definition sent to the LLM so it knows the tool's parameters.
    Tool Definition { get; }

    // Validate and transform raw LLM arguments before execution.
    // Called before BeforeToolCall hook. Use this to normalize paths,
    // resolve defaults, or reject invalid input.
    Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);

    // Execute the tool. Returns content (text/images) and optional metadata.
    // onUpdate callback enables progress reporting for long-running tools.
    Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null);

    // Optional: return a text snippet injected into the system prompt.
    // Use for dynamic context (e.g., current working directory, environment info).
    string? GetPromptSnippet() => null;

    // Optional: return guideline strings appended to the system prompt.
    // Use for tool-specific instructions the LLM should follow.
    IReadOnlyList<string> GetPromptGuidelines() => [];
}
```

### Method details

| Method | Required? | Purpose |
|--------|-----------|---------|
| `Name` | Yes | Identity — the LLM uses this string to call the tool |
| `Label` | Yes | Display name for events and logging |
| `Definition` | Yes | JSON Schema that tells the LLM what parameters the tool accepts |
| `PrepareArgumentsAsync` | Yes | Pre-execution validation and argument normalization |
| `ExecuteAsync` | Yes | The actual tool implementation |
| `GetPromptSnippet` | No (default: null) | Dynamic system prompt injection |
| `GetPromptGuidelines` | No (default: []) | Static guidelines appended to the system prompt |

> **Key takeaway:** Implement `IAgentTool` to add any capability to the agent. The LLM sees `Definition` to know what's available, calls tools by `Name`, and the pipeline handles the rest.

## Before/After hooks

Hooks let you intercept tool execution without modifying tool implementations. They're set on `AgentOptions` and apply to all tools.

### BeforeToolCall

```csharp
public delegate Task<BeforeToolCallResult?> BeforeToolCallDelegate(
    BeforeToolCallContext context,
    CancellationToken cancellationToken);

public record BeforeToolCallContext(
    AssistantAgentMessage AssistantMessage,
    ToolCallContent ToolCallRequest,
    IReadOnlyDictionary<string, object?> ValidatedArgs,
    AgentContext AgentContext);

public record BeforeToolCallResult(bool Block, string? Reason = null);
```

Return `null` to allow execution. Return `new BeforeToolCallResult(Block: true, Reason: "...")` to block the tool — the agent receives an error result with your reason.

**Example — Block dangerous commands:**

```csharp
var options = new AgentOptions(
    // ... other fields ...
    BeforeToolCall: async (context, ct) =>
    {
        if (context.ToolCallRequest.Name == "bash" &&
            context.ValidatedArgs.TryGetValue("command", out var cmd) &&
            cmd?.ToString()?.Contains("rm -rf") == true)
        {
            return new BeforeToolCallResult(Block: true, Reason: "Destructive command blocked.");
        }
        return null; // allow
    },
    // ...
);
```

### AfterToolCall

```csharp
public delegate Task<AfterToolCallResult?> AfterToolCallDelegate(
    AfterToolCallContext context,
    CancellationToken cancellationToken);

public record AfterToolCallContext(
    AssistantAgentMessage AssistantMessage,
    ToolCallContent ToolCallRequest,
    IReadOnlyDictionary<string, object?> ValidatedArgs,
    AgentToolResult Result,
    bool IsError,
    AgentContext AgentContext);

public record AfterToolCallResult(
    IReadOnlyList<AgentToolContent>? Content = null,
    object? Details = null,
    bool? IsError = null);
```

Return `null` to pass the result through unchanged. Return an `AfterToolCallResult` to replace the content, details, or error status.

**Example — Truncate large results:**

```csharp
AfterToolCall: async (context, ct) =>
{
    var text = context.Result.Content.FirstOrDefault()?.Value ?? "";
    if (text.Length > 10_000)
    {
        return new AfterToolCallResult(
            Content: [new AgentToolContent(AgentToolContentType.Text, text[..10_000] + "\n[truncated]")]);
    }
    return null; // pass through
}
```

> **Key takeaway:** `BeforeToolCall` gates execution (allow/block); `AfterToolCall` transforms results. Both are optional and apply globally to all tools.

## Event system

The agent emits a structured stream of events during every run. Subscribe via `Agent.Subscribe()` to observe progress, render UI, log activity, or drive external integrations.

> **Phase 4 note:** Listener exceptions on failure/abort paths are now logged via `OnDiagnostic` instead of being swallowed. This improves observability when listening to agent events.

### Event lifecycle

```
AgentStartEvent
│
├── TurnStartEvent
│   ├── MessageStartEvent (user prompt)
│   └── MessageEndEvent
│   ├── MessageStartEvent (assistant response)
│   │   ├── MessageUpdateEvent (content delta)
│   │   ├── MessageUpdateEvent (content delta)
│   │   ├── MessageUpdateEvent (tool call start)
│   │   └── MessageUpdateEvent (tool call arguments)
│   └── MessageEndEvent
│   ├── ToolExecutionStartEvent
│   │   ├── ToolExecutionUpdateEvent (progress, if any)
│   │   └── ToolExecutionEndEvent
│   ├── ToolExecutionStartEvent
│   │   └── ToolExecutionEndEvent
│   └── TurnEndEvent
│
├── TurnStartEvent (next turn with tool results)
│   └── ... (repeat)
│
└── AgentEndEvent
```

### Event reference

| Event | When emitted | Key properties |
|-------|-------------|----------------|
| `AgentStartEvent` | First event of every run | `Timestamp` |
| `AgentEndEvent` | Final event — agent becomes idle after listeners settle | `Messages`, `Timestamp` |
| `TurnStartEvent` | Before each LLM call | `Timestamp` |
| `TurnEndEvent` | After assistant message and tool results are finalized | `Message` (AssistantAgentMessage), `ToolResults`, `Timestamp` |
| `MessageStartEvent` | When a message begins processing | `Message`, `Timestamp` |
| `MessageUpdateEvent` | Streaming incremental update | `Message` (snapshot), `ContentDelta`, `IsThinking`, `ToolCallId`, `ToolName`, `ArgumentsDelta`, `FinishReason`, `InputTokens`, `OutputTokens`, `Timestamp` |
| `MessageEndEvent` | When a message finishes processing | `Message`, `Timestamp` |
| `ToolExecutionStartEvent` | Before argument validation, before `ExecuteAsync` | `ToolCallId`, `ToolName`, `Args` (raw, unvalidated), `Timestamp` |
| `ToolExecutionUpdateEvent` | Reserved for progress updates during execution | `ToolCallId`, `ToolName`, `Args`, `PartialResult`, `Timestamp` |
| `ToolExecutionEndEvent` | After execution and after-hooks complete | `ToolCallId`, `ToolName`, `Result`, `IsError`, `Timestamp` |

All events inherit from:

```csharp
public abstract record AgentEvent(AgentEventType Type, DateTimeOffset Timestamp);
```

> **Key takeaway:** Events form a strict hierarchy: Agent → Turn → Message/ToolExecution. Subscribe once and pattern-match on event type to handle everything.

## Steering and follow-up messages

Steering and follow-up messages let external code inject context into the agent's conversation without starting a new run.

### Steering messages

Steering messages are consumed **during** a run, at turn boundaries. They're added to the timeline before the next LLM call.

```csharp
// Inject from outside the run:
agent.Steer(new UserMessage("Focus on error handling, ignore styling."));
```

Or supply a delegate in `AgentOptions.GetSteeringMessages` that's called automatically at each turn boundary.

**When they fire:** At step 1 of the turn loop — before context transformation and LLM call.

### Follow-up messages

Follow-up messages are consumed **after** a run settles (no more tool calls, no terminal condition). They restart the turn loop with new context.

```csharp
// Inject from outside the run:
agent.FollowUp(new UserMessage("Now write tests for the code you just produced."));
```

Or supply `AgentOptions.GetFollowUpMessages` for automatic follow-ups.

**When they fire:** At step 9 of the turn loop — after the model finishes naturally with no tool calls.

### QueueMode

Both queues support two drain modes:

```csharp
public enum QueueMode
{
    OneAtATime,    // Drain one message per loop iteration (default: enum value 0)
    All,           // Drain all queued messages at once
}
```

`QueueMode.All` is commonly used for steering — all pending messages are added to the timeline in a single drain. `QueueMode.OneAtATime` drains one message per turn, which gives the LLM a chance to respond to each message individually. Both must be specified explicitly in `AgentOptions`.

**Example — Guided multi-step workflow:**

```csharp
var agent = new Agent(new AgentOptions(
    // ...
    SteeringMode: QueueMode.OneAtATime,
    FollowUpMode: QueueMode.All,
    // ...
));

// Queue a series of follow-up instructions before starting:
agent.FollowUp(new UserMessage("Step 1: Read the existing code."));
agent.FollowUp(new UserMessage("Step 2: Refactor for testability."));
agent.FollowUp(new UserMessage("Step 3: Add unit tests."));

await agent.PromptAsync("I need to improve the auth module.");
```

> **Key takeaway:** Steering injects context mid-run (between turns). Follow-ups restart the loop after completion. Use `QueueMode` to control whether messages drain all at once or one per turn.

## Abort and error handling

### AbortAsync

`AbortAsync()` signals cancellation to the running loop:

```csharp
await agent.AbortAsync();
```

The cancellation token propagates to the LLM stream and tool execution. The loop exits with `StopReason.Aborted`, emitting `TurnEndEvent` and `AgentEndEvent`. The agent status transitions: `Running` → `Aborting` → `Idle`.

### LLM errors

If the provider stream emits an `ErrorEvent`, the `StreamAccumulator` converts it to a `MessageEndEvent` with `FinishReason = Error`. The loop checks this at step 7 and exits — no tool execution, no follow-ups.

### Tool errors

Exceptions thrown during `ExecuteAsync` are caught and converted to an error `AgentToolResult` with `IsError = true`. The error message is included in the `ToolResultAgentMessage` sent back to the LLM, which can then decide how to proceed (retry, apologize, or try a different approach).

### Hook errors

Exceptions in `BeforeToolCall` or `AfterToolCall` hooks are logged and ignored. The tool execution continues as if the hook returned `null`. This prevents buggy hooks from crashing the agent.

### Concurrency

Single-run concurrency is enforced by `_runLock` (`SemaphoreSlim(1, 1)`). If you call `PromptAsync` while a run is active, the second call throws `InvalidOperationException`. `AbortAsync` + `WaitForIdleAsync` is the pattern for preempting a running agent:

```csharp
await agent.AbortAsync();
await agent.WaitForIdleAsync();
await agent.PromptAsync("Start fresh with a new approach.");
```

> **Key takeaway:** The agent is resilient — LLM errors stop the loop cleanly, tool errors are reported to the LLM, hook errors are swallowed, and concurrent access is serialized.

## Code example: creating a simple agent with custom tools

```csharp
using System.Text.Json;
using BotNexus.AgentCore;
using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;

// 1. Define a custom tool
public class CalculatorTool : IAgentTool
{
    public string Name => "calculator";
    public string Label => "Calculator";

    public Tool Definition => new Tool(
        Name: "calculator",
        Description: "Evaluate a mathematical expression.",
        Parameters: JsonSerializer.Deserialize<JsonElement>("""
        {
            "type": "object",
            "properties": {
                "expression": { "type": "string", "description": "Math expression to evaluate" }
            },
            "required": ["expression"]
        }
        """));

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        if (!arguments.ContainsKey("expression"))
            throw new ArgumentException("Missing 'expression' argument.");
        return Task.FromResult(arguments);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var expr = arguments["expression"]?.ToString() ?? "";
        // Simple evaluation (in production, use a proper math parser)
        var result = $"Result of '{expr}' = 42"; // placeholder

        return new AgentToolResult(
            Content: [new AgentToolContent(AgentToolContentType.Text, result)]);
    }

    public string? GetPromptSnippet() => null;
    public IReadOnlyList<string> GetPromptGuidelines() =>
        ["Use the calculator tool for any math operations. Always show your work."];
}

// 2. Configure and create the agent
var model = new LlmModel(
    Id: "gpt-4.1",
    Name: "GPT-4.1",
    Api: "openai-completions",
    Provider: "github-copilot",
    BaseUrl: "https://api.githubcopilot.com",
    Reasoning: false,
    Input: ["text"]);

var options = new AgentOptions(
    InitialState: new AgentInitialState(
        SystemPrompt: "You are a helpful math assistant. Use the calculator tool for computations.",
        Tools: [new CalculatorTool()]),
    Model: model,
    LlmClient: llmClient,
    ConvertToLlm: contextConverter.ConvertAsync,
    TransformContext: (messages, ct) => Task.FromResult(messages),
    GetApiKey: (provider, ct) => Task.FromResult<string?>(apiKey),
    GetSteeringMessages: null,
    GetFollowUpMessages: null,
    ToolExecutionMode: ToolExecutionMode.Sequential,
    BeforeToolCall: null,
    AfterToolCall: null,
    GenerationSettings: new SimpleStreamOptions(),
    SteeringMode: QueueMode.All,
    FollowUpMode: QueueMode.All);

var agent = new Agent(options);

// 3. Subscribe to events
agent.Subscribe(async (evt, ct) =>
{
    switch (evt)
    {
        case MessageUpdateEvent update when !update.IsThinking && update.ContentDelta != null:
            Console.Write(update.ContentDelta);
            break;
        case ToolExecutionStartEvent toolStart:
            Console.WriteLine($"\n[Calling tool: {toolStart.ToolName}]");
            break;
        case ToolExecutionEndEvent toolEnd:
            Console.WriteLine($"[Tool result: {toolEnd.Result.Content.FirstOrDefault()?.Value}]");
            break;
        case AgentEndEvent:
            Console.WriteLine("\n[Done]");
            break;
    }
});

// 4. Run the agent
var messages = await agent.PromptAsync("What is 15 * 28 + 73?");
```

> **Key takeaway:** Building an agent is: define tools → create `AgentOptions` → instantiate `Agent` → subscribe to events → call `PromptAsync`. The loop handles the rest.

## Delegate signatures reference

All delegate types are defined in `BotNexus.AgentCore.Configuration.Delegates`:

```csharp
// Convert agent messages to provider-level messages.
public delegate Task<IReadOnlyList<Message>> ConvertToLlmDelegate(
    IReadOnlyList<AgentMessage> messages,
    CancellationToken cancellationToken);

// Transform context before LLM call (compaction, summarization, filtering).
public delegate Task<IReadOnlyList<AgentMessage>> TransformContextDelegate(
    IReadOnlyList<AgentMessage> messages,
    CancellationToken cancellationToken);

// Resolve API key for a provider.
public delegate Task<string?> GetApiKeyDelegate(
    string provider,
    CancellationToken cancellationToken);

// Produce contextual messages (used for steering and follow-up).
public delegate Task<IReadOnlyList<AgentMessage>> GetMessagesDelegate(
    CancellationToken cancellationToken);

// Intercept before tool execution (can block).
public delegate Task<BeforeToolCallResult?> BeforeToolCallDelegate(
    BeforeToolCallContext context,
    CancellationToken cancellationToken);

// Intercept after tool execution (can transform result).
public delegate Task<AfterToolCallResult?> AfterToolCallDelegate(
    AfterToolCallContext context,
    CancellationToken cancellationToken);
```

| Delegate | Input | Output | Nullable return? |
|----------|-------|--------|------------------|
| `ConvertToLlmDelegate` | Agent messages | Provider messages | No |
| `TransformContextDelegate` | Agent messages | Transformed agent messages | No |
| `GetApiKeyDelegate` | Provider name | API key string | Yes (null = no key) |
| `GetMessagesDelegate` | — | Message list | No |
| `BeforeToolCallDelegate` | `BeforeToolCallContext` | `BeforeToolCallResult` | Yes (null = allow) |
| `AfterToolCallDelegate` | `AfterToolCallContext` | `AfterToolCallResult` | Yes (null = pass through) |

> **Key takeaway:** Delegates are the extension points of `AgentOptions`. They let you customize message conversion, context management, key resolution, and tool interception without subclassing.

---

## See also

- **[Architecture overview](00-overview.md)** — High-level system architecture and the three-layer model
- **[Provider system](01-providers.md)** — LLM communication, streaming protocol, and `LlmStream` details
- **[Coding agent](03-coding-agent.md)** — How `CodingAgent` builds on top of the agent core with built-in tools and session management
- **[Building your own](04-building-your-own.md)** — Step-by-step tutorials for creating custom agents and tools
