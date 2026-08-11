# Conversation Provenance: Kind, Source, Visibility, Initiator, Status

Every conversation in BotNexus carries five **orthogonal** pieces of provenance. Each
answers a different question, and none of them is derivable from the others. Together
they are the complete, authoritative answer to "what is this conversation?" — and they
replace every form of per-surface inference that used to guess at it.

| Axis | Field | Answers | Type |
|---|---|---|---|
| **Topology** | `Conversation.Kind` | *who is talking to whom* | `ConversationKind` |
| **Trigger** | `Conversation.Source` | *why does it exist* | `ConversationSource` |
| **Visibility** | `Conversation.Visibility` | *who may see it* | `ConversationVisibility` |
| **Identity** | `Conversation.Initiator` | *which citizen opened it* | `CitizenRef` |
| **Lifecycle** | `Conversation.Status` | *active or archived* | `ConversationStatus` |

## Why five axes and not one

The temptation is always to collapse these into a single "type" field. Doing so loses
information, because the axes genuinely vary independently:

- A cron-driven run, an inbound-webhook run, and a user DM are **all**
  `Kind = HumanAgent`. `Kind` alone cannot distinguish them — only `Source` can.
- An agent-to-agent converse and a sub-agent supervision thread are **both**
  `Source = Agent`. `Source` alone cannot distinguish them — only `Kind` can
  (`AgentAgent` vs `AgentSubAgent`).
- `Initiator` says *which* citizen opened the conversation. It does **not** say what
  triggered it: the cron scheduler opens a conversation on an agent's behalf, so the
  initiator is an agent while the trigger is a schedule.
- `Status` is pure lifecycle and is the only one of the four that legitimately changes
  over the conversation's life.

`(Kind, Source)` together fully disambiguate every origination case. That is why
`ConversationSource` is deliberately **coarse** — a fifth value such as "SubAgent" would
re-introduce overlap between the trigger axis and the topology axis.

## `ConversationKind`

```csharp
public enum ConversationKind
{
    HumanAgent    = 0,   // a human talking to one or more named agents - back-compat default
    AgentAgent    = 1,   // two named agents in a peer exchange (IAgentExchangeService.ConverseAsync)
    AgentSubAgent = 2,   // a named agent supervising a spawned sub-agent
    Ralph         = 3,   // a self-retriggering autonomous work loop (issue #2818)
}
```

`HumanAgent = 0` is first so the enum's default-value contract makes it the back-compat
value. Members are persisted numerically and are **never renumbered**; a new kind is added
at the end with the next free explicit number.

### `Ralph`

A ralph conversation owns a set of instructions and re-triggers itself: when an agent turn
inside it ends, the gateway starts a **fresh session** in the same conversation, seeded with
those instructions, until a gateway-enforced stop condition fires.

- **Fresh session per iteration, not a growing one.** Each iteration mints a new session id
  (prefix `ralph`) and starts with empty history, so a fact stated only in iteration N's
  transcript cannot reach iteration N+1. This is structural, not a convention: it stops the
  loop's behaviour becoming a function of accumulated context, and stops it compacting.
- **Instructions are re-read every iteration.** Editing a ralph conversation's instructions
  changes the next iteration's prompt without recreating the conversation.
- **Turn-end driven, not timer driven.** The loop subscribes to the existing turn-end
  lifecycle event, so a turn still running (for example one waiting on a sub-agent) simply
  has not published yet and the loop does not re-trigger. There is no missed-wake class.
- **Topologically an agent talking to itself**, which is why it is a `Kind` and not a
  `Source`: the trigger is still whatever minted the conversation.
- **Unattended by construction.** `ConversationRenderProjection` treats `Ralph` alongside
  `AgentAgent`/`AgentSubAgent` as unattended and read-only - each iteration is a new session,
  so anything typed would be discarded rather than answered.

#### Stop conditions

Enforcement is the gateway's job, never the prompt's: an instruction asking the agent to stop
has no enforcement and no retry if a turn ends early. Bounds live in `RalphLoopConfig`, stored
in the conversation's metadata under the `ralph` key, and every stop is attributable to one
named `RalphStopReason` recorded with human-readable disclosure text.

| Bound | Default | Stop reason |
|---|---|---|
| `MaxIterations` | `null` (unbounded by count) | `MaxIterations` |
| `MaxDurationMinutes` | `null` (unbounded by time) | `MaxDuration` |
| `MaxConsecutiveFailures` | `3` | `Failed` |

