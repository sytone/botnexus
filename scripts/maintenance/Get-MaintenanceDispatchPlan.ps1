[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$StatePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-PropertyValue {
    param([object]$Object, [string]$Name, [object]$Default)

    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $Default }
    return $property.Value
}

function Get-ActiveWorkers {
    param([object[]]$Workers, [string]$Lane)

    return @($Workers | Where-Object {
        (Get-PropertyValue $_ 'lane' '') -eq $Lane -and
        (Get-PropertyValue $_ 'status' '') -in @('starting', 'running')
    })
}

function Test-FileOverlap {
    param([string[]]$CandidateFiles, [Collections.Generic.HashSet[string]]$ReservedFiles)

    foreach ($file in $CandidateFiles) {
        if ($ReservedFiles.Contains($file)) { return $true }
    }
    return $false
}

if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
    throw "Maintenance state file does not exist: $StatePath"
}

$state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
$budgets = Get-PropertyValue $state 'budgets' $null
$remote = Get-PropertyValue $state 'remoteValidation' $null
if ($null -eq $budgets -or $null -eq $remote) {
    throw 'Maintenance state must define budgets and remoteValidation ceilings.'
}

$workers = @(Get-PropertyValue $state 'workers' @())
$candidates = @(Get-PropertyValue $state 'candidates' @())
$dispatch = [Collections.Generic.List[object]]::new()
$blockers = [Collections.Generic.List[object]]::new()
$reservedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$assignedIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

foreach ($file in @(Get-PropertyValue $state 'reservedFiles' @())) {
    [void]$reservedFiles.Add([string]$file)
}

foreach ($worker in $workers) {
    if ((Get-PropertyValue $worker 'status' '') -notin @('starting', 'running')) { continue }
    [void]$assignedIds.Add([string](Get-PropertyValue $worker 'id' ''))
    foreach ($file in @(Get-PropertyValue $worker 'files' @())) {
        [void]$reservedFiles.Add([string]$file)
    }
}

$laneRemaining = @{
    implementation = [Math]::Max(0, [int](Get-PropertyValue $budgets 'implementation' 0) - @(Get-ActiveWorkers $workers 'implementation').Count)
    repair = [Math]::Max(0, [int](Get-PropertyValue $budgets 'repair' 0) - @(Get-ActiveWorkers $workers 'repair').Count)
    recovery = [Math]::Max(0, [int](Get-PropertyValue $budgets 'recovery' 0) - @(Get-ActiveWorkers $workers 'recovery').Count)
}

$telemetryInput = Get-PropertyValue $state 'telemetry' $null
$implementationStarts = [int](Get-PropertyValue $telemetryInput 'implementationStarts' 0)
$maxStarts = [int](Get-PropertyValue $budgets 'maxImplementationStartsPerCycle' 0)
$projectedOpenPrs = [int](Get-PropertyValue $state 'openPrCount' 0)
$openPrSoftCap = [int](Get-PropertyValue $budgets 'openPrSoftCap' 0)
$remoteActive = [int](Get-PropertyValue $remote 'active' 0)
$remoteMax = [int](Get-PropertyValue $remote 'maxConcurrent' 0)
$committedCost = [double](Get-PropertyValue $remote 'committedCost' 0)
$maxCost = [double](Get-PropertyValue $remote 'maxCost' 0)

