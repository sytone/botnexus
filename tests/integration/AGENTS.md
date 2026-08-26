# Integration / Seam Tests

This folder is the deterministic home for **seam tests**.

## What a seam test is

A *seam* is one boundary between real collaborators. A seam test exercises the
real components on **both sides of exactly one boundary** and asserts that the
data or behaviour crossing it is correct. The whole point is to catch the bugs
that mock-based unit tests hide - the ones that only appear when the *real*
writer, the *real* store, the *real* HTTP pipeline, or the *real* hub is on the
other side of the seam.

### The one hard rule

> **A seam test MUST NOT mock the component whose seam it is asserting.**

Mocks (or fakes/stubs) are permitted **only** for *out-of-scope external*
dependencies - things that are genuinely outside the boundary under test and
that you cannot exercise deterministically, e.g. a remote LLM endpoint, a
third-party billing API, wall-clock time. The local seam - the config writer,
the SQLite store, the ASP.NET request pipeline, the SignalR hub - is **always
real**.

An in-memory `IFileSystem` (`MockFileSystem` from
`TestableIO.System.IO.Abstractions.TestingHelpers`) is **not** a mock of the
seam *when the disk is genuinely out of scope*: it is a deterministic stand-in,
and the component under test is still the real production type running its real
logic.

> **But when the disk IS the seam, `MockFileSystem` does not qualify.** (#2066)

If the behaviour under test depends on OS semantics - atomic replace, temp-file
staging and cleanup, file locking, last-write timestamps, physical backups,
file-watcher-driven `IConfiguration` reload, or two writers racing on one inode -
an in-memory filesystem cannot observe it, and a test that uses one is a **unit**
test. Put it in the owning unit-test project and label it as such. The
integration acceptance bar for those behaviours is a real filesystem under a
temporary `BOTNEXUS_HOME`.

## Naming & layout convention

- Seam tests live under `tests/integration/`.
- One clearly-named project **per seam**, named
  `BotNexus.Integration.<Seam>.Tests` (folder-per-seam is equivalent).
- The `<Seam>` segment names the boundary, not a class:
  - `ConfigDiskE2E` - config mutation through the real writer onto a real disk
  - `SessionStore` - session persistence through the real store
  - `Conversation` - conversation REST + SignalR through the real pipeline/hub
  - `ProviderHttp`  - provider client through a real HTTP pipeline
- Place every new test project under `tests/` so `tests/dirs.proj` discovers it under the
  `/tests/integration/` solution folder.

When a future seam needs a regression home, add a new
`BotNexus.Integration.<Seam>.Tests` project here rather than smuggling the test
into an unrelated unit-test project.

## Current seams

| Seam        | Project                                     | Real component under test | Out-of-scope stand-in |
|-------------|---------------------------------------------|---------------------------|-----------------------|
| config-disk | `BotNexus.Integration.ConfigDiskE2E.Tests`  | `PlatformConfigWriter`, `PlatformConfigAgentWriter`, `ConfigBackupService`, the JSON configuration provider and `IOptionsMonitor<PlatformConfig>` | none - real disk, real watcher |

The config-disk seam is the canonical example. It drives every production config
mutation entry point across the full chain:

```
UI/API/CLI/tool/service -> production writer -> physical config.json
  -> JSON provider reload -> IOptionsMonitor / runtime consumer
```

Each scenario seeds a maximal realistic config, performs one mutation, and diffs
the whole before/after document so that **only the intended semantic delta** is
permitted - which is how collateral-damage bugs (#1954 dropped subtrees, #1955
clobbered secrets) get caught rather than hoped away. It also covers backups,
temp-file cleanup, reload acknowledgement, rejected validation, secrets,
unknown/extension JSON, `agents.defaults`, collections, and concurrent writers.

This project replaced `BotNexus.Integration.ConfigSave.Tests`, which described
itself as a real round trip but ran on `MockFileSystem`. Those tests were not
deleted: they were migrated to
`tests/gateway/BotNexus.Gateway.Tests/Configuration/ConfigSaveMergeUnitTests.cs`
and relabelled as the unit tests they always were. Do not re-promote them.
