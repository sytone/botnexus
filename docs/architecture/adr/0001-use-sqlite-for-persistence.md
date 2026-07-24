# 0001. Use SQLite for platform persistence

- **Status:** Accepted
- **Date:** 2026-07-24
- **Deciders:** BotNexus Team

## Context and Problem Statement

The Gateway must durably persist sessions, conversations, agent memory, and usage
telemetry so that each (agent, channel) pair can rehydrate its history across restarts.
The platform runs on both Windows and Linux, on developer machines and small hosts, and
must not require an external database server to stand up a Gateway. What persistence
technology should back these stores?

## Decision Drivers

- **Zero-ops local run** — a Gateway must start without provisioning a separate DB.
- **Cross-platform** — identical behaviour on Windows and Linux.
- **Embeddable & file-based** — portable, easy to back up, easy to inspect.
- **Concurrent read/write** — multiple sessions and background maintenance run at once.

## Considered Options

- **SQLite (embedded, file-based)** — no server, cross-platform, WAL for concurrency.
- **A hosted RDBMS (PostgreSQL / SQL Server)** — richer, but requires provisioning and
  an always-on dependency.
- **Bespoke flat-file / JSON stores** — simple, but no transactions or query support.

## Decision

We will use **SQLite** as the platform persistence engine, encapsulated in
`src/persistence/BotNexus.Persistence.Sqlite`. Connections are created through a
`SqliteConnectionFactory` and databases are tracked by a `SqliteDatabaseRegistry`.
Write-Ahead Logging (WAL) is used for concurrency, with a hosted
`SqliteWalCheckpointHostedService` and `SqliteWalMaintenance` performing periodic
checkpoints. Usage telemetry is persisted via `SqliteUsageTelemetryStore`.

## Consequences

**Positive**

- A Gateway runs with no external database dependency on any supported OS.
- Stores are single files — trivially portable, inspectable, and backup-friendly.
- WAL mode gives good concurrent read/write throughput for the platform's workload.

**Negative / costs**

- SQLite is single-writer; heavy write contention must be managed (hence WAL checkpoint
  maintenance and a network-path detector, since SQLite over network shares is unsafe).
- No horizontal scale-out at the database tier; appropriate for the current single-host
  Gateway model but a constraint to revisit if that changes.

## References

- `src/persistence/BotNexus.Persistence.Sqlite/` (`SqliteConnectionFactory`,
  `SqliteDatabaseRegistry`, `SqliteWalCheckpointHostedService`, `SqliteWalMaintenance`,
  `SqliteUsageTelemetryStore`, `NetworkPathDetector`)
- [arc42-lite overview](../README.md) — §7 Deployment View
- Issue [#220](https://github.com/Sytone/botnexus/issues/220)
