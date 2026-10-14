# Conversation-scoped event emission

Every event the gateway emits that concerns a conversation must **name** that conversation in its
payload. This page records the audit behind issue #3065 and the invariant it established.

## The invariant

> A conversation-scoped event carries a non-empty `ConversationId`, sourced from the resolved
> `ConversationSessionResolution`. A channel that has no conversation yet obtains one from the
> gateway before emitting.

The id is resolved server-side long before emission: `GatewayHost.ResolveConversationSessionAsync`
produces a `ConversationSessionResolution` with a concrete `ConversationId`, which is stamped onto
`session.ConversationId`. Every conversation-scoped emit site reads it from there. Nothing
downstream re-derives it.

### Why "name it" rather than "let the receiver work it out"

An event that arrives without a conversation id is not dropped and does not error. The receiver
attributes it to whatever conversation is currently active, so a stream chunk, tool pill or
steering pill belonging to conversation B renders inside conversation A. The user sees a UI
glitch, not a routing fault, and there is no log line to correlate. Correct attribution has to be
decided by the only party that actually knows the answer — the gateway.

## Emit-site audit

Every gateway-side emit site for a conversation-scoped event, and whether it set a conversation id
before #3065.

| # | Emit site | Event(s) | Set an id before? | Source of the id now |
|---|---|---|---|---|
| 1 | `GatewayHost` streaming `OnEventAsync` stamp | all `AgentStreamEvent` types | Yes | `session.ConversationId` (from the resolution) |
| 2 | `GatewayHost.HandleUserInputRequiredAsync` | `UserInputRequired` | Yes — `conversationId` parameter | `session.ConversationId` |
| 3 | `GatewayHost.SendAskUserToBindingAsync` (observer bindings) | `UserInputRequired` | Yes — via `ChannelStreamTarget` | `session.ConversationId` |
| 4 | `GatewayHost` SignalR observer fan-out | all `AgentStreamEvent` types | Yes — per-observer `ChannelStreamTarget` | `session.ConversationId` |
| 5 | `GatewayHost.HandleSteeringAsync` → `SteeringInjected` | steering feedback | **No** | `session.ConversationId`, passed in explicitly |
| 6 | `GatewayHost.HandleSteeringAsync` → `SteeringQueued` | steering feedback | **No** | `session.ConversationId`, passed in explicitly |
| 7 | `SteeringSignalRBridge.ForwardToSignalRAsync` | `SteeringFeedback` | Partially — fell back to the session id as a group key | `GatewayActivity.ConversationId` (required) |
| 8 | `SignalRChannelAdapter.SendStreamEventAsync` | all `AgentStreamEvent` types | Passthrough | event id, else target id; refuses when neither is set |
| 9 | `SignalRCanvasNotifier` | `CanvasUpdated`, `CanvasStateChanged` | Yes — required parameter | caller |
| 10 | `SignalRTodoNotifier` | `TodoUpdated` | Yes — required parameter | caller |
| 11 | `SignalRConversationChangeNotifier` | `ConversationChanged` | Yes — required parameter | caller |
| 12 | `SubAgentSignalRBridge` | sub-agent lifecycle | Yes — stamped since #682 | caller |

Sites 5, 6 and 7 were the gaps. Sites 1–4 already carried the id; site 8 is the boundary where the
invariant is now enforced.

Agent-level events — registration, agent lifecycle, `AgentsChanged` — are deliberately excluded.
They are not conversation-scoped and correctly have no conversation.

### Site 7 in detail

`SteeringSignalRBridge` used to substitute the **session** id as a conversation group key when the
activity event carried no conversation id:

```csharp
var conversationKey = !string.IsNullOrWhiteSpace(evt.ConversationId)
    ? evt.ConversationId
    : evt.SessionId;                     // removed
```

No client ever joins `conversation:{sessionId}` — connections subscribe by conversation id. The
send therefore *looked* delivered while reaching nobody, and the feedback surfaced in the client's
active conversation instead. Now that sites 5 and 6 stamp the resolved id, the synonym is dead
weight and the bridge refuses (loudly) rather than addressing a group nobody is in.

## Minting when a channel has no conversation

`GatewayHost.EnsureConversationForEmissionAsync` runs immediately after the resolution is applied
to the session, and only when `session.ConversationId` is still the uninitialized sentinel. It
mints through `ConversationFactory.CreateForChannel` with a server-generated id — the same seam the
REST `POST /conversations` endpoint uses — so ids are still minted server-side and clients never
supply one.

It is deliberately **not** a fallback to an "active" or "last used" conversation. A fresh
conversation is an honest answer; somebody else's conversation is the misattribution this
invariant exists to prevent.

## What is not covered here

- **Outbound send resolution.** Channels addressed by `ChannelAddress` with no route legitimately
  rely on `DefaultConversationDispatcher.ResolveExistingConversationAsync` → `ResolveByBindingAsync`.
  That path is unchanged.
- **Portal-side fallbacks.** The `?? agent.ActiveConversationId` fallbacks in the portal's
  `GatewayEventHandler` are now provably unreachable for conversation-scoped events, but their
  removal belongs to the portal route-ownership arc, not here.

## REST binding-removal facts and reconciliation

For developers integrating conversation lifecycle consumers, REST binding removal is a bounded
part of [issue #2088](https://github.com/Sytone/botnexus/issues/2088), not completion of the whole
lifecycle migration. `ConversationsController.RemoveBinding` preserves the client write guard and
uses the store's transactional single-binding delete. A rejected or failed store mutation emits
no success fact. A successful removal keeps the existing `binding_removed` audit entry and emits
`ConversationBindingRemovedEvent` instead of calling the legacy change notifier.

The fact carries the agent and conversation identifiers, an immutable value snapshot of the
removed binding, and an immutable snapshot of the remaining bindings reloaded after commit.
The latter is an observation of durable state after deletion, not an atomic deletion result;
concurrent mutations may already be reflected in it. If the removed binding was absent from the
pre-delete read, or the conversation no longer exists after deletion, the controller cannot
publish a complete removal fact and does not invent one.

Event delivery is best effort, not a durable outbox. Publisher rejection or an exception does
not undo the committed delete or change the successful response; the controller logs a warning.
Consumers must refresh conversation state from the store or conversation API after a missed
notification or reconnect. Do not restore a removed binding from an old client snapshot, and do
not retry a successful delete solely because its notification was missed. The durable store is
the authority; an event is a post-commit notification, not proof of exactly-once delivery.

`ConversationsControllerRemoveBindingEventTests` exercises real in-memory and SQLite stores,
checks durable deletion through a fresh SQLite store, and pins snapshot detachment, post-commit
consumption, publisher failure isolation, and absence of legacy notifications.
