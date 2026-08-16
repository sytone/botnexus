# ADR: Scenario Test Framework

**Status:** Accepted
**Date:** 2026-08-16
**Issue:** [#1963](https://github.com/Sytone/botnexus/issues/1963) (parent epic [#1958](https://github.com/Sytone/botnexus/issues/1958))
**Decision:** Keep the hand-rolled scenario harness on xUnit. Formalise it. Do **not** adopt
Reqnroll or TUnit.

## Context

BotNexus scenario tests are full user journeys — "install the platform, bring up the first
agent, send it a message, get a reply" — rather than unit tests. Issue #1963 asked whether
those journeys deserve a purpose-built BDD or DI-native test framework, and named three
candidates: Reqnroll, TUnit, or formalising what already exists.

### What is actually on disk

Measured before deciding, not taken from the issue text:

| Asset | Path | Shape |
|---|---|---|
| Harness class library | `tests/scenarios/BotNexus.Scenarios.Harness` | `VirtualWorld`, `VirtualChannelAdapter`, `ScenarioFakeApiProvider` |
| Scenario test project | `tests/scenarios/BotNexus.Scenarios.Tests` | xUnit, 8 journey files under `Citizens/`, 2 conformance files under `Adapter/` |
| Container scenario runner | `tests/scenarios/BotNexus.Integration.Tests` | console app, JSON-driven `ScenarioRunner` against a live gateway URL |
| Container fixtures | `tests/container/` (before this change) | `config.json` + `scenarios/smoke.json`, no project |
| Fitness functions | `tests/architecture/.../ScenarioSuiteArchitectureTests.cs` | 5 rules that already enforce the conventions |

The harness is not a sketch. `VirtualWorld.StartAsync` boots the **production**
`AddBotNexusGateway()` service graph against an isolated temp `BotNexusHome`, substitutes
only the LLM round-trip and the channel, and exposes typed verbs (`GivenAgentAsync`,
`WhenSendsAsync`, `WaitForReplyAsync`). Scenarios already read as prose and are already
structurally prevented from reaching into DI.

## Options considered

### Option A — Reqnroll (Gherkin `.feature` files)

Reqnroll is the maintained SpecFlow successor. It would give `Given/When/Then` in plain
text, parsed and bound to step-definition methods.

Against:

- **The readability gain is near zero here.** The value of Gherkin is letting a
  non-programmer author the spec. BotNexus scenarios are authored by Jon and by agents,
  both of whom read C# fluently. `AGENTS.md` already mandates prose-shaped `[Fact]` names,
  so the existing `User_SendsMessage_AgentReplies_OutboundDelivered_ToVirtualChannel` is
  the same sentence Gherkin would produce — with compiler-checked bindings.
- **It adds an indirection layer that the fitness functions cannot see through.** The five
  rules in `ScenarioSuiteArchitectureTests` work on assembly references and exported types.
  Step definitions bound by regex to text files move the coupling into strings, where
  NetArchTest cannot reach it.
- **Code-generation build step.** `.feature` files generate `.feature.cs` at build time,
  which the remote container gate and the architecture reflection tests both have to
  tolerate.
- **A new dependency across a 13,000-test solution** for a suite that is currently 10 files.

### Option B — TUnit

TUnit is a modern, source-generated, DI-native .NET test framework with less ceremony than
xUnit and first-class async lifecycle hooks.

Against:

- **It is a second test framework, not a replacement.** Migrating 13,000 existing tests is
  not on the table, so adopting TUnit means the solution runs xUnit *and* TUnit. The remote
  gate (`Invoke-AzureBuildTest.ps1`) aggregates TRX counters across every test project; a
  second runner is a real risk to the "counters must corroborate" contract that
  `result.json` depends on.
- **The ceremony it removes is not the ceremony we have.** TUnit's headline win is DI
  injection into test classes. Our fitness functions *deliberately forbid* scenario tests
  from touching DI — `ScenarioTests_DoNotDependOnIServiceProvider`. The framework's main
  selling point is a feature this suite has banned on purpose.
- `VirtualWorld` is already `IAsyncDisposable` and consumed via `await using`, which is the
  async-lifecycle problem TUnit solves, already solved.

### Option C — Formalise the hand-rolled harness (chosen)

Keep xUnit + `VirtualWorld`. Spend the effort on the parts that are genuinely missing:
journeys, and the container fixtures' broken home.

For:

- **Zero new dependencies, zero migration, zero risk to the gate.** The lowest-churn option
  that satisfies the journeys, which is the explicit bar for this decision.
- **The harness already boots production wiring.** Neither Reqnroll nor TUnit would improve
  fidelity; they only change how the test is *spelled*. Fidelity comes from
  `AddBotNexusGateway()`, and we already have it.
- **The conventions are enforced by code, not by convention.** Five architecture rules
  already fail the build on channel coupling, DI leakage, and harness drift. That is
  stronger enforcement than either candidate framework offers, and it is framework-agnostic.
- **The real gap was never the framework.** It was that `tests/container/` sat outside the
  scenarios tree with CI paths pointing at a directory that does not exist (see below).

## Decision

Keep the hand-rolled harness on xUnit. Formalise it by:

1. Folding `tests/container/` into `tests/scenarios/container/` so every scenario asset —
   in-process and containerised — lives under one root.
2. Documenting the two scenario execution modes and when to use each (below).
3. Adding the first-agent bring-up journey as the worked proof of the chosen approach.

Revisit only if a non-engineer stakeholder needs to author scenarios directly, which is the
one condition under which Reqnroll's cost would be repaid.

## Consequences

### `tests/container/` is folded in, not deleted

The issue described it as a "weird" stray folder. It is not stray: it is live CI
infrastructure. `config.json` is the credential-free gateway config mounted into the
container by both jobs in `.github/workflows/ci-container-integration.yml`, and
`scenarios/smoke.json` is the no-LLM API smoke suite that job runs. Deleting it would break
container CI, so it is **relocated**, not removed:

```
tests/container/config.json          → tests/scenarios/container/config.json
tests/container/scenarios/smoke.json → tests/scenarios/container/scenarios/smoke.json
```

### Two pre-existing broken CI paths were fixed by the same move

Auditing the references turned up that `ci-container-integration.yml` was already broken
before this change, independently of the folder move. Both jobs run:

```
dotnet build tests/BotNexus.Integration.Tests/BotNexus.Integration.Tests.csproj
dotnet run  --project tests/BotNexus.Integration.Tests
```

That path does not exist. The project lives at
`tests/scenarios/BotNexus.Integration.Tests`. The workflow is `workflow_dispatch`-only, so
the breakage was never surfaced by a push. Corrected in the same commit, since leaving a
known-dead path behind while touching the file would be dishonest.

The doc table in `container-integration-testing.md` claiming `container-smoke` runs on
"every push/PR to `main`" is likewise inaccurate — the workflow trigger is
`workflow_dispatch` only. Corrected to match the workflow.

### The two scenario execution modes

| Mode | Project | Boots | Use for |
|---|---|---|---|
| **In-process** | `BotNexus.Scenarios.Tests` (xUnit) | `VirtualWorld` — production DI graph, fake LLM, virtual channel | Journeys, lifecycle, routing, capability gating. Default choice. |
| **Container** | `BotNexus.Integration.Tests` (console + JSON) | A real gateway container over HTTP | Container-isolation journeys, real API surface, deployment shape. |

A journey belongs in-process unless it specifically needs a real process boundary. The
container mode exists for the "create an agent using container isolation" journey in the
parent epic, which by definition cannot be proven in-process.

## Proof

`tests/scenarios/BotNexus.Scenarios.Tests/Journeys/FirstAgentBringUpJourney.cs` implements
the first-agent bring-up journey against the chosen approach: a clean platform with no
agents, register the first agent, send it a message, receive a reply, and observe the
conversation and session the platform created as a side effect. It asserts the empty
starting state — which is what makes it a *bring-up* journey rather than a message-round-trip
test — and it asserts on the fake provider's turn count so a silently-bypassed LLM path
cannot pass.
