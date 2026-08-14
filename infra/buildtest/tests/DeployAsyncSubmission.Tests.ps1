# Tests for asynchronous deployment submission and bounded polling (#3118).
#
# WHY THIS EXISTS: a synchronous `az deployment group create` against the buildtest subscription
# hung indefinitely having submitted NOTHING - no deployment record, no resource. That made an
# unattended BCDR rebuild impossible. `--no-wait` plus explicit polling is the only submission
# path observed to work, so these tests pin it: if `--no-wait` is ever dropped from the deploy
# script, test 1 fails.
#
# No Azure access required. The invocation checks are AST analysis of the real script file, and
# the polling behaviour is exercised against a stubbed `az`.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:failures = @()
function Assert-True {
    param([bool]$Condition, [string]$Because)
    if (-not $Condition) { $script:failures += $Because }
}

$scriptPath = (Resolve-Path (Join-Path $PSScriptRoot '../Deploy-BuildTestInfrastructure.ps1')).Path
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)

# 0. The script must parse. Everything below is vacuous otherwise.
Assert-True (-not $parseErrors -or $parseErrors.Count -eq 0) "0: deploy script has parse errors: $($parseErrors -join '; ')"

function Get-AzCommandAsts {
    param([Parameter(Mandatory)]$Ast, [Parameter(Mandatory)][string[]]$Words)

    $Ast.FindAll({
            param($n)
            if ($n -isnot [System.Management.Automation.Language.CommandAst]) { return $false }
            $e = $n.CommandElements
            if ($e.Count -lt $Words.Count) { return $false }
            for ($i = 0; $i -lt $Words.Count; $i++) {
                if ("$($e[$i].Extent.Text)" -ne $Words[$i]) { return $false }
            }
            return $true
        }, $true)
}

$creates = @(Get-AzCommandAsts -Ast $ast -Words @('az', 'deployment', 'group', 'create'))

# --- clause 5 non-vacuity: there must actually BE a deployment submission to inspect -----------
Assert-True ($creates.Count -ge 1) '5: no az deployment group create invocation found -- the assertion below would pass vacuously'

# --- clause 1: EVERY deployment submission passes --no-wait ------------------------------------
foreach ($c in $creates) {
    $argText = @($c.CommandElements | ForEach-Object { "$($_.Extent.Text)" })
    Assert-True ($argText -contains '--no-wait') `
        "1: an 'az deployment group create' invocation at line $($c.Extent.StartLineNumber) is missing --no-wait -- this is the hang from #3118"
}

# --- clause 2/4: the script polls provisioningState and bounds the wait -------------------------
$shows = @(Get-AzCommandAsts -Ast $ast -Words @('az', 'deployment', 'group', 'show'))
Assert-True ($shows.Count -ge 1) '2: no az deployment group show invocation -- nothing polls for terminal state'
Assert-True (@($shows | Where-Object { $_.CommandElements.Extent.Text -contains 'properties.provisioningState' }).Count -ge 1) `
    '2: nothing queries properties.provisioningState'
Assert-True (@($shows | Where-Object { $_.CommandElements.Extent.Text -contains 'properties.error' }).Count -ge 1) `
    '3: nothing queries properties.error -- ARM failure detail would not be surfaced'

# --- behavioural: exercise the real polling function against a stubbed az ----------------------
#
# The function is extracted from the script's own AST rather than copy-pasted, so the behaviour
# tested is the behaviour that ships. A rename or deletion fails test 6 rather than silently
# testing a stale duplicate.
$fnAst = $ast.Find({
        param($n)
        $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-BuildTestDeployment'
    }, $true)
Assert-True ($null -ne $fnAst) '6: Invoke-BuildTestDeployment not found in the deploy script'

if ($fnAst) {
    $harness = {
        param($FunctionText, $Mode)

        Set-StrictMode -Version Latest
        $SubscriptionId = 'sub-test'
        $ResourceGroup = 'rg-test'
        $script:seenArgs = @()
        $script:showCalls = 0

        function az {
            $all = @($args | ForEach-Object { "$_" })
            $script:seenArgs += , $all
            $global:LASTEXITCODE = 0
            if ($all -contains 'create') { return }
            $script:showCalls++
            if ($all -contains 'properties.error') { return '{"code":"BadRequest","message":"boom"}' }
            switch ($Mode) {
                'succeed' { return 'Succeeded' }
                'fail' { return 'Failed' }
                'running' { return 'Running' }
            }
        }

        . ([scriptblock]::Create($FunctionText))

        $timeout = if ($Mode -eq 'running') { 0 } else { 5 }
        $result = [pscustomobject]@{ Threw = $null; Args = $null }
        try {
            Invoke-BuildTestDeployment -DeploymentName 'd1' -TemplateFile 't.bicep' -Parameters @('a=1') -TimeoutMinutes $timeout -PollSeconds 1 | Out-Null
        }
        catch { $result.Threw = "$_" }
        $result.Args = $script:seenArgs
        return $result
    }

    $fnText = $fnAst.Extent.Text

    # 6. Happy path: succeeds, and the submission carried --no-wait at RUNTIME, not just in source.
    $ok = & $harness $fnText 'succeed'
    Assert-True ($null -eq $ok.Threw) "6: a Succeeded deployment threw: $($ok.Threw)"
    $createArgs = @($ok.Args | Where-Object { $_ -contains 'create' })
    Assert-True ($createArgs.Count -eq 1) '6: expected exactly one create invocation'
    Assert-True ($createArgs[0] -contains '--no-wait') '6: the runtime create invocation did not include --no-wait'

    # 7. A Failed deployment throws, and the message carries ARM's own error payload (clause 3).
    $bad = & $harness $fnText 'fail'
    Assert-True ($null -ne $bad.Threw) '7: a Failed deployment did not throw'
    Assert-True ($bad.Threw -match "Failed") '7: the failure message does not name the terminal state'
    Assert-True ($bad.Threw -match 'BadRequest' -and $bad.Threw -match 'boom') `
        "7: the failure message does not surface properties.error detail: $($bad.Threw)"

    # 8. A non-terminal state does not loop forever: the bounded timeout throws (clause 4).
    $stuck = & $harness $fnText 'running'
    Assert-True ($null -ne $stuck.Threw) '8: a never-terminal deployment did not throw -- polling is unbounded'
    Assert-True ($stuck.Threw -match 'did not reach a terminal state') "8: timeout message is not the bounded-wait message: $($stuck.Threw)"
    Assert-True ($stuck.Threw -match "Running") '8: the timeout message does not report the last observed state'
}

if ($script:failures.Count) {
    Write-Host "FAILED ($($script:failures.Count)):" -ForegroundColor Red
    $script:failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host 'Deploy async submission + polling: all checks passed.' -ForegroundColor Green
exit 0
