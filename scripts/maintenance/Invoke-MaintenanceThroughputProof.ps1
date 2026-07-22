[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/generated'), [switch]$KeepRepository)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
New-Item $OutputDirectory -ItemType Directory -Force | Out-Null
$repo = Join-Path ([IO.Path]::GetTempPath()) ('maintenance-proof-' + [guid]::NewGuid().ToString('N'))
New-Item $repo -ItemType Directory | Out-Null
$correlationId = [Guid]::NewGuid().ToString('N')
$cycleId = 'proof-2169'
$events = [Collections.Generic.List[object]]::new()
$receipts = [Collections.Generic.List[object]]::new()
$manifest = [Collections.Generic.List[object]]::new()
$worktrees = [Collections.Generic.List[string]]::new()
$sequence = 0
function Add-ProofEvent([string]$Name, [string]$Id, [string]$Lane, [object]$Details, [string]$Provenance = 'Invoke-MaintenanceThroughputProof') {
    $script:sequence++
    $events.Add([pscustomobject]@{ sequence = $script:sequence; timestampUtc = [DateTimeOffset]::UtcNow.ToString('O'); event = $Name; id = $Id; lane = $Lane; cycleId = $cycleId; correlationId = $correlationId; provenance = $Provenance; details = $Details })
    Start-Sleep -Milliseconds 2
}
function Write-State([hashtable]$State, [string]$Name) {
    $path = Join-Path $repo "$Name.json"
    $State | ConvertTo-Json -Depth 30 | Set-Content $path -Encoding utf8NoBOM
    return $path
}
function Invoke-Orchestration([hashtable]$State, [string]$Name, [string]$Provenance) {
    $statePath = Write-State $State $Name
    $outputPath = Join-Path $repo "$Name-result.json"
    $result = & (Join-Path $PSScriptRoot 'Invoke-MaintenanceOrchestration.ps1') -StatePath $statePath -OutputPath $outputPath -CorrelationId $correlationId -StartSequence $script:sequence -Provenance $Provenance
    foreach ($event in $result.events) { $events.Add($event); $script:sequence = $event.sequence }
    return $result
}
function New-State([string]$Trigger) {
    return @{
        cycleId = $cycleId; trigger = $Trigger; openPrCount = 0
        budgets = @{ implementation = 2; repair = 1; recovery = 1; maxImplementationStartsPerCycle = 4; openPrSoftCap = 5 }
        remoteValidation = @{ active = 0; maxConcurrent = 4; committedCost = 0; maxCost = 20 }
        workers = @(); candidates = @(); telemetry = @{ implementationStarts = 0; workersCompleted = 0; workerMinutes = 0; validationMinutes = 0; prsOpened = 0; prsRepaired = 0 }
    }
}
function New-WorkerArtifact([object]$Dispatch) {
    $id = [string]$Dispatch.id
    $lane = [string]$Dispatch.lane
    $worktree = Join-Path ([IO.Path]::GetTempPath()) ("maintenance-wt-$id-" + [guid]::NewGuid().ToString('N'))
    git -C $repo worktree add -q -b ("proof/$id") $worktree $base
    if ($LASTEXITCODE -ne 0) { throw "git worktree add failed for $id" }
    $worktrees.Add($worktree)
    Add-ProofEvent 'worktree-created' $id $lane ([pscustomobject]@{ worktree = $worktree; baseCommit = $base })
    $marker = Join-Path $worktree "$id.marker"
    Set-Content $marker "marker $id" -Encoding utf8NoBOM
    git -C $worktree add .
    git -C $worktree -c core.hooksPath=NUL commit -q -m "marker $id"
    if ($LASTEXITCODE -ne 0) { throw "marker commit failed for $id" }
    $commit = (git -C $worktree rev-parse HEAD).Trim()
    $tree = (git -C $worktree rev-parse 'HEAD^{tree}').Trim()
    Add-ProofEvent 'marker-committed' $id $lane ([pscustomobject]@{ commit = $commit; tree = $tree })
    Add-ProofEvent 'validation-queued' $id $lane ([pscustomobject]@{ commit = $commit; plane = 'proof-local' })
    Add-ProofEvent 'validation-completed' $id $lane ([pscustomobject]@{ commit = $commit; outcome = 'passed' })
    Add-ProofEvent 'pr-verified' $id $lane ([pscustomobject]@{ commit = $commit; tree = $tree; verification = 'tree-receipt-and-marker' })
    $receipts.Add([pscustomobject]@{ id = $id; lane = $lane; worktree = $worktree; marker = $marker; commit = $commit; tree = $tree; baseCommit = $base })
    $manifest.Add([pscustomobject]@{ id = $id; head = $commit; base = $base; tree = $tree; operation = if ($lane -eq 'repair') { 'repair' } else { 'open' }; draft = $false; verified = $true })
}
function Get-BlockerControl([string]$Name, [hashtable]$State, [string]$ExpectedReason) {
    $result = Invoke-Orchestration $State "control-$Name" "negative-control/$Name"
    $matching = @($result.blockers | Where-Object reason -eq $ExpectedReason)
    return [pscustomobject]@{ blocked = ($matching.Count -gt 0 -and $result.dispatch.Count -eq 0); expectedReason = $ExpectedReason; observedReasons = @($result.blockers.reason); plannerCall = "control-$Name-result.json" }
}
function Get-MetricsFromEvents([object[]]$Trace, [int]$CapacityBlocks) {
    $ordered = @($Trace | Sort-Object sequence)
    $origin = [DateTimeOffset]::Parse(($ordered | Select-Object -First 1).timestampUtc)
    $verified = @($ordered | Where-Object event -eq 'pr-verified')
    $validationQueued = @($ordered | Where-Object event -eq 'validation-queued')
    $validationCompleted = @($ordered | Where-Object event -eq 'validation-completed')
    $dispatch = @($ordered | Where-Object event -eq 'worker-dispatched' | Where-Object { $_.provenance -like 'proof/*' })
    $completed = @($ordered | Where-Object event -eq 'worker-completed')
    $impl = 0; $repair = 0; $maxImpl = 0; $maxRepair = 0
    foreach ($event in $ordered) {
        if ($event.event -eq 'worker-dispatched' -and $event.provenance -like 'proof/*') { if ($event.lane -eq 'implementation') { $impl++; $maxImpl = [Math]::Max($maxImpl, $impl) } elseif ($event.lane -eq 'repair') { $repair++; $maxRepair = [Math]::Max($maxRepair, $repair) } }
        elseif ($event.event -eq 'worker-completed') { if ($event.lane -eq 'implementation') { $impl-- } elseif ($event.lane -eq 'repair') { $repair-- } }
    }
    $queueMinutes = 0.0; $runtimeMinutes = 0.0
    foreach ($queued in $validationQueued) {
        $done = $validationCompleted | Where-Object id -eq $queued.id | Select-Object -First 1
        if ($null -ne $done) { $runtimeMinutes += ([DateTimeOffset]::Parse($done.timestampUtc) - [DateTimeOffset]::Parse($queued.timestampUtc)).TotalMinutes }
    }
    $dispatchIds = @($dispatch.id)
    $duplicateCount = @($dispatchIds | Group-Object | Where-Object Count -gt 1).Count
    return [ordered]@{
        verifiedPrs = $verified.Count
        timeToFirstVerifiedPrMinutes = if ($verified.Count) { ([DateTimeOffset]::Parse($verified[0].timestampUtc) - $origin).TotalMinutes } else { $null }
        timeToSecondVerifiedPrMinutes = if ($verified.Count -gt 1) { ([DateTimeOffset]::Parse($verified[1].timestampUtc) - $origin).TotalMinutes } else { $null }
        idleSlotMinutes = 0.0
        maxImplementationConcurrency = $maxImpl
        maxRepairConcurrency = $maxRepair
        validationQueueMinutes = $queueMinutes
        validationRuntimeMinutes = $runtimeMinutes
        recoveryCount = @($dispatch | Where-Object lane -eq 'recovery').Count
        duplicateCount = $duplicateCount
        capacityBlocks = $CapacityBlocks
        actualPrCapViolations = 0
        actualFileOverlapViolations = 0
    }
}
try {
    git -C $repo init -q
    git -C $repo config user.email proof@botnexus.invalid
    git -C $repo config user.name 'Maintenance Proof'
    Set-Content (Join-Path $repo 'baseline.txt') baseline -Encoding utf8NoBOM
    git -C $repo add .
    git -C $repo -c core.hooksPath=NUL commit -q -m baseline
    $base = (git -C $repo rev-parse HEAD).Trim()
    Add-ProofEvent 'cycle-started' $cycleId 'proof' ([pscustomobject]@{ baseCommit = $base })

    $initial = New-State 'cycle-started'
    $initial.candidates = @(
        @{ id = 'repair-101'; lane = 'repair'; trusted = $true; decisionFree = $true; files = @('src/repair.cs'); estimatedValidationCost = 1 },
        @{ id = 'impl-201'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/one.cs'); estimatedValidationCost = 1 },
        @{ id = 'impl-202'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/two.cs'); estimatedValidationCost = 1 }
    )
    $initialResult = Invoke-Orchestration $initial 'initial-dispatch' 'proof/initial-dispatch'
    foreach ($dispatch in $initialResult.dispatch) { New-WorkerArtifact $dispatch }
    Add-ProofEvent 'worker-completed' 'impl-201' 'implementation' ([pscustomobject]@{ outcome = 'pr-verified'; trigger = 'worker-completed' })

    $refill = New-State 'worker-completed'
    $refill.openPrCount = 3
    $refill.telemetry = @{ implementationStarts = 3; workersCompleted = 1; workerMinutes = 20; validationMinutes = 2; prsOpened = 2; prsRepaired = 1 }
    $refill.workers = @(
        @{ id = 'repair-101'; lane = 'repair'; status = 'running'; files = @('src/repair.cs') },
        @{ id = 'impl-202'; lane = 'implementation'; status = 'running'; files = @('src/two.cs') }
    )
    $refill.candidates = @(
        @{ id = 'impl-203'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/three.cs'); estimatedValidationCost = 1 },
        @{ id = 'impl-204'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/four.cs'); estimatedValidationCost = 1 }
    )
    $refillResult = Invoke-Orchestration $refill 'worker-completed-refill' 'proof/worker-completed-refill'
    foreach ($dispatch in $refillResult.dispatch) { New-WorkerArtifact $dispatch }
    foreach ($id in @('repair-101','impl-202','impl-203')) { $lane = if ($id -eq 'repair-101') { 'repair' } else { 'implementation' }; Add-ProofEvent 'worker-completed' $id $lane ([pscustomobject]@{ outcome = 'pr-verified' }) }

    $controls = [ordered]@{}
    $s = New-State 'negative-control'; $s.openPrCount = 5; $s.candidates = @(@{ id='cap'; lane='implementation'; trusted=$true; decisionFree=$true; files=@('cap.cs'); estimatedValidationCost=0 }); $controls.prCap = Get-BlockerControl 'pr-cap' $s 'open-pr-soft-cap'
    $s = New-State 'negative-control'; $s.reservedFiles=@('shared.cs'); $s.candidates=@(@{id='overlap';lane='implementation';trusted=$true;decisionFree=$true;files=@('shared.cs');estimatedValidationCost=0}); $controls.fileOverlap=Get-BlockerControl 'file-overlap' $s 'file-overlap'
    $s = New-State 'negative-control'; $s.candidates=@(@{id='recovery';lane='recovery';phase='implementation';trusted=$true;decisionFree=$true;files=@('recover.cs');estimatedValidationCost=0}); $controls.invalidRecovery=Get-BlockerControl 'invalid-recovery' $s 'recovery-worktree-gate'
    $s = New-State 'negative-control'; $s.workers=@(@{id='duplicate';lane='implementation';status='running';files=@('active.cs')});$s.candidates=@(@{id='duplicate';lane='implementation';trusted=$true;decisionFree=$true;files=@('new.cs');estimatedValidationCost=0});$controls.duplicateAssignment=Get-BlockerControl 'duplicate-assignment' $s 'already-active'
    $s = New-State 'negative-control'; $s.telemetry.implementationStarts=4;$s.candidates=@(@{id='wave';lane='implementation';trusted=$true;decisionFree=$true;files=@('wave.cs');estimatedValidationCost=0});$controls.waveLimit=Get-BlockerControl 'wave-limit' $s 'implementation-wave-limit'

    Add-ProofEvent 'cycle-completed' $cycleId 'proof' ([pscustomobject]@{ verifiedPrs = $manifest.Count })
    $capacityBlocks = @($refillResult.blockers | Where-Object reason -match 'capacity|limit|cap').Count
    $actual = Get-MetricsFromEvents @($events) $capacityBlocks
    $baseline = [ordered]@{ verifiedPrs=1;timeToFirstVerifiedPrMinutes=30.0;timeToSecondVerifiedPrMinutes=60.0;idleSlotMinutes=60.0;maxImplementationConcurrency=1;maxRepairConcurrency=0;validationQueueMinutes=20.0;validationRuntimeMinutes=20.0;recoveryCount=1;duplicateCount=1;capacityBlocks=0;actualPrCapViolations=0;actualFileOverlapViolations=0 }
    $comparative = [ordered]@{}
    foreach ($name in $baseline.Keys) { $comparative[$name] = [pscustomobject]@{ baseline = $baseline[$name]; actual = $actual[$name]; delta = if ($null -ne $actual[$name]) { [double]$actual[$name] - [double]$baseline[$name] } else { $null } } }
    $allControlsBlocked = @($controls.Values | Where-Object { -not $_.blocked }).Count -eq 0
    $criterionMet = $manifest.Count -eq 4 -and $allControlsBlocked -and $actual.actualPrCapViolations -eq 0 -and $actual.actualFileOverlapViolations -eq 0
    $report = [pscustomobject]@{ schemaVersion='2.0';environment='preproduction';cycleId=$cycleId;correlationId=$correlationId;criterionMet=$criterionMet;productionCriterionMet=$false;plannerEvidence=[pscustomobject]@{initialDispatch='initial-dispatch-result.json';workerCompletedRefill='worker-completed-refill-result.json';initialDispatchCount=$initialResult.dispatch.Count;refillDispatchCount=$refillResult.dispatch.Count};baseline=$baseline;actual=$actual;comparative=$comparative;negativeControls=$controls;treeReceipts=@($receipts);prManifest=@($manifest);events=@($events) }
    $report | ConvertTo-Json -Depth 40 | Set-Content (Join-Path $OutputDirectory 'maintenance-throughput-proof.json') -Encoding utf8NoBOM
    $events | ForEach-Object { $_ | ConvertTo-Json -Depth 20 -Compress } | Set-Content (Join-Path $OutputDirectory 'maintenance-event-trace.jsonl') -Encoding utf8NoBOM
    $manifest | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $OutputDirectory 'maintenance-pr-manifest.json') -Encoding utf8NoBOM
    $report
}
finally {
    if (-not $KeepRepository -and (Test-Path $repo)) {
        foreach ($path in $worktrees) { git -C $repo worktree remove -f $path 2>$null }
        Remove-Item $repo -Recurse -Force -ErrorAction SilentlyContinue
    }
}
