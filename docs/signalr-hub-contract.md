# SignalR Hub Contract

> This document replaces the retired raw WebSocket protocol.  
> Real-time gateway traffic now uses SignalR at `/hub/gateway`.

## Endpoint

- **Hub URL:** `http://localhost:5005/hub/gateway`
- **Transport:** SignalR negotiation (WebSockets/Server-Sent Events/Long Polling as available)

### Connection query parameters

Two optional query parameters are read from the connection URL at connect time and stay in
effect for the lifetime of the connection:

| Parameter | Values | Default | Purpose |
|---|---|---|---|
| `client` | `mobile`, `desktop` (any string; normalized to lowercase, trimmed) | `desktop` | Distinguishes the client surface (#1737). The gateway stamps the resolved value onto `InboundMessage.Metadata["clientKind"]` for every message this connection sends, so agents and the dispatch pipeline can tell a mobile client from a desktop one. A blank or absent value normalizes to `desktop`, so existing desktop clients that send nothing keep working. |
| `clientVersion` | any string | `unknown` | Client build/version hint, recorded in the connect-time log line for diagnostics. |

Append them to the hub URL, e.g. `…/hub/gateway?client=mobile&clientVersion=1.4.2`. The mobile
portal sets `client=mobile`; the desktop portal sends no `client` value and is treated as
`desktop`. Both values are attacker-controlled and are sanitized (CR/LF and control characters
stripped) before being logged.

## Hub Methods (Client → Server)

Methods invoked by the client on the hub. Return values are shown where the method
responds directly (most stream their results back as server events instead).

### Discovery & messaging

| Method | Purpose |
|---|---|
| `SubscribeAll()` → `SubscribeAllResult` | Subscribe to all agent/session groups. Call after connecting and on every reconnect. |
| `GetAgents()` → `AgentDescriptor[]` | List the agents registered on this gateway. |
| `SendMessage(agentId, channelType, content, conversationId?)` → `SendMessageResult` | Send a text message to an agent, optionally targeting a specific conversation. |
| `SendMessageWithMedia(agentId, channelType, content, contentParts)` → `SendMessageResult` | Send a message with attached media (`MediaContentPartDto[]`). |

### Steering a running agent

| Method | Purpose |
|---|---|
| `Steer(agentId, sessionId, content, conversationId?)` → `SendMessageResult` | Queue a message to be applied at the next turn boundary of the running session. |
| `InterruptAndSteer(agentId, sessionId, message)` → `bool` | Abort the in-flight step and steer immediately (the portal **Redirect** control). |
| `FollowUp(agentId, sessionId, content)` | Queue a message to be delivered after the whole run loop completes. |
| `Abort(agentId, sessionId)` | Stop the entire run loop immediately (the portal **Stop** control). |

### Session management

| Method | Purpose |
|---|---|
| `CompactSession(agentId, sessionId)` → `CompactSessionResult` | Summarise the active session to reduce token usage while preserving full history. |
| `ResetSession(agentId, sessionId)` | Seal the current session and start a fresh one (history is retained). |
| `RespondToAskUser(conversationId, requestId, freeFormText, selectedValues, cancelled)` | Submit the user's answer to an outstanding `ask_user` prompt (pairs with the `UserInputRequired` event). |

> **Durable `ask_user` prompts (#1488).** A pending `ask_user` prompt is persisted on the
> conversation row, so a reloaded tab, a newly-opened window, or a mobile client that missed the
> live `UserInputRequired` event can rehydrate it. Clients fetch it with
> `GET /api/agents/{agentId}/conversations/{conversationId}/pending-ask-user`, which returns the
> serialized prompt as JSON, `204 No Content` when nothing is pending, or `404` when the
> conversation is unknown. The portal hydrates this automatically when a conversation is selected
> (mirroring canvas and todo hydration); the durable copy is cleared once the prompt is answered,
> times out, or is cancelled.

> `OnConnectedAsync` / `OnDisconnectedAsync` are SignalR lifecycle hooks, not client-callable methods.

## Server Events (Server → Client)

Events the server pushes to subscribed clients. Defined by the typed
`IGatewayHubClient` contract — every method maps to a client-side handler.

### Connection lifecycle

| Event | Meaning |
|---|---|
| `Connected(payload)` | Sent once the connection is established and subscribed. |
| `SessionReset(payload)` | A session was reset; the client should clear its session context. |
| `AgentsChanged(payload)` | The set of registered agents changed. |
| `ConversationChanged(payload)` | A conversation's metadata (title, bindings, archive state) changed. |

### Run & turn brackets

| Event | Meaning |
|---|---|
| `RunStarted(evt)` | The agent run loop started. Brackets the whole loop with `RunEnded` so clients have an authoritative "agent busy" signal that stays asserted across the gaps between turns and tools. |
| `RunEnded(evt)` | The run loop has fully settled — final turn, last tool result, and any follow-up continuations are all done. Treat the agent as idle only after this fires. |
| `TurnEnd(evt)` | A single agent turn completed (all tool calls for that turn are done). |
| `TurnInterrupted(evt)` | A gateway restart interrupted an active turn. |

### Streaming

| Event | Meaning |
|---|---|
| `MessageStart(evt)` | The agent began producing a message. |
| `ThinkingDelta(evt)` | A chunk of reasoning/thinking output (when the provider streams it). |
| `ContentDelta(evt)` | A chunk of assistant message content. The payload carries an optional `role` field (see note below). |
| `ToolStart(evt)` | A tool call began. |
| `ToolEnd(evt)` | A tool call finished. |
| `MessageEnd(evt)` | The agent finished producing a message. |
| `Error(evt)` | An error occurred (also used to surface a steer that could not be applied). |
| `UserInputRequired(evt)` | The agent called `ask_user`; the client must collect input and reply with `RespondToAskUser`. |

### Sub-agents

| Event | Meaning |
|---|---|
| `SubAgentSpawned(payload)` | A sub-agent was spawned. |
| `SubAgentCompleted(payload)` | A sub-agent finished successfully. |
| `SubAgentFailed(payload)` | A sub-agent failed. |
| `SubAgentKilled(payload)` | A sub-agent was killed. |

### Canvas & todo

| Event | Meaning |
|---|---|
| `CanvasUpdated(agentId, conversationId, html)` | The conversation's canvas HTML was replaced — refresh the Canvas panel live. |
| `CanvasStateChanged(conversationId, key, value)` | A single canvas state key was set or cleared. |
| `TodoUpdated(agentId, conversationId, todoJson)` | The conversation's per-conversation todo state changed (raw `TodoJson`, or null/empty when cleared) — refresh the Todo panel live. |
| `SteeringFeedback(payload)` | Acknowledges how a steer/follow-up was queued or applied. |

## Notes

- All gateway-originated messages use `channelType = "signalr"`.
- The `ContentDelta` payload carries an optional `role` field. It is `null` for ordinary
  streamed/relayed content — the client then renders the assistant bubble, matching every
  pre-existing payload — and is only set when an agent-post must render under a specific role
  (e.g. an on-behalf-of-user kickoff stamped `user`). The field is trailing-optional, so older
  clients and existing wire messages deserialize unchanged.
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
