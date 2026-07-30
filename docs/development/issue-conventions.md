# Issue conventions

Companion to [`pr-and-commit-conventions.md`](pr-and-commit-conventions.md). That doc governs what
lands; this one governs what gets asked for.

## Why these templates exist

A heading census of all 1,248 issues in this repo (2026-07-29) found:

| Finding | Measurement |
|---|---|
| Issues with **no acceptance criteria at all** | **47%** (596 / 1,248) |
| Distinct headings meaning "fix" | 8 (`proposed fix`, `fix`, `suggested fix`, `recommended fix`, `proposed solution`, `proposed design`, `design`, `proposed shape`) |
| Distinct headings meaning "evidence" | 9 |
| Distinct headings meaning "problem" | 7 |

Sections present on **closed** (actioned) issues materially more often than on open ones:
`Impact` +6.0pp, `Files` +4.1pp, `Why it matters` +3.7pp, `Evidence` +2.4pp, `Out of scope` +2.1pp.

Those are the sections that made an issue actionable, so they are required rather than suggested.
The canonical section names are the *winners* of the census — adopting them renames the minority,
it does not impose a new vocabulary.

## The shared spine

Every issue, whatever its type:

```
## Summary               (required)
   ...type-specific sections...
## Acceptance criteria   (required)
## Files                 (optional)
## Related               (optional)
```

`Acceptance criteria` deliberately sits **after** the type-specific context: you cannot write
checkable clauses until the problem and the evidence are on the page.

**Clauses must be numbered (`1.`) or checkboxes (`- [ ]`).** Prose is rejected. This is not
formatting pedantry — the close gate is applied clause by clause, and an issue is closed only when
every clause is met. A partially-satisfying PR uses `Refs #N`, not `Closes #N`. Prose criteria
cannot be evaluated that way, so they silently become uncloseable or falsely closed.

## Type-specific requirements

| Type | Requires beyond the spine |
|---|---|
| `type:bug` | Evidence, Expected behaviour, Impact |
| `type:feature` | Problem, Out of scope |
| `type:improvement` | Current behaviour, Desired behaviour, Impact |
| `type:refactor` | Current shape, Proposed shape, **Behaviour parity** |
| `type:test` | Coverage gap, Risk, **Non-vacuity** |
| `type:docs` | Location, What is wrong |
| `type:chore` | Problem |
| `type:spike` | Question, Why it matters, Deliverable, **Timebox** |
| `type:epic` | Problem, Goal, Decomposition, Out of scope |

Three of those encode specific failures this repo has actually hit:

- **Behaviour parity** (refactor) — name the suite that proves nothing changed. A refactor with no
  parity evidence is a rewrite wearing a refactor label.
- **Non-vacuity** (test) — name the mutation or revert that must turn the new test red. A test that
  cannot fail is decoration; this repo has found five separate vacuous-pass clusters.
- **Timebox** (spike) — a spike without one is an open-ended dig.

`type:bug` asks for `Root cause` but does **not** require it. Say plainly when you have not
diagnosed it — a guess asserted as fact is worse than an admission, and several issues here have
had their stated premise disproven during implementation.

## Filing

**Humans** — use the issue forms. Blank issues are disabled; pick a type so the right questions get asked.

**Agents** — never hand-roll `gh issue create`. Use the script, which lints before filing:

```powershell
$s = 'skills/botnexus-maintenance/scripts/New-BotNexusIssue.ps1'
& $s -Action template -Type type:bug > tmp/issue.md
& $s -Action lint -Type type:bug -BodyFile tmp/issue.md
& $s -Action file -Type type:bug -Title '[Gateway] ...' -BodyFile tmp/issue.md `
     -Area area:platform -Priority priority:high -DryRun
```

The forms and the agent templates are generated from one schema
(`skills/botnexus-maintenance/reference/issue-schema.json`), so they cannot drift apart.

## Labels

Five namespaces with enforced cardinality:

| Namespace | Cardinality | Members |
|---|---|---|
| `type:` | **exactly one** | bug, feature, improvement, refactor, chore, test, docs, spike, epic |
| `priority:` | at most one | critical, high, medium, low |
| `area:` | zero or more | platform, channels, portal, mobile, security, tooling |
| `status:` | zero or more | blocked, needs-jon-decision, in-progress |
| `source:` | zero or more | opencode, team-servicebus, build-validation, user-feedback, dependabot |

The `squad:` namespace was retired on 2026-07-29 along with the multi-agent squad workflow it served. Ownership is expressed by assignee, not by label.

`type:` maps 1:1 to conventional-commit types, which is what makes PR labelling mechanical:
`fix`→bug, `feat`→feature, `perf`→improvement, `refactor`→refactor, `test`→test, `docs`→docs,
`chore`/`ci`/`build`/`style`→chore.

**`refactor` vs `improvement` vs `chore`** — the distinction is behaviour. Refactor preserves it;
improvement changes it for the better on something that already works; chore changes no product
behaviour at all (dependencies, CI, build).

Labels are written only by `Set-BotNexusLabels.ps1`, against a closed allow-list. Bare legacy names
(`bug`, `enhancement`, `documentation`, …) were migrated and deleted on 2026-07-29 and no longer exist.

## Existing issues

Issues filed before these conventions are **not** retro-fitted. Rewriting an old body to satisfy a
schema destroys authored content and invents detail the original author never wrote. Improve an old
issue only when you are already working it and can add real information.
