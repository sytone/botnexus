# Source generator survey

**Status:** spike deliverable for [#2770](https://github.com/Sytone/botnexus/issues/2770). No generator is implemented here.

This page is the code-backed answer to one question: *where in BotNexus is knowledge written down
twice, in two places that must agree, where nothing notices when they stop agreeing?* Those are the
sites where a Roslyn incremental generator converts *a thing someone must remember* into *a thing
that cannot be forgotten*, because the second copy no longer exists — it is derived.

Every candidate below cites a file that was read. Candidates that could not be pointed at a real
file were not written down.

## Premise check: how many generators exist today

The issue was filed claiming zero. That is still true of `src`, but no longer true of the repo.

| Scope | Files scanned | `IIncrementalGenerator` / `ISourceGenerator` |
|---|---:|---:|
| `src/**/*.cs` | 1,324 | **0** |
| whole repo, excluding `bin`/`obj`/`node_modules` | 3,043 | **1** |

The one is `tools/BotNexus.SourceGenerators/FeatureFlagSourceGenerator.cs`, landed by
[#2769](https://github.com/Sytone/botnexus/issues/2769) — the reference implementation the spike
said should ship first. It is wired into exactly one consumer,
`src/gateway/BotNexus.Gateway.Configuration/BotNexus.Gateway.Configuration.csproj`, whose comment
records the three things that must line up (`OutputItemType="Analyzer"`, `AdditionalFiles`,
`CompilerVisibleProperty`) and notes that a property the compiler cannot see fails *silently*.

So the pattern is proven in-repo and the MSBuild wiring cost is known. The survey below is about
where to point it next.

## Ranking method

Candidates are ranked by **how silent the divergence is** — not by lines saved. A divergence that
throws is a bug; a divergence that reads as a clean pass is the defect class this repo keeps
re-filing ([#2764](https://github.com/Sytone/botnexus/issues/2764),
[#2767](https://github.com/Sytone/botnexus/issues/2767),
[#2700](https://github.com/Sytone/botnexus/issues/2700)).

---

## Candidate 1 — SignalR hub event inventory (**attribute/interface-driven**)

**Rank: 1. Divergence is total and currently live.** Filed as
[#3318](https://github.com/Sytone/botnexus/issues/3318).

**Hand-maintained today.** The server→client event contract is declared once as a typed interface,
`src/extensions/BotNexus.Extensions.Channels.SignalR/IGatewayHubClient.cs` — **24 members**. It is
then restated by hand in at least three places:

| Restatement | File | Members |
|---|---|---:|
| Blazor client registration | `src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core/Services/GatewayHubConnection.cs` | 24 |
| Integration harness | `tests/integration/BotNexus.Conversation.Tests/TestSignalRClient.cs` (`AllHubEvents`) | 13 |
| Scenario harness | `tests/scenarios/BotNexus.Integration.Tests/TestSignalRClient.cs` (`AllHubEvents`) | 13 |

**Measured drift, today, on `main`.** The Blazor client matches the interface exactly — 24/24, zero
gap in either direction. Both test harnesses do not: each omits **eleven** declared events —
`RunStarted`, `RunEnded`, `TurnEnd`, `TurnInterrupted`, `UserInputRequired`, `AgentsChanged`,
`ConversationChanged`, `SteeringFeedback`, `CanvasUpdated`, `CanvasStateChanged`, `TodoUpdated`.
The comment above `AllHubEvents` reads *"All events the WebUI registers (events.js) — must match
exactly"*. It does not match exactly, the comment still claims it does, and `events.js` no longer
exists outside vendored Playwright output. The harness cannot observe an event it never subscribed
to, so a regression in any of those eleven is invisible to the integration suite and the failure
presents as *nothing was received* — indistinguishable from a passing quiet run.

**Generator input.** `IGatewayHubClient` itself — it is already the single declaration, and it is
already a C# type, so this is attribute-driven in the strong sense (no new file, nothing to
orphan). A marker attribute on the interface is enough; the generator reflects its members.

**Emits.** The client-side `On<T>(...)` registration block, and a `HubEvents.All` constant array the
test harnesses consume instead of their own literal lists.

**Failure made impossible.** Adding an event to the contract and forgetting to subscribe to it. Not
"fewer lines" — the eleven-event gap above is the failure, present right now, and no test can
currently fail because of it.

**Size.** Small. One generator over one interface; three consumer sites collapse to one.

---

## Candidate 2 — `doctor` check and advisory inventory (**attribute-driven**)

**Rank: 2. Two live documentation gaps, and the docs are the operator's only index.** Filed as
[#3319](https://github.com/Sytone/botnexus/issues/3319).

**Hand-maintained today.** Each check declares its own id as a property —
`public string Id => "compaction-model";` — across
`src/gateway/BotNexus.Cli/Commands/Doctor/*.cs` (16 such declarations). The set that actually runs
is restated by hand in two registries:

- `DoctorCheckRegistry.CreateDefault()` — 6 aggregate checks.
- `DoctorConfigCommand.Checks` — 8 `IConfigCheck`s; `DoctorConfigCommand.Advisories` — 2
  `IConfigAdvisory`s.

and then restated a *third* time as prose and tables in `docs/cli-reference.md`.

**Measured drift, today, on `main`.**

1. `UnknownFeatureFlagAdvisory` (id `feature-flags-unknown-key`) is registered in
   `DoctorConfigCommand.Advisories` but the advisory table in `docs/cli-reference.md` lists only
   `gateway-wildcard-bind`.
2. `FeatureFlagSeedCheck` (id `feature-flags-explicit`) is registered in
   `DoctorConfigCommand.Checks` but the *"Current checks include: the `extensions` block, the Skills
   world default, the dev-mode origin enforcement flag, cron configuration, the memory agent
   default, and the compaction model settings"* sentence does not mention feature flags.

Both were added by real work and neither reached the docs. Nothing failed.

**Generator input.** `[DoctorCheck(Id = "...", Suite = ...)]` on each check class. The id currently
lives as a property *inside* the class, which is the right home — an attribute keeps it there while
making it enumerable at compile time.

**Emits.** The registry arrays (so a check cannot be written and left unregistered — the
[#2041](https://github.com/Sytone/botnexus/issues/2041) seam, currently defended by a hand-written
list), and a machine-readable id inventory a docs fence can diff against `cli-reference.md`.

**Failure made impossible.** A check that exists, compiles, has tests, and never runs — the
[#2700](https://github.com/Sytone/botnexus/issues/2700) shape exactly: a rule structurally incapable
of firing reads as a clean pass. Secondarily, the documented inventory silently drifting from the
real one, which is the state of `main` today.

**Size.** Small–medium. One attribute, one generator, two registries replaced, one docs fence.

---

## Candidate 3 — Tool JSON schema vs. argument reader (**attribute-driven**)

**Rank: 3. Largest surface and the strongest historical defect record, but current drift is low —
this is regression prevention, not a live bug.** Filed as
[#3320](https://github.com/Sytone/botnexus/issues/3320), scoped as a one-tool spike rather than a
fleet conversion.

**Hand-maintained today.** The issue's measured figure was **42 of 44** `*Tool.cs` files hand-writing
an inline `"type": "object"` schema. Re-measured on the merged base: **49** real `*Tool.cs` files in
`src` (excluding the `IAgentTool`/`Tool` contract types and any `bin`/`obj` output), **47** containing
an inline object schema, **44** whose schema parses as JSON with a `properties` block, **218**
declared schema properties in total. The ratio has grown from 42/44 to **47/49**; the shape is
unchanged. The largest are `src/gateway/BotNexus.Tools/EditTool.cs` (1,260 lines) and
`src/extensions/BotNexus.Extensions.ExecTool/ExecTool.cs` (1,226).

**Measured drift, today, on `main`.** Better than the issue implies, and this must be stated
honestly rather than assumed:

- Schema properties never referenced anywhere else in their own tool: **1 of 218** — `fields` in
  `ConversationTool.cs`, and that one is a false positive: it is consumed indirectly via
  `JsonFieldProjection.ReadFields(arguments)` (line 426).
- Argument keys read by code but not declared by the tool's own schema: three files, all benign on
  inspection. `src/gateway/BotNexus.Tools/GrepTool.cs` accepts `include` and `max_results` as
  undocumented aliases for the declared `glob` and `limit` (lines 129, 164) — deliberate
  compatibility coercion. `GitHubIssueListTool.cs` and `GitHubPullRequestListTool.cs` read
  `requestedPerPage`, which is not a caller argument at all: it is written by their own
  `PrepareArgumentsAsync` (line 59) so the executor can report `perPageClamped` back.

So the schema/reader pair is currently in good agreement. The justification is therefore **not**
"fix today's drift" — it is that this agreement is maintained entirely by hand across 218 properties
and 47 files, and it has failed before:

- [#2690](https://github.com/Sytone/botnexus/issues/2690): `edit` carries the highest absolute error
  count in the fleet — 148 non-unique `oldText`, 87 `edits` passed as a JSON *string*, 119 encoding
  failures. `EditTool.cs` still contains `TryParseStringifiedEdits` (line 292) and
  `TryReadLegacyEdit` (line 278): coercion layers that exist precisely because callers send a shape
  the schema did not describe well enough.
- [#2415](https://github.com/Sytone/botnexus/issues/2415): four of six documented argument-shape
  defects had already been fixed elsewhere; the schema said one thing and the tool did another.

**Would a generator have prevented those two?** Partially, and the distinction matters. A generator
emitting the schema from a parameter record would have made *#2415's* class — schema says X, reader
reads Y — unrepresentable. It would **not** have prevented *#2690*: those are callers sending
malformed values for a correctly-described parameter. A richer generated schema (declared as
`edits: EditEntry[]`, never as a stringifiable blob) narrows the space a caller can get wrong, but
the coercion layer earns its place either way. Claiming the generator would have prevented #2690
outright would be overselling it.

**The precedent that generalises.** [#2641](https://github.com/Sytone/botnexus/issues/2641) found
`CronTool.PrepareArgumentsAsync` to be an *allow-list*: it whitelist-copies each argument into a
`prepared` dictionary (`CopyString(arguments, prepared, "jobId")` and its siblings in
`src/gateway/BotNexus.Cron/Tools/CronTool.cs`). A parameter added to the schema but omitted from
that copy list is answered with a plausible default and no error anywhere. That is a third
representation of the same parameter set — schema, copy list, execute-side reader — and it is the
one most likely to be forgotten, because forgetting it produces a working-looking answer.

**Generator input.** An attribute on a parameter record: `[ToolParameter(Description = "...")]` on
each property of a `record EditArgs(string Path, EditEntry[] Edits, string? ExpectedHash)`.

**Emits.** The JSON schema *and* the prepare-side copy/coercion list from one declaration.

**Failure made impossible.** A parameter that exists in exactly one of {schema, prepare allow-list,
execute reader}. Specifically the #2641 shape: a documented argument silently ignored.

**Size.** Large. 47 tools, some over 1,200 lines, several with hand-tuned coercion that must be
preserved verbatim. **Do one tool first and measure** — `CronTool` or `GrepTool`, not `EditTool`.

---

## Considered and rejected

Recorded so the same ground is not re-surveyed.

| Candidate | Why rejected |
|---|---|
| **`[ConfigField]` config metadata** (130+ `[ConfigField]`, 120+ `[Display]`, 75+ `[DefaultValue]` usages in `src`) | Already attribute-driven and already single-source. `src/gateway/BotNexus.Gateway.Api/Configuration/ConfigSchemaBuilder.cs` reflects over the annotated tree at runtime and `tests/architecture/BotNexus.Architecture.Tests/ConfigFieldCoverageFenceArchitectureTests.cs` fences coverage with a two-way baseline. There is no second hand-maintained copy to eliminate — moving reflection to compile time would be a performance change, not a defect-prevention one. Rejecting on the AC4 rule: brevity/speed is not sufficient justification. |
| **DTO mapping** (`ToDto`/`FromDto`/`MapTo`) | Measured: 14 call sites across **5** files (`ConversationsController.cs`, `ConversationSectionsController.cs`, `SecurityDiagnosticsController.cs`, `SkillsEndpointContributor.cs`, `MetricsSnapshotCollector.cs`). A missing mapping is a compile error or a visibly null field, not a silent divergence. Justified only by brevity → rejected under AC4. |
| **Architecture fences** (98 `*ArchitectureTests.cs` files) | Explicitly out of scope per the issue: a generated test asserting generated code proves the generator ran, not that the behaviour is right. Fences check *hand-written* code and stay hand-written. |
| **Exhaustive-inventory tests** | Only 9 test files use `Enum.GetValues`, and the best of them argue *against* generation. `SubAgentStatusPolicyTests` is deliberately value-by-value rather than "the enum has six members", because [#2677](https://github.com/Sytone/botnexus/issues/2677) AC5 requires that removing an arm fails a test *by name*. A generated exhaustiveness assertion would regenerate itself around the removal and stay green. Rejected on correctness, not size. |
| **CLI option definitions** (`new Option<T>("--verbose", ...)`) | Single declaration site per option, consumed by System.CommandLine directly. No second copy. |

## Findings that are not generator work

Two live drifts were measured while surveying. Both are folded into the follow-up issues above rather
than left as prose, because a finding described only in a docs page is not tracked:

- Test harness `AllHubEvents` lists omit 11 of 24 declared hub events — [#3318](https://github.com/Sytone/botnexus/issues/3318) AC2.
- `docs/cli-reference.md` omits the `feature-flags-unknown-key` advisory and the
  `feature-flags-explicit` check — [#3319](https://github.com/Sytone/botnexus/issues/3319) AC5.

## Follow-up issues filed

| Rank | Issue | Candidate |
|---:|---|---|
| 1 | [#3318](https://github.com/Sytone/botnexus/issues/3318) | Generate the hub event inventory from `IGatewayHubClient` |
| 2 | [#3319](https://github.com/Sytone/botnexus/issues/3319) | Generate the doctor check and advisory inventory from an attribute |
| 3 | [#3320](https://github.com/Sytone/botnexus/issues/3320) | Prove an attribute-driven tool schema generator on one tool (spike) |

## The standing rule

Adopted into [PR and Commit Conventions](./pr-and-commit-conventions.md#generated-vs-hand-maintained-shapes).
Restated here because this page is where the reasoning lives:

> When a change introduces a shape that must be kept in sync with another representation of the
> same knowledge, say so in the PR body and state whether generation was considered.

The test is not "is this repetitive". It is: **if these two copies disagree, what happens?** If the
answer is *a compile error* or *a loud failure*, hand-maintenance is fine. If the answer is *nothing
— it reads as a clean pass*, that is the #2764 / #2767 / #2700 family, and generation is the lever
that removes the second copy rather than defending it with one more fence.

Prefer an **attribute** over a **file**. An attribute sits on the thing it describes and moves with
it; a file is a second place to remember, and can be orphaned. Use a file only where the data must
be enumerable *before* any code references it — the #2769 feature-flag case, and so far the only
one.
