# Decision Proposal: Gateway Multi-Agent Routing

- **Author:** Bender
- **Date:** 2026-04-01
- **Requested by:** Jon Bullen
- **Status:** Proposed

## Context

Gateway dispatch was hardcoded to `runners[0].RunAsync()`, which prevented true multi-agent behavior and violated the OpenClaw-style multi-agent requirement.

## Decision

Introduce an injectable routing layer (`IAgentRouter`) that resolves one or more `IAgentRunner` targets per inbound message by agent name:

1. Use explicit inbound target metadata first (`agent`, `agent_name`, `agentName`).
2. Support explicit broadcast targets (`all` or `*`) to fan out to all registered runners.
3. If no explicit target is present:
   - Use configured `Gateway.DefaultAgent` when available.
   - If `Gateway.BroadcastWhenAgentUnspecified` is true, broadcast to all.
   - Fall back to all runners when no default runner is resolvable.

`IAgentRunner` now exposes `AgentName` so routing resolves runners deterministically via dictionary lookup.

## Consequences

- Gateway now supports multiple agent runners without first-runner bias.
- WebSocket ingress can provide target agent information directly (`agent`/`agent_name` fields).
- Dispatch logs now clearly identify which agent(s) handled each inbound message for observability and debugging.
