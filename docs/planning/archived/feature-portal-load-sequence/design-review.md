# Feature: Portal load sequence refactor — design review

**Author:** Leela (Lead/Architect)  
**Date:** 2026-04-30  
**Issue:** #79  
**Status:** Approved for implementation

## Executive summary

The current Blazor client is complex because it initializes in the wrong order:

1. SignalR connects first
2. `Connected`/stream events trigger REST loading indirectly
3. the UI renders while data is still missing
4. retry loops and polling are used to compensate

That sequence is the root cause of first-load history failures and the follow-on hacks added in #66 and #73.

The replacement design is deliberately small:

- keep `GatewayHubConnection`
- replace `AgentSessionManager` as the orchestration god class with four narrow responsibilities
- make startup explicitly **REST first, SignalR second**
- introduce a single readiness gate: `IPortalLoadService.IsReady`
- remove retry loops, `Task.Run` background loading, polling, and state-change-triggered loading

The goal is not “more abstraction”. The goal is **less incidental behavior**.

---

## Current-state review

### What is wrong today

`AgentSessionManager` currently owns all of this at once:

- startup orchestration
- REST calls
- SignalR subscription wiring
- agent/session/conversation state
- message mutation logic
- sub-agent lifecycle tracking
- conversation selection
- history loading
- unread tracking
- reconnection recovery

That produces several concrete problems:

1. **Wrong startup direction**  
   `InitializeAsync()` connects SignalR first, then relies on hub callbacks to populate the client.

2. **Reactive loading instead of explicit loading**  
   `HandleConnected()` kicks off `LoadConversationsAsync()` in fire-and-forget `Task.Run()` calls.

3. **UI races**  
   `MainLayout.razor` and `SetActiveAgentAsync()` must guess whether data is ready.

4. **Polling hacks**  
   `SetActiveAgentAsync()` contains a 20x100ms polling loop waiting for conversations to appear.

5. **State-change side effects**  
   `MainLayout.HandleStateChanged()` loads gateway info after `ApiBaseUrl` appears, which is another symptom of missing startup ownership.

6. **Mixed responsibilities**  
   streaming event routing and REST history loading are in the same class with shared mutable state.

### Architectural conclusion

The correct fix is not another retry. The correct fix is to make the portal startup sequence a first-class workflow and keep event handling separate from REST loading.

---

## Design principles

1. **REST establishes initial truth; SignalR delivers deltas**
2. **UI stays blocked until the initial model is complete**
3. **One service owns startup sequencing**
4. **One service owns REST calls**
5. **One service owns state**
6. **One service maps hub events to state mutations**
7. **No background retry loops, no polling waits, no load-from-state-change behavior**

---

## Service decomposition

These are the replacement contracts.

### Namespace / folder placement

All interfaces and implementations live under:

- `src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/Services/`

Recommended files:

- `Services/Abstractions/IGatewayRestClient.cs`
- `Services/Abstractions/IGatewayEventHandler.cs`
- `Services/Abstractions/IClientStateStore.cs`
- `Services/Abstractions/IPortalLoadService.cs`
- `Services/GatewayRestClient.cs`
- `Services/GatewayEventHandler.cs`
- `Services/ClientStateStore.cs`
- `Services/PortalLoadService.cs`
- `Services/ClientStateModels.cs`
- `Services/ConversationContracts.cs` (existing/new REST DTO home)

### 1. `IGatewayRestClient`

**Purpose:** all portal REST traffic. Nothing else.

**Owns:**
- agents fetch
- conversations fetch/create/update
- conversation history fetch with pagination
- channel binding fetch if needed by the sidebar/details UI
- gateway info fetch if that remains API-backed

**Does not own:**
- SignalR connection
- UI state
- event routing

```csharp
namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

public interface IGatewayRestClient
{
    void Configure(string apiBaseUrl);

    Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationSummaryDto>> GetConversationsAsync(
        string agentId,
        CancellationToken cancellationToken = default);

    Task<ConversationResponseDto> CreateConversationAsync(
        CreateConversationRequestDto request,
        CancellationToken cancellationToken = default);

    Task RenameConversationAsync(
        string conversationId,
        PatchConversationRequestDto request,
        CancellationToken cancellationToken = default);

    Task<ConversationHistoryPageDto> GetConversationHistoryAsync(
        string conversationId,
        int limit = 50,
        string? before = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChannelBindingDto>> GetConversationBindingsAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    Task<GatewayInfoDto?> GetGatewayInfoAsync(CancellationToken cancellationToken = default);
}
```

