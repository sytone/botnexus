# Internal Triggers and Agent-to-Agent Communication

This document describes BotNexus's internal trigger system (Cron, Soul) and peer agent conversation mechanisms.

## Internal Triggers

Internal triggers initiate agent sessions without external user input. They enable scheduled execution, daily soul sessions, and autonomous agent behavior.

### IInternalTrigger Interface

```csharp
public interface IInternalTrigger
{
    TriggerType Type { get; }
    string DisplayName { get; }
    
    Task<SessionId> CreateSessionAsync(
        AgentId agentId,
        string prompt,
        CancellationToken ct = default,
        InternalTriggerRequest? request = null);
}
```

The optional `InternalTriggerRequest` carries scheduler-supplied options in (`CronJobId`, `ModelOverride`,
`ConversationId`, `JobName`, `CreatedBy`) and is written back out by the trigger after the turn completes
(`ResolvedConversationId`, `ToolInvocationCount`, `TurnCount`, `PromptTokens`, `CompletionTokens`,
`DeliveryError`). A `null` write-back means "not measured", never zero - the cron scheduler relies on that
distinction when it applies the execution-class zero-tool rule.

**Trigger Types:**

| Type | Implementation | Display name |
|------|----------------|--------------|
| `Cron` | `CronTrigger` | Cron Scheduler |
| `Soul` | `SoulTrigger` | Soul Session |
| `Heartbeat` | `HeartbeatTrigger` | Heartbeat |

### Cron Trigger

**Purpose:**
- Execute agents on a schedule (hourly, daily, weekly, etc.)
- Useful for periodic tasks (backups, reports, monitoring)
- No user interaction required

**Implementation:**

`CreateSessionAsync` resolves the run's conversation, adds the prompt as a user message, executes the agent, records the response, and saves the session.

Conversation ownership is inverted: `CronJob.ConversationId` is the canonical link from a job to its
long-lived conversation. When the scheduler passes an `InternalTriggerRequest.ConversationId` (a pinned
job) the trigger reuses that conversation verbatim; otherwise it creates a fresh one titled after the job.
When two parallel runs create different conversations, the scheduler's compare-and-set
(`ICronStore.TrySetConversationIdAsync`) picks one winner and the loser archives its conversation and
rebinds its session.

See [CronTrigger.cs](../../src/gateway/BotNexus.Gateway.Api/Triggers/CronTrigger.cs) for the full implementation.

**Execution Flow:**

```text
CronScheduler → CronTrigger → CreateSession → Agent Execution → Session Saved
```

**Session Characteristics:**
- SessionType: `Cron`
- ChannelType: `cron`
- No streaming (batch execution)
- Results logged to session history
- Auto-archives after completion

**Example Use Cases:**

1. **Daily Report Generation:**
   ```json
   {
     "schedule": "0 9 * * *",
     "agentId": "report-generator",
     "prompt": "Generate daily performance report for yesterday."
   }
   ```

2. **Hourly Health Check:**
   ```json
   {
     "schedule": "0 * * * *",
     "agentId": "monitoring",
     "prompt": "Check system health and alert if issues found."
   }
   ```

3. **Weekly Cleanup:**
   ```json
   {
     "schedule": "0 2 * * 0",
     "agentId": "maintenance",
     "prompt": "Archive old sessions and clean up workspace."
   }
   ```

### Soul Trigger

**Purpose:**
- Daily soul session heartbeat for persistent agent memory
- Agents maintain continuity across days via soul sessions
- Reflection and planning at day boundaries
- Inspired by long-term agent memory research

**Key Concepts:**

