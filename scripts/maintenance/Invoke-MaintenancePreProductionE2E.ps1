[CmdletBinding()]param([string]$OutputDirectory=(Join-Path $PSScriptRoot 'artifacts/generated'))
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$report = & (Join-Path $PSScriptRoot 'Invoke-MaintenanceThroughputProof.ps1') -OutputDirectory $OutputDirectory
$controls = @($report.negativeControls.Values)
$e2e = [pscustomobject]@{
    schemaVersion='2.0';environment='preproduction';scenario='planner-orchestrated-repair-two-implementations-refill'
    criterionMet=($report.criterionMet -and @($controls | Where-Object { -not $_.blocked }).Count -eq 0)
    correlationId=$report.correlationId;verifiedPrs=$report.actual.verifiedPrs
    initialPlannerDispatches=$report.plannerEvidence.initialDispatchCount;refillPlannerDispatches=$report.plannerEvidence.refillDispatchCount
    proof='maintenance-throughput-proof.json';eventTrace='maintenance-event-trace.jsonl';prManifest='maintenance-pr-manifest.json'
}
$e2e | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $OutputDirectory 'maintenance-preproduction-e2e.json') -Encoding utf8NoBOM
$e2e

