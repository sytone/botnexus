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

function Assert-Match([string]$Pattern, [string[]]$Output, [string]$Message) {
    $joined = ($Output -join "`n")
    if ($joined -notmatch $Pattern) {
        $failures.Add("$Message Expected output matching '$Pattern', got: $joined")
    }
}

# Every call routes through here so no test block can accidentally read ambient
# Process/User/Machine configuration (#2400). Both the mode selector AND the legacy
# BOTNEXUS_VALIDATION_LOCAL_FALLBACK escape hatch are injected as empty three-scope
# maps. Output is captured so assertions can prove which branch was actually taken
# rather than inferring it from an exit code that many paths share.
function Invoke-ValidationScript {
    param([hashtable]$Parameters)

    $arguments = @{} + $Parameters
    $arguments.ValidationModeEnvironment = [ordered]@{ Process = $null; User = $null; Machine = $null }
    $arguments.LegacyFallbackEnvironment = [ordered]@{ Process = $null; User = $null; Machine = $null }
    $global:LASTEXITCODE = 0
    $output = & $validationScript @arguments *>&1 | ForEach-Object { [string]$_ }
    $script:scenariosExercised++
    return [pscustomobject]@{ Output = @($output); ExitCode = $LASTEXITCODE }
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
    Invoke-IsolatedGit -Arguments @('-C', $path, 'config', '--local', 'user.email', 'test@domain.com') *> $null
    Set-Content (Join-Path $path 'candidate.txt') 'candidate' -Encoding utf8NoBOM
    Invoke-IsolatedGit -Arguments @('-C', $path, 'add', '--all') *> $null
    # Isolate fixture commits from the caller's configured hooks. Otherwise a global
    # core.hooksPath can recursively invoke BotNexus validation from inside the fixture.
    Invoke-IsolatedGit -Arguments @('-c', 'core.hooksPath=', '-C', $path, 'commit', '-m', 'initial') *> $null
    Invoke-IsolatedGit -Arguments @('-C', $path, 'branch', 'origin/main') *> $null
    return $path
}

