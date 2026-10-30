<#
.SYNOPSIS
Tests exact-source transport and the production Azure receipt boundary offline.
.DESCRIPTION
Runs Pester regressions using isolated Git repositories and extracted production source
fragments. No Azure execution, gateway, dotnet or product test host is started. Two
case-sensitive filesystem acceptance cases require Linux and deliberately fail elsewhere.
Invoke Pester with this explicit file path because its approved command name does not
match Pester's default *.Tests.ps1 directory-discovery suffix.
.EXAMPLE
Invoke-Pester -Path ./scripts/repo/Test-SourceSnapshot.ps1 -Output Detailed
Runs all source transport cases, including the Linux filesystem acceptance checks.
.OUTPUTS
None. Pester reports assertion results to its caller.
#>
# Offline only: no Azure, gateway, test hosts, or caller Git configuration.
BeforeAll {
    $script:taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $script:scratch = Join-Path $taskRoot ('artifacts/source-snapshot-tests-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($scratch) | Out-Null
    $script:savedEnvironment = @{}
    Get-ChildItem Env: | Where-Object { $_.Name -match '^(GIT_|GH_|GITHUB_|HOME$|XDG_CONFIG_HOME$)' } | ForEach-Object {
        $savedEnvironment[$_.Name] = $_.Value
        Remove-Item "Env:$($_.Name)" -ErrorAction SilentlyContinue
    }
    $env:GIT_CONFIG_NOSYSTEM = '1'
    $env:GIT_CONFIG_GLOBAL = Join-Path $scratch 'no-config'
    $env:HOME = $scratch
    $env:XDG_CONFIG_HOME = $scratch
    $env:GIT_TERMINAL_PROMPT = '0'
    $env:GIT_ALLOW_PROTOCOL = 'file'
    $env:GIT_AUTHOR_NAME = $env:GIT_COMMITTER_NAME = 'Snapshot Fixture'
    $env:GIT_AUTHOR_EMAIL = $env:GIT_COMMITTER_EMAIL = 'snapshot@example.com'
    if (Test-Path (Join-Path $PSScriptRoot 'SourceSnapshot.psm1')) {
        Import-Module (Join-Path $PSScriptRoot 'SourceSnapshot.psm1') -Force
    }
    function Invoke-GitFixture {
        <#
        .SYNOPSIS
        Runs Git against an isolated fixture repository.
        .DESCRIPTION
        Preserves native output and throws immediately for a nonzero Git exit.
        .PARAMETER Arguments
        Exact Git argument array, including the isolated repository path.
        .EXAMPLE
        Invoke-GitFixture -Arguments @('-C', $repo, 'status', '--short')
        .OUTPUTS
        System.Object. Git standard output.
        #>
        param([string[]]$Arguments)
        $output = & git @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Fixture git failed: $output"
        }
        $output
    }
    function Invoke-SourceFragment {
        <#
        .SYNOPSIS
        Executes an extracted production fragment with explicit fixture variables.
        .DESCRIPTION
        Uses InvokeWithContext to supply named PSVariable instances from a dictionary.
        Production source stays unchanged; fixture dependencies are explicit and observable.
        .PARAMETER Source
        Exact production source fragment to execute.
        .PARAMETER Context
        Dictionary of variable names and fixture values visible to the fragment.
        .EXAMPLE
        Invoke-SourceFragment -Source $guard -Context $guardContext
        .OUTPUTS
        System.Object. Unmodified pipeline output from the production fragment.
        #>
        [CmdletBinding()]
        param(
            [Parameter(Mandatory)][string]$Source,
            [Parameter(Mandatory)][hashtable]$Context
        )
        $variables = [Collections.Generic.List[System.Management.Automation.PSVariable]]::new()
        foreach ($entry in $Context.GetEnumerator()) {
            $variables.Add([System.Management.Automation.PSVariable]::new($entry.Key, $entry.Value))
        }
        ([scriptblock]::Create($Source)).InvokeWithContext($null, $variables, @())
    }
    function Invoke-ProductionTransport {
        <#
        .SYNOPSIS
        Roundtrips a Git fixture through the real sender and runner transport fragments.
        .DESCRIPTION
        Extracts current production capture and restore blocks and supplies isolated paths
        through explicit variable dictionaries. Returns the reconstructed source directory.
        .PARAMETER Repo
        Isolated source Git repository.
        .PARAMETER Area
        Owned fixture artifact directory for archive creation and extraction.
        .EXAMPLE
        Invoke-ProductionTransport -Repo $repo -Area $area
        .OUTPUTS
        System.String. Reconstructed source directory.
        #>
        [CmdletBinding()]
        param([string]$Repo, [string]$Area)
        $context = @{
            repoRoot = $Repo
            tempRoot = $Area
            runId = 'fixture-run'
            BaseRef = 'HEAD'
            bundlePath = Join-Path $Area 'repository.bundle'
            workspaceArchive = Join-Path $Area 'workspace.tar.gz'
            payloadArchive = Join-Path $Area 'payload.tar.gz'
            payloadRoot = Join-Path $Area 'payload'
            sourceRoot = Join-Path $Area 'reconstructed'
            fingerprintScript = Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1'
        }
        [IO.Directory]::CreateDirectory($context.payloadRoot) | Out-Null
        $senderSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Invoke-AzureBuildTest.ps1'))
        $runner = [IO.File]::ReadAllText((Join-Path $taskRoot 'infra/buildtest/runner/entrypoint.ps1'))
        if ($senderSource.Contains('# BEGIN EXACT SOURCE CAPTURE')) {
            Import-Module (Join-Path $PSScriptRoot 'SourceSnapshot.psm1') -Force
            Invoke-SourceFragment -Source ([regex]::Match($senderSource, '(?s)# BEGIN EXACT SOURCE CAPTURE(.*?)# END EXACT SOURCE CAPTURE').Groups[1].Value) -Context $context
            Invoke-SourceFragment -Source ([regex]::Match($runner, '(?s)# BEGIN EXACT SOURCE RESTORE(.*?)# END EXACT SOURCE RESTORE').Groups[1].Value) -Context $context
        }
        else {
            # Execute CURRENT production packaging and extraction, not a mock of the defect.
            $capture = [regex]::Match($senderSource, '(?s)    & git -C \$repoRoot bundle create.*?(?=    \$sourceBlob =)').Value
            if (-not $capture) {
                throw 'Production capture block not found'
            }
            Invoke-SourceFragment -Source $capture -Context $context
            $restore = [regex]::Match($runner, '(?s)    tar -xzf \$payloadArchive.*?(?=    # The packed payload)').Value
            if (-not $restore) {
                throw 'Production restore block not found'
            }
            Invoke-SourceFragment -Source $restore -Context $context
        }
        $context.sourceRoot
    }
}
Describe 'Production source transport regression' {
    It 'does not re-read the captured tree before payload creation' {
        $senderSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Invoke-AzureBuildTest.ps1'))
        $capture = [regex]::Match($senderSource, '(?s)# BEGIN EXACT SOURCE CAPTURE(.*?)# END EXACT SOURCE CAPTURE').Groups[1].Value

        $capture | Should -Not -Match 'Assert-SourceSnapshot\s+-Root\s+\$captureRoot'
        $capture | Should -Match 'New-SourcePayloadArchive\s+-Root\s+\$tempRoot'
        $capture | Should -Match '\$current\s*=\s*&\s*\$fingerprintScript'
    }
    BeforeEach {
        $script:area = Join-Path $scratch ([guid]::NewGuid().ToString('N'))
        $script:repo = Join-Path $area 'repo'
        [IO.Directory]::CreateDirectory($repo) | Out-Null
        Invoke-GitFixture @('-C', $repo, 'init', '-b', 'main') | Out-Null
        [IO.File]::WriteAllText((Join-Path $repo 'deleted-staged.md'), 'remove me')
        [IO.File]::WriteAllText((Join-Path $repo 'keep.md'), 'keep me')
        Invoke-GitFixture @('-C', $repo, 'add', '--all') | Out-Null
        Invoke-GitFixture @('-C', $repo, 'commit', '-m', 'fixture base') | Out-Null
    }
    AfterAll {
        Get-ChildItem Env: | Where-Object { $_.Name -match '^(GIT_|GH_|GITHUB_|HOME$|XDG_CONFIG_HOME$)' } | ForEach-Object { Remove-Item "Env:$($_.Name)" -ErrorAction SilentlyContinue }
        foreach ($entry in $savedEnvironment.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
        }
        if (Test-Path -LiteralPath $scratch) {
            Remove-Item -LiteralPath $scratch -Recurse -Force
        }
    }
    It 'creates a runner-compatible payload without recompressing compressed inputs' {
        $payloadArea = Join-Path $area 'payload-archive'
        [IO.Directory]::CreateDirectory($payloadArea) | Out-Null
        $random = [byte[]]::new(2MB)
        [Security.Cryptography.RandomNumberGenerator]::Fill($random)
        foreach ($name in @('repository.bundle', 'workspace.zip')) {
            [IO.File]::WriteAllBytes((Join-Path $payloadArea $name), $random)
        }
        [IO.File]::WriteAllText((Join-Path $payloadArea 'source-manifest.json'), '{}')
        [IO.File]::WriteAllText((Join-Path $payloadArea 'SourceSnapshot.psm1'), '# fixture')
        $payload = Join-Path $payloadArea 'payload.tar.gz'

        $proof = New-SourcePayloadArchive -Root $payloadArea -Destination $payload

        $proof.compression | Should -Be 'gzip-no-compression'
        $proof.inputBytes | Should -BeGreaterThan 4MB
        $proof.outputBytes | Should -BeGreaterThan 4MB
        $proof.outputBytes | Should -BeLessThan ($proof.inputBytes + 1MB)
        $listed = @(& tar -tzf $payload)
        $LASTEXITCODE | Should -Be 0
        $listed | Should -Be @('repository.bundle', 'workspace.zip', 'source-manifest.json', 'SourceSnapshot.psm1')
    }
    It 'captures deletions, renames, additions, modifications and literal portable names' {
        Remove-Item -LiteralPath (Join-Path $repo 'deleted-staged.md')
        Invoke-GitFixture @('-C', $repo, 'mv', 'keep.md', 'renamed.md') | Out-Null
        [IO.File]::WriteAllText((Join-Path $repo 'renamed.md'), "modified`r`nbytes")
        foreach ($name in @('staged.md', 'space name.md', 'café.md', '-leading.md', '[brackets].md')) {
            [IO.File]::WriteAllText((Join-Path $repo $name), $name)
        }
        Invoke-GitFixture @('-C', $repo, 'add', '--', 'staged.md') | Out-Null
        $restored = Invoke-ProductionTransport $repo $area
        Test-Path -LiteralPath (Join-Path $restored 'deleted-staged.md') | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $restored 'keep.md') | Should -BeFalse
        [IO.File]::ReadAllText((Join-Path $restored 'renamed.md')) | Should -Be "modified`r`nbytes"
        foreach ($name in @('staged.md', 'space name.md', 'café.md', '-leading.md', '[brackets].md')) {
            [IO.File]::ReadAllText((Join-Path $restored $name)) | Should -Be $name
        }
    }
    It 'fingerprints raw bytes and preserves forced ignored additions and caller index' {
        [IO.File]::WriteAllText((Join-Path $repo '.gitignore'), 'forced.md')
        [IO.File]::WriteAllText((Join-Path $repo 'forced.md'), 'forced')
        Invoke-GitFixture @('-C', $repo, 'add', '-f', '--', 'forced.md') | Out-Null
        $index = Join-Path $repo '.git/index'
        $env:GIT_INDEX_FILE = $index
        try {
            $before = (Get-FileHash -LiteralPath $index).Hash
            $first = & (Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1') -WorktreePath $repo -BaseRef HEAD
            $env:GIT_INDEX_FILE | Should -Be $index
            (Get-FileHash -LiteralPath $index).Hash | Should -Be $before
            $restored = Invoke-ProductionTransport $repo $area
            [IO.File]::ReadAllText((Join-Path $restored 'forced.md')) | Should -Be 'forced'
            [IO.File]::WriteAllText((Join-Path $repo 'keep.md'), "keep me`r`n")
            $crlf = & (Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1') -WorktreePath $repo -BaseRef HEAD
            [IO.File]::WriteAllText((Join-Path $repo 'keep.md'), "keep me`n")
            $lf = & (Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1') -WorktreePath $repo -BaseRef HEAD
            $crlf.fingerprint | Should -Not -Be $lf.fingerprint
            $first.sourceSnapshot.version | Should -Be 1
        }
        finally {
            Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
        }
    }
    It 'rejects unresolved conflicts but accepts resolved uncommitted merge without mutating Git state' {
        Invoke-GitFixture @('-C', $repo, 'checkout', '-b', 'side') | Out-Null
        [IO.File]::WriteAllText((Join-Path $repo 'keep.md'), 'side')
        Invoke-GitFixture @('-C', $repo, 'commit', '-am', 'side') | Out-Null
        Invoke-GitFixture @('-C', $repo, 'checkout', 'main') | Out-Null
        [IO.File]::WriteAllText((Join-Path $repo 'keep.md'), 'main')
        Invoke-GitFixture @('-C', $repo, 'commit', '-am', 'main') | Out-Null
        & git -C $repo merge side 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 1
        { & (Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1') -WorktreePath $repo -BaseRef HEAD } | Should -Throw '*unmerged*'
        [IO.File]::WriteAllText((Join-Path $repo 'keep.md'), 'resolved')
        Invoke-GitFixture @('-C', $repo, 'add', '--', 'keep.md') | Out-Null
        Invoke-GitFixture @('-C', $repo, 'rm', '--', 'deleted-staged.md') | Out-Null
        $paths = @('HEAD', 'MERGE_HEAD', 'index') | ForEach-Object { Join-Path $repo ".git/$_" }
        $before = @($paths | ForEach-Object { (Get-FileHash -LiteralPath $_).Hash }) -join ','
        $restored = Invoke-ProductionTransport $repo $area
        [IO.File]::ReadAllText((Join-Path $restored 'keep.md')) | Should -Be 'resolved'
        Test-Path -LiteralPath (Join-Path $restored 'deleted-staged.md') | Should -BeFalse
        (@($paths | ForEach-Object { (Get-FileHash -LiteralPath $_).Hash }) -join ',') | Should -Be $before
    }
    It 'rejects unsafe manifest path <path>' -ForEach @(
        @{path = '../escape' }, @{path = '/absolute' }, @{path = 'C:/drive' }, @{path = 'a\b' },
        @{path = '.git/config' }, @{path = 'a/../b' }, @{path = 'a//b' }, @{path = "bad`nname" }, @{path = 'a/.git/config' }
    ) {
        { Assert-SourceSnapshotPath $path } | Should -Throw '*Unsafe*'
    }
    It 'rejects index links and submodules' {
        $blob = (Invoke-GitFixture @('-C', $repo, 'rev-parse', 'HEAD:keep.md')).Trim()
        Invoke-GitFixture @('-C', $repo, 'update-index', '--add', '--cacheinfo', "120000,$blob,link") | Out-Null
        { Get-SourceSnapshotManifest -RepoRoot $repo } | Should -Throw '*Unsupported*'
        Invoke-GitFixture @('-C', $repo, 'update-index', '--force-remove', '--', 'link') | Out-Null
        $head = (Invoke-GitFixture @('-C', $repo, 'rev-parse', 'HEAD')).Trim()
        Invoke-GitFixture @('-C', $repo, 'update-index', '--add', '--cacheinfo', "160000,$head,submodule") | Out-Null
        { Get-SourceSnapshotManifest -RepoRoot $repo } | Should -Throw '*Unsupported*'
    }
    It 'rejects missing, tampered and extra restored files' {
        $restored = Invoke-ProductionTransport $repo $area
        $manifest = Get-Content -LiteralPath (Join-Path $area 'source-manifest.json') -Raw | ConvertFrom-Json
        [IO.File]::WriteAllText((Join-Path $restored 'keep.md'), 'tampered')
        { Assert-SourceSnapshot -Root $restored -Manifest $manifest } | Should -Throw
        [IO.File]::WriteAllText((Join-Path $restored 'keep.md'), 'keep me')
        Remove-Item -LiteralPath (Join-Path $restored 'keep.md')
        { Assert-SourceSnapshot -Root $restored -Manifest $manifest } | Should -Throw
        [IO.File]::WriteAllText((Join-Path $restored 'keep.md'), 'keep me')
        [IO.File]::WriteAllText((Join-Path $restored 'extra.md'), 'extra')
        { Assert-SourceSnapshot -Root $restored -Manifest $manifest } | Should -Throw
    }
    It 'requires matching proof and sound test counts at the production receipt boundary' {
        $senderSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Invoke-AzureBuildTest.ps1'))
        $guard = [regex]::Match($senderSource, '(?s)# BEGIN EXACT SOURCE RECEIPT GUARD(.*?)# END EXACT SOURCE RECEIPT GUARD').Groups[1].Value
        $guard | Should -Not -BeNullOrEmpty
        # Execute the guard AND real receipt writer, ending before Azure cleanup.
        $guard = [regex]::Match($senderSource, '(?s)# BEGIN EXACT SOURCE RECEIPT GUARD(.*?)(?=    if \(-not \$KeepRemoteArtifacts\))').Groups[1].Value
        $guard | Should -Not -BeNullOrEmpty
        Import-Module (Join-Path $PSScriptRoot 'AzureBuildTestRecovery.psm1') -Force
        $executionName = 'offline-execution'
        $receiptPath = Join-Path $repo '.git/botnexus-validation/azure-buildtest.json'
        $runId = 'receipt-run'
        $Mode = 'core'
        $status = [pscustomobject]@{ properties = [pscustomobject]@{ status = 'Succeeded'; startTime = '2026-10-28T01:00:00Z'; endTime = '2026-10-28T01:10:00Z' } }
        $fingerprintScript = Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1'
        $fingerprint = & $fingerprintScript -WorktreePath $repo -BaseRef HEAD
        $recoveryContext = [pscustomobject]@{version = 1; runId = $runId; mode = $Mode; executionName = $executionName; jobUrl = 'https://management.azure.com/subscriptions/fixture/resourceGroups/fixture/providers/Microsoft.App/jobs/fixture'; subscriptionId = 'fixture'; resourceGroup = 'fixture'; jobName = 'fixture'; expectedPreviousReceipt = 'absent'; fingerprint = $fingerprint }
        $status | Add-Member name $executionName
        $status | Add-Member id "/subscriptions/fixture/resourceGroups/fixture/providers/Microsoft.App/jobs/fixture/executions/$executionName"
        $status.properties | Add-Member template ([pscustomobject]@{containers = @([pscustomobject]@{env = @([pscustomobject]@{name = 'RUN_ID'; value = $runId }, [pscustomobject]@{name = 'TEST_MODE'; value = $Mode }, [pscustomobject]@{name = 'BASE_REF'; value = 'HEAD' }) }) })
        $result = [pscustomobject]@{completedUtc = [DateTime]::UtcNow.ToString('o'); runId = $runId; mode = $Mode; exitCode = 0; tests = [pscustomobject]@{total = 12000; executed = 12000; passed = 12000; failed = 0; skipped = 0; fixtureFailures = 0; isComplete = $true; failureReason = $null } }
        $guardContext = @{
            Mode = $Mode
            status = $status
            result = $result
            requiredArtifactsPresent = $true
            fingerprint = $fingerprint
            fingerprintScript = $fingerprintScript
            runId = $runId
            repoRoot = $repo
            BaseRef = 'HEAD'
            recoveryContext = $recoveryContext
            OutputPath = $area
        }
        { Invoke-SourceFragment -Source $guard -Context $guardContext } | Should -Throw '*operator*'
        $proof = [pscustomobject]@{version = 1; digest = $fingerprint.sourceSnapshot.digest; runId = $runId; verified = $true }
        $result | Add-Member sourceSnapshot $proof
        { Invoke-SourceFragment -Source $guard -Context $guardContext } | Should -Not -Throw
        $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        $receipt.fingerprint | Should -Be $fingerprint.fingerprint
        $receipt.sourceSnapshot.digest | Should -Be $proof.digest
        Remove-Item -LiteralPath $receiptPath
        $proof.digest = 'wrong'
        { Invoke-SourceFragment -Source $guard -Context $guardContext } | Should -Throw
        $proof.digest = $fingerprint.sourceSnapshot.digest; $proof.runId = 'wrong'
        { Invoke-SourceFragment -Source $guard -Context $guardContext } | Should -Throw
        $proof.runId = $runId; $result.tests = $null
        { Invoke-SourceFragment -Source $guard -Context $guardContext } | Should -Throw
        $result.tests = [pscustomobject]@{total = 12000; executed = 12001; passed = 12000; failed = 0; skipped = 0; fixtureFailures = 0; isComplete = $true; failureReason = $null }
        { Invoke-SourceFragment -Source $guard -Context $guardContext } | Should -Throw
        $result.tests.executed = 12000
        [IO.File]::WriteAllText((Join-Path $repo 'keep.md'), 'changed after remote run')
        { Invoke-SourceFragment -Source $guard -Context $guardContext } | Should -Throw '*changed before receipt*'
        Test-Path -LiteralPath (Join-Path $repo '.git/botnexus-validation/azure-buildtest.json') | Should -BeFalse
    }
    It 'requires every runner mode to produce a test contract' {
        $runner = [IO.File]::ReadAllText((Join-Path $taskRoot 'infra/buildtest/runner/entrypoint.ps1'))
        $runner | Should -Not -Match 'if \(\$strictResults\)'
        $runner | Should -Match 'sourceSnapshot = \$sourceSnapshot'
    }
    It 'rejects archive traversal, link entries and missing files before replacing existing contents' {
        $manifest = Get-SourceSnapshotManifest -RepoRoot $repo
        $archive = Join-Path $area 'hostile.zip'
        foreach ($attack in @('../escape', '.git/config', 'link', 'missing')) {
            $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
            try {
                $entry = $zip.CreateEntry($attack)
                if ($attack -eq 'link') {
                    $entry.ExternalAttributes = [int](0xA000 -shl 16)
                }
            }
            finally {
                $zip.Dispose()
            }
            { Restore-SourceSnapshot -Root $repo -Archive $archive -Manifest $manifest -RunId test } | Should -Throw
            [IO.File]::ReadAllText((Join-Path $repo 'keep.md')) | Should -Be 'keep me'
            Remove-Item -LiteralPath $archive
        }
    }
    It 'refuses filesystem reparse traversal' {
        $outside = Join-Path $area 'outside'
        [IO.Directory]::CreateDirectory($outside) | Out-Null
        [IO.File]::WriteAllText((Join-Path $outside 'file.md'), 'outside')
        $link = Join-Path $repo 'linked'
        $type = if ($IsWindows) {
            'Junction'
        }
        else {
            'SymbolicLink'
        }
        New-Item -ItemType $type -Path $link -Target $outside | Out-Null
        try {
            $blob = (Invoke-GitFixture @('-C', $repo, 'rev-parse', 'HEAD:keep.md')).Trim()
            Invoke-GitFixture @('-C', $repo, 'update-index', '--add', '--cacheinfo', "100644,$blob,linked/file.md") | Out-Null
            { Get-SourceSnapshotManifest -RepoRoot $repo } | Should -Throw '*reparse*'
        }
        finally {
            Remove-Item -LiteralPath $link -Force
        }
    }
    It 'restores absent index environment after success and failure and invalidates legacy receipts' {
        Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
        $fp = & (Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1') -WorktreePath $repo -BaseRef HEAD
        Test-Path Env:GIT_INDEX_FILE | Should -BeFalse
        $material = "$($fp.head)`n$($fp.baseCommit)`n$($fp.tree)`n"
        $legacy = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($material))).ToLowerInvariant()
        $fp.fingerprint | Should -Not -Be $legacy
        { & (Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1') -WorktreePath $repo -BaseRef missing-ref } | Should -Throw
        Test-Path Env:GIT_INDEX_FILE | Should -BeFalse
    }
    It 'roundtrips a tracked file replaced by an unstaged directory' {
        Invoke-GitFixture @('-C', $repo, 'mv', 'keep.md', 'config') | Out-Null
        Invoke-GitFixture @('-C', $repo, 'commit', '-am', 'config file') | Out-Null
        Remove-Item -LiteralPath (Join-Path $repo 'config')
        [IO.Directory]::CreateDirectory((Join-Path $repo 'config')) | Out-Null
        [IO.File]::WriteAllText((Join-Path $repo 'config/settings.json'), '{"new":true}')
        $restored = Invoke-ProductionTransport $repo $area
        Test-Path -LiteralPath (Join-Path $restored 'config') -PathType Container | Should -BeTrue
        [IO.File]::ReadAllText((Join-Path $restored 'config/settings.json')) | Should -Be '{"new":true}'
    }
    It 'roundtrips an unstaged case-only rename on a real case-sensitive filesystem' -Tag LinuxCaseSensitive {
        $IsLinux | Should -BeTrue -Because 'this acceptance test requires the isolated Linux harness'
        Invoke-GitFixture @('-C', $repo, 'mv', 'keep.md', 'A.cs') | Out-Null
        Invoke-GitFixture @('-C', $repo, 'commit', '-am', 'uppercase file') | Out-Null
        [IO.File]::Move((Join-Path $repo 'A.cs'), (Join-Path $repo 'a.cs'))
        Test-Path -LiteralPath (Join-Path $repo 'A.cs') | Should -BeFalse
        $restored = Invoke-ProductionTransport $repo $area
        Test-Path -LiteralPath (Join-Path $restored 'A.cs') | Should -BeFalse
        [IO.File]::ReadAllText((Join-Path $restored 'a.cs')) | Should -Be 'keep me'
    }
    It 'rejects two existing case-colliding files on a case-sensitive filesystem' -Tag LinuxCaseSensitive {
        $IsLinux | Should -BeTrue
        [IO.File]::WriteAllText((Join-Path $repo 'A.cs'), 'upper')
        [IO.File]::WriteAllText((Join-Path $repo 'a.cs'), 'lower')
        @(Get-ChildItem -LiteralPath $repo | Where-Object Name -CLike '*.cs').Count | Should -Be 2
        { Get-SourceSnapshotManifest -RepoRoot $repo } | Should -Throw '*Unsafe case-colliding source path*'
    }
    It 'rejects only link attributes in an otherwise valid archive and accepts its regular-file control' {
        $manifest = Get-SourceSnapshotManifest -RepoRoot $repo
        $capture = Join-Path $area 'link-control'
        [IO.Directory]::CreateDirectory($capture) | Out-Null
        Get-SourceSnapshotManifest -RepoRoot $repo -CaptureRoot $capture | Out-Null
        $archive = Join-Path $area 'matching.zip'
        [IO.Compression.ZipFile]::CreateFromDirectory($capture, $archive)
        $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Update)
        try {
            $zip.GetEntry('keep.md').ExternalAttributes = [int](0xA000 -shl 16)
        }
        finally {
            $zip.Dispose()
        }
        [IO.File]::WriteAllText((Join-Path $repo 'keep.md'), 'destination sentinel')
        { Restore-SourceSnapshot -Root $repo -Archive $archive -Manifest $manifest -RunId test } | Should -Throw '*Unsupported archive link/directory.*'
        [IO.File]::ReadAllText((Join-Path $repo 'keep.md')) | Should -Be 'destination sentinel'
        $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Update)
        try {
            $zip.GetEntry('keep.md').ExternalAttributes = [int](0x8000 -shl 16)
        }
        finally {
            $zip.Dispose()
        }
        $proof = Restore-SourceSnapshot -Root $repo -Archive $archive -Manifest $manifest -RunId test
        $proof.verified | Should -BeTrue
        [IO.File]::ReadAllText((Join-Path $repo 'keep.md')) | Should -Be 'keep me'
    }
    It 'does not resurrect a staged deletion from bundled HEAD' {
        Invoke-GitFixture @('-C', $repo, 'rm', '--', 'deleted-staged.md') | Out-Null
        $restored = Invoke-ProductionTransport $repo $area
        Test-Path -LiteralPath (Join-Path $restored 'deleted-staged.md') | Should -BeFalse
        [IO.File]::ReadAllText((Join-Path $restored 'keep.md')) | Should -Be 'keep me'
    }
}
