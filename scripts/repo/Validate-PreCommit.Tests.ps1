[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot | Split-Path -Parent | Split-Path -Parent
$validationScript = Join-Path $repoRoot 'scripts/repo/Validate-PreCommit.ps1'
$fingerprintScript = Join-Path $repoRoot 'scripts/repo/Get-WorktreeValidationFingerprint.ps1'
$failures = [Collections.Generic.List[string]]::new()
$gitEnvironment = @{}
$gitLocalEnvironmentNames = @(& git -C $repoRoot rev-parse --local-env-vars)
foreach ($name in $gitLocalEnvironmentNames) {
    if ([string]::IsNullOrWhiteSpace($name)) { continue }
    $gitEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Assert-Equal([object]$Expected, [object]$Actual, [string]$Message) {
    if ($Expected -ne $Actual) { $failures.Add("$Message Expected '$Expected', got '$Actual'.") }
}

function Invoke-IsolatedGit {
    param([string[]]$Arguments)

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'git'
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($name in $gitLocalEnvironmentNames) {
        if (-not [string]::IsNullOrWhiteSpace($name)) { [void]$startInfo.Environment.Remove($name) }
    }
    foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($startInfo)
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $global:LASTEXITCODE = $process.ExitCode
    if ($stdout.Length -gt 0) { Write-Output $stdout.TrimEnd() }
    if ($stderr.Length -gt 0) { Write-Error $stderr.TrimEnd() -ErrorAction Continue }
}

function New-TestRepository {
    $path = Join-Path ([IO.Path]::GetTempPath()) "botnexus-validation-test-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $path | Out-Null
    Invoke-IsolatedGit -Arguments @('-c', 'core.bare=false', '-C', $path, 'init', '--initial-branch', 'main') *> $null
    if (-not (Test-Path (Join-Path $path '.git') -PathType Container)) { throw "Unable to initialize test repository: $path" }
    Invoke-IsolatedGit -Arguments @('-C', $path, 'config', '--local', 'user.name', 'test') *> $null
    Invoke-IsolatedGit -Arguments @('-C', $path, 'config', '--local', 'user.email', 'test@example.invalid') *> $null
    Set-Content (Join-Path $path 'candidate.txt') 'candidate' -Encoding utf8NoBOM
    Invoke-IsolatedGit -Arguments @('-C', $path, 'add', '--all') *> $null
    # Isolate fixture commits from the caller's configured hooks. Otherwise a global
    # core.hooksPath can recursively invoke BotNexus validation from inside the fixture.
    Invoke-IsolatedGit -Arguments @('-c', 'core.hooksPath=', '-C', $path, 'commit', '-m', 'initial') *> $null
    Invoke-IsolatedGit -Arguments @('-C', $path, 'branch', 'origin/main') *> $null
    return $path
}

function Write-Receipt([string]$Repository, [string]$Mode = 'strict') {
    $fingerprint = & $fingerprintScript -WorktreePath $Repository
    $gitDirectory = (Invoke-IsolatedGit -Arguments @('-C', $Repository, 'rev-parse', '--absolute-git-dir')).Trim()
    $receiptDirectory = Join-Path $gitDirectory 'botnexus-validation'
    New-Item -ItemType Directory -Path $receiptDirectory -Force | Out-Null
    @{
        version = 1
        fingerprint = $fingerprint.fingerprint
        head = $fingerprint.head
        baseRef = $fingerprint.baseRef
        baseCommit = $fingerprint.baseCommit
        tree = $fingerprint.tree
        mode = $Mode
        runId = 'test-run'
    } | ConvertTo-Json | Set-Content (Join-Path $receiptDirectory 'azure-buildtest.json') -Encoding utf8NoBOM
}

function New-CommandScript([string]$Directory, [string]$Name, [string]$Marker, [int]$ExitCode = 0) {
    $path = Join-Path $Directory $Name
    @(
        "param([string]`$WorktreePath, [string]`$BaseRef, [string]`$Mode)"
        "Add-Content -Path '$($Marker.Replace("'", "''"))' -Value '$Name'"
        "exit $ExitCode"
    ) | Set-Content $path -Encoding utf8NoBOM
    return $path
}

$localRunnerSource = Get-Content (Join-Path $repoRoot 'scripts/repo/Invoke-LocalValidation.ps1') -Raw
if ($localRunnerSource -notmatch "botnexus-local-validation-global" -or
    $localRunnerSource -match 'botnexus-local-validation-\$') {
    $failures.Add('Local fallback must use a global host lock across all BotNexus worktrees.')
}

$azureRunnerSource = Get-Content (Join-Path $repoRoot 'scripts/repo/Invoke-AzureBuildTest.ps1') -Raw
if ($azureRunnerSource -notmatch "(?s)Mode -ne 'strict'.+playwrightArtifact" -or
    $azureRunnerSource -notmatch 'result.exitCode -eq 0 -and\s+\$requiredArtifactsPresent') {
    $failures.Add('Strict Azure receipt creation must require a Playwright artifact.')
}
if ($azureRunnerSource -match 'ls-files.+-z.+tar --null' -or
    $azureRunnerSource -notmatch 'workspace-files\.txt' -or
    $azureRunnerSource -notmatch 'System32/tar\.exe') {
    $failures.Add('Azure snapshot creation must use an LF file list and Windows tar.exe rather than a native pipeline.')
}
$entrypointSource = Get-Content (Join-Path $repoRoot 'infra/buildtest/runner/entrypoint.ps1') -Raw
if ($entrypointSource -notmatch "playwright\.log" -or
    $entrypointSource -notmatch "'strict' \{") {
    $failures.Add('The remote runner must implement strict mode and fail when Playwright did not run.')
}

$repositories = [Collections.Generic.List[string]]::new()
$originalFallbackEnvironment = $env:BOTNEXUS_VALIDATION_LOCAL_FALLBACK
$originalModeEnvironment = $env:BOTNEXUS_VALIDATION_MODE
Remove-Item Env:BOTNEXUS_VALIDATION_LOCAL_FALLBACK -ErrorAction SilentlyContinue
Remove-Item Env:BOTNEXUS_VALIDATION_MODE -ErrorAction SilentlyContinue
$noValidationModeEnvironment = [ordered]@{ Process = $null; User = $null; Machine = $null }
try {
    # Exact-content receipts are authoritative only for selected remote validation.
    $repo = New-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = New-CommandScript $repo 'remote.ps1' $marker
    $local = New-CommandScript $repo 'local.ps1' $marker
    Write-Receipt $repo
    & $validationScript -WorktreePath $repo -AzureValidationScript $remote -LocalValidationScript $local -ValidationMode remote -ValidationModeEnvironment $noValidationModeEnvironment
    Assert-Equal 0 $LASTEXITCODE 'Matching remote receipt should pass.'
    Assert-Equal $false (Test-Path $marker) 'Matching remote receipt should bypass redundant validation.'

    # Local is the operational default and runs the globally serialized strict gate.
    $repo = New-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = New-CommandScript $repo 'remote.ps1' $marker 9
    $local = New-CommandScript $repo 'local.ps1' $marker
    & $validationScript -WorktreePath $repo -AzureValidationScript $remote -LocalValidationScript $local -ValidationModeEnvironment $noValidationModeEnvironment
    Assert-Equal 0 $LASTEXITCODE 'Default local validation should pass.'
    Assert-Equal 'local.ps1' ((Get-Content $marker) -join ',') 'Default validation should select local only.'

    # Exact-content receipts are authoritative and bypass remote work.
    $repo = New-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = New-CommandScript $repo 'remote.ps1' $marker
    $local = New-CommandScript $repo 'local.ps1' $marker
    Write-Receipt $repo
    & $validationScript -WorktreePath $repo -AzureValidationScript $remote -LocalValidationScript $local -ValidationMode remote -ValidationModeEnvironment $noValidationModeEnvironment
    Assert-Equal 0 $LASTEXITCODE 'Matching receipt should pass.'
    Assert-Equal $false (Test-Path $marker) 'Matching receipt should bypass redundant validation.'

    # Any content change invalidates the receipt when remote mode is selected.
    $repo = New-TestRepository; $repositories.Add($repo); Write-Receipt $repo
    Add-Content (Join-Path $repo 'candidate.txt') 'changed'
    $marker = Join-Path $repo 'commands.log'
    $remote = New-CommandScript $repo 'remote.ps1' $marker
    $local = New-CommandScript $repo 'local.ps1' $marker
    & $validationScript -WorktreePath $repo -AzureValidationScript $remote -LocalValidationScript $local -ValidationMode remote -ValidationModeEnvironment $noValidationModeEnvironment
    Assert-Equal 0 $LASTEXITCODE 'Selected remote validation should pass.'
    Assert-Equal 'remote.ps1' ((Get-Content $marker) -join ',') 'Stale receipt should select Azure only.'

    # Local fallback is opt-in and uses a cross-process serialization lock.
    $repo = New-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = New-CommandScript $repo 'remote.ps1' $marker 9
    $local = New-CommandScript $repo 'local.ps1' $marker
    & $validationScript -WorktreePath $repo -AzureValidationScript $remote -LocalValidationScript $local -LocalFallback -ValidationModeEnvironment $noValidationModeEnvironment
    Assert-Equal 0 $LASTEXITCODE 'Explicit local fallback should pass.'
    Assert-Equal 'local.ps1' ((Get-Content $marker) -join ',') 'Explicit fallback should not attempt Azure first.'

    # The durable selector can choose remote validation without removing local support.
    $repo = New-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = New-CommandScript $repo 'remote.ps1' $marker 9
    $local = New-CommandScript $repo 'local.ps1' $marker
    $env:BOTNEXUS_VALIDATION_MODE = 'remote'
    try {
        & $validationScript -WorktreePath $repo -AzureValidationScript $remote -LocalValidationScript $local
    }
    finally {
        Remove-Item Env:BOTNEXUS_VALIDATION_MODE -ErrorAction SilentlyContinue
    }
    Assert-Equal 9 $LASTEXITCODE 'Environment-selected remote validation should preserve failure.'
    Assert-Equal 'remote.ps1' ((Get-Content $marker) -join ',') 'Environment selector should choose remote only.'

    # A failed authoritative remote run must not silently fall back locally.
    $repo = New-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = New-CommandScript $repo 'remote.ps1' $marker 9
    $local = New-CommandScript $repo 'local.ps1' $marker
    & $validationScript -WorktreePath $repo -AzureValidationScript $remote -LocalValidationScript $local -ValidationMode remote -ValidationModeEnvironment $noValidationModeEnvironment
    Assert-Equal 9 $LASTEXITCODE 'Remote failure should be preserved.'
    Assert-Equal 'remote.ps1' ((Get-Content $marker) -join ',') 'Remote failure must not silently run local validation.'
}
finally {
    if ($null -ne $originalFallbackEnvironment) { $env:BOTNEXUS_VALIDATION_LOCAL_FALLBACK = $originalFallbackEnvironment }
    if ($null -ne $originalModeEnvironment) { $env:BOTNEXUS_VALIDATION_MODE = $originalModeEnvironment }
    foreach ($repository in $repositories) {
        Remove-Item $repository -Recurse -Force -ErrorAction SilentlyContinue
    }
    foreach ($entry in $gitEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}

Write-Host 'Validate-PreCommit tests passed.' -ForegroundColor Green
exit 0