function Write-Receipt([string]$Repository, [string]$Mode = 'full') {
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
        "Add-Content -Path '$($Marker.Replace("'", "''")).modes' -Value ('$Name -Mode ' + `$Mode)"
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

# The reported timeout budget must equal the DEPLOYED replicaTimeout, or the breach warning
# reassures against a number nobody is enforcing. Two spellings of one value is the same
# defect family as #2793/#2796, so pin them to each other rather than trusting a comment.
$bicepSource = Get-Content (Join-Path $repoRoot 'infra/buildtest/main.bicep') -Raw
if ($bicepSource -notmatch 'replicaTimeout:\s*(\d+)') {
    $failures.Add('Could not read replicaTimeout from infra/buildtest/main.bicep.')
}
elseif ($azureRunnerSource -notmatch 'ReplicaTimeoutMinutes\s*=\s*(\d+)') {
    $failures.Add('Could not read ReplicaTimeoutMinutes from Invoke-AzureBuildTest.ps1.')
}
else {
    $bicepSeconds = [int]([regex]::Match($bicepSource, 'replicaTimeout:\s*(\d+)').Groups[1].Value)
    $scriptMinutes = [int]([regex]::Match($azureRunnerSource, 'ReplicaTimeoutMinutes\s*=\s*(\d+)').Groups[1].Value)
    if ($bicepSeconds -ne $scriptMinutes * 60) {
        $failures.Add("Timeout budget drift: main.bicep replicaTimeout is ${bicepSeconds}s but Invoke-AzureBuildTest reports ${scriptMinutes} min ($($scriptMinutes * 60)s).")
    }
}
if ($azureRunnerSource -notmatch "(?s)Mode -ne 'strict'.+playwrightArtifact" -or
    $azureRunnerSource -notmatch 'result.exitCode -eq 0 -and\s+\$requiredArtifactsPresent') {
    $failures.Add('Strict Azure receipt creation must require a Playwright artifact.')
}
# Preserve the original cross-platform path-safety invariant with the replacement
# transport: NUL-safe Git enumeration and ZIP entries replace tar list-file parsing.
$snapshotSource = Get-Content (Join-Path $repoRoot 'scripts/repo/SourceSnapshot.psm1') -Raw
if ($azureRunnerSource -match 'ls-files.+-z.+tar --null' -or
    $azureRunnerSource -notmatch 'ZipFile\]::CreateFromDirectory' -or
    $azureRunnerSource -notmatch 'Assert-SourceSnapshot -Root \$captureRoot' -or
    $snapshotSource -notmatch "'--exclude-standard', '-z'" -or
    $snapshotSource -notmatch 'Assert-SourceSnapshotPath \$entry.FullName') {
    $failures.Add('Azure snapshots must preserve literal paths through NUL-safe enumeration, ZIP capture and validated reconstruction.')
}
$entrypointSource = Get-Content (Join-Path $repoRoot 'infra/buildtest/runner/entrypoint.ps1') -Raw
if ($entrypointSource -notmatch "playwright\.log" -or
    $entrypointSource -notmatch "'strict' \{") {
    $failures.Add('The remote runner must implement strict mode and fail when Playwright did not run.')
}

$repositories = [Collections.Generic.List[string]]::new()
# Scenario counter: proves the block below actually ran. If an early throw skips the
# scenarios, this stays low and the run is reported RED instead of vacuously green.
$script:scenariosExercised = 0
$expectedScenarioCount = 9
# Clear git's per-invocation environment for the duration of the run. When this test is
# executed FROM the pre-commit hook, git exports GIT_INDEX_FILE/GIT_DIR/GIT_PREFIX etc.
# Those leak into the fixture repositories and make Get-WorktreeValidationFingerprint
# read the OUTER repository's index, so every fixture receipt mismatches and the
# receipt-bypass scenarios never execute their branch. The values are restored in the
# finally block below from $gitEnvironment (#2400).
foreach ($name in $gitLocalEnvironmentNames) {
    # Remove-Item, not SetEnvironmentVariable($name, $null): PowerShell binds $null to the
    # string overload as an EMPTY STRING, and git treats an empty GIT_DIR as a real (broken)
    # value rather than an absent one.
    if (-not [string]::IsNullOrWhiteSpace($name)) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
}
try {
    # Exact-content receipts are authoritative only for selected remote validation.
    $repo = New-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = New-CommandScript $repo 'remote.ps1' $marker
    $local = New-CommandScript $repo 'local.ps1' $marker
    Write-Receipt $repo
    $result = Invoke-ValidationScript @{ WorktreePath = $repo; AzureValidationScript = $remote; LocalValidationScript = $local; ValidationMode = 'remote' }
    Assert-Equal 0 $result.ExitCode 'Matching remote receipt should pass.'
    Assert-Equal $false (Test-Path $marker) 'Matching remote receipt should bypass redundant validation.'
    Assert-Match 'Validation mode: remote' $result.Output 'Matching remote receipt should resolve remote mode despite ambient configuration.'
    Assert-Match 'skipping redundant remote validation' $result.Output 'The receipt-bypass branch must be genuinely evaluated, not skipped.'

    # #2825: remote validation must dispatch the FULL solution, not the impacted subset.
    # Strict was measured to exercise ~4,700 of 13,088 tests, so a silent regression to it
    # would drop roughly two-thirds of coverage while still reporting a green gate. A
    # strict receipt must NOT satisfy a full gate either, or the bypass reintroduces it.
    $repo = New-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = New-CommandScript $repo 'remote.ps1' $marker
    $local = New-CommandScript $repo 'local.ps1' $marker
    $result = Invoke-ValidationScript @{ WorktreePath = $repo; AzureValidationScript = $remote; LocalValidationScript = $local; ValidationMode = 'remote' }
    Assert-Equal 0 $result.ExitCode 'Remote validation should pass.'
    Assert-Match '-Mode full' (Get-Content "$marker.modes") 'Remote validation must dispatch full-solution mode by default.'

    $repo = New-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = New-CommandScript $repo 'remote.ps1' $marker
    $local = New-CommandScript $repo 'local.ps1' $marker
    Write-Receipt $repo 'strict'
    $result = Invoke-ValidationScript @{ WorktreePath = $repo; AzureValidationScript = $remote; LocalValidationScript = $local; ValidationMode = 'remote' }
    Assert-Equal $true (Test-Path $marker) 'A strict receipt must not satisfy the full remote gate.'

    # #2158: REMOTE is the operational default. An unconfigured caller must reach the Azure
    # runner and must NOT touch the local script, because local validation spawns gateway
    # processes that outlive their parent and steal the live gateway's cron jobs. The marker
    # assertion is the load-bearing half: it proves local was never invoked, not merely that
    # the run happened to succeed.
    $repo = New-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = New-CommandScript $repo 'remote.ps1' $marker
    $local = New-CommandScript $repo 'local.ps1' $marker 9
    $result = Invoke-ValidationScript @{ WorktreePath = $repo; AzureValidationScript = $remote; LocalValidationScript = $local }
    Assert-Equal 0 $result.ExitCode 'Default remote validation should pass.'
    Assert-Equal 'remote.ps1' ((Get-Content $marker) -join ',') 'Default validation should select remote only.'
    Assert-Match 'Validation mode: remote' $result.Output 'Default validation should resolve remote mode.'

    # Exact-content receipts are authoritative and bypass remote work.
    $repo = New-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = New-CommandScript $repo 'remote.ps1' $marker
    $local = New-CommandScript $repo 'local.ps1' $marker
    Write-Receipt $repo
    $result = Invoke-ValidationScript @{ WorktreePath = $repo; AzureValidationScript = $remote; LocalValidationScript = $local; ValidationMode = 'remote' }
    Assert-Equal 0 $result.ExitCode 'Matching receipt should pass.'
    Assert-Equal $false (Test-Path $marker) 'Matching receipt should bypass redundant validation.'
    Assert-Match 'skipping redundant remote validation' $result.Output 'The receipt-bypass branch must be genuinely evaluated, not skipped.'

    # Any content change invalidates the receipt when remote mode is selected.
    $repo = New-TestRepository; $repositories.Add($repo); Write-Receipt $repo
    Add-Content (Join-Path $repo 'candidate.txt') 'changed'
    $marker = Join-Path $repo 'commands.log'
    $remote = New-CommandScript $repo 'remote.ps1' $marker
    $local = New-CommandScript $repo 'local.ps1' $marker
    $result = Invoke-ValidationScript @{ WorktreePath = $repo; AzureValidationScript = $remote; LocalValidationScript = $local; ValidationMode = 'remote' }
    Assert-Equal 0 $result.ExitCode 'Selected remote validation should pass.'
    Assert-Equal 'remote.ps1' ((Get-Content $marker) -join ',') 'Stale receipt should select Azure only.'
    Assert-Match 'does not match the exact candidate' $result.Output 'Receipt staleness must be genuinely evaluated, not skipped.'

    # Local fallback is opt-in and uses a cross-process serialization lock.
    $repo = New-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = New-CommandScript $repo 'remote.ps1' $marker 9
    $local = New-CommandScript $repo 'local.ps1' $marker
    $result = Invoke-ValidationScript @{ WorktreePath = $repo; AzureValidationScript = $remote; LocalValidationScript = $local; LocalFallback = $true }
    Assert-Equal 0 $result.ExitCode 'Explicit local fallback should pass.'
    Assert-Equal 'local.ps1' ((Get-Content $marker) -join ',') 'Explicit fallback should not attempt Azure first.'
    Assert-Match 'Validation mode: local' $result.Output 'Explicit fallback should resolve local mode.'

    # The durable selector can choose remote validation without removing local support.
    # The selector value is INJECTED rather than written to $env:, so this scenario neither
    # depends on nor races ambient machine state (#2400).
    $repo = New-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = New-CommandScript $repo 'remote.ps1' $marker 9
    $local = New-CommandScript $repo 'local.ps1' $marker
    $script:scenariosExercised++
    $global:LASTEXITCODE = 0
    $selectorOutput = @(& $validationScript -WorktreePath $repo -AzureValidationScript $remote -LocalValidationScript $local -ValidationModeEnvironment ([ordered]@{ Process = 'remote'; User = $null; Machine = $null }) -LegacyFallbackEnvironment ([ordered]@{ Process = $null; User = $null; Machine = $null }) *>&1 | ForEach-Object { [string]$_ })
    Assert-Equal 9 $LASTEXITCODE 'Environment-selected remote validation should preserve failure.'
    Assert-Equal 'remote.ps1' ((Get-Content $marker) -join ',') 'Environment selector should choose remote only.'
    Assert-Match 'Validation mode: remote' $selectorOutput 'Environment selector must genuinely resolve remote mode.'

    # A failed authoritative remote run must not silently fall back locally.
    $repo = New-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = New-CommandScript $repo 'remote.ps1' $marker 9
    $local = New-CommandScript $repo 'local.ps1' $marker
    $result = Invoke-ValidationScript @{ WorktreePath = $repo; AzureValidationScript = $remote; LocalValidationScript = $local; ValidationMode = 'remote' }
    Assert-Equal 9 $result.ExitCode 'Remote failure should be preserved.'
    Assert-Equal 'remote.ps1' ((Get-Content $marker) -join ',') 'Remote failure must not silently run local validation.'
}
finally {
    foreach ($repository in $repositories) {
        Remove-Item $repository -Recurse -Force -ErrorAction SilentlyContinue
    }
    foreach ($entry in $gitEnvironment.GetEnumerator()) {
        if ($null -eq $entry.Value) { Remove-Item "Env:$($entry.Key)" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
    }
}

if ($scenariosExercised -ne $expectedScenarioCount) {
    $failures.Add("Vacuous run guard: expected $expectedScenarioCount validation scenarios to execute, but only $scenariosExercised ran.")
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    Write-Host "Validate-PreCommit tests FAILED ($($failures.Count) failure(s) across $scenariosExercised scenario(s))." -ForegroundColor Red
    exit 1
}

Write-Host "Validate-PreCommit tests passed ($scenariosExercised scenarios, 0 failures)." -ForegroundColor Green
exit 0



