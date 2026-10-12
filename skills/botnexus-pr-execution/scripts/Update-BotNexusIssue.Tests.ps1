[CmdletBinding()]
param([string]$ScriptPath = (Join-Path $PSScriptRoot 'Update-BotNexusIssue.ps1'))

# Isolated CLI fixtures only: no GitHub, credentials, gateway or test host.
# Match the helper's ordinary invocation scope (it does not enable StrictMode).
$ErrorActionPreference = 'Stop'
$failures = [Collections.Generic.List[string]]::new()
$global:IssueLabelFixture_passed = 0
$originalTemp = $env:TEMP
$root = Join-Path ([IO.Path]::GetTempPath()) ('issue-label-fixture-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$env:TEMP = $root

function Test-Path {
    param([string]$LiteralPath)
    if ($LiteralPath -eq 'C:/Users/jobullen/.botnexus/scripts/get-farnsworth-token.ps1') { return $false }
    Microsoft.PowerShell.Management\Test-Path -LiteralPath $LiteralPath
}

function gh {
    $global:LASTEXITCODE = 0
    $global:IssueLabelFixture_calls.Add(($args -join ' '))
    if ($args[0] -eq 'issue') {
        $global:IssueLabelFixture_reads++
        if ($global:IssueLabelFixture_reads -gt 1 -and $global:IssueLabelFixture_failureMode -eq 'read-error') {
            $global:LASTEXITCODE = 1
            return
        }
        $names = if ($global:IssueLabelFixture_reads -eq 1) { $global:IssueLabelFixture_initial } else { $global:IssueLabelFixture_observed }
        $state = if ($global:IssueLabelFixture_reads -gt 1 -and $global:IssueLabelFixture_failureMode -eq 'closed') { 'CLOSED' } else { 'OPEN' }
        @{number=123; title='fixture'; url='https://example.invalid/123'; state=$state; labels=@($names | ForEach-Object { @{name=$_} })} | ConvertTo-Json -Depth 5
        return
    }
    if ($args[0] -ne 'api' -or $args[2] -ne 'PATCH') { throw 'Unexpected CLI operation.' }
    $inputIndex = [Array]::IndexOf($args, '--input')
    $global:IssueLabelFixture_payload = Get-Content -LiteralPath $args[$inputIndex + 1] -Raw | ConvertFrom-Json
    if ($global:IssueLabelFixture_failureMode -eq 'write-error') { $global:LASTEXITCODE = 1; return }
    if ($global:IssueLabelFixture_payload.labels -isnot [array]) { throw 'GitHub rejects labels that are not an array.' }
    $global:IssueLabelFixture_observed = if ($global:IssueLabelFixture_failureMode -eq 'drift') { @('unexpected') } else { @($global:IssueLabelFixture_payload.labels) }
    '{}'
}

function Invoke-Case([string]$Name, [string[]]$Labels, [string[]]$Expected, [string]$FailureMode = '', [string]$Action = 'Release') {
    $global:IssueLabelFixture_initial=$Labels; $global:IssueLabelFixture_observed=@(); $global:IssueLabelFixture_failureMode=$FailureMode
    $global:IssueLabelFixture_reads=0; $global:IssueLabelFixture_payload=$null; $global:IssueLabelFixture_calls=[Collections.Generic.List[string]]::new()
    try {
        $errorText = ''
        $result = $null
        try { $result = & $ScriptPath -Issue 123 -Action $Action | ConvertFrom-Json }
        catch { $errorText = $_.Exception.Message }
        if ($FailureMode) {
            $expectedError = switch ($FailureMode) {
                'write-error' { 'Failed to release' }
                'read-error' { 'readback failed' }
                default { 'readback drift' }
            }
            if ($errorText -notmatch $expectedError) { throw "Expected '$expectedError', got '$errorText'." }
        }
        else {
            if ($errorText) { throw $errorText }
            if (-not $result.updated -or $global:IssueLabelFixture_reads -ne 2) { throw 'Authoritative successful readback required.' }
            if ($global:IssueLabelFixture_payload.labels -isnot [array] -or $result.labels -isnot [array]) { throw 'Payload and result labels must be arrays.' }
            if (($global:IssueLabelFixture_payload.labels -join '|') -cne ($Expected -join '|')) { throw 'Incorrect target labels.' }
            if (($result.labels -join '|') -cne ($Expected -join '|')) { throw 'Incorrect result labels.' }
            if ($Action -eq 'Release' -and 'status:ready-for-agent' -notin $Labels -and 'status:ready-for-agent' -in $global:IssueLabelFixture_payload.labels) { throw 'Release must not create admission.' }
            if (@($global:IssueLabelFixture_calls | Where-Object { $_ -like 'api *' }).Count -ne 1) { throw 'Expected one authoritative PATCH.' }
        }
        if (@(Get-ChildItem -LiteralPath $root -File).Count) { throw 'Payload was not cleaned up.' }
        $global:IssueLabelFixture_passed++
    }
    catch { $failures.Add("${Name}: $($_.Exception.Message)") }
}

try {
    Invoke-Case 'unlabeled no-op release' @() @()
    Invoke-Case 'only claim release' @('status:in-progress') @()
    Invoke-Case 'single unrelated label' @('type:bug','status:in-progress') @('type:bug')
    Invoke-Case 'multiple unrelated labels' @('type:bug','priority:medium','status:in-progress') @('type:bug','priority:medium')
    Invoke-Case 'already released' @('type:bug') @('type:bug')
    Invoke-Case 'repeated empty release' @() @()
    Invoke-Case 'existing admission is preserved, not added' @('status:ready-for-agent','status:in-progress') @('status:ready-for-agent')
    Invoke-Case 'write failure remains fatal' @('status:in-progress') @() 'write-error'
    Invoke-Case 'readback failure remains fatal' @('status:in-progress') @() 'read-error'
    Invoke-Case 'label drift remains fatal' @('status:in-progress') @() 'drift'
    Invoke-Case 'closed readback remains fatal' @('status:in-progress') @() 'closed'
    Invoke-Case 'unadmit empty array' @('status:ready-for-agent') @() '' 'Unadmit'
    [pscustomobject]@{total=12; passed=$global:IssueLabelFixture_passed; failed=$failures.Count; failures=@($failures)} | ConvertTo-Json -Depth 3
    if ($failures.Count) { exit 1 }
}
finally {
    $env:TEMP = $originalTemp
    Get-Variable -Scope Global -Name 'IssueLabelFixture_*' | Remove-Variable -Scope Global
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
