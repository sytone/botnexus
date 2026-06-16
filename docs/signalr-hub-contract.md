# SignalR Hub Contract

> This document replaces the retired raw WebSocket protocol.  
> Real-time gateway traffic now uses SignalR at `/hub/gateway`.

## Endpoint

- **Hub URL:** `http://localhost:5005/hub/gateway`
- **Transport:** SignalR negotiation (WebSockets/Server-Sent Events/Long Polling as available)

## Hub Methods (Client → Server)

- `SubscribeAll()`
- `SendMessage(agentId, channelType, content)`
- `Steer(agentId, sessionId, content, conversationId?)`
- `FollowUp(agentId, sessionId, content)`
- `Abort(agentId, sessionId)`
- `ResetSession(agentId, sessionId)`
- `GetAgents()`
- `GetAgentStatus(agentId, sessionId)`

## Server Events (Server → Client)

- `Connected`
- `SessionReset`
- `MessageStart`
- `ThinkingDelta`
- `ContentDelta`
- `ToolStart`
- `ToolEnd`
- `MessageEnd`
- `Error`

## Notes

- All gateway-originated messages use `channelType = "signalr"`.
- Clients should call `SubscribeAll` after connecting and on reconnect.
- Channel switching is a client-only UI operation. Do not call join/leave methods.
- Session fan-out uses SignalR groups: `session:{sessionId}`.
- `Steer` targets the **session of the conversation being acted on**. Clients must pass the
  displayed conversation's own session id (resolved from that conversation's `activeSessionId`),
  not an agent-global "last session" value — otherwise a steer can land on an unrelated
  conversation's session. `SessionSummary.conversationId` (returned by `SubscribeAll` and
  `GET /api/sessions`) lets clients keep each session bound to the right conversation.
- A steer is only applied while the agent is **running** in the target session (there must be an
  in-flight turn to steer). If the agent is idle, the gateway does **not** queue the message into a
  dormant handle (it would never drain); instead it publishes an `Error` activity so the client can
  surface that the steer wasn't applied. Send a normal message to start a new turn.
