---
status: deferred
depends-on: Phase 1.3 (Participants), Phase 3.1 (Existence queries)
created: 2026-04-12
---

# Phase 5: Agent-to-Agent Communication

## Summary

Enable registered agents to have conversations with each other. An agent can call upon another agent - one with its own Soul, Identity, and Existence - and have a structured conversation to get help, share information, or delegate specialized work. This is fundamentally different from sub-agents (disposable workers) - this is two colleagues talking.

## Current State

- Agents can spawn sub-agents (stateless workers that use the parent's prompt)
- No mechanism for Agent A to talk to Agent B as peers
- `DefaultAgentCommunicator` has a `CrossSessionSendAsync` method but it's for one-shot message delivery, not conversations

## Target State

Agent A can invoke a tool to start a conversation with Agent B. The system creates a session, routes messages back and forth until the objective is met, then seals the session. Both agents retain the conversation in their Existence.

## Detailed Design

### The `agent_converse` Tool

```json
{
  "name": "agent_converse",
  "description": "Start a conversation with another registered agent",
  "parameters": {
    "agentId": { "type": "string", "description": "The target agent's ID" },
    "message": { "type": "string", "description": "Opening message to send" },
    "objective": { "type": "string", "description": "What you want to achieve (used for auto-seal detection)" },
    "maxTurns": { "type": "integer", "default": 10, "description": "Maximum back-and-forth turns before auto-seal" }
  }
}
```

**Return shape**: The tool returns when the conversation completes (target responds, objective met, or max turns reached). Returns the full conversation transcript and final response.

```json
{
  "sessionId": "nova::agent-agent::leela::abc123",
  "status": "sealed",
  "turns": 4,
  "finalResponse": "Here's the analysis you requested...",
  "transcript": [
    { "role": "user", "content": "Can you review this design?" },
    { "role": "assistant", "content": "Sure, let me look at it..." },
    ...
  ]
}
```

### Session Model

**Creation**:
- SessionType: `SessionType.AgentAgent`
- SessionId: `SessionId.ForAgentConversation(initiatorId, targetId, uniqueId)`
- AgentId (owner): the initiating agent
- Participants: both agents with their roles
  - Initiator: `SessionParticipant { Type = Agent, Id = initiatorAgentId, Role = "initiator" }`
  - Target: `SessionParticipant { Type = Agent, Id = targetAgentId, Role = "target" }`
- IsInteractive: false (users cannot inject)
- ChannelType: null (internal, no external channel)

**Message roles**:
- Initiating agent's messages: `MessageRole.User` (it's the one asking)
- Target agent's messages: `MessageRole.Assistant` (it's the one responding)
- This means the target agent's system prompt, tools, and behavior all apply naturally - it's being interacted with just like a human would interact with it

**Lifecycle**:
- Created when `agent_converse` is called
- Conversation runs synchronously from the initiator's perspective (the tool blocks while turns execute)
- Sealed when: objective is met (target indicates completion), max turns reached, error occurs, or initiator tool times out
- Each new conversation creates a new session - no reuse

### Conversation Loop

```
Initiator calls agent_converse(agentId="leela", message="Review this design", maxTurns=10)
    |
    v
[Create Agent-Agent session]
    |
    v
[Send initiator's message as "user" to target agent's handle]
    |
    v
[Target agent processes, responds as "assistant"]
    |
    v
[Check: objective met? max turns? error?]
    |-- No --> [Initiator reviews response, may send follow-up "user" message]
    |           (automated by tool based on objective, or single-turn by default)
    |-- Yes --> [Seal session, return result to initiator]
```

**Default behavior**: Single-turn (initiator sends one message, target responds, session sealed). Multi-turn requires explicit `maxTurns > 1` and the tool manages the back-and-forth automatically.

### Existence Integration

The session appears in BOTH agents' Existence:
- Initiator finds it via `AgentId == initiatorId` (owned sessions)
- Target finds it via `Participants.Any(p => p.Id == targetId)` (participated sessions)

This requires Phase 3.1 (Existence dual-lookup) to be delivered.

### Cycle Detection

Prevent: Agent A calls Agent B, which calls Agent A, which calls Agent B...

**Implementation**:
- Each `agent_converse` call includes a `callChain` in session metadata: `["nova", "leela", "hermes"]`
- Before creating the session, check if the target agent is already in the call chain
- If so, reject with error: "Cycle detected: {chain} -> {target}"
- Enforce a maximum chain depth (configurable, default 3)

```csharp
public sealed record ConversationRequest
{
    public required AgentId InitiatorId { get; init; }
    public required AgentId TargetId { get; init; }
    public required string Message { get; init; }
    public string? Objective { get; init; }
    public int MaxTurns { get; init; } = 1;
    public IReadOnlyList<AgentId> CallChain { get; init; } = [];
}
```

### Access Control

Not every agent can call every other agent. The initiator's `AgentDescriptor.SubAgentIds` list (or a new `ConversableAgentIds` list) controls which agents it can converse with. If the target isn't in the allowlist, the tool returns an error.

### Error Handling

| Scenario | Behavior |
|----------|----------|
| Target agent not registered | Tool returns error |
| Target agent not in allowlist | Tool returns error |
| Cycle detected | Tool returns error with chain details |
| Target agent fails/crashes | Session sealed with error status, tool returns error |
| Max turns reached | Session sealed, tool returns partial transcript |
| Tool timeout | Session sealed, tool returns what was collected |

### How This Differs from Sub-Agents

| Aspect | Sub-Agent | Agent-Agent |
|--------|-----------|-------------|
| Identity | Archetype-based, ephemeral | Full Soul, Identity, Existence |
| System prompt | Parent-provided | Target's own |
| Memory | None | Target's own memory |
| Existence | Parent's only | Both agents' |
| Tools | Parent-specified subset | Target's own tools |
| Statefulness | Stateless | Full agent state |

## Migration Plan

1. Add `ConversationRequest` and related types to `BotNexus.Domain`
2. Implement `AgentConversationManager` in Gateway (similar to `DefaultSubAgentManager`)
3. Add `agent_converse` tool
4. Add cycle detection
5. Update Existence queries to verify agent-agent sessions appear in both agents' history

## Test Requirements

- Single-turn conversation: initiator sends, target responds, session sealed
- Multi-turn conversation: multiple back-and-forth exchanges
- Cycle detection: A -> B -> A rejected
- Max depth enforcement
- Access control: unauthorized target rejected
- Target agent unavailable: graceful error
- Session appears in both agents' Existence
- Session metadata includes call chain

## Acceptance Criteria

- [ ] `agent_converse` tool available to agents with configured targets
- [ ] Agent-Agent sessions created with correct SessionType, Participants, and ownership
- [ ] Target agent responds using its own Soul, Identity, and tools
- [ ] Cycle detection prevents recursive call chains
- [ ] Conversations are sealed when complete
- [ ] Both agents can find the conversation in their Existence
