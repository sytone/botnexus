# Spike: first-class workflow conversations (`ConversationSource.Workflow`)

> **Status: spike deliverable — a recommendation, not a decision, and not a shipped feature.**
> Everything below is an engineering recommendation grounded in source read on `main` at
> `f5e0182e`. Adoption is Jon's call; nothing here records it as taken.
> Tracking issue: [#2832](https://github.com/sytone/botnexus/issues/2832).

## 0. Reading guide

Multi-step, long-running processes ("drive a PR to green, then to merge, then post-merge") are
today expressed as a cron job plus hand-rolled state files. At least two independent
implementations of that pattern exist, in two languages, by two authors, and neither is visible to
a user as *a process with a position in it*. This page answers whether BotNexus should gain a
first-class workflow conversation, what the minimum model is, and what the portal needs first.

Four required elements, in order: [the decision](#1-decision), [the schema](#2-proposed-schema),
[the client changes](#3-signalr-client-changes), [the artifact mapping](#4-artifact-mapping).
[Section 5](#5-corrections-to-the-issue-body) records premises in the issue body that did not
survive re-checking against source — read it before quoting any figure from the issue.

---

## 1. Decision

### 1.1 Recommendation

**Recommend: adopt `ConversationSource.Workflow = 4`, and reject a bespoke general-purpose workflow
engine at this time.** These are two separate recommendations and the second is the load-bearing
one.

| Question | Recommendation | Confidence |
|---|---|---|
| Add `ConversationSource.Workflow = 4`? | **Adopt** | High — the axis argument is clean |
| Build the executor described across the issue's 44 acceptance criteria? | **Reject as one piece of work**; recommend a narrow first slice | High — scope evidence below |
| Adopt an off-the-shelf engine (Elsa 3, Durable Task, Temporal)? | **Reject**, on capability and infrastructure grounds | Medium-high |
| Adopt the Open Workflow Specification DSL wholesale? | **Reject**; publish our own JSON Schema instead | Medium — see [1.5](#15-format) |

### 1.2 Why `Workflow` is not `Cron` — the axis argument

`ConversationEnums.cs` states `ConversationSource` is *"deliberately coarse"* and explicitly refuses
a fifth value that would *"re-introduce overlap between the two axes"*. That refusal is scoped: it
is aimed at a value that would duplicate what `ConversationKind` already separates (the doc comment
names sub-agent supervision vs peer converse as exactly that case). It is not a general ban on
appending values, and `ConversationKind.Ralph = 3` is precedent that the enums do grow when a
genuinely new origin shape appears.

The orthogonality test is whether `Workflow` is distinguishable from `Cron` by any *existing*
field. It is:

| Property | `Source.Cron` conversation | A workflow run |
|---|---|---|
| What the conversation contains | a log of **independent** runs; run *N* does not read run *N−1*'s state | a **single** run with a current position and a remaining step list |
| Trigger | always a schedule | a schedule, a webhook, a human, or another run |
| Terminal state | none — a cron job runs until disabled | yes — merged / abandoned / failed |
| Correct answer to "where is this at?" | "it last ran at *T*" | "step 4 of 9, gated on an approval since *T*" |

The trigger row is the decisive one. A workflow may be *pulsed* by cron and still not *be* cron —
the same way a `Channel` conversation is not `Webhook` merely because both arrive over HTTP. Source
answers "why does this conversation exist", and the answer for a workflow run is "because a
workflow definition was instantiated", which no existing value expresses. `Cron` would be actively
wrong for a hand-started or webhook-started run of the same definition.

Two properties make the addition cheap, both verified in source:

- **Back-compat is free.** `ConversationRowMapper.ParseConversationSource` (line 238) is
  `Enum.TryParse<ConversationSource>(value, ignoreCase: true, ...)` with a documented fallback to
  `Channel` on NULL/absent/garbage. Note the correction this forces: **`source` persists as a
  string, not an ordinal** (`ConversationRowMapper.cs:60`, `:107`). Appending value `4` therefore
  cannot renumber or misparse anything; a pre-existing row has no `Workflow` string in it to
  mis-hydrate. The "0 remains the default" reasoning in the issue reaches the right conclusion via
  a mechanism the store does not actually use.
- **No schema migration.** `conversations.source` is already a nullable text column. A new enum
  member needs **no** `conversations` table change, no `ConversationRowMapper` change and no
  `SqliteConversationStore` change. This is a narrower answer than the issue's AC9 anticipated, and
  it is the strongest single argument for adopting the value early even if the engine never ships.

### 1.3 The axis values for a workflow run

| Axis | Value | Justification |
|---|---|---|
| `Kind` | `HumanAgent` | A human answers its gates and can type into it. Not `Ralph`: a ralph loop re-triggers off turn-end with nobody listening; a workflow run expects a reply. Not `AgentAgent`/`AgentSubAgent`: those encode a pairing topology the run does not have. |
| `Source` | `Workflow` (new, `= 4`) | Per [1.2](#12-why-workflow-is-not-cron--the-axis-argument). |
| `Visibility` | `UserFacing` | The run is the thing the user watches. `InspectableReadOnly` would be wrong for the same reason `IsUnattended` is — see next. |

### 1.4 `IsUnattended` must **not** include `Source.Workflow`

This is the trap the issue flags in AC3, and re-reading the shipped code confirms it exactly.
`ConversationRenderProjection.IsUnattended` is currently:

```csharp
public bool IsUnattended =>
    Kind is ConversationKind.AgentAgent or ConversationKind.AgentSubAgent or ConversationKind.Ralph
    || Source is ConversationSource.Cron or ConversationSource.Webhook;
```

with `IsReadOnly => IsUnattended || SelectionSource == SelectionSource.SubAgentView` and
`ShowComposer => !IsReadOnly`.

Adding `Workflow` to that disjunct by analogy with `Cron` would set `IsReadOnly`, which would hide
the composer, which would make every human gate unanswerable — the run could never advance past its
first approval. The projection's own doc comment already states the correct rule for the directly
analogous case (#2526): `Source.Agent` is excluded because *"that source only records who pulled
the trigger, not who participates"*. The identical sentence is true of `Workflow`, and the `Kind`
axis is again what actually encodes participation.

**Recommendation: `IsUnattended` is left unchanged when `Workflow` is added.** The test that must
fail if this is flipped is a new case in
`tests/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests/ConversationRenderProjectionTests.cs`
asserting `ShowComposer == true` for `(HumanAgent, Workflow)`; it belongs beside the existing
`CronReadOnlyRegressionTests.cs` cases, which are the same guard for the neighbouring value.

### 1.5 Group, badge, and format

**Group and badge.** `ConversationListGroup` currently has four values (`Normal`, `Scheduled`,
`Automated`, `AgentInitiated`) and `Badge` maps them to `"Cron"` / `"Webhook"` / `"Read-only"` /
`null`. Reusing `Automated` would be wrong on the same grounds as reusing `Cron`: `Automated`'s doc
comment says *"grouped alongside scheduled unattended runs"*, and a workflow run is neither
unattended nor read-only. **Recommend a fifth value `Workflow` with badge text `"Workflow"`**,
ordered after `Scheduled`/`Automated` and before `AgentInitiated` in the picker. `Group` is a
`switch` over `Source` with a `_ =>` default, so an unhandled new source silently falls to `Normal`
— the new arm must be explicit, and the grouping test must pin it.

**Format.** The Open Workflow Specification's published JSON Schema (draft 2020-12) is the most
valuable artefact in the engine survey, because a validating schema — not a DSL — is what makes an
LLM able to author a definition and be told *which field* is wrong. But the go/no-go probe the
issue asks for (AC39) resolves against adopting the spec itself: the three hardest requirements
(a gate that keeps executing remediation while gated; a retry budget keyed by runtime-discovered
signatures; a step that selects its own outgoing edge *and* writes facts) have no natural
expression in a DSL designed for stateless service orchestration, so a faithful definition would be
predominantly `botnexus:`-namespaced extensions. At that ratio the standard buys nothing: the
published schema validates only the skeleton, a third-party editor renders our most important
semantics as opaque generic nodes, and we cannot claim conformance anyway.
**Recommend: publish a native BotNexus JSON Schema, and borrow the spec's task-type vocabulary
(`run`, `set`, `switch`, `try`, `wait`, `listen`) as naming rather than as a dependency.**

### 1.6 Why the engine is rejected as one piece of work — the scope evidence

This is the recommendation most likely to be argued with, so the evidence is stated plainly.

The issue accumulated **44 acceptance criteria across five addenda and three consulted agents**,
and each consultation *corrected premises in the previous one* — ten recorded corrections, several
of which reversed an earlier acceptance criterion outright (AC8 superseded by AC15; AC11 reversed
by Sentinel's finding that pr-doctor is immune to the scheduler clobber by construction). That is
not a sign of a badly-written issue. It is a measurement: **the requirement set was still moving
after three reviews**, and the union of three customers is materially larger than any intersection.

The survey's own finding is that no engine on the market supports the three hardest requirements,
so ~40% of the spec is bespoke regardless of what is adopted. Combining that with a requirement set
that is still growing yields the recommendation: **do not commit to the engine; commit to the
cheapest slice that makes the next hand-rolled state file unnecessary.**

The single most valuable finding in the whole issue is Sentinel's cost measurement, and it is worth
more than the engine:

> A no-change pr-doctor tick under a Scheduled Task costs **zero tokens and about two seconds**.
> The same tick under BotNexus cron today is **a full LLM turn**.

BotNexus has no way to run a cheap tick inside an agent-driven loop. That is the actual defect, it
is independently useful, and it does not require a workflow engine to fix.

### 1.7 Where the evidence genuinely splits

Two questions do not have an evidence-forced answer, and both are recorded as open rather than
resolved:

1. **Step typing: by mechanism or by who decides.** Typing steps `command` / `agent-prompt` /
   `subagent` / `approval` matches the executors that exist today (`CommandCronAction.ActionType`
   is literally `"command"`) and is implementable now. Typing them `derive` / `decide` / `act` /
   `notify` with mechanism as an attribute is the better long-run model — it is what allows a step
   to be promoted from an LLM turn to a script *without rewriting the graph*, which was named as the
   highest-value platform capability by one consulted agent. Recommendation, weakly held: **type by
   who decides, carry mechanism as an attribute**, because the migration cost runs one way only.
2. **Whether the second and third customers are real customers.** The Keel and Nova requirements
   (non-blocking gates, fan-out of unknown cardinality, suppression windows, cross-run anomaly
   detection) were elicited by asking two agents what they would need. Elicited requirements are
   not the same as observed ones. They belong in the design record; they should not size the first
   slice.

### 1.8 No production behaviour changes in this spike

`git diff --stat` against `main` for this branch shows documentation only. No runtime code path,
enum value, projection, mapper or test is modified by this spike.

---

## 2. Proposed schema

Deliberately small: the point is that it fits on one screen and can be argued with, not that it is
complete. It covers the pr-doctor shape and names what it cannot express.

### 2.1 Step definition

```jsonc
{
  "schemaVersion": 1,
  "id": "wf_01J8Z...",              // opaque, immutable, engine-assigned
  "name": "pr-doctor",              // display only; freely editable
  "pulse": "*/5 * * * *",           // a FLOOR, never an authority (see 2.4)
  "steps": [
    {
      "id": "s_01J8Z...",           // opaque, engine-assigned; never a human-meaningful string
      "name": "read build state",
      "decides": "derive",          // derive | decide | act | notify
      "via": { "kind": "command", "shellCommand": "pwsh -File ./probe.ps1" },
      "produces": ["buildState", "tipSha"],   // typed facts written to the ledger
      "next": [
        { "when": "facts.buildState == 'failed'", "to": "s_fix" },
        { "to": "s_wait" }
      ]
    },
    {
      "id": "s_fix",
      "name": "generate a fix",
      "decides": "decide",
      "via": { "kind": "agent-prompt", "message": "..." },
      "edges": ["fixed", "cannot_fix"],       // the agent SELECTS one; it does not just run
      "budgetKey": "compile:{facts.file}:{facts.code}",  // runtime-discovered signature
      "next": [
        { "when": "edge == 'fixed'", "to": "s_probe" },
        { "to": "s_gate" }
      ]
    },
    {
      "id": "s_gate",
      "name": "human decision",
      "decides": "notify",
      "via": { "kind": "gate", "policy": "manual", "blocking": false,
               "choices": ["retry", "abandon", "hold"], "expiresAfter": "P7D" }
    }
  ]
}
```

Four properties are load-bearing:

- **Step ids are opaque and engine-assigned.** A human-meaningful id is an id an agent will be
  tempted to "improve", and a renamed id is a silently dangling edge.
- **`decides` is the type; `via` is the mechanism.** Promoting a step from `agent-prompt` to
  `command` changes `via` only; no edge is touched.
- **An `agent-prompt` step selects an edge *and* writes facts.** A step that can only run cannot
  express the fix/disable/file-flaky choice, which is where the real workflows live.
- **`budgetKey` is a template resolved at execution time**, so the retry budget is a run-level,
  dynamically-keyed counter set — not a static `retries: 3` on the step.

### 2.2 Run record (a ledger, not a step pointer)

```jsonc
{
  "runId": "wr_01J8Z...",
  "definitionId": "wf_01J8Z...",
  "definitionVersion": 7,
  "rulesVersion": "delete-rules@2026-08-12",   // which external predicate data was evaluated
  "conversationId": "conv_...",                 // (HumanAgent, Workflow, UserFacing)
  "currentStepId": "s_gate",
  "status": "gated",       // running | gated | blocked_external | terminal
  "nextWakeAt": "2026-08-19T04:00:00Z",
  "facts": {
    "buildState": { "value": "failed", "observedAt": "...", "byStepRunId": "sr_..." }
  },
  "budgets": { "compile:Foo.cs:CS0103": 2 },
  "holds":   [ { "scope": "s_gate", "until": "2026-09-02T00:00:00Z" } ],
  "entries": [ /* append-only; a correction is a NEW entry, never a mutation */ ]
}
```

Every carried fact has a value, an observation time and the step run that produced it. A number
without a timestamp is a claim with no expiry, and the failure mode is that it decays optimistically
— it keeps reading as current long after it stopped being true.

### 2.3 Gate policies — three, not one

The single most consequential correction in the issue, and it holds up against source:
`Doctor-Common.ps1:1112-1125` defines **three** clear policies, verified by direct read:

| `clearPolicy` | Reasons | Platform equivalent |
|---|---|---|
| `stale_only` | `build_failed_code`, `thread_needs_attention`, `auth_expired` | **none today** — needs a predicate re-evaluated by a cheap `derive` step |
| `timed_retry` | `build_failed_tests`, `infra_blocker`, `retry_budget_exceeded`, `auto_complete_stuck` | **none today** — a gated run that *keeps executing a remediation step* |
| `manual_only` | `infra_blocker_stuck`, and the fail-closed default | `ask_user` |

`ask_user` models exactly one of the three. If `approval` is the only gate primitive, the majority
of pr-doctor's gates by frequency cannot be expressed at all. A fourth class, `blocked_external`
(blocked on a capability the runtime structurally does not possess — an interactive OAuth consent
only a human can click), is behaviourally distinct from all three: stop the expensive work
immediately, keep a cheap liveness probe, escalate on a schedule.

**This table is the go/no-go for the whole engine.** A design that ships only `manual` has not
replaced the artifact it set out to replace.

### 2.4 Pulse — fixed, with backoff as tick-local state

`CronScheduler.cs:344-360` recomputes `computedNext` from the expression on every scan and, when
that is sooner than the stored value, overwrites the stored value and continues. A run that
finished a tick and asked not to be woken for 30 minutes is dragged back to the expression's next
occurrence, silently. Correct for cron, where the expression is the source of truth; wrong for a
workflow, where the run state is.

The tempting fix is a self-set wake time via `ICronStore.SetNextRunAtAsync` — which is already the
documented narrow scheduler-owned write and would need no new persistence. **Recommend against it
as the primary mechanism.** pr-doctor is immune to this bug by construction: it keeps a fixed
5-minute pulse (`Register-PRDoctor.ps1:117-118`, `-RepetitionInterval (New-TimeSpan -Minutes 5)`,
verified) and recomputes "is it time yet" arithmetically from persisted timestamps every tick.
Backoff as tick-local state makes a missed tick, an early tick and a reboot all harmless; backoff
as scheduler state makes each of them a correctness question.

**Recommend: a dumb fixed heartbeat, with backoff expressed as timestamp arithmetic in the
ledger.** A gated run should ideally not pulse at all — `ask_user` is push-resumed on answer — but
`stale_only` and `timed_retry` gates *do* need the pulse, because nothing pushes "the external
system is still doing nothing". The `CronScheduler` stale-correction behaviour remains a real bug
worth fixing on its own merits; it is simply not a prerequisite here.

### 2.5 What this schema deliberately cannot express

- **Open-ended investigation.** A workflow may *launch* one as a step and await a structured
  verdict. It must never append nodes mid-run. A design that grows "let the agent add steps" has
  reinvented an agent loop with extra latency. Stated as a designed limit, not a gap.
- **Multi-actor concerns** — role-addressed gates whose target is a runtime query, mid-run handoff
  with per-step visibility, blackout windows, per-run cost budgets, single-step replay against
  historical input. All are **deferred, and recorded here so the deferral is deliberate**. None is
  discoverable from inside this instance: every consulted agent is single-actor, with one human who
  is owner, approver and sole escalation target.
- **Fan-out of unknown cardinality** with per-item lifecycle. Recorded as required by a real third
  shape and **not** modelled in v1; without it, triage workflows degenerate into one giant step
  looping internally, which is the machinery this would exist to delete.

---

## 3. SignalR client changes

Each change names a file and a behaviour. Files verified to exist on `main`.

### 3.1 Must land before a workflow run can render at all

| # | File | Behaviour |
|---|---|---|
| C1 | `src/domain/BotNexus.Domain.Wire/ConversationEnums.cs` | Append `Workflow = 4` with a doc comment stating the axis argument and that it is *not* an `IsUnattended` disjunct. |
| C2 | `.../BlazorClient.Core/Services/ConversationRenderProjection.cs` | Add an explicit `ConversationSource.Workflow => ConversationListGroup.Workflow` arm to `Group`, and a `"Workflow"` arm to `Badge`. **Leave `IsUnattended` untouched** — the `_ =>` default would otherwise silently group a workflow run as `Normal`. |
| C3 | `.../BlazorClient.Core/Services/ConversationRenderProjection.cs` | Add `ConversationListGroup.Workflow` as a fifth enum value. |
| C4 | `.../BlazorClient.Core/Services/PortalConversationGrouping.cs` | Emit the new group in picker and list ordering, after Scheduled/Webhooks. Its `IsScheduled` partition must not claim workflow rows. |
| C5 | `.../BlazorClient.Core/Services/ClientStateModels.cs` | Carry `CurrentStepName` and `RunStatus` on `ConversationState` so the list row can render "step 4 of 9" without a REST round-trip. Init-only, seeded from the server payload, per Rule 1. |

C1–C4 are the irreducible set: without them a workflow conversation renders as an ungrouped,
unbadged normal conversation. C5 is what makes the list answer "where is this run at?" — the
question the spike exists to make answerable.

### 3.2 Polish

| # | File | Behaviour |
|---|---|---|
| P1 | `src/gateway/BotNexus.Gateway/Tools/TodoTool.cs` | Allow the runtime (not only an agent) to write the per-conversation checklist, so a `done` transition on a workflow step is a machine fact rather than an agent claim. |
| P2 | `.../SignalR/IGatewayHubClient.cs` + `SignalRTodoNotifier.cs` | Reuse the existing `TodoUpdated(agentId, conversationId, todoJson)` push. **No new hub method is required** — this is the single largest saving available, and it is why the checklist is the right surface for run position. |
| P3 | `.../BlazorClient.Core/Services/GatewayEventHandler.cs` | `HandleTodoUpdated` already routes a live todo payload to the conversation (line 737). It needs no change; it needs only to be *fed*. |
| P4 | `src/gateway/BotNexus.Gateway/Tools/CanvasTool.cs` | Render the run's graph on the Canvas with node styling driven by ledger state. Canvas already has server-side `set_state`/`get_state` and a `submitToAgent` bridge — enough for a status board with no new UI primitive. |

**The finding worth carrying out of this section: the render path is nearly free.** The todo
primitive already persists on the conversation row, already pushes over SignalR, and the client
already applies it live. Making run position visible is mostly a matter of writing to a surface
that exists, not building one.

---

## 4. Artifact mapping

pr-doctor's hand-built machinery, mapped to the platform primitive that would replace each.
Verified by direct read of the skill's own sources.

| # | Artifact | Platform primitive | Gap |
|---|---|---|---|
| 1 | `status.json` — current step, prior tip SHA, retry counters | Run ledger ([2.2](#22-run-record-a-ledger-not-a-step-pointer)) | **Full replacement.** Note the correction: the file is `status.json`, written by `Save-DoctorStatus`; there is no `state.json`. |
| 2 | `events.jsonl` — append-only event stream | Ledger `entries[]`, append-only with corrections as new entries | **Full replacement**, and an improvement: typed and queryable rather than newline-delimited prose. |
| 3 | `escalation.json` — the gate | `ask_user` covers **`manual_only` only** | **Partial — the largest gap on this page.** `stale_only` and `timed_retry` have no platform equivalent at all. See [2.3](#23-gate-policies--three-not-one). |
| 4 | `foreground-lease.json` — TTL 15 min, no renewal | **No replacement.** Needs a run-level `driver` field with a yield rule | The concept survives even though the file should not. BotNexus has the underlying need *today*: an interactive conversation and a scheduled tick can both drive the same run. |
| 5 | Retry budget keyed by runtime signature (`compile:{file}:{code}`, `test:{fqn}`) | Ledger `budgets{}` map | **No replacement today.** No engine surveyed offers a dynamically-keyed budget; all offer static per-activity retry policies. Net-new cost, unavoidable. |
| 6 | Self-unregister on terminal state (`Watch-PR.ps1:594`, `:633`) | Run `status: terminal` + the existing `deleteJobAfterRun` cron lifecycle | **Full replacement**, and strictly better: the scheduler owns the deletion rather than the job deleting itself mid-run. |
| 7 | The Windows Scheduled Task clock (5-min repetition) | `CronScheduler` as the pulse | **Replacement with one caveat**: a tick must be able to cost zero tokens. Today it cannot — every cron tick that reaches an agent is a full LLM turn. This is the prerequisite, not a detail. |

Three artifacts have **no** replacement: the foreground lease (4), the signature-keyed retry budget
(5), and two of the three gate policies (3). Two more depend on capabilities that do not exist yet:
the cheap tick (7) and the runtime-written checklist (P1). That is the honest size of the gap.

One artifact is correctly *dropped* rather than replaced: `doctor.log` is a stdout redirect, and
`merge-summary.json` is a terminal event that belongs as the last record in the event stream.

---

## 5. Corrections to the issue body

Re-checking premises against source found five that do not hold. They are recorded rather than
silently fixed, so the reasoning trail stays intact.

| # | Issue says | Source says |
|---|---|---|
| 1 | `state.json` holds current step and retry counters | The file is **`status.json`** (`Doctor-Common.ps1:18`, `:435`). No `state.json` exists in the skill. |
| 2 | 77 files (body), corrected to 76 (Addendum 2) | **75 files.** `Doctor-Common.ps1` is 2,949 lines / 147 KB; its test file 2,820 lines / 150 KB; `Watch-PR.ps1` 1,772 lines / 107 KB. All three line counts differ from both figures in the issue. |
| 3 | AC9: `conversations` table, `ConversationRowMapper` and `SqliteConversationStore` changes are required | **None are.** `source` persists as a *string* and parses via `Enum.TryParse` with a `Channel` fallback. Adding a member needs no persistence change whatsoever. |
| 4 | Back-compat holds "since 0 remains the default" | True conclusion, wrong mechanism — the ordinal is never persisted. The real guarantee is the string parse plus fallback. |
| 5 | `escalation.json` "halts every tick" | Only under `manual_only`. Verified at `Doctor-Common.ps1:1112-1125`. |

The pattern is worth naming: **four of five corrections are cases where the issue reasoned from a
plausible mechanism instead of reading the one in use, and three of the four still reached the
right conclusion.** A right answer from a wrong mechanism is not a safe answer — it stops being
right the moment the mechanism changes.

---

## 6. What to do next

Recommended order, smallest first. Each is independently useful and none commits to the engine.

1. **A cheap tick** — [#3351](https://github.com/sytone/botnexus/issues/3351). Let a cron job
   advance a loop without billing an LLM turn. This is the measured defect, it is the prerequisite
   for artifact 7, and it stands alone.
2. **`ConversationSource.Workflow = 4`** plus C1–C4. No persistence migration; a self-contained,
   reviewable change that makes the value available before anything depends on it.
3. **Runtime-written todo** (P1/P2). Turns the checklist into a machine fact and makes run position
   visible using a surface that already exists end-to-end.
4. **The gate policy model** ([2.3](#23-gate-policies--three-not-one)) — the genuine research
   problem, and the one to attempt only once 1–3 are shipped.

The `CronScheduler` stale-`NextRunAt` correction ([2.4](#24-pulse--fixed-with-backoff-as-tick-local-state))
is a real defect on its own merits and is tracked separately as
[#3350](https://github.com/sytone/botnexus/issues/3350).
