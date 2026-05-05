---
id: feature-conversation-model
title: "Feature: Conversation Model — for Omnichannel Continuity"
type: feature
priority: high
status: ready
created: 2026-04-27
author: rusty
tags: [sessions, channels, signalr, portal, omnichannel, architecture]
related:
  - archived/feature-multi-session-connection/architecture-proposal.md
  - archived/feature-session-visibility/design-spec.md
  - bug-blazor-session-history-loss/design-spec.md
---

# Design Spec: Conversation Model for Omnichannel Continuity

**Type**: Feature  
**Priority**: High  
**Status**: Ready for implementation  
**Author**: Rusty (via Jon)

## Overview

Introduce a new user-facing container above `Session` called **Conversation**. A conversation represents the discussion a user has with an agent, regardless of which channel or device they are using. Sessions remain the runtime/history segments within that conversation.

This allows BotNexus to support the intended interaction model:
- each agent has a default conversation when it comes online,
- the portal shows that default conversation immediately,
- the portal can deliberately create second/third/fourth independent conversations with the same agent,
- external channels like Telegram and iMessage bind to a conversation rather than directly to a session,
- the same conversation can continue across channels,
- agent replies can be delivered to all subscribed channels on the conversation,
- session operations like compact/reset happen inside the conversation without collapsing the conversation itself.

## Problem

Today, `Session` is overloaded. It acts as:
- runtime unit,
- persistence unit,
- history segment,
- user-visible conversation,
- channel routing target.

That leads to several product problems:

1. **No true omnichannel continuity**  
   A conversation started in Telegram cannot naturally continue in iMessage and then in the portal as the same conversation.

2. **Portal usability is weak by default**  
   When an agent appears in the portal, there is no durable default conversation object that the user can immediately open and use.

3. **Multi-conversation UX is built on the wrong primitive**  
   The portal can show multiple sessions, but the user really wants multiple conversations, not multiple raw runtime segments.

4. **Channel routing semantics are wrong**  
   External channels should attach to a durable conversation, not whichever active session happens to exist.

5. **Session lifecycle operations are mixed with conversation identity**  
   Compaction, reset, sealing, and rollover are runtime concerns, but today they implicitly redefine the visible conversation.

## Goals

### Must Have

- Add a new durable parent container above session: `Conversation`
- Each agent has one default conversation available for the user by default
- A conversation can contain one or more sessions over time
- Portal UI becomes conversation-first
- Portal allows deliberate creation of additional conversations per agent
- External channels bind to a conversation
- Inbound channel messages route to the conversation's active session
- Conversation can survive session compaction/reset/sealing/rollover
- Conversation history can be reconstructed from conversation sessions
- Existing session-based infrastructure remains usable during migration

### Should Have

- Agent replies fan out to all active channel bindings on the conversation
- User replies from one external channel are **not** mirrored into other external channels as if sent by the user
- Conversation metadata supports title, created/updated timestamps, and a default flag
- Portal can switch between conversations cleanly
- Session list becomes a detail view inside a conversation rather than the main sidebar abstraction

### Nice to Have

- Per-binding notification modes (`interactive`, `notify-only`, `muted`)
- Conversation rename/archive support
- Conversation search/filter in the portal
- Conversation-level analytics and summaries
- Explicit "move channel to another conversation" workflows in UI

## Non-Goals

- Full multi-user permissions model
- Cross-agent shared conversations
- Automatic semantic splitting of one conversation into multiple conversations
- Cross-conversation search/analytics in this first iteration
- Immediate parity across every channel UI from day one

## Proposed Domain Model

### New Entity: Conversation

```csharp
public sealed record Conversation
{
    public ConversationId ConversationId { get; set; }
    public AgentId AgentId { get; set; }
    public string Title { get; set; } = "New conversation";
    public bool IsDefault { get; set; }
    public ConversationStatus Status { get; set; } = ConversationStatus.Active;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public SessionId? ActiveSessionId { get; set; }
    public List<ChannelBinding> ChannelBindings { get; set; } = [];
    public Dictionary<string, object?> Metadata { get; set; } = [];
}
```

### New Value/Object Model: ChannelBinding

