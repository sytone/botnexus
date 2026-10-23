# Triage flaky required-gate tests

This policy is for contributors and maintainers investigating a nondeterministic test in a required continuous integration (CI) check. Its goal is to repair shared validation without blaming unrelated changes or treating a passing retry as a fix. It records the contributor policy for [issue #4565](https://github.com/sytone/botnexus/issues/4565); it does not claim that every automation requirement in that issue is implemented.

## Confirm the failure class

A required-gate flake is a test that can both pass and fail without a relevant source change, in a check required for delivery. Before applying `ci:flaky-required-test`, record:

1. The exact test name, failing assertion or error signature, and affected required check.
2. Links to failing and passing runs, their commit SHAs, and the relevant result artifacts.
3. Evidence that the same immutable candidate both passes and fails, or equivalent evidence from unrelated-source candidates that rules out a relevant source change.

One failure, a timeout by itself, or a failed local harness does not prove nondeterminism. Keep the cause unknown until the evidence supports it. A reproducible deterministic regression still needs repair, but does not belong to this class merely because it affects a required check.

## Give confirmed shared failures P0 priority

Every open issue classified `ci:flaky-required-test` must also carry `priority:critical` and `source:build-validation`. P0 means the highest review and repair priority: address the shared required-gate defect ahead of ordinary feature, bug, refactor, documentation, and maintenance work. Keep an executable delivery status unless a named, evidenced external prerequisite prevents all safe progress.

This is a policy requirement, not a statement that repository automation currently enforces the label combination or queue ordering. Issue admission, scheduling, failure matching, and their fixtures are local maintainer tooling; do not copy them into product source to satisfy this policy. Repository-native CI checks and deterministic test repairs remain repository scope.

Preemption does not authorize cancelling an existing lane, discarding its work, changing capacity or security controls, or bypassing validation. Neither P0 priority nor green checks authorize a merge; human merge authorization is still required.

An ordinary test failure, build failure, harness initialization failure, worker timeout, or incomplete investigation is an execution problem, not evidence that the product issue is externally blocked. Repair or preserve the attempt without inventing a product dependency.

## Attribute recurrences without hiding red checks

Before opening another issue, compare the exact test and error signature with existing repair issues. Link a matching recurrence to its owning issue and make that attribution visible on the affected pull request (PR). Do not attribute a shared defect to the candidate merely because that candidate's check exposed it. If the signatures differ or the cause is unverified, preserve that uncertainty rather than declaring a match.

Keep the owning issue's queue-health evidence current:

| Field | Required evidence |
| --- | --- |
| Affected check and signature | Exact check, test name, and assertion or error |
| Recurrence count | Count of distinct linked failing runs; do not count repeated notifications as new failures |
| Most recent failure | Timestamp and direct run/result link, with candidate SHA |
| Passing observations | Linked runs and SHAs, recorded as observations rather than resolution |
| Affected PRs | Links to currently affected open PRs |
| Repair | Linked repair PR and its current validation state, or an explicit statement that none exists |

The affected required check must remain failing until validation genuinely passes. Attribution is not a waiver. A later passing run does not close, downgrade, remove the classification from, or reset the recurrence evidence of the open defect. Reruns collect evidence; they do not repair a race.

## Repair the cause, not the signal

Use a focused regression that controls the failure mechanism: for example, an explicit synchronization point for an ordering race or isolated ownership for shared state. Show that the regression fails on the pre-repair implementation, then passes with the repair. Preserve the original behavioral assertions.

The following do **not** satisfy the repair policy:

- Skipping or deleting the test, weakening its assertions, or reducing coverage.
- Merely increasing a timeout or serializing the complete suite.
- Converting a required failure into a passing warning or optional check.
- Repeating unchanged validation until one run passes.

A targeted ordering or isolation change must explain why it removes the actual race; its presence alone is not proof. Follow the repository's [validation rules](../../AGENTS.md#validation) and [validation receipt contract](validation-receipts.md). Do not start gateway-backed test infrastructure on a host running the live gateway.

## Closure checklist

Close the flake issue only when the evidence covers every item below:

1. Name the diagnosed failure mechanism and the merged repair that removes it.
2. Name a deterministic regression and link its pre-repair RED and repaired GREEN evidence.
3. Link repeated required-gate runs on the repaired source. Include parallel execution appropriate to the original failure mode, such as competing state owners or concurrent operations, rather than relying only on isolated runs.
4. Record exact source identities, run counts, concurrency conditions, result counts, and any remaining uncertainty. Choose repetition and concurrency to exercise the diagnosed mechanism; this policy does not invent a universal run count or promise statistical certainty.
5. Verify that required checks and assertions remain intact and that the linked repair is present on the target branch.

A single passing retry cannot satisfy this checklist. Keep the issue open when the deterministic regression or the repeated/parallel evidence is missing. See [issue conventions](issue-conventions.md) for partial PR links and [PR conventions](pr-and-commit-conventions.md) for presenting validation evidence.
