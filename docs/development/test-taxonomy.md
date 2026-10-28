# Choose the right test category

Use this guide when adding or reviewing BotNexus tests. Choose the smallest test that proves the behavior, and name the boundary it actually exercises. A **seam** is a boundary between components, such as a caller and its database store. A mock at that boundary cannot prove the real components work together.

This page describes the existing layout and contributor routing at repository revision `469cbeb8c2`. It does not add enforcement, move projects, or declare the [test taxonomy epic #1958](https://github.com/sytone/botnexus/issues/1958) complete.

## Choose by the evidence you need

| What you need to prove | Where to put it | What the test must exercise |
| --- | --- | --- |
| One production project's behavior, including isolated unit or component tests | Its exact mirror under `tests/agent`, `tests/domain`, `tests/extensions`, or `tests/gateway` | The production code under test. Test doubles can isolate dependencies; they do not establish integration coverage for those dependencies. |
| A dependency, project-layout, or other structural invariant | `tests/architecture/BotNexus.Architecture.Tests` | The actual assemblies, references, or repository files relevant to the rule. |
| Multiple components work across a real seam | `tests/integration` | The real boundary named by the assertion: for example, a writer and real filesystem. Do not mock the boundary and call the result integration evidence. Unrelated dependencies may still be controlled. |
| A caller/store interaction, durability, or concurrent write invariant | Existing suites in `tests/persistence` | The real store and database behavior, with explicit deterministic ordering where operations interleave. This is an existing specialized home for integration seams, not a demand to move these tests into `integration`. |
| A user flow through an actual browser or concrete channel transport | `tests/e2e` | The real browser/channel path being claimed. A fake provider can make replies deterministic, but a virtual channel cannot prove a concrete transport. A placeholder or skipped stub is not passing coverage. |
| A channel-independent citizen, conversation, or session acceptance flow | `tests/scenarios` | The typed harness domain-specific language (DSL), `VirtualChannelAdapter`, and scripted provider responses. Do not reference concrete channel extensions or reach through the DSL into dependency injection (DI). |

These categories describe evidence, not test-name keywords. Existing integration projects with `E2E` in their names do not all prove browser or channel end-to-end behavior. Provider conformance already has a home at `tests/agent/BotNexus.Providers.Conformance.Tests`; `tests/examples` covers examples, and `tests/tools` covers tooling outside `src/`. Do not create a new root-level or `conformance` category to accommodate them.

### Examples and limits

- **Real disk integration:** [ConfigWriteDurabilityDiskTests](https://github.com/sytone/botnexus/blob/main/tests/integration/BotNexus.Integration.ConfigDiskE2E.Tests/ConfigWriteDurabilityDiskTests.cs) checks physical backups, temporary-file cleanup, and replacement contents. These assertions require the real filesystem, not a mocked writer. This is evidence about the existing disk-writing compatibility path, not a recommendation to introduce a new configuration authority.
- **Real persistence seam:** [SessionLostUpdateSeamTests](https://github.com/sytone/botnexus/blob/main/tests/persistence/BotNexus.Persistence.Seam.Tests/Sessions/SessionLostUpdateSeamTests.cs) uses the real SQLite store and database file to characterize fenced and unfenced writes. A mock store would replace the very guarantee under test. See [Persistence seam testing](persistence-seam-testing.md) for write classification and deterministic interleavings.
- **Deterministic scenario:** [VirtualWorld](https://github.com/sytone/botnexus/blob/main/tests/scenarios/BotNexus.Scenarios.Harness/VirtualWorld.cs) boots a production-shaped in-process gateway graph in an isolated home, with a virtual channel and scripted provider. It proves platform flows without proving Telegram, Service Bus, or browser transport behavior. [ScenarioSuiteArchitectureTests](https://github.com/sytone/botnexus/blob/main/tests/architecture/BotNexus.Architecture.Tests/ScenarioSuiteArchitectureTests.cs) enforces channel independence, DSL use, and a public harness surface that does not expose DI primitives. Use explicit readiness/completion signals and controlled time rather than sleeps.
- **Stub is not E2E evidence:** [ServiceBusLoopbackStubTests](https://github.com/sytone/botnexus/blob/main/tests/e2e/BotNexus.E2E.ServiceBus.Tests/ServiceBusLoopbackStubTests.cs) explicitly skips because its transport loopback is not implemented. A project folder or green aggregate cannot turn that placeholder into exercised coverage. Browser-emulated E2E also remains quarantined from ordinary validation under [#3601](https://github.com/sytone/botnexus/issues/3601).

## Preserve the exact source mirror

For a production project, preserve every relative category/intermediate folder and add `.Tests` to the project directory and project name:

```text
src/gateway/BotNexus.Gateway/BotNexus.Gateway.csproj
  -> tests/gateway/BotNexus.Gateway.Tests/BotNexus.Gateway.Tests.csproj
```

[SrcTestsMirrorArchitectureTests](https://github.com/sytone/botnexus/blob/main/tests/architecture/BotNexus.Architecture.Tests/SrcTestsMirrorArchitectureTests.cs) is the current enforcement source:

- Every recursively discovered `src/**/*.csproj`, outside `bin` and `obj`, needs its exact mirror unless listed in `TestMirrorExemptions` with a justification.
- There are eight explicit source-project exemptions at the revision above. Being an abstraction, adapter, or composition project does not automatically exempt a new project. Add the mirror or make an explicit justified exemption change; stale exemptions fail the fence.
- Projects under `tests/` must use a known first-level category: `architecture`, `agent`, `domain`, `extensions`, `gateway`, `persistence`, `integration`, `e2e`, `scenarios`, `examples`, or `tools`.
- `RootLevelTestExemptions` is empty and closed. Cross-cutting projects must not be added directly under `tests/`.

The `.Tests` suffix rule above is the exact **source-mirror** mapping, not a blanket naming check for everything under `tests/`. The existing `tests/integration/BotNexus.Integration.ProviderTests` name is legacy. `tests/scenarios/BotNexus.Scenarios.Harness` is a reusable class library, not a runnable test project. Neither changes the required naming of a new source mirror. The layout fence does not classify test semantics or prove that every test in a category exercises the boundary described here.

## Build discovery and safe validation

Run commands from your isolated task worktree. You need the repository's .NET SDK for compilation, PowerShell and the configured Azure runner identity/settings for remote tests, and Node/npm dependencies for the documentation build. See [Developer setup](../getting-started-dev.md) and the [Azure runner guide](azure-build-test-runner.md) for setup.

[Root dirs.proj](https://github.com/sytone/botnexus/blob/main/dirs.proj) composes the production and test traversals plus tooling. [src/dirs.proj](https://github.com/sytone/botnexus/blob/main/src/dirs.proj) and [tests/dirs.proj](https://github.com/sytone/botnexus/blob/main/tests/dirs.proj) discover `.csproj` files recursively and explicitly exclude `bin` and `obj`. A newly placed project is discovered from disk; do not maintain a separate `.slnx` list for this traversal. Discovery also includes support libraries and does not by itself mean every discovered project contains runnable tests.

**Do not execute local tests on a host with a live gateway.** Tests can spawn real gateway children that outlive their parent and interfere with the live gateway's scheduled jobs. Local compilation starts no test host and is permitted:

```powershell
# Compile only the production traversal
dotnet build src/dirs.proj

# Compile a specific test project without running its tests
dotnet build tests/gateway/BotNexus.Gateway.Tests/BotNexus.Gateway.Tests.csproj
```

For ordinary code changes, use the strict candidate gate from [root AGENTS.md](https://github.com/sytone/botnexus/blob/main/AGENTS.md#validation). Remote validation is the default; do not replace it with impacted-only selection:

```powershell
scripts/repo/Validate-PreCommit.ps1
```

To explicitly run remote non-browser CORE for a task worktree, replace `<worktree>` with its path:

```powershell
scripts/repo/Invoke-AzureBuildTest.ps1 -Mode core -WorktreePath <worktree>
```

The [runner's core selection](https://github.com/sytone/botnexus/blob/main/infra/buildtest/runner/entrypoint.ps1) excludes the `BotNexus.Integration.E2E` and `BotNexus.E2E` fully qualified name families. Browser-emulated E2E is quarantined under #3601. `-Mode full` and `-Mode playwright` are explicit diagnostics, not required normal gates or proof that quarantine re-entry criteria are satisfied. Record actual results and skipped coverage separately; non-browser CORE passing does not establish browser coverage.

`scripts/repo/test-impacted.ps1 -DryRun` is a selection preview only. Do not run local `dotnet test`, `test.ps1`, or executing `test-impacted.ps1` on a live-gateway host. If remote infrastructure is genuinely unavailable, the root instructions define the explicitly selected local strict fallback and its safety restrictions; do not silently switch to it.

For documentation-only changes, the normal required gate is:

```powershell
npm ci # first time only
npm run docs:build
```

That checks documentation rendering and links, not runtime behavior. An explicit task requirement for remote CORE still applies even when the diff is documentation-only. If the remote harness cannot start, preserve the failure evidence and recover the execution environment before publication. A failed execution attempt is not a product blocker and is never evidence of a test pass.
