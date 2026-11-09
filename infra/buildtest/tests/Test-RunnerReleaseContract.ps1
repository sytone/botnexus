<#
.SYNOPSIS
Specifies the external operator runner-release compatibility boundary.
.DESCRIPTION
Pester 5 tests with synthetic files only. Pins the read-only compatibility API;
missing commands are API-contract RED, not behavioral RED. No Azure or product runtime.
The fixture manifest is NOT operator approval for any real release.
.EXAMPLE
Invoke-Pester -Path ./infra/buildtest/tests/Test-RunnerReleaseContract.ps1
.OUTPUTS
None. Pester reports contract assertion results.
#>
[CmdletBinding()]
param()

BeforeAll {
    $script:repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    $script:module = Join-Path $script:repository 'scripts/repo/RunnerReleaseCompatibility.psm1'
    if (Test-Path -LiteralPath $script:module) {
        Import-Module $script:module -Force
    }
    if (-not $env:BOTNEXUS_TEST_SCRATCH_ROOT) {
        throw 'Set BOTNEXUS_TEST_SCRATCH_ROOT to owned isolated scratch.'
    }
    $script:root = Join-Path $env:BOTNEXUS_TEST_SCRATCH_ROOT ([guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($script:root) | Out-Null
}

AfterAll {
    Remove-Item -LiteralPath $script:root -Recurse -Force
}

Describe 'Operator-owned release compatibility API' {
    BeforeEach {
        $script:candidate = Join-Path $script:root ([guid]::NewGuid().ToString('N'))
        $runner = Join-Path $script:candidate 'infra/buildtest/runner'
        [IO.Directory]::CreateDirectory($runner) | Out-Null
        [IO.File]::WriteAllText((Join-Path $runner 'entrypoint.ps1'), "# synthetic runner`n")
        $script:manifestPath = Join-Path $script:root 'operator-release.json'
        $script:image = 'example.invalid/runner@sha256:' + ('b' * 64)
        # Single fixture file. Production must enumerate the full approved execution input set.
        $script:frame = {
            param([string]$Path, [string]$Content)
            '{0}:{1}{2}:{3}' -f [Text.Encoding]::UTF8.GetByteCount($Path), $Path, [Text.Encoding]::UTF8.GetByteCount($Content), $Content
        }
        $material = "botnexus-qualified-execution-inputs-v1`0" + (& $script:frame 'infra/buildtest/runner/entrypoint.ps1' "# synthetic runner`n")
        $script:inputDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($material))).ToLowerInvariant()
        $script:document = [ordered]@{
            schemaVersion = 1
            releaseId = 'fixture-release-1'
            image = $script:image
            approved = $true
            revoked = $false
            runnerInputDigest = $script:inputDigest
            modes = @('core', 'diagnostic')
            payloadVersion = 1
            evidenceVersion = 1
            capabilities = @('exact-source-v1', 'diagnostic-containment-v1')
        }
    }

    It 'accepts the exact qualified release and exposes runner identity separately from candidate proof' {
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        $result = Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core
        $result.releaseId | Should -BeExactly 'fixture-release-1'
        $result.image | Should -BeExactly $script:image
        $result.manifestSha256 | Should -Match '^[0-9a-f]{64}$'
        $result.runnerInputDigest | Should -BeExactly $script:inputDigest
        $result.PSObject.Properties.Name | Should -Not -Contain 'sourceSnapshot'
    }

    It 'keeps the release unchanged when unrelated application content changes' {
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        $first = Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core
        [IO.File]::WriteAllText((Join-Path $script:candidate 'Application.cs'), '// unrelated candidate change')
        $second = Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core
        $second.releaseId | Should -BeExactly $first.releaseId
        $second.manifestSha256 | Should -BeExactly $first.manifestSha256
    }

    It 'rejects <Case> with an actionable compatibility error' -ForEach @(
        @{ Case = 'unapproved'; Field = 'approved'; Value = $false }
        @{ Case = 'revoked'; Field = 'revoked'; Value = $true }
        @{ Case = 'string approval'; Field = 'approved'; Value = 'true' }
        @{ Case = 'unknown schema'; Field = 'schemaVersion'; Value = 2 }
        @{ Case = 'string schema'; Field = 'schemaVersion'; Value = '1' }
        @{ Case = 'mutable image tag'; Field = 'image'; Value = 'example.invalid/runner:latest' }
        @{ Case = 'malformed digest'; Field = 'image'; Value = 'example.invalid/runner@sha256:abc' }
        @{ Case = 'different immutable image'; Field = 'image'; Value = ('example.invalid/runner@sha256:' + ('c' * 64)) }
        @{ Case = 'empty release'; Field = 'releaseId'; Value = '' }
        @{ Case = 'unknown mode'; Field = 'modes'; Value = @('arbitrary') }
        @{ Case = 'missing requested mode'; Field = 'modes'; Value = @('diagnostic') }
        @{ Case = 'unknown payload contract'; Field = 'payloadVersion'; Value = 2 }
        @{ Case = 'unknown evidence contract'; Field = 'evidenceVersion'; Value = 2 }
        @{ Case = 'missing source capability'; Field = 'capabilities'; Value = @() }
        @{ Case = 'unknown property'; Field = 'trustMe'; Value = $true }
        @{ Case = 'wrong execution inputs'; Field = 'runnerInputDigest'; Value = ('0' * 64) }
    ) {
        $script:document[$Field] = $Value
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        # ErrorId prevents a missing command from satisfying a negative test.
        { Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
    }

    It 'rejects missing required field <Field>' -ForEach @(
        @{ Field = 'schemaVersion' }, @{ Field = 'releaseId' }, @{ Field = 'image' },
        @{ Field = 'approved' }, @{ Field = 'revoked' }, @{ Field = 'runnerInputDigest' },
        @{ Field = 'modes' }, @{ Field = 'payloadVersion' }, @{ Field = 'evidenceVersion' }, @{ Field = 'capabilities' }
    ) {
        $script:document.Remove($Field)
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        { Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
    }

    It 'requires a newly qualified release after execution input <Path> changes' -ForEach @(
        @{ Path = 'infra/buildtest/runner/entrypoint.ps1' }
        @{ Path = 'infra/buildtest/runner/Dockerfile' }
        @{ Path = 'infra/buildtest/main.bicep' }
        @{ Path = 'scripts/repo/SourceSnapshot.psm1' }
        @{ Path = 'scripts/repo/Assert-TestResultContract.ps1' }
        @{ Path = 'scripts/repo/Invoke-BoundedCoreTests.ps1' }
        @{ Path = 'global.json' }
        @{ Path = 'Directory.Packages.props' }
    ) {
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        $target = Join-Path $script:candidate $Path
        [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        [IO.File]::WriteAllText($target, 'changed execution contract')
        { Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
    }

    It 'rejects diagnostic mode without containment capability even when the image is exact' {
        $script:document.capabilities = @('exact-source-v1')
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        { Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode diagnostic } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
    }

    It 'rejects candidate-contained and dot-segment paths' {
        $inside = Join-Path $script:candidate 'release.json'
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $inside -Encoding utf8NoBOM
        $disguised = Join-Path -Path $script:candidate -ChildPath '../' -AdditionalChildPath @([IO.Path]::GetFileName($script:candidate), 'release.json')
        foreach ($path in @($inside, $disguised)) {
            { Assert-RunnerReleaseCompatibility -ManifestPath $path -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
        }
    }

    It 'rejects a manifest reached through a directory link into the candidate' {
        $inside = Join-Path $script:candidate 'release.json'
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $inside -Encoding utf8NoBOM
        $link = Join-Path $script:root ('link-' + [guid]::NewGuid().ToString('N'))
        $kind = if ($IsWindows) {
            'Junction'
        }
        else {
            'SymbolicLink'
        }
        New-Item -ItemType $kind -Path $link -Target $script:candidate -ErrorAction Stop | Out-Null
        try {
            $linkedManifest = Join-Path $link 'release.json'
            { Assert-RunnerReleaseCompatibility -ManifestPath $linkedManifest -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
        }
        finally {
            [IO.Directory]::Delete($link)
        }
    }

    It 'rejects strict type or bound violation <Case>' -ForEach @(
        @{ Case = 'null approval'; Field = 'approved'; Value = $null }
        @{ Case = 'numeric revocation'; Field = 'revoked'; Value = 0 }
        @{ Case = 'floating version'; Field = 'schemaVersion'; Value = 1.5 }
        @{ Case = 'string payload'; Field = 'payloadVersion'; Value = '1' }
        @{ Case = 'boolean evidence'; Field = 'evidenceVersion'; Value = $true }
        @{ Case = 'release whitespace'; Field = 'releaseId'; Value = 'bad release' }
        @{ Case = 'release too long'; Field = 'releaseId'; Value = ('x' * 129) }
        @{ Case = 'image too long'; Field = 'image'; Value = (('x' * 513) + '@sha256:' + ('b' * 64)) }
        @{ Case = 'image without repository'; Field = 'image'; Value = ('sha256:' + ('b' * 64)) }
        @{ Case = 'image with tag and digest'; Field = 'image'; Value = ('example.invalid/runner:latest@sha256:' + ('b' * 64)) }
        @{ Case = 'uppercase digest'; Field = 'runnerInputDigest'; Value = ('A' * 64) }
        @{ Case = 'scalar modes'; Field = 'modes'; Value = 'core' }
        @{ Case = 'empty modes'; Field = 'modes'; Value = @() }
        @{ Case = 'duplicate modes'; Field = 'modes'; Value = @('core', 'core') }
        @{ Case = 'too many modes'; Field = 'modes'; Value = @('core', 'core', 'core', 'core', 'core', 'core', 'core') }
        @{ Case = 'wrong mode type'; Field = 'modes'; Value = @(1) }
        @{ Case = 'scalar capability'; Field = 'capabilities'; Value = 'exact-source-v1' }
        @{ Case = 'unknown capability'; Field = 'capabilities'; Value = @('exact-source-v1', 'magic-trust-v9') }
        @{ Case = 'duplicate capability'; Field = 'capabilities'; Value = @('exact-source-v1', 'exact-source-v1') }
    ) {
        $script:document[$Field] = $Value
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        { Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
    }

    It 'accepts diagnostic containment and hashes the exact manifest bytes afresh' {
        $json = $script:document | ConvertTo-Json -Depth 8
        [IO.File]::WriteAllText($script:manifestPath, $json)
        $first = Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode diagnostic
        $expectedHash = (Get-FileHash -LiteralPath $script:manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $first.manifestSha256 | Should -BeExactly $expectedHash
        $first.mode | Should -BeExactly 'diagnostic'
        $first.payloadVersion | Should -Be 1
        $first.evidenceVersion | Should -Be 1
        [IO.File]::WriteAllText($script:manifestPath, ($json + "`n "))
        $second = Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode diagnostic
        $second.manifestSha256 | Should -Not -Be $first.manifestSha256
        $script:document.revoked = $true
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        { Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode diagnostic } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
    }

    It 'accepts a sibling manifest with a candidate-name prefix and normalized external dot segments' {
        $sibling = $script:candidate + '-operator'
        [IO.Directory]::CreateDirectory($sibling) | Out-Null
        $path = Join-Path $sibling 'release.json'
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
        $dotted = Join-Path -Path $sibling -ChildPath '../' -AdditionalChildPath @([IO.Path]::GetFileName($sibling), 'release.json')
        (Assert-RunnerReleaseCompatibility -ManifestPath $dotted -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core).releaseId | Should -BeExactly 'fixture-release-1'
    }

    It 'rejects missing, relative, provider and directory manifest paths' -ForEach @(
        @{ Kind = 'missing' }, @{ Kind = 'relative' }, @{ Kind = 'provider' }, @{ Kind = 'directory' }
    ) {
        $path = switch ($Kind) {
            'missing' {
                Join-Path $script:root 'not-present.json'
            }
            'relative' {
                'operator-release.json'
            }
            'provider' {
                'FileSystem::/operator-release.json'
            }
            'directory' {
                $script:root
            }
        }
        { Assert-RunnerReleaseCompatibility -ManifestPath $path -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
    }

    It 'normalizes CRLF execution inputs but ignores excluded application, build output and fixture files' {
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        [IO.File]::WriteAllText((Join-Path $script:candidate 'infra/buildtest/runner/entrypoint.ps1'), "# synthetic runner`r`n")
        foreach ($relative in @('src/application/Example.cs', 'infra/buildtest/tests/Test-Fixture.ps1', 'infra/buildtest/runner/tests/Test-Fixture.ps1', 'tests/unit/bin/generated.props', 'tests/unit/obj/generated.targets', '.git/config')) {
            $path = Join-Path $script:candidate $relative
            [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
            [IO.File]::WriteAllText($path, 'unrelated')
        }
        (Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core).runnerInputDigest | Should -BeExactly $script:inputDigest
    }

    It 'rejects impacted policy mutation after qualification and accepts external requalification of the SAME image' {
        $policyPath = Join-Path $script:candidate 'scripts/repo/test-impacted.ps1'
        [IO.Directory]::CreateDirectory((Split-Path -Parent $policyPath)) | Out-Null
        [IO.File]::WriteAllText($policyPath, '# initial impacted policy')
        # Model the qualification tool against the current implementation, so the
        # pre-mutation positive is valid both before and after the scope repair.
        $loadedModule = Get-Module RunnerReleaseCompatibility
        $script:document.runnerInputDigest = & $loadedModule { param($root) Get-RunnerReleaseInputDigest -Root $root } $script:candidate
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        (Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core).image | Should -BeExactly $script:image
        [IO.File]::WriteAllText($policyPath, '# changed impacted policy')
        { Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core } | Should -Throw '*Execution inputs changed*' -ErrorId 'RunnerReleaseCompatibility*'
        $material = "botnexus-qualified-execution-inputs-v1`0" + (& $script:frame 'infra/buildtest/runner/entrypoint.ps1' "# synthetic runner`n") + (& $script:frame 'scripts/repo/test-impacted.ps1' '# changed impacted policy')
        $script:document.runnerInputDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($material))).ToLowerInvariant()
        $script:document.releaseId = 'fixture-requalified-policy'
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        (Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core).image | Should -BeExactly $script:image
    }

    It 'continues to exclude true repository fixtures: <Path>' -ForEach @(
        @{ Path = 'scripts/repo/Test-Fixture.ps1' }
        @{ Path = 'scripts/repo/Policy.Tests.ps1' }
    ) {
        $path = Join-Path $script:candidate $Path
        [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        [IO.File]::WriteAllText($path, '# initial fixture')
        Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core | Out-Null
        [IO.File]::WriteAllText($path, '# changed fixture')
        (Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core).runnerInputDigest | Should -BeExactly $script:inputDigest
    }

    It 'includes transitive execution or policy input <Path>' -ForEach @(
        @{ Path = 'scripts/repo/ValidationFreshness.psm1' }
        @{ Path = 'scripts/repo/Ensure-TesthostFirewallRules.ps1' }
        @{ Path = 'scripts/repo/FirewallLeaseProgram.ps1' }
        @{ Path = 'scripts/repo/CoreTestFailureDiagnostics.ps1' }
        @{ Path = 'scripts/repo/RunnerReleaseCompatibility.psm1' }
        @{ Path = '.config/dotnet-tools.json' }
        @{ Path = 'dotnet-tools.json' }
        @{ Path = 'NuGet.Config' }
        @{ Path = 'tests/Directory.Build.targets' }
        @{ Path = 'tests/unit/Example.Tests.csproj' }
        @{ Path = 'tests/dirs.proj' }
        @{ Path = 'src/dirs.proj' }
        @{ Path = 'tools/BotNexus.TestEventCollector/EventJournal.cs' }
        @{ Path = 'custom/Directory.Build.targets' }
    ) {
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        $path = Join-Path $script:candidate $Path
        [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
        [IO.File]::WriteAllText($path, 'changed policy')
        { Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
    }

    It 'rejects deleting or renaming the qualified execution file' -ForEach @(
        @{ Operation = 'delete' }, @{ Operation = 'rename' }
    ) {
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        $path = Join-Path $script:candidate 'infra/buildtest/runner/entrypoint.ps1'
        if ($Operation -eq 'rename') {
            Move-Item -LiteralPath $path -Destination (Join-Path (Split-Path -Parent $path) 'renamed.ps1')
        }
        else {
            Remove-Item -LiteralPath $path
        }
        { Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
    }

    It 'rejects linked candidate roots and selected execution directories' -ForEach @(
        @{ Kind = 'candidate' }, @{ Kind = 'input' }
    ) {
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        $link = Join-Path $script:root ('link-' + [guid]::NewGuid().ToString('N'))
        $target = $script:candidate
        $root = $link
        if ($Kind -eq 'input') {
            $link = Join-Path $script:candidate 'scripts'
            $target = Join-Path $script:root 'external-execution'
            [IO.Directory]::CreateDirectory($target) | Out-Null
            $root = $script:candidate
        }
        $type = if ($IsWindows) {
            'Junction'
        }
        else {
            'SymbolicLink'
        }
        New-Item -ItemType $type -Path $link -Target $target -ErrorAction Stop | Out-Null
        try {
            { Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $root -DeployedImage $script:image -Mode core } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
        }
        finally {
            [IO.Directory]::Delete($link)
        }
    }

    It 'rejects invalid UTF8 bytes without replacement decoding' {
        [IO.File]::WriteAllBytes($script:manifestPath, [byte[]]@(255, 254, 123, 0))
        { Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
    }

    It 'rejects malformed tokens in an otherwise complete document: <Case>' -ForEach @(
        @{ Case = 'trailing comma'; Replacement = '"schemaVersion": 1,'; Pattern = '"schemaVersion": 1' }
        @{ Case = 'duplicate key'; Replacement = '"schemaVersion": 1, "schemaVersion": 1'; Pattern = '"schemaVersion": 1' }
        @{ Case = 'escaped duplicate'; Replacement = '"schemaVersion": 1, "schema\u0056ersion": 1'; Pattern = '"schemaVersion": 1' }
        @{ Case = 'case collision'; Replacement = '"schemaVersion": 1, "SchemaVersion": 1'; Pattern = '"schemaVersion": 1' }
        @{ Case = 'numeric exponent'; Replacement = '"schemaVersion": 1e0'; Pattern = '"schemaVersion": 1' }
        @{ Case = 'decimal number'; Replacement = '"schemaVersion": 1.0'; Pattern = '"schemaVersion": 1' }
        @{ Case = 'comment'; Replacement = '"schemaVersion": /*comment*/1'; Pattern = '"schemaVersion": 1' }
    ) {
        $json = $script:document | ConvertTo-Json -Depth 8
        $json = $json.Replace($Pattern, $Replacement)
        $json | Should -Not -BeExactly ($script:document | ConvertTo-Json -Depth 8)
        [IO.File]::WriteAllText($script:manifestPath, $json)
        { Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
    }

    It 'rejects strict JSON root or token violation <Case>' -ForEach @(
        @{ Case = 'array root'; Text = '[]' }
        @{ Case = 'null root'; Text = 'null' }
        @{ Case = 'trailing comma'; Text = '{"schemaVersion":1}' }
        @{ Case = 'comment'; Text = '{/* comment */"schemaVersion":1}' }
        @{ Case = 'case-colliding keys'; Text = '{"schemaVersion":1,"SchemaVersion":1}' }
        @{ Case = 'escaped duplicate keys'; Text = '{"schemaVersion":1,"schema\u0056ersion":1}' }
        @{ Case = 'noncanonical integer'; Text = '{"schemaVersion":1.0}' }
        @{ Case = 'deep value'; Text = '{"schemaVersion":[[[[[[[[[1]]]]]]]]]}' }
    ) {
        [IO.File]::WriteAllText($script:manifestPath, $Text)
        { Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
    }

    It 'accepts a qualified multi-file input stream in ordinal path order and rejects subsequent deletion' {
        $extra = Join-Path $script:candidate 'global.json'
        [IO.File]::WriteAllText($extra, "{}`r`n")
        $material = "botnexus-qualified-execution-inputs-v1`0" + (& $script:frame 'global.json' "{}`n") + (& $script:frame 'infra/buildtest/runner/entrypoint.ps1' "# synthetic runner`n")
        $script:document.runnerInputDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($material))).ToLowerInvariant()
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        (Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core).runnerInputDigest | Should -BeExactly $script:document.runnerInputDigest
        Remove-Item -LiteralPath $extra
        { Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core } | Should -Throw '*Execution inputs changed*' -ErrorId 'RunnerReleaseCompatibility*'
    }

    It 'allows external requalification of compatible sender inputs against the SAME immutable image' {
        $path = Join-Path $script:candidate 'scripts/repo/SourceSnapshot.psm1'
        [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
        [IO.File]::WriteAllText($path, '# compatible sender change')
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        { Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core } | Should -Throw '*qualification*' -ErrorId 'RunnerReleaseCompatibility*'
        $material = "botnexus-qualified-execution-inputs-v1`0" + (& $script:frame 'infra/buildtest/runner/entrypoint.ps1' "# synthetic runner`n") + (& $script:frame 'scripts/repo/SourceSnapshot.psm1' '# compatible sender change')
        $script:document.runnerInputDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($material))).ToLowerInvariant()
        $script:document.releaseId = 'fixture-qualification-2'
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        (Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core).image | Should -BeExactly $script:image
    }

    It 'keeps ordinary test implementation changes outside qualification inputs' {
        $path = Join-Path $script:candidate 'tests/unit/Example.Tests/ExampleTests.cs'
        [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
        [IO.File]::WriteAllText($path, '// ordinary candidate test implementation')
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        (Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core).runnerInputDigest | Should -BeExactly $script:inputDigest
    }

    It 'rejects a manifest while another writer holds the file exclusively' {
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        $stream = [IO.File]::Open($script:manifestPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            { Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
        }
        finally {
            $stream.Dispose()
        }
    }

    It 'rejects unknown requested mode or a different provider repository' -ForEach @(
        @{ RequestedMode = 'CORE'; Image = ('example.invalid/runner@sha256:' + ('b' * 64)) }
        @{ RequestedMode = 'arbitrary'; Image = ('example.invalid/runner@sha256:' + ('b' * 64)) }
        @{ RequestedMode = 'core'; Image = ('other.invalid/runner@sha256:' + ('b' * 64)) }
        @{ RequestedMode = 'core'; Image = 'example.invalid/runner:latest' }
    ) {
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        { Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $Image -Mode $RequestedMode } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
    }

    It 'rejects an external linked ancestor even when its target is also outside the candidate' {
        $outside = Join-Path $script:root ('operator-' + [guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($outside) | Out-Null
        $script:document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outside 'release.json') -Encoding utf8NoBOM
        $link = Join-Path $script:root ('external-link-' + [guid]::NewGuid().ToString('N'))
        $type = if ($IsWindows) {
            'Junction'
        }
        else {
            'SymbolicLink'
        }
        New-Item -ItemType $type -Path $link -Target $outside -ErrorAction Stop | Out-Null
        try {
            $path = Join-Path $link 'release.json'
            { Assert-RunnerReleaseCompatibility -ManifestPath $path -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core } | Should -Throw '*Linked paths*' -ErrorId 'RunnerReleaseCompatibility*'
        }
        finally {
            [IO.Directory]::Delete($link)
        }
    }

    It 'rejects malformed, oversized and duplicate-key JSON' -ForEach @(
        @{ Text = '{ broken' }
        @{ Text = (' ' * 65537) }
        @{ Text = '{"schemaVersion":1,"schemaVersion":1}' }
    ) {
        [IO.File]::WriteAllText($script:manifestPath, $Text)
        { Assert-RunnerReleaseCompatibility -ManifestPath $script:manifestPath -CandidateRoot $script:candidate -DeployedImage $script:image -Mode core } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
    }
}
