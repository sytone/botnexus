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

```csharp
public sealed class CronTrigger : IInternalTrigger
{
    public TriggerType Type => TriggerType.Cron;
    public string DisplayName => "Cron Scheduler";
    
    public async Task<SessionId> CreateSessionAsync(
        AgentId agentId,
        string prompt,
        CancellationToken ct)
    {
        // 1. Create unique session ID
        var sessionId = SessionId.From($"cron:{DateTimeOffset.UtcNow:yyyyMMddHHmmss}:{Guid.NewGuid():N}");
        
        // 2. Create session
        var session = await _sessions.GetOrCreateAsync(sessionId, agentId, ct);
        session.ChannelType = ChannelKey.From("cron");
        session.CallerId = $"cron:{agentId.Value}";
        session.SessionType = SessionType.Cron;
        
        // 3. Add user message
        session.AddEntry(new SessionEntry {
            Role = MessageRole.User,
            Content = prompt
        });
        
        // 4. Execute agent
        var handle = await _supervisor.GetOrCreateAsync(agentId, sessionId, ct);
        var response = await handle.PromptAsync(prompt, ct);
        
        // 5. Record response
        session.AddEntry(new SessionEntry {
            Role = MessageRole.Assistant,
            Content = response.Content
        });
        
        await _sessions.SaveAsync(session, ct);
        return sessionId;
    }
}
```

**Execution Flow:**

```
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

```csharp
public sealed class SoulTrigger : IInternalTrigger
{
    public TriggerType Type => TriggerType.Soul;
    public string DisplayName => "Soul Session";
    
    public async Task<SessionId> CreateSessionAsync(
        AgentId agentId,
        string prompt,
        CancellationToken ct)
    {
        // 1. Resolve soul date (respects timezone)
        var soulConfig = _registry.Get(agentId)?.Soul;
        var (timeZone, dayBoundary) = ResolveCalendarSettings(soulConfig);
        var nowUtc = _timeProvider.GetUtcNow();
        var soulDate = ResolveSoulDate(nowUtc, timeZone, dayBoundary);
        
        // 2. Create session ID tied to date
        var sessionId = SessionId.ForSoul(agentId, soulDate);
        // Format: "soul:{agentId}:{yyyy-MM-dd}"
        
        // 3. Seal older soul sessions
        await SealOlderSoulSessionsAsync(agentId, soulDate, soulConfig, ct);
        
        // 4. Get or create today's soul session
        var session = await _sessions.GetOrCreateAsync(sessionId, agentId, ct);
        InitializeSoulSession(session, agentId, soulDate);
        
        // 5. Execute heartbeat prompt
        session.AddEntry(new SessionEntry {
            Role = MessageRole.User,
            Content = prompt
        });
        
        var handle = await _supervisor.GetOrCreateAsync(agentId, sessionId, ct);
        var response = await handle.PromptAsync(prompt, ct);
        
        session.AddEntry(new SessionEntry {
            Role = MessageRole.Assistant,
            Content = response.Content
        });
        
        await _sessions.SaveAsync(session, ct);
        return sessionId;
    }
    
    private async Task SealOlderSoulSessionsAsync(
        AgentId agentId,
        DateOnly todaySoulDate,
        SoulAgentConfig? soulConfig,
        CancellationToken ct)
    {
        // Find all active soul sessions older than today
        var agentSessions = await _sessions.ListAsync(agentId, ct);
        var oldActiveSoulSessions = agentSessions
            .Where(s => s.SessionType == SessionType.Soul &&
                       s.Status == SessionStatus.Active)
            .Where(s => TryGetSoulDate(s, out var soulDate) &&
                       soulDate < todaySoulDate)
            .ToArray();
        
        foreach (var previousSession in oldActiveSoulSessions)
        {
            // Optional: Reflection before sealing
            if (soulConfig?.ReflectionOnSeal == true &&
                !string.IsNullOrWhiteSpace(soulConfig.ReflectionPrompt))
            {
                var reflectionPrompt = soulConfig.ReflectionPrompt;
                previousSession.AddEntry(new SessionEntry {
                    Role = MessageRole.User,
                    Content = reflectionPrompt
                });
                
                var handle = await _supervisor.GetOrCreateAsync(
                    agentId, previousSession.SessionId, ct);
                var reflection = await handle.PromptAsync(reflectionPrompt, ct);
                
                previousSession.AddEntry(new SessionEntry {
                    Role = MessageRole.Assistant,
                    Content = reflection.Content
                });
            }
            
            // Seal session
            previousSession.Status = SessionStatus.Sealed;
            await _sessions.SaveAsync(previousSession, ct);
            
            _logger.LogInformation(
                "Sealed soul session '{SessionId}' for agent '{AgentId}'.",
                previousSession.SessionId, agentId);
        }
    }
}
```

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

```
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

