# Design Review: Conversation Model

**Date:** 2026-04-27  
**Author:** Leela (Lead/Architect)  
**Status:** Approved with clarifications before implementation starts  
**Spec reviewed:** `docs/planning/feature-conversation-topics/design-spec.md`  
**Research reviewed:** `docs/planning/feature-conversation-topics/research.md`

## Executive Summary

The feature is directionally correct and buildable on the current BotNexus codebase. The core architectural move — introducing a durable `Conversation` above runtime `Session` — is the right correction for omnichannel continuity.

The spec is **not blocked**, but it is **underspecified in seven implementation-critical seams**:

1. `ConversationId` is **already present** in `BotNexus.Domain.Primitives`; the spec should build on that instead of re-inventing it.
2. `IConversationStore` needs a concrete home and default persistence strategy.
3. DI registration must be explicit in `GatewayServiceCollectionExtensions` and platform config wiring.
4. `GatewayHub.ResolveOrCreateSessionAsync()` must become conversation-first instead of channel/session-first.
5. `Session.ConversationId` needs nullable backfill semantics for existing sessions.
6. `ISessionWarmupService` should stay session-scoped; conversation warmup needs a separate concern.
7. Fan-out cannot live in channel adapters alone; it needs a gateway-level conversation routing service.

## Overall Assessment

### What is strong

- The product model is correct: **conversation = user-visible continuity**, **session = runtime/history segment**.
- The additive migration strategy is correct.
- Keeping session history as the source of truth and assembling conversation history on demand is correct.
- The portal becoming conversation-first while live transport stays session-level is the right compatibility balance.

### What must be clarified before implementation

The spec currently mixes three different layers:
- domain model (`Conversation`, `ChannelBinding`)
- routing behavior (inbound resolution, outbound fan-out)
- UI/API terminology (`Topic*` method names in some examples, `Conversation*` elsewhere)

Implementation should standardize on **Conversation** everywhere in C# contracts and REST routes.

---

## Final Architecture Review

## 1. Missing primitives

### Finding
`ConversationId` is **not missing**. It already exists at:
- `src/domain/BotNexus.Domain/Primitives/ConversationId.cs`

That is the correct location, alongside `SessionId`, `AgentId`, and `ChannelKey`.

### Decision
- **Use the existing `ConversationId` value object.**
- Do **not** create a second `ConversationId` in Gateway projects.
- Add generation helper parity later if needed (for example `Create()`), but do not fork the primitive.

### Implication
The spec's Phase 1 item "Add `ConversationId` primitive" is already complete from a type-placement perspective. Implementation only needs to adopt it.

---

## 2. Storage: where `IConversationStore` lives and who implements it

### Finding
The current codebase pattern is:
- contracts/interfaces in `src/gateway/BotNexus.Gateway.Contracts/`
- runtime orchestration in `src/gateway/BotNexus.Gateway/`
- persistence implementations in `src/gateway/BotNexus.Gateway.Conversations/` (extracted from Sessions)
- domain/value types in `src/domain/BotNexus.Domain/`

Session persistence already has three implementations:
- `InMemorySessionStore`
- `FileSessionStore`
- `SqliteSessionStore`

### Decision
- `IConversationStore` should live in **`BotNexus.Gateway.Contracts/Conversations`**.
- The initial implementations should live in **`BotNexus.Gateway.Conversations`** (a dedicated project, extracted during implementation).
- Required implementations for v1:
  - `InMemoryConversationStore`
  - `FileConversationStore`
  - `SqliteConversationStore`

### Rationale
Conversation persistence is the same class of problem as session persistence: gateway-owned durable state. During implementation, conversation stores were extracted into a dedicated `BotNexus.Gateway.Conversations` project to keep the Sessions project focused on session persistence.

### Backing store shape
- **InMemory**: for tests/dev parity.
- **File**: one JSON file per conversation plus binding metadata embedded in the same document.
- **Sqlite**: preferred durable implementation for single-node production.

### Sqlite schema recommendation
Use two tables, not one:

#### `conversations`
- `id TEXT PRIMARY KEY`
- `agent_id TEXT NOT NULL`
- `title TEXT NOT NULL`
- `is_default INTEGER NOT NULL`
- `status TEXT NOT NULL`
- `active_session_id TEXT NULL`
- `created_at TEXT NOT NULL`
- `updated_at TEXT NOT NULL`
- `metadata_json TEXT NOT NULL`