`MaxIterations` and `MaxDurationMinutes` are enforced **independently**; whichever binds
first stops the loop and is named in the recorded reason. The other reasons are `Paused` (the
agent signalled "nothing to do" from within a turn; resumable), `Killed` (an external kill
switch - explicit stop, disable or archive - which cancels the in-flight iteration rather
than awaiting it), `NotActive` (the conversation is archived), `NoInstructions` (nothing to
seed the next iteration with), and `NotRalph`. `None` is the continue decision and never
appears on a decision that halts.

The decision is made in exactly one place, `RalphLoopPolicy.Evaluate`, called once per turn
end. Loop state (`Iterations`, `StartedAt`, `ConsecutiveFailures`, `IsPaused`, `IsKilled`,
`StopReason`, `StopDetail`) is durable rather than in-context, so continuity survives a
gateway restart - each iteration inherits no transcript.

Unreadable or absent ralph metadata degrades to `RalphLoopConfig.Default` plus the initial
state rather than throwing: an unparseable blob must not make a conversation unloadable.

## `ConversationSource`

```csharp
public enum ConversationSource
{
    Channel = 0,   // user/channel-driven (portal, Telegram, Signal, SMS, REST) — back-compat default
    Cron    = 1,   // schedule / heartbeat driven
    Webhook = 2,   // inbound webhook POST
    Agent   = 3,   // agent-initiated: conversation_new, peer converse, sub-agent spawn
}
```

`Channel = 0` is first so the enum's default-value contract makes it the back-compat
value: every conversation row persisted before the field existed deserializes to
`Channel` with no migration step. This mirrors `ConversationKind.HumanAgent = 0`.

`Source` is stamped **once**, at creation, by whichever origin path mints the
conversation, and is `init`-only thereafter.

## `ConversationVisibility`

```csharp
public enum ConversationVisibility
{
    UserFacing          = 0,   // shown in the user's list and fully interactive — back-compat default
    InspectableReadOnly = 1,   // visible but never writable: an observer/audit view
    InternalHidden      = 2,   // runtime bookkeeping, filtered out of user-facing listings entirely
}
```

