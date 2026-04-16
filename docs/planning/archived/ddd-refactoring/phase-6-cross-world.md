---
status: deferred
depends-on: Phase 4 (World), Phase 5 (Agent-to-Agent)
created: 2026-04-12
---

# Phase 6: Cross-World Federation

## Summary

Enable agents on different BotNexus Gateways to communicate with each other. Each Gateway is a World; cross-world communication uses a dedicated channel adapter to bridge them while maintaining session sovereignty - each World owns its own sessions.

## Why Deferred

Already marked as future in the original spec. Foundational work (World as domain object, Agent-to-Agent conversations) must land first.

## Detailed Design

### Architecture Overview

```
+-------------------+                    +-------------------+
|    World A        |                    |    World B        |
|   (Gateway A)     |                    |   (Gateway B)     |
|                   |                    |                   |
| [Agent Nova]      |   Cross-World      | [Agent Leela]     |
|   Session A  <----+--- Channel  -------+-->  Session B     |
|   (owned)         |                    |   (owned)         |
+-------------------+                    +-------------------+
```

### Cross-World Channel Adapter

A real `IChannelAdapter` implementation (unlike cron/soul which are internal triggers). This is genuine external communication between two separate processes.

```csharp
public class CrossWorldChannelAdapter : IChannelAdapter
{
    public ChannelKey ChannelType => ChannelKey.From("cross-world");
    public bool SupportsStreaming => false;  // Request/response only for v1
    public bool SupportsSteering => false;
    public bool SupportsFollowUp => false;
    // ...
}
```

### Two Sessions Per Conversation

Unlike local agent-to-agent (one shared session), cross-world creates two sessions:

**Session A** (on World A, owned by initiating agent):
- SessionType: `SessionType.AgentAgent`
- AgentId: initiating agent
- Participants: both agents (target has WorldId set)
- Contains: initiator's messages as "user", target's responses as "assistant"
- ChannelType: `ChannelKey.From("cross-world")`

**Session B** (on World B, owned by target agent):
- SessionType: `SessionType.AgentAgent`
- AgentId: target agent
- Participants: both agents (initiator has WorldId set)
- Contains: initiator's messages as "user", target's responses as "assistant"
- ChannelType: `ChannelKey.From("cross-world")`

Both sessions contain the full conversation but from their respective side. Ordering may differ slightly due to request/response timing.

### Communication Protocol

**v1: HTTP Request/Response** (simple, stateless)

World A's agent calls `agent_converse` with a target that resolves to another World:

1. Agent Nova on World A calls `agent_converse(agentId="worldb:leela", message="...")`
2. Gateway A creates Session A locally
3. Gateway A sends HTTP POST to Gateway B's cross-world endpoint:
   ```json
   {
     "sourceWorldId": "world-a",
     "sourceAgentId": "nova",
     "targetAgentId": "leela",
     "message": "...",
     "conversationId": "nova::cross::leela::abc123"
   }
   ```
4. Gateway B authenticates the request, creates Session B
5. Gateway B routes the message to Agent Leela
6. Agent Leela processes and responds
7. Gateway B returns the response to Gateway A
8. Gateway A records the response in Session A, delivers to Agent Nova
9. Repeat for multi-turn, or seal both sessions when done

### Gateway-to-Gateway Authentication

**Mutual TLS or shared secret** for v1:

```json
{
  "crossWorld": {
    "peers": {
      "world-b": {
        "endpoint": "https://gateway-b.internal:5001",
        "authType": "shared-secret",
        "secret": "${CROSS_WORLD_SECRET_B}"
      }
    },
    "inbound": {
      "enabled": true,
      "allowedWorlds": ["world-b"]
    }
  }
}
```

### Agent Discovery

How does Agent A know Agent B exists on another World?

**Option 1: Explicit configuration** (v1)
```json
{
  "crossWorldAgents": {
    "worldb:leela": {
      "worldId": "world-b",
      "agentId": "leela",
      "description": "Research specialist"
    }
  }
}
```

**Option 2: Discovery protocol** (future)
Gateways expose a discovery endpoint listing their available agents. Peers periodically sync.

Recommendation: Option 1 for v1. Keep it simple and explicit.

### Agent ID Convention for Cross-World

Cross-world agent references use `worldId:agentId` format. The `AgentId` value object should support parsing this:
- `AgentId.From("leela")` - local agent
- `AgentId.From("world-b:leela")` - cross-world agent

### Existence

Both agents have their respective sessions in their Existence:
- Agent Nova's Existence includes Session A (owned)
- Agent Leela's Existence includes Session B (owned)

Both sessions contain the full conversation content, so each agent has complete context.

### Error Handling

| Scenario | Behavior |
|----------|----------|
| Target world unreachable | Retry with backoff, then fail with error |
| Target world rejects auth | Fail with permission error |
| Target agent not found on remote world | Fail with not-found error |
| Network timeout mid-conversation | Seal both sessions with error, return partial |
| Target world goes down mid-conversation | Same as timeout |

## Migration Plan

1. Add cross-world config schema
2. Implement `CrossWorldChannelAdapter`
3. Add cross-world HTTP endpoints to Gateway API
4. Add gateway-to-gateway auth
5. Extend `agent_converse` tool to handle cross-world targets
6. Test with two local gateways

## Test Requirements

- Two-gateway setup with cross-world communication
- Authentication between gateways
- Session created on both sides
- Both agents see conversation in Existence
- Error handling for all failure modes
- Multi-turn cross-world conversation

## Acceptance Criteria

- [ ] Cross-world channel adapter exists and handles message exchange
- [ ] Two sessions created per cross-world conversation (one per World)
- [ ] Gateway-to-gateway authentication works
- [ ] Both agents retain the conversation in their Existence
- [ ] Explicit agent discovery via configuration
- [ ] Error handling for network failures, auth failures, and agent not found
