# Verified maintenance PR file footprints

Maintenance admission must reserve **every changed path** in every open PR before
checking candidate disjointness. A nested `gh pr list/view --json files` connection
is not completeness evidence: PR #3557 returned 100 of its 122 changed paths.
Increasing the outer PR list limit does not fix nested file truncation.

## Collect before admission

Use `scripts/maintenance/Get-PullRequestFileFootprints.ps1` with the complete,
separately verified open-PR number inventory. The collector does not discover PRs
or claim work. Authenticate as the caller's approved GitHub identity first; it
uses `gh api` without changing credentials, environment variables, Git config or
remotes.

```powershell
# $openPrNumbers comes from the caller's complete open-PR inventory, not a capped list.
$footprints = & ./scripts/maintenance/Get-PullRequestFileFootprints.ps1 -Repository Sytone/botnexus -PullRequestNumbers $openPrNumbers
if ($null -eq $footprints -or -not $footprints.isComplete) {
    throw 'PR footprint verification failed; stop admission.'
}
$state.reservedFiles = @($footprints.reservedFiles) + @($otherOwnedFiles)
# Persist $state, then invoke the existing planner with -StatePath.
```

Invoke from PowerShell to retain the structured object and array parameters. To
persist evidence, pipe the successful object to `ConvertTo-Json -Depth 10` and
write to the caller's scratch evidence directory. The planner remains the policy
boundary: `Get-MaintenanceDispatchPlan.ps1` consumes `reservedFiles` unchanged and
rejects exact overlaps. Do not pass raw nested `files` data or discard existing
worker/manual reservations when composing the state.

**Any exception blocks the whole admission decision.** Never catch a collection
failure and substitute an empty array, reuse an earlier map without freshness
verification, or feed partial PR results to the planner. An empty PR-number input
is rejected: the caller must separately prove a genuinely empty open-PR inventory.
A zero-file PR is allowed only after valid metadata and an explicit empty file
page agree. No map is emitted until all requested PRs pass.

## Completion evidence

The returned object contains `repository`, `collectedAtUtc`, `isComplete`,
`pullRequests` and the exact-path union `reservedFiles`. Every PR record contains:

| Field | Evidence |
|---|---|
| `number`, `files` | PR identity and exact REST `filename` values in page order |
| `expectedCount`, `actualCount` | REST PR `changed_files` compared with unique file records |
| `pages` | Each sequential page's number, endpoint and actual record count |
| `headSha`, `baseSha` | Metadata versions checked before and after file enumeration |
| `isComplete`, `evidence` | Passed pagination, uniqueness, exact-count and snapshot checks |

Pages explicitly request `per_page=100&page=N`. A short page ends enumeration;
it does **not** waive the exact-count guard. An exact multiple of 100 requires
an additional empty terminal page. Duplicate records/pages, missing pages,
malformed JSON, malformed filenames/metadata, HTTP failures, oversized pages,
count mismatches and a changed head/base/count all throw. A server-side file-list
cap cannot silently become success: if fewer paths than `changed_files` are
available, admission stops.

Filenames are neither trimmed nor case-folded. Deleted paths remain reservations.
The collector records REST `filename`, not rename aliases; it does not infer
additional ownership policy. The union deduplicates exact ordinal strings across
PRs, while retaining each PR's own count and paths. The existing planner's
case-insensitive overlap policy remains unchanged.

This is a point-in-time collection, not an atomic GitHub lock. Recollect after PR
updates or an inventory change and immediately before a new admission decision.
Before/after SHA and count checks detect observed movement during each PR read;
they cannot prevent an update after verification.

## Offline regression tests

```powershell
pwsh -NoProfile -File scripts/maintenance/Get-PullRequestFileFootprints.Tests.ps1
```

The standalone PowerShell fixture suite starts no gateway or .NET test host. It
injects a request scriptblock that accepts one REST endpoint and returns one raw
JSON string, throwing on transport failure. Its temporary directory is unique
and removed in `finally`; `-ScratchRoot` allows caller-selected scratch storage.

The 122-path fixture invokes the **unchanged** planner and verifies that a
candidate touching path 122 is rejected for `file-overlap`. Other fixtures cover
small/multiple/exact-boundary pages, malformed and duplicate responses, count
guards, snapshot movement, multiple PRs, zero-file verification and caller
environment preservation. `-CollectorPath` supports testing temporary mutated
collector copies without editing the planner or weakening any assertion.

The runtime maintenance playbook is owned outside the repository. Its caller must
use this same verified-object contract; this page does not silently update that
runtime configuration. See [autonomous maintenance orchestration](autonomous-maintenance-orchestration.md)
for the planner and admission workflow.