```csharp
public sealed class AgentConverseTool : IAgentTool
{
    public string Name => "agent_converse";
    
    public Tool Definition => new Tool(
        "agent_converse",
        "Start a conversation with another registered agent.",
        schema: {
            "type": "object",
            "properties": {
                "agentId": {
                    "type": "string",
                    "description": "The target agent's ID."
                },
                "message": {
                    "type": "string",
                    "description": "Opening message to send."
                },
                "objective": {
                    "type": "string",
                    "description": "What you want to achieve."
                },
                "maxTurns": {
                    "type": "integer",
                    "minimum": 1,
                    "default": 1,
                    "description": "Maximum number of turns."
                }
            },
            "required": ["agentId", "message"]
        }
    );
}
```

**Execution Flow:**

```csharp
public async Task<AgentToolResult> ExecuteAsync(
    string toolCallId,
    IReadOnlyDictionary<string, object?> arguments,
    CancellationToken ct)
{
    var targetAgentId = ReadString(arguments, "agentId");
    var message = ReadString(arguments, "message");
    var objective = ReadString(arguments, "objective");
    var maxTurns = ReadInt(arguments, "maxTurns", 1);
    
    // Resolve call chain (prevent cycles)
    var callChain = await ResolveCallChainAsync(ct);
    
    // Execute conversation
    var result = await _conversationService.ConverseAsync(
        new ConversationRequest
        {
            InitiatorId = _initiatorAgentId,
            TargetId = AgentId.From(targetAgentId),
            Message = message,
            Objective = objective,
            MaxTurns = maxTurns,
            CallChain = callChain
        },
        ct);
    
    return new AgentToolResult([
        new AgentToolContent(Text, JsonSerializer.Serialize(result))
    ]);
}
```

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

```csharp
public async Task<AgentConversationResult> ConverseAsync(
    ConversationRequest request,
    CancellationToken ct)
{
    // 1. Validate request
    ValidateRequest(request);
    
    // 2. Check authorization
    var initiatorDescriptor = _registry.Get(request.InitiatorId);
    if (!initiatorDescriptor.SubAgentIds.Contains(request.TargetId.Value))
        throw new UnauthorizedAccessException(
            $"Agent '{request.InitiatorId}' cannot converse with '{request.TargetId}'.");
    
    // 3. Check for cycles
    var normalizedChain = NormalizeChain(request.CallChain, request.InitiatorId);
    EnsureCallChainAllowed(normalizedChain, request.TargetId);
    
    // 4. Create session
    var sessionId = SessionId.ForAgentConversation(
        request.InitiatorId,
        request.TargetId,
        Guid.NewGuid().ToString("N"));
    
    var session = await _sessionStore.GetOrCreateAsync(sessionId, request.InitiatorId, ct);
    session.SessionType = SessionType.AgentAgent;
    session.Participants.Add(new SessionParticipant {
        Type = ParticipantType.Agent,
        Id = request.InitiatorId.Value,
        Role = "initiator"
    });
    session.Participants.Add(new SessionParticipant {
        Type = ParticipantType.Agent,
        Id = request.TargetId.Value,
        Role = "target"
    });
    
    // Store call chain in metadata
    session.Metadata["callChain"] = normalizedChain
        .Append(request.TargetId)
        .Select(id => id.Value)
        .ToArray();
    
    // 5. Execute conversation turns
    var transcript = new List<AgentConversationTranscriptEntry>();
    var targetHandle = await _supervisor.GetOrCreateAsync(
        request.TargetId, sessionId, ct);
    
    var message = request.Message;
    var finalResponse = string.Empty;
    
    for (var turn = 0; turn < request.MaxTurns; turn++)
    {
        // User turn (from initiator)
        AddTurn(MessageRole.User, message, transcript, session);
        
        // Agent response (from target)
        var response = await targetHandle.PromptAsync(message, ct);
        finalResponse = response.Content ?? string.Empty;
        AddTurn(MessageRole.Assistant, finalResponse, transcript, session);
        
        // Check if objective met
        if (IsObjectiveMet(request.Objective, finalResponse))
            break;
        
        // Multi-turn: generate next user message (not implemented yet)
        if (turn < request.MaxTurns - 1)
        {
            message = GenerateFollowUpMessage(request.Objective, finalResponse);
        }
    }
    
    await _sessionStore.SaveAsync(session, ct);
    
    return new AgentConversationResult
    {
        SessionId = sessionId,
        FinalResponse = finalResponse,
        Transcript = transcript,
        ObjectiveMet = IsObjectiveMet(request.Objective, finalResponse),
        TurnCount = transcript.Count / 2
    };
}
```

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

```csharp
// Format: world:worldId:agentId
// Example: world:production:data-analyst
public static class CrossWorldAgentReference
{
    public static bool TryParse(string reference, out ParsedReference? result)
    {
        if (reference.StartsWith("world:"))
        {
            var parts = reference.Split(':', 3);
            if (parts.Length == 3)
            {
                result = new ParsedReference {
                    WorldId = parts[1],
                    AgentId = parts[2]
                };
                return true;
            }
        }
        result = null;
        return false;
    }
}
```

