# Tool schema generator spike (#3320)

A measured answer to one question: can a `[ToolParameter]`-annotated declaration emit both a tool's
JSON schema and its `PrepareArgumentsAsync` copy list, without changing what the model sees or losing
any coercion behaviour — and what would converting the other 46 tools cost?

Subject tool: `src/gateway/BotNexus.Tools/GrepTool.cs`. It was chosen over `CronTool` because it is the
smaller surface that still exercises the hard case — the two undocumented alias parameters (`include`,
`max_results`) that must survive conversion — and because the `BotNexus.Cron` tree was heavily
contended by open branches at the time.

## Premise correction

The issue's framing implies the generator project is new. It is not. `tools/BotNexus.SourceGenerators`
already exists and already ships `FeatureFlagSourceGenerator` (#2769), wired as an `Analyzer` reference
from `BotNexus.Gateway.Configuration`. A repo-wide scan of every `*.csproj` and every non-`bin`/`obj`
`*.cs` for `IIncrementalGenerator|ISourceGenerator|analyzer` found **1** generator implementation file
and **67** matching project files (the great majority of which are ordinary `IncludeAssets` analyzer
lines, not generators).

That materially reduced the cost of this spike: no new project, no `BotNexus.slnx` entry, no
netstandard2.0/Roslyn-version archaeology. The second generator was added alongside the first.

## What was built

| Piece | File |
|---|---|
| Marker attributes (`[ToolSchema]`, `[ToolParameter]`), injected at post-init | `ToolSchemaCodeGenerator.AttributeSource` |
| Generator model with structural equality (incremental caching) | `ToolSchemaModel.cs` |
| Schema + prepare-table emitter | `ToolSchemaCodeGenerator.cs` |
| Incremental generator + `BNTS001`–`BNTS003` diagnostics | `ToolSchemaSourceGenerator.cs` |
| The grep declaration — 10 attributes, the whole argument surface | `src/gateway/BotNexus.Tools/GrepToolSchema.cs` |

The attributes are *injected* rather than referenced, so `BotNexus.Tools` takes no runtime dependency
on the generator — the same analyzer-not-assembly posture #2769 established.

## AC2 — schema comparison

**Byte-identical.** The generated `SchemaJson` reproduces the previous hand-written literal exactly:
same property order, same descriptions, same two-space indentation, same single-line property objects,
same `"required": ["pattern"]`. Verified two ways:

1. `EmitCompilerGeneratedFiles=true` on a rebuild of `BotNexus.Tools`, output inspected directly.
2. `GeneratedGrepSchema_IsByteIdenticalToTheHandWrittenLiteral` pins the pre-conversion literal as a
   constant in the test and asserts equality, so future drift fails rather than passes quietly.

The emitter's formatting is deliberately shaped to match the hand-written convention. That is not
cosmetic: "changing any tool's observable schema" is explicitly out of scope for #3320, so
byte-identity is the out-of-scope guard, and it is asserted rather than argued.

The two undocumented aliases are declared with `HiddenFromSchema = true`: accepted by the prepare
stage, not advertised to the model. Advertising them would have enlarged the model-visible schema;
dropping them would have broken callers already sending them. Neither was acceptable.

## AC3 — the #2641 failure is now unrepresentable

#2641 was a parameter present in the JSON schema and absent from the `PrepareArgumentsAsync` copy
list. Nothing threw. The caller's `windowDays=90` was silently discarded and the 7-day default
answered instead — a plausible number for the wrong question.

After conversion there is no second list. `PrepareArgumentsAsync` iterates
`GrepToolSchema.Parameters`, which is generated from the same attributes as the schema. A parameter
cannot reach one representation without reaching the other, because there is only one representation.

`AddingAParameterToTheDeclaration_ReachesBothSchemaAndPrepareTable` adds a parameter to a declaration
and asserts it appears in the emitted schema *and* the emitted table, with no second edit.
`EveryDeclaredParameter_ReachesThePreparedDictionaryWithItsEffectiveValue` asserts the **effective
value** of every declared parameter, not merely key presence — presence was never the property that
failed in #2641, which is precisely why it hid.

## AC4 — pre-existing tests

Every pre-existing `GrepTool` test was left unmodified. No assertion was weakened, loosened or
deleted. The three files that exercise the tool — `GrepToolTests`, `GrepToolLimitCeilingTests`,
`GrepToolSymlinkPathTests` — are untouched in the diff.

One behaviour changed by necessity and is called out rather than buried: the out-of-range message for
`max_results` previously read `"max_results must be greater than 0."` and the generated path now
reports the originating spelling through a `sourceKeys` lookup, preserving that text. Range validation
itself stays hand-written. Per-parameter semantics (`limit > 0`, `context >= 0`, the `MaxLimit` clamp)
are not schema shape, and the spike deliberately did not try to generate them.

## AC6 — what this would and would not have prevented

| Defect | Prevented? | Why |
|---|---|---|
| #2641 — declared in schema, forgotten in copy list | **Yes** | The copy list no longer exists as a separate artifact. |
| #2415 — schema says X, reader reads Y | **Partly** | The declared keys and their types now agree by construction. A reader that consults an entirely undeclared key is still possible; the generator does not police `ExecuteAsync`. |
| #2690 — callers send malformed values (`edits` as a JSON string, non-unique `oldText`) | **No** | These are correctly-described parameters carrying wrong values. Declaration agreement constrains nothing about what a caller actually sends. The coercion layers (`TryParseStringifiedEdits`, grep's aliases) earn their place regardless. |

Claiming this fixes #2690 would be overselling it, and the issue asks explicitly for the honest split.

## AC5 — measured cost and recommendation

**Measured cost for `GrepTool`:** roughly 2.5 hours of agent time against an 8-hour timebox, including
building the generator itself from scratch. The reusable generator is a one-time cost that is now
paid. The *marginal* per-tool cost is the interesting number, and for grep it decomposes as:

- ~10 minutes writing the declaration (one attribute per parameter, mechanical from the existing JSON);
- ~20 minutes rewriting `PrepareArgumentsAsync` to iterate the generated table;
- ~15 minutes proving byte-identity and adding behavioural tests;
- one line of `.csproj` (`OutputItemType="Analyzer"`), once per consuming project, not per tool.

Call it **45–60 minutes per simple tool**. `EditTool` at 1,260 lines with the fleet's heaviest coercion
layer would be several times that and should be last, not first.

**My recommendation: proceed, but incrementally and opportunistically — not as a 46-tool sweep.**

Reasoning, including the case against:

- The generator works, the schema is provably unchanged, and the #2641 class is genuinely gone for the
  converted tool. That is a real result, not a hopeful one.
- But the issue is honest that **current drift is low** — 1 of 218 apparently-unreferenced properties,
  and that one a false positive. This is regression *prevention*, not a live-bug fix. A 46-tool sweep
  would be ~40 hours of mechanical edits against a defect rate of approximately zero per quarter, and
  every one of those edits is a chance to introduce the very drift being prevented.
- The value is concentrated in tools that are *actively changing*. A tool nobody edits cannot drift.
  Converting on next substantive touch captures nearly all the benefit at nearly none of the cost.

So: adopt the generator, convert a tool when you are already editing its parameter surface, and
require conversion for any *new* tool. Do not schedule a sweep.

This is my recommendation as the implementing agent, not a decision. Jon rules on adoption.

## Diagnostics

Failure is a build error with a named cause, following the #2769 posture — a malformed declaration
must not emit nothing and produce a cascade of errors at innocent call sites.

| ID | Severity | Condition |
|---|---|---|
| `BNTS001` | Error | A parameter declares a JSON type that is not a JSON Schema type keyword. |
| `BNTS002` | Error | A parameter name is declared more than once. |
| `BNTS003` | Error | An alias targets a key not declared before it — the #2641 failure reintroduced through the alias path. |

Each has a dedicated test asserting the ID and `DiagnosticSeverity.Error`, plus
`AliasDeclaredAfterItsTarget_IsAccepted` as the non-vacuity counterpart so the ordering rule cannot
pass by rejecting everything.

## Follow-ups worth filing if adopted

- Generate the range/clamp rules (`limit > 0`, `MaxLimit`) rather than leaving them hand-written.
- An analyzer that flags `ExecuteAsync` reading a key no declaration mentions — closing the remaining
  half of the #2415 class.
- A convention test requiring any new `*Tool.cs` to carry a declaration rather than an inline literal.
