# Verified PR freshness for maintenance

Run `pwsh -NoProfile -File scripts/ci-pr-status.ps1` to collect the CI board.
Freshness and check health are independent: passing checks do not establish that
 a branch contains its current target, and unavailable freshness does not mean
that a check failed.

## Evidence contract

The collector captures `headRefOid` and `baseRefName` from the PR list, resolves
`git/ref/heads/<encoded target branch>` independently in the target repository,
then compares **that immutable current-target SHA against the captured head SHA**.
Fork branch names are never resolved as unqualified names in the target repository.
The PR object's `base.sha` / `baseRefOid` is deliberately not used: it can describe
an older base even when the compare request succeeds.

For example, the #3932 reproduction had a stale PR-object base comparison of
0 behind / 12 ahead, but the independently captured current-main comparison was
41 behind / 12 ahead. Both compare requests succeeded; only the second answered
the maintenance question.

| Field | Meaning |
| --- | --- |
| `freshnessStatus` | `verified` only after successful ref and compare requests with valid data; otherwise `unknown` |
| `targetRef`, `targetSha`, `headSha` | Target branch and immutable comparison inputs; target SHA is null if resolution failed |
| `behindBy`, `aheadBy` | Nonnegative integer counts when verified; both null when unknown |
| `freshnessDiagnostic` | Failure stage (`target-ref` or `compare`) and reason; null when verified |
| `ciStatus`, `awaitingAck` | Existing check/maintainer-ack classifications, independent of freshness |

A consumer may describe a PR as current against the captured target only when
`freshnessStatus == 'verified'` and `behindBy == 0`. Never cast missing/null counts
to an integer or treat `unknown` as current. Report the diagnostic and obtain new
evidence before any action requiring freshness. Counts are a snapshot, not a
promise that either branch has remained unchanged since capture.

HTTP/native-command failure, malformed JSON, non-object compare responses,
missing/null counters, strings, fractions and negative counters all fail closed.
A successful zero comparison remains distinct from all of these cases.

## Process exit contract

- **0:** a JSON board was emitted, including an empty array or rows with explicit
  unknown freshness. This is collection success, not proof of CI or freshness.
- **1:** board collection failed (for example, the PR list request failed or its
  response was malformed). A diagnostic is written to stderr, not mixed into JSON.

Incidental `gh` exit codes must not leak into the collector's process result.
Dot-sourcing loads functions without executing the board or exiting the caller.

## Offline regression suite

```powershell
Invoke-Pester -Path scripts/ci-pr-status.Tests.ps1
```

The suite retains the check-state and maintainer-ack assertions and adds actual
collector behavior with a fake `gh`, plus child-PowerShell entrypoint tests. No
GitHub access, .NET test host, or gateway is required for these tests.
