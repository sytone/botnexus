[CmdletBinding()]param()
Set-StrictMode -Version Latest;$ErrorActionPreference='Stop';$fail=[Collections.Generic.List[string]]::new()
function A([bool]$c,[string]$m){if(-not$c){$fail.Add($m)}}
$out=Join-Path ([IO.Path]::GetTempPath()) ('proof-test-'+[guid]::NewGuid().ToString('N'))
try{$r=& (Join-Path $PSScriptRoot 'Invoke-MaintenanceThroughputProof.ps1') -OutputDirectory $out
A ($r.treeReceipts.Count-eq 4) 'four actual worktree receipts';A (@($r.treeReceipts|Where-Object{[string]::IsNullOrWhiteSpace($_.commit)-or[string]::IsNullOrWhiteSpace($_.tree)}).Count-eq 0) 'commits and trees recorded'
A (($r.events.sequence -join ',')-eq '1,2,3,4,5,6,7,8,9,10') 'chronological trace';A ($r.actual.implementationStarts-eq 3) 'repair plus two implementations and refill';A ($r.prManifest.Count-eq 4) 'PR manifest'
foreach($n in 'prCapBlocked','fileOverlapBlocked','invalidRecoveryBlocked','duplicateAssignmentBlocked','blockedRefillAtWaveLimit'){A ([bool]$r.negativeControls.$n) "negative $n"}
A ($r.comparative.throughputMultiplier-eq 6) 'comparative metrics';A (-not $r.productionCriterionMet) 'proof cannot satisfy production criterion'
if($fail.Count){$fail|%{Write-Error $_ -ErrorAction Continue};exit 1};Write-Host 'Maintenance throughput proof tests passed.' -ForegroundColor Green}finally{Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue}


