[CmdletBinding()]param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$failures = [Collections.Generic.List[string]]::new()
function Assert-True([bool]$Condition,[string]$Message){if(-not $Condition){$failures.Add($Message)}}
function Assert-Equal([object]$Expected,[object]$Actual,[string]$Message){if($Expected-ne$Actual){$failures.Add("$Message Expected '$Expected', got '$Actual'.")}}
$output = Join-Path ([IO.Path]::GetTempPath()) ('proof-test-' + [guid]::NewGuid().ToString('N'))
try {
    $report = & (Join-Path $PSScriptRoot 'Invoke-MaintenanceThroughputProof.ps1') -OutputDirectory $output
    Assert-Equal 3 $report.plannerEvidence.initialDispatchCount 'Initial dispatch must come from the checked-in orchestration/planner path.'
    Assert-Equal 1 $report.plannerEvidence.refillDispatchCount 'Worker completion must invoke orchestration and refill one slot.'
    Assert-Equal 4 $report.treeReceipts.Count 'Proof should retain four actual worktree receipts.'
    Assert-True (@($report.treeReceipts | Where-Object { [string]::IsNullOrWhiteSpace($_.commit) -or [string]::IsNullOrWhiteSpace($_.tree) -or [string]::IsNullOrWhiteSpace($_.marker) }).Count -eq 0) 'Receipts must include marker, commit, and tree evidence.'
    Assert-Equal 4 $report.prManifest.Count 'PR manifest should describe four verified PRs.'
    Assert-True (@($report.prManifest | Where-Object { -not $_.verified }).Count -eq 0) 'Every manifest PR must be verified.'
    $events = @($report.events | Sort-Object sequence)
    Assert-Equal (($events.Count * ($events.Count + 1)) / 2) (($events.sequence | Measure-Object -Sum).Sum) 'Sequences must be contiguous.'
    Assert-True (@($events | Where-Object { [string]::IsNullOrWhiteSpace($_.timestampUtc) -or [string]::IsNullOrWhiteSpace($_.correlationId) -or [string]::IsNullOrWhiteSpace($_.provenance) }).Count -eq 0) 'Every event needs a machine timestamp, correlation, and provenance.'
    foreach ($event in $events) { $parsed=[DateTimeOffset]::MinValue; Assert-True ([DateTimeOffset]::TryParse($event.timestampUtc,[ref]$parsed)) "Invalid timestamp for sequence $($event.sequence)." }
    Assert-Equal 4 @($events | Where-Object event -eq 'validation-queued').Count 'Validation queue events must be observable.'
    Assert-Equal 4 @($events | Where-Object event -eq 'validation-completed').Count 'Validation completion events must be observable.'
    Assert-Equal 4 @($events | Where-Object event -eq 'pr-verified').Count 'PR verification events must be observable.'
    Assert-Equal (@($events | Where-Object event -eq 'pr-verified').Count) $report.actual.verifiedPrs 'Verified PR metric must be event-derived.'
    Assert-Equal 2 $report.actual.maxImplementationConcurrency 'Implementation concurrency must be event-derived.'
    Assert-Equal 1 $report.actual.maxRepairConcurrency 'Repair concurrency must be event-derived.'
    Assert-Equal 0 $report.actual.duplicateCount 'Actual dispatches must contain no duplicate assignments.'
    Assert-Equal 0 $report.actual.actualPrCapViolations 'Actual run must have no PR-cap violations.'
    Assert-Equal 0 $report.actual.actualFileOverlapViolations 'Actual run must have no file-overlap violations.'
    Assert-Equal 1 $report.actual.capacityBlocks 'Refill planner should record its wave-limit capacity blocker.'
    foreach ($controlName in @('prCap','fileOverlap','invalidRecovery','duplicateAssignment','waveLimit')) {
        $control = $report.negativeControls.$controlName
        Assert-True ([bool]$control.blocked) "Planner control $controlName must block."
        Assert-True (@($control.observedReasons).Count -gt 0) "Planner control $controlName must preserve observed blocker evidence."
    }
    foreach ($metric in @('verifiedPrs','timeToFirstVerifiedPrMinutes','timeToSecondVerifiedPrMinutes','idleSlotMinutes','maxImplementationConcurrency','maxRepairConcurrency','validationQueueMinutes','validationRuntimeMinutes','recoveryCount','duplicateCount','capacityBlocks','actualPrCapViolations','actualFileOverlapViolations')) { Assert-True ($null -ne $report.comparative.$metric) "Comparative metric $metric is required." }
    if ($failures.Count) { $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }; exit 1 }
    Write-Host 'Maintenance throughput proof tests passed.' -ForegroundColor Green
}
finally { Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue }
