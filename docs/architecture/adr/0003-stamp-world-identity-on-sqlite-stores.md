# ADR-0003: Stamp a world identity on every SQLite store and verify it on open

- **Status**: Accepted
- **Date**: 2026-08-11
- **Deciders**: BotNexus platform
- **Issue**: [#2833](https://github.com/Sytone/botnexus/issues/2833)

## Context

Every BotNexus SQLite store is located by path alone. Opening a SQLite file by path *always*
succeeds - if the file does not exist, SQLite creates it - so a path that resolves to the wrong
place produces no error at any layer.

This is not hypothetical. In [#2819](https://github.com/Sytone/botnexus/issues/2819),
`CronServiceCollectionExtensions.ResolveRootPath` located `BotNexusHome` through an
assembly-qualified **string** passed to `Type.GetType(...)`. A refactor moved the type to a
different assembly, the lookup began returning `null` at runtime, nothing failed at compile time,
and the resolver fell through to a hard-coded `~/.botnexus` default. E2E-test gateways launched
with an isolated `--target` home opened the *live* `cron.sqlite` and wrote 177 phantom cron jobs
and 1,474 poisoned run rows into production state.

Those instances' `config.json` isolation worked correctly. Only the store path escaped. A
configuration-level check would not have caught it.

The general defect: **path resolution failing today means you quietly get production data.** That
is the worst available default.

## Decision

Every BotNexus SQLite store carries a `store_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL)`
table holding `world_id`, `store_kind`, `created_at` and `created_by_version`. The identity is
verified at connection time, and a store belonging to another world is **refused**.

Verification lives at the `SqliteConnectionFactory` seam established by
[#1541](https://github.com/Sytone/botnexus/issues/1541), not in the individual stores. The factory
is already the single answer to "how is a BotNexus SQLite connection opened", so a store type added
tomorrow with no identity code of its own is still verified - it cannot open a connection without
passing through the guard.

Rules, in order:

| Store state | Action |
|---|---|
| No `store_meta`, no other tables | Stamp it (bootstrap). Silent. |
| No `store_meta`, other tables present | Adopt into the configured world, stamp, log **one** warning naming the path. |
| Stamped, `world_id` matches | Proceed. |
| Stamped, `world_id` differs | Throw, naming both world IDs, the store path and the resolved home. |
| Stamped, `store_kind` differs | Throw. Catches a swapped path such as opening `sessions.db` as the cron store. |

The world ID is read from configuration **once** at startup
([#2834](https://github.com/Sytone/botnexus/issues/2834)) and threaded through as a single injected
value. It is never re-derived per store. If identity and path were derived independently by the
same broken resolver they would fail consistently, both answers would agree, the guard would pass,
and the data would still be wrong - the one-value-two-derivations family behind #2796, #2792 and
#2748.

## Consequences

- A mis-resolved store path becomes a **startup failure** instead of silent cross-world corruption.
  This is the whole point: an operator sees the fallback immediately rather than inferring it from
  poisoned data days later.
- Identity mismatch **never** auto-recovers. "Recovering" would mean writing into another world's
  production data.
- Every existing store on every machine is unstamped, and is adopted into its configured world on
  first open with a one-time warning. Adoption is unavoidable: whether an unstamped store is
  legitimate pre-existing data or the wrong file is not knowable from inside the process. Stamping
  makes every *subsequent* open decidable, which is the property that matters.
- In-memory stores are not stamped. They have no path that can be mis-resolved - the failure mode
  being guarded does not exist for them - and stamping them would add a table every schema-shape
  assertion would then have to know about.
- Where no identity is configured the guard is **inert**. Tools and hosts that have not opted in
  behave exactly as before.

## Explicitly out of scope

- **Schema versioning and migration.** Identity and version have opposite mismatch semantics:
  identity must never auto-recover, version must migrate forward. Fusing them makes a legitimate
  rollback indistinguishable from a wrong-world open.
- **Non-SQLite stores** - `FileSessionStore`, the markdown memory tree, agent workspaces.
- **Generating and persisting the `worldId` config value** - that is #2834; this ADR consumes it.
