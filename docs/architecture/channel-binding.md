---
owner: shared
author: BotNexus Team
ai-policy: collaborative
---

# Channel Binding

**Last Updated:** 2026-06  
**Status:** Canonical reference for how channels bind to conversations

This page documents how channel adapters identify *where a message came from* and how the gateway turns that identity into a `Conversation` row. It's the answer to "I'm writing a new channel — what address should I emit and where does that address end up?"

If you're tracing a specific message through the gateway, read [gateway-flow.md](./gateway-flow.md) and [development/message-flow.md](../development/message-flow.md) instead. This page is about the *contract* between channels and the router.

---

## The contract

Every inbound message carries three identity values that together determine its conversation:

| Field | Purpose | Authoritative source |
|---|---|---|
| `InboundMessage.ChannelType` | Names the adapter (e.g. `signalr`, `telegram`, `tui`, `internal`). | Adapter constant. |
| `InboundMessage.ChannelAddress` | Channel-native identifier that distinguishes one "place to talk" from another within that channel. | Channel-specific encoding (see below). |
| `InboundMessage.Sender` / `SenderId` | Typed `CitizenId` plus a wire-level token used for audit and allow-list filtering. | The producing adapter. |

The `IConversationRouter` resolves these to a `(Conversation, Session)` pair using the rule:

> **A conversation is uniquely identified by `(AgentId, ChannelType, ChannelAddress)`.**

There is no global "default" conversation per channel. There is no per-user fan-out unless the adapter encodes the user into its `ChannelAddress`. If two messages share `(ChannelType, ChannelAddress)` they will resolve to the same conversation — full stop. If they differ in either, they resolve to different conversations.

This is why **the choice of `ChannelAddress` is the most consequential decision a channel author makes**.

---

## Per-channel conventions

### SignalR (Portal)

