# Autonomous maintenance orchestration

The repository ships `scripts/maintenance/Get-MaintenanceDispatchPlan.ps1` as the deterministic policy boundary for autonomous maintenance. Runtime playbooks and skills may collect state and execute the returned dispatches, but they should not duplicate or relax the gates encoded here.

## Event-driven cycle

Call the planner at cycle start and again when a worker completion event arrives. A completion callback is a push signal: pass `trigger: "worker-completed"` and the accumulated cycle telemetry back to the planner. Do not poll and do not create a follow-up cron job. `maxImplementationStartsPerCycle` bounds the initial and refill waves together.

The planner has separate `implementation`, `repair`, and validation-only `recovery` budgets. Repair therefore cannot consume implementation capacity. Recovery is admitted only for an existing worktree in `validation` or `shipping` phase; incomplete implementation remains normal implementation work and cannot bypass its gates.

## Input contract

Pass a JSON file with this shape:

```json
{
  "cycleId": "2026-06-05T20:00Z",
  "trigger": "cycle-started",
  "openPrCount": 2,
  "budgets": {
    "implementation": 2,
    "repair": 1,
    "recovery": 1,
    "maxImplementationStartsPerCycle": 4,
    "openPrSoftCap": 5
  },
  "validationMode": "local",
  "remoteValidation": {
    "active": 0,
    "maxConcurrent": 2,
    "committedCost": 0,
    "maxCost": 10
  },
  "workers": [],
  "reservedFiles": ["src/files/changed-by-an-open-pr.cs"],
  "candidates": [
    {
      "id": "issue-2169",
      "lane": "implementation",
      "trusted": true,
      "decisionFree": true,
      "files": ["scripts/maintenance/Get-MaintenanceDispatchPlan.ps1"],
      "validationRequired": true,
      "estimatedValidationCost": 2
    }
  ],
  "telemetry": {
    "implementationStarts": 0,
    "workersCompleted": 0,
    "workerMinutes": 0,
    "validationMinutes": 0,
    "prsOpened": 0,
    "prsRepaired": 0
  }
}
```

Invoke it with:

```powershell
$plan = scripts/maintenance/Get-MaintenanceDispatchPlan.ps1 -StatePath maintenance-state.json
```

Candidate order is priority order. The runtime owns issue/PR discovery and must populate `trusted`, `decisionFree`, and the complete intended file set from authoritative evidence. Put every path changed by an open PR in `reservedFiles`; active workers contribute their file sets automatically. Missing evidence fails closed, and an issue already assigned to an active worker is rejected as `already-active`.

Set `validationMode` to `local` or `remote`; omitted values default to `local`, matching the repository gate. Workers still run `scripts/repo/Validate-PreCommit.ps1`; the planner records the selected plane but does not replace strict validation. In local mode no remote capacity is reserved, eliminating the unhealthy remote runner from the current critical path while `Invoke-LocalValidation.ps1` globally serializes host work. In remote mode, `validationRequired` reserves capacity and `remoteValidation.maxConcurrent`/`maxCost` remain hard ceilings.

## Preserved gates

Before every dispatch the planner checks, in order:

- trusted-source evidence;
- decision-free eligibility;
- a non-empty file set and case-insensitive disjointness from open PRs, active workers, and new dispatches;
- one active assignment per candidate ID;
- bounded implementation waves and the projected open-PR soft cap;
- lane capacity;
- recovery phase plus existing worktree identity;
- remote validation concurrency and cost reservations only when remote mode is selected.

The planner never opens, updates, or merges a PR and never changes validation policy. PR review, merge decision, live PR-cap reconciliation, and post-worker validation remain explicit runtime responsibilities. Re-run the planner with fresh live state before each refill event; projected PR count prevents multiple implementation dispatches in one plan from overbooking the cap.

## Output and telemetry

The result contains:

- `dispatch`: admitted workers, lane, existing worktree for recovery, file set, and validation reservation;
- `blockers`: a concrete reason for each rejected candidate;
- `idleCapacity`: unused capacity by lane;
- `telemetry`: candidates selected, workers started/completed, implementation starts, worker/validation minutes, PRs opened/repaired, remote reservations/cost, and projected PR count.

Persist one result per trigger and aggregate by `cycleId`. Report PR repair separately from new issue PRs. Low throughput can then be attributed to trust/decision/file gates, PR cap, lane saturation, remote limits, cost, or lack of candidates instead of appearing as unexplained idle time.

## Tests

Run the focused deterministic suite on PowerShell 7:

```powershell
scripts/maintenance/Get-MaintenanceDispatchPlan.Tests.ps1
```

The tests cover independent lanes, push-based refill, every preserved gate, validation ceilings, existing-worktree recovery, and telemetry accumulation.


## Throughput proof and production soak

Issue #2169 adds an executable proof extension under `scripts/maintenance`. `Invoke-MaintenanceThroughputProof.ps1` uses real temporary Git worktrees, marker commits, tree receipts, a chronological JSONL event trace, and a PR manifest to demonstrate one repair lane alongside two implementation lanes and a push-style refill. Its baseline and comparative report includes worker starts/completions, implementation starts, PRs opened/repaired, worker minutes, validation minutes, cycle time, throughput per hour, deltas, and multiplier.

`Invoke-MaintenancePreProductionE2E.ps1` regenerates the checked-in preproduction/E2E evidence. Negative controls cover the PR cap, file overlap, invalid recovery, duplicate assignment, and a refill blocked by the cycle wave limit. These deterministic artifacts are evidence of mechanics only. `Aggregate-MaintenanceCycleRecords.ps1` requires at least three qualifying records explicitly marked `production`; the checked-in empty production soak has `productionCriterionMet: false`, so generated proof data cannot claim production readiness.

Focused validation: `pwsh -NoProfile -File scripts/maintenance/Invoke-MaintenanceThroughputProof.Tests.ps1`.
