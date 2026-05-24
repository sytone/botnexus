---
owner: ai
author: Hermes
ai-policy: open
---

# Gateway Flow Diagrams

Architectural diagrams for the BotNexus gateway message processing pipeline.
Focus: gateway core. Channel-specific details (Telegram, SignalR) are deliberately omitted.

---

## Diagram 1 — Inbound Message Flow

```mermaid
sequenceDiagram
    participant CA as Channel Adapter
    participant GH as GatewayHost
    participant CR as ConversationRouter
    participant SS as SessionStore
    participant AS as AgentSupervisor
    participant A as Agent

    CA->>GH: dispatcher.DispatchAsync(InboundMessage)

    alt message has explicit ConversationId
        GH->>CR: ResolveInboundByConversationAsync(conversationId, ...)
    else route by channel key
        GH->>CR: ResolveInboundAsync(agentId, channelType, channelAddress, threadId)
    end

    CR->>CR: Resolve or create Conversation
    CR->>SS: GetOrCreate Session
    note over CR,SS: Reuse Active/Expired session<br/>Create new only if Sealed or absent
    CR-->>GH: ConversationRoutingResult(conversation, sessionId, originatingBinding)

    GH->>GH: Stamp BindingId on message from originatingBinding
    GH->>SS: GetOrCreateAsync(sessionId, agentId)
    GH->>AS: GetOrCreateAsync(agentId, sessionId)
    AS-->>GH: IAgentHandle

    alt streaming supported
        GH->>A: StreamAsync(prompt)
        A-->>GH: IAsyncEnumerable<AgentResponse>
    else
        GH->>A: PromptAsync(prompt)
        A-->>GH: AgentResponse
    end

    GH->>CA: adapter.SendAsync(OutboundMessage) [direct reply]
    GH->>SS: session.AddEntry(assistant response)
    GH->>GH: FanOutAsync(message, sessionId)
    note over GH: Fan-out to all non-originating<br/>Interactive/NotifyOnly bindings
```

---

## Diagram 2 — Conversation & Session Resolution

```mermaid
flowchart TD
    A([InboundMessage arrives]) --> B{ConversationId\non message?}

    B -- Yes --> C[Direct lookup by ConversationId]
    B -- No  --> D[Lookup by channelType + channelAddress + threadId]

    C --> E{Conversation\nfound?}
    D --> E

    E -- No  --> F[Create new Conversation + ChannelBinding\nstamp Initiator = inbound.Sender]
    E -- Yes --> G[Use existing Conversation\nInitiator preserved write-once]
    F --> G

    G --> H{ActiveSessionId\npresent?}

    H -- No  --> I[Create new Session]
    H -- Yes --> J{Session\nstatus?}

    J -- Sealed --> I
    J -- Active  --> K[Reuse session as-is]
    J -- Expired --> L[Reactivate: set status back to Active]

    I --> M([Return ConversationRoutingResult])
    K --> M
    L --> M
```

---

## Diagram 3 — Outbound Fan-out

```mermaid
sequenceDiagram
    participant GH as GatewayHost
    participant CR as ConversationRouter
    participant CM as ChannelManager
    participant CA as Channel Adapter

    GH->>CR: GetOutboundBindingsAsync(sessionId, originatingBindingId)
    note over CR: Excludes originating binding<br/>Returns Interactive + NotifyOnly only<br/>(Muted bindings filtered out)
    CR-->>GH: [ChannelBinding, ...]

    loop for each binding
        GH->>CM: ResolveChannelAdapter(binding.ChannelType)
        CM-->>GH: IChannelAdapter (or null → skip)

        GH->>CA: SendAsync(OutboundMessage with binding fields)

        alt StaleChannelConnectionException
            CA--xGH: throws StaleChannelConnectionException
            GH->>CR: MuteBindingAsync(conversationId, binding.BindingId)
            note over GH: Self-heal: demote dead binding<br/>to Muted so future fan-outs skip it
        end
    end
```

---

## Diagram 4 — Channel Extension Lifecycle

