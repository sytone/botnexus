[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$watchdogPath = Join-Path $PSScriptRoot 'botnexus-watchdog.ps1'
$failures = [Collections.Generic.List[string]]::new()

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $failures.Add($Message) }
}

function Assert-Equal([object]$Expected, [object]$Actual, [string]$Message) {
    if ($Expected -ne $Actual) { $failures.Add("$Message Expected '$Expected', got '$Actual'.") }
}

function Invoke-WatchdogCase {
    param(
        [ValidateSet('Missing', 'Corrupt', 'Valid', 'Partial')]
        [string]$StateKind
    )

    $caseRoot = Join-Path ([IO.Path]::GetTempPath()) "botnexus-watchdog-test-$([Guid]::NewGuid().ToString('N'))"
    $configDir = Join-Path $caseRoot 'config'
    $stateFile = Join-Path $caseRoot 'watchdog-state.json'
    $commandLog = Join-Path $caseRoot 'commands.log'
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null

    switch ($StateKind) {
        'Corrupt' { Set-Content -LiteralPath $stateFile -Value '{not-json' -Encoding utf8NoBOM }
        'Valid' {
            @{
                FailureCount = 2
                LastGitCheck = '2025-01-02T03:04:05+00:00'
                LastCliCheck = [DateTimeOffset]::Now.ToString('o')
                CustomValue = 'preserved'
            } | ConvertTo-Json | Set-Content -LiteralPath $stateFile -Encoding utf8NoBOM
        }
        'Partial' {
            @{ CustomValue = 'preserved' } | ConvertTo-Json | Set-Content -LiteralPath $stateFile -Encoding utf8NoBOM
        }
    }

    function global:Invoke-WebRequest { return [pscustomobject]@{ StatusCode = 200 } }
    $binDir = Join-Path $caseRoot 'bin'
    New-Item -ItemType Directory -Path $binDir -Force | Out-Null
    if ($IsWindows) {
        Set-Content -LiteralPath (Join-Path $binDir 'botnexus.cmd') -Value "@echo off`r`necho botnexus %* >> `"$commandLog`"" -Encoding ascii
        Set-Content -LiteralPath (Join-Path $binDir 'dotnet.cmd') -Value "@echo off`r`necho dotnet %* >> `"$commandLog`"" -Encoding ascii
    }
    else {
        $botnexusShim = Join-Path $binDir 'botnexus'
        $dotnetShim = Join-Path $binDir 'dotnet'
        Set-Content -LiteralPath $botnexusShim -Value "#!/bin/sh`necho botnexus `$* >> `"$commandLog`"" -Encoding utf8NoBOM
        Set-Content -LiteralPath $dotnetShim -Value "#!/bin/sh`necho dotnet `$* >> `"$commandLog`"" -Encoding utf8NoBOM
        & chmod +x $botnexusShim $dotnetShim
    }
    $previousPath = $env:PATH
    $env:PATH = "$binDir$([IO.Path]::PathSeparator)$env:PATH"

    try {
        & $watchdogPath -ConfigDir $configDir -StateFile $stateFile -RepoPath ''
        $exitCode = $LASTEXITCODE
        $state = if (Test-Path -LiteralPath $stateFile) {
            Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json -AsHashtable
        } else {
            @{}
        }
        $commands = if (Test-Path -LiteralPath $commandLog) { Get-Content -LiteralPath $commandLog -Raw } else { '' }
        return @{ ExitCode = $exitCode; State = $state; Commands = $commands }
    }
    finally {
        Remove-Item Function:\global:Invoke-WebRequest -ErrorAction SilentlyContinue
        $env:PATH = $previousPath
        Remove-Item -LiteralPath $caseRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$missing = Invoke-WatchdogCase -StateKind Missing
Assert-Equal 0 $missing.ExitCode 'A first run without a state file should succeed.'
Assert-Equal 0 $missing.State.FailureCount 'A first run should persist the default failure count.'
Assert-True $missing.State.ContainsKey('LastGitCheck') 'A first run should persist LastGitCheck.'
Assert-True $missing.State.ContainsKey('LastCliCheck') 'A first run should persist LastCliCheck.'
Assert-Equal $null $missing.State.LastGitCheck 'An explicitly empty RepoPath should leave LastGitCheck unset.'

$corrupt = Invoke-WatchdogCase -StateKind Corrupt
Assert-Equal 0 $corrupt.ExitCode 'A corrupt state file should be reinitialized successfully.'
Assert-Equal 0 $corrupt.State.FailureCount 'A corrupt state file should receive the default failure count.'
Assert-True $corrupt.State.ContainsKey('LastGitCheck') 'A corrupt state file should receive LastGitCheck.'
Assert-True $corrupt.State.ContainsKey('LastCliCheck') 'A corrupt state file should receive LastCliCheck.'

$valid = Invoke-WatchdogCase -StateKind Valid
Assert-Equal 0 $valid.ExitCode 'A valid existing state file should load successfully.'
Assert-Equal 'preserved' $valid.State.CustomValue 'Loading valid state should preserve additional values.'
Assert-Equal ([DateTimeOffset]::Parse('2025-01-02T03:04:05+00:00')) ([DateTimeOffset]::Parse($valid.State.LastGitCheck.ToString())) 'Loading valid state should preserve an existing git timestamp in health-only mode.'

$partial = Invoke-WatchdogCase -StateKind Partial
Assert-Equal 0 $partial.ExitCode 'A valid state file with missing keys should load successfully.'
Assert-Equal 0 $partial.State.FailureCount 'Missing FailureCount should be backfilled.'
Assert-True $partial.State.ContainsKey('LastGitCheck') 'Missing LastGitCheck should be backfilled.'
Assert-True $partial.State.ContainsKey('LastCliCheck') 'Missing LastCliCheck should be backfilled.'
Assert-True $partial.State.ContainsKey('LastKnownGoodConfig') 'Missing LastKnownGoodConfig should be backfilled when state is persisted.'
Assert-Equal $null $partial.State.LastGitCheck 'Health-only mode should not stamp a repository check timestamp.'
Assert-Equal 'preserved' $partial.State.CustomValue 'Backfilling keys should preserve existing values.'

Assert-True (-not $missing.Commands.Contains('botnexus update')) 'An explicitly empty RepoPath should skip repository checks.'
Assert-True (-not $valid.Commands.Contains('botnexus update')) 'Health-only mode should not run repository checks for valid state.'

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}

Write-Host 'Watchdog state tests passed.' -ForegroundColor Green
