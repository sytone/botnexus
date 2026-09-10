<#
.SYNOPSIS
    Produce a schema-correct state document for Get-MaintenanceDispatchPlan.ps1.
.DESCRIPTION
    Originally filed as #3774: hand-authored state omitted gating keys, and the
    planner's defaulting accessor could not distinguish an explicit zero from an
    absent key. Incomplete literals therefore produced misleading board verdicts.
    The planner now independently rejects missing budget keys (#3874); this producer
    emits and validates the complete eleven-key schema, including non-budget fields.

    Measured 2026-09-02 08:0xZ (cycle 48): the natural top-level placement of
    `maxImplementationStartsPerCycle` and `openPrSoftCap` -- which is how the cron
    prompt and seven prior playbook entries phrase them -- is WRONG. The planner reads
    both from inside `budgets`, and reads remote concurrency from `maxConcurrent`, not
    `max`. Before the planner guard, all three misplaced returned `implementation-wave-limit` for a
    candidate whose real blocker is `open-pr-soft-cap`, and the reason does not change
    when `openPrCount` is varied. That invariance is the only tell.

    This producer emits the nesting the planner actually reads, and FAILS CLOSED on a
    missing gating value rather than defaulting it to 0.

    Pipeline:
      & ./scripts/maintenance/New-MaintenanceState.ps1 -OpenPrCount 31 -OpenPrSoftCap 30 -Candidates $c -OutPath tmp/state.json
      & ./scripts/maintenance/Get-MaintenanceDispatchPlan.ps1 -StatePath tmp/state.json
.NOTES
    Suite: New-MaintenanceState.Tests.ps1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$OpenPrCount,
    [Parameter(Mandatory)][int]$OpenPrSoftCap,
    [object[]]$Candidates = @(),
    [object[]]$Workers = @(),
    [string[]]$ReservedFiles = @(),
    [ValidateSet('local', 'remote')][string]$ValidationMode = 'remote',
    [int]$ImplementationBudget = 2,
    [int]$RepairBudget = 2,
    [int]$RecoveryBudget = 1,
    [int]$MaxImplementationStartsPerCycle = 2,
    [int]$ImplementationStartsSoFar = 0,
    [int]$RemoteActive = 0,
    [int]$RemoteMaxConcurrent = 2,
    [double]$RemoteCommittedCost = 0,
    [double]$RemoteMaxCost = 100,
    [string]$OutPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Pure functions -- exercised directly by the suite, no filesystem or GitHub I/O.

# The exact gating paths Get-MaintenanceDispatchPlan.ps1 reads. The producer requires
# all eleven, even though the planner only requires the five budget keys (#3874) and
# preserves defaults for its other callers. The suite cross-checks every path against
# the planner's own source text.
function Get-MaintenanceStateGatingPath {
    return @(
        'budgets.implementation'
        'budgets.repair'
        'budgets.recovery'
        'budgets.maxImplementationStartsPerCycle'
        'budgets.openPrSoftCap'
        'openPrCount'
        'validationMode'
        'remoteValidation.active'
        'remoteValidation.maxConcurrent'
        'remoteValidation.committedCost'
        'remoteValidation.maxCost'
    )
}

function Resolve-StatePath {
    param([object]$State, [string]$Path)

    $node = $State
    foreach ($segment in $Path.Split('.')) {
        if ($null -eq $node) { return $null }
        $property = $node.PSObject.Properties[$segment]
        if ($null -eq $property) { return $null }
        $node = $property.Value
    }
    return $node
}

# Fail closed: a gating key that is absent or null is an authoring defect, not a zero.
function Test-MaintenanceState {
    param([Parameter(Mandatory)][object]$State)

    $missing = @()
    foreach ($path in Get-MaintenanceStateGatingPath) {
        if ($null -eq (Resolve-StatePath -State $State -Path $path)) { $missing += $path }
    }
    return $missing
}

# The candidate keys Get-MaintenanceDispatchPlan.ps1 gates on at line ~92 BEFORE any
# board constraint is evaluated. Filed as #3777: validating only the state keys let a
# candidate authored with `kind` instead of `lane` through, and the planner answered
# `invalid-candidate` -- a verdict about the literal, not the board -- while this script
# printed `11/11 present`. The lane vocabulary is cross-checked against planner source.
function Get-MaintenanceCandidateLane {
    return @('implementation', 'repair', 'recovery')
}

# The planner's pre-board reason vocabulary, mapped to the candidate key whose absence or
# falsity produces it. #3785: #3777 covered only `invalid-candidate` (id/lane) because that
# was the instance being debugged, so a candidate with a valid id and lane but no `files`
# passed the producer and came back `missing-file-set` -- again a verdict about the literal.
# The suite DERIVES the planner's pre-board reason set from its SOURCE TEXT and asserts every
# one is present here, so a sixth gate added upstream fails the suite instead of silently
# re-opening the hole.
function Get-MaintenanceCandidateGate {
    return [ordered]@{
        'invalid-candidate' = 'id+lane'
        'trust-gate'        = 'trusted'
        'decision-gate'     = 'decisionFree'
        'missing-file-set'  = 'files'
    }
}

# Pre-board reasons that are NOT properties of the candidate literal and therefore cannot be
# checked here: `already-active` is a function of the worker board. Declared explicitly so the
# suite's derivation can tell "deliberately out of scope" from "missed".
function Get-MaintenanceCandidateBoardDependentGate {
    return @('already-active')
}

function Test-MaintenanceCandidate {
    param([object[]]$Candidates = @())
    $problems = @()
    $lanes = Get-MaintenanceCandidateLane
    $all = @($Candidates)
    for ($i = 0; $i -lt $all.Count; $i++) {
        $c = $all[$i]
        if ($null -eq $c) { $problems += "candidate[$i]: null"; continue }
        $idProp = $c.PSObject.Properties['id']
        $id = if ($idProp) { [string]$idProp.Value } else { '' }
        $label = if ([string]::IsNullOrWhiteSpace($id)) { "candidate[$i]" } else { "candidate[$i] (id=$id)" }
        if ([string]::IsNullOrWhiteSpace($id)) { $problems += ($label + ": missing or empty 'id'") }
        $laneProp = $c.PSObject.Properties['lane']
        if (-not $laneProp) {
            $other = @($c.PSObject.Properties.Name | Where-Object { $_ -in @('kind', 'type', 'bucket') })
            $hint = if ($other.Count) { " (found '" + ($other -join "', '") + "' -- the planner reads 'lane')" } else { '' }
            $problems += ($label + ": missing 'lane'" + $hint)
        }
        elseif (([string]$laneProp.Value) -notin $lanes) {
            $problems += ($label + ": lane '" + [string]$laneProp.Value + "' is not one of " + ($lanes -join '|'))
        }

        # trusted / decisionFree: the planner defaults BOTH to $false, so an absent key is
        # indistinguishable from an explicit refusal and yields trust-gate/decision-gate.
        foreach ($flag in @(@{ Key = 'trusted'; Reason = 'trust-gate' }, @{ Key = 'decisionFree'; Reason = 'decision-gate' })) {
            $prop = $c.PSObject.Properties[$flag.Key]
            if (-not $prop) {
                $problems += ($label + ": missing '" + $flag.Key + "' (the planner defaults it false -> " + $flag.Reason + ")")
            }
            elseif ($prop.Value -isnot [bool] -or -not $prop.Value) {
                # Nonempty strings and arrays are truthy in PowerShell, but are not
                # JSON Boolean evidence. Preserve type until this admission check.
                $problems += ($label + ": '" + $flag.Key + "' must be Boolean true -> the planner will answer " + $flag.Reason)
            }
        }

        # files: `$files.Count -eq 0` -> missing-file-set. An absent key defaults to @().
        $filesProp = $c.PSObject.Properties['files']
        $files = if ($filesProp) { @($filesProp.Value) } else { @() }
        $files = @($files | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
        if ($files.Count -eq 0) {
            $problems += ($label + ": empty or missing 'files' -> the planner will answer missing-file-set")
        }
    }
    return $problems
}
function New-MaintenanceStateObject {
    param(
        [int]$OpenPrCount,
        [int]$OpenPrSoftCap,
        [object[]]$Candidates,
        [object[]]$Workers,
        [string[]]$ReservedFiles,
        [string]$ValidationMode,
        [int]$ImplementationBudget,
        [int]$RepairBudget,
        [int]$RecoveryBudget,
        [int]$MaxImplementationStartsPerCycle,
        [int]$ImplementationStartsSoFar,
        [int]$RemoteActive,
        [int]$RemoteMaxConcurrent,
        [double]$RemoteCommittedCost,
        [double]$RemoteMaxCost
    )

    return [pscustomobject]@{
        validationMode  = $ValidationMode
        openPrCount     = $OpenPrCount
        budgets         = [pscustomobject]@{
            implementation                  = $ImplementationBudget
            repair                          = $RepairBudget
            recovery                        = $RecoveryBudget
            maxImplementationStartsPerCycle = $MaxImplementationStartsPerCycle
            openPrSoftCap                   = $OpenPrSoftCap
        }
        remoteValidation = [pscustomobject]@{
            active        = $RemoteActive
            maxConcurrent = $RemoteMaxConcurrent
            committedCost = $RemoteCommittedCost
            maxCost       = $RemoteMaxCost
        }
        telemetry       = [pscustomobject]@{ implementationStarts = $ImplementationStartsSoFar }
        workers         = @($Workers)
        reservedFiles   = @($ReservedFiles)
        candidates      = @($Candidates)
    }
}

# A candidate missing trusted/decisionFree defaults to $false in the planner and comes
# back as trust-gate/decision-gate -- a verdict about the literal, not the board.
function New-MaintenanceCandidate {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][ValidateSet('implementation', 'repair', 'recovery')][string]$Lane,
        [Parameter(Mandatory)][string[]]$Files,
        [bool]$Trusted = $true,
        [bool]$DecisionFree = $true,
        [bool]$ValidationRequired = $true,
        [double]$EstimatedValidationCost = 10,
        [string]$Phase,
        [string]$ExistingWorktree
    )

    $c = [pscustomobject]@{
        id                      = $Id
        lane                    = $Lane
        trusted                 = $Trusted
        decisionFree            = $DecisionFree
        validationRequired      = $ValidationRequired
        estimatedValidationCost = $EstimatedValidationCost
        files                   = @($Files)
    }
    if ($Lane -eq 'recovery') {
        $c | Add-Member -NotePropertyName phase -NotePropertyValue $Phase
        $c | Add-Member -NotePropertyName existingWorktree -NotePropertyValue $ExistingWorktree
    }
    return $c
}

