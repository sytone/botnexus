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

A temp-file-backed `IFileSystem` (`MockFileSystem` from
`TestableIO.System.IO.Abstractions.TestingHelpers`) is **not** a mock of the
seam: it is a deterministic stand-in for the out-of-scope disk. The component
under test (e.g. `PlatformConfigWriter`) is still the real production type
running its real logic.

## Naming & layout convention

- Seam tests live under `tests/integration/`.
- One clearly-named project **per seam**, named
  `BotNexus.Integration.<Seam>.Tests` (folder-per-seam is equivalent).
- The `<Seam>` segment names the boundary, not a class:
  - `ConfigSave`   - config `GET -> edit -> PUT` round-trip through the real writer
  - `SessionStore` - session persistence through the real store
  - `Conversation` - conversation REST + SignalR through the real pipeline/hub
  - `ProviderHttp`  - provider client through a real HTTP pipeline
- Register every new project in `BotNexus.slnx` under the
  `/tests/integration/` solution folder.

When a future seam needs a regression home, add a new
`BotNexus.Integration.<Seam>.Tests` project here rather than smuggling the test
into an unrelated unit-test project.

## Current seams

| Seam        | Project                                   | Real component under test | Out-of-scope stand-in |
|-------------|-------------------------------------------|---------------------------|-----------------------|
| config-save | `BotNexus.Integration.ConfigSave.Tests`   | `PlatformConfigWriter`    | temp-file `IFileSystem` |

The config-save seam is the canonical example: `ConfigSaveRoundTripTests`
drives the real `PlatformConfigWriter` through the exact `GET -> redact ->
edit -> PUT` flow the Configuration UI uses, with the same
`ConfigSecretMerge.Redact` the controller applies. It reproduces the #1954 /
#1955 data-loss bugs and locks in the fix - none of which a writer mock could
have caught.
