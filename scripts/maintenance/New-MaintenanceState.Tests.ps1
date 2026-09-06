<#
.SYNOPSIS
    Tests for New-MaintenanceState.ps1 -- schema-correct planner state (#3774).
.DESCRIPTION
    Run: pwsh -NoProfile -File scripts/maintenance/New-MaintenanceState.Tests.ps1

    The defect this producer exists to prevent is a CONFIDENTLY WRONG VERDICT, not a
    crash: before #3874 the planner defaulted absent budget keys to zero, so a misplaced key
    yielded a blocker reason about a constraint nobody expressed. The
    load-bearing cases are therefore:

      (a) the gating-path list is CROSS-CHECKED AGAINST THE PLANNER'S OWN SOURCE, so a
          key renamed or re-nested upstream fails here instead of silently defaulting;
      (b) a state with the historically-wrong top-level nesting is REJECTED;
      (c) the end-to-end verdict VARIES with openPrCount -- an invariant verdict is the
          signature of the cycle-47/48 defect.

    Self-contained (no Pester). Pure functions plus one real planner invocation on a
    temp file; no GitHub I/O.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:pass = 0
$script:fail = 0
function Assert-True { param([string]$Name, [bool]$Cond)
    if ($Cond) { $script:pass++; Write-Host "  PASS $Name" -ForegroundColor DarkGreen }
    else { $script:fail++; Write-Host "  FAIL $Name" -ForegroundColor Red }
}
function Assert-Eq { param([string]$Name, $Expected, $Actual)
    if ("$Expected" -eq "$Actual") { $script:pass++; Write-Host "  PASS $Name" -ForegroundColor DarkGreen }
    else { $script:fail++; Write-Host "  FAIL $Name`n       expected: $Expected`n       actual:   $Actual" -ForegroundColor Red }
}

$scriptPath = Join-Path $PSScriptRoot 'New-MaintenanceState.ps1'
$src = Get-Content -Raw -LiteralPath $scriptPath
$start = $src.IndexOf('# Pure functions')
$cut = $src.IndexOf('# GitHub I/O')
if ($start -lt 0 -or $cut -lt 0) { throw 'could not locate the function-region boundary markers' }
Invoke-Expression $src.Substring($start, $cut - $start)

$plannerPath = Join-Path $PSScriptRoot 'Get-MaintenanceDispatchPlan.ps1'
if (-not (Test-Path -LiteralPath $plannerPath -PathType Leaf)) {
    throw "Required planner source not present at $plannerPath"
}

Write-Host 'Get-MaintenanceStateGatingPath' -ForegroundColor Cyan
$paths = @(Get-MaintenanceStateGatingPath)
Assert-True 'enumerates at least the five budget-scoped gates' ($paths.Count -ge 11)
Assert-True 'openPrSoftCap is budget-scoped, NOT top level' ($paths -contains 'budgets.openPrSoftCap' -and $paths -notcontains 'openPrSoftCap')
Assert-True 'maxImplementationStartsPerCycle is budget-scoped' ($paths -contains 'budgets.maxImplementationStartsPerCycle')
Assert-True 'remote concurrency key is maxConcurrent, not max' ($paths -contains 'remoteValidation.maxConcurrent' -and $paths -notcontains 'remoteValidation.max')

Write-Host 'gating paths cross-checked against the planner source' -ForegroundColor Cyan
if (Test-Path -LiteralPath $plannerPath) {
    $planner = Get-Content -Raw -LiteralPath $plannerPath
    # Each gating path's LEAF must appear as a quoted accessor name in the planner, read
    # off the container its path names. A rename upstream must break this, not default to 0.
    $containerVar = @{
        'budgets'          = '$budgets'
        'remoteValidation' = '$remote'
        ''                 = '$state'
    }
    foreach ($p in $paths) {
        $segments = $p.Split('.')
        $leaf = $segments[-1]
        $container = if ($segments.Count -gt 1) { $segments[0] } else { '' }
        $var = [regex]::Escape($containerVar[$container])
        $pattern = "Get-PropertyValue\s+$var\s+'$([regex]::Escape($leaf))'"
        Assert-True "planner reads $p off $($containerVar[$container])" ([bool]([regex]::IsMatch($planner, $pattern)))
    }
    # Anti-vacuity: the cross-check must be capable of failing.
    Assert-True 'cross-check rejects a fabricated key' (-not [regex]::IsMatch($planner, "Get-PropertyValue\s+\`$budgets\s+'notARealGatingKey'"))
}
else {
    throw "Required planner source not present at $plannerPath"
}

# #3874 producer-only gating regressions BEGIN
# These six paths are optional in the planner, but required by this stricter producer.
# Migrated from the exploratory planner draft without dropping the independent
# missing/null, candidate/no-candidate, or numeric-zero cases.
$producerOnlyPaths = @(
    'openPrCount', 'validationMode', 'remoteValidation.active',
    'remoteValidation.maxConcurrent', 'remoteValidation.committedCost', 'remoteValidation.maxCost'
)
function New-ProducerGatingTestState {
    return New-MaintenanceStateObject -OpenPrCount 1 -OpenPrSoftCap 5 -Candidates @() -Workers @() -ReservedFiles @() -ValidationMode 'local' -ImplementationBudget 2 -RepairBudget 1 -RecoveryBudget 1 -MaxImplementationStartsPerCycle 4 -ImplementationStartsSoFar 0 -RemoteActive 1 -RemoteMaxConcurrent 2 -RemoteCommittedCost 1 -RemoteMaxCost 10
}
foreach ($path in $producerOnlyPaths) {
    foreach ($kind in 'missing', 'null') {
        foreach ($withCandidate in $false, $true) {
            $state = New-ProducerGatingTestState
            if ($withCandidate) {
                $state.candidates = @([pscustomobject]@{ id = 'schema-check'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @('src/schema.cs') })
            }
            $segments = $path.Split('.')
            $container = $state
            if ($segments.Count -gt 1) { $container = $state.($segments[0]) }
            if ($kind -eq 'missing') { $container.PSObject.Properties.Remove($segments[-1]) }
            else { $container.($segments[-1]) = $null }
            $missing = @(Test-MaintenanceState -State $state)
            Assert-True "Schema $kind $path (candidate=$withCandidate) must report the full path at the producer boundary." ($missing.Count -eq 1 -and $missing[0] -ceq $path)
        }
    }
}
foreach ($path in @($producerOnlyPaths | Where-Object { $_ -ne 'validationMode' })) {
    $state = New-ProducerGatingTestState
    $segments = $path.Split('.')
    $container = $state
    if ($segments.Count -gt 1) { $container = $state.($segments[0]) }
    $container.($segments[-1]) = 0
    Assert-Eq "Schema explicit zero $path must remain valid at the producer boundary." 0 (@(Test-MaintenanceState -State $state)).Count
}
# #3874 producer-only gating regressions END

Write-Host 'Test-MaintenanceState fails closed' -ForegroundColor Cyan
$good = New-MaintenanceStateObject -OpenPrCount 31 -OpenPrSoftCap 30 -Candidates @() -Workers @() `
    -ReservedFiles @() -ValidationMode 'remote' -ImplementationBudget 2 -RepairBudget 2 -RecoveryBudget 1 `
    -MaxImplementationStartsPerCycle 2 -ImplementationStartsSoFar 0 -RemoteActive 0 `
    -RemoteMaxConcurrent 2 -RemoteCommittedCost 0 -RemoteMaxCost 100
Assert-Eq 'a complete state reports zero missing keys' 0 (@(Test-MaintenanceState -State $good)).Count

# The historically-wrong shape: gating keys hoisted to top level. This is exactly what
# seven prior cycles authored; the planner used to accept it while defaulting both to 0.
$wrong = [pscustomobject]@{
    validationMode   = 'remote'
    openPrCount      = 31
    openPrSoftCap    = 30
    maxImplementationStartsPerCycle = 2
    budgets          = [pscustomobject]@{ implementation = 2; repair = 2; recovery = 1 }
    remoteValidation = [pscustomobject]@{ active = 0; max = 2; committedCost = 0; maxCost = 100 }
}
$missing = @(Test-MaintenanceState -State $wrong)
Assert-True 'top-level openPrSoftCap is reported missing' ($missing -contains 'budgets.openPrSoftCap')
Assert-True 'top-level maxImplementationStartsPerCycle is reported missing' ($missing -contains 'budgets.maxImplementationStartsPerCycle')
Assert-True 'remoteValidation.max is not accepted for maxConcurrent' ($missing -contains 'remoteValidation.maxConcurrent')

$null = $good.PSObject.Properties.Remove('openPrCount')
Assert-True 'a removed scalar gate is reported missing' ((@(Test-MaintenanceState -State $good)) -contains 'openPrCount')

Write-Host 'New-MaintenanceCandidate' -ForegroundColor Cyan
$cand = New-MaintenanceCandidate -Id '3773' -Lane 'implementation' -Files @('scripts/ci-pr-status.ps1')
Assert-True 'trusted defaults TRUE (planner defaults it false)' ($cand.trusted -eq $true)
Assert-True 'decisionFree defaults TRUE' ($cand.decisionFree -eq $true)
Assert-True 'implementation candidate carries no recovery keys' ($null -eq $cand.PSObject.Properties['phase'])
$rec = New-MaintenanceCandidate -Id '9' -Lane 'recovery' -Files @('a.cs') -Phase 'validation' -ExistingWorktree '../botnexus-wt/example'
Assert-True 'recovery candidate carries phase + existingWorktree' ($rec.phase -eq 'validation' -and $rec.existingWorktree -eq '../botnexus-wt/example')

Write-Host 'Test-MaintenanceCandidate fails closed (#3777)' -ForegroundColor Cyan
# The exact literal that produced `invalid-candidate` while the producer printed 11/11:
# `kind` is the natural word and the planner reads `lane`.
$kindNotLane = [pscustomobject]@{ id = 3773; kind = 'implementation'; trusted = $true; decisionFree = $true; files = @('scripts/ci-pr-status.ps1') }
$p = @(Test-MaintenanceCandidate -Candidates @($kindNotLane))
Assert-True 'the cycle-51 literal (kind, not lane) is REJECTED' ($p.Count -gt 0)
Assert-True 'the diagnostic names the offending id' (($p -join ' ') -match '3773')
Assert-True 'the diagnostic names the wrong key it found' (($p -join ' ') -match "kind")
Assert-Eq 'a well-formed candidate produces no problems' 0 (@(Test-MaintenanceCandidate -Candidates @($cand))).Count
Assert-Eq 'an empty candidate set produces no problems' 0 (@(Test-MaintenanceCandidate -Candidates @())).Count
Assert-True 'a blank id is rejected' (((@(Test-MaintenanceCandidate -Candidates @([pscustomobject]@{ id = ''; lane = 'repair' }))) -join ' ') -match "missing or empty 'id'")
Assert-True 'an unknown lane is rejected' (((@(Test-MaintenanceCandidate -Candidates @([pscustomobject]@{ id = '1'; lane = 'triage' }))) -join ' ') -match 'triage')
foreach ($lane in Get-MaintenanceCandidateLane) {
    # A lane-only literal now legitimately fails the other pre-board gates (#3785), so assert
    # the lane specifically: no problem mentions the lane vocabulary for a valid lane.
    $laneProblems = @(Test-MaintenanceCandidate -Candidates @([pscustomobject]@{ id = '1'; lane = $lane })) -join ' '
    Assert-True "lane '$lane' is accepted" ($laneProblems -notmatch 'is not one of' -and $laneProblems -notmatch "missing 'lane'")
}
# Cross-check the lane vocabulary against the planner's own source, not against my belief
# about it: a lane added or renamed upstream must fail HERE, not silently become invalid.
if (Test-Path -LiteralPath $plannerPath) {
    $plannerSrc = Get-Content -Raw -LiteralPath $plannerPath
    foreach ($lane in Get-MaintenanceCandidateLane) {
        Assert-True "planner source still knows lane '$lane'" ([bool]([regex]::IsMatch($plannerSrc, "'$([regex]::Escape($lane))'")))
    }
    Assert-True 'lane cross-check rejects a fabricated lane' (-not [regex]::IsMatch($plannerSrc, "'notARealLane'"))
}

Write-Host 'pre-board gate coverage derived from planner source (#3785)' -ForegroundColor Cyan
if (Test-Path -LiteralPath $plannerPath) {
    $pl = Get-Content -Raw -LiteralPath $plannerPath
    # Derive the planner's PRE-BOARD reason set from its own source text. The candidate loop
    # assigns $reason in a single if/elseif chain; everything assigned BEFORE the first board
    # constraint ('file-overlap', the first reason that depends on the board rather than on
    # the candidate literal) is a gate the producer must cover. Hand-typing this list is the
    # exact mistake #3777 made -- a sixth gate added upstream would never be noticed.
    $loopIdx = $pl.IndexOf('foreach ($candidate in $candidates)')
    Assert-True 'planner candidate loop located' ($loopIdx -ge 0)
    $boardIdx = $pl.IndexOf("'file-overlap'", [Math]::Max($loopIdx, 0))
    Assert-True 'first board gate (file-overlap) located' ($boardIdx -gt $loopIdx)
    $preBoard = $pl.Substring($loopIdx, $boardIdx - $loopIdx)
    $derived = @([regex]::Matches($preBoard, "\`$reason\s*=\s*'([a-z-]+)'") | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    Assert-True 'derivation found at least four pre-board reasons' ($derived.Count -ge 4)
    # Anti-vacuity: the window must NOT include the board gates it is meant to stop before.
    Assert-True 'derivation excludes board gates' ($derived -notcontains 'file-overlap' -and $derived -notcontains 'open-pr-soft-cap')

    $covered = @((Get-MaintenanceCandidateGate).Keys)
    $boardDependent = @(Get-MaintenanceCandidateBoardDependentGate)
    foreach ($r in $derived) {
        Assert-True "pre-board gate '$r' is covered by Test-MaintenanceCandidate (or declared board-dependent)" (($covered -contains $r) -or ($boardDependent -contains $r))
    }
    # And the reverse: nothing claimed as covered may be absent from the planner, or the
    # producer would be rejecting candidates the planner would have accepted.
    foreach ($k in $covered) {
        Assert-True "claimed gate '$k' really exists in the planner's pre-board chain" ($derived -contains $k)
    }
    Assert-True 'the derivation is capable of failing (no fabricated reason present)' ($derived -notcontains 'notARealGate')
}
else {
    throw "Required planner source not present at $plannerPath"
}

Write-Host 'Test-MaintenanceCandidate covers the full pre-board gate set (#3785)' -ForegroundColor Cyan
$base = @{ id = '3765'; lane = 'repair'; trusted = $true; decisionFree = $true; files = @('a.ps1') }
function New-TestCandidate { param([hashtable]$Override = @{}, [string[]]$Drop = @())
    $h = @{}; foreach ($k in $base.Keys) { $h[$k] = $base[$k] }
    foreach ($k in $Override.Keys) { $h[$k] = $Override[$k] }
    foreach ($k in $Drop) { $h.Remove($k) | Out-Null }
    return [pscustomobject]$h
}
Assert-Eq 'the fully-gated candidate is accepted' 0 (@(Test-MaintenanceCandidate -Candidates @((New-TestCandidate)))).Count
# The exact #3785 literal: valid id and lane, no files -> producer said 1 validated, planner said missing-file-set.
$noFiles = @(Test-MaintenanceCandidate -Candidates @((New-TestCandidate -Drop @('files')))) -join ' '
Assert-True 'an ABSENT files key is rejected' ($noFiles -ne '')
Assert-True 'the files diagnostic names files' ($noFiles -match 'files')
Assert-True 'the files diagnostic names the id' ($noFiles -match '3765')
$emptyFiles = @(Test-MaintenanceCandidate -Candidates @((New-TestCandidate -Override @{ files = @() }))) -join ' '
Assert-True 'an EMPTY files array is rejected' ($emptyFiles -match 'files')
$blankFiles = @(Test-MaintenanceCandidate -Candidates @((New-TestCandidate -Override @{ files = @('  ') }))) -join ' '
Assert-True 'a files array of only blanks is rejected' ($blankFiles -match 'files')
foreach ($flag in 'trusted', 'decisionFree') {
    $false_ = @(Test-MaintenanceCandidate -Candidates @((New-TestCandidate -Override @{ $flag = $false }))) -join ' '
    Assert-True "'$flag' = false is rejected" ($false_ -match [regex]::Escape($flag))
    $absent = @(Test-MaintenanceCandidate -Candidates @((New-TestCandidate -Drop @($flag)))) -join ' '
    Assert-True "an absent '$flag' is rejected" ($absent -match [regex]::Escape($flag))
}
# New-MaintenanceCandidate must satisfy its own validator on every lane.
foreach ($lane in Get-MaintenanceCandidateLane) {
    $c2 = New-MaintenanceCandidate -Id '1' -Lane $lane -Files @('x.cs') -Phase 'validation' -ExistingWorktree '../botnexus-wt/example'
    Assert-Eq "New-MaintenanceCandidate '$lane' passes the full gate set" 0 (@(Test-MaintenanceCandidate -Candidates @($c2))).Count
}

Write-Host 'end-to-end: the verdict must VARY with the board' -ForegroundColor Cyan
if (Test-Path -LiteralPath $plannerPath) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("mstate-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        $reasons = @{}
        foreach ($n in 31, 20) {
            $s = New-MaintenanceStateObject -OpenPrCount $n -OpenPrSoftCap 30 -Candidates @($cand) -Workers @() `
                -ReservedFiles @() -ValidationMode 'remote' -ImplementationBudget 2 -RepairBudget 2 -RecoveryBudget 1 `
                -MaxImplementationStartsPerCycle 2 -ImplementationStartsSoFar 0 -RemoteActive 0 `
                -RemoteMaxConcurrent 2 -RemoteCommittedCost 0 -RemoteMaxCost 100
            $f = Join-Path $tmp "s$n.json"
            $s | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $f -Encoding utf8
            # `pwsh -File` returns FORMATTED TEXT, not an object -- round-trip via JSON.
            $raw = & pwsh -NoProfile -Command "& '$plannerPath' -StatePath '$f' | ConvertTo-Json -Depth 12"
            $plan = ($raw -join "`n") | ConvertFrom-Json
            $reasons[$n] = (@($plan.blockers) | ForEach-Object { $_.reason }) -join ','
            $reasons["d$n"] = @($plan.dispatch).Count
        }
        Assert-Eq 'at 31/30 the blocker is open-pr-soft-cap' 'open-pr-soft-cap' $reasons[31]
        Assert-Eq 'at 31/30 nothing dispatches' 0 $reasons['d31']
        Assert-Eq 'at 20/30 there is no blocker' '' $reasons[20]
        Assert-Eq 'at 20/30 the candidate dispatches' 1 $reasons['d20']
        Assert-True 'the verdict is not invariant under openPrCount' ($reasons[31] -ne $reasons[20])
    }
    finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
}
else {
    throw "Required planner source not present at $plannerPath"
}

