# Scenario Test Suite

This folder hosts the **channel-agnostic scenario test suite** for BotNexus. It is the executable
specification of the citizen → conversation → session model documented in `plan.md §10` and
`docs/architecture/domain-model.md` (when that ships in Phase 0).

## Why this exists

The user's directive (2026-05-22) is the charter:

> "I want good TDD validation and emulation of the platform using virtual test channel
> adapters to emulate the different citizen scenarios in agent creation, conversation
> creation and interactions as well as how this impacts sessions. These will also stop slop
> coming in in the future as they articulate the core scenarios and will allow it to work
> no matter what channel implementations there are."

The three roles this suite plays:

1. **Spec.** Each scenario reads as plain English: "a citizen starts a conversation,
   exchanges three turns, compaction fires, conversation continues with history hidden
   from the LLM but preserved in the store." If the implementation can't make that pass,
   the implementation is wrong — not the spec.
2. **Regression net.** Today's slop happened because the inbound→router→session→adapter
   path had transport-coupled tests and zero contract-level tests. This suite is the
   contract-level net that survives any channel-implementation change.
3. **Channel-conformance gate.** A future SignalR/Telegram/Teams/CLI channel that wants
   to claim "I am a BotNexus channel" runs the same scenarios with its own adapter and
   passes — or it isn't a BotNexus channel.

## Folder layout

```
tests/scenarios/
├── BotNexus.Scenarios.Harness/        # class library — reusable across future per-channel
│   │                                    conformance projects (not a test project itself)
│   ├── VirtualChannelAdapter.cs       # full IChannelAdapter implementation; captures
│   │                                    outbound; pushes inbound on demand
│   ├── VirtualChannelAdapterOptions.cs  # per-instance capability flags + AdapterId
│   └── ScenarioFakeApiProvider.cs     # deterministic IApiProvider; scripted responses
│
└── BotNexus.Scenarios.Tests/          # the only test project under tests/scenarios/
    ├── Adapter/                       # harness conformance — proves the harness itself works
    │   ├── VirtualChannelAdapterConformance.cs
    │   └── ScenarioFakeApiProviderConformance.cs
    ├── Citizens/                      # (future) end-to-end citizen scenarios
    ├── Conversations/                 # (future) conversation lifecycle scenarios
    └── Sessions/                      # (future) session lifecycle + compaction scenarios
```

## The three layers

```
┌──────────────────────────────────────────────────────────────┐
│  tests/scenarios/BotNexus.Scenarios.Tests                    │  ← scenario specs (xUnit)
│  - citizen scenarios (User↔Agent, Agent↔Agent, Agent↔Sub)   │
│  - conversation lifecycle (create, list, switch, end, reset)│
│  - session lifecycle (turn, compact, seal, new-in-same-conv) │
│  - channel-binding scenarios (multi-channel, fan-out)        │
│  - capability gating (steering / streaming / images)         │
└──────────────────────────────────────────────────────────────┘
              uses ↓
┌──────────────────────────────────────────────────────────────┐
│  tests/scenarios/BotNexus.Scenarios.Harness                  │  ← reusable test fixture
│  - VirtualChannelAdapter (IChannelAdapter impl)              │     (class library)
│  - VirtualWorld (in-process gateway harness — coming next)   │
│  - ScenarioFakeApiProvider (deterministic LLM)               │
│  - Given/When/Then DSL (coming next)                         │
└──────────────────────────────────────────────────────────────┘
              uses ↓
┌──────────────────────────────────────────────────────────────┐
│  src/gateway + src/extensions/* (production code)            │
└──────────────────────────────────────────────────────────────┘
```

The harness is a class library, not a test project, so that any future channel adapter
(Telegram, ServiceBus, Teams, CLI) can reference it as a test dep and rerun the same
flows with its own adapter substituted in.

## Conventions

These conventions are **structurally enforced** by four architecture fitness functions
in `tests/architecture/BotNexus.Architecture.Tests/ScenarioSuiteArchitectureTests.cs`.
Violations fail the build.

1. **Every scenario is a `[Fact]` with a prose-shaped name.**
   Use `WhatHappens_When_Setup` not `Method_Scenario_Result` — these are spec tests, not
   unit tests. Examples:
   - `User_OpensConversation_WithAgent_AndReceivesReply_OnSingleTurn`
   - `Session_Compacts_WhenBudgetExceeded_AndConversationContinues_InSameSession`
   - `OneConversation_TwoVirtualChannels_BothReceiveOutbound_FromSameAgentTurn`

