# PR and Commit Conventions

BotNexus is a squash-merge repository with a high proportion of agent-authored changes. That combination
makes the PR body and the squash-commit message the *only* durable record of why a change exists — the
branch commits are discarded at merge. This page defines the required format for both.

Applies to every contributor, human or agent.

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

## Reviewer inspection order

For agent-authored PRs, review in this order — it front-loads the most expensive defects:

1. CI and workflow files — was anything weakened, skipped, or gated away?
2. New helpers and utilities — does an equivalent already exist? Require consolidation, not a comment.
3. The single most critical path in the diff — trace it input-to-output, check zero/max/empty boundaries
   and permission checks on every branch.
4. The evidence — do the claimed suites and counts match what CI actually ran?