**Cross-World Conversation Flow:**

```csharp
if (CrossWorldAgentReference.TryParse(request.TargetId, out var crossWorld))
{
    return await ConverseCrossWorldAsync(
        request,
        crossWorld,
        normalizedChain,
        ct);
}

async Task<AgentConversationResult> ConverseCrossWorldAsync(
    ConversationRequest request,
    CrossWorldAgentReference target,
    IReadOnlyList<AgentId> callChain,
    CancellationToken ct)
{
    // 1. Resolve target world endpoint
    var targetWorld = _platformConfig.FederatedWorlds
        .FirstOrDefault(w => w.Id == target.WorldId);
    if (targetWorld == null)
        throw new KeyNotFoundException($"World '{target.WorldId}' not configured.");
    
    // 2. Create relay request
    var relayRequest = new CrossWorldRelayRequest
    {
        SourceWorldId = _sourceWorldId,
        SourceAgentId = request.InitiatorId.Value,
        TargetAgentId = target.AgentId,
        Message = request.Message,
        ConversationId = Guid.NewGuid().ToString("N"),
        CallChain = callChain.Select(id => id.Value).ToArray()
    };
    
    // 3. Send via CrossWorldChannelAdapter
    var response = await _crossWorldChannelAdapter.ExchangeAsync(
        new OutboundMessage
        {
            Content = request.Message,
            Metadata = new Dictionary<string, object?>
            {
                ["endpoint"] = targetWorld.Endpoint,
                ["sourceWorldId"] = _sourceWorldId,
                ["sourceAgentId"] = request.InitiatorId.Value,
                ["targetAgentId"] = target.AgentId,
                ["conversationId"] = relayRequest.ConversationId,
                ["apiKey"] = targetWorld.ApiKey
            }
        },
        ct);
    
    // 4. Return result
    return new AgentConversationResult
    {
        SessionId = SessionId.From($"cross-world:{relayRequest.ConversationId}"),
        FinalResponse = response.Content,
        Transcript = [
            new AgentConversationTranscriptEntry {
                Role = MessageRole.User,
                Content = request.Message
            },
            new AgentConversationTranscriptEntry {
                Role = MessageRole.Assistant,
                Content = response.Content
            }
        ],
        ObjectiveMet = IsObjectiveMet(request.Objective, response.Content),
        TurnCount = 1
    };
}
```

**CrossWorldChannelAdapter:**

```csharp
public async Task<CrossWorldRelayResponse> ExchangeAsync(
    OutboundMessage message,
    CancellationToken ct)
{
    var endpoint = RequireMetadata(message.Metadata, "endpoint");
    var apiKey = TryGetMetadata(message.Metadata, "apiKey");
    
    var requestUri = new Uri($"{endpoint.TrimEnd('/')}/api/federation/relay");
    
    using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
    {
        Content = JsonContent.Create(new CrossWorldRelayRequest
        {
            SourceWorldId = metadata["sourceWorldId"],
            SourceAgentId = metadata["sourceAgentId"],
            TargetAgentId = metadata["targetAgentId"],
            Message = message.Content,
            ConversationId = metadata["conversationId"]
        })
    };
    
    if (!string.IsNullOrWhiteSpace(apiKey))
        request.Headers.Add("X-Cross-World-Key", apiKey);
    
    using var response = await _httpClient.SendAsync(request, ct);
    response.EnsureSuccessStatusCode();
    
    return await response.Content.ReadFromJsonAsync<CrossWorldRelayResponse>(ct);
}
```

**Relay Endpoint (Target World):**

```csharp
[HttpPost("api/federation/relay")]
public async Task<CrossWorldRelayResponse> RelayAsync(
    [FromBody] CrossWorldRelayRequest request,
    CancellationToken ct)
{
    // 1. Authenticate
    var apiKey = Request.Headers["X-Cross-World-Key"].FirstOrDefault();
    _authService.ValidateCrossWorldKey(request.SourceWorldId, apiKey);
    
    // 2. Create session
    var sessionId = SessionId.From($"cross-world:{request.ConversationId}");
    var session = await _sessions.GetOrCreateAsync(
        sessionId,
        AgentId.From(request.TargetAgentId),
        ct);
    
    session.SessionType = SessionType.AgentAgent;
    session.Metadata["sourceWorldId"] = request.SourceWorldId;
    session.Metadata["sourceAgentId"] = request.SourceAgentId;
    
    // 3. Execute agent
    var handle = await _supervisor.GetOrCreateAsync(
        AgentId.From(request.TargetAgentId),
        sessionId,
        ct);
    
    var response = await handle.PromptAsync(request.Message, ct);
    
    // 4. Return response
    return new CrossWorldRelayResponse
    {
        ConversationId = request.ConversationId,
        Content = response.Content,
        SessionId = sessionId.Value
    };
}
```

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