# GitHub I/O and entry point -- below this marker the suite does not load.

$state = New-MaintenanceStateObject -OpenPrCount $OpenPrCount -OpenPrSoftCap $OpenPrSoftCap `
    -Candidates $Candidates -Workers $Workers -ReservedFiles $ReservedFiles `
    -ValidationMode $ValidationMode -ImplementationBudget $ImplementationBudget `
    -RepairBudget $RepairBudget -RecoveryBudget $RecoveryBudget `
    -MaxImplementationStartsPerCycle $MaxImplementationStartsPerCycle `
    -ImplementationStartsSoFar $ImplementationStartsSoFar -RemoteActive $RemoteActive `
    -RemoteMaxConcurrent $RemoteMaxConcurrent -RemoteCommittedCost $RemoteCommittedCost `
    -RemoteMaxCost $RemoteMaxCost

# @() is MANDATORY: PowerShell unrolls a returned empty array to $null, so a COMPLETE
# state -- the normal case -- would throw on .Count under StrictMode. Filed as #3775.
# `Write-Error` under `$ErrorActionPreference='Stop'` is a TERMINATING error: the process
# dies with exit code 1 and the `exit 2` below is never reached. That silently downgraded
# the fail-closed contract to an indistinguishable generic failure (#3777). Write the
# diagnostic to stderr by hand so the intended exit code survives.
$missing = @(Test-MaintenanceState -State $state)
if ($missing.Count -gt 0) {
    [Console]::Error.WriteLine("Maintenance state is missing gating keys: " + ($missing -join ', '))
    exit 2
}
# #3777: the planner rejects a malformed candidate BEFORE any board gate, so an
# unvalidated candidate yields `invalid-candidate` and the real constraint is never
# evaluated. @() for the same unrolling reason as above (#3775).
$badCandidates = @(Test-MaintenanceCandidate -Candidates @($state.candidates))
if ($badCandidates.Count -gt 0) {
    [Console]::Error.WriteLine("Maintenance state has candidates the planner will reject: " + ($badCandidates -join '; '))
    exit 2
}

$json = $state | ConvertTo-Json -Depth 12
if ($OutPath) {
    $dir = Split-Path -Parent $OutPath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Set-Content -LiteralPath $OutPath -Value $json -Encoding utf8
    # #3785: report WHICH gates were checked. `candidates validated: N` alone is coverage of
    # the subset the validator chose to examine, phrased as coverage of the whole.
    $gateKeys = @((Get-MaintenanceCandidateGate).Keys)
    $keyCount = @(Get-MaintenanceStateGatingPath).Count
    Write-Host ("state written: $OutPath (gating keys: $keyCount/$keyCount present; candidates validated: " + @($state.candidates).Count + " against pre-board gates " + ($gateKeys -join ', ') + ")")
}
else {
    $json
}
exit 0