```csharp
public sealed record ChannelBinding
{
    public string BindingId { get; set; } = Guid.NewGuid().ToString("N");
    public ChannelKey ChannelType { get; set; }
    public string ExternalAddress { get; set; } = string.Empty; // e.g. telegram:1234567890
    public BindingMode Mode { get; set; } = BindingMode.Interactive;
    public ThreadingMode ThreadingMode { get; set; } = ThreadingMode.Single;
    public string? ThreadId { get; set; } // native thread/topic id when ThreadingMode = NativeThread
    public string? DisplayPrefix { get; set; } // short name for prefix mode, e.g. "Project Alpha"
    public DateTimeOffset BoundAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastInboundAt { get; set; }
    public DateTimeOffset? LastOutboundAt { get; set; }
}
```

### New Enum: ConversationStatus

```csharp
public enum ConversationStatus
{
    Active,
    Archived
}
```

### New Enum: BindingMode

```csharp
public enum BindingMode
{
    Interactive, // inbound + outbound
    NotifyOnly,  // outbound only
    Muted        // retained binding, no outbound fan-out
}
```

### New Enum: ThreadingMode

```csharp
public enum ThreadingMode
{
    Single,       // channel maps to one conversation only (default; Telegram, iMessage)
    NativeThread, // conversation maps to a native thread/topic (Teams, Slack)
    Prefix        // conversation name prefixed on all outbound messages (iMessage fallback, SMS)
}
```

## Relationship to Existing Session Model

`Session` remains and continues to represent:
- agent runtime,
- stored history segment,
- compaction/reset boundary,
- replay buffer scope,
- execution lifetime.

New relationship:

```text
Agent
  -> Conversation
      -> Session (1..n over time)
      -> ChannelBinding (0..n)
```

### Required Session Changes

Minimal additive change:

```csharp
public sealed record Session
{
    ...
    public ConversationId? ConversationId { get; set; }
}
```

This preserves backward compatibility while letting sessions be grouped under a conversation.

## Conceptual Behavior

### 1. Default Conversation per Agent

When an agent becomes visible/available to the system, BotNexus ensures a default conversation exists.

Rules:
- one default conversation per `AgentId`
- portal shows this conversation as the main conversation for that agent
- if no conversation exists yet, create it lazily on first contact or eagerly during warmup (configurable)

### 2. Additional Conversations Created Deliberately

The portal exposes **New Conversation** for an agent.

That creates a new conversation:
- `IsDefault = false`
- no channel bindings initially (or optionally binds the portal view implicitly)
- title can start as `Conversation 2`, `New conversation`, or first-user-message derived

### 3. Channel Binding Rules

External channels bind to a conversation, not directly to a session.

For v1:
- Telegram/iMessage default to the agent's default conversation
- later, user may explicitly rebind a channel to another conversation
- multiple channels may bind to the same conversation

### 4. Inbound Routing

When an inbound message arrives:
1. resolve `(AgentId, ChannelType, ExternalAddress)` to a conversation binding
2. if none exists, bind the channel to the default conversation for that agent
3. resolve or create the conversation's active session
4. dispatch message into that session

### 5. Outbound Routing

When the agent emits an assistant/user-visible response in the conversation's active session:
- deliver to all channel bindings on the conversation where mode allows outbound delivery
- portal subscribers viewing that conversation always receive the update

Important rule:
- **Do not mirror a human user's inbound message from Telegram into iMessage/portal as if that user sent it there.**
- Only assistant/system outputs are fanned out cross-channel.

### 6. Session Lifecycle Under a Conversation

A conversation may accumulate multiple sessions over time.

Examples:
- conversation starts with session A
- user compacts session A -> same conversation continues on compacted A or new session B depending implementation
- user resets conversation -> session A sealed, session B created, conversation unchanged
- agent crash/restart -> runtime session replaced, conversation unchanged

The visible conversation identity is stable even if the runtime segment changes.

## Portal UX Model

### Primary Sidebar

Portal sidebar should show **conversations**, not raw sessions.

Per agent:
- show default conversation immediately
- show any additional user-created conversations
- each conversation appears as a separate chat/conversation

### Conversation Detail View

Inside a conversation:
- current conversation transcript is shown as the merged history of the conversation's sessions
- session boundaries **always** appear as explicit dividers in the portal — a horizontal rule showing the date/time and session ID 
- controls for compact/reset operate on the active session in the conversation context
- advanced panel may show session history/segments for diagnostics

### Session Detail

Raw session list remains valuable, but as a secondary/diagnostic UI:
- active session id
- prior sealed sessions in the conversation
- channel bindings attached to the conversation