| | |
|---|---|
| `ChannelType` | `signalr` |
| `ChannelAddress` | `agentId` (the target agent's id, as a string) |
| Sender mapping | `Sender = CitizenId.Of(UserId.From(ConnectionId))` |
| Binding cardinality | One conversation per agent, shared across all browser tabs/reconnects |

The Portal hub uses the **agent id** as the channel address (see `GatewayHub.cs` — `ChannelAddress = ChannelAddress.From(typedAgentId.Value)` on every inbound). The SignalR `ConnectionId` is used as the `SenderId` for audit but is **not** part of the binding — that's intentional, so that closing and reopening a browser tab resolves to the same conversation.

If a Portal caller wants to target a *specific* non-default conversation, the hub method accepts an explicit `conversationId` parameter and forwards it as `InboundMessageContext.RequestedConversationId`. The router takes the explicit-id path (see "Resolution paths" below) and bypasses binding lookup.

### Telegram

| | |
|---|---|
| `ChannelType` | `telegram` |
| `ChannelAddress` | `<chatId>` or `<chatId>/topic:<threadId>` (encoded via `TelegramChannelAddress.Encode`) |
| Sender mapping | `Sender = CitizenId.Of(UserId.From(from.id.ToString()))` |
| Binding cardinality | One conversation per chat (or per forum topic within a supergroup) |

Telegram's native id space carries both a chat id (signed 64-bit) and an optional `message_thread_id` for forum topics. `TelegramChannelAddress` folds both into the opaque `ChannelAddress` so the router can stay channel-agnostic. Examples:

- DM with user `12345`: `12345`
- Supergroup `-1001234567890` topic `5`: `-1001234567890/topic:5`
- "General" topic (Telegram always assigns thread id `1`): `12345/topic:1`

The slash is illegal inside a chat id, so it's a safe delimiter. Channels with different sub-address shapes (Slack threads, Teams channels) define their own encoding rather than reusing Telegram's.

The reverse decode (`TryDecode`) is best-effort: legacy bindings created through the REST API may carry a non-numeric `thread_id` suffix, which is treated as "no topic" rather than failing the whole decode. This keeps existing rows compatible.

### TUI (terminal)

| | |
|---|---|
| `ChannelType` | `tui` |
| `ChannelAddress` | literal `"console"` |
| Sender mapping | `Sender = CitizenId.Of(UserId.From(Environment.UserName))` |
| Binding cardinality | One conversation per agent (the TUI is single-user by definition) |

The TUI runs against a single local terminal, so every inbound uses the same `ChannelAddress`. The terminal user's OS username is the audit token. There is no thread or sub-address concept.

### Internal (sub-agent wake-ups)

| | |
|---|---|
| `ChannelType` | `internal` |
| `ChannelAddress` | the **target session id** that should receive the wake-up |
| Sender mapping | `Sender = CitizenId.Of(<spawning agent>)` |
| Binding cardinality | n/a — bypasses conversation resolution |

`internal` is the one channel that **does not** participate in the standard binding model. Sub-agent wake-ups and cross-agent routing target an already-known parent session directly. `GatewayHost.ProcessAsync` short-circuits on `ChannelKey.From("internal")` and uses `ChannelAddress` as the session id (`requestedSessionIdValue ?? message.ChannelAddress.Value`). Resolving these through the conversation router would create synthetic "internal" conversations and misroute the user-visible response stream.

Outbound delivery from the internal channel uses `InternalChannelAdapter`, which reads `session.ChannelType` to pick the original channel adapter (with a fallback to `signalr`). This keeps sub-agent results flowing back through the same surface the parent conversation lives on.

---

## Resolution paths

Given the contract above, an inbound message follows one of three resolution paths in `DefaultConversationRouter.ResolveInboundAsync`:

### 1. Explicit `RequestedConversationId`

When the inbound carries `InboundMessageContext.RequestedConversationId`, the router fetches the conversation by id and:

- Reactivates an `Archived` conversation back to `Active`.
- Adds a binding for `(ChannelType, ChannelAddress)` if one doesn't exist (bind-on-first-use), or un-mutes an existing muted binding.
- Resolves or creates the active session.

This is the path used by Portal "open a specific conversation" clicks and by REST POSTs that target a known conversation. **The explicit-id path is the only way to attach a new channel binding to an existing conversation post-creation.**

### 2. Binding lookup

When there's no `RequestedConversationId`, the router calls `IConversationStore.ResolveByBindingAsync(agentId, channelType, channelAddress)`. This is an indexed lookup against the `ChannelBindings` table.

If a match is found, the conversation is reused. If the binding was previously muted (e.g. through `IConversationRouter.MuteBindingAsync` on disconnect), the router does NOT automatically un-mute it — the explicit-id path is required to revive a muted binding.

### 3. Archived-conversation reopen

If binding lookup misses, the router tries `TryReopenArchivedConversationAsync` — looking for an archived conversation that previously held this binding. If found, it's revived to `Active` and reused.

### 4. New conversation

If steps 2 and 3 both miss, the router creates a new `Conversation` with:

- `ConversationId = ConversationId.Create()` (new GUID).
- `Title = "{channelType}:{channelAddress}"` (placeholder; users typically rename via Portal).
- A single `ChannelBinding` for `(channelType, channelAddress)`.
- `Initiator` set to the inbound's `Sender` when valid.

There is no special "default" conversation for addressless channels. An empty `ChannelAddress` is a valid stable identity if your channel has no external id concept — you just get one conversation forever for that channel.

---

## Writing a new channel adapter

Use this checklist:

1. **Pick your `ChannelKey`.** Lowercase, short, stable (`signalr`, `telegram`, `tui`, `discord`, `slack`). Add to `BotNexus.Domain.Primitives.ChannelKey`'s known-values list if there's a constant for it.

2. **Decide your `ChannelAddress` encoding.**
   - If your channel has a single addressable surface (like TUI), use a literal constant.
   - If your channel has a flat per-conversation id (like Telegram chats), use that id verbatim.
   - If your channel has a primary id plus a sub-address (like forum topics, threads, or channels-within-workspaces), define an encoder class that folds them into an opaque string. Pick a delimiter that's illegal in the primary id. See `TelegramChannelAddress` for the reference shape.

3. **Populate every inbound's two-field sender invariant.** Both `InboundMessage.SenderId` (channel-native wire token, used for audit/allow-list filtering) AND `InboundMessage.Sender` (typed `CitizenId`, used by downstream participant tracking) must be set. They may differ — for example sub-agent wake-ups use `SenderId = "subagent:<id>"` for audit while `Sender` carries the typed child agent id. Downstream code never re-parses `SenderId` to infer species; consumers use `Sender.Kind`.

4. **Dispatch through `IInboundMessageOrchestrator.AcceptAsync`**, not `IChannelDispatcher.DispatchAsync` directly. The orchestrator wraps the per-session FIFO queue and bounded backpressure that every channel needs. (`IChannelDispatcher` is still implemented by the orchestrator for backward-compat but new channels should target `IInboundMessageOrchestrator`.)

5. **Don't set `RoutingHints.RequestedSessionId` unless you've already resolved the session through the gateway.** The TUI used to fabricate `RequestedSessionId = SessionId.From("tui-console")` as a pre-P9 escape hatch — this is now removed. The conversation router resolves `(channelType, channelAddress)` to the active conversation and reuses or opens the session through the standard path.

6. **Implement outbound delivery.** `IChannelAdapter.SendAsync(OutboundMessage)` for whole messages; `SendStreamDeltaAsync(ChannelStreamTarget, string delta)` for streaming chunks. Stream targets carry a typed `ChannelStreamTarget(SessionId, ConversationId, ChannelAddress)` so adapters can subscribe by conversation rather than session — this matters across compaction boundaries when the active session id changes but the conversation persists. Implement `IStreamEventChannelAdapter` if your channel can carry structured stream events (tool starts, thinking blocks) rather than just text deltas.

7. **Document your channel's conventions in this page** when you ship it.

---

## Common pitfalls

- **Encoding sub-addresses into `Sender` instead of `ChannelAddress`.** Sender identifies *who* sent the message; ChannelAddress identifies *which conversation it belongs to*. Two users posting in the same group chat have the same `ChannelAddress` (the chat id) but different `Sender` values.
- **Re-using a `ChannelAddress` across `ChannelType`s.** A Telegram chat id `12345` and a SignalR connection token that happens to also be `12345` are different bindings because their `ChannelType` differs. They will never collide. But you should still make addresses self-describing in logs.
- **Hardcoding a fake `RequestedSessionId`.** This bypasses the router entirely. Don't.
- **Mutating `session.ChannelType` or `session.SessionType` from the channel.** Owned by `GatewayHost.ProcessAsync`. Channels just emit inbounds with the right `ChannelType` on the message itself; the gateway propagates it to the session row.
- **Joining a SignalR group by session id.** Past tense — the SignalR adapter now groups by conversation id (#682) so streams survive session compaction. New adapters should follow the same pattern.

---

## See also

- [gateway-flow.md](./gateway-flow.md) — sequence diagrams of the inbound and outbound paths.
- [development/message-flow.md](../development/message-flow.md) — implementation walkthrough of how the SignalR hub feeds the orchestrator.
- [domain-model.md](./domain-model.md) — the World / Citizen / Conversation / Session model that this binding contract serves.
