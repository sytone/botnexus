# Domain Model

**Purpose:** Core domain objects, value objects, and domain rules that define BotNexus's conceptual model.

---

## Core Primitives

### Strongly-Typed IDs

| Type | Description | Format Example |
|------|-------------|----------------|
| `AgentId` | Agent identifier | `"gateway"`, `"coding-agent"` |
| `SessionId` | Session identifier | `"sess_abc123"`, `"soul:gateway:2025-01-15"` |
| `ChannelKey` | Channel type | `"signalr"`, `"telegram"`, `"cron"` |
| `SenderId` | Sender identifier | connectionId, userId, agentId |
| `WorldId` | World identifier | `"default"`, `"prod-cluster"` |
| `ConversationId` | Alias for SessionId | Used in some contexts |

**Value object behavior:**

- Immutable records with structural equality
- Parse/create factory methods with validation
- Implicit/explicit conversions to/from string
- `ToString()` for serialization

---

## Domain Value Objects

### SessionType (Smart Enum)

Discriminates different conversation flows:

| Type | Description | Created By |
|------|-------------|------------|
| `UserAgent` | User ↔ Agent conversation | WebUI, CLI, external channels |
| `AgentSelf` | Agent internal reflection | Agent-initiated introspection |
| `AgentSubAgent` | Parent ↔ Sub-agent task | `subagent_spawn` tool |
| `AgentAgent` | Peer agent-to-agent | `agent_converse` tool |
| `Soul` | Daily persistent memory | Soul trigger (heartbeat) |
| `Cron` | Scheduled execution | Cron trigger |

**Domain Rules:**

- `UserAgent` requires `ChannelKey`
- `Soul` uses date-based session IDs: `soul:{agentId}:{yyyy-MM-dd}`
- `Cron` sessions are sealed after execution
- `AgentAgent` tracks caller/target participants

---

### SessionStatus (Smart Enum)

Session lifecycle state:

| Status | Description | Transitions To |
|--------|-------------|----------------|
| `Active` | Accepting new messages | Suspended, Sealed |
| `Suspended` | Paused, can resume | Active, Sealed |
| `Sealed` | Completed, read-only | (none – terminal) |

**Domain Rules:**

- Only `Active` sessions accept new messages
- `Sealed` prevents all writes
- `Suspended` preserves state but blocks message dispatch

---

### ParticipantType (Smart Enum)

Session participant roles:

| Type | Description | Example ID |
|------|-------------|------------|
| `User` | Human user | connectionId, userId |
| `Agent` | AI agent | agentId |
| `System` | Platform-generated | "system" |

---

### ExecutionStrategy (Smart Enum)

Agent execution environment:

| Strategy | Description | Latency | Security |
|----------|-------------|---------|----------|
| `InProcess` | Same process as Gateway | <10ms | Shared memory |
| `Container` | Docker isolation | 1-3s | Network + filesystem isolation |
| `Remote` | Separate machine | 50-100ms | Full isolation + distribution |
| `Sandbox` | OS-level process sandbox | 100-500ms | Process isolation |

---

## Domain Entities

### GatewaySession

Aggregate root for sessions:

```csharp
public class GatewaySession
{
    public SessionId SessionId { get; set; }
    public AgentId AgentId { get; set; }
    public SessionType SessionType { get; set; }
    public SessionStatus Status { get; set; }
    public ChannelKey? ChannelType { get; set; }
    public string? CallerId { get; set; }  // For AgentAgent sessions
    
    // Collections
    public List<SessionParticipant> Participants { get; set; }
    public List<SessionEntry> History { get; set; }
    public Dictionary<string, object?> Metadata { get; set; }
    
    // Timestamps
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
```

**Invariants:**