**Notes:**
- `Configure()` is a simple seam so the API base can be set once from the resolved hub URL.
- History must return a page object, not a raw list, because pagination is now a first-class contract.
- `GetConversationHistoryAsync()` uses `before` cursor semantics so scroll-to-top can request older items.

### 2. `IGatewayEventHandler`

**Purpose:** map SignalR payloads into state mutations.

**Owns:**
- translating hub payloads into store updates
- reconnection state transitions
- sub-agent event mapping
- conversation invalidation/refresh triggers when the server indicates sidebar data changed

**Does not own:**
- UI rendering
- REST transport details beyond calling `IGatewayRestClient` when required
- startup sequencing

```csharp
namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

public interface IGatewayEventHandler
{
    Task AttachAsync(CancellationToken cancellationToken = default);

    void HandleConnected(ConnectedPayload payload);
    void HandleSessionReset(SessionResetPayload payload);

    void HandleMessageStart(AgentStreamEvent payload);
    void HandleContentDelta(AgentStreamEvent payload);
    void HandleThinkingDelta(AgentStreamEvent payload);
    void HandleToolStart(AgentStreamEvent payload);
    void HandleToolEnd(AgentStreamEvent payload);
    void HandleMessageEnd(AgentStreamEvent payload);
    void HandleError(AgentStreamEvent payload);

    void HandleSubAgentSpawned(SubAgentEventPayload payload);
    void HandleSubAgentCompleted(SubAgentEventPayload payload);
    void HandleSubAgentFailed(SubAgentEventPayload payload);
    void HandleSubAgentKilled(SubAgentEventPayload payload);

    void HandleConversationUpdated(ConversationUpdatedPayload payload);

    void HandleReconnecting();
    Task HandleReconnectedAsync(CancellationToken cancellationToken = default);
    void HandleDisconnected();
}
```

**Notes:**
- `AttachAsync()` wires `GatewayHubConnection` events once.
- `ConversationUpdated` is treated differently from token-stream events: it is a sidebar invalidation signal, not transcript content.
- The handler talks only to `IClientStateStore` plus `IGatewayRestClient`.

### 3. `IClientStateStore`

**Purpose:** the single in-memory source of truth for portal state.

**Owns:**
- readiness state
- agent list
- conversation list
- selected agent / selected conversation
- per-conversation transcript state
- stream buffers
- unread counts
- pagination cursors
- sub-agent state
- notifications for UI refresh

**Does not own:**
- HTTP
- SignalR transport
- UI components

```csharp
namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

public interface IClientStateStore
{
    event Action? OnChange;

    PortalClientState Snapshot { get; }

    bool IsReady { get; }
    bool IsInitializing { get; }
    string? InitializationError { get; }

    void BeginInitialization();
    void CompleteInitialization();
    void FailInitialization(string message);

    void SetAgents(IEnumerable<AgentClientState> agents);
    void UpsertAgent(AgentClientState agent);
    bool TryGetAgent(string agentId, out AgentClientState? agent);

    void SetActiveAgent(string? agentId);
    void SetActiveConversation(string agentId, string? conversationId);

    void SetConversations(string agentId, IEnumerable<ConversationClientState> conversations);
    void UpsertConversation(string agentId, ConversationClientState conversation);
    void MarkConversationUnread(string agentId, string conversationId);
    void MarkConversationRead(string agentId, string conversationId);

    void SetConversationHistoryPage(
        string agentId,
        string conversationId,
        IReadOnlyList<ChatMessage> messages,
        string? nextBeforeCursor,
        bool hasMore);

    void PrependConversationHistoryPage(
        string agentId,
        string conversationId,
        IReadOnlyList<ChatMessage> messages,
        string? nextBeforeCursor,
        bool hasMore);

    void AppendMessage(string agentId, string conversationId, ChatMessage message);
    void ReplaceMessage(string agentId, string conversationId, string messageId, ChatMessage message);

    void StartStream(string sessionId);
    void AppendContentDelta(string sessionId, string content);
    void AppendThinkingDelta(string sessionId, string thinking);
    void CompleteStream(string sessionId, DateTimeOffset timestamp);
    void FailStream(string sessionId, string errorMessage, DateTimeOffset timestamp);
    void ResetSession(string agentId, string? sessionId);

    void RegisterSession(string agentId, string sessionId, string? channelType = null, string? sessionType = null);
    bool TryResolveAgentBySession(string sessionId, out string? agentId);
    bool TryResolveConversationBySession(string agentId, string sessionId, out string? conversationId);

    void SetConnectionState(bool isConnected);
    void SetAgentConnectionState(string agentId, bool isConnected);

    void UpsertSubAgent(string parentAgentId, SubAgentInfo subAgent);
    void UpdateSubAgentStatus(string parentAgentId, string subAgentId, string status, DateTimeOffset? completedAt, string? resultSummary);

    void NotifyChanged();
}
```