Visibility (issue #2340) is a genuinely separate axis: a runtime bookkeeping thread and a
user's own DM can share identical `Kind`, `Source`, `Initiator` and `Status` values and
still differ on whether the sidebar should render them.

`UserFacing = 0` is first so the enum's default-value contract makes it the back-compat
value — every row persisted before the field existed deserializes to `UserFacing` and
stays visible, the same contract `Source = Channel` already uses. Like `Source`, it is
stamped once by `ConversationFactory` and is `init`-only thereafter, so no inbound event
can re-stamp it and make a hidden bookkeeping thread appear (or a user's conversation
disappear).

`InspectableReadOnly` is deliberately distinct from both neighbours: distinct from
`UserFacing` so a surface can render the row while suppressing every write affordance, and
distinct from `InternalHidden` so the row is not silently dropped from the list. It is
independent of the read-only gating derived from `Source`/`Kind` — a conversation can be
read-only for either reason.

## The contract

> **Surfaces render from `(Kind, Source, SelectionSource)`.
> Surfaces NEVER infer origin from an id string.**

Both halves are load-bearing.

### Render from the projection

`ConversationRenderProjection` is the single place the decision is made. Given the
conversation's immutable `(Kind, Source)` and the store's current `SelectionSource`, it
returns:

| Property | Meaning |
|---|---|
| `IsUnattended` | no human citizen participates, so nothing typed could ever be delivered |
| `IsReadOnly` | unattended, **or** the view was promoted by the "view sub-agent" observer action |
| `ShowComposer` | exactly `!IsReadOnly` |
| `Group` | `Normal` / `Scheduled` / `Automated` / `AgentInitiated` |
| `Badge` | `null` / `"Cron"` / `"Webhook"` / `"Read-only"` |

The truth table is total: there is no fallthrough to inference.

**Cron conversations are read-only because `Source == ConversationSource.Cron`.** That
single fact flows through `IsUnattended` → `IsReadOnly` → composer suppression. It is
covered by explicit tests precisely because it is the behaviour most likely to regress
silently if someone reintroduces a mutable origin flag.

### Every input is immutable

`ConversationState.Source` and `.Kind` are `init`-only on the client and seeded straight
from the server payload; `SelectionSource` is a read-only projection of the store's
single view-selection value (see [epic #2245's single-writer `SelectView` seam]). No
inbound SignalR event can move any of them.

This matters because the alternative already bit us. Before this model, read-only gating
ORed in a mutable `IsVirtualSession` flag that inbound events could write. That is the
same defect class as #2248, where a mutable agent flag let an inbound event make an agent
vanish from the dropdown and revert the user's selection. With immutable inputs, a
concurrent cron or sub-agent event **cannot** hide a user's composer, and cannot make a
cron conversation writable.

### Never infer from an id string

The following are all **banned** in production code and fenced by
`ConversationSourceArchitectureTests`:

```csharp
// ✗ banned — origin inferred from a conversation-id prefix
conversationId.StartsWith("cronconv:", StringComparison.OrdinalIgnoreCase)

// ✗ banned — origin inferred from a session-id prefix
activeSessionId.StartsWith("cron:", StringComparison.OrdinalIgnoreCase)

// ✓ correct — origin read from the modelled, server-stamped field
conversation.Source == ConversationSource.Cron
```

Prefix sniffing is per-surface: the portal, the mobile client, and any future
rich-rendering chat channel each reimplement their own version, and they drift apart
silently. The typed signal is shared, so they cannot.

One thing that looks similar but is **not** origin inference, and remains legitimate:

- **`cron:` *session* ids inside the cron subsystem.** `SessionId.IsCron`,
  `CronScheduler`, and `CronTrigger` mint and match their own session ids. That is a
  naming convention the cron subsystem owns for its own sessions, not an unrelated
  surface re-deriving a conversation's origin.

> **Superseded:** the portal formerly hid runtime bookkeeping threads by probing the
> conversation id for an `internal:` prefix. That was tolerated as a namespace reservation
> rather than origin inference, but it was still behaviour keyed on the *text* of an opaque
> identifier — a hidden coupling between id-minting code and rendering code, failing
> silently in both directions. Issue #2340 replaced it with the typed
> `ConversationVisibility` field, removing the last allowlisted exception from the
> origin-inference fence.

## The single enum declaration

`ConversationSource` and `ConversationKind` are declared **once**, in
`BotNexus.Gateway.Abstractions.Models` (`src/domain/BotNexus.Domain`), and the Blazor
client references that canonical declaration rather than re-declaring the wire values.

An earlier iteration mirrored both enums client-side, on the reasoning that the client is
a separate deployment unit. That is a duplicated contract that can drift silently: adding
a value server-side fails no client build — it quietly degrades to the tolerant-parse
fallback and renders wrong. Since `BotNexus.Domain` is a pure model assembly (its only
package reference is the compile-time Vogen source generator), referencing it costs the
client a model dependency, not a gateway-host dependency.

**Tolerant parsing is kept anyway.** Sharing the enum removes *drift*; it does not remove
*version skew*, because a deployed client can still be older than the server it talks to.
`ConversationOrigin.ParseSource` / `ParseKind` are therefore total: an unknown, empty, or
absent wire value falls back to the back-compat default (`Channel` / `HumanAgent`) rather
than throwing. Forward-compatibility and a shared contract are complementary, not
alternatives.

## Row provenance is not conversation origin

`ConversationState.IsLocallySynthesised` (client-only, `init`-only) marks a row the
*client* minted rather than one the server enumerated — today, only the sub-agent
observer transcript row created by "view sub-agent". Such a row reads raw session history
instead of merged conversation history, is never back-paged, and survives a server-driven
list reconciliation.

That is a statement about **who created this row object**, not about **why the
conversation exists**. Origin is answered solely by `Source`/`Kind`. Keeping the two
concepts separate is what stopped the deleted `IsVirtualSession` flag from growing back
under a new name.

## Architecture fences

`tests/architecture/BotNexus.Architecture.Tests/ConversationSourceArchitectureTests.cs`
fails the build on any of:

1. `Conversation.Source` or client `ConversationState.Source`/`.Kind` gaining a public
   `set` setter instead of `init`.
2. Any client surface probing an id string for a `cron:` / `cronconv:` origin prefix.
3. The identifiers `IsVirtualSession` or `VirtualSessionKind` appearing anywhere under
   `src/` or `tests/`.

Rule 2 carries an explicit anti-vacuity test asserting the detector both matches every
probe shape that was deleted and does *not* match benign id handling — a fence that
matches nothing is not a fence.

## History

- Issue #2340 added `ConversationVisibility` as a fifth axis, replacing the client-side
  `internal:` conversation-id prefix probe with a typed, server-stamped field.
- Epic #2300 introduced `ConversationSource`, stamped it at every origin path, exposed it
  on the DTOs and SignalR payloads, projected it client-side, deleted the inference, and
  fenced the result.
- Epic #2245 established the single-writer `SelectView` seam and route-owned selection
  that `SelectionSource` depends on.
- Issue #2248 made `AgentState.IsObserverAgent` immutable — the agent-shaped version of
  the same defect class.
