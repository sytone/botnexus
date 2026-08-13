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
| `SubscribeAgents(agentIds)` | Join the per-agent notification groups for the agents this connection renders, so it receives `ConversationChanged` for those agents and no others (#2541). A **separate verb** from `SubscribeAll` on purpose: the conversation groups `SubscribeAll` joins are derived from *existing* sessions, so they can never cover a conversation that has not been created yet - and `created` is one of the change types the event carries. The agent is the smallest scope that can name a not-yet-existing conversation. Idempotent (rejoining a group is a no-op), so the reconnect and rebuild paths may call it on every dial. Blank entries are ignored. |
| `GetAgents()` → `AgentDescriptor[]` | List the agents registered on this gateway. |
| `SendMessage(agentId, channelType, content, conversationId?)` → `SendMessageResult` | Send a text message to an agent, optionally targeting a specific conversation. |
| `SendMessageWithMedia(agentId, channelType, content, contentParts)` → `SendMessageResult` | Send a message with attached media (`MediaContentPartDto[]`). |
| `SubmitCanvasPrompt(agentId, channelType, content, conversationId)` → `SendMessageResult` | Submit an instruction composed by a canvas (#2449). A **separate verb** from `SendMessage` on purpose: the provenance kind (`MessageKind.CanvasSubmission`) is stamped by the **server** from the transport surface the call arrived on, so it cannot be forged by a caller-supplied field. `conversationId` is **required** — a canvas is attached to one conversation and may target only that conversation. |

### Steering a running agent

| Method | Purpose |
|---|---|
| `Steer(agentId, sessionId, content, conversationId?)` → `SendMessageResult` | Queue a message to be applied at the next turn boundary of the running session. |
| `SteerWithMedia(agentId, sessionId, content, contentParts, conversationId?)` → `SendMessageResult` | Steer overload carrying draft attachments (#2484). |
| `InterruptAndSteer(agentId, sessionId, message)` → `bool` | Abort the in-flight step and steer immediately (the portal **Redirect** control). |
| `InterruptAndSteerWithMedia(agentId, sessionId, message, contentParts)` → `bool` | Redirect overload carrying draft attachments (#2484). |
| `FollowUp(agentId, sessionId, content)` | Queue a message to be delivered after the whole run loop completes. |
| `FollowUpWithMedia(agentId, sessionId, content, contentParts)` | Follow-up overload carrying draft attachments (#2484). The composed message round-trips through the agent's pending-message queue (#2458) when a run is in flight, and is dispatched as content parts when the agent is idle — so attachments survive both branches. |
| `Abort(agentId, sessionId)` | Stop the entire run loop immediately (the portal **Stop** control). |

### Session management

| Method | Purpose |
|---|---|
| `CompactSession(agentId, sessionId)` → `CompactSessionResult` | Summarise the active session to reduce token usage while preserving full history. |
| `ResetSession(agentId, sessionId)` | Seal the current session and start a fresh one (history is retained). |
| `RespondToAskUser(conversationId, requestId, freeFormText, selectedValues, cancelled)` | Submit the user's answer to an outstanding `ask_user` prompt (pairs with the `UserInputRequired` event). |
| `Ping()` → `long` | Lightweight liveness probe: a no-op server round-trip returning the server's current UTC tick count. Clients use a short-timeout `Ping` to verify the transport is actually alive end-to-end rather than a zombie socket (iOS silently recycles background WebSockets, leaving the client reporting `Connected` on a dead socket). |

> **Durable `ask_user` prompts (#1488).** A pending `ask_user` prompt is persisted on the
> conversation row, so a reloaded tab, a newly-opened window, or a mobile client that missed the
> live `UserInputRequired` event can rehydrate it. Clients fetch it with
> `GET /api/agents/{agentId}/conversations/{conversationId}/pending-ask-user`, which returns the
> serialized prompt as JSON, `204 No Content` when nothing is pending, or `404` when the
> conversation is unknown. The portal hydrates this automatically when a conversation is selected
> (mirroring canvas and todo hydration); the durable copy is cleared only when the prompt reaches an
> explicit terminal state -- the user answers, the user cancels, or an optional caller-supplied
> reminder-style limit elapses.

> **Durable resumable checkpoint (#2047).** A pending `ask_user` prompt is a durable, resumable
> checkpoint with **no hard timeout by default**. It survives graceful and unclean gateway restarts,
> page reloads, conversation switches, and newly-opened clients, and stays pending until the user
> answers or cancels. `RespondToAskUser` resolves through the durable checkpoint even when the
> original in-memory waiter, provider stream, and `TaskCompletionSource` no longer exist: on the
> first restart-safe response the gateway atomically claims and clears the persisted checkpoint and
> dispatches a continuation turn to resume the conversation. Resolution is **idempotent** -- duplicate
> responses, stale request ids, and competing clients cannot resume the conversation twice; a stale
> request id (a client that missed a newer prompt) is rejected with a `HubException`, while an
> already-answered or already-cancelled prompt resolves as a silent no-op. The former mandatory
> 300-second default timeout has been removed; `timeout_seconds` on the `ask_user` tool is now an
> optional, non-terminal reminder-style limit rather than an expiry that cancels the prompt.

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
