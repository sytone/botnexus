# BotNexus Gateway Test Specification

## Scope

This specification defines starter coverage for the Gateway service while architecture and interfaces are being finalized.

## Test Categories

### Unit Tests
- Gateway orchestration services: agent lifecycle, session state transitions, adapter routing, isolation strategy selection.
- Stateless validation logic: request validators, route/key parsing, protocol framing and message schema checks.
- Error shaping: domain exceptions mapped to API/websocket responses.

### Integration Tests
- In-process gateway host with fake adapters/providers/isolation backends.
- REST + WebSocket interaction across the same session.
- Session persistence/reconnect behavior against real session store implementation.
- Multi-agent registration and cross-agent invocation contracts.

### End-to-End Tests
- Full startup with configured adapters and isolation strategy plugin selected by config.
- User message from a channel adapter through orchestration to streamed response.
- Reconnection and resume for interrupted sessions with preserved history.

## Requirement Scenarios

### 1) Agent Management
- Register a new agent with valid configuration.
- Reject duplicate agent ID registration.
- Update mutable agent settings without restarting unrelated agents.
- Cross-agent call dispatch routes to the intended target agent.
- Sub-agent invocation propagates parent correlation/session IDs.
- Agent shutdown disposes resources and rejects new work.
- Concurrent agent start/stop operations remain consistent.

### 2) Isolation Strategies
- Strategy resolver selects local, sandbox, container, or remote by configuration.
- Unknown strategy identifier returns validation failure.
- Fallback behavior when configured strategy is unavailable.
- Isolation startup timeout handling and error propagation.
- Resource cleanup for failed initialization.
- Capability matrix checks (feature unsupported by strategy returns clear error).

### 3) Channel Adapters
- Adapter registration exposes enabled channels (TUI, Telegram, WebUI).
- Incoming message normalization to canonical gateway request.
- Outgoing message formatting per adapter contract.
- Adapter auth/identity metadata propagation into session context.
- Adapter failure isolation (one channel error does not break others).
- Hot reconnect/retry behavior for transient adapter transport failures.

### 4) Session Management
- Session create/load/update lifecycle with persisted history.
- Reconnect to existing session restores state and pending stream context.
- Session expiry and cleanup policies enforced.
- Concurrent writes to same session resolve deterministically.
- History truncation/summarization boundary behaviors.
- Resume after gateway restart preserves message ordering.

### 5) API Surface
- REST endpoints: CRUD for agents/sessions/adapters and health/status probes.
- REST request validation and standardized problem details responses.
- WebSocket handshake validation and protocol version negotiation.
- Bidirectional streaming messages with correlation and sequencing.
- WebSocket error events and close codes for known failure classes.
- REST + WebSocket consistency for session and agent state.

## Edge Cases

- Duplicate IDs, empty IDs, excessively long IDs, invalid characters.
- Large payloads, high-frequency message bursts, out-of-order events.
- Reconnect during in-flight stream and late-arriving events.
- Partial failures in multi-agent call chains.
- Adapter transport disconnect mid-stream.
- Isolation backend unavailable at startup vs. runtime interruption.
- Clock skew impacts on session expiry and ordering metadata.
- Cancellation tokens triggered during each lifecycle phase.

## Mock/Stub Strategy

### Unit Layer
- Mock interfaces for adapter transports, isolation runners, session store, and agent executors using Moq.
- Deterministic clocks/IDs via injectable providers.
- Contract test doubles for protocol messages and failure injection.

### Integration Layer
- In-memory host with TestServer/WebApplicationFactory.
- Fake channel adapter implementations for TUI/Telegram/WebUI semantics.
- Fake isolation strategy providers simulating success, timeout, and crash.
- Real serialization stack and middleware pipeline.

### Real Integrations Required
- WebSocket protocol framing/streaming behavior against real ASP.NET Core WebSocket server.
- Session persistence semantics with real configured store implementation.
- REST surface route/middleware behavior including validation and auth pipeline (once auth lands).
- Isolation plugin wiring and strategy resolution from real configuration binding.

## Out of Scope for Initial Stub Phase

- Provider-specific LLM behavior correctness.
- External Telegram network integration.
- Container runtime specifics beyond gateway strategy selection contract.