2. **No references to channel-extension projects.** A scenario test that does
   `using BotNexus.Extensions.Channels.SignalR;` (or any other concrete channel) is
   rejected by `ScenarioTests_DoNotReferenceAnyChannelExtension`. The whole point is
   channel-agnostic.

3. **Use the DSL or nothing.** Direct DI access from a scenario test is a smell — the
   DSL exposes the right level of abstraction. If the DSL is missing a verb, add the
   verb; don't reach past it. The
   `ScenarioTests_DoNotDependOnIServiceProvider` rule rejects any
   `Microsoft.Extensions.DependencyInjection` or `System.IServiceProvider` dependency
   from the scenarios project.

4. **Deterministic time.** When the `VirtualWorld` ships, it will provide
   `FakeTimeProvider`. Tests advance time explicitly via the DSL — no `await Task.Delay`.

5. **Deterministic LLM.** `ScenarioFakeApiProvider` takes a scripted
   `Func<int turnIndex, Context, string> respond` so every scenario's LLM behaviour is
   reproducible. Random replies are banned — they break the regression-net property.

6. **The harness is production-shaped.** `BotNexus.Scenarios.Harness` is a class library,
   not a test project. It must remain re-usable by any future per-channel conformance
   project. The
   `ScenarioHarness_DoesNotReferenceAnyChannelExtension` rule prevents accidentally
   coupling the harness to a single transport.

## Scenario inventory and phasing

The first wave of 12 scenarios is documented in `plan.md §10.4`. They are sequenced so
that:

- **Conformance tests** (this PR, 16 [Fact]s) prove the harness itself works end-to-end:
  the virtual adapter implements `IChannelAdapter` correctly, captures outbound and
  receives inbound, honours capability flags, and the fake API provider produces
  observable, deterministic LLM responses.
- **End-to-end citizen scenarios** land in follow-up PRs together with the
  `VirtualWorld` host harness and the fluent DSL. The detailed scenario list is in
  `plan.md §10.4` (first wave) and `plan.md §10.5` (later waves that depend on model
  evolution — Citizen abstraction, mark-as-history compaction, ThreadId removal,
  isolation strategies).

Each behaviour-changing phase tightens scenarios from "today's behaviour" to "target
behaviour" within the same PR — see `plan.md §9.5` for the per-phase TDD outline.

## How to add a scenario

1. Pick the folder under `BotNexus.Scenarios.Tests/` that matches the area
   (Citizens, Conversations, Sessions, ...) — create the folder if needed.
2. Name the file after the actor-and-outcome (e.g. `UserOpensConversation.cs`,
   `CompactionPreservesHistory.cs`). One file per scenario family.
3. Write a `[Fact]` with a prose-shaped name. The body must be readable as English —
   if you find yourself reaching for DI, switch to the harness DSL or extend the DSL.
4. Use `ScenarioFakeApiProvider` and `VirtualChannelAdapter` (once `VirtualWorld` is
   in place, prefer the world-level DSL).
5. Run the architecture fitness functions:
   `dotnet test tests/architecture/BotNexus.Architecture.Tests/ --filter ScenarioSuiteArchitectureTests`.

## Running

```shell
# All scenario tests
dotnet test tests/scenarios/BotNexus.Scenarios.Tests/BotNexus.Scenarios.Tests.csproj --nologo --tl:off

# Just the harness conformance
dotnet test tests/scenarios/BotNexus.Scenarios.Tests/BotNexus.Scenarios.Tests.csproj --nologo --tl:off --filter "FullyQualifiedName~Adapter"

# Architecture rules for the suite
dotnet test tests/architecture/BotNexus.Architecture.Tests/BotNexus.Architecture.Tests.csproj --nologo --tl:off --filter "FullyQualifiedName~ScenarioSuiteArchitectureTests"
```

## Related docs

- `plan.md §10` — the full scenario-suite design and scenario inventory.
- `plan.md §9` — the broader TDD strategy these scenarios sit inside.
- `tests/architecture/BotNexus.Architecture.Tests/ScenarioSuiteArchitectureTests.cs` —
  the four fitness functions that keep the suite honest.
- Root `AGENTS.md` (Test Enforcement, Value Objects Use Vogen) — global conventions.
