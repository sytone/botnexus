<#
.SYNOPSIS
Tests canonical validation selection and approved v2 receipt reuse offline.
.DESCRIPTION
Uses disposable Git repositories and synthetic delegates that only write markers.
No Azure, dotnet, testhost or gateway is executed. The nine original selection
scenarios plus legacy-receipt and base-only-change safeguards inject environment
resolution and preserve original safety assertions.
.EXAMPLE
./Test-PreCommitValidation.ps1
.OUTPUTS
None. Reports scenario failures and exits nonzero, or reports eleven passing scenarios.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot | Split-Path -Parent | Split-Path -Parent
$validationScript = Join-Path $repoRoot 'scripts/repo/Invoke-PreCommitValidation.ps1'
$fingerprintScript = Join-Path $repoRoot 'scripts/repo/Get-WorktreeValidationFingerprint.ps1'
$failures = [Collections.Generic.List[string]]::new()
$savedSelector = $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST
if (-not $env:BOTNEXUS_TEST_SCRATCH_ROOT) {
    throw 'Set BOTNEXUS_TEST_SCRATCH_ROOT to owned task scratch.'
}
$gitEnvironment = @{}
$gitLocalEnvironmentNames = @(& git -C $repoRoot rev-parse --local-env-vars)
foreach ($name in $gitLocalEnvironmentNames) {
    if ([string]::IsNullOrWhiteSpace($name)) {
        continue
    }
    $gitEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

<#
.SYNOPSIS
Records unequal expected and actual fixture values.
.DESCRIPTION
Records unequal expected and actual fixture values. This helper is isolated test infrastructure, not an operator command.
.PARAMETER Expected
Expected assertion value.
.PARAMETER Actual
Observed assertion value.
.PARAMETER Message
Failure context.
.EXAMPLE
Assert-Equal -Expected 0 -Actual $result.ExitCode -Message 'Gate failed.'
.OUTPUTS
None. Adds a useful failure message without bypassing later scenarios.
#>
function Assert-Equal([object]$Expected, [object]$Actual, [string]$Message) {
    if ($Expected -ne $Actual) {
        $failures.Add("$Message Expected '$Expected', got '$Actual'.")
    }
}

<#
.SYNOPSIS
Requires captured fixture output to match the expected branch marker.
.DESCRIPTION
Requires captured fixture output to match the expected branch marker. This helper is isolated test infrastructure, not an operator command.
.PARAMETER Pattern
Expected regular expression.
.PARAMETER Output
Captured output lines.
.PARAMETER Message
Failure context.
.EXAMPLE
Assert-Match -Pattern 'remote' -Output $result.Output -Message 'Wrong mode.'
.OUTPUTS
None. Records branch-routing failures.
#>
function Assert-Match([string]$Pattern, [string[]]$Output, [string]$Message) {
    $joined = ($Output -join "`n")
    if ($joined -notmatch $Pattern) {
        $failures.Add("$Message Expected output matching '$Pattern', got: $joined")
    }
}

<#
.SYNOPSIS
Runs the real gate with synthetic delegates and injected empty mode environment.
.DESCRIPTION
Runs the real gate with synthetic delegates and injected empty mode environment.
Both mode and legacy fallback values are explicit three-scope maps. Captured output
proves which branch ran, not merely an exit code. This is isolated test infrastructure.
.PARAMETER Parameters
Explicit gate parameters including synthetic delegates.
.EXAMPLE
Invoke-ValidationScript -Parameters $fixtureParameters
.OUTPUTS
System.Management.Automation.PSCustomObject. Captured output and exit status.
#>
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

<#
.SYNOPSIS
Runs Git after removing inherited repository-selection variables from the child.
.DESCRIPTION
Runs Git after removing inherited repository-selection variables from the child. This helper is isolated test infrastructure, not an operator command.
.PARAMETER Arguments
Git argument vector.
.EXAMPLE
Invoke-IsolatedGit -Arguments @('-C', $fixture, 'status', '--short')
.OUTPUTS
System.String. Git stdout; propagates the native exit status.
#>
function Invoke-IsolatedGit {
    param([string[]]$Arguments)

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'git'
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($name in $gitLocalEnvironmentNames) {
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            [void]$startInfo.Environment.Remove($name)
        }
    }
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($startInfo)
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $global:LASTEXITCODE = $process.ExitCode
    if ($stdout.Length -gt 0) {
        Write-Output $stdout.TrimEnd()
    }
    if ($stderr.Length -gt 0) {
        Write-Error $stderr.TrimEnd() -ErrorAction Continue
    }
}