```mermaid
sequenceDiagram
    participant EL as Extension Loader
    participant CM as ChannelManager
    participant CA as Channel Adapter
    participant D  as IChannelDispatcher
    participant GH as GatewayHost

    note over EL: Startup
    EL->>CM: Register IChannelAdapter via IAgentToolContributor
    CM->>CA: StartAsync(IChannelDispatcher)
    note over CA: Adapter now listening on its channel

    note over CA,GH: Inbound path
    CA->>D: dispatcher.DispatchAsync(InboundMessage)
    D->>GH: DispatchAsync(message)

    note over GH,CA: Outbound path
    GH->>CA: adapter.SendAsync(OutboundMessage)

    note over EL: Shutdown
    EL->>CA: StopAsync()
    note over CA: Adapter stops listening
```

---

## Strong Types — Status

All channel-addressing fields have been migrated to strong types (completed in PR #171/#172/#173).

| Field / Parameter | Type | Notes |
|---|---|---|
| `ChannelBinding.ChannelAddress` | `ChannelAddress` | Value type; `ChannelAddress.Empty` for addressless channels (e.g. portal SignalR) |
| `ChannelBinding.ThreadId` | `ThreadId?` | Nullable; `ThreadId.FromNullable()` for optional thread context |
| `InboundMessage.ChannelAddress` | `ChannelAddress` | Required on all inbound messages |
| `InboundMessage.ThreadId` | `ThreadId?` | Nullable thread context |
| `OutboundMessage.ChannelAddress` | `ChannelAddress` | Required on all outbound messages |
| `OutboundMessage.ThreadId` | `ThreadId?` | Nullable thread context |
| `StaleChannelConnectionException.ConversationId` | `ConversationId` | Was `string` — now strong type |

### Remaining string boundaries

| Field | Location | Note |
|---|---|---|
| `InboundMessage.SenderId` | `InboundMessage` | Wire-level audit/allow-list token (channel-native, e.g. SignalR connection id). Phase 2c (#526) added the companion `InboundMessage.Sender` of typed `CitizenId`; the legacy hand-rolled `SenderId` value-object struct was deleted in the same change since the typed identity now lives on `Sender`. |
| `CrossWorldRelayRequest.ChannelAddress` | DTO | Intentionally string for HTTP wire format |
| Streaming `conversationId` | `IStreamEventChannelAdapter` | Channel-specific encoding; strong type would need format changes |

---

## Conversation.Initiator (provenance)

Phase 2b (#529) added `Conversation.Initiator` of type `CitizenId?` to record **who first
caused the conversation to be created**. Distinct from `Conversation.AgentId` (current
agent owner): `Initiator` is **write-once at creation time** and never overwritten — even
when an archived conversation is re-opened or the agent assignment changes.

**Stamping path:**

| Producer | Initiator value | Source |
|---|---|---|
| Channel-driven inbound (SignalR, Telegram, ServiceBus, TUI, ...) | `inbound.Sender` (always a `CitizenId`) | `DefaultConversationRouter` via `GatewayHost` / `DefaultConversationDispatcher` |
| `ConversationTool.NewAsync` (agent-initiated) | `CitizenId.Of(agentId)` | `ConversationTool` |
| `HeartbeatTrigger` / `CronTrigger` (system-initiated for an agent) | `CitizenId.Of(agentId)` | trigger code |
| `ConversationsController.Create` (REST admin path) | `null` (no trustworthy citizen until #527 lands) | controller; left explicit with `// pending SignalR claims-based auth (#527)` |

**Write-once discipline:** the router only stamps `Initiator` on the create-new-conversation
branch (Diagram 2 box F). Existing conversations — including the archived-reopen and
`explicit-ConversationId` fast-path branches — preserve the original `Initiator` even if
the current inbound message carries a different `Sender`.

**Query path:** `IConversationStore.ListForCitizenAsync(CitizenId)` returns the **union**
of `Initiator == citizen` and (when the citizen is an agent) `AgentId == citizen.AsAgent`.
For user citizens the predicate degenerates to the initiator leg only — users do not own
conversations. SQLite implements this with a single `WHERE` clause so duplicates are
naturally deduplicated by the row identity.
