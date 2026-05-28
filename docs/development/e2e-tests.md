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

## Followup work (issue #598)

The PR that introduced this project landed a single Playwright assertion
(portal renders agent IDs). Additional flows are pre-registered in
`PortalUserJourneyTests` as `[Fact(Skip = "Followup #598: …")]`:

- per-agent new-conversation + `HELLO_WORLD` send,
- parallel `MULTI_DELTA` streams across all three agents,
- mixed existing/new conversations driven concurrently.

These depend on stable `data-testid` hooks in the portal; pin them down once
the portal layout settles.
