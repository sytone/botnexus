# PR and Commit Conventions

BotNexus is a squash-merge repository with a high proportion of agent-authored changes. That combination
makes the PR body and the squash-commit message the *only* durable record of why a change exists — the
branch commits are discarded at merge. This page defines the required format for both.

Applies to every contributor, human or agent.

For what a good *issue* looks like, see [issue-conventions.md](issue-conventions.md).

## Why this is stricter than a typical repo

An audit of 44 consecutive PRs found 100% Conventional-Commits-compliant titles but seventeen different
heading vocabularies in the bodies, with root cause stated in 13% and risk/rollback in 6%. Agent-generated
code is measurably more prone to three defect classes that a reviewer cannot spot by skimming a clean diff:

| Defect class | What it looks like | Which section catches it |
|---|---|---|
| **CI gaming** | Tests removed, renamed, skipped, or thresholds lowered to get green | Risk & rollback → CI integrity |
| **Reuse blindness** | A new helper that duplicates one already in the codebase | Anti-reinvention |
| **Hallucinated correctness** | Compiles, passes every test, still wrong on a boundary | Root cause + proven-red test |

The template exists to force those three into the open before a human opens the diff.

## The title is the squash subject

The PR title becomes the squash-merge commit subject and flows directly into the changelog. It must be a
valid [Conventional Commit](https://www.conventionalcommits.org/) subject:

```
<type>(<scope>): <short description>
```

- **Types**: `feat`, `fix`, `chore`, `docs`, `refactor`, `test`, `perf`, `style`, `ci`, `build`
- **Scope**: optional but preferred — the issue number (`#2312`) or the component (`portal`, `cron`, `security`)
- **Description**: lowercase, imperative mood, no trailing period, ≤ 72 characters
- **Breaking changes**: append `!` — `feat(api)!: drop legacy tool descriptor shape`

The single commit on the branch must carry the same subject as the PR title. If a PR accumulates multiple
commits, the title still wins — it is what lands.

```
feat(portal): add sidebar nav ordering model with server-side overrides
fix(#2293): latch route guard on the agent-fallback branch
docs: standardise pr body and squash-commit templates
```

## Squash commit body

The subject alone is not the commit message. When squash-merging, replace GitHub's auto-generated body
(the list of branch commits) with this:

```
<type>(<scope>): <same subject as the PR title>

<Why the change exists, in 1–3 wrapped lines at 72 columns. State the root cause for a
fix, or the capability gained for a feature. Do not restate the diff.>

Refs: #<issue>
Validated-by: build 0/0; <Project>.Tests N/0; Architecture.Tests N; Scenarios.Tests N
Co-authored-by: <agent or human co-author>
```

The trailers are the machine-readable half. Keep them as `Key: value` lines in a contiguous block at the
end with no blank lines between them, so `git interpret-trailers` and changelog tooling can parse them.

- `Refs:` — the issue this closes or relates to. Always present.
- `Validated-by:` — the actual suites and counts that were run. `tests pass` is not acceptable.
- `Co-authored-by:` — attribute agent authorship honestly. Reviewers calibrate their depth on this.
- `BREAKING CHANGE:` — a footer, not a trailer; full prose describing the migration.

This replacement is **manual today** — see [Enforcement](#enforcement) for why the repository setting
that would automate it is not yet applied, and what happens when the textarea is left untouched.

Worked example:

```
fix(#2293): latch route guard on the agent-fallback branch

Home.razor recursed forever between StateChanged and NotifyChanged because the
idempotency guard was never set on the "agent not yet in Store.Agents" fallback
path, so every re-raised OnChanged re-entered it. Latches the guard before
notifying and adds a re-entrancy flag.

Refs: #2293
Validated-by: build 0/0; BlazorClient.Tests 914/0; Architecture.Tests 233; Scenarios.Tests 29
Co-authored-by: agent-farnsworth[bot] <293187211+agent-farnsworth[bot]@users.noreply.github.com>
```

## PR body

Use [`.github/pull_request_template.md`](https://github.com/Sytone/botnexus/blob/main/.github/pull_request_template.md), which GitHub prefills.
Sections, in order:

| Section | Required | Purpose |
|---|---|---|
| **Summary** | always | Problem and outcome in 1–2 sentences, plus `Closes #N` |
| **Root cause** | `fix` only | The mechanism that was actually wrong |
| **Changes** | always | One bullet per behavioural change, most important first |
| **Anti-reinvention** | always | What existing seam this reuses, or why a new one was justified |
| **Tests** | unless `docs`/`chore` | What each new test pins; the proven-red test for a fix |
| **Validation** | always | Suites and counts, plus any suite skipped and why |
| **UI evidence** | UI changes only | Screenshot/recording of the capability working with real agents |
| **Risk & rollback** | always | Blast radius, undo path, explicit CI-integrity statement |
| **Merge notes** | always | New files/stores, additive-vs-breaking surface, parallel-merge safety, out-of-scope |

Rules that matter more than the headings:

1. **Evidence is numeric.** `Gateway.Tests 4026/0/1` is evidence. "All tests pass" is a claim.
2. **A bug fix needs a test that fails on the pre-change code.** If one cannot be written, the
   understanding of the bug is incomplete — say so rather than shipping.
3. **Never weaken CI to get green.** A failing cross-platform test is usually a real cross-platform bug.
   Fix the production code, not the assertion.
4. **Scope must match the issue.** Before pushing, run `git diff --stat origin/main` and confirm the diff
   is scope-matched. Deletions of already-merged files mean the branch is contaminated — rebuild it off a
   fresh `origin/main` rather than pushing.
5. **Edit agent output before requesting review.** Agents are verbose. Cut anything the diff says better.

## Closing an issue: the clause-by-clause rule

`Closes #N` is a claim that **every** clause of issue N is satisfied. It is not a claim that a PR
related to N has landed. GitHub does not distinguish the two, so the discipline has to live here.

An issue is closed correctly only when each clause of its title and each acceptance-criteria item has
been individually verified against the code that actually shipped. A conjunction in the title
("X **and** Y", "X / Y", "without A **or** B") is a list of separate obligations, not a single one.

### Why this rule exists

Three issues were closed on the strength of their first clause. None was caught by review; all three
were caught months later by independent re-measurement, because a closed issue reads as settled and
the unshipped half becomes invisible.

| Issue | Clause that shipped | Clause that did not | How the gap surfaced |
|---|---|---|---|
| [#1780](https://github.com/Sytone/botnexus/issues/1780) | `Cache-Control` headers | **ETag** / `Last-Modified` — so conditional GETs stayed impossible and ~32 KB brotli was re-downloaded on every page load, on both surfaces, indefinitely | Weekly portal profiling → #2413 / PR #2422 |
| [#2104](https://github.com/Sytone/botnexus/issues/2104) | retry-storm suppression | **branch deletion after failed removal** — still live, orphaned `fix/2248` and `fix/2293` | Weekly tool-failure forensics → #2419 |
| [#2105](https://github.com/Sytone/botnexus/issues/2105) | M365 wrapper parameter schemas | **safe aliases** — failure rate went from 8/wk at close to 152/wk across 9 agents, the largest tool-failure cluster in the repo | Weekly tool-failure forensics → #2414 |

#1780 is the sharpest case: the missing capability was **named in the issue title** and the issue was
still closed without it. This is the same failure family as a vacuous test — a green signal that does
not prove the property it is trusted to prove.

### The rules

1. **Partial work uses `Refs:`, never `Closes`.** If a PR satisfies only some clauses of an issue, its
   body carries `Refs #N` and the issue stays open. Never `Closes #N` for partial scope, and never
   "close now, follow up later" — the follow-up is exactly what stops happening. If the remainder is
   genuinely a separate piece of work, file it as its own issue **first** and link it from the close
   comment, then close the original.
2. **The closing comment must enumerate clause coverage.** Walk the acceptance criteria in order and
   state, per clause, what satisfies it and where. Any clause that is *not* covered must be named
   explicitly along with the reason and the follow-up issue number. Silence about a clause is treated
   as a defect, not as coverage.
3. **Verify against what shipped, not against what the PR said.** Re-read the acceptance criteria at
   close time and check each one against the merged code. "A PR referencing this issue merged" is not
   evidence for any individual clause.
4. **Prefer splitting at filing time.** An issue whose title needs a conjunction is usually two issues.
   Decomposing at filing costs minutes; a clause discovered unshipped a quarter later costs a
   regression, a re-file and a second implementation cycle. See
   [issue-conventions.md](issue-conventions.md) for the acceptance-criteria format that makes clauses
   individually checkable.
5. **Re-measurement outranks closure.** Recurring analysis runs (tool-failure forensics, portal
   profiling, log analysis) must re-check previously-closed findings rather than assume them resolved.
   That is what caught all three cases above.

### Worked example

A PR that shipped `Cache-Control` but not `ETag` for #1780 should have carried `Refs #1780` in the
body, left the issue open, and closed with a comment shaped like this:

```markdown
Clause coverage for #1780:

- [x] `Cache-Control` on both static surfaces — `StaticAssetHeaders.cs`, PR #1801.
- [ ] **ETag / `Last-Modified`** — NOT covered. Conditional GETs remain impossible;
      the asset is re-downloaded on every load. Split out as #2413.

Closing as partially superseded: remaining clause tracked in #2413.
```

The unticked box is the point. A close comment that lists only what shipped is indistinguishable from
one where nothing was missed.

### What CI checks

The [PR conventions guard](#enforcement) raises an **advisory** warning when a PR body says
`Closes #N` and issue N still has an unticked `- [ ]` acceptance-criteria checkbox. It is a prompt to
re-read the criteria, not a gate: an unticked box is often legitimately satisfied but never ticked,
and the guard cannot tell. It never blocks, and it fails open if the issue cannot be read.

## UI evidence

A reviewer cannot tell from a `.razor` diff whether a UI change actually works. Any PR touching the
rendered UI surface must include a **UI evidence** section with a screenshot or screen recording.

The surface is detected by changed path:

- `src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/**`
- `src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile/**`
- `src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core/**`
- any `**/wwwroot/**`, `*.razor`, `*.razor.css`, `*.scss`

Test projects are excluded — changing a bUnit test renders no user-visible UI.

What makes the evidence useful:

- **Exercise it with real generating agents and live conversations.** A still of an empty shell proves
  the component mounts, not that it works. Streaming, incremental rendering, and state transitions are
  exactly the behaviours a diff hides and a screenshot of an idle page does not show.
- **Prefer a recording** for anything animated or progressive — token streaming, spinners, optimistic
  updates, scroll anchoring, sub-agent panels updating in the background.
- **Cover the states the reviewer cannot infer**: empty, loading/generating, populated, and error.
- **Mobile and desktop** when the change touches both client projects.
- If the change genuinely has no visible delta (a pure refactor), write
  `No visible UI change — pure refactor` instead of attaching media. The guard accepts that as an
  explicit opt-out; it does not accept silence.

## Generated vs hand-maintained shapes

When a change introduces a shape that must be kept in sync with another representation of the same
knowledge, **say so in the PR body and state whether generation was considered.**

The test is not "is this repetitive". It is: **if these two copies disagree, what happens?**

- *A compile error, or a loud failure* → hand-maintenance is fine. Say that and move on.
- *Nothing — it reads as a clean pass* → this is the recurring defect family in this repo (#2764 a
  doctor check reading a path nothing binds; #2767 one flag name declared twice with nothing binding
  them; #2700 a fence scoped where the violation could not occur). Generation is the lever that
  **removes** the second copy rather than defending it with one more fence.

One sentence in the PR body discharges this — for example *"the new hub event is declared only on
`IGatewayHubClient`; the client registration is hand-written and would fail silently if omitted —
covered by the generator candidate in #2770"*. This is a prompt to answer the question, not a gate:
no workflow blocks on it.

Prefer an **attribute** over a **declarative file**. An attribute sits on the thing it describes and
moves with it; a file is a second place to remember and can be orphaned. Use a file only where the
data must be enumerable *before* any code references it — `feature-flags.json` (#2769) is so far the
only such case.

The ranked inventory of candidate sites, with measured drift and the candidates explicitly rejected,
lives in [Source generator survey](./source-generator-survey.md).

## Enforcement

`.github/workflows/pr-conventions-guard.yml` checks every PR against these rules. It reuses the safety
model of the sensitive-file guard: it runs on `pull_request_target` but checks out only the **trusted
base copy** of its own script, reads changed files from the API rather than a working tree, and treats
the PR title and body as inert text that is regex-matched and never executed.

**The guard is currently warning-first (#2317).** It annotates the run and writes a job summary but does
not fail the check, so the in-flight PR queue can drain before the format becomes mandatory. Enforcement
flips by changing `ENFORCEMENT_MODE` to `"block"` in `.github/scripts/pr-conventions-guard.mjs`.

| Check | On flip to blocking |
|---|---|
| Conventional-Commits title, lowercase, no trailing period, ≤ 72 chars | blocks |
| Body contains `Closes #N` / `Refs #N` | blocks |
| Required sections present for the change type | blocks |
| `fix` PRs state a root cause | blocks |
| UI-touching PRs carry visual evidence or an explicit no-visible-change note | blocks |
| Numeric `Validated-by` evidence rather than "all tests pass" | advisory only |
| `Closes #N` against an issue with an unticked acceptance-criteria box | advisory only |

**Exemptions.** Automated external bot authors (Dependabot, Renovate, `github-actions[bot]`) are skipped:
they open mechanical dependency PRs with no root cause or UI evidence to give. `agent-farnsworth[bot]` is
deliberately **not** exempt — it authors most substantive changes here and is precisely what the guard
exists to hold to the standard.

**Waivers.** A maintainer (admin/maintain/write) can waive violations by commenting
`/allow-pr-convention-exception <head-sha>`. The waiver is bound to the current head SHA, so pushing a new
commit invalidates it.

**One gap CI cannot close, and it is currently open.** The squash body is typed into the merge-button
textarea *after* every check has passed, so no workflow can enforce it. The repository setting that would
close it — squash-merge default commit message set to **"pull request title and description"**
(`squash_merge_commit_message: PR_BODY`) — is **not applied**. The live value is `COMMIT_MESSAGES`:

```console
$ gh api repos/Sytone/botnexus --jq .squash_merge_commit_message
COMMIT_MESSAGES
```

With `COMMIT_MESSAGES` and the single-commit branches this repo ships, GitHub pre-fills the textarea with
the subject line only, so a merge performed without editing it lands a commit with an **empty body** — no
why-paragraph, no `Refs:`, no `Validated-by:`, no `Co-authored-by:`. Measured over the last 40 commits on
`main`: 30 had a body on their source branch and none of them retained it.

Until the setting is changed, **the merger must paste the PR body into the squash textarea by hand.**
Applying the setting is tracked by [#3731](https://github.com/Sytone/botnexus/issues/3731); it is a
repository-settings change, so it cannot be made from inside the repo.

## Reviewer inspection order

For agent-authored PRs, review in this order — it front-loads the most expensive defects:

1. CI and workflow files — was anything weakened, skipped, or gated away?
2. New helpers and utilities — does an equivalent already exist? Require consolidation, not a comment.
3. The single most critical path in the diff — trace it input-to-output, check zero/max/empty boundaries
   and permission checks on every branch.
4. The evidence — do the claimed suites and counts match what CI actually ran?
