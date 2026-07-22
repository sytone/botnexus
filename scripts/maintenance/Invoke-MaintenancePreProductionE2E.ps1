[CmdletBinding()]param([string]$OutputDirectory=(Join-Path $PSScriptRoot 'artifacts/generated'))
Set-StrictMode -Version Latest;$ErrorActionPreference='Stop'
$r=& (Join-Path $PSScriptRoot 'Invoke-MaintenanceThroughputProof.ps1') -OutputDirectory $OutputDirectory
$e=[pscustomobject]@{schemaVersion='1.0';environment='preproduction';scenario='repair-two-implementations-refill';criterionMet=($r.criterionMet-and$r.negativeControls.prCapBlocked-and$r.negativeControls.fileOverlapBlocked-and$r.negativeControls.invalidRecoveryBlocked-and$r.negativeControls.duplicateAssignmentBlocked-and$r.negativeControls.blockedRefillAtWaveLimit);proof='maintenance-throughput-proof.json';eventTrace='maintenance-event-trace.jsonl';prManifest='maintenance-pr-manifest.json'}
$e|ConvertTo-Json -Depth 10|Set-Content (Join-Path $OutputDirectory 'maintenance-preproduction-e2e.json') -Encoding utf8NoBOM;$e