#### `conversation_bindings`
- `binding_id TEXT PRIMARY KEY`
- `conversation_id TEXT NOT NULL`
- `channel_type TEXT NOT NULL`
- `external_address TEXT NOT NULL`
- `mode TEXT NOT NULL`
- `threading_mode TEXT NOT NULL`
- `thread_id TEXT NULL`
- `display_prefix TEXT NULL`
- `bound_at TEXT NOT NULL`
- `last_inbound_at TEXT NULL`
- `last_outbound_at TEXT NULL`

Unique index:
- `(agent_id, channel_type, external_address)` is conceptually required, but because `agent_id` lives on `conversations`, the effective uniqueness should be enforced in store logic or via join-aware insert/update logic.

### Why not store conversation state in session metadata?
Because conversation is a first-class durable object with its own lifecycle, bindings, active session pointer, title, and archive state. Session metadata would become a fragmented secondary source of truth.

---

## 3. DI registration

### Finding
Current gateway registration happens in:
- `src/gateway/BotNexus.Gateway/Extensions/GatewayServiceCollectionExtensions.cs`

Session store selection is already driven from platform config in `ConfigureSessionStore(...)`.

### Decision
Add a parallel `ConfigureConversationStore(...)` path in the same extension file.

### Required DI registrations
At minimum:
- `IConversationStore`
- `IConversationRouter` (new)
- `IConversationHistoryService` (new)
- `IConversationLifecycleService` (optional if reset/compact orchestration is separated)

### Default registrations
- `TryAddSingleton<IConversationStore, InMemoryConversationStore>()`
- Platform config override to `FileConversationStore` or `SqliteConversationStore`
- `AddSingleton<IConversationRouter, DefaultConversationRouter>()`
- `AddSingleton<IConversationHistoryService, ConversationHistoryService>()`

### Config recommendation
Mirror session store config rather than inventing a separate top-level model:

```json
"gateway": {
  "conversationStore": {
    "type": "Sqlite",
    "connectionString": "Data Source=..."
  }
}
```

If omitted:
- follow the same mode as `sessionStore` where practical
- default to in-memory in tests/dev

---

## 4. Channel routing: `GatewayHub.ResolveOrCreateSessionAsync()`

### Finding
Today `GatewayHub.SendMessage(...)` is session-centric:
1. find a recent session by `(agentId, channelType)`
2. create or reuse session
3. dispatch inbound message with `ConversationId = session.SessionId.Value`

This is the biggest architectural mismatch with the new spec.

### Decision
`GatewayHub` must stop resolving sessions directly for user-facing chat entrypoints.

### Required replacement flow
Replace `ResolveOrCreateSessionAsync(...)` with conversation-first resolution:

1. resolve or create the target `Conversation`
   - portal message with explicit conversation ID -> use it
   - external channel message -> resolve by binding
   - no binding -> bind to default conversation
2. resolve or create the conversation's active session
3. ensure `Session.ConversationId = conversation.ConversationId`
4. dispatch inbound message to that session
5. keep live stream semantics session-based

### Concrete change to hub surface
Current:
- `SendMessage(AgentId agentId, ChannelKey channelType, string content)`

Needed additions:
- `SendConversationMessage(ConversationId conversationId, string content)` for portal
- legacy `SendMessage(...)` can remain as a shim that resolves default/bound conversation first

### Important nuance
The portal is **not** a persisted `ChannelBinding`. Jon already resolved that: the portal is an implicit observer/control surface.

So the router must distinguish:
- **interactive external channel bindings** -> persisted in store
- **portal traffic** -> explicit conversation id from UI, no stored binding

---

## 5. `Session.ConversationId` and existing sessions

### Finding
Adding nullable `Session.ConversationId` is the correct compatibility move.

### Risk
Existing sessions in file/sqlite stores will have no `ConversationId`. The spec does not define what that means at read time.

### Decision
Treat null `ConversationId` as **legacy ungrouped session**, not as corrupted data.

### Migration/backfill rules
- Existing sessions remain readable with `ConversationId = null`.
- They do **not** need eager data migration.
- When a legacy session becomes the active session of a conversation, save it back with `ConversationId` populated.
- When listing conversation history, only sessions explicitly linked by `ConversationId` are included.
- A compatibility shim may expose "legacy sessions" separately in diagnostics, but they are not auto-stitched into conversations without explicit adoption.

