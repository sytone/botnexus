# Compat Shim Lifecycle: Migrate Forward, Then Delete

When the persisted model changes, the compatibility path you add to keep old data
loading is a **time-boxed migration step, not a permanent runtime branch**.

This convention exists because `LegacyConversationResolver` (issue #615) violated it.
A one-time backfill for sessions persisted before `Session.ConversationId` was
guaranteed non-null was still an always-on component on the session load path months
after the migration issue closed. In that time it accrued a per-agent `SemaphoreSlim`,
cross-process race caveats, first-wins active-session binding, a security guard against
XPIA title-impersonation, and an obligation to be updated by every subsequent
conversation-model change (see PR #2308, which had to stamp it with a provenance value
it could not actually know). A one-time migration had become permanent architecture.

## The rule

> **Honour the original defaults for existing data, migrate forward eagerly and
> completely, then DELETE the compatibility path.**

## What that means concretely

### 1. Ship a one-shot forward migration, not lazy resolve-on-read

The migration runs **once** - at startup, or as an explicit operator-invoked step - and
sweeps *all* affected rows. It does not wait to be triggered by a read.

Lazy resolve-on-read feels cheaper because it touches only the data you actually load.
It is more expensive in the long run: it can never complete, so it can never be removed,
and it puts migration logic permanently on a hot path.

A forward migration must be **idempotent** and safe to run on every startup - a no-op
when there is nothing left to migrate.

### 2. Back-compat defaults make existing persisted data load - nothing more

A default exists so a row written under the old schema can be materialised. It must not
become a supported way for **new** writes to keep taking the old path. If new data can
still arrive in the legacy shape, you have not migrated the model; you have forked it.

### 3. File the removal issue at the same time as the shim

Every compat shim gets a removal issue created **in the same PR that introduces the
shim**, referencing the migration that obsoletes it. A shim merged without a removal
issue has no scheduled end, and shims without an end date become architecture.

The shim's own doc comment should name that issue, so anyone reading the code knows it
is scheduled for deletion and why.

### 4. Instrument the shim so "is it dead yet?" is answerable

You cannot delete a shim you cannot prove is unused, and you cannot prove it from the
source tree alone - the question is about *data in live environments*, not call sites.
Add activation telemetry when you add the shim:

- Count every activation, attributed to the **call path** that caused it. Startup-sweep
  activity is the migration working as intended; load-path or save-path activity means
  unmigrated data is still arriving.
- Log at `Warning`, not `Information`, when the shim does real work after its migration
  is nominally complete. It is an anomaly by then, not routine.
- Make a snapshot readable so an operator can answer the deletion question directly.

`LegacyConversationTelemetry` in `BotNexus.Gateway.Sessions` is the reference
implementation of this step.

### 5. Delete the shim once the forward migration is confirmed complete

"Confirmed" means telemetry from real environments shows zero activations - not that the
migration code looks right. When you delete, remove the whole surface:

- the shim type and every call site
- its locks and concurrency machinery
- its **security guards** - these exist only to defend the shim, and they are pure
  attack surface once it is gone
- its telemetry
- its tests

Per `AGENTS.md`, tests are never net-deleted. If the shim's behaviour has a replacement,
its tests are migrated to the replacement. If the behaviour is genuinely gone (the whole
point of deleting a completed migration), the tests go with it - but the architecture
test that pins the *invariant* the migration established must remain. For #615 that
invariant is "every persisted session carries a real conversation id", pinned by
`SessionConversationIdNonNullableArchitectureTests`; it outlives the resolver that
established it.

## Checklist

When introducing a compat shim:

- [ ] One-shot forward migration ships with it (idempotent, sweeps everything)
- [ ] Back-compat default only makes existing data load; new writes cannot take the old path
- [ ] Removal issue filed in the same PR, referenced from the shim's doc comment
- [ ] Activation telemetry, attributed by call path, with `Warning`-level logging
- [ ] Deletion plan stated: what evidence will authorise removal

When removing one:

- [ ] Telemetry from live environments shows zero activations
- [ ] Type, call sites, locks, security guards, and telemetry all removed together
- [ ] Tests migrated to the replacement, or removed with the behaviour
- [ ] The invariant the migration established is still pinned by an architecture test

## Related

- #2311 - retire `LegacyConversationResolver`, adopt this convention
- #615 - the original Phase 9 / P9-B conversation-id backfill
- #2310 / PR #2321 - the single conversation-creation seam the resolver now mints through