Write-Host 'the SCRIPT ITSELF must run as a process, not just its functions (#3775)' -ForegroundColor Cyan
# A suite that only dot-sources the pure functions proves the library, not the tool. The
# assertions above wrap results in @(), which silently supplies the robustness the real
# entry point lacked -- so the crash on the SUCCESS path survived a 30/0 suite.
$scriptPath = Join-Path $PSScriptRoot 'New-MaintenanceState.ps1'
$tmp2 = Join-Path ([IO.Path]::GetTempPath()) ("mstate-e2e-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp2 | Out-Null
try {
    $out = Join-Path $tmp2 'state.json'
    & pwsh -NoProfile -File $scriptPath -OpenPrCount 31 -OpenPrSoftCap 30 -OutPath $out 2>&1 | Out-Null
    Assert-Eq 'complete state: the script exits 0' 0 $LASTEXITCODE
    Assert-True 'complete state: the output file exists' (Test-Path -LiteralPath $out)
    if (Test-Path -LiteralPath $out) {
        $parsed = $null
        try { $parsed = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json } catch { $parsed = $null }
        Assert-Eq 'complete state: openPrSoftCap round-trips inside budgets' 30 $parsed.budgets.openPrSoftCap
    }
    else {
        Assert-True 'complete state: openPrSoftCap round-trips inside budgets' $false
    }

    $stdout = & pwsh -NoProfile -File $scriptPath -OpenPrCount 5 -OpenPrSoftCap 30 2>$null
    Assert-Eq 'no -OutPath: the script still exits 0' 0 $LASTEXITCODE
    # Parse defensively: a mutant must be scored on the TALLY, and an unguarded
    # ConvertFrom-Json on empty output kills the suite, voiding the mutation run.
    $stdoutOk = $false
    try { $stdoutOk = $null -ne ((($stdout -join "`n") | ConvertFrom-Json).budgets) } catch { $stdoutOk = $false }
    Assert-True 'no -OutPath: stdout is parseable JSON' $stdoutOk

    # #3777 end-to-end: the ENTRY POINT must refuse the bad candidate, not just the
    # library. #3775 proved a suite can cover every function and still miss the ten lines
    # below the I/O marker, which are the whole externally-visible contract.
    # NOTE: `pwsh -File` marshals every argument as a STRING, so a [pscustomobject]
    # candidate arrives as "@{id=3773; ...}" and is rejected for the WRONG reason -- it
    # has no properties at all. These cases must use `-Command` to pass real objects.
    # SECOND trap, measured: `pwsh -Command "& script"` COLLAPSES any nonzero exit code
    # to 1 -- the child's `exit 2` is reported as 1 -- so a fail-closed contract that
    # distinguishes 2 from 1 is untestable unless the command explicitly re-exports
    # $LASTEXITCODE. Verified: `& script` -> 1, `& script; exit $LASTEXITCODE` -> 2.
    $badOut = Join-Path $tmp2 'bad.json'
    $badCmd = "`$c = @([pscustomobject]@{ id = 3773; kind = 'implementation'; files = @('x.ps1') }); & '$scriptPath' -OpenPrCount 31 -OpenPrSoftCap 30 -Candidates `$c -OutPath '$badOut'; exit `$LASTEXITCODE"
    & pwsh -NoProfile -Command $badCmd 2>&1 | Out-Null
    Assert-Eq 'bad candidate: the script exits 2' 2 $LASTEXITCODE
    Assert-True 'bad candidate: NO state file is written' (-not (Test-Path -LiteralPath $badOut))

    # And the good path must still write, with candidate coverage in the success line.
    $okOut = Join-Path $tmp2 'ok.json'
    $okCmd = "`$c = @([pscustomobject]@{ id = 3773; lane = 'implementation'; trusted = `$true; decisionFree = `$true; validationRequired = `$true; estimatedValidationCost = 10; files = @('x.ps1') }); & '$scriptPath' -OpenPrCount 31 -OpenPrSoftCap 30 -Candidates `$c -OutPath '$okOut'; exit `$LASTEXITCODE"
    $okLine = & pwsh -NoProfile -Command $okCmd 2>&1
    Assert-Eq 'good candidate: the script exits 0' 0 $LASTEXITCODE
    Assert-True 'good candidate: the state file is written' (Test-Path -LiteralPath $okOut)
    Assert-True 'the success line reports candidate coverage, not just key coverage' ((($okLine -join ' ') -match 'candidates validated: 1'))
    # AC5 (#3785): the success line must state WHICH gates were checked, not a bare count.
    $okText = ($okLine -join ' ')
    foreach ($g in @((Get-MaintenanceCandidateGate).Keys)) {
        Assert-True "the success line names the '$g' gate" ($okText -match [regex]::Escape($g))
    }

    # AC1-3 (#3785) at the ENTRY POINT. `pwsh -File` marshals objects to strings, so these
    # must use -Command with an explicit `exit $LASTEXITCODE` re-export (see the note above).
    $preBoardCases = @(
        @{ Name = 'absent files';       Literal = "@{ id = '3765'; lane = 'repair'; trusted = `$true; decisionFree = `$true }"; Names = 'files' }
        @{ Name = 'empty files';        Literal = "@{ id = '3765'; lane = 'repair'; trusted = `$true; decisionFree = `$true; files = @() }"; Names = 'files' }
        @{ Name = 'trusted false';      Literal = "@{ id = '3765'; lane = 'repair'; trusted = `$false; decisionFree = `$true; files = @('a.ps1') }"; Names = 'trusted' }
        @{ Name = 'decisionFree false'; Literal = "@{ id = '3765'; lane = 'repair'; trusted = `$true; decisionFree = `$false; files = @('a.ps1') }"; Names = 'decisionFree' }
    )
    $ci = 0
    foreach ($case in $preBoardCases) {
        $ci++
        $pOut = Join-Path $tmp2 "pre$ci.json"
        $cmd = "`$c = @([pscustomobject]$($case.Literal)); & '$scriptPath' -OpenPrCount 20 -OpenPrSoftCap 30 -Candidates `$c -OutPath '$pOut'; exit `$LASTEXITCODE"
        $err = & pwsh -NoProfile -Command $cmd 2>&1
        Assert-Eq "$($case.Name): exits 2" 2 $LASTEXITCODE
        Assert-True "$($case.Name): NO state file is written" (-not (Test-Path -LiteralPath $pOut))
        Assert-True "$($case.Name): stderr names '$($case.Names)'" ((($err -join ' ') -match [regex]::Escape($case.Names)))
    }

    # AC3: a candidate satisfying all five pre-board gates reaches a BOARD verdict -- never a
    # pre-board reason. Run the real planner on the producer's own output.
    if (Test-Path -LiteralPath $plannerPath) {
        $boardOut = Join-Path $tmp2 'board.json'
        $boardCmd = "`$c = @([pscustomobject]@{ id = '3765'; lane = 'implementation'; trusted = `$true; decisionFree = `$true; validationRequired = `$true; estimatedValidationCost = 10; files = @('a.ps1') }); & '$scriptPath' -OpenPrCount 31 -OpenPrSoftCap 30 -Candidates `$c -OutPath '$boardOut'; exit `$LASTEXITCODE"
        & pwsh -NoProfile -Command $boardCmd 2>&1 | Out-Null
        Assert-Eq 'fully-gated candidate: producer exits 0' 0 $LASTEXITCODE
        $planReasons = 'PLANNER-DID-NOT-RUN'
        if (Test-Path -LiteralPath $boardOut) {
            try {
                $raw2 = & pwsh -NoProfile -Command "& '$plannerPath' -StatePath '$boardOut' | ConvertTo-Json -Depth 12"
                $plan2 = ($raw2 -join "`n") | ConvertFrom-Json
                $planReasons = (@($plan2.blockers) | ForEach-Object { $_.reason }) -join ','
            }
            catch { $planReasons = 'PLANNER-THREW' }
        }
        $preBoardReasons = @('invalid-candidate', 'trust-gate', 'decision-gate', 'missing-file-set')
        Assert-True 'fully-gated candidate: the planner returns no PRE-BOARD reason' (@($planReasons.Split(',') | Where-Object { $_ -in $preBoardReasons }).Count -eq 0)
        Assert-Eq 'fully-gated candidate: the verdict is the BOARD constraint' 'open-pr-soft-cap' $planReasons
    }

    # The gating-key path must ALSO exit 2, not 1 -- `Write-Error` under StrictMode's Stop
    # preference is TERMINATING and killed the process before its `exit 2` (#3777).
    # Build the mutant IN-PROCESS; nesting a -replace inside a -Command string is a
    # quoting trap that silently produces a no-op mutant (cycle 44).
    $gateOut = Join-Path $tmp2 'gate.json'
    $mutantPath = Join-Path $tmp2 'mutated.ps1'
    $orig = Get-Content -Raw -LiteralPath $scriptPath
    $mutated = $orig.Replace('openPrSoftCap                   = $OpenPrSoftCap', 'ignoredKey                      = 0')
    Assert-True 'gate mutant actually changed the source' ($mutated -ne $orig)
    Set-Content -LiteralPath $mutantPath -Value $mutated -Encoding utf8
    $gateCmd = "& '$($mutantPath -replace '\\','/')' -OpenPrCount 31 -OpenPrSoftCap 30 -OutPath '$($gateOut -replace '\\','/')'; exit `$LASTEXITCODE"
    & pwsh -NoProfile -Command $gateCmd 2>&1 | Out-Null
    Assert-Eq 'a state missing a gating key exits 2, not 1' 2 $LASTEXITCODE
    Assert-True 'a state missing a gating key writes NO file' (-not (Test-Path -LiteralPath $gateOut))
}
finally { Remove-Item -Recurse -Force $tmp2 -ErrorAction SilentlyContinue }

Write-Host ''
# #3960: pass deserialized JSON candidates to the actual producer process.
$flagCases = @(
    @{ Name = 'string-false'; Json = '"false"' }, @{ Name = 'string-true'; Json = '"true"' },
    @{ Name = 'empty-string'; Json = '""' }, @{ Name = 'zero'; Json = '0' },
    @{ Name = 'one'; Json = '1' }, @{ Name = 'empty-array'; Json = '[]' },
    @{ Name = 'true-array'; Json = '[true]' }, @{ Name = 'false-array'; Json = '[false]' },
    @{ Name = 'mixed-array'; Json = '[false,true]' }, @{ Name = 'object'; Json = '{}' },
    @{ Name = 'null'; Json = 'null' }, @{ Name = 'omitted'; Json = 'null' },
    @{ Name = 'boolean-false'; Json = 'false' }, @{ Name = 'boolean-true'; Json = 'true' }
)
$flagTemp = Join-Path ([IO.Path]::GetTempPath()) ('boolean-producer-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $flagTemp | Out-Null
try {
    foreach ($flag in 'trusted', 'decisionFree') {
        foreach ($case in $flagCases) {
            $candidate = @{ id = 'boolean-contract'; lane = 'repair'; trusted = $true; decisionFree = $true; files = @('src/boolean.cs') }
            $typed = ConvertFrom-Json -InputObject ('{"value":' + $case.Json + '}')
            if ($case.Name -eq 'omitted') { $candidate.Remove($flag) }
            else { $candidate[$flag] = $typed.value }
            $inputPath = Join-Path $flagTemp 'candidate.json'
            $outPath = Join-Path $flagTemp "$flag-$($case.Name).json"
            $candidate | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $inputPath -Encoding utf8
            $escapedScript = $scriptPath.Replace("'", "''")
            $escapedInput = $inputPath.Replace("'", "''")
            $escapedOut = $outPath.Replace("'", "''")
            $command = "`$c = Get-Content -LiteralPath '$escapedInput' -Raw | ConvertFrom-Json; & '$escapedScript' -OpenPrCount 1 -OpenPrSoftCap 5 -Candidates @(`$c) -OutPath '$escapedOut'; exit `$LASTEXITCODE"
            $diagnostic = @(& pwsh -NoProfile -Command $command 2>&1) -join ' '
            $exitCode = $LASTEXITCODE
            $accept = $case.Name -eq 'boolean-true'
            Assert-Eq "Boolean $flag $($case.Name): producer exit" $(if ($accept) { 0 } else { 2 }) $exitCode
            Assert-True "Boolean $flag $($case.Name): file exists only for actual true" ((Test-Path -LiteralPath $outPath) -eq $accept)
            if (-not $accept) {
                Assert-True "Boolean $flag $($case.Name): diagnostic names candidate and flag" ($diagnostic.Contains('boolean-contract') -and $diagnostic.Contains($flag))
            }
            else {
                $roundTrip = Get-Content -LiteralPath $outPath -Raw | ConvertFrom-Json
                Assert-True "Boolean $flag actual true retains Boolean type" ($roundTrip.candidates[0].$flag -is [bool] -and $roundTrip.candidates[0].$flag)
            }
        }
    }
}
finally { Remove-Item -LiteralPath $flagTemp -Recurse -Force }

Write-Host "PASS: $script:pass  FAIL: $script:fail" -ForegroundColor $(if ($script:fail) { 'Red' } else { 'Green' })
exit $(if ($script:fail) { 1 } else { 0 })