**Notes:**
- This interface is intentionally state-focused.
- `Snapshot` is the read model for components.
- Store methods are mutation verbs, not transport verbs.
- `StartStream/AppendDelta/CompleteStream` keep streaming logic out of UI code.

### 4. `IPortalLoadService`

**Purpose:** own startup sequence and readiness gate.

**Owns:**
- startup orchestration
- initial REST loading
- SignalR connect/subscribe after REST finishes
- readiness transition

**Does not own:**
- steady-state event mutation
- component rendering

```csharp
namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

public interface IPortalLoadService
{
    bool IsReady { get; }
    bool IsInitializing { get; }
    string? InitializationError { get; }

    Task InitializeAsync(string hubUrl, CancellationToken cancellationToken = default);

    Task SelectAgentAsync(string agentId, CancellationToken cancellationToken = default);

    Task SelectConversationAsync(
        string agentId,
        string conversationId,
        CancellationToken cancellationToken = default);

    Task<string?> CreateConversationAsync(
        string agentId,
        string? title = null,
        bool select = true,
        CancellationToken cancellationToken = default);

    Task LoadOlderHistoryAsync(
        string agentId,
        string conversationId,
        CancellationToken cancellationToken = default);
}
```

**Notes:**
- `IsReady` is the UI gate.
- Selection methods live here because selection may trigger REST work.
- This is the only service allowed to perform initial blocking data load.

---

## Startup sequence

`IPortalLoadService.InitializeAsync()` must implement this exact order.

### Required sequence

```text
1. BeginInitialization()
2. Derive /api base URL from hub URL
3. Configure IGatewayRestClient
4. REST: GET /api/agents
5. Build agent state in store
6. REST: GET /api/conversations?agentId=... for each agent in parallel
7. Populate conversation state for every agent
8. Choose initial active agent and active conversation
9. REST: GET /api/conversations/{id}/history?limit=50 for the selected conversation only
10. Populate transcript state for the selected conversation
11. Connect SignalR
12. SubscribeAll
13. Register returned live sessions in store
14. Attach event handler / mark connection state connected
15. CompleteInitialization() => IsReady = true
```

### What is parallel

These steps may run in parallel:

- fetching conversations for each agent after the agent list is known

```csharp
var conversationTasks = agents.Select(agent =>
    restClient.GetConversationsAsync(agent.AgentId, cancellationToken));
```

### What must stay sequential

These steps must be sequential:

1. get agents before conversations
2. load initial selected conversation history before UI is released
3. connect SignalR only after initial REST model is built
4. `SubscribeAll` only after SignalR is connected
5. `IsReady = true` only after all of the above succeeds

### Failure behavior

- if initial REST load fails: `FailInitialization()` and keep UI blocked with error state
- if SignalR connect fails after REST succeeds: also fail initialization; the portal is not ready
- no hidden retries inside `InitializeAsync()`
- retries are explicit user actions, e.g. “Retry load” button

### Initial selection rules

1. selected agent = first available agent ordered by display name unless prior route/selection exists
2. selected conversation = default conversation if present, else most recently updated conversation
3. if an agent has no conversations, create none automatically during startup; the UI shows empty state

### Why only one transcript is loaded during startup

