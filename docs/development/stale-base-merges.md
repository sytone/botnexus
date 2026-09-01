# Stale-base merges and tree-wide rules

## Why this exists

On 2026-08-14 `main` went red without any single pull request being wrong.

- `8fa88a1f` (PR #3148, 07:44:32 -0700) introduced the primitive-ID parameter fence from
  issue #3099 — a **tree-wide** architecture rule that scans every non-boundary source file.
- `c97109df` (PR #3164, 07:48:20 -0700) introduced
  `src/gateway/BotNexus.Gateway/Providers/EmbeddingAwareMemoryStoreFactory.cs`, which declares
  two primitive `agentId` parameters.

Neither branch contained the other's commit. `#3164`'s `core-tests` result was **correct for
its base**: the fence did not exist there. The first tree that ever contained both the rule and
the violation was `main` itself, and run `31811335987` failed on it. Thirteen merges landed in
roughly five minutes, all on mutually stale bases.

This is structural, not a one-off. It is available to **any** rule whose verdict depends on the
whole tree rather than on the diff:

| Rule | Where |
| --- | --- |
| `PrimitiveIdParameterFenceArchitectureTests` | `tests/architecture/BotNexus.Architecture.Tests/` |
| `ConfigFieldCoverage` | `tests/architecture/BotNexus.Architecture.Tests/` |
| `CoreTestScopeConsistency` | `tests/architecture/BotNexus.Architecture.Tests/` |

Per-file unit tests are **not** vulnerable in this way, because a file and the test that covers
it move together in the same commit. A tree-wide rule and a violation of it can move separately,
which is precisely the hole.

## The mechanism

`.github/workflows/ci-base-freshness.yml` contains two jobs.

### `base-freshness` (pull requests)

1. Checks out the PR head at its exact SHA.
2. Fetches and merges the **current tip of `main`** into it — a test-only merge that is never
   pushed. A conflict fails the job with an explicit "rebase or merge `main`" message, because
   the rules cannot be evaluated on a tree that does not exist.
3. Runs `BotNexus.Architecture.Tests` against that **merged** tree.

The critical difference from `core-tests` in `ci-build-test.yml`: `core-tests` answers *"is this
PR green against the base it was branched from?"*. `base-freshness` answers *"is the tree that
will exist on `main` after this merges green?"*. In the #3148/#3164 race, `core-tests` was
correctly green for both PRs and `base-freshness` would have been red for whichever merged
second — which is the outcome the acceptance criterion asks for.

Only the architecture project runs, so the job costs a fraction of a full suite while covering
every rule class that a stale base can defeat.

### `main-health` (schedule / manual)

Scheduled at `*/15 * * * *`. It reads the latest `CI: Build & Test` conclusion for `main` and,
if it is `failure`, emits a workflow `::error::` annotation naming the red commit and run ID,
and fails. A red `main` therefore announces itself on its own, instead of waiting to be
discovered by whoever happens to open the next unrelated PR.

**The cadence is best-effort, and in practice it is far below the declared one.** GitHub queues
`schedule` events on a best-effort basis and drops them under load. Measured over a 147-hour
window this workflow fired **46** scheduled runs against the ~588 implied by `*/15` — about 8%
of its nominal rate — with observed gaps between consecutive runs ranging from **2.2 to 11.4
hours**. In the 2026-08-30 incident the break landed at 20:59Z and the first failing probe ran
at 22:46Z, 1 h 47 m later.

So treat this job as a **multi-hour backstop, not a 15-minute alarm**. In particular, the
absence of a recent failing run is not evidence that `main` is green — it is at least as likely
that the probe has not run. Check the newest `CI: Build & Test` conclusion for `main` directly
when freshness matters. See #3715 for the measurement and the proposed fix.

### Inherited vs introduced

When `base-freshness` fails it queries the latest `main` conclusion and writes a **Verdict**
section into the GitHub step summary:

- `main` is `failure` → **INHERITED (probably).** Compare failing test names with the linked
  `main` run before touching any code.
- otherwise → **INTRODUCED.** A tree-wide rule fails on the post-merge tree even though the PR
  may be green against its own base.

That distinction previously required opening two runs and diffing their logs by hand. It is now
a sentence at the top of the failing job.

## What is still not covered in-repo

A workflow can make a stale-base violation **visible and failing**; it cannot make GitHub refuse
the merge button. That requires repository settings, which are not files in this repository:

> On the `main` branch protection rule (or ruleset), enable
> **"Require branches to be up to date before merging"** and add
> **`base freshness (tree-wide rules on merged tree)`** to the required status checks.

Without that setting, `base-freshness` is advisory: it will be red on the offending PR, but
nothing stops an administrator merging anyway. A GitHub **merge queue** on `main` is the
equivalent alternative — it builds the prospective merged tree before landing it, which is the
same guarantee obtained from GitHub rather than from this workflow.

## Do not "fix" a fence by widening its baseline

Every fence baseline in `BotNexus.Architecture.Tests` is **shrink-only**. When
`base-freshness` reports a tree-wide violation, fix the violating code — convert the primitive
to its typed ID, add the missing config coverage, correct the test scope. Adding the offending
file to a baseline converts a caught regression into a permanent one and defeats the entire
point of the fence.

## Related

- Issue #3173 (this mechanism), #3099 (the fence), #3148, #3164, #2977 (a previous round of
  inherited-red PRs), #2855.
- [`pre-commit-gate.md`](pre-commit-gate.md) — the local/remote validation gate.
- [`running-tests.md`](running-tests.md) — test scopes and what `core` means.
