# Persistence Seam Testing

How to prove an aggregate cannot silently lose an update, and the checklist to run before
shipping a new persistence feature. Introduced by issue #2130.

## Why this exists

The webhook conversation-pin regression shipped green. The controller had tests — against a
**mocked** store. The SQLite store had tests — **in isolation**. Neither half could see the seam
between them, and the bug lived exactly there: the controller read a conversation, something
pinned it, and the controller then wrote its pre-pin snapshot back and silently unpinned it.

A mock cannot regress a guarantee that lives in SQL. If the invariant you care about is
implemented by a `WHERE version = $expected` clause or an `INSERT OR IGNORE`, a test double
re-implements the invariant instead of testing it.

## The shape of a lost update

Three steps, in this order:

1. a caller **reads** an aggregate, producing a detached snapshot;
2. an independent, narrower operation **commits** a change to part of that aggregate;
3. the caller **writes** its snapshot back, carrying the pre-change value.

Step 2 must be *observed* to happen between 1 and 3, or the test proves nothing.

## Write classification

Before writing a seam test, classify every mutation entry point on the store. A lost update is
only possible where a broad write can interleave with an independent narrower one, so the
classification tells you which pairs are worth testing.

| Classification | Meaning | Lost-update risk |
| --- | --- | --- |
| `Create` | Inserts a new aggregate; fails if it exists | None |
| `FullReplace` | Rewrites every caller-owned column from a snapshot | **High** — this is the hazard |
| `NarrowPatch` | Writes only the columns it owns | Low; but it is the *victim* of a `FullReplace` |
| `Merge` | Additive, idempotent collection merge | None by construction |
| `CompareAndSwap` | Conditional on the revision the snapshot was read at | Guarded |
| `Fenced` | Guarded by an external lease/session token | Guarded |

Keep the inventory **executable**, not prose. `ConversationWriteInventoryTests` reflects over
`IConversationStore` and fails when a new mutation entry point appears without a classification.
A markdown table drifts silently; a reflection test does not.

### Conversations aggregate (worked example)

| Entry point | Class | Owns |
| --- | --- | --- |
| `CreateAsync` | Create | the new row |
| `SaveAsync` | FullReplace + CAS | title, purpose, status, active session, metadata, instructions, canvas **html**, todo, pending prompt, overrides, pin columns, and the **entire** binding set |
| `PinAsync` | NarrowPatch | `is_pinned`, `pinned_at` |
| `ArchiveAsync` | NarrowPatch | `status`, `active_session_id` |
| `TouchAsync` | NarrowPatch | `updated_at` only — deliberately does **not** bump the revision |
| `PatchMetadataAsync` / `PatchOverrideAsync` | NarrowPatch | only fields marked set |
| `AddParticipantsAsync` | Merge | `conversation_participants` — **not** written by `SaveAsync` at all |
| `Add`/`Remove`/`MoveBindingAsync` | NarrowPatch | one binding row |
| `*CanvasState*` | NarrowPatch | the `canvas_state` side table (distinct from `canvas_html`) |

Two guarantees of different strength appear here, and the distinction matters:

- **Revision CAS** protects fields `SaveAsync` *does* write. A stale save is refused with
  `ConversationConcurrencyException`.
- **Structural separation** protects state `SaveAsync` cannot write at all (participants, canvas
  state). Those survive even when the interleaved save is legitimately *accepted*.

### Sessions aggregate (worked example)

Sessions separate the **deliberately unguarded session-row upsert** from history persistence.
`SaveAsync` still uses unconditional `INSERT … ON CONFLICT DO UPDATE` for the caller-owned
session columns: pre-run write-ahead saves (the user message, the crash sentinel) rely on it to
create the row. There is no revision column to compare against, so post-run finalizers still
need the fenced overload to protect lifecycle fields.