- **Soul Session**: Daily persistent session tied to a specific date
- **Soul Date**: Logical day boundary (respects agent's timezone)
- **Reflection on Seal**: End-of-day reflection before archiving
- **Session Continuity**: Next day's soul session has access to previous reflections

**Implementation:**

`SoulTrigger` implements `IInternalTrigger` for daily soul sessions. Key behaviors:

- **Resolves soul date** respecting agent timezone and day boundary via `ResolveCalendarSettings`
- **Session ID format:** `{agentId}::soul::{yyyy-MM-dd}`, produced by `SessionId.ForSoul`. This shape is
  part of the persisted wire format and has format-pinning tests. Note that soul-ness is **not** inferred
  from the id - the canonical signal is `Session.Metadata["soulDate"]` (directive G-4).
- **Seals older soul sessions** before creating today's session
- **Optional reflection prompt** before sealing, configurable via `SoulAgentConfig.ReflectionOnSeal`
- **Timezone resolution** accepts an IANA id first, falls back to a Windows id via
  `TimeZoneInfo.TryConvertIanaIdToWindowsId`, and degrades to UTC when neither resolves

See [SoulTrigger.cs](../../src/gateway/BotNexus.Gateway.Api/Triggers/SoulTrigger.cs) for the full implementation.

**Soul Date Resolution:**

```csharp
DateOnly ResolveSoulDate(DateTimeOffset utcNow, TimeZoneInfo timeZone, TimeSpan dayBoundary)
{
    // Convert UTC to agent's local time
    var localNow = TimeZoneInfo.ConvertTime(utcNow, timeZone);
    
    // Adjust for day boundary (e.g., 4am = new day starts at 4am)
    var adjustedLocal = localNow.TimeOfDay < dayBoundary
        ? localNow.AddDays(-1)
        : localNow;
    
    return DateOnly.FromDateTime(adjustedLocal.DateTime);
}
```

**SoulAgentConfig:**

```csharp
public sealed class SoulAgentConfig
{
    public bool Enabled { get; set; }                       // Soul journalling on/off for this agent
    public string Timezone { get; set; } = "UTC";           // IANA timezone (e.g. "America/Los_Angeles")
    public string DayBoundary { get; set; } = "00:00";      // Local HH:mm at which the journal day rolls over
    public bool ReflectionOnSeal { get; set; }              // Write a reflection when a day is sealed
    public string? ReflectionPrompt { get; set; }           // Custom prompt; built-in prompt when unset
}
```

**Example Configuration:**

```json
{
  "id": "personal-assistant",
  "displayName": "Personal Assistant",
  "model": "anthropic:claude-sonnet-4",
  "soul": {
    "enabled": true,
    "timezone": "America/Los_Angeles",
    "dayBoundary": "04:00",
    "reflectionOnSeal": true,
    "reflectionPrompt": "Reflect on today's conversations. What did you learn? What should you remember for tomorrow?"
  }
}
```

**Soul Session Lifecycle:**

```text
Day 1 (2024-01-15):
  - Session: personal-assistant::soul::2024-01-15
  - Status: Active
  - Multiple heartbeat prompts throughout the day
  - Accumulates conversation history

Day 2 (2024-01-16):
  - Previous session (2024-01-15) gets reflection prompt
  - Previous session sealed (Status: Sealed)
  - New session: personal-assistant::soul::2024-01-16
  - Status: Active
  - Has access to sealed sessions for context
```

**Session Access Patterns:**

```csharp
// Agent can access sealed soul sessions
var previousSoulSessions = await _sessions.ListAsync(agentId, ct)
    .Where(s => s.SessionType == SessionType.Soul &&
               s.Status == SessionStatus.Sealed)
    .OrderByDescending(s => s.CreatedAt)
    .Take(7);  // Last 7 days

// Use in system prompt or tool
var recentMemories = previousSoulSessions
    .SelectMany(s => s.History)
    .Where(e => e.Role == MessageRole.Assistant)
    .Select(e => e.Content);
```

**Use Cases:**

1. **Personal Assistant Memory:**
   - Daily check-in: "What's on my agenda today?"
   - Evening reflection: "Review today's accomplishments"
   - Continuous context across days

2. **Project Status Tracking:**
   - Morning standup: "What did we work on yesterday?"
   - Evening summary: "Summarize today's progress"
   - Weekly planning: "What should we focus on next week?"

3. **Long-term Learning:**
   - Daily learnings: "What did I learn today?"
   - Pattern recognition: "What themes emerge across the week?"
   - Skill development: "How am I progressing on X skill?"

## Agent-to-Agent Communication

### agent_converse Tool

Enables peer agent conversations via the `agent_converse` tool.

**Tool Definition:**

The `agent_converse` tool accepts the following parameters:

| Parameter   | Type    | Required | Description                      |
|-------------|---------|----------|----------------------------------|
| `agentId`   | string  | yes      | The target agent's ID            |
| `message`   | string  | yes      | Opening message to send          |
| `objective` | string  | no       | What you want to achieve         |
| `timeoutSeconds` | integer | no | Wall-clock budget (default: 600, maximum: 1800) |
| `maxTurns`  | integer | no       | Maximum number of turns (default: 1) |

The wall-clock timeout and `maxTurns` are independent budgets. Values above the 30-minute timeout ceiling are clamped; values below one second or non-integer values are rejected. Caller cancellation continues to stop an exchange immediately.

`ExecuteAsync` resolves the call chain to prevent cycles, then delegates to `IAgentExchangeService.ConverseAsync`
with an `AgentExchangeRequest`. `maxTurns` is clamped to `AgentExchangeOptions.MaxTurnsCeiling` (default 30).

See [AgentConverseTool.cs](../../src/gateway/BotNexus.Gateway/Tools/AgentConverseTool.cs) for the full implementation.

### Agent Exchange Service

**IAgentExchangeService:**

```csharp
public interface IAgentExchangeService
{
    Task<AgentExchangeResult> ConverseAsync(
        AgentExchangeRequest request,
        CancellationToken cancellationToken = default);
}
```

**AgentExchangeRequest:**

```csharp
public sealed record AgentExchangeRequest
{
    public required AgentId InitiatorId { get; init; }
    public required AgentId TargetId { get; init; }
    public required string Message { get; init; }
    public string? Objective { get; init; }
    public int MaxTurns { get; init; } = 1;
    public IReadOnlyList<AgentId> CallChain { get; init; } = [];

    // Observational only - the delivery address for handoff progress events.
    public SessionId? InitiatorSessionId { get; init; }
    public ConversationId? InitiatorConversationId { get; init; }
}
```

Leaving `InitiatorSessionId`/`InitiatorConversationId` null silently disables progress emission and changes
nothing else about the exchange.

**AgentExchangeResult:**

```csharp
public sealed record AgentExchangeResult
{
    public required SessionId SessionId { get; init; }
    public required ConversationId ConversationId { get; init; }
    public required string Status { get; init; }
    public required int Turns { get; init; }
    public required string FinalResponse { get; init; }
    public IReadOnlyList<AgentExchangeTranscriptEntry> Transcript { get; init; } = [];
    public string? CompletionReason { get; init; }
    public string? FinishReason { get; init; }
    public string? FinishSummary { get; init; }
}
```

Each `ConverseAsync` call creates exactly one new conversation - agent-to-agent exchanges are one-shot
bounded loops - so `ConversationId` is the handle for retrieving the persisted transcript later via
`IConversationStore.GetAsync` or `ISessionStore.ListByConversationAsync`.

**CompletionReason values:**

| Value | Meaning |
|-------|---------|
| `exchangeFinished` | The target agent invoked the `finish_agent_exchange` tool successfully. `FinishReason` and `FinishSummary` carry its arguments. |
| `singleShot` | The request set no `Objective`, so the exchange ran for exactly one prompt and returned the target's first response. |
| `maxTurnsReached` | The multi-turn loop exhausted `MaxTurns` without a completion signal. |
| `error` | An exception was thrown during the exchange. |

Completion is a **tool call**, not prose inspection - there is no substring matching on the target's reply.

### Conversation Flow

`AgentExchangeService.ConverseAsync` orchestrates the full agent-to-agent conversation:

1. **Validate request** and check authorization (the initiator's `SubAgentIds` allow-list, or a role match
   between the initiator's `SubAgentRoles` and the target's `metadata.role`)
2. **Check for cycles and depth** via `EnsureCallChainAllowed`
3. **Enforce the exchange budget** (daily cap, loop detection, cooldown). A budget refusal is the one
   terminal outcome that produces no child conversation at all, so it publishes a `Halted` progress event
   before rethrowing.
4. **Route cross-world** to `ICrossWorldExchangeRouter` when the target is a `world:` reference
5. **Create a real `Conversation`** via `IConversationStore` so the exchange is discoverable by
   `ListByConversationAsync` and the portal - the conversation owns the lifecycle, the session is one
   bounded LLM context inside it
6. **Execute turns** up to `MaxTurns`, stopping early when the target calls `finish_agent_exchange`
7. **Seal and archive**, returning the transcript and result metadata

See [AgentExchangeService.cs](../../src/gateway/BotNexus.Gateway/Agents/AgentExchangeService.cs) for the full implementation.

### Cycle Detection

**Call Chain Tracking:**

```csharp
AgentId[] callChain = [AgentA, AgentB];

// AgentB wants to call AgentC - depth 3, within the default maximum
EnsureCallChainAllowed(callChain, AgentC);  // OK

// AgentB wants to call AgentA (cycle!)
EnsureCallChainAllowed(callChain, AgentA);  // Throws InvalidOperationException

// Comparison is case-insensitive on the agent id value.
```

**Implementation:**

```csharp
private void EnsureCallChainAllowed(IReadOnlyList<AgentId> chain, AgentId targetId)
{
    if (chain.Any(id => string.Equals(id.Value, targetId.Value, StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException($"Cycle detected: {chainText}");

    // Configurable, not a constant: gateway.agentConversationMaxDepth (default 3).
    var maxDepth = _options.Value.AgentConversationMaxDepth <= 0
        ? 1
        : _options.Value.AgentConversationMaxDepth;

    if (chain.Count + 1 > maxDepth)
        throw new InvalidOperationException(
            $"Agent conversation call chain depth {chain.Count + 1} exceeded maximum configured depth {maxDepth}. Chain: {chainText}");
}
```

### Cross-World Federation

Enables conversations between agents in different BotNexus instances.

**CrossWorldAgentReference:**

Parses `world:{worldId}:{agentId}` format references for cross-world agent targeting (e.g., `world:production:data-analyst`).

See [CrossWorldAgentReference.cs](../../src/domain/BotNexus.Domain/AgentExchange/CrossWorldAgentReference.cs) for the full implementation.

**Cross-World Conversation Flow:**

When `ConverseAsync` detects a `world:` prefix in the target agent ID, it delegates to
`ICrossWorldExchangeRouter.ConverseCrossWorldAsync`, which:

1. Resolves the target world endpoint from the `gateway.crossWorld` peer configuration
2. Enforces the outbound cross-world permission gate
3. Builds the sender-side conversation and creates a `CrossWorldRelayRequest` per turn
4. Sends each turn via `CrossWorldChannelAdapter`
5. Returns the response as an `AgentExchangeResult`

Federation routing was split out of `AgentExchangeService` (#1542) so the peer, permission and call-chain
logic is testable against just a config plus a fake relay. The shared turn loop, session pinning, transcript
and seal/archive stay in `AgentExchangeTurnEngine`; the router owns only the cross-world resolution,
permission gate and per-turn relay.

See [CrossWorldExchangeRouter.cs](../../src/gateway/BotNexus.Gateway/Agents/CrossWorldExchangeRouter.cs) for the full implementation.

**CrossWorldChannelAdapter:**

Sends HTTP POST to `{endpoint}/api/federation/cross-world/relay` (the path is configurable via `RelayPath`)
with a `CrossWorldRelayRequest` body and an optional `X-Cross-World-Key` authentication header.

Two relay fields exist for multi-turn correctness:

- `CloseAfterResponse` - sender-determined finality. When `true` the receiver **must** archive its local
  conversation when the relay completes, regardless of whether the target called `finish_agent_exchange`.
  Defaults to `false` so older senders keep the previous behaviour.
- `TurnId` - optional per-turn idempotency key. When supplied, the receiver skips re-appending the user
  turn if the last history entry already carries the same key, preventing duplicate entries on
  cancel-and-retry.

The response (`CrossWorldRelayResponse`) carries `Response`, `Status`, `SessionId`, and the
`ExchangeFinished`/`FinishReason`/`FinishSummary` completion signals.

See [CrossWorldChannelAdapter.cs](../../src/gateway/BotNexus.Gateway.Channels/CrossWorldChannelAdapter.cs) for the full implementation.

**Relay Endpoint (Target World):**

Receives relay requests at `POST /api/federation/cross-world/relay`, authenticates via the
`X-Cross-World-Key` header, creates a cross-world session, executes the target agent, and returns the
response. See [Cross-World Relay](../api-reference#cross-world-relay) for the REST contract and
[`gateway.crossWorld`](../configuration) for the peer and inbound allow-list configuration.

## Summary

**Internal Triggers:**

- **Cron**: Scheduled agent execution (reports, monitoring, maintenance)
- **Soul**: Daily soul sessions with reflection (long-term memory)
- **Heartbeat**: System liveness ticks

**Agent-to-Agent:**

- **agent_converse tool**: Peer conversations within same world, served by `IAgentExchangeService`
- **Cycle detection**: Prevents infinite call loops (case-insensitive on the agent id)
- **Call chain tracking**: Depth capped by `gateway.agentConversationMaxDepth` (default 3)
- **Authorization**: The initiator's `SubAgentIds` allow-list, or a `SubAgentRoles`/`metadata.role` match
- **Completion**: The target calls `finish_agent_exchange`; there is no prose inspection

**Cross-World Federation:**

- **world:worldId:agentId**: Reference agents in other BotNexus instances
- **CrossWorldChannelAdapter**: HTTP-based relay protocol to `/api/federation/cross-world/relay`
- **Dual sessions**: Source and target both create sessions
- **Authentication**: X-Cross-World-Key header validation
- **Use cases**: Multi-datacenter deployments, team collaboration, specialized agent clusters