Loading every conversation transcript up front recreates the same problem in a different form. The portal needs:

- all agents
- all conversation summaries
- one selected transcript
- live SignalR afterward

That is enough to make the UI correct without front-loading unnecessary history.

---

## State model

`AgentSessionState` is too session-centric and carries old single-session assumptions (`HistoryLoaded`, `Messages`, `CurrentStreamBuffer`, etc.) at the agent root.

The replacement store should use an explicit tree.

## State tree

```csharp
namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

public sealed class PortalClientState
{
    public bool IsReady { get; init; }
    public bool IsInitializing { get; init; }
    public string? InitializationError { get; init; }
    public bool IsConnected { get; init; }
    public string? ActiveAgentId { get; init; }
    public IReadOnlyDictionary<string, AgentClientState> Agents { get; init; } =
        new Dictionary<string, AgentClientState>();
}

public sealed class AgentClientState
{
    public required string AgentId { get; init; }
    public string DisplayName { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public string? ChannelType { get; set; }
    public string SessionType { get; set; } = "user-agent";
    public bool IsReadOnly => SessionType == "agent-subagent";
    public bool IsConnected { get; set; }
    public bool IsStreaming { get; set; }
    public string? ProcessingStage { get; set; }
    public int UnreadCount { get; set; }
    public bool ShowTools { get; set; } = true;
    public bool ShowThinking { get; set; } = true;
    public string? ActiveConversationId { get; set; }
    public Dictionary<string, ConversationClientState> Conversations { get; } = new();
    public Dictionary<string, ActiveToolCall> ActiveToolCalls { get; } = new();
    public Dictionary<string, SubAgentInfo> SubAgents { get; } = new();
}

public sealed class ConversationClientState
{
    public required string ConversationId { get; init; }
    public string Title { get; set; } = "New conversation";
    public bool IsDefault { get; set; }
    public string Status { get; set; } = "Active";
    public string? ActiveSessionId { get; set; }
    public int UnreadCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool HasLoadedLatestPage { get; set; }
    public bool IsLoadingHistory { get; set; }
    public bool HasMoreHistory { get; set; }
    public string? NextBeforeCursor { get; set; }

    public string CurrentStreamBuffer { get; set; } = string.Empty;
    public string ThinkingBuffer { get; set; } = string.Empty;

    public List<ChatMessage> Messages { get; } = new();
}
```

## Key differences from current `AgentSessionState`

### 1. history flags move to the conversation node

Current:
- `HistoryLoaded` at agent level
- `ConversationHistoryLoaded` as a separate set
- `Messages` computed from active conversation

Replacement:
- `HasLoadedLatestPage`, `HasMoreHistory`, `NextBeforeCursor`, `Messages` live on `ConversationClientState`

That removes split-brain history tracking.

### 2. streaming buffers move to the conversation node

Current:
- `CurrentStreamBuffer` and `ThinkingBuffer` live on the agent

Replacement:
- stream buffers live on the active/live conversation

That matters because streaming belongs to a conversation transcript, not abstractly to the agent.

### 3. state is explicitly portal-level

Current:
- readiness is implicit
- API availability is inferred from `ApiBaseUrl`

Replacement:
- `PortalClientState.IsReady`
- `PortalClientState.IsInitializing`
- `PortalClientState.InitializationError`

### 4. no computed `Messages` property on the agent root

The current computed `Messages` property hides which conversation is actually being mutated. The new model makes that explicit.

---

## Event routing

The event handler must mutate only the store. It must not know about Razor components.

## Mapping rules

### `Connected`

**Purpose:** server confirms hub connection and returns current agents.

**Action:**
- do **not** reload initial portal data from this event
- use it only to confirm connection state and optionally upsert missing agent metadata
- never trigger `LoadConversationsAsync()` from this event again

### `MessageStart`

**Action:**
- resolve `sessionId -> agentId`
- resolve `sessionId -> conversationId`
- mark agent `IsStreaming = true`
- clear conversation `CurrentStreamBuffer`
- clear conversation `ThinkingBuffer`
- set `ProcessingStage = "🤖 Agent is responding…"`

### `ContentDelta`

**Action:**
- append `ContentDelta` to conversation `CurrentStreamBuffer`

### `ThinkingDelta`

