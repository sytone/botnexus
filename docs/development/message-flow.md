# Message Routing and Session Flow

This guide follows an inbound message from the client to an agent and back. The
transport is channel-based, but a **conversation** is the durable routing identity;
a **session** is an execution/history segment within that conversation. Compaction
can replace the active session without changing the conversation's subscription.

For the complete wire surface and authorization rules, see the
[SignalR API reference](../api/signalr.md) and
[hub protocol contract](../signalr-hub-contract.md). The source links below identify
the implementations behind this walkthrough, rather than proposed behavior.

## Overview

```text
SignalR client -> GatewayHub -> GatewayHubApplicationService
    -> conversation/session resolution and conversation-group subscription
    -> IInboundMessageOrchestrator.AcceptAsync
    -> GatewayHost.ProcessAsync -> IMessageRouter -> conversation/session resolution
    -> IAgentSupervisor -> IAgentHandle -> agent loop
    -> GatewayHost stream callback -> SignalRChannelAdapter -> conversation group

Other channel adapters -> IChannelDispatcher.DispatchAsync
    -> shared inbound orchestrator -> GatewayHost.ProcessAsync -> same execution path
```

The hub resolves identifiers before dispatch so it can subscribe the caller before
streaming starts. That is distinct from processing the message or completing an
agent turn. The shared orchestrator owns inbound queuing and delivery decisions;
the hub is not a second agent executor.

