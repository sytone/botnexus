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
    if (Test-Path (Join-Path $PSScriptRoot 'SourceSnapshot.psm1')) { Import-Module (Join-Path $PSScriptRoot 'SourceSnapshot.psm1') -Force }
    function Git-Fixture {
        param([string[]]$Arguments)
        $output = & git @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Fixture git failed: $output" }
        $output
    }
    function Invoke-ProductionTransport {
        param($Repo, $Area)
        $repoRoot = $Repo; $tempRoot = $Area
        $runId = 'fixture-run'; $BaseRef = 'HEAD'
        $bundlePath = Join-Path $Area 'repository.bundle'
        $workspaceArchive = Join-Path $Area 'workspace.tar.gz'
        $payloadArchive = Join-Path $Area 'payload.tar.gz'
        $payloadRoot = Join-Path $Area 'payload'
        $sourceRoot = Join-Path $Area 'reconstructed'
        [IO.Directory]::CreateDirectory($payloadRoot) | Out-Null
        $sender = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Invoke-AzureBuildTest.ps1'))
        $runner = [IO.File]::ReadAllText((Join-Path $taskRoot 'infra/buildtest/runner/entrypoint.ps1'))
        if ($sender.Contains('# BEGIN EXACT SOURCE CAPTURE')) {
            Import-Module (Join-Path $PSScriptRoot 'SourceSnapshot.psm1') -Force
            $fingerprintScript = Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1'
            . ([scriptblock]::Create([regex]::Match($sender, '(?s)# BEGIN EXACT SOURCE CAPTURE(.*?)# END EXACT SOURCE CAPTURE').Groups[1].Value))
            . ([scriptblock]::Create([regex]::Match($runner, '(?s)# BEGIN EXACT SOURCE RESTORE(.*?)# END EXACT SOURCE RESTORE').Groups[1].Value))
        }
        else {
            # Execute CURRENT production packaging and extraction, not a mock of the defect.
            $capture = [regex]::Match($sender, '(?s)    & git -C \$repoRoot bundle create.*?(?=    \$sourceBlob =)').Value
            if (-not $capture) { throw 'Production capture block not found' }
            . ([scriptblock]::Create($capture))
            $restore = [regex]::Match($runner, '(?s)    tar -xzf \$payloadArchive.*?(?=    # The packed payload)').Value
            if (-not $restore) { throw 'Production restore block not found' }
            . ([scriptblock]::Create($restore))
        }
        $sourceRoot
    }
}
Describe 'Production source transport regression' {
BeforeEach {
    $script:area = Join-Path $scratch ([guid]::NewGuid().ToString('N'))
    $script:repo = Join-Path $area 'repo'
    [IO.Directory]::CreateDirectory($repo) | Out-Null
    Git-Fixture @('-C', $repo, 'init', '-b', 'main') | Out-Null
    [IO.File]::WriteAllText((Join-Path $repo 'deleted-staged.md'), 'remove me')
    [IO.File]::WriteAllText((Join-Path $repo 'keep.md'), 'keep me')
    Git-Fixture @('-C', $repo, 'add', '--all') | Out-Null
    Git-Fixture @('-C', $repo, 'commit', '-m', 'fixture base') | Out-Null
}
AfterAll {
    Get-ChildItem Env: | Where-Object { $_.Name -match '^(GIT_|GH_|GITHUB_|HOME$|XDG_CONFIG_HOME$)' } | ForEach-Object { Remove-Item "Env:$($_.Name)" -ErrorAction SilentlyContinue }
    foreach ($entry in $savedEnvironment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value) }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
    It 'captures deletions, renames, additions, modifications and literal portable names' {
        Remove-Item -LiteralPath (Join-Path $repo 'deleted-staged.md')
        Git-Fixture @('-C', $repo, 'mv', 'keep.md', 'renamed.md') | Out-Null
        [IO.File]::WriteAllText((Join-Path $repo 'renamed.md'), "modified`r`nbytes")
        foreach ($name in @('staged.md', 'space name.md', 'café.md', '-leading.md', '[brackets].md')) {
            [IO.File]::WriteAllText((Join-Path $repo $name), $name)
        }
        Git-Fixture @('-C', $repo, 'add', '--', 'staged.md') | Out-Null
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
        Git-Fixture @('-C', $repo, 'add', '-f', '--', 'forced.md') | Out-Null
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
        finally { Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue }
    }
    It 'rejects unresolved conflicts but accepts resolved uncommitted merge without mutating Git state' {
        Git-Fixture @('-C', $repo, 'checkout', '-b', 'side') | Out-Null
        [IO.File]::WriteAllText((Join-Path $repo 'keep.md'), 'side')
        Git-Fixture @('-C', $repo, 'commit', '-am', 'side') | Out-Null
        Git-Fixture @('-C', $repo, 'checkout', 'main') | Out-Null
        [IO.File]::WriteAllText((Join-Path $repo 'keep.md'), 'main')
        Git-Fixture @('-C', $repo, 'commit', '-am', 'main') | Out-Null
        & git -C $repo merge side 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 1
        { & (Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1') -WorktreePath $repo -BaseRef HEAD } | Should -Throw '*unmerged*'
        [IO.File]::WriteAllText((Join-Path $repo 'keep.md'), 'resolved')
        Git-Fixture @('-C', $repo, 'add', '--', 'keep.md') | Out-Null
        Git-Fixture @('-C', $repo, 'rm', '--', 'deleted-staged.md') | Out-Null
        $paths = @('HEAD', 'MERGE_HEAD', 'index') | ForEach-Object { Join-Path $repo ".git/$_" }
        $before = @($paths | ForEach-Object { (Get-FileHash -LiteralPath $_).Hash }) -join ','
        $restored = Invoke-ProductionTransport $repo $area
        [IO.File]::ReadAllText((Join-Path $restored 'keep.md')) | Should -Be 'resolved'
        Test-Path -LiteralPath (Join-Path $restored 'deleted-staged.md') | Should -BeFalse
        (@($paths | ForEach-Object { (Get-FileHash -LiteralPath $_).Hash }) -join ',') | Should -Be $before
    }
    It 'rejects unsafe manifest path <path>' -ForEach @(
        @{path='../escape'}, @{path='/absolute'}, @{path='C:/drive'}, @{path='a\b'},
        @{path='.git/config'}, @{path='a/../b'}, @{path='a//b'}, @{path="bad`nname"}, @{path='a/.git/config'}
    ) {
        { Assert-SourceSnapshotPath $path } | Should -Throw '*Unsafe*'
    }
    It 'rejects index links and submodules' {
        $blob = (Git-Fixture @('-C', $repo, 'rev-parse', 'HEAD:keep.md')).Trim()
        Git-Fixture @('-C', $repo, 'update-index', '--add', '--cacheinfo', "120000,$blob,link") | Out-Null
        { Get-SourceSnapshotManifest -RepoRoot $repo } | Should -Throw '*Unsupported*'
        Git-Fixture @('-C', $repo, 'update-index', '--force-remove', '--', 'link') | Out-Null
        $head = (Git-Fixture @('-C', $repo, 'rev-parse', 'HEAD')).Trim()
        Git-Fixture @('-C', $repo, 'update-index', '--add', '--cacheinfo', "160000,$head,submodule") | Out-Null
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
        $sender = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Invoke-AzureBuildTest.ps1'))
        $guard = [regex]::Match($sender, '(?s)# BEGIN EXACT SOURCE RECEIPT GUARD(.*?)# END EXACT SOURCE RECEIPT GUARD').Groups[1].Value
        $guard | Should -Not -BeNullOrEmpty
        # Execute the guard AND real receipt writer, ending before Azure cleanup.
        $guard = [regex]::Match($sender, '(?s)# BEGIN EXACT SOURCE RECEIPT GUARD(.*?)(?=    if \(-not \$KeepRemoteArtifacts\))').Groups[1].Value
        $guard | Should -Not -BeNullOrEmpty
        $executionName = 'offline-execution'
        $receiptPath = Join-Path $repo '.git/botnexus-validation/azure-buildtest.json'
        $runId = 'receipt-run'; $Mode = 'core'; $requiredArtifactsPresent = $true
        $status = [pscustomobject]@{ properties = [pscustomobject]@{ status='Succeeded' } }
        $repoRoot = $repo; $BaseRef = 'HEAD'
        $fingerprintScript = Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1'
        $fingerprint = & $fingerprintScript -WorktreePath $repo -BaseRef HEAD
        $result = [pscustomobject]@{completedUtc=[DateTime]::UtcNow.ToString('o');runId=$runId;mode=$Mode;exitCode=0;tests=[pscustomobject]@{total=12000;executed=12000;passed=12000;failed=0;skipped=0;fixtureFailures=0;isComplete=$true;failureReason=$null}}
        { . ([scriptblock]::Create($guard)) } | Should -Throw '*operator*'
        $proof = [pscustomobject]@{version=1;digest=$fingerprint.sourceSnapshot.digest;runId=$runId;verified=$true}
        $result | Add-Member sourceSnapshot $proof
        { . ([scriptblock]::Create($guard)) } | Should -Not -Throw
        $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        $receipt.fingerprint | Should -Be $fingerprint.fingerprint
        $receipt.sourceSnapshot.digest | Should -Be $proof.digest
        Remove-Item -LiteralPath $receiptPath
        $proof.digest = 'wrong'
        { . ([scriptblock]::Create($guard)) } | Should -Throw
        $proof.digest = $fingerprint.sourceSnapshot.digest; $proof.runId='wrong'
        { . ([scriptblock]::Create($guard)) } | Should -Throw
        $proof.runId=$runId; $result.tests=$null
        { . ([scriptblock]::Create($guard)) } | Should -Throw
        $result.tests=[pscustomobject]@{total=12000;executed=12001;passed=12000;failed=0;skipped=0;fixtureFailures=0;isComplete=$true;failureReason=$null}
        { . ([scriptblock]::Create($guard)) } | Should -Throw
        $result.tests.executed=12000
        [IO.File]::WriteAllText((Join-Path $repo 'keep.md'), 'changed after remote run')
        { . ([scriptblock]::Create($guard)) } | Should -Throw '*changed before receipt*'
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
                if ($attack -eq 'link') { $entry.ExternalAttributes = [int](0xA000 -shl 16) }
            }
            finally { $zip.Dispose() }
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
        $type = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
        New-Item -ItemType $type -Path $link -Target $outside | Out-Null
        try {
            $blob = (Git-Fixture @('-C', $repo, 'rev-parse', 'HEAD:keep.md')).Trim()
            Git-Fixture @('-C', $repo, 'update-index', '--add', '--cacheinfo', "100644,$blob,linked/file.md") | Out-Null
            { Get-SourceSnapshotManifest -RepoRoot $repo } | Should -Throw '*reparse*'
        }
        finally { Remove-Item -LiteralPath $link -Force }
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
    It 'does not resurrect a staged deletion from bundled HEAD' {
        Git-Fixture @('-C', $repo, 'rm', '--', 'deleted-staged.md') | Out-Null
        $restored = Invoke-ProductionTransport $repo $area
        Test-Path -LiteralPath (Join-Path $restored 'deleted-staged.md') | Should -BeFalse
        [IO.File]::ReadAllText((Join-Path $restored 'keep.md')) | Should -Be 'keep me'
    }
}