### Why not auto-backfill all existing sessions into a default conversation?
Because channel identity and user intent are ambiguous for historical session data. Silent mass migration would create false conversation groupings.

---

## 6. `ISessionWarmupService`

### Finding
`ISessionWarmupService` currently caches and returns available session summaries for the portal/session subscription model.

### Decision
Do **not** overload `ISessionWarmupService` to warm conversations.

### Rationale
Its responsibility is session availability cache. Conversations are a different abstraction and will need:
- default conversation eager creation
- conversation summaries per agent
- possibly binding hydration

That is not a session warmup concern.

### Required addition
Introduce a separate service:

```csharp
public interface IConversationCatalogService
{
    Task<IReadOnlyList<ConversationSummary>> GetAvailableConversationsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConversationSummary>> GetAvailableConversationsAsync(AgentId agentId, CancellationToken cancellationToken = default);
    Task<ConversationSummary> EnsureDefaultConversationAsync(AgentId agentId, CancellationToken cancellationToken = default);
}
```

### Warmup behavior recommendation
- Keep `SessionWarmupService` unchanged except for ignoring conversation concerns.
- Add `ConversationCatalogService : IHostedService` or an eager-create startup pass for default conversations.
- On gateway startup, ensure each visible agent has exactly one default conversation.

This aligns with Jon's explicit decision: **default conversation eager creation**.

---

## 7. Fan-out and outbound routing

### Finding
The spec correctly says assistant replies fan out to all bound channels.

But the proposed implementation direction is underspecified. The current architecture routes outbound delivery via channel adapters keyed off the session's original channel context. That is not sufficient for conversation fan-out.

### Decision
Fan-out must be coordinated in a gateway-level service, not embedded inside each channel adapter.

### Required new service
```csharp
public interface IConversationRouter
{
    Task<Conversation> ResolveForInboundAsync(
        AgentId agentId,
        ChannelKey channelType,
        string externalAddress,
        CancellationToken cancellationToken = default);

    Task<GatewaySession> ResolveActiveSessionAsync(
        ConversationId conversationId,
        CancellationToken cancellationToken = default);

    Task FanOutAssistantMessageAsync(
        ConversationId conversationId,
        SessionId sessionId,
        OutboundConversationMessage message,
        CancellationToken cancellationToken = default);
}
```

### Rationale
The router owns three policies that adapters should not each re-implement:
- binding lookup
- active-session resolution
- binding-mode/threading fan-out

### Adapter role after change
Adapters become transport-specific render/send executors:
- apply `ThreadingMode`
- apply `DisplayPrefix`
- send to external address

They should not decide *which* bindings receive a message.

### SignalR-specific implication
`SignalRChannelAdapter` and any other adapters should receive outbound sends from the conversation router with already-resolved binding targets. The portal still receives live session events through SignalR groups, but explicit external outbound fan-out is conversation-driven.

---

## Interface Contracts

## 1. `ConversationId`

**Placement:** `src/domain/BotNexus.Domain/Primitives/ConversationId.cs`  
**Namespace:** `BotNexus.Domain.Primitives`

### Decision
Reuse the existing type.

### Optional improvement
Add `Create()` for parity with `SessionId.Create()` if missing:

```csharp
public static ConversationId Create() => From($"c_{Guid.NewGuid():N}");
```

This is optional but recommended for consistency.

---

## 2. `Conversation`

**Placement:** `src/domain/BotNexus.Domain/Conversations/Conversation.cs`  
**Namespace:** `BotNexus.Domain.Conversations`

```csharp
using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.Conversations;

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

### Notes
- Keep it in Domain, not Gateway.Contracts.
- `ActiveSessionId` is the durable pointer used by routing.
- `Metadata` is appropriate for future title suggestions, archive reason, summary hints.

---

## 3. `ChannelBinding` and `ThreadingMode`

**Placement:** `src/domain/BotNexus.Domain/Conversations/ChannelBinding.cs`  
**Namespace:** `BotNexus.Domain.Conversations`

```csharp
using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.Conversations;

public sealed record ChannelBinding
{
    public string BindingId { get; set; } = Guid.NewGuid().ToString("N");
    public ChannelKey ChannelType { get; set; }
    public string ExternalAddress { get; set; } = string.Empty;
    public BindingMode Mode { get; set; } = BindingMode.Interactive;
    public ThreadingMode ThreadingMode { get; set; } = ThreadingMode.Single;
    public string? ThreadId { get; set; }
    public string? DisplayPrefix { get; set; }
    public DateTimeOffset BoundAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastInboundAt { get; set; }
    public DateTimeOffset? LastOutboundAt { get; set; }
}