**Action:**
- append `ThinkingContent` to conversation `ThinkingBuffer`
- set `ProcessingStage = "💭 Thinking…"`

### `ToolStart`

**Action:**
- resolve target conversation
- append tool placeholder message
- register `ActiveToolCall`
- set `ProcessingStage = $"🔧 Using tool: {toolName}"`
- if conversation is not active, increment its unread count

### `ToolEnd`

**Action:**
- resolve `ActiveToolCall`
- update existing tool message in place with result, duration, and error state
- fallback to append if original message is missing
- if conversation is inactive, increment unread
- restore `ProcessingStage` to assistant response or null

### `MessageEnd`

**Action:**
- combine current stream buffer and thinking buffer into one final assistant message
- append to conversation `Messages`
- clear both buffers
- set `IsStreaming = false`
- clear `ProcessingStage`
- increment unread counts for inactive agent / inactive conversation

### `Error`

**Action:**
- append error message to the resolved conversation
- clear stream buffers
- set `IsStreaming = false`
- clear `ProcessingStage`

### `SessionReset`

**Action:**
- clear `SessionId`
- clear active tool calls
- clear stream buffers
- mark the conversation history page as not loaded
- clear transcript for the active conversation
- append system message: `Session reset. Start a new conversation.`

### `ConversationUpdated`

**This is the key architectural shift.**

This event does **not** directly mutate transcript content.

**Action:**
- use the payload as a stale-sidebar signal
- fetch the updated conversation summary over REST
- upsert the conversation node
- if the updated conversation is not active, increment unread
- do not synthesize transcript entries from this event

This keeps SignalR as notification and REST as source of truth for sidebar metadata.

### `SubAgentSpawned` / `Completed` / `Failed` / `Killed`

**Action:**
- mutate `SubAgents`
- append system timeline entries to the parent conversation
- keep sub-agent state entirely data-driven, not UI-driven

### `Reconnecting`

**Action:**
- set global connection false
- set all agent connection flags false
- keep transcript state intact
- do not clear conversations or history

### `Reconnected`

**Action:**
- resubscribe all sessions
- restore connection flags
- refresh conversation summaries for any agents that were streaming during disconnect
- do not reload every transcript

## Contract rule

`IGatewayEventHandler` may call:
- `IClientStateStore`
- `IGatewayRestClient`
- `GatewayHubConnection.SubscribeAllAsync()` during reconnect

It may not call Razor component APIs or trigger JS.

---

## Pagination design

## REST contract

History needs a paged DTO.

```csharp
namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

public sealed class ConversationHistoryPageDto
{
    public IReadOnlyList<ConversationHistoryItemDto> Entries { get; init; } = [];
    public string? NextBefore { get; init; }
    public bool HasMore { get; init; }
}
```

Request shape:

```text
GET /api/conversations/{id}/history?limit=50
GET /api/conversations/{id}/history?limit=50&before=<cursor>
```

Cursor rules:
- `before = null` means latest page
- `before = NextBefore` means fetch older items than the earliest currently loaded item

## Store state per conversation

Each `ConversationClientState` tracks:

- `HasLoadedLatestPage`
- `IsLoadingHistory`
- `HasMoreHistory`
- `NextBeforeCursor`
- `Messages`

## UI flow

On agent + conversation selection:
1. if `HasLoadedLatestPage == false`, call `GetConversationHistoryAsync(conversationId, 50)`
2. replace transcript with returned latest page
3. set `HasLoadedLatestPage = true`
4. set `NextBeforeCursor`
5. set `HasMoreHistory`
6. scroll to bottom

On scroll-to-top:
1. if `IsLoadingHistory == true`, do nothing
2. if `HasMoreHistory == false`, do nothing
3. call `GetConversationHistoryAsync(conversationId, 50, NextBeforeCursor)`
4. prepend returned messages to the existing list
5. preserve visual scroll anchor in the component
6. update `NextBeforeCursor` and `HasMoreHistory`

## Important rule

Pagination prepends only older persisted history. It must never overwrite streaming messages already in memory.

---

## What gets deleted

Once the new services are implemented, the following code should be deleted.

## Entire classes to delete

### Delete entirely
- `Services/AgentSessionManager.cs`
- `Services/AgentSessionState.cs` (replace with `ClientStateModels.cs`)