## API and Storage Impact

### New Store Contract

Add `IConversationStore`:

```csharp
public interface IConversationStore
{
    Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default);
    Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default);
    Task<Conversation> GetOrCreateDefaultAsync(AgentId agentId, CancellationToken ct = default);
    Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default);
    Task SaveAsync(Conversation conversation, CancellationToken ct = default);
    Task ArchiveAsync(ConversationId conversationId, CancellationToken ct = default);
    Task<Conversation?> ResolveByBindingAsync(AgentId agentId, ChannelKey channelType, string externalAddress, CancellationToken ct = default);
}
```

### New Summary DTOs

```csharp
public sealed record ConversationSummary(
    string ConversationId,
    string AgentId,
    string Title,
    bool IsDefault,
    string Status,
    string? ActiveSessionId,
    int SessionCount,
    int BindingCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
```

### New Gateway/REST Endpoints

Potential endpoints:
- `GET /api/conversations?agentId=assistant`
- `POST /api/conversations`
- `GET /api/conversations/{topicId}`
- `GET /api/conversations/{topicId}/history`
- `POST /api/conversations/{topicId}/bind-channel`
- `POST /api/conversations/{topicId}/sessions/reset`
- `POST /api/conversations/{topicId}/sessions/compact`

### SignalR Additions

Potential hub methods:
- `GetTopics(agentId)`
- `CreateTopic(agentId, title?)`
- `OpenTopic(topicId)`
- `SendTopicMessage(topicId, content)`
- `ResetTopic(topicId)`
- `CompactTopic(topicId)`

New events:
- `TopicCreated`
- `TopicUpdated`
- `TopicBindingsChanged`
- `TopicSessionChanged`

## History Model

### Conversation History Endpoint

`GET /api/conversations/{topicId}/history`

Behavior:
- returns merged chronological entries across all sessions linked to the conversation
- includes explicit `boundary` entries between sessions carrying `sessionId`, `timestamp`, and `reason`
- the portal renders each boundary as a full-width horizontal rule: `────── Session s_abc123 · Apr 27 2026 14:32 UTC ──────`
- supports pagination

Example response concept:

```json
{
  "conversationId": "c_123",
  "entries": [
    { "kind": "message", "sessionId": "s1", "role": "user", "content": "hey", "timestamp": "2026-04-27T10:00:00Z" },
    { "kind": "message", "sessionId": "s1", "role": "assistant", "content": "hi", "timestamp": "2026-04-27T10:00:05Z" },
    { "kind": "boundary", "sessionId": "s1", "timestamp": "2026-04-27T10:05:00Z", "reason": "compacted" },
    { "kind": "message", "sessionId": "s2", "role": "user", "content": "continue", "timestamp": "2026-04-27T10:05:10Z" }
  ]
}
```

This lets the portal show one continuous conversation while preserving runtime segmentation.

## Migration Strategy

### Phase 1 — Planning + additive model

- Add `ConversationId` primitive
- Add `Conversation` + `ChannelBinding`
- Add `IConversationStore`
- Add optional `Session.ConversationId`
- Introduce default conversation creation for agents
- No UI change yet

### Phase 2 — routing layer

- Change inbound channel routing to resolve conversation first
- Add conversation binding persistence
- Route inbound messages to conversation active session
- Fan out assistant replies to conversation bindings
- Preserve current session APIs for compatibility

### Phase 3 — portal conversation-first UX

- Portal sidebar lists conversations instead of raw sessions
- Default conversation visible immediately
- New conversation creates a new conversation
- Conversation history endpoint used by portal
- Session list moved into advanced/details panel

### Phase 4 — lifecycle polish

- reset/compact operate in conversation context
- explicit rebind/move channel workflows
- archive conversation
- optional conversation rename and summaries

## Compatibility Notes

- Existing session store remains valid.
- Existing channel adapters can keep writing to sessions during transition if routing shims resolve conversation -> active session.
- Existing Blazor history/session code can be adapted gradually by swapping session list endpoints for conversation list endpoints.
- Existing `SubscribeAll` session model may remain as a lower-level transport while the portal becomes conversation-oriented.

## Risks

### 1. Conceptual duplication

Having both conversations and sessions may confuse contributors unless responsibilities are documented clearly.

**Mitigation:** make conversation user-facing, session runtime-facing in all docs/API naming.

