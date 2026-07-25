# Conversation Provenance: Kind, Source, Initiator, Status

Every conversation in BotNexus carries four **orthogonal** pieces of provenance. Each
answers a different question, and none of them is derivable from the others. Together
they are the complete, authoritative answer to "what is this conversation?" — and they
replace every form of per-surface inference that used to guess at it.

| Axis | Field | Answers | Type |
|---|---|---|---|
| **Topology** | `Conversation.Kind` | *who is talking to whom* | `ConversationKind` |
| **Trigger** | `Conversation.Source` | *why does it exist* | `ConversationSource` |
| **Identity** | `Conversation.Initiator` | *which citizen opened it* | `CitizenRef` |
| **Lifecycle** | `Conversation.Status` | *active or archived* | `ConversationStatus` |

## Why four axes and not one

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

Two things that look similar but are **not** origin inference, and remain legitimate:

- **`internal:` id namespacing.** Hiding runtime-internal bookkeeping threads is an
  explicit namespace reservation on the conversation id. It answers "is this row a
  runtime artefact?", never "why does this conversation exist?".
- **`cron:` *session* ids inside the cron subsystem.** `SessionId.IsCron`,
  `CronScheduler`, and `CronTrigger` mint and match their own session ids. That is a
  naming convention the cron subsystem owns for its own sessions, not an unrelated
  surface re-deriving a conversation's origin.

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

- Epic #2300 introduced `ConversationSource`, stamped it at every origin path, exposed it
  on the DTOs and SignalR payloads, projected it client-side, deleted the inference, and
  fenced the result.
- Epic #2245 established the single-writer `SelectView` seam and route-owned selection
  that `SelectionSource` depends on.
- Issue #2248 made `AgentState.IsObserverAgent` immutable — the agent-shaped version of
  the same defect class.