Sources: [GatewayHubApplicationService.cs](https://github.com/Sytone/botnexus/blob/main/src/extensions/BotNexus.Extensions.Channels.SignalR/GatewayHubApplicationService.cs),
[DefaultInboundMessageOrchestrator.cs](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway.Dispatching/DefaultInboundMessageOrchestrator.cs),
[GatewayHost.cs](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/GatewayHost.cs).

## Message Routing Flow

### 1. Client Connection (WebUI Example)

1. Connect to the SignalR hub at **`/hub/gateway`**, mapped by
   `SignalREndpointContributor.MapEndpoints`.
2. Call `SubscribeAll()`. It obtains the available session summaries through the
   hub application service and returns `SubscribeAllResult` with a `sessions` list.
3. For each returned session, the hub joins the connection to the real
   `conversation:{conversationId}` group when its conversation ID is present,
   **and** to the compatibility alias `conversation:{sessionId}`. Duplicate group
   keys are joined once.
4. For conversation-list lifecycle notifications, separately call
   `SubscribeAgents(IReadOnlyList<string> agentIds)`. This joins `agent:{agentId}`
   groups for `ConversationChanged`, including creation of conversations that did
   not exist when `SubscribeAll()` ran.

`SubscribeAll()` is a snapshot subscription, not a promise that every stored
session or every future conversation is subscribed. Its summaries are selected by
`SessionWarmupService`; see [visibility and filtering](#session-visibility-and-filtering).
A new session **within an already subscribed conversation** continues to use the
same conversation group.

Sources: [SignalREndpointContributor.cs](https://github.com/Sytone/botnexus/blob/main/src/extensions/BotNexus.Extensions.Channels.SignalR/SignalREndpointContributor.cs),
[GatewayHub.cs](https://github.com/Sytone/botnexus/blob/main/src/extensions/BotNexus.Extensions.Channels.SignalR/GatewayHub.cs)
(`SubscribeAll`, `SubscribeAgents`).

### 2. Sending a Message

The current server signature is:

```csharp
public Task<SendMessageResult> SendMessage(
    AgentId agentId,
    ChannelKey channelType,
    string content,
    string? conversationId = null)
```

The last parameter is **optional** in the server contract. Omitting it is not, by
itself, evidence of a runtime failure. An explicit conversation ID selects the
intended conversation; `null` requests binding-based resolution. The example below
passes all four arguments so that choice is visible. `AgentId` and `ChannelKey`
are typed server values serialized as strings on the wire.

```javascript
// Assumes the SignalR JavaScript client is loaded and content is nonblank.
const connection = new signalR.HubConnectionBuilder()
  .withUrl('/hub/gateway') // Supply authentication as required by the host.
  .withAutomaticReconnect()
  .build();

async function subscribe() {
  await connection.invoke('SubscribeAll');
  await connection.invoke('SubscribeAgents', ['my-agent']);
}
connection.onreconnected(subscribe);
await connection.start();
await subscribe();

// Use an existing conversation ID to target it, or null for binding resolution.
const conversationId = null;
const result = await connection.invoke(
  'SendMessage', 'my-agent', 'signalr', content, conversationId);
// result contains sessionId, agentId and channelType; it is not the final reply.
```

**Current send path:**

1. `SendMessage` checks control scope, normalizes the agent/channel values, and
   rejects blank content. Blank conversation IDs are normalized to `null`.
2. `ResolveOrCreateSessionAsync` delegates through
   `IGatewayHubApplicationService.ResolveSessionAsync` to
   `IConversationDispatcher.DispatchAsync` to obtain the session/conversation pair.
   The hub's binding identity is always **`signalr` plus the agent ID as channel
   address**. The caller's `channelType` is a preference, not permission to create
   a binding for a different transport.
3. `SubscribeConversationInternalAsync` joins the caller to
   `conversation:{conversationId}` before dispatch.
4. `SafeDispatchAsync` starts the application's `AcceptAsync` path without awaiting
   turn completion. Dispatch exceptions are logged and reported through a
   best-effort activity error. The synchronous result identifies the session,
   agent and channel; it does not certify a completed agent response.

The conversation router can materialize and save a session while resolving a
binding. Session status/channel/type stamping and caller-participant registration
belong to `GatewayHost.ProcessAsync`, not the hub. A reused expired session can
therefore remain expired until the worker processes it. Do not infer completed
state mutation from the hub return alone.

Sources: `GatewayHub.SendMessageCore`, `ResolveOrCreateSessionAsync`,
`BuildInboundMessage`, `SafeDispatchAsync`, and
[HubContracts.cs](https://github.com/Sytone/botnexus/blob/main/src/extensions/BotNexus.Extensions.Channels.SignalR/HubContracts.cs).

### 3. Message Dispatch and Identity

Channel adapters construct `InboundMessage` and submit it through
`IChannelDispatcher`; the SignalR hub uses the application service's orchestrator
entry point. The current hub envelope uses typed routing hints, not the retired
`TargetAgentId` and `SessionId` top-level example fields.

```csharp
// Relevant fields from the hub path; local variables represent resolved values.
var inbound = new InboundMessage
{
    ChannelType = ChannelKey.From("signalr"),
    SenderId = connectionId,
    Sender = CitizenId.Of(UserId.From(authenticatedUserId)),
    ChannelAddress = ChannelAddress.From(agentId.Value),
    RoutingHints = new InboundMessageRoutingHints(
        RequestedAgentId: agentId,
        RequestedSessionId: sessionId,
        RequestedConversationId: conversationId), // ConversationId? here
    Content = content,
    Metadata = new Dictionary<string, object?>
    {
        ["messageType"] = "message",
        ["clientKind"] = clientKind
    }
};
```

**`SenderId` versus `Sender`:** the first is the channel-native audit/allow-list
and fan-out token; the second is the typed `CitizenId` used for domain identity.
For SignalR, `SenderId` is the connection ID, while `GetAuthenticatedUserId()` uses
`Context.UserIdentifier` and falls back to the connection ID when absent. They
need not have the same value. Agent-originated deliveries use an agent citizen;
downstream code should inspect `Sender.Kind`, not re-parse an audit string.

Conversation creation records initiator provenance separately from current
participants. Reusing a conversation does not mean replacing its original
initiator. The conversation router's creation path uses `ConversationFactory`.

The orchestrator uses `InboundIsolationKey`: requested conversation first,
requested session second, otherwise channel type/address. Queued messages sharing
one key are FIFO-serialized; different keys may run concurrently. Configured
running-turn delivery can steer instead of enqueueing. These queue keys are not
SignalR subscription groups, even when they share a prefix.

Sources: `GatewayHub.BuildInboundMessage`,
[InboundIsolationKey.cs](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway.Dispatching/InboundIsolationKey.cs),
[DefaultConversationRouter.cs](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway.Conversations/DefaultConversationRouter.cs).

### 4. Agent and Conversation Resolution

`GatewayHost.ProcessAsync` asks `IMessageRouter.ResolveAsync` for target agents.
`DefaultMessageRouter` applies this priority:

1. `RoutingHints.RequestedAgentId`: use the registered agent, or return no route
   if the explicit target is unknown (do not silently fall back).
2. `RoutingHints.RequestedSessionId`: use the session's agent if registered.
3. `GatewayOptions.DefaultAgentId`: use it if configured and registered.
4. Otherwise return an empty target list.

The host then resolves the conversation/session for each target. In
`DefaultConversationRouter.ResolveInboundAsync`, an existing explicit conversation
bypasses binding lookup; a missing explicit ID falls through to binding lookup.
The binding path reuses an existing conversation, can reopen an archived one, or
creates one. An archived conversation is reopened with its active-session pointer
cleared. Session selection reuses a non-sealed active segment unless there is a
cross-channel conflict; otherwise it creates a replacement segment.

Conversation ownership is durable on `Conversation.AgentId`;
`GatewaySession.AgentId` is hydrated from it. Do not treat the latter as a mutable,
independent owner binding.

Sources: [DefaultMessageRouter.cs](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Routing/DefaultMessageRouter.cs),
`GatewayHost.ResolveConversationSessionAsync`,
`DefaultConversationRouter.ResolveOrCreateSessionAsync`.

### 5. Agent Execution

`IAgentSupervisor.GetOrCreateAsync(agentId, sessionId)` obtains an execution handle
for the resolved pair. The supervisor owns instance lifecycle and delegates
creation to the selected isolation strategy. Reuse, configured concurrency, and
shutdown belong at this layer, rather than in the SignalR hub.

The in-process strategy builds context and the workspace/tool environment, creates
an agent, and wraps it in a handle. It runs inside the gateway process, not across
a security boundary. Isolation is pluggable, but the earlier descriptions of
[container](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Isolation/ContainerIsolationStrategy.cs),
[remote](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Isolation/RemoteIsolationStrategy.cs),
and [sandbox](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Isolation/SandboxIsolationStrategy.cs)
execution describe planned backends: those strategies' `CreateAsync` methods
currently throw `NotSupportedException`.

Sources: [DefaultAgentSupervisor.cs](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Agents/DefaultAgentSupervisor.cs),
[InProcessIsolationStrategy.cs](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Isolation/InProcessIsolationStrategy.cs)
(including its in-process handle).

### 6. Agent Prompt and Tool Execution

The in-process handle's `PromptAsync` returns an `AgentResponse`; its `StreamAsync`
path yields `AgentStreamEvent` values. Both use the underlying agent timeline and
loop. The loop calls the configured model, processes tool requests and their
results, and can continue across multiple model turns. A provider's literal stop
reason is not the portable gateway completion contract.

Tool invocation uses before/after hooks and configured tool execution behavior.
The gateway policy contract is **`IToolPolicyProvider`**, declared in
[ToolPolicy.cs](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway.Contracts/Security/ToolPolicy.cs).
It supplies risk classification, approval requirements/fallback, HTTP exclusions,
and tool availability. It is not a generic path-validation interface named
`IToolPolicy`.

[ToolPolicyHookHandler.cs](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Hooks/ToolPolicyHookHandler.cs)
uses the production `DefaultToolPolicyProvider` before execution to deny blocked
tools and apply the configured approval fallback. Do not assume that an
approval-required policy automatically opens an interactive approval prompt at
this hook seam.

### 7. Response Streaming

```text
Agent handle/loop -> AgentStreamEvent -> GatewayHost stream callback
    -> IStreamEventChannelAdapter.SendStreamEventAsync(target, streamEvent, ct)
    -> SignalR conversation group -> subscribed clients
```

The host stamps agent, session and conversation IDs on events and constructs a
`ChannelStreamTarget` carrying the conversation, session, channel address and
binding/request identifiers. SignalR's current streaming methods take that typed
target, **not a bare conversation ID string**:

```csharp
Task SendStreamDeltaAsync(
    ChannelStreamTarget target, string delta,
    CancellationToken cancellationToken = default);

Task SendStreamEventAsync(
    ChannelStreamTarget target, AgentStreamEvent streamEvent,
    CancellationToken cancellationToken = default);
```

`SignalRChannelAdapter.SendStreamDeltaAsync` addresses
`conversation:{target.ConversationId}`. Structured events prefer the IDs stamped
on the event over the target's IDs, so observer delivery retains the originating
conversation/session. An uninitialized conversation ID is logged and the event is
not emitted.

| Client event | Meaning in the stream |
|---|---|
| `RunStarted`, `RunEnded` | Bracket the whole run; `RunEnded` is the authoritative idle signal. |
| `MessageStart`, `MessageEnd` | Begin/end an assistant message, not necessarily the whole run. |
| `ThinkingDelta`, `ContentDelta` | Incremental reasoning/content. |
| `ToolStart`, `ToolEnd` | Tool invocation and completion metadata. |
| `UserInputRequired` | A user-input checkpoint; the host has dedicated prompt handling. |
| `TurnInterrupted`, `TurnEnd` | Interruption and turn completion, including tool-only turns. |
| `Error` | Stream error information. |

`SendAsync(OutboundMessage)` is the non-streaming adapter path. It emits
`ContentDelta`, preferring `OutboundMessage.ConversationId`; legacy callers fall
back to **`conversation:{sessionId}`**, not a session-prefixed group. It also
suppresses the exact trimmed `NO_REPLY` sentinel.

Sources: `GatewayHost`'s `OnEventAsync` callback,
[SignalRChannelAdapter.cs](https://github.com/Sytone/botnexus/blob/main/src/extensions/BotNexus.Extensions.Channels.SignalR/SignalRChannelAdapter.cs),
[IChannelAdapter.cs](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway.Contracts/Channels/IChannelAdapter.cs)
(including `IStreamEventChannelAdapter`).

### 8. Conversation Group Broadcast

| Group key | Current use |
|---|---|
| `conversation:{conversationId}` | Production conversation stream; stable across session compaction. |
| `conversation:{sessionId}` | Compatibility alias joined by `SubscribeAll`, used by session-only outbound callers. |
| `agent:{agentId}` | Separate agent-scoped conversation lifecycle notifications via `SubscribeAgents`. |

`GetSessionGroup` still exists in the adapter as a legacy helper returning
`session:{sessionId}`. Its presence is **not** evidence that current subscription
or stream delivery uses it: the paths above call `GetConversationGroup`.

Clients route payloads using the emitted conversation/session IDs and render their
selected view. A group subscription is delivery fan-out, not a concurrency lock
or proof of participant authorization. Keep rendering and reconnect behavior
aligned with the [hub protocol contract](../signalr-hub-contract.md).

## Session Lifecycle

### Session States

The [SessionStatus enum](https://github.com/Sytone/botnexus/blob/main/src/domain/BotNexus.Domain/Gateway/Models/SessionStatus.cs)
contains `Active`, `Suspended`, `Expired`, and `Sealed`. These are not a single
mandatory `Active -> Suspended -> Sealed` sequence.

- **Active:** available for message processing.
- **Suspended:** paused; `GatewayHost.ProcessAsync` rejects non-active states other
  than its explicit reactivation cases.
- **Expired:** may be reused by conversation resolution and reactivated by the worker.
- **Sealed:** normally replaced during conversation-router session selection.
  However, `GatewayHost.ProcessAsync` also has a reactivation branch for a sealed
  session reaching it; describing sealing as universally terminal/read-only would
  overstate the implementation.

The worker clears `ExpiresAt` when reactivating expired or sealed sessions. The
session's computed `IsInteractive` marker and the warmup selection rules are
separate from its lifecycle status.

### Session Types

The canonical [SessionType values](https://github.com/Sytone/botnexus/blob/main/src/domain/BotNexus.Domain/Primitives/SessionType.cs)
are `UserAgent`, `AgentSelf`, `AgentSubAgent`, and `AgentAgent`. Legacy persisted
`soul` and `heartbeat` values map to `AgentSelf`; `cron` maps to `UserAgent`.
Trigger provenance lives on `SessionEntry.Trigger`, not in separate canonical
`Soul`/`Cron` session types. A cron-channel session can therefore be `UserAgent`
while remaining excluded from the warmup roster.

### Session Creation and Participants

Sending a message can resolve or create a conversation and its active session;
it is not simply a lookup of the first active agent/channel session. Conversations
can also exist before their first message. Compaction and reset can move the
conversation to another session while retaining its conversation identity.

Participants now live on the **conversation**, not on `GatewaySession`.
`GatewayHost.EnsureCallerParticipantAsync` calls
`IConversationStore.AddParticipantsAsync` with the inbound typed citizen after
session persistence. The current participant shape is:

```csharp
public sealed record SessionParticipant
{
    public required CitizenId CitizenId { get; init; }
    public string? Role { get; init; }
}
```

For example, a human participant uses `CitizenId.Of(UserId.From(userId))`; a peer
agent uses `CitizenId.Of(AgentId.From(agentId))`. The old `Type`/`Id` participant
shape survives in serialization compatibility handling, not as the current
record's writable properties.

Sources: [SessionParticipant.cs](https://github.com/Sytone/botnexus/blob/main/src/domain/BotNexus.Domain/Primitives/SessionParticipant.cs),
[GatewaySession.cs](https://github.com/Sytone/botnexus/blob/main/src/domain/BotNexus.Domain/Gateway/Models/GatewaySession.cs),
`GatewayHost.EnsureCallerParticipantAsync`.

### Session Persistence

`ISessionStore` persists session segments and provides history and summary query
operations. `GatewaySession` exposes typed `SessionId`/`ConversationId`, hydrated
`AgentId`, status/type/channel, timestamps, history and metadata; runtime state is
kept separately from the persisted domain session. Its former `Participants`
facade is removed. Use the linked source for the complete model instead of copying
a reduced class definition as if it were the persistence contract.

### Session Visibility and Filtering

`SubscribeAll()` delegates to `SessionWarmupService.GetAvailableSessionsAsync`.
The current implementation:

- Returns no sessions when warmup is disabled.
- Queries transcript-free summaries inside the configured retention window for
  registered agents.
- Selects `UserAgent` summaries, excluding the `cron` channel; it does not include
  `Soul` or every non-sealed session by default.
- When continuation collapsing is enabled, groups channel-bearing summaries by
  channel and prefers the most recently updated active/suspended summary, falling
  back to the most recent sealed summary. Channel-less candidates are retained.
  Disabling collapse retains the type/channel-filtered candidates without that
  status preference.
- Orders results by update time and applies `MaxSessionsPerAgent` per agent.

This is a roster selection policy, not a universal authorization rule or a
non-expired-only predicate.

Sources: [SessionWarmupService.cs](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Sessions/SessionWarmupService.cs),
[SessionWarmupOptions.cs](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway.Configuration/SessionWarmupOptions.cs).

## Channel Adapters

Adapters implement `IChannelAdapter` for lifecycle, inbound dispatch and outbound
delivery. The contract exposes streaming, steering, follow-up, thinking/tool
display, inbound-image and interactive-prompt capabilities. Structured stream
events use the optional `IStreamEventChannelAdapter` contract. Capabilities must
be read from each implementation; they are not implied by being an adapter.

SignalR combines the inbound hub with `SignalRChannelAdapter` for outbound
conversation-group delivery. Other transports retain their own addressing and
streaming semantics. Custom adapters should construct typed inbound identity and
routing hints, dispatch through the shared entry point, and consume the fields of
`ChannelStreamTarget` appropriate to their transport. See
[IChannelAdapter.cs](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway.Contracts/Channels/IChannelAdapter.cs)
for the full current interface rather than a stale partial copy.

## Deprecated: Direct Session Join

### Historical Pattern (Removed, Not Callable)

Earlier versions used `JoinSession(agentId, sessionId)` followed by `Prompt(content)`
and described session-keyed group delivery. That explains older examples and why
conversation-keyed delivery replaced session-keyed subscriptions: compaction can
change the active session while the user is still viewing the same conversation
(#682).

**Current `GatewayHub` exposes neither `JoinSession` nor `Prompt`.** These are
historical names, not deprecated-but-working compatibility APIs. Migrate clients
to the complete `SendMessage(AgentId agentId, ChannelKey channelType, string content,
string? conversationId = null)` contract and the current subscription sequence
shown [above](#_2-sending-a-message). Pass the intended conversation ID, or explicitly
use `null` for binding-based routing. The surviving group alias does not restore
the removed hub methods.

Session mutation moved out of the hub into worker processing (#721); retain that
ownership distinction when comparing older hub code or diagnosing the short
resolution-versus-processing consistency window.

## Summary and Measurement Guidance

- Route by conversation identity; retain session identity for the execution/history
  segment and event payloads.
- Subscribe before dispatch, and distinguish stream groups from agent lifecycle
  groups and inbound isolation keys.
- Treat hub return, message end, turn end and run end as different milestones.
- Keep participant identity on conversations and consult actual policy contracts.

This guide makes **no fixed startup-latency or connections-per-hub promise**. The
previous startup/capacity estimates had no reproducible measurements attached.
Likewise, auto-session routing is not guaranteed to be a single database lookup:
resolution can read/write conversations, bindings and sessions.

For a performance claim, record the tested commit, host/runtime, configured
persistence and isolation strategy, cold versus reused handles, transcript size,
concurrent conversations/subscribers, transport, model/provider, and workload.
Measure startup separately from queue wait and first-token latency; report latency
distributions, throughput, failures/backpressure and resource use. Verify delivery
across reconnect and compaction under that workload. Report the observed operating
range, not an inferred universal limit from a cache, group helper or configuration
value. No load-test result is asserted by this documentation reconciliation.