public enum BindingMode
{
    Interactive,
    NotifyOnly,
    Muted
}

public enum ThreadingMode
{
    Single,
    NativeThread,
    Prefix
}
```

### Notes
- This belongs in Domain with `Conversation`, not hidden in an infrastructure project.
- `ExternalAddress` stays string-based for v1; channel-specific parsing stays in adapters/router.

---

## 4. `IConversationStore`

**Placement:** `src/gateway/BotNexus.Gateway.Contracts/Conversations/IConversationStore.cs`  
**Namespace:** `BotNexus.Gateway.Abstractions.Conversations`

```csharp
using BotNexus.Domain.Conversations;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Conversations;

public interface IConversationStore
{
    Task<Conversation?> GetAsync(
        ConversationId conversationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Conversation>> ListAsync(
        AgentId? agentId = null,
        CancellationToken cancellationToken = default);

    Task<Conversation> GetOrCreateDefaultAsync(
        AgentId agentId,
        CancellationToken cancellationToken = default);

    Task<Conversation> CreateAsync(
        Conversation conversation,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        Conversation conversation,
        CancellationToken cancellationToken = default);

    Task ArchiveAsync(
        ConversationId conversationId,
        CancellationToken cancellationToken = default);

    Task<Conversation?> ResolveByBindingAsync(
        AgentId agentId,
        ChannelKey channelType,
        string externalAddress,
        CancellationToken cancellationToken = default);
}
```

### Additional recommendation
Do not add reset/compact methods here. Those are orchestration operations over conversation + session, not raw persistence operations.

---

## 5. `ConversationSummary`

**Placement:** `src/gateway/BotNexus.Gateway.Contracts/Conversations/ConversationSummary.cs`  
**Namespace:** `BotNexus.Gateway.Abstractions.Conversations`

```csharp
namespace BotNexus.Gateway.Abstractions.Conversations;

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

### Notes
- Keep as transport/API DTO, not domain entity.
- `SessionCount` should be derived from `ISessionStore` by `ConversationId`.

---

## 6. `Session` changes

**Placement:** `src/domain/BotNexus.Domain/Gateway/Models/Session.cs`  
**Namespace:** existing `BotNexus.Gateway.Abstractions.Models`

### Required additive change
```csharp
public ConversationId? ConversationId { get; set; }
```

### Recommended placement in record
Immediately after `AgentId` so identity fields stay grouped:

```csharp
public SessionId SessionId { get; set; }
public AgentId AgentId { get; set; }
public ConversationId? ConversationId { get; set; }
public ChannelKey? ChannelType { get; set; }
```

### Migration rule
Nullable, no eager backfill, populate on first conversation-aware save.

---

## 7. `GatewayHub` changes

### Required contract changes

#### Keep for compatibility
- `SendMessage(AgentId agentId, ChannelKey channelType, string content)`

#### Add conversation-first hub methods
**Placement:** existing `GatewayHub.cs`

```csharp
public Task<IReadOnlyList<ConversationSummary>> GetConversations(string agentId);
public Task<ConversationSummary> CreateConversation(string agentId, string? title = null);
public Task OpenConversation(string conversationId);
public Task<SendMessageResult> SendConversationMessage(string conversationId, string content);
public Task ResetConversation(string conversationId);
public Task<CompactSessionResult> CompactConversation(string conversationId);
```

### Behavioral changes in hub internals
- Replace `ResolveOrCreateSessionAsync(AgentId, ChannelKey)` with conversation routing.
- `SendConversationMessage` should:
  1. load conversation
  2. resolve/create active session
  3. subscribe caller to that active session group
  4. dispatch using that session id and conversation id

### Naming decision
Use `Conversation*` methods, not `Topic*`, to match resolved product language.

---

## Additional contracts required by implementation

## 1. `IConversationRouter`

**Placement:** `src/gateway/BotNexus.Gateway.Contracts/Conversations/IConversationRouter.cs`

```csharp
using BotNexus.Domain.Conversations;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Conversations;

public interface IConversationRouter
{
    Task<Conversation> ResolveForInboundAsync(
        AgentId agentId,
        ChannelKey channelType,
        string externalAddress,
        CancellationToken cancellationToken = default);

    Task<GatewaySession> ResolveActiveSessionAsync(
        ConversationId conversationId,
        CancellationToken cancellationToken = default);

    Task BindChannelAsync(
        ConversationId conversationId,
        ChannelBinding binding,
        CancellationToken cancellationToken = default);

    Task FanOutAssistantMessageAsync(
        ConversationId conversationId,
        SessionId sessionId,
        string content,
        CancellationToken cancellationToken = default);
}
```

---

## 2. `IConversationHistoryService`

**Placement:** `src/gateway/BotNexus.Gateway.Contracts/Conversations/IConversationHistoryService.cs`

```csharp
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Conversations;

public interface IConversationHistoryService
{
    Task<ConversationHistoryResponse> GetHistoryAsync(
        ConversationId conversationId,
        ConversationHistoryCursor? cursor = null,
        int pageSize = 100,
        CancellationToken cancellationToken = default);
}
```

With DTOs:
- `ConversationHistoryResponse`
- `ConversationHistoryEntryDto`
- `ConversationBoundaryEntryDto`
- `ConversationHistoryCursor`

This keeps history assembly out of the store.

---

## Implementation Wave Breakdown

## Wave 1 — Domain + Contracts + Storage Foundation

**Agent:** Farnsworth  
**Dependencies:** none  
**Estimated tests:** 18-24

### Tasks
- Create `Conversation`, `ChannelBinding`, `ConversationStatus`, `BindingMode`, `ThreadingMode`
- Reuse/adjust `ConversationId` primitive as needed
- Add `Session.ConversationId`
- Add `IConversationStore`
- Add `ConversationSummary`
- Add file/in-memory/sqlite conversation store implementations
- Add conversation store config wiring in `GatewayServiceCollectionExtensions`

### Files
- `src/domain/BotNexus.Domain/Conversations/Conversation.cs`
- `src/domain/BotNexus.Domain/Conversations/ChannelBinding.cs`
- `src/domain/BotNexus.Domain/Gateway/Models/Session.cs`
- `src/gateway/BotNexus.Gateway.Contracts/Conversations/IConversationStore.cs`
- `src/gateway/BotNexus.Gateway.Contracts/Conversations/ConversationSummary.cs`
- `src/gateway/BotNexus.Gateway.Conversations/InMemoryConversationStore.cs`
- `src/gateway/BotNexus.Gateway.Conversations/FileConversationStore.cs`
- `src/gateway/BotNexus.Gateway.Conversations/SqliteConversationStore.cs`
- `src/gateway/BotNexus.Gateway/Extensions/GatewayServiceCollectionExtensions.cs`

### Done when
- Conversations can be created, listed, archived, and resolved by binding
- Default conversation creation is idempotent
- Session model compiles with nullable `ConversationId`
- Storage mode selection works the same way as session store selection

---

## Wave 2 — Routing + Active Session Resolution + Fan-out Core

**Agent:** Bender  
**Dependencies:** Wave 1  
**Estimated tests:** 16-22

### Tasks
- Add `IConversationRouter` contract and `DefaultConversationRouter`
- Move inbound resolution from raw session selection to conversation resolution
- Ensure default conversation eager-create path exists
- Resolve/create active session per conversation
- Persist `Session.ConversationId` when session adopted by conversation
- Add assistant outbound fan-out orchestration for external bindings

### Files
- `src/gateway/BotNexus.Gateway.Contracts/Conversations/IConversationRouter.cs`
- `src/gateway/BotNexus.Gateway/Conversations/DefaultConversationRouter.cs`
- `src/extensions/BotNexus.Extensions.Channels.SignalR/GatewayHub.cs`
- `src/gateway/BotNexus.Gateway/GatewayHost.cs`
- `src/gateway/BotNexus.Gateway/Channels/InternalChannelAdapter.cs`
- possibly channel adapters that currently assume one-session-one-channel delivery

### Done when
- inbound Telegram/SignalR-style traffic resolves conversation first
- a conversation always has or can create an active session
- assistant output can be delivered to all eligible bindings on that conversation
- user inbound is not mirrored cross-channel

---

## Wave 3 — History Assembly + Conversation APIs

**Agent:** Fry  
**Dependencies:** Wave 1, partial Wave 2  
**Estimated tests:** 12-18

### Tasks
- Add `IConversationHistoryService`
- Assemble merged history from sessions linked by `ConversationId`
- Emit explicit session boundary entries
- Add REST endpoints:
  - list conversations
  - create conversation
  - get conversation
  - get conversation history
  - bind channel
- Add hub methods for conversation-first portal usage

### Files
- `src/gateway/BotNexus.Gateway.Contracts/Conversations/IConversationHistoryService.cs`
- `src/gateway/BotNexus.Gateway/Conversations/ConversationHistoryService.cs`
- `src/gateway/BotNexus.Gateway.Api/Controllers/ConversationsController.cs`
- `src/extensions/BotNexus.Extensions.Channels.SignalR/GatewayHub.cs`
- DTO files under Contracts/Conversations

### Done when
- conversation history returns merged session history with boundary entries
- conversation list endpoint returns summaries with counts
- portal can call conversation-first APIs without relying on raw session ids

---

## Wave 4 — Portal/UI Conversation-First Flip

**Agent:** Amy  
**Dependencies:** Wave 3  
**Estimated tests:** 10-14 UI/component/E2E

### Tasks
- flip sidebar from session-first to conversation-first
- show default conversation immediately per agent
- add new conversation affordance
- render merged history with session divider rows
- move raw session list into advanced/details panel

### Files
- Blazor or SignalR client conversation list/view components
- any existing session-first sidebar/view-model code
- UI tests/E2E coverage

### Done when
- user can open an agent and immediately see its default conversation
- new conversation creates a second visible conversation
- merged transcript renders boundaries correctly
- reset/compact actions target active session within the current conversation context

---

## Wave 5 — QA, Compatibility, and Documentation

**Agent:** Hermes + Kif  
**Dependencies:** Waves 1-4  
**Estimated tests:** 20-28

### Tasks
- regression coverage for legacy session APIs
- compatibility tests for old clients using session shims
- docs for conversation vs session responsibility split
- update training and architecture docs

### Done when
- old session APIs still function
- conversation-aware behavior is documented clearly
- no ambiguity remains in naming or ownership boundaries

---

## Risk Register

## 1. Two sources of truth for routing

**Risk:** conversation bindings and session channel fields diverge, causing delivery bugs.  
**Mitigation:** declare conversation binding as the sole routing source for external outbound; keep `Session.ChannelType` as runtime provenance only.

## 2. Legacy sessions remain ungrouped and confuse history

**Risk:** existing persisted sessions without `ConversationId` produce fragmented UX.  
**Mitigation:** treat them as legacy-only diagnostics; only explicitly linked sessions participate in conversation history.

## 3. Fan-out duplicates or loops through existing adapter logic

**Risk:** assistant messages may be sent once via old session path and again via new conversation path.  
**Mitigation:** centralize outbound fan-out in `IConversationRouter`; audit/remove any direct adapter-side rebroadcast assumptions.

## 4. Portal/session live stream mismatch

**Risk:** portal is conversation-first while live streaming remains session-first, creating state mismatch when active session rolls over.  
**Mitigation:** portal tracks conversation -> activeSessionId mapping and re-subscribes on `ConversationSessionChanged` or equivalent response path.

## 5. SQLite/file schema drift between session and conversation persistence

**Risk:** implementing conversation store in a separate pattern from session store increases maintenance burden.  
**Mitigation:** keep conversation persistence in `BotNexus.Gateway.Conversations` and mirror session store configuration and serialization conventions.

---

## Final Decisions

1. **Conversation is approved as the top-level user-visible container.**
2. **`ConversationId` already exists and must be reused.**
3. **`IConversationStore` belongs in `BotNexus.Gateway.Contracts`; implementations belong in `BotNexus.Gateway.Conversations`.**
4. **`GatewayHub.ResolveOrCreateSessionAsync()` must be replaced by conversation-first routing.**
5. **`Session.ConversationId` remains nullable for compatibility; no eager historical backfill.**
6. **`ISessionWarmupService` remains session-scoped; conversation warmup/catalog is a separate service.**
7. **Fan-out belongs in a gateway conversation router, not scattered through channel adapters.**
8. **Portal uses conversation-first APIs; live streaming remains session-level transport.**

## Recommendation

Proceed with implementation, but do it in the wave order above. Do **not** start with the UI flip. The highest-risk seam is routing. Get domain/contracts/storage/routing correct first, then layer history and UI on top.