<#
.SYNOPSIS
Creates a disposable Git repository under explicitly owned test scratch.
.DESCRIPTION
Creates a disposable Git repository under explicitly owned test scratch. This helper is isolated test infrastructure, not an operator command.
.EXAMPLE
Initialize-TestRepository
.OUTPUTS
System.String. Synthetic repository path.
#>
function Initialize-TestRepository {
    $path = Join-Path $env:BOTNEXUS_TEST_SCRATCH_ROOT "botnexus-validation-test-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $path | Out-Null
    Invoke-IsolatedGit -Arguments @('-c', 'core.bare=false', '-C', $path, 'init', '--initial-branch', 'main') *> $null
    if (-not (Test-Path (Join-Path $path '.git') -PathType Container)) {
        throw "Unable to initialize test repository: $path"
    }
    Invoke-IsolatedGit -Arguments @('-C', $path, 'config', '--local', 'user.name', 'test') *> $null
    Invoke-IsolatedGit -Arguments @('-C', $path, 'config', '--local', 'user.email', 'test@domain.com') *> $null
    Set-Content (Join-Path $path 'candidate.txt') 'candidate' -Encoding utf8NoBOM
    $runner = Join-Path $path 'infra/buildtest/runner'
    [IO.Directory]::CreateDirectory($runner) | Out-Null
    [IO.File]::WriteAllText((Join-Path $runner 'entrypoint.ps1'), '# fixture runner')
    Invoke-IsolatedGit -Arguments @('-C', $path, 'add', '--all') *> $null
    # Isolate fixture commits from the caller's configured hooks. Otherwise a global
    # core.hooksPath can recursively invoke BotNexus validation from inside the fixture.
    Invoke-IsolatedGit -Arguments @('-c', 'core.hooksPath=', '-C', $path, 'commit', '-m', 'initial') *> $null
    Invoke-IsolatedGit -Arguments @('-C', $path, 'branch', 'origin/main') *> $null
    return $path
}

<#
.SYNOPSIS
Writes a synthetic v2 receipt with independently framed qualified runner input approval.
.DESCRIPTION
Writes a synthetic v2 receipt with independently framed qualified runner input approval. This helper is isolated test infrastructure, not an operator command.
.PARAMETER Repository
Disposable candidate root.
.PARAMETER Mode
Receipt certification scope.
.EXAMPLE
Write-Receipt -Repository $fixture -Mode core
.OUTPUTS
None. Creates only fixture approval and receipt files.
#>
function Write-Receipt([string]$Repository, [string]$Mode = 'core') {
    $relative = 'infra/buildtest/runner/entrypoint.ps1'
    $content = '# fixture runner'
    $material = "botnexus-qualified-execution-inputs-v1`0" + ('{0}:{1}{2}:{3}' -f [Text.Encoding]::UTF8.GetByteCount($relative), $relative, [Text.Encoding]::UTF8.GetByteCount($content), $content)
    $image = 'fixture.invalid/runner@sha256:' + ('b' * 64)
    $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST = $Repository + '.operator.json'
    [ordered]@{
        schemaVersion = 1
        releaseId = 'fixture-qualified-1'
        image = $image
        approved = $true
        revoked = $false
        runnerInputDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($material))).ToLowerInvariant()
        modes = @('core', 'strict')
        payloadVersion = 1
        evidenceVersion = 1
        capabilities = @('exact-source-v1')
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST -Encoding utf8NoBOM
    Import-Module (Join-Path $PSScriptRoot 'RunnerReleaseCompatibility.psm1') -Force
    $binding = Assert-RunnerReleaseCompatibility -ManifestPath $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST -CandidateRoot $Repository -DeployedImage $image -Mode $Mode
    $fingerprint = & $fingerprintScript -WorktreePath $Repository
    $gitDirectory = (Invoke-IsolatedGit -Arguments @('-C', $Repository, 'rev-parse', '--absolute-git-dir')).Trim()
    $receiptDirectory = Join-Path $gitDirectory 'botnexus-validation'
    New-Item -ItemType Directory -Path $receiptDirectory -Force | Out-Null
    @{
        version = 2
        runnerRelease = $binding
        executionConfiguration = 'image-entrypoint-v1'
        sourceSnapshot = [pscustomobject]@{ version = 1; verified = $true; digest = $fingerprint.sourceSnapshot.digest; runId = 'test-run' }
        fingerprint = $fingerprint.fingerprint
        head = $fingerprint.head
        baseRef = $fingerprint.baseRef
        baseCommit = $fingerprint.baseCommit
        tree = $fingerprint.tree
        mode = $Mode
        runId = 'test-run'
    } | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $receiptDirectory 'azure-buildtest.json') -Encoding utf8NoBOM
}

