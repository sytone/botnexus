# Bender Decision Note — Cross-Agent Calling + Steering/Follow-Up

## Context
Gateway needed two runtime capabilities:
1. Cross-agent calls between agents hosted on the same gateway instance.
2. Mid-run steering and queued follow-up controls surfaced through Gateway APIs.

## Decisions

1. **Local-only cross-agent in Phase 2**
   - Implement `DefaultAgentCommunicator.CallCrossAgentAsync` for local calls only.
   - If `targetEndpoint` is provided, throw `NotSupportedException` with explicit remote guidance.
   - Local calls create an isolated cross-session ID:
     - `cross::{sourceAgentId}::{targetAgentId}::{guid}`
   - Dispatch uses existing supervisor lifecycle (`GetOrCreateAsync` + `PromptAsync`) to avoid bypassing runtime controls.

2. **Promote steering/follow-up into gateway handle contract**
   - Extended `IAgentHandle` with:
     - `Task SteerAsync(string message, CancellationToken cancellationToken = default)`
     - `Task FollowUpAsync(string message, CancellationToken cancellationToken = default)`
   - In-process implementation maps directly to AgentCore queues:
     - `Agent.Steer(new UserMessage(message))`
     - `Agent.FollowUp(new UserMessage(message))`

3. **Expose controls on both transport surfaces**
   - **WebSocket:** added client message types:
     - `{ "type": "steer", "content": "..." }`
     - `{ "type": "follow_up", "content": "..." }`
   - **REST:** added endpoints:
     - `POST /api/chat/steer`
     - `POST /api/chat/follow-up`
   - Both paths require an existing active session (`GetInstance` check) before queuing runtime control messages.

## Why
- Keeps cross-agent execution safe and deterministic by reusing session-scoped supervisor orchestration.
- Aligns Gateway with AgentCore queue semantics without adding new execution primitives.
- Provides parity across synchronous API usage (REST) and live sessions (WebSocket).

## Follow-up
- Phase 3 should implement remote cross-agent transport behind `targetEndpoint` (HTTP/gateway-to-gateway path) while preserving the same communicator contract.