History no longer undergoes a whole-transcript delete/reinsert on each save (#3907). Ordinary
saves append only the captured unpersisted delta. Explicit destructive history mutations use
identity-based reconciliation: they update known rows and may delete only previously observed
row IDs recorded as removed, leaving a concurrent append outside that deletion authority. See
[`SqliteSessionStore.SaveAsync` and `PersistHistoryAsync`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway.Sessions/SqliteSessionStore.cs)
and the executable
[`SessionWriteInventoryTests`](https://github.com/Sytone/botnexus/blob/main/tests/persistence/BotNexus.Persistence.Seam.Tests/Sessions/SessionWriteInventoryTests.cs).

| Entry point | Class | Owns |
| --- | --- | --- |
| `GetOrCreateAsync` | Create | constructs only; nothing reaches SQLite until a save |
| `SaveAsync(session)` | FullReplace | caller-owned **session-row columns** remain unguarded; history uses append/targeted reconciliation, not whole-transcript replacement |
| `SaveAsync(session, fence)` | Fenced | same session columns and history delta, gated on `SessionFenceEvaluator.Passes` over a lock-scoped re-read |
| `AppendEntriesAsync` | NarrowPatch | new `session_history` rows + `updated_at`; refused against a terminal row |
| `PatchMetadataAsync` | Merge | the `metadata` column only, read-merge-written under one lock |
| `TransitionStatusAsync` | CompareAndSwap | `status` only, via `UPDATE … WHERE status = $expected` |
| `DeleteAsync` | NarrowPatch | removes the row and its history |
| `ArchiveAsync` | NarrowPatch (logical mutation) | seals status + advances `updated_at` via an authoritative row upsert after draining the run and re-loading **inside** the lock (#2903); history is untouched |
| `Save`/`UpdateSubAgentSessionAsync` | Create / NarrowPatch | `sub_agent_sessions` side table |

`ArchiveAsync` is classified as `NarrowPatch` for its logical mutation, **not** because it issues
a status-only SQL `UPDATE`. It drains the active run before taking the lock, then reloads the
session under that lock, changes status and `updated_at`, and calls `UpsertSessionAsync` without
persisting history. A drain timeout throws before any archive write.

The protections have distinct scopes:

- **Narrowing** (#2132) — append, metadata-patch and status-transition paths target their own
  state (plus `updated_at`), so they compose instead of clobbering. This is what callers should
  reach for when they only add turns or only edit metadata.
- **History delta/reconciliation** (#3907) — an accepted stale unfenced save preserves a
  concurrently appended history row. This does not guard the broad session-row upsert or make
  stale lifecycle writes safe.
- **Fencing** (#1518) — the post-run finalizer overload re-reads `(status, conversation_id)`
  straight from SQLite under the *same* striped lock it then writes under, and returns `Rebound`
  when the row was deleted, sealed by a competing reset, or rebound to another conversation while
  the run was in flight.

[`SessionLostUpdateSeamTests.StaleUnfencedSave_PreservesAConcurrentAppend`](https://github.com/Sytone/botnexus/blob/main/tests/persistence/BotNexus.Persistence.Seam.Tests/Sessions/SessionLostUpdateSeamTests.cs)
now asserts both that the stale unfenced save is **accepted** and that the concurrent turn
**survives**, verified through a fresh store. It supersedes the original characterisation of
transcript erasure; it does not assert that the session-row upsert has gained a guard.

The original sessions seam coverage shipped in
[PR #3452](https://github.com/Sytone/botnexus/pull/3452), including recorded proven-red evidence
for removing the fence-token check. That historical fence-mutation evidence is distinct from
the current append-preservation assertion; updating this guide does not constitute a new test
or mutation run.

## The harness

`tests/persistence/BotNexus.Persistence.Seam.Tests` provides two reusable pieces:

**`SeamGate`** — a single-shot rendezvous for ordering two concurrent arms. Its timeout is a
**deadlock detector, not a synchroniser**: firing it means the test is mis-wired, and it throws
`SeamDeadlockException` naming the gate rather than hanging.

**`LostUpdateScenario<T>`** — encodes read → concurrent mutation → stale write → verify. It
awaits the mutation to completion before the stale write, so "the mutation had already committed"
is a fact of the test rather than a hope about scheduling.

```csharp
var result = await new LostUpdateScenario<Conversation>()
    .ReadSnapshot(() => store.GetAsync(id))
    .ThenConcurrently(() => store.ArchiveAsync(id))
    .ThenStaleWrite(s => { s.Title = "renamed"; return store.SaveAsync(s); })
    .VerifyBy(() => fixture.CreateStore().GetAsync(id))   // FRESH store — see below
    .RunAsync();

result.Outcome.ShouldBe(StaleWriteOutcome.Rejected);
result.Committed!.ActiveSessionId.ShouldBeNull();
```

The harness deliberately **asserts nothing itself**. Which field must survive is per-seam domain
knowledge; a harness that guessed it would quietly weaken assertions.

## Rules

1. **Real store, real database file.** No mocks at the seam under assertion.
2. **Never order steps with a sleep.** `Task.Delay` makes an interleaving *likely*; on a loaded CI
   agent it degrades into a coin flip. Use gates.
3. **Verify through a fresh store instance.** The writing store has an in-process cache and will
   happily answer the verification read from memory — which is exactly the state a broken
   implementation would also report.
4. **Assert the observable, not the mechanism.** "The pin is still set" survives a refactor;
   "`Version == 3`" does not, and passes vacuously more often.
5. **Assert the loser was told.** A guard that refuses silently is indistinguishable from data
   loss to the caller. Assert the exception *and* the committed state.
6. **Assert the refusal is recoverable.** Re-read, re-apply intent, retry — and prove both the
   retried edit *and* the concurrent change are present afterwards. A guard that only refuses is
   an outage.
7. **Prove non-vacuity by mutation.** Break the guard in the code under test, watch the seam test
   go red, then revert. Never commit the mutation.

## Checklist for a new persistence feature

- [ ] Every mutation entry point is classified, in an executable inventory.
- [ ] Each field is labelled caller-owned, store-owned, or independently mutable.
- [ ] Any `FullReplace` write is guarded by CAS, a fence, or narrowed into a patch.
- [ ] State that must never be clobbered lives structurally outside the broad write.
- [ ] A deterministic lost-update seam test exists for every broad-vs-narrow pair.
- [ ] The refusal path is asserted, and so is the retry path.
- [ ] The test project is under `tests/` so `tests/dirs.proj` discovers it and the gate runs it.

## Scope today

Conversations and **sessions**. Cron jobs, webhook registrations/runs and configuration writers
are still uninventoried and untested, and remain tracked on issue #3327 along with an architecture
test that flags new broad aggregate updates in high-risk services. Each domain ships as its own
PR; #2130 closes only when all of them are covered.