foreach ($candidate in $candidates) {
    $id = [string](Get-PropertyValue $candidate 'id' '')
    $lane = [string](Get-PropertyValue $candidate 'lane' '')
    $files = @((Get-PropertyValue $candidate 'files' @()) | ForEach-Object { [string]$_ })
    $cost = [double](Get-PropertyValue $candidate 'estimatedValidationCost' 0)
    $reason = $null

    if ([string]::IsNullOrWhiteSpace($id) -or $lane -notin @('implementation', 'repair', 'recovery')) {
        $reason = 'invalid-candidate'
    }
    elseif ($assignedIds.Contains($id)) {
        $reason = 'already-active'
    }
    elseif (-not [bool](Get-PropertyValue $candidate 'trusted' $false)) {
        $reason = 'trust-gate'
    }
    elseif (-not [bool](Get-PropertyValue $candidate 'decisionFree' $false)) {
        $reason = 'decision-gate'
    }
    elseif ($files.Count -eq 0) {
        $reason = 'missing-file-set'
    }
    elseif (Test-FileOverlap $files $reservedFiles) {
        $reason = 'file-overlap'
    }
    elseif ($lane -eq 'implementation' -and $implementationStarts -ge $maxStarts) {
        $reason = 'implementation-wave-limit'
    }
    elseif ($lane -eq 'implementation' -and $projectedOpenPrs -ge $openPrSoftCap) {
        $reason = 'open-pr-soft-cap'
    }
    elseif ($laneRemaining[$lane] -le 0) {
        $reason = "$lane-capacity"
    }
    elseif ($lane -eq 'recovery' -and
        ((Get-PropertyValue $candidate 'phase' '') -notin @('validation', 'shipping') -or
         [string]::IsNullOrWhiteSpace([string](Get-PropertyValue $candidate 'existingWorktree' '')))) {
        $reason = 'recovery-worktree-gate'
    }
    elseif (([bool](Get-PropertyValue $candidate 'validationRequired' $false) -or ($lane -eq 'recovery' -and (Get-PropertyValue $candidate 'phase' '') -eq 'validation')) -and $remoteActive -ge $remoteMax) {
        $reason = 'remote-validation-concurrency'
    }
    elseif (([bool](Get-PropertyValue $candidate 'validationRequired' $false) -or ($lane -eq 'recovery' -and (Get-PropertyValue $candidate 'phase' '') -eq 'validation')) -and ($committedCost + $cost) -gt $maxCost) {
        $reason = 'remote-validation-cost'
    }

    if ($null -ne $reason) {
        $blockers.Add([pscustomobject]@{ id = $id; lane = $lane; reason = $reason })
        continue
    }

    $reserveValidation = [bool](Get-PropertyValue $candidate 'validationRequired' $false) -or ($lane -eq 'recovery' -and (Get-PropertyValue $candidate 'phase' '') -eq 'validation')
    $worktree = if ($lane -eq 'recovery') { [string](Get-PropertyValue $candidate 'existingWorktree' '') } else { $null }
    $dispatch.Add([pscustomobject]@{
        id = $id
        lane = $lane
        worktree = $worktree
        files = $files
        validation = [pscustomobject]@{ plane = 'remote'; reserved = $reserveValidation; reservedCost = if ($reserveValidation) { $cost } else { 0 } }
    })

    $laneRemaining[$lane]--
    if ($reserveValidation) {
        $remoteActive++
        $committedCost += $cost
    }
    [void]$assignedIds.Add($id)
    foreach ($file in $files) { [void]$reservedFiles.Add($file) }
    if ($lane -eq 'implementation') {
        $implementationStarts++
        $projectedOpenPrs++
    }
}

$idleCapacity = [pscustomobject]@{
    implementation = $laneRemaining.implementation
    repair = $laneRemaining.repair
    recovery = $laneRemaining.recovery
}

[pscustomobject]@{
    cycleId = [string](Get-PropertyValue $state 'cycleId' '')
    trigger = [string](Get-PropertyValue $state 'trigger' 'cycle-started')
    dispatch = @($dispatch)
    blockers = @($blockers)
    idleCapacity = $idleCapacity
    telemetry = [pscustomobject]@{
        candidatesSelected = $candidates.Count
        workersStarted = $dispatch.Count
        implementationStarts = $implementationStarts
        workersCompleted = [int](Get-PropertyValue $telemetryInput 'workersCompleted' 0)
        workerMinutes = [double](Get-PropertyValue $telemetryInput 'workerMinutes' 0)
        validationMinutes = [double](Get-PropertyValue $telemetryInput 'validationMinutes' 0)
        prsOpened = [int](Get-PropertyValue $telemetryInput 'prsOpened' 0)
        prsRepaired = [int](Get-PropertyValue $telemetryInput 'prsRepaired' 0)
        remoteReservations = $remoteActive
        remoteCommittedCost = $committedCost
        projectedOpenPrCount = $projectedOpenPrs
    }
}
