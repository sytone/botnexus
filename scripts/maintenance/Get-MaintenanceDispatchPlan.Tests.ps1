[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Get-MaintenanceDispatchPlan.ps1'
$azureScriptPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'repo/Invoke-AzureBuildTest.ps1'
$failures = [Collections.Generic.List[string]]::new()
$script:pass = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $failures.Add($Message) }
    else { $script:pass++ }
}
function Assert-Equal([object]$Expected, [object]$Actual, [string]$Message) {
    if ($Expected -ne $Actual) { $failures.Add("$Message Expected '$Expected', got '$Actual'.") }
    else { $script:pass++ }
}
function Invoke-Plan([hashtable]$State) {
    $path = Join-Path ([IO.Path]::GetTempPath()) "maintenance-state-$([Guid]::NewGuid().ToString('N')).json"
    try {
        $State | ConvertTo-Json -Depth 20 | Set-Content $path -Encoding utf8NoBOM
        return & $scriptPath -StatePath $path
    }
    finally { Remove-Item $path -Force -ErrorAction SilentlyContinue }
}
function New-State {
    return @{
        cycleId = 'cycle-1'; trigger = 'worker-completed'; openPrCount = 1
        budgets = @{ implementation = 2; repair = 1; recovery = 1; maxImplementationStartsPerCycle = 4; openPrSoftCap = 5 }
        validationMode = 'local'
        remoteValidation = @{ active = 0; maxConcurrent = 2; committedCost = 0; maxCost = 10 }
        workers = @(); candidates = @()
    }
}

# Remote archive enumeration must filter empty path records before invoking tar.
$azureScript = Get-Content -LiteralPath $azureScriptPath -Raw
Assert-True ($azureScript.Contains('Where-Object { -not [string]::IsNullOrWhiteSpace($_) }')) 'Azure validation should not pass empty worktree paths to tar.'

