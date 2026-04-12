# SignalR Hub Contract

> This document replaces the retired raw WebSocket protocol.  
> Real-time gateway traffic now uses SignalR at `/hub/gateway`.

## Endpoint

- **Hub URL:** `http://localhost:5005/hub/gateway`
- **Transport:** SignalR negotiation (WebSockets/Server-Sent Events/Long Polling as available)

## Hub Methods (Client → Server)

- `SubscribeAll()`
- `SendMessage(agentId, channelType, content)`
- `Steer(agentId, sessionId, content)`
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
