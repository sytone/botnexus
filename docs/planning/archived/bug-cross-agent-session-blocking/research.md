# Bug: Cross-Agent Session Blocking

## Summary

When one agent (Aurum) is actively running, another agent (Nova) cannot receive messages or deliver responses until the first agent finishes. This is **not** caused by a shared semaphore or global lock. The root cause is in `GatewayHub.SendMessage` which **awaits** `DispatchAsync`, and `DispatchAsync` blocks until the agent run completes. Combined with SignalR's per-connection sequential message processing, this serializes all hub calls from a single client connection.

## Root Cause Analysis

### The Blocking Chain

#### 1. `GatewayHub.SendMessage` awaits `DispatchAsync` (PRIMARY BOTTLENECK)

**File:** `src/gateway/BotNexus.Gateway.Api/Hubs/GatewayHub.cs`

```csharp
public async Task<object> SendMessage(AgentId agentId, ChannelKey channelType, string content)
{
    // ...
    await DispatchMessageAsync(typedAgentId, session.SessionId, content, "message");  // ← BLOCKS
    return new { ... };
}
```

`DispatchMessageAsync` calls `_dispatcher.DispatchAsync(...)` which is `GatewayHost.DispatchAsync`.

#### 2. `GatewayHost.DispatchAsync` awaits the full agent run

**File:** `src/gateway/BotNexus.Gateway/GatewayHost.cs`, lines ~95-115

```csharp
public async Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
{
    var queueKey = GetQueueKey(message);
    var queueState = _sessionQueues.GetOrAdd(queueKey, CreateSessionQueueState);
    var queueItem = new QueuedInboundMessage(message, cancellationToken);

    if (!queueState.Queue.Writer.TryWrite(queueItem))
    {
        await SendBusyAsync(message, cancellationToken);
        return;
    }

    // ← THIS AWAITS UNTIL THE AGENT RUN COMPLETES
    await queueItem.Completion.Task.WaitAsync(cancellationToken);
}
```

Each `QueuedInboundMessage` has a `TaskCompletionSource` that only completes after `ProcessInboundMessageAsync` finishes — which includes the full LLM agent run (potentially minutes for tool-heavy runs).

#### 3. SignalR Hub Sequential Dispatch (THE MULTIPLIER)

**This is the critical architectural interaction.** SignalR hubs process invocations from a single connection **sequentially**. A second `SendMessage` call from the same client connection will not begin executing until the first one returns.

So when a user sends a message to Aurum via the web UI:
1. `SendMessage("aurum", ...)` starts and **blocks** the SignalR connection's processing pipeline
2. User sends a message to Nova via the same web UI connection
3. `SendMessage("nova", ...)` is **queued by SignalR** — it cannot start until call #1 returns
4. Aurum finishes → call #1 returns → call #2 begins → Nova finally receives the message

### Why Response Delivery Is Also Blocked

Response delivery via `SignalRChannelAdapter` uses `IHubContext<GatewayHub>` which broadcasts to SignalR groups. This is **not** blocked — it uses the hub context, not the connection pipeline. So Nova's responses *would* be delivered if Nova's run could start. The issue is that Nova's run never starts because the `SendMessage` invocation is queued behind Aurum's.

### Session Queue Design (NOT the cross-agent bottleneck)

**File:** `src/gateway/BotNexus.Gateway/GatewayHost.cs`

The `_sessionQueues` dictionary keys queues by session ID (`GetQueueKey`), not globally. Each session gets its own `Channel<QueuedInboundMessage>` with a `SingleReader` worker. This correctly isolates sessions — Aurum's session queue does NOT block Nova's session queue at the `GatewayHost` level.

```csharp
private static string GetQueueKey(InboundMessage message)
    => !string.IsNullOrWhiteSpace(message.SessionId)
        ? message.SessionId
        : $"{message.ChannelType}:{message.ConversationId}";
```

The per-session serialization is intentional and correct (prevents concurrent runs on the same agent session). The bug is one layer up — in the hub call awaiting the full dispatch.

### Agent-Level Locking (NOT the cross-agent bottleneck)

**File:** `src/agent/BotNexus.Agent.Core/Agent.cs`

The `Agent` class has a per-instance `_runLock = new SemaphoreSlim(1, 1)` that prevents concurrent runs on the same agent instance. This is per-agent-per-session and does not cause cross-agent blocking.

## Affected Components

| Component | File | Role in Bug |
|-----------|------|------------|
| `GatewayHub.SendMessage` | `src/gateway/BotNexus.Gateway.Api/Hubs/GatewayHub.cs` | Awaits full dispatch — blocks SignalR connection pipeline |
| `GatewayHost.DispatchAsync` | `src/gateway/BotNexus.Gateway/GatewayHost.cs` | Awaits `Completion.Task` until agent run finishes |
| SignalR Hub infrastructure | ASP.NET Core SignalR | Serializes hub method calls per connection |
| `Agent._runLock` | `src/agent/BotNexus.Agent.Core/Agent.cs` | Per-instance only — NOT the cross-agent issue |

## Proposed Fix

### Option A: Fire-and-forget dispatch in hub (Recommended)

`GatewayHub.SendMessage` should **not** await `DispatchAsync`. Instead, fire-and-forget the dispatch and return immediately with the session info. The response will arrive asynchronously via SignalR streaming events.

```csharp
public async Task<object> SendMessage(AgentId agentId, ChannelKey channelType, string content)
{
    var session = await ResolveOrCreateSessionAsync(typedAgentId, typedChannelType);
    await SubscribeInternalAsync(session.SessionId);

    // Fire-and-forget: don't block the hub connection pipeline
    _ = DispatchMessageAsync(typedAgentId, session.SessionId, content, "message");

    return new { sessionId = session.SessionId.Value, ... };
}
```

**Pros:** Simple, unblocks all cross-agent scenarios immediately.  
**Cons:** Client loses the ability to know when dispatch fails (must rely on error stream events). Exceptions need to be caught and forwarded via SignalR `Error` event.

### Option B: Separate DispatchAsync into enqueue + completion

Split `DispatchAsync` into `EnqueueAsync` (returns immediately after writing to queue) and let the response flow entirely through streaming events. This is cleaner but requires more refactoring.

### Option C: Hub method returns after enqueue, polls for completion

`DispatchAsync` could return a correlation ID after successfully writing to the session queue, without awaiting completion. Less invasive than Option B.

## Impact Assessment

- **Severity:** High — any multi-agent workflow is fundamentally broken when agents share a SignalR connection (which is always the case for web UI users)
- **Scope:** All SignalR clients. Telegram/other channel adapters that don't await dispatch may not be affected.
- **Risk of fix:** Low for Option A — the streaming path already delivers all content via events, so the await on dispatch is redundant for streaming channels.
