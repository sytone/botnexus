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
        CancellationToken ct = default);
}
```

**Trigger Types:**
- `Cron`: Scheduled execution via cron expressions
- `Soul`: Daily soul session heartbeat

### Cron Trigger

**Purpose:**
- Execute agents on a schedule (hourly, daily, weekly, etc.)
- Useful for periodic tasks (backups, reports, monitoring)
- No user interaction required

**Implementation:**

`CreateSessionAsync` creates a unique cron session, adds the prompt as a user message, executes the agent, records the response, and saves the session.

See [CronChannelAdapter.cs](../../src/gateway/BotNexus.Gateway.Api/Hubs/CronChannelAdapter.cs) for the full implementation.

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
- **Session ID format:** `soul:{agentId}:{yyyy-MM-dd}`
- **Seals older soul sessions** before creating today's session
- **Optional reflection prompt** before sealing, configurable via `SoulAgentConfig.ReflectionOnSeal`

See [SoulTrigger.cs](../../src/gateway/BotNexus.Gateway.Api/Hubs/SoulTrigger.cs) for the full implementation.

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
public record SoulAgentConfig
{
    public string? TimeZone { get; init; }              // IANA timezone (e.g., "America/Los_Angeles")
    public TimeSpan? DayBoundary { get; init; }         // When new day starts (default: 00:00)
    public bool ReflectionOnSeal { get; init; }         // Run reflection before sealing
    public string? ReflectionPrompt { get; init; }      // Prompt for end-of-day reflection
    public int RetentionDays { get; init; }             // How long to keep sealed sessions
}
```

**Example Configuration:**

```json
{
  "id": "personal-assistant",
  "displayName": "Personal Assistant",
  "model": "anthropic:claude-sonnet-4",
  "soul": {
    "timeZone": "America/Los_Angeles",
    "dayBoundary": "04:00:00",
    "reflectionOnSeal": true,
    "reflectionPrompt": "Reflect on today's conversations. What did you learn? What should you remember for tomorrow?",
    "retentionDays": 90
  }
}
```

**Soul Session Lifecycle:**

```text
Day 1 (2024-01-15):
  - Session: soul:personal-assistant:2024-01-15
  - Status: Active
  - Multiple heartbeat prompts throughout the day
  - Accumulates conversation history

Day 2 (2024-01-16):
  - Previous session (2024-01-15) gets reflection prompt
  - Previous session sealed (Status: Sealed)
  - New session: soul:personal-assistant:2024-01-16
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
| `maxTurns`  | integer | no       | Maximum number of turns (default: 1) |

`ExecuteAsync` resolves the call chain to prevent cycles, then delegates to `IAgentConversationService.ConverseAsync` with a `ConversationRequest`.

See [AgentConverseTool.cs](../../src/gateway/BotNexus.Gateway/Tools/AgentConverseTool.cs) for the full implementation.

### Agent Conversation Service

**IAgentConversationService:**

```csharp
public interface IAgentConversationService
{
    Task<AgentConversationResult> ConverseAsync(
        ConversationRequest request,
        CancellationToken ct = default);
}
```

**ConversationRequest:**

```csharp
public record ConversationRequest
{
    public AgentId InitiatorId { get; init; }
    public AgentId TargetId { get; init; }
    public string Message { get; init; }
    public string? Objective { get; init; }
    public int MaxTurns { get; init; } = 1;
    public IReadOnlyList<AgentId> CallChain { get; init; } = [];
}
```

**AgentConversationResult:**

```csharp
public record AgentConversationResult
{
    public SessionId SessionId { get; init; }
    public string FinalResponse { get; init; }
    public IReadOnlyList<AgentConversationTranscriptEntry> Transcript { get; init; }
    public bool ObjectiveMet { get; init; }
    public int TurnCount { get; init; }
}
```

### Conversation Flow

`AgentConversationService.ConverseAsync` orchestrates the full agent-to-agent conversation:

1. **Validate request** and check authorization (`SubAgentIds` whitelist)
2. **Check for cycles** via call chain tracking
3. **Create AgentAgent session** with both participants (initiator + target)
4. **Execute conversation turns** up to `maxTurns`
5. **Check objective completion** after each turn
6. **Save session** and return transcript with result metadata

See [AgentConversationService.cs](../../src/gateway/BotNexus.Gateway/Agents/AgentConversationService.cs) for the full implementation.

### Cycle Detection

**Call Chain Tracking:**

```csharp
AgentId[] callChain = [AgentA, AgentB, AgentC];

// AgentC wants to call AgentD
EnsureCallChainAllowed(callChain, AgentD);  // OK

// AgentC wants to call AgentA (cycle!)
EnsureCallChainAllowed(callChain, AgentA);  // Throws InvalidOperationException
```

**Implementation:**

```csharp
void EnsureCallChainAllowed(IReadOnlyList<AgentId> chain, AgentId targetId)
{
    if (chain.Contains(targetId))
        throw new InvalidOperationException(
            $"Conversation cycle detected: {string.Join(" → ", chain)} → {targetId}");
    
    const int MaxDepth = 5;
    if (chain.Count >= MaxDepth)
        throw new InvalidOperationException(
            $"Maximum conversation depth ({MaxDepth}) exceeded.");
}
```

### Cross-World Federation

Enables conversations between agents in different BotNexus instances.

**CrossWorldAgentReference:**

Parses `world:{worldId}:{agentId}` format references for cross-world agent targeting (e.g., `world:production:data-analyst`).

See [CrossWorldAgentReference.cs](../../src/domain/BotNexus.Domain/Conversations/CrossWorldAgentReference.cs) for the full implementation.

**Cross-World Conversation Flow:**

When `ConverseAsync` detects a `world:` prefix in the target agent ID, it delegates to `ConverseCrossWorldAsync`, which:

1. Resolves the target world endpoint from `FederatedWorlds` configuration
2. Creates a `CrossWorldRelayRequest` with source/target metadata and the call chain
3. Sends the request via `CrossWorldChannelAdapter`
4. Returns the response as an `AgentConversationResult`

See [AgentConversationService.cs](../../src/gateway/BotNexus.Gateway/Agents/AgentConversationService.cs) for the full implementation.

**CrossWorldChannelAdapter:**

Sends HTTP POST to `{endpoint}/api/federation/relay` with a `CrossWorldRelayRequest` body and optional `X-Cross-World-Key` authentication header.

See [CrossWorldChannelAdapter.cs](../../src/gateway/BotNexus.Gateway.Channels/CrossWorldChannelAdapter.cs) for the full implementation.

**Relay Endpoint (Target World):**

Receives relay requests at `POST api/federation/relay`, authenticates via `X-Cross-World-Key` header, creates a cross-world session, executes the target agent, and returns the response.

<!-- Note: Implementation is in the federation controller/hub handling relay requests -->

## Summary

**Internal Triggers:**

- **Cron**: Scheduled agent execution (reports, monitoring, maintenance)
- **Soul**: Daily soul sessions with reflection (long-term memory)

**Agent-to-Agent:**

- **agent_converse tool**: Peer conversations within same world
- **Cycle detection**: Prevents infinite call loops
- **Call chain tracking**: Records conversation depth
- **Authorization**: SubAgentIds whitelist controls who can talk to whom

**Cross-World Federation:**

- **world:worldId:agentId**: Reference agents in other BotNexus instances
- **CrossWorldChannelAdapter**: HTTP-based relay protocol
- **Dual sessions**: Source and target both create sessions
- **Authentication**: X-Cross-World-Key header validation
- **Use cases**: Multi-datacenter deployments, team collaboration, specialized agent clusters
