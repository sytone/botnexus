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
- [ ] The test project is registered in `BotNexus.slnx` so the gate actually runs it.

## Scope today

Conversations only. Sessions, cron jobs, webhook registrations/runs and configuration writers are
tracked as follow-up work, along with an architecture test that flags new broad aggregate updates
in high-risk services.