## Methods in `AgentSessionManager` that disappear rather than move 1:1

These should not survive as-is:

### Startup / orchestration hacks
- `InitializeAsync(string hubUrl)`
- `SetActiveAgentAsync(string? agentId)`
- `LoadHistoryAsync(string agentId)`
- `RefreshAgentsAsync()` as currently structured

### Conversation loading hacks
- `LoadConversationsAsync(string agentId)`
- private `LoadConversationHistoryAsync(string agentId, string conversationId)` current implementation
- polling branch inside `SetActiveAgentAsync()`
- `Task.Run(() => LoadConversationsAsync(id))` from `HandleConnected`

### State-change-driven load coupling
- `MainLayout.HandleStateChanged()` logic that waits for `Manager.ApiBaseUrl` to exist before loading gateway info

### Single-class event plumbing
- `HandleConnected`
- `HandleMessageStart`
- `HandleContentDelta`
- `HandleThinkingDelta`
- `HandleToolStart`
- `HandleToolEnd`
- `HandleMessageEnd`
- `HandleError`
- `HandleSessionReset`
- `HandleSubAgentSpawned`
- `HandleSubAgentCompleted`
- `HandleSubAgentFailed`
- `HandleSubAgentKilled`
- `HandleReconnecting`
- `HandleReconnected`
- `HandleDisconnected`

These behaviors still exist, but as methods on `GatewayEventHandler` against `IClientStateStore`.

### Helpers that should disappear with the old model
- `FindStateBySessionId`
- `FindConversationIdForSession`
- `GetOrCreateMessageStore`
- agent-root `Messages` computed property
- `ConversationHistoryLoaded` set
- agent-root `HistoryLoaded`
- agent-root `IsLoadingHistory`

## Methods that survive conceptually but move to better homes

### To `IGatewayRestClient`
- create conversation
- rename conversation
- fetch agents
- fetch conversations
- fetch history

### To `IPortalLoadService`
- select agent
- select conversation
- load older history
- initial startup

### To `IClientStateStore`
- register session
- mark conversation read
- unread counting
- state mutation helpers

---

## Wave breakdown for Fry

## Wave 1 — `IGatewayRestClient` + `IPortalLoadService`

### Create
- `Services/Abstractions/IGatewayRestClient.cs`
- `Services/Abstractions/IPortalLoadService.cs`
- `Services/GatewayRestClient.cs`
- `Services/PortalLoadService.cs`
- `Services/ConversationHistoryPageDto.cs` or add to `ConversationContracts.cs`

### Modify
- `Services/GatewayHubConnection.cs` if `ConversationUpdated` needs to be added
- `Layout/MainLayout.razor`
- `Program.cs`
- any startup component currently calling `AgentSessionManager.InitializeAsync()`

### Delete
- none yet, beyond dead `ApiBaseUrl` plumbing if safe in this wave

### Done when
- portal shows blocking loading state until startup completes
- startup order is REST agents -> REST conversations -> selected history -> SignalR -> SubscribeAll -> ready
- no polling loop remains in startup path
- no `Task.Run` background conversation loading remains
- `MainLayout` no longer infers readiness from `ApiBaseUrl`

## Wave 2 — `IClientStateStore` + state migration

### Create
- `Services/Abstractions/IClientStateStore.cs`
- `Services/ClientStateStore.cs`
- `Services/ClientStateModels.cs`

### Modify
- `Layout/MainLayout.razor`
- `Components/ChatPanel.razor`
- any components reading `AgentSessionState`
- `Program.cs` DI registrations

### Delete
- `Services/AgentSessionState.cs`

### Done when
- components read from `PortalClientState` / `AgentClientState` / `ConversationClientState`
- history and streaming buffers live on conversations, not the agent root
- no `ConversationHistoryLoaded` set remains
- no agent-root computed `Messages` property remains

## Wave 3 — `IGatewayEventHandler` + dead code deletion

### Create
- `Services/Abstractions/IGatewayEventHandler.cs`
- `Services/GatewayEventHandler.cs`

### Modify
- `Services/GatewayHubConnection.cs` to expose all required hub events including `ConversationUpdated`
- `Program.cs`
- `PortalLoadService.cs` to attach event handler after connect