<#
.SYNOPSIS
Creates an inert fixture delegate that writes a marker and returns the requested status.
.DESCRIPTION
Creates an inert fixture delegate that writes a marker and returns the requested status. This helper is isolated test infrastructure, not an operator command.
.PARAMETER Directory
Disposable output directory.
.PARAMETER Name
Synthetic delegate filename.
.PARAMETER Marker
Fixture marker file.
.PARAMETER ExitCode
Native status the synthetic delegate returns.
.EXAMPLE
Initialize-CommandScript -Directory $fixture -Name remote.ps1 -Marker $marker -ExitCode 0
.OUTPUTS
System.String. Synthetic delegate path.
#>
function Initialize-CommandScript([string]$Directory, [string]$Name, [string]$Marker, [int]$ExitCode = 0) {
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
    $azureRunnerSource -notmatch 'SourceSnapshot\.psm1' -or
    $azureRunnerSource -notmatch 'ZipFile\]::CreateFromDirectory' -or
    $snapshotSource -notmatch "'--exclude-standard', '-z'" -or
    $snapshotSource -notmatch 'Assert-SourceSnapshotPath \$entry.FullName') {
    $failures.Add('Azure snapshots must use the exact-source module and preserve literal paths through NUL-safe enumeration, ZIP capture and validated reconstruction.')
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
$expectedScenarioCount = 11
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
    if (-not [string]::IsNullOrWhiteSpace($name)) {
        Remove-Item "Env:$name" -ErrorAction SilentlyContinue
    }
}
try {
    # Exact-content receipts are authoritative only for selected remote validation.
    $repo = Initialize-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = Initialize-CommandScript -Directory $repo -Name 'remote.ps1' -Marker $marker
    $local = Initialize-CommandScript -Directory $repo -Name 'local.ps1' -Marker $marker
    Write-Receipt -Repository $repo
    $result = Invoke-ValidationScript -Parameters @{ WorktreePath = $repo; AzureValidationScript = $remote; LocalValidationScript = $local; ValidationMode = 'remote' }
    Assert-Equal -Expected 0 -Actual $result.ExitCode -Message 'Matching remote receipt should pass.'
    Assert-Equal -Expected $false -Actual (Test-Path $marker) -Message 'Matching remote receipt should bypass redundant validation.'
    Assert-Match -Pattern 'Validation mode: remote' -Output $result.Output -Message 'Matching remote receipt should resolve remote mode despite ambient configuration.'
    Assert-Match -Pattern 'skipping redundant remote validation' -Output $result.Output -Message 'The receipt-bypass branch must be genuinely evaluated, not skipped.'

    # #3601: remote validation dispatches the complete non-browser CORE suite while browser-
    # emulated E2E is quarantined for review. Strict was measured to exercise ~4,700 tests, so
    # a silent regression to it would still drop most coverage. A strict receipt must NOT satisfy
    # the core gate either.
    $repo = Initialize-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = Initialize-CommandScript -Directory $repo -Name 'remote.ps1' -Marker $marker
    $local = Initialize-CommandScript -Directory $repo -Name 'local.ps1' -Marker $marker
    $result = Invoke-ValidationScript -Parameters @{ WorktreePath = $repo; AzureValidationScript = $remote; LocalValidationScript = $local; ValidationMode = 'remote' }
    Assert-Equal -Expected 0 -Actual $result.ExitCode -Message 'Remote validation should pass.'
    Assert-Match -Pattern '-Mode core' -Output (Get-Content "$marker.modes") -Message 'Remote validation must dispatch fail-closed non-browser core mode by default.'

    $repo = Initialize-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = Initialize-CommandScript -Directory $repo -Name 'remote.ps1' -Marker $marker
    $local = Initialize-CommandScript -Directory $repo -Name 'local.ps1' -Marker $marker
    Write-Receipt -Repository $repo -Mode 'strict'
    $result = Invoke-ValidationScript -Parameters @{ WorktreePath = $repo; AzureValidationScript = $remote; LocalValidationScript = $local; ValidationMode = 'remote' }
    Assert-Equal -Expected $true -Actual (Test-Path $marker) -Message 'A strict receipt must not satisfy the core remote gate.'

    # #2158: REMOTE is the operational default. An unconfigured caller must reach the Azure
    # runner and must NOT touch the local script, because local validation spawns gateway
    # processes that outlive their parent and steal the live gateway's cron jobs. The marker
    # assertion is the load-bearing half: it proves local was never invoked, not merely that
    # the run happened to succeed.
    $repo = Initialize-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = Initialize-CommandScript -Directory $repo -Name 'remote.ps1' -Marker $marker
    $local = Initialize-CommandScript -Directory $repo -Name 'local.ps1' -Marker $marker -ExitCode 9
    $result = Invoke-ValidationScript -Parameters @{ WorktreePath = $repo; AzureValidationScript = $remote; LocalValidationScript = $local }
    Assert-Equal -Expected 0 -Actual $result.ExitCode -Message 'Default remote validation should pass.'
    Assert-Equal -Expected 'remote.ps1' -Actual ((Get-Content $marker) -join ',') -Message 'Default validation should select remote only.'
    Assert-Match -Pattern 'Validation mode: remote' -Output $result.Output -Message 'Default validation should resolve remote mode.'

    # Exact-content receipts are authoritative and bypass remote work.
    $repo = Initialize-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = Initialize-CommandScript -Directory $repo -Name 'remote.ps1' -Marker $marker
    $local = Initialize-CommandScript -Directory $repo -Name 'local.ps1' -Marker $marker
    Write-Receipt -Repository $repo
    $result = Invoke-ValidationScript -Parameters @{ WorktreePath = $repo; AzureValidationScript = $remote; LocalValidationScript = $local; ValidationMode = 'remote' }
    Assert-Equal -Expected 0 -Actual $result.ExitCode -Message 'Matching receipt should pass.'
    Assert-Equal -Expected $false -Actual (Test-Path $marker) -Message 'Matching receipt should bypass redundant validation.'
    Assert-Match -Pattern 'skipping redundant remote validation' -Output $result.Output -Message 'The receipt-bypass branch must be genuinely evaluated, not skipped.'

    # Any content change invalidates the receipt when remote mode is selected.
    $repo = Initialize-TestRepository; $repositories.Add($repo); Write-Receipt -Repository $repo
    Add-Content (Join-Path $repo 'candidate.txt') 'changed'
    $marker = Join-Path $repo 'commands.log'
    $remote = Initialize-CommandScript -Directory $repo -Name 'remote.ps1' -Marker $marker
    $local = Initialize-CommandScript -Directory $repo -Name 'local.ps1' -Marker $marker
    $result = Invoke-ValidationScript -Parameters @{ WorktreePath = $repo; AzureValidationScript = $remote; LocalValidationScript = $local; ValidationMode = 'remote' }
    Assert-Equal -Expected 0 -Actual $result.ExitCode -Message 'Selected remote validation should pass.'
    Assert-Equal -Expected 'remote.ps1' -Actual ((Get-Content $marker) -join ',') -Message 'Stale receipt should select Azure only.'
    Assert-Match -Pattern 'does not match the required v2 candidate and qualification contract' -Output $result.Output -Message 'Receipt staleness must be genuinely evaluated, not skipped.'

    # Legacy evidence cannot self-upgrade even when current approval is valid.
    $repo = Initialize-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = Initialize-CommandScript -Directory $repo -Name 'remote.ps1' -Marker $marker
    $local = Initialize-CommandScript -Directory $repo -Name 'local.ps1' -Marker $marker -ExitCode 9
    Write-Receipt -Repository $repo
    $receiptPath = Join-Path $repo '.git/botnexus-validation/azure-buildtest.json'
    $legacy = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    $legacy.version = 1
    $legacy | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
    $result = Invoke-ValidationScript -Parameters @{ WorktreePath = $repo; AzureValidationScript = $remote; LocalValidationScript = $local; ValidationMode = 'remote' }
    Assert-Equal -Expected 0 -Actual $result.ExitCode -Message 'Legacy rejection should use the synthetic remote delegate.'
    Assert-Equal -Expected 'remote.ps1' -Actual ((Get-Content $marker) -join ',') -Message 'Legacy receipt must not bypass validation or run local fallback.'
    Assert-Match -Pattern 'does not match the required v2 candidate and qualification contract' -Output $result.Output -Message 'Legacy rejection must be specifically evaluated.'

    # Moving only the comparison base invalidates otherwise unchanged candidate bytes.
    $repo = Initialize-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = Initialize-CommandScript -Directory $repo -Name 'remote.ps1' -Marker $marker
    $local = Initialize-CommandScript -Directory $repo -Name 'local.ps1' -Marker $marker -ExitCode 9
    Write-Receipt -Repository $repo
    $before = & $fingerprintScript -WorktreePath $repo
    $newBase = Invoke-IsolatedGit -Arguments @('-C', $repo, '-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit-tree', $before.tree, '-p', $before.head, '-m', 'base-only fixture')
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to construct synthetic comparison commit.'
    }
    Invoke-IsolatedGit -Arguments @('-C', $repo, 'update-ref', 'refs/heads/origin/main', $newBase) | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to move synthetic comparison base.'
    }
    $after = & $fingerprintScript -WorktreePath $repo
    Assert-Equal -Expected $before.tree -Actual $after.tree -Message 'Base-only fixture must not change candidate tree.'
    Assert-Equal -Expected $before.sourceSnapshot.digest -Actual $after.sourceSnapshot.digest -Message 'Base-only fixture must not change source bytes.'
    Assert-Equal -Expected $false -Actual ($before.baseCommit -eq $after.baseCommit) -Message 'Base-only fixture must actually move the comparison base.'
    $result = Invoke-ValidationScript -Parameters @{ WorktreePath = $repo; AzureValidationScript = $remote; LocalValidationScript = $local; ValidationMode = 'remote' }
    Assert-Equal -Expected 0 -Actual $result.ExitCode -Message 'Base mismatch should use synthetic remote validation.'
    Assert-Equal -Expected 'remote.ps1' -Actual ((Get-Content $marker) -join ',') -Message 'A changed base must invalidate reuse without local fallback.'
    Assert-Match -Pattern 'does not match the required v2 candidate and qualification contract' -Output $result.Output -Message 'Base mismatch must be specifically evaluated.'

    # Local fallback is opt-in and uses a cross-process serialization lock.
    $repo = Initialize-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = Initialize-CommandScript -Directory $repo -Name 'remote.ps1' -Marker $marker -ExitCode 9
    $local = Initialize-CommandScript -Directory $repo -Name 'local.ps1' -Marker $marker
    $result = Invoke-ValidationScript -Parameters @{ WorktreePath = $repo; AzureValidationScript = $remote; LocalValidationScript = $local; LocalFallback = $true }
    Assert-Equal -Expected 0 -Actual $result.ExitCode -Message 'Explicit local fallback should pass.'
    Assert-Equal -Expected 'local.ps1' -Actual ((Get-Content $marker) -join ',') -Message 'Explicit fallback should not attempt Azure first.'
    Assert-Match -Pattern 'Validation mode: local' -Output $result.Output -Message 'Explicit fallback should resolve local mode.'

    # The durable selector can choose remote validation without removing local support.
    # The selector value is INJECTED rather than written to $env:, so this scenario neither
    # depends on nor races ambient machine state (#2400).
    $repo = Initialize-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = Initialize-CommandScript -Directory $repo -Name 'remote.ps1' -Marker $marker -ExitCode 9
    $local = Initialize-CommandScript -Directory $repo -Name 'local.ps1' -Marker $marker
    $script:scenariosExercised++
    $global:LASTEXITCODE = 0
    $selectorOutput = @(& $validationScript -WorktreePath $repo -AzureValidationScript $remote -LocalValidationScript $local -ValidationModeEnvironment ([ordered]@{ Process = 'remote'; User = $null; Machine = $null }) -LegacyFallbackEnvironment ([ordered]@{ Process = $null; User = $null; Machine = $null }) *>&1 | ForEach-Object { [string]$_ })
    Assert-Equal -Expected 9 -Actual $LASTEXITCODE -Message 'Environment-selected remote validation should preserve failure.'
    Assert-Equal -Expected 'remote.ps1' -Actual ((Get-Content $marker) -join ',') -Message 'Environment selector should choose remote only.'
    Assert-Match -Pattern 'Validation mode: remote' -Output $selectorOutput -Message 'Environment selector must genuinely resolve remote mode.'

    # A failed authoritative remote run must not silently fall back locally.
    $repo = Initialize-TestRepository; $repositories.Add($repo)
    $marker = Join-Path $repo 'commands.log'
    $remote = Initialize-CommandScript -Directory $repo -Name 'remote.ps1' -Marker $marker -ExitCode 9
    $local = Initialize-CommandScript -Directory $repo -Name 'local.ps1' -Marker $marker
    $result = Invoke-ValidationScript -Parameters @{ WorktreePath = $repo; AzureValidationScript = $remote; LocalValidationScript = $local; ValidationMode = 'remote' }
    Assert-Equal -Expected 9 -Actual $result.ExitCode -Message 'Remote failure should be preserved.'
    Assert-Equal -Expected 'remote.ps1' -Actual ((Get-Content $marker) -join ',') -Message 'Remote failure must not silently run local validation.'
}
finally {
    $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST = $savedSelector
    foreach ($repository in $repositories) {
        Remove-Item -LiteralPath ($repository + '.operator.json') -ErrorAction SilentlyContinue
        Remove-Item $repository -Recurse -Force -ErrorAction SilentlyContinue
    }
    foreach ($entry in $gitEnvironment.GetEnumerator()) {
        if ($null -eq $entry.Value) {
            Remove-Item "Env:$($entry.Key)" -ErrorAction SilentlyContinue
        }
        else {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }
}

if ($scenariosExercised -ne $expectedScenarioCount) {
    $failures.Add("Vacuous run guard: expected $expectedScenarioCount validation scenarios to execute, but only $scenariosExercised ran.")
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    Write-Information -MessageData "Invoke-PreCommitValidation tests FAILED ($($failures.Count) failure(s) across $scenariosExercised scenario(s))." -InformationAction Continue
    exit 1
}

Write-Information -MessageData "Invoke-PreCommitValidation tests passed ($scenariosExercised scenarios, 0 failures)." -InformationAction Continue
exit 0
