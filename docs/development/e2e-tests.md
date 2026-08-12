---
owner: shared
ai-policy: open
---

# End-to-end tests

The `BotNexus.Integration.E2E.Tests` project simulates the **new user
experience** from a clean slate: it packs and installs the in-tree CLI as a
global tool, runs the green-field provisioning flow, starts the gateway, and
then drives the Blazor portal with Playwright.

## What it covers

Per test-run sandbox under `Path.GetTempPath()/botnexus-e2e/<runId>/`:

1. `dotnet pack` the in-tree `BotNexus.Cli` as `99.99.99-e2e-<hash>` and
   `dotnet tool install` it into a per-run `tool/` directory.
2. `botnexus init --target <home>` — fresh `BOTNEXUS_HOME`.
3. `botnexus provider add --name integration-mock --api integration-mock
   --default-model integration-mock-echo --base-url <e2e-catalog.json>`.
4. `botnexus agent add <id> --provider integration-mock --model
   integration-mock-echo` × 3 (`alpha`, `bravo`, `charlie`).
5. `botnexus locations add <name> --type filesystem --path <tmp>` × 2.
6. `botnexus config set world.id`, `world.displayName`,
   `extensions.enabled true`, `gateway.defaultAgentId`.
7. `dotnet build BotNexus.slnx -c Release` (warmup so step 8 is fast).
8. `botnexus gateway start --attached --source <repo> --target <home>
   --port <free>` — runs as a child subprocess for the test-suite lifetime.
9. Poll `GET /health` until `200 OK` (max 3 minutes).
10. Playwright opens `http://127.0.0.1:<port>/` and asserts the portal renders.

## Running locally

```pwsh
# One-time: install Chromium for Playwright.
dotnet build BotNexus.slnx -c Release
pwsh tests/integration/BotNexus.Integration.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install chromium

# Run the suite.
dotnet test tests/integration/BotNexus.Integration.E2E.Tests --nologo --tl:off
```

The fixture installs Chromium on first run if it is missing, but a manual
install up front makes the first test more predictable.

## Mock catalog

`MockCatalogs/e2e-catalog.json` ships next to the test assembly and is wired
into the integration-mock provider via `provider add --base-url`. Add new keys
here (e.g. `TOOL_CALL_SEQUENCE`, `LONG_RUNNING`) rather than mutating
`DefaultCatalog` in production code so the catalog stays test-only.

Current keys:

- `HELLO_WORLD` — short four-delta response, useful as a liveness probe.
- `MULTI_DELTA` — ~14 deltas with ~80 ms inter-delta delay, used to exercise
  the portal's streaming-assembly path.

## Fixture-success skips are a mass-vacuity generator (issue #2491)

An xUnit **collection fixture** is constructed once for an entire collection. When
every test class in that collection opens with `Skip.IfNot(fixture.Succeeded, …)`,
a single provisioning fault flips one boolean and the whole collection converts
itself into skips. The runner then prints `Passed!` and exits `0`.

This is repo history, not a hypothesis. The `NewUserExperience` collection —
23 test classes — went fully dark on `main` **twice**:

| Cause | Fixed by |
|---|---|
| `botnexus init` seeds the `assistant` agent, so the provisioning loop's re-add exited non-zero | #2738 |
| The fixture's solution prebuild raced concurrent test hosts (`CS2012` / `MSB3883`) | #2749 |

Both times CI was green throughout and the E2E run reported
*"No test matches the given testcase filter"*. Roughly 56 genuinely broken tests
stayed hidden behind that green.

### The rule

Skipping is legitimate when a suite is genuinely opt-in on external
infrastructure. The rule is that **a skip must never be the only signal**: every
fixture whose success flag gates a collection must also be watched by a test that
*asserts*. Use a plain `[Fact]` — never `[SkippableFact]`, and with no `Skip.`
call in its body — so it fails loudly and by name when provisioning fails.

`BotNexus.Integration.E2E.Tests/FixtureHealthTests.cs` is the reference shape;
`BotNexus.Integration.ExtensionBoot.Tests/ExtensionBootFixtureHealthTests.cs` is
the same pattern applied to the extension-boot gate.

This is enforced by
`BotNexus.Architecture.Tests/FixtureSuccessSkipArchitectureTests.cs`, which sweeps
every tracked test source, finds each `ICollectionFixture<T>` and its boolean
success flags, and fails when a flag gates tests via `Skip` without a
corresponding asserting health test. It resolves one level of indirection, so
hiding the gate behind a `private bool ShouldSkip() => !_fx.Succeeded;` helper
does not evade it.

### Recorded audit findings (AC4)

| Fixture | Project | Status |
|---|---|---|
| `NewUserExperienceFixture` | `Integration.E2E.Tests` | Skip-gated, **watched** by `FixtureHealthTests` |
| `ExtensionBootFixture` | `Integration.ExtensionBoot.Tests` | Was skip-gated via `ShouldSkip()` indirection with **no** health test — fixed here |
| `CliInstallFixture` | `Integration.Cli.Tests` | Not skip-gated; asserts `InstallSucceeded` directly. Compliant. |
| `LocalCliInstallFixture` | `Integration.Cli.Tests` | Not skip-gated; asserts `Succeeded` via `AssertFixture()`. Compliant. |
| `LiveGatewayFixture` | `Conversation.Tests` | **Permanently dark in CI** — registered exemption, see below |

`LiveGatewayFixture` probes a *developer-run* gateway at `localhost:5006` that no
CI or container gate ever starts, so `IsAvailable` is always `false` unattended
and asserting it would fail every run. It is registered in the fence's
`EnvironmentGatedFixtures` allowlist with that reason. The finding stands on its
own merits: **that suite has never contributed gate signal** and should either be
re-hosted on a self-provisioned fixture (as `NewUserExperienceFixture` does) or
dropped from the gate scope. Tracked separately rather than fixed here.

## Followup work (issue #598)

The PR that introduced this project landed a single Playwright assertion
(portal renders agent IDs). Additional flows are pre-registered in
`PortalUserJourneyTests` as `[Fact(Skip = "Followup #598: …")]`:

- per-agent new-conversation + `HELLO_WORLD` send,
- parallel `MULTI_DELTA` streams across all three agents,
- mixed existing/new conversations driven concurrently.

These depend on stable `data-testid` hooks in the portal; pin them down once
the portal layout settles.
