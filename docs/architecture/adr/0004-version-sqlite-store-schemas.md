# ADR-0004: Version SQLite store schemas and migrate forward only

- **Status**: Accepted
- **Date**: 2026-08-17
- **Deciders**: BotNexus platform
- **Issue**: [#2835](https://github.com/Sytone/botnexus/issues/2835)

## Context

BotNexus writes to fourteen SQLite stores and used `PRAGMA user_version` in **none** of them. A
survey on 2026-08-05 found `git grep user_version -- 'src/**/*.cs'` returned zero hits across the
repository; the only `SchemaVersion` constants that existed govern JSON documents
(`AgentTemplate`, `ConfigSchemaBuilder`), not databases.

Nothing recorded which schema a store was written by, which leaves two failure modes:

- **Roll forward** — new code expecting a new column against an old store fails at query time with
  a SQLite error naming a missing column. That happens at whatever moment the query first runs, not
  at startup, and the message does not name the real cause.
- **Roll back** — old code against a store written by newer code is worse. If the newer schema only
  *added* things, the old code reads it successfully, ignores the new columns, and writes rows that
  the newer code will later read as incomplete. **There is no error at all.** This is the dangerous
  direction and it was completely undefended.

## Decision

Every BotNexus SQLite store records a `schema_version` in the `store_meta` table introduced by
[ADR-0003](0003-stamp-world-identity-on-sqlite-stores.md), mirrored into `PRAGMA user_version`.
Each store declares its current version as a constant next to its DDL and supplies an ordered,
forward-only migration set. `SqliteSchemaMigrator.Apply` is called at store initialisation, **after**
the identity check has passed.

Rules, in order:

| Store state | Action |
|---|---|
| No recorded version, store empty | Bootstrap directly to the code's version. No migrations replayed. |
| No recorded version, tables present | Adopt the code's version as the baseline. No migrations replayed. |
| Recorded version **equals** code version | Proceed. Repair `user_version` if it lags. |
| Recorded version **below** code version | Run the intervening migrations in order inside one transaction, then write both slots. |
| Recorded version **above** code version | Throw, naming both versions and the store path. |

### Why the version is recorded twice

`store_meta` is the value BotNexus reads and writes and is the authority. `PRAGMA user_version` is
the idiomatic SQLite slot — free, atomic, and readable by any external tool without knowing the
BotNexus table layout. Both are written in the same transaction so they cannot diverge across a
crash, and a lagging pragma is repaired on the next open.

### Why identity and version stay separate

Their mismatch semantics are opposite. Identity mismatch is always a bug and must never
auto-recover; version mismatch is expected during a deployment and must migrate forward
automatically. Fusing them into one token would make a legitimate rollback and a wrong-world open
produce the same error, leaving the operator unable to tell which happened.

### Why there are no down-migrations

A rollback is handled by restoring a backup. Pretending a schema change can be losslessly reversed
invites exactly the data loss it claims to prevent. A store ahead of the code is therefore a loud,
actionable stop, not a silent partial read.

## Consequences

- A rollback onto a newer store becomes a **startup failure** instead of silent row corruption.
- The whole migration step — every schema change plus both version stamps — runs in one transaction.
  SQLite DDL is transactional and the `user_version` header write is journaled, so "half-migrated" is
  unrepresentable rather than merely unlikely.
- Existing stores adopt the current version as their baseline. Historical migrations for shapes
  already shipped are **not** backfilled; replaying them would re-run steps whose effects are present.
- A migration targeting a version beyond the store's declared constant is **rejected at the call
  site**, so forgetting to bump the constant fails immediately rather than shipping a step that can
  never run. Duplicate target versions are rejected for the same reason: the resulting schema would
  depend on declaration order, which is not a decidable migration history.
- In-memory stores are not versioned, matching ADR-0003's treatment of the same case.
- Store adoption is incremental. This slice lands the mechanism and adopts it in
  `SqliteUsageTelemetryStore`; the remaining stores adopt it in follow-up work, each declaring its
  own baseline constant.

## Explicitly out of scope

- **World identity stamping** — ADR-0003; this ADR consumes the `store_meta` table it introduced.
- **Down-migrations / automatic schema rollback.**
- **Backfilling historical migrations** for schema changes already shipped.
- **Non-SQLite stores.**