### 2. History reconstruction complexity

Merged conversation history across sessions may complicate pagination and scrollback.

**Mitigation:** introduce explicit boundary entries and keep session-scoped history endpoints intact.

### 3. Channel fan-out surprises

Cross-channel outbound delivery could annoy users if too aggressive.

**Mitigation:** binding modes and conservative default behavior.

### 4. Migration churn in UI and tests

The Blazor client recently moved toward session-first logic.

**Mitigation:** additive rollout; first add conversations alongside sessions, then flip UI abstraction later.

### 5. Future multi-user scope

Current design assumes a single effective user per agent/channel context.

**Mitigation:** keep `Conversation` model extensible for future owner/participant metadata.

## Testing Plan

### Domain / Store Tests

- creating default conversation for an agent is idempotent
- creating additional conversations does not affect default conversation
- resolving channel binding returns the correct conversation
- archived conversations are excluded from active lists
- conversation summaries include active session and binding counts

### Routing Tests

- first Telegram message binds to default conversation
- subsequent Telegram message routes to same conversation even if session rolled over
- iMessage can bind to same conversation and continue context
- assistant message fans out to all interactive/notify bindings
- inbound user message on one channel is not replayed as user input to other channels

### Portal Tests

- default conversation is visible when agent loads
- new conversation creates second conversation
- switching conversations changes visible merged history
- compact/reset inside conversation preserves conversation identity
- conversation history shows session boundaries correctly

### Compatibility Tests

- existing session APIs still work when conversation support is enabled
- session history endpoint remains correct for a conversation's active session
- old clients can still send messages through session routing shim

## Design Decisions

The following were decided by BotNexus Team on 2026-04-27:

1. **Name: `Conversation`** — More natural. Humans have conversations across any channel — in person, on the phone, via text.

2. **Default conversation: eager creation** — When an agent comes online, BotNexus immediately ensures a default conversation exists. The agent is always ready; no waiting for a first contact.

3. **Portal: implicit subscriber to all active conversations** — The portal does not create an explicit channel binding. It is always subscribed to all active conversations and sees everything. It is the control surface, not just another channel.

4. **Fan-out: all surfaces receive all agent replies by default** — Agent replies go to every surface with an active binding. This may be refined later with `BindingMode`, but the starting position is full fan-out so every channel stays in sync.

   **Channel threading strategies** — Channels vary in their ability to represent multiple conversations:
   - **Teams / Slack:** a dedicated agent channel where each new post starts a thread that maps to one conversation. Replies stay inside that thread. Native, idiomatic.
   - **iMessage / SMS:** no thread model. Prefix messages with the short conversation name so the user can visually group them. e.g. `[Project Alpha] Here is the plan...`
   - **Telegram:** may support topics/threads in groups; otherwise fall back to the prefix approach.
   - **Portal:** full native multi-conversation UI — sidebar shows each conversation as a distinct chat, no prefixing needed.
   - `ChannelBinding` should carry a `ThreadingMode` hint: `NativeThread`, `Prefix`, or `Single`, so each channel adapter can apply the right display strategy.

5. **History: assembled from session segments** — No separate materialized conversation history store. Full history is assembled on demand from the ordered sessions linked to the conversation. The portal renders an explicit **session boundary divider** between segments — a horizontal rule with the date/time and session ID — so the user can see exactly where one session ended and the next began. Sessions remain the single source of truth.

## Remaining Open Questions

None — all decisions resolved.

## Resolved Decisions

1. **Reset timing** — resetting a conversation creates a new session. The first inbound message starts the session. Any pre-session hooks (e.g. soul/memory injection) should fire eagerly on reset to reduce response latency on the first message.

2. **Conversation title** — user can set or rename a conversation title at any time. If no title is set, the agent auto-generates one based on conversation content. Both can coexist: agent suggestion, user override.

3. **Event stream** — **Option A: session stream + lookup.** Conversations are metadata — a stable identity container. All live events happen at session level. The portal observes session events and looks up conversation context via REST when needed. No dedicated conversation event stream required. Conversation is just a lookup.

## Recommendation

Proceed with a **conversation-first architectural iteration**.

The key product correction is:
- **sessions are runtime segments**,
- **conversations are the user-visible omnichannel conversations**.

That matches the mental model Jon described and gives BotNexus a cleaner long-term foundation for portal UX, channel routing, and agent continuity across transports.