### Delete
- `Services/AgentSessionManager.cs`

### Done when
- all hub event callbacks are handled by `GatewayEventHandler`
- event handler mutates only `IClientStateStore`
- `AgentSessionManager` is gone
- reconnect logic does not reload portal state from scratch
- `ConversationUpdated` refreshes sidebar data through REST

## Wave 4 — pagination

### Create
- none required beyond wave 1 DTOs if already present

### Modify
- `Services/GatewayRestClient.cs`
- `Services/ClientStateStore.cs`
- `Services/PortalLoadService.cs`
- `Components/ChatPanel.razor`
- JS scroll helper if anchor preservation needs support

### Delete
- any ad-hoc history reload code that still assumes one-shot latest-only history

### Done when
- selecting a conversation loads latest 50 messages
- scrolling to top loads older pages using cursor pagination
- older pages prepend without losing live streamed messages
- scroll anchor is preserved during prepend

---

## DI wiring

Target registration in `Program.cs`:

```csharp
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<GatewayHubConnection>();
builder.Services.AddScoped<IGatewayRestClient, GatewayRestClient>();
builder.Services.AddScoped<IClientStateStore, ClientStateStore>();
builder.Services.AddScoped<IGatewayEventHandler, GatewayEventHandler>();
builder.Services.AddScoped<IPortalLoadService, PortalLoadService>();
```

Any component still injecting `AgentSessionManager` should instead inject the specific service it needs:

- layout/sidebar components: `IClientStateStore`, `IPortalLoadService`
- connection widget: `GatewayHubConnection`
- gateway info widget/service: `IGatewayRestClient` or existing `GatewayInfoService` with explicit startup call

---

## Risks

### 1. Over-abstraction risk

Adding four interfaces can become ceremony if the implementations are tiny wrappers.

**Mitigation:**
- keep one implementation per interface
- do not add extra mediator/services beyond these four
- delete old code aggressively as each wave lands

### 2. Session-to-conversation resolution risk

Live events still arrive by `sessionId`.

**Mitigation:**
- keep explicit `sessionId -> agentId`
- keep explicit `conversation.ActiveSessionId`
- centralize resolution inside the store

### 3. `ConversationUpdated` payload uncertainty

If the current hub does not yet emit `ConversationUpdated`, the handler cannot rely on it.

**Mitigation:**
- add the event in `GatewayHubConnection` and server hub contract as part of implementation wave 3 if missing
- until then, a successful send can still trigger a focused conversation refresh for that agent

### 4. Reconnect gap risk

Messages may be missed during transient disconnect.

**Mitigation:**
- on reconnect, refresh conversation summaries for affected agents
- only reload the active transcript when there is evidence of drift, not on every reconnect

---

## Final architectural call

This refactor should **replace hidden behavior with explicit behavior**.

The simplification is:

- startup is owned by `IPortalLoadService`
- REST is owned by `IGatewayRestClient`
- state is owned by `IClientStateStore`
- live event mutation is owned by `IGatewayEventHandler`

That is enough. Do not add more layers.

The implementation should optimize for deleting code, not preserving class names.

---

## Sequence Diagrams

### 1. Site Startup — Page Load to IsReady

```mermaid
sequenceDiagram
    participant Browser
    participant MainLayout
    participant PortalLoadService
    participant RestClient
    participant SignalR
    participant Gateway

    Browser->>MainLayout: OnInitializedAsync
    MainLayout->>PortalLoadService: InitializeAsync()
    MainLayout-->>Browser: render loading spinner (IsReady=false)

    PortalLoadService->>RestClient: GetAgentsAsync()
    RestClient->>Gateway: GET /api/agents
    Gateway-->>RestClient: [agent list]
    RestClient-->>PortalLoadService: agents

    par for each agent
        PortalLoadService->>RestClient: GetConversationsAsync(agentId)
        RestClient->>Gateway: GET /api/conversations?agentId=
        Gateway-->>RestClient: [conversation list]
        RestClient-->>PortalLoadService: conversations
    end

    PortalLoadService->>SignalR: ConnectAsync()
    SignalR->>Gateway: WS connect + SubscribeAll
    Gateway-->>SignalR: Connected payload
    SignalR-->>PortalLoadService: connected

    PortalLoadService->>StateStore: Seed(agents, conversations)
    PortalLoadService-->>MainLayout: IsReady = true
    MainLayout-->>Browser: hide spinner, render UI
```

