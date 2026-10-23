# Durable peer-dialogue admission

The peer-dialogue admission store is a backend foundation for interactive agent conversations.
It does **not** enable an asynchronous peer tool, dispatch a message, wake an agent, or return a
peer's answer. The existing `agent_converse` tool remains a blocking exchange.

## What the store guarantees

`IPeerDialogueStore` pins two agent-owned ordinary Conversation endpoints, an originating endpoint,
a stable dialogue Conversation ID, an absolute expiry, and a finite automatic-message limit.
Session IDs are not permanent routing identities.

Each accepted message has a client-assigned `PeerDialogueMessageId`, the complete immutable
sender/recipient envelope, optional reply-to identity, exact bounded content, and a one-based
sequence. One SQLite transaction reserves a dialogue-wide turn and stores the message as
`Pending`. Concurrent store instances cannot independently consume the same final turn.

- Identical authenticated retries return the original admission without another reservation.
- Reusing a message identity with changed immutable fields is a conflict.
- Reply-to must identify a message in the same dialogue sent by the opposite endpoint to the
  replying endpoint.
- Either member can close admission. Closure is irreversible and idempotent.
- Expired, closed, or exhausted dialogues reject new messages, while accepted retries remain
  available for reconciliation.
- Pending enumeration is recipient-scoped and paged by exclusive sequence cursor, with a limit
  of 1–100 records. It describes retained admission evidence, not permission to execute work.

Messages are limited to 16,384 UTF-16 code units. Endpoint identifiers are limited to 256 code
units at this boundary. Invalid inputs and unauthorized callers receive typed rejections without
message payloads in error text.

## Trust and storage boundaries

The caller must authenticate the actual agent, authorize access to both endpoint Conversations,
and provision those Conversations before using this store. Passing an `AgentId` is not credential
authentication. The store checks pinned endpoint membership; it does not resolve registration,
current access policy, Conversation existence, or ownership from other stores.

`SqlitePeerDialogueStore` uses an explicitly supplied durable database connection string and the
shared SQLite connection, journaling, and schema-version helpers. No live database is created or
opened merely by registering an agent. There is intentionally no dependency-injection registration
or runtime producer in this foundation.

## Admission is not execution

`Pending` means a message and its turn reservation are durable. It does not mean an agent has
started, a tool has run, a response exists, or an external side effect can safely be retried.
Pending evidence remains inspectable after dialogue closure or expiry; a future dispatcher must
check lifecycle and current authorization before execution.

The next runtime integration must add fenced delivery/start/completion transitions, correlated
reply intents, safe busy/idle delivery, restart reconciliation, cancellation, and execution
resource limits. It must not replay uncertain external effects or replenish shared budgets on
restart. A caller waiting for a peer must not occupy an executor merely to wait.

The broader collaboration scenario also requires invitation, actual subsequent-message fan-out,
addressed recipients, producing-speaker provenance, and desktop/mobile participant and thread UI.
None of those outcomes is implied by this store or by participant metadata alone.

Implementation and acceptance tracking: [collaboration parent #5044](https://github.com/sytone/botnexus/issues/5044),
[admission foundation #5045](https://github.com/sytone/botnexus/issues/5045),
[bilateral runtime #5048](https://github.com/sytone/botnexus/issues/5048),
[group routing #5046](https://github.com/sytone/botnexus/issues/5046), and
[shared-thread UI #5047](https://github.com/sytone/botnexus/issues/5047).
