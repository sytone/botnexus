<!--
BotNexus standard PR template. Full guidance: docs/development/pr-and-commit-conventions.md
Delete any section that genuinely does not apply — do not leave empty headings.
The PR title IS the squash-commit subject: <type>(<scope>): <lowercase imperative, no period>
-->

## Summary

<!-- One or two sentences: what problem this solves and the outcome. Not a diff restatement. -->

Closes #

## Root cause

<!-- Required for `fix`. The actual mechanism, not the symptom. Say which layer was wrong and why the
     obvious suspect was NOT the defect if you ruled one out. Omit this section for `feat`/`docs`/`chore`. -->

## Changes

<!-- Bulleted, one bullet per meaningful behavioural change, most important first.
     Bold the subject of each bullet. Reference the type/file that owns the change. -->

-

## Anti-reinvention

<!-- Agent PRs duplicate existing utilities more than human PRs do. State explicitly:
     - what existing seam/pattern/helper this reuses, OR
     - why a new abstraction was necessary and what you searched before adding it.
     "New helper X; no existing equivalent found (searched: A, B)" is a valid answer. -->

## Tests

<!-- What new tests exist and what behaviour each pins. For a bug fix, name the test that
     fails on the pre-change code — a fix without a proven-red test is incomplete. -->

-

## Validation

<!-- Evidence, with counts. Never "tests pass". Note any suite skipped and why (link the issue). -->

- Build: **0 warnings / 0 errors**
- `<Project>.Tests`: **N passed / 0 failed**
- `Architecture.Tests`: **N** · `Scenarios.Tests`: **N**

## UI evidence

<!-- REQUIRED when this PR changes rendered UI (*.razor, *.scss, wwwroot/**, the BlazorClient
     projects). Delete this section entirely for backend-only changes.

     Attach a screenshot or screen recording of the new capability being exercised with REAL
     generating agents and live conversations — a still of an empty shell does not show a
     reviewer that streaming, incremental rendering, or state transitions actually work.
     Prefer a recording for anything animated or progressive.

     Cover the states a reviewer cannot infer from the diff: empty, loading/generating,
     populated, and error. If the change genuinely has no visible delta (pure refactor),
     write "No visible UI change — pure refactor" instead of attaching media. -->

## Documentation impact

<!-- Issue #2865, rule 4. Documentation drifts because nobody is asked at the time the code
     changes. This is that question. Tick exactly one.

     The docs-lint workflow enforces the same rule mechanically for changes to an extension
     manifest, a provider interface, or a controller route: with no `docs/` change in the PR
     it fails unless the body contains `no-docs-impact`. -->

- [ ] Documentation updated in this PR (list the pages under **Changes**)
- [ ] Follow-up docs issue opened: #
- [ ] `no-docs-impact` — reason:

## Risk & rollback

<!-- Blast radius and how to undo. Call out anything a reviewer would regret discovering post-merge. -->

- **Risk**: low | medium | high —
- **Rollback**: revert this commit | flag flip | config revert
- **CI integrity**: no tests removed, renamed, skipped, or weakened; no coverage threshold changed

## Merge notes

<!-- Operational facts for the merger: new files/stores created, migrations, additive-vs-breaking API
     surface, parallel-merge safety vs other open PRs, and anything deliberately left out of scope. -->

-