---

### 2. Agent + Conversation Select

```mermaid
sequenceDiagram
    participant User
    participant Sidebar
    participant StateStore
    participant RestClient
    participant ChatPanel
    participant Gateway

    User->>Sidebar: click conversation
    Sidebar->>StateStore: SetActiveConversation(agentId, convId)

    alt history not cached
        StateStore->>RestClient: GetHistoryAsync(convId, limit=50)
        RestClient->>Gateway: GET /api/conversations/{id}/history?limit=50
        Gateway-->>RestClient: { entries, totalCount }
        RestClient-->>StateStore: history entries
        StateStore-->>ChatPanel: OnStateChanged
        ChatPanel-->>User: render messages, scroll to bottom
    else history cached (feature flag)
        StateStore-->>ChatPanel: OnStateChanged (from cache)
        ChatPanel-->>User: render immediately
        StateStore->>RestClient: GetHistoryAsync() in background
        RestClient-->>StateStore: update if changed
    end
```

---

### 3. Outbound Message — User Sends to Response Rendered

```mermaid
sequenceDiagram
    participant User
    participant ChatPanel
    participant StateStore
    participant SignalR
    participant Gateway
    participant Agent

    User->>ChatPanel: type + send
    ChatPanel->>StateStore: AddUserMessage(convId, text)
    StateStore-->>ChatPanel: render user message
    ChatPanel->>SignalR: SendMessageToConversation(agentId, convId, text)
    SignalR->>Gateway: hub invoke
    Gateway->>Agent: dispatch message

    loop streaming
        Agent-->>Gateway: ContentDelta
        Gateway-->>SignalR: ContentDelta event
        SignalR-->>StateStore: HandleContentDelta
        StateStore-->>ChatPanel: OnStateChanged (stream buffer)
        ChatPanel-->>User: render streaming text
    end

    Agent-->>Gateway: MessageEnd
    Gateway-->>SignalR: MessageEnd event
    SignalR-->>StateStore: HandleMessageEnd (commit message)
    StateStore-->>ChatPanel: OnStateChanged (final message)
    ChatPanel-->>User: render complete response
```

---

### 4. Inbound SignalR Event — Live Update

```mermaid
sequenceDiagram
    participant Gateway
    participant SignalR
    participant EventHandler
    participant StateStore
    participant ChatPanel
    participant Sidebar

    Gateway-->>SignalR: event (ContentDelta / ToolStart / etc.)
    SignalR->>EventHandler: route event by type
    EventHandler->>StateStore: mutate state (FindConversation by sessionId)
    
    alt active conversation
        StateStore-->>ChatPanel: OnStateChanged
        ChatPanel-->>ChatPanel: re-render canvas
    else inactive conversation
        StateStore-->>Sidebar: OnStateChanged (unread badge++)
    end
```

---

### 5. External Conversation — Arrives from Telegram

```mermaid
sequenceDiagram
    participant TelegramUser
    participant TelegramAdapter
    participant Gateway
    participant SignalR
    participant EventHandler
    participant StateStore
    participant RestClient
    participant Sidebar

    TelegramUser->>TelegramAdapter: sends message
    TelegramAdapter->>Gateway: InboundMessage (telegram channel)
    Gateway->>Gateway: route to agent, create/find conversation
    Gateway-->>SignalR: ConversationUpdated event (convId, agentId)
    SignalR->>EventHandler: HandleConversationUpdated
    
    alt conversation known to client
        EventHandler->>StateStore: mark conversation dirty
    else new conversation
        EventHandler->>RestClient: GetConversationAsync(convId)
        RestClient->>Gateway: GET /api/conversations/{id}
        Gateway-->>RestClient: conversation details
        RestClient-->>StateStore: add conversation
    end

    Gateway-->>SignalR: ContentDelta (for the agent response)
    SignalR->>EventHandler: route to conversation store
    StateStore-->>Sidebar: OnStateChanged (new conv + unread badge)
    Sidebar-->>Sidebar: show new conversation with unread dot
```