- `SessionId` must be unique
- `AgentId` must exist in registry
- `UserAgent` sessions must have `ChannelType`
- `Participants` must have at least one entry
- `Status` transitions are monotonic (can't go back to Active from Sealed)

---

### SessionParticipant

Value object representing session participants:

```csharp
public record SessionParticipant
{
    public ParticipantType Type { get; init; }
    public string Id { get; init; }
    public string? Role { get; init; }
    public DateTimeOffset? JoinedAt { get; init; }
}
```

**Examples:**

- User participant: `{ Type: User, Id: "conn_abc123" }`
- Agent participant: `{ Type: Agent, Id: "gateway" }`
- Caller/target: `{ Type: Agent, Id: "peer-agent", Role: "target" }`

---

### AgentDescriptor

Configuration record for agents:

```csharp
public record AgentDescriptor
{
    public AgentId AgentId { get; init; }
    public string DisplayName { get; init; }
    public string? Description { get; init; }
    
    // Model Configuration
    public string ApiProvider { get; init; }
    public string ModelId { get; init; }
    
    // Execution
    public ExecutionStrategy ExecutionStrategy { get; init; }
    public int MaxConcurrentSessions { get; init; }
    
    // Tools and Capabilities
    public List<string> Tools { get; init; }
    public List<string> SubAgentIds { get; init; }
    
    // Prompts
    public string? SystemPrompt { get; init; }
    public string? HeartbeatPrompt { get; init; }
    
    // Security
    public FileAccessPolicy FileAccess { get; init; }
    
    // Extensions
    public List<ExtensionReference> Extensions { get; init; }
    
    // Soul Configuration
    public SoulAgentConfig? Soul { get; init; }
}
```

**Domain Rules:**

- `AgentId`, `DisplayName`, `ApiProvider`, `ModelId` are required
- `MaxConcurrentSessions = 0` means unlimited
- `SubAgentIds` must reference registered agents
- `Tools` must match registered tool names

---

### FileAccessPolicy

Security boundary for file operations:

```csharp
public record FileAccessPolicy
{
    public string WorkspaceRoot { get; init; }
    public List<string> AllowedPaths { get; init; }
    public List<string> DeniedPaths { get; init; }
    public bool AllowWorkspaceEscape { get; init; } = false;
}
```

**Domain Rules:**

- All paths must be absolute
- `AllowedPaths` uses glob patterns
- `DeniedPaths` takes precedence over `AllowedPaths`
- `AllowWorkspaceEscape = false` (default) restricts to workspace root

---

### SoulAgentConfig

Configuration for daily soul sessions:

```csharp
public record SoulAgentConfig
{
    public bool Enabled { get; init; }
    public string? CronSchedule { get; init; }  // e.g., "0 8 * * *"
    public string? Timezone { get; init; }      // IANA timezone
    public string? HeartbeatPrompt { get; init; }
    public bool SealPreviousSession { get; init; } = true;
}
```

**Domain Rules:**

- `Enabled = true` requires `CronSchedule`
- Session ID format: `soul:{agentId}:{yyyy-MM-dd}`
- Previous day's session is sealed before creating new one
- Heartbeat prompt runs daily at scheduled time

---

## Domain Services

### World Identity

Cross-world federation uses a hierarchical identity model:

```
world:{worldId}:{agentId}
```

**Examples:**

- Local agent: `"gateway"` (within same world)
- Remote agent: `"world:prod-cluster:gateway"` (cross-world)

**Domain Rules:**

- Local references use bare `agentId`
- Cross-world references use full `world:{worldId}:{agentId}` format
- `WorldId` must be registered in federation registry
- Cross-world calls require authentication (`X-Cross-World-Key` header)

---

### Location Abstraction

Resources are scoped by Location:

```csharp
public record Location(WorldId? WorldId, AgentId AgentId, SessionId? SessionId);
```

**Scopes:**

| Location | Scope | Example Usage |
|----------|-------|---------------|
| `(null, agentId, null)` | Global agent | Workspace directory |
| `(worldId, agentId, null)` | World-scoped agent | Cross-world resources |
| `(null, agentId, sessionId)` | Agent + session | Session-specific memory |

---

## Domain Events

### Session Lifecycle Events

- `SessionCreated(SessionId, AgentId, SessionType)`
- `SessionUpdated(SessionId, UpdatedAt)`
- `SessionSealed(SessionId, SealedAt)`
- `SessionSuspended(SessionId, Reason)`
- `SessionResumed(SessionId)`

### Agent Lifecycle Events

- `AgentRegistered(AgentId, Descriptor)`
- `AgentInstanceCreated(AgentId, SessionId, InstanceId)`
- `AgentInstanceStopped(InstanceId)`

---

## Ubiquitous Language

| Term | Domain Meaning |
|------|----------------|
| **World** | Isolated BotNexus instance with its own agents and sessions |
| **Agent** | AI assistant configured with model, tools, and execution strategy |
| **Session** | Conversation between participants (users, agents, system) |
| **Channel** | Communication medium (SignalR, Telegram, cron, etc.) |
| **Workspace** | Agent-specific file system directory |
| **Soul** | Daily persistent memory session for agent self-reflection |
| **Trigger** | Internal event source (cron, soul heartbeat) |
| **Isolation** | Execution environment (in-process, container, remote) |
| **Hook** | Interception point for tool execution (before/after) |
| **Participant** | User, agent, or system entity in a session |
| **Location** | (WorldId, AgentId, SessionId?) resource scoping tuple |

---

## Domain Invariants

### Session Invariants

1. `SessionId` is unique across all sessions
2. `Active` sessions must have at least one participant
3. `Sealed` sessions cannot accept new messages
4. `Soul` sessions use date-based IDs
5. `AgentAgent` sessions have caller + target participants

### Agent Invariants

1. `AgentId` is unique within a world
2. `MaxConcurrentSessions` enforced by supervisor
3. `SubAgentIds` must reference registered agents
4. `FileAccessPolicy` paths must be absolute

### Execution Invariants

1. Each (agentId, sessionId) pair gets a unique instance
2. Agent instances are isolated per execution strategy
3. Tools operate within `FileAccessPolicy` boundaries
4. Cross-world calls require authentication

---

## Summary

The domain model provides a clean, framework-agnostic foundation for BotNexus. All higher layers (Gateway, AgentCore, Providers) depend on these primitives and follow the domain rules. Extensions and customizations respect domain invariants through interfaces and policies.

**For implementation details:**

- **[System Flows](system-flows.md)** — How domain objects participate in runtime flows
- **[Principles](principles.md)** — Design decisions behind the domain model
- **[Development Guide](../development/domain-model.md)** — Code-level domain model implementation