# Repair work must not consume implementation capacity.
$state = New-State
$state.candidates = @(
    @{ id = 'pr-20'; lane = 'repair'; trusted = $true; decisionFree = $true; files = @('src/a.cs'); estimatedValidationCost = 2 },
    @{ id = 'issue-21'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/b.cs'); estimatedValidationCost = 2 },
    @{ id = 'issue-22'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/c.cs'); estimatedValidationCost = 2 }
)
$plan = Invoke-Plan $state
Assert-Equal 3 $plan.dispatch.Count 'Independent repair and implementation lanes should dispatch together.'
Assert-Equal 2 @($plan.dispatch | Where-Object lane -eq 'implementation').Count 'Repair must not consume an implementation slot.'

# A completion event immediately refills a slot in a bounded second wave.
$state = New-State
$state.telemetry = @{ implementationStarts = 2; workersCompleted = 1; workerMinutes = 35; validationMinutes = 12; prsOpened = 1; prsRepaired = 0 }
$state.workers = @(@{ id = 'issue-21'; lane = 'implementation'; status = 'running'; files = @('src/b.cs'); validationReserved = $true; estimatedValidationCost = 2 })
$state.remoteValidation.active = 1; $state.remoteValidation.committedCost = 2
$state.candidates = @(@{ id = 'issue-23'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/d.cs'); estimatedValidationCost = 2 })
$plan = Invoke-Plan $state
Assert-Equal 'worker-completed' $plan.trigger 'The completion trigger should be retained as push-based evidence.'
Assert-Equal 1 $plan.dispatch.Count 'A completion event should refill the free implementation slot.'
Assert-Equal 3 $plan.telemetry.implementationStarts 'Second-wave starts should accumulate in cycle telemetry.'

# Every gate remains fail closed and blockers are observable.
$state = New-State; $state.openPrCount = 4
$state.workers = @(@{ id = 'active'; lane = 'implementation'; status = 'running'; files = @('src/shared.cs'); validationReserved = $false; estimatedValidationCost = 0 })
$state.candidates = @(
    @{ id = 'untrusted'; lane = 'implementation'; trusted = $false; decisionFree = $true; files = @('src/u.cs'); estimatedValidationCost = 1 },
    @{ id = 'decision'; lane = 'implementation'; trusted = $true; decisionFree = $false; files = @('src/d.cs'); estimatedValidationCost = 1 },
    @{ id = 'overlap'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/shared.cs'); estimatedValidationCost = 1 },
    @{ id = 'cap-first'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/one.cs'); estimatedValidationCost = 1 },
    @{ id = 'cap-second'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/two.cs'); estimatedValidationCost = 1 }
)
$plan = Invoke-Plan $state
Assert-Equal 1 $plan.dispatch.Count 'The live PR soft cap should be checked before each implementation spawn.'
Assert-True (@($plan.blockers | Where-Object reason -eq 'trust-gate').Count -eq 1) 'Trust rejection should be reported.'
Assert-True (@($plan.blockers | Where-Object reason -eq 'decision-gate').Count -eq 1) 'Decision rejection should be reported.'
Assert-True (@($plan.blockers | Where-Object reason -eq 'file-overlap').Count -eq 1) 'File overlap should be reported.'
Assert-True (@($plan.blockers | Where-Object reason -eq 'open-pr-soft-cap').Count -eq 1) 'PR cap rejection should be reported.'

# Open-PR file ownership and duplicate active assignments remain reserved.
$state = New-State
$state.reservedFiles = @('src/open-pr.cs')
$state.workers = @(@{ id = 'issue-active'; lane = 'implementation'; status = 'running'; files = @('src/active.cs') })
$state.candidates = @(
    @{ id = 'open-pr-overlap'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/open-pr.cs'); estimatedValidationCost = 1 },
    @{ id = 'issue-active'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/new.cs'); estimatedValidationCost = 1 }
)
$plan = Invoke-Plan $state
Assert-Equal 0 $plan.dispatch.Count 'Open PR files and active assignments should block duplicate dispatch.'
Assert-True (@($plan.blockers | Where-Object reason -eq 'file-overlap').Count -eq 1) 'Open PR file overlap should be reported.'
Assert-True (@($plan.blockers | Where-Object reason -eq 'already-active').Count -eq 1) 'Duplicate active issue assignment should be reported.'

# Local validation is the maintenance default and does not reserve the remote plane.
# The throughput proof omits validationMode; keep that optional-field contract.
$state = New-State; $state.Remove('validationMode'); $state.remoteValidation.active = 2; $state.remoteValidation.committedCost = 10
$state.candidates = @(
    @{ id = 'local-a'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/local-a.cs'); validationRequired = $true; estimatedValidationCost = 2 },
    @{ id = 'local-b'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/local-b.cs'); validationRequired = $true; estimatedValidationCost = 2 }
)
$plan = Invoke-Plan $state
Assert-Equal 2 $plan.dispatch.Count 'Remote saturation must not block local-mode candidates.'
Assert-True (@($plan.dispatch | Where-Object { $_.validation.plane -eq 'local' -and -not $_.validation.reserved }).Count -eq 2) 'Local dispatch should not consume remote reservations.'

# Remote mode preserves concurrency and cost reservations when deliberately selected.
$state = New-State; $state.validationMode = 'remote'; $state.remoteValidation.active = 1; $state.remoteValidation.committedCost = 8
$state.candidates = @(
    @{ id = 'fits'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/a.cs'); validationRequired = $true; estimatedValidationCost = 2 },
    @{ id = 'no-remote-slot'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/b.cs'); validationRequired = $true; estimatedValidationCost = 1 }
)
$plan = Invoke-Plan $state
Assert-Equal 1 $plan.dispatch.Count 'Only one remaining remote reservation should be used.'
Assert-True (@($plan.blockers | Where-Object reason -in @('remote-validation-concurrency', 'remote-validation-cost')).Count -eq 1) 'Remote ceiling should explain the blocked candidate.'

# Validation-only recovery must use the existing worktree and its own capacity.
$state = New-State
$state.candidates = @(
    @{ id = 'recover-30'; lane = 'recovery'; phase = 'validation'; trusted = $true; decisionFree = $true; files = @('src/r.cs'); existingWorktree = '../botnexus-wt-30'; estimatedValidationCost = 2 },
    @{ id = 'issue-31'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/i.cs'); estimatedValidationCost = 2 }
)
$plan = Invoke-Plan $state
Assert-Equal 2 $plan.dispatch.Count 'Recovery should not consume new implementation capacity.'
Assert-Equal '../botnexus-wt-30' ($plan.dispatch | Where-Object id -eq 'recover-30').worktree 'Recovery should preserve the existing worktree.'

# #3874: incomplete budgets are an authoring error, never a board verdict.
# Each path is independently absent/null, with and without a candidate, so validation
# must happen before any dispatch decisions (including an otherwise idle cycle).
# The six other producer gating paths remain optional here. Their missing/null and
# numeric-zero assertions live in New-MaintenanceState.Tests.ps1 at that stricter boundary.
$gatingPaths = @(
    'budgets.implementation', 'budgets.repair', 'budgets.recovery',
    'budgets.maxImplementationStartsPerCycle', 'budgets.openPrSoftCap'
)
foreach ($path in $gatingPaths) {
    foreach ($kind in 'missing', 'null') {
        foreach ($withCandidate in $false, $true) {
            $state = New-State
            if ($withCandidate) {
                $state.candidates = @(@{ id = 'schema-check'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/schema.cs') })
            }
            $segments = $path.Split('.')
            $container = $state
            if ($segments.Count -gt 1) { $container = $state[$segments[0]] }
            if ($kind -eq 'missing') { $container.Remove($segments[-1]) }
            else { $container[$segments[-1]] = $null }
            $diagnostic = ''
            $output = @()
            try { $output = @(Invoke-Plan $state) }
            catch { $diagnostic = $_.Exception.Message }
            Assert-True ($diagnostic.Contains($path) -and $output.Count -eq 0) "Schema $kind $path (candidate=$withCandidate) must throw naming the path, not return a verdict."
        }
    }
}

# Explicit zero is not an omission. Test every required budget independently.
foreach ($path in $gatingPaths) {
    $state = New-State
    $segments = $path.Split('.')
    $container = $state
    if ($segments.Count -gt 1) { $container = $state[$segments[0]] }
    $container[$segments[-1]] = 0
    $accepted = $false
    try { $zeroPlan = Invoke-Plan $state; $accepted = $null -ne $zeroPlan }
    catch { $accepted = $false }
    Assert-True $accepted "Schema explicit zero $path must remain valid."
}

# Complete cycle-92 input must expose the real board constraint, not a fabricated wave limit.
$state = New-State
$state.cycleId = 'cycle-92'
$state.openPrCount = 31
$state.budgets.openPrSoftCap = 30
$state.budgets.maxImplementationStartsPerCycle = 2
$state.telemetry = @{ implementationStarts = 0 }
$state.candidates = @(@{ id = 'soft-cap'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/soft-cap.cs') })
$plan = Invoke-Plan $state
Assert-Equal 'open-pr-soft-cap' (($plan.blockers | ForEach-Object reason) -join ',') 'Complete 31/30 state must return exactly open-pr-soft-cap.'
Assert-Equal 0 $plan.dispatch.Count 'Complete 31/30 state must not dispatch.'
Assert-Equal 0 $plan.telemetry.implementationStarts 'Complete 31/30 state must preserve zero implementation starts.'

# #3960: exercise JSON types through the real file entry point, not a bool cast.
$flagCases = @(
    @{ Name = 'string-false'; Json = '"false"' }, @{ Name = 'string-true'; Json = '"true"' },
    @{ Name = 'empty-string'; Json = '""' }, @{ Name = 'zero'; Json = '0' },
    @{ Name = 'one'; Json = '1' }, @{ Name = 'empty-array'; Json = '[]' },
    @{ Name = 'true-array'; Json = '[true]' }, @{ Name = 'false-array'; Json = '[false]' },
    @{ Name = 'mixed-array'; Json = '[false,true]' }, @{ Name = 'object'; Json = '{}' },
    @{ Name = 'null'; Json = 'null' }, @{ Name = 'omitted'; Json = 'null' },
    @{ Name = 'boolean-false'; Json = 'false' }, @{ Name = 'boolean-true'; Json = 'true' }
)
foreach ($flag in 'trusted', 'decisionFree') {
    foreach ($case in $flagCases) {
        $state = New-State
        $candidate = @{ id = 'boolean-contract'; lane = 'repair'; trusted = $true; decisionFree = $true; files = @('src/boolean.cs') }
        $typed = ConvertFrom-Json -InputObject ('{"value":' + $case.Json + '}')
        if ($case.Name -eq 'omitted') { $candidate.Remove($flag) }
        else { $candidate[$flag] = $typed.value }
        $state.candidates = @($candidate)
        $plan = Invoke-Plan $state
        $accept = $case.Name -eq 'boolean-true'
        $expectedReason = if ($flag -eq 'trusted') { 'trust-gate' } else { 'decision-gate' }
        Assert-Equal ([int]$accept) $plan.dispatch.Count "Boolean $flag $($case.Name): dispatch only actual true."
        $reasons = (@($plan.blockers) | ForEach-Object reason) -join ','
        Assert-Equal $(if ($accept) { '' } else { $expectedReason }) $reasons "Boolean $flag $($case.Name): precise gate reason."
    }
}

Write-Host "PASS: $script:pass  FAIL: $($failures.Count)"
if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Host "  FAIL $_" -ForegroundColor Red }; exit 1 }
Write-Host 'Maintenance dispatch tests passed.' -ForegroundColor Green
