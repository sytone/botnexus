#Requires -Modules Pester
# Regression coverage for issue #3115: Invoke-AzureBuildTest.ps1 downloaded run artifacts into
# artifacts/azure-buildtest/<runId>/<runId>/ while advertising artifacts/azure-buildtest/<runId>/
# on success, so the result contract did not resolve at the path the script itself printed.
#
# Root cause (verified in source, not assumed): `az storage blob download-batch` reproduces the
# full blob NAME under --destination. The artifact blobs are named "<runId>/result.json", and
# --destination was $OutputPath, which ALREADY ended in the run id. The prefix was applied twice.
#
# These tests exercise the real filesystem against a fake blob download - the thing under test is
# the placement of files on disk, so mocking the filesystem would prove nothing.

BeforeAll {
    $script:ModulePath = Join-Path $PSScriptRoot 'AzureBuildTestArtifacts.psm1'
    Import-Module $script:ModulePath -Force

    $script:ScriptPath = Join-Path $PSScriptRoot 'Invoke-AzureBuildTest.ps1'

    $script:Scratch = Join-Path ([IO.Path]::GetTempPath()) ("bn3115-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $script:Scratch -Force | Out-Null

    # Reproduces what `az storage blob download-batch --destination <root> --pattern "<runId>/*"`
    # actually writes: every blob laid down under its own full name, run-id prefix included.
    function New-DownloadedBlobTree {
        param([Parameter(Mandatory)][string]$StagingRoot, [Parameter(Mandatory)][string]$RunId)
        $prefixed = Join-Path $StagingRoot $RunId
        New-Item -ItemType Directory -Path (Join-Path $prefixed 'test-results') -Force | Out-Null
        Set-Content -Path (Join-Path $prefixed 'result.json') -Value '{"exitCode":0,"tests":{"total":13,"isComplete":true}}' -Encoding utf8NoBOM
        Set-Content -Path (Join-Path $prefixed 'test-result.json') -Value '{}' -Encoding utf8NoBOM
        Set-Content -Path (Join-Path $prefixed 'build.log') -Value 'build ok' -Encoding utf8NoBOM
        Set-Content -Path (Join-Path $prefixed 'test-results/Domain.Tests.trx') -Value '<TestRun />' -Encoding utf8NoBOM
        return $prefixed
    }

    function New-Case {
        param([string]$RunId = '20260814120000-abcd1234')
        $case = Join-Path $script:Scratch ("case-" + [Guid]::NewGuid().ToString('N'))
        $staging = Join-Path $case 'staging'
        $destination = Join-Path $case "artifacts/azure-buildtest/$RunId"
        New-Item -ItemType Directory -Path $staging -Force | Out-Null
        New-DownloadedBlobTree -StagingRoot $staging -RunId $RunId | Out-Null
        [pscustomobject]@{ RunId = $RunId; Staging = $staging; Destination = $destination }
    }
}

AfterAll {
    if (Test-Path $script:Scratch) { Remove-Item $script:Scratch -Recurse -Force -ErrorAction SilentlyContinue }
}

Describe 'Move-AzureBuildTestArtifacts' {
    It 'places result.json directly in the run directory and it parses as JSON (AC1)' {
        $case = New-Case
        $returned = Move-AzureBuildTestArtifacts -StagingRoot $case.Staging -RunId $case.RunId -Destination $case.Destination

        $contract = Join-Path $case.Destination 'result.json'
        Test-Path -LiteralPath $contract -PathType Leaf | Should -BeTrue
        { Get-Content -LiteralPath $contract -Raw | ConvertFrom-Json } | Should -Not -Throw
        (Get-Content -LiteralPath $contract -Raw | ConvertFrom-Json).exitCode | Should -Be 0
        $returned | Should -Be $case.Destination
    }

    It 'does not create a duplicated runId-inside-runId directory (AC2, AC4 mutation target)' {
        $case = New-Case
        Move-AzureBuildTestArtifacts -StagingRoot $case.Staging -RunId $case.RunId -Destination $case.Destination | Out-Null

        # This is the assertion that reddens if the run-id prefix is ever applied twice again.
        Test-Path -LiteralPath (Join-Path $case.Destination $case.RunId) | Should -BeFalse
        @(Get-ChildItem -LiteralPath $case.Destination -Recurse -Filter 'result.json').Count | Should -Be 1
    }

    It 'returns the directory that directly contains result.json, so the printed path cannot drift (AC3)' {
        $case = New-Case
        $reported = Move-AzureBuildTestArtifacts -StagingRoot $case.Staging -RunId $case.RunId -Destination $case.Destination

        # The success line prints $OutputPath, which is reassigned from this return value.
        Test-Path -LiteralPath (Join-Path $reported 'result.json') -PathType Leaf | Should -BeTrue
    }

    It 'keeps test-results/*.trx reachable under the same run directory (AC5)' {
        $case = New-Case
        $reported = Move-AzureBuildTestArtifacts -StagingRoot $case.Staging -RunId $case.RunId -Destination $case.Destination

        @(Get-ChildItem -Path (Join-Path $reported 'test-results') -Filter '*.trx').Count | Should -Be 1
        Test-Path -LiteralPath (Join-Path $reported 'test-results/Domain.Tests.trx') -PathType Leaf | Should -BeTrue
    }

    It 'is a no-op-safe passthrough when the blob prefix is absent' {
        # If the prefix is ever dropped upstream, flattening must not empty the run directory.
        $case = New-Case
        $flat = Join-Path $script:Scratch ("flat-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $flat -Force | Out-Null
        Set-Content -Path (Join-Path $flat 'result.json') -Value '{"exitCode":0}' -Encoding utf8NoBOM

        $reported = Move-AzureBuildTestArtifacts -StagingRoot $flat -RunId $case.RunId -Destination $case.Destination
        Test-Path -LiteralPath (Join-Path $reported 'result.json') -PathType Leaf | Should -BeTrue
    }
}

Describe 'Invoke-AzureBuildTest.ps1 artifact wiring' {
    It 'downloads into a staging root rather than the advertised output directory (AC3)' {
        $source = Get-Content -LiteralPath $script:ScriptPath -Raw
        $source | Should -Match '--destination \$downloadStaging'
        $source | Should -Not -Match '--destination \$OutputPath'
    }

    It 'reports the same variable it flattened the artifacts into (AC3)' {
        $source = Get-Content -LiteralPath $script:ScriptPath -Raw
        $source | Should -Match '\$OutputPath = Move-AzureBuildTestArtifacts'
        $source | Should -Match 'Azure validation passed\. Artifacts: \$OutputPath'
    }

    It 'reads the result contract non-recursively so a nested copy cannot be accepted (AC2)' {
        $source = Get-Content -LiteralPath $script:ScriptPath -Raw
        $source | Should -Not -Match 'Filter result\.json -Recurse'
    }
}

# ---------------------------------------------------------------------------------------------
# Issue #3805: a gate run whose BUILD phase fails destroyed its own diagnosis.
#
# Two independent defects, both only reachable on the failure path:
#
#   1. `$x = if ($c) { @($result.projectCosts) } else { @() }` UNROLLS to a bare $null when the
#      JSON property is null, and `.Count` on $null throws under `Set-StrictMode -Version Latest`.
#      A build failure skips the test phase, so `result.json` carries `"tests": null` and
#      `"projectCosts": null` - i.e. the null shape is the NORMAL shape of a build failure, which
#      is why the secondary PowerShell error always masked the real cause.
#   2. The `finally` removed $tempRoot unconditionally, and the downloaded artifacts were still
#      inside it, so the throw deleted the only local copy of result.json and build.log while the
#      remote blobs had already been deleted (no -KeepRemoteArtifacts).
#
# These tests exercise the real helpers and the real filesystem. The null-collection case is
# asserted directly, per AC3, so it cannot regress silently.
# ---------------------------------------------------------------------------------------------

Describe 'ConvertTo-CountableArray (#3805 AC1, AC3)' {
    It 'returns a countable empty array for a JSON null property - the skipped-test-phase shape' {
        # This is the exact expression that threw in production, with the exact input.
        $result = '{"exitCode":1,"tests":null,"projectCosts":null}' | ConvertFrom-Json
        $costs = ConvertTo-CountableArray $result.projectCosts

        # An empty array IS "empty" - the assertion that matters is that it is not $NULL and that
        # .Count is reachable, which is the exact pair the production line needed and did not get.
        $null -ne $costs | Should -BeTrue -Because 'a null-collapsing result is what threw'
        $costs -is [array] | Should -BeTrue
        { $costs.Count } | Should -Not -Throw
        $costs.Count | Should -Be 0
    }

    It 'proves the original idiom really does collapse to $null, so this test is not vacuous' {
        # Non-vacuity: without this, the fix above could be trivially satisfied by any value.
        $result = '{"projectCosts":null}' | ConvertFrom-Json
        $collapsed = if ($result.PSObject.Properties['projectCosts']) { @($result.projectCosts) } else { @() }

        $null -eq $collapsed | Should -BeTrue -Because 'the if-statement pipeline unrolls @($null)'
        { Set-StrictMode -Version Latest; $collapsed.Count } | Should -Throw -ExpectedMessage "*'Count'*"
    }

    It 'returns a countable empty array when the property is absent entirely' {
        $result = '{"exitCode":1}' | ConvertFrom-Json
        $value = if ($result.PSObject.Properties['projectCosts']) { $result.projectCosts } else { $null }
        (ConvertTo-CountableArray $value).Count | Should -Be 0
    }

    It 'returns a countable empty array for an EMPTY JSON array - the shape observed on the live gate' {
        # Measured, not assumed: run 20260903153054-5b81c25c (-Mode core, build failed on an
        # inherited CS0535) produced `"projectCosts": []` and `"tests": null`, and reproduced the
        # #3805 'Count' throw. An EMPTY array unrolls to $null on assignment exactly as @($null)
        # does, so the null-property case alone would not have covered the observed failure.
        $result = '{"exitCode":1,"projectCosts":[],"tests":null,"timeout":null}' | ConvertFrom-Json
        $costs = ConvertTo-CountableArray $result.projectCosts

        $null -ne $costs | Should -BeTrue -Because 'this is the exact shape the live gate emitted'
        { $costs.Count } | Should -Not -Throw
        $costs.Count | Should -Be 0
    }

    It 'proves the empty-array shape also collapses under the original idiom (non-vacuity)' {
        $result = '{"projectCosts":[]}' | ConvertFrom-Json
        $collapsed = if ($result.PSObject.Properties['projectCosts']) { @($result.projectCosts) } else { @() }

        $null -eq $collapsed | Should -BeTrue
        { Set-StrictMode -Version Latest; $collapsed.Count } | Should -Throw -ExpectedMessage "*'Count'*"
    }

    It 'preserves a populated collection unchanged (AC4 - success path is not altered)' {
        $result = '{"projectCosts":[{"project":"A","seconds":9.5},{"project":"B","seconds":2}]}' | ConvertFrom-Json
        $costs = ConvertTo-CountableArray $result.projectCosts

        $costs.Count | Should -Be 2
        $costs[0].project | Should -Be 'A'
        $costs[1].seconds | Should -Be 2
    }

    It 'wraps a bare scalar into a one-element array' {
        (ConvertTo-CountableArray 'solo').Count | Should -Be 1
    }

    It 'drops null elements rather than counting an absent datum' {
        (ConvertTo-CountableArray @('a', $null, 'b')).Count | Should -Be 2
    }
}

Describe 'Save-AzureBuildTestFailureArtifacts (#3805 AC2)' {
    It 'lands result.json and build.log on disk when the run failed and -OutputPath was not supplied' {
        # -OutputPath unsupplied means $OutputPath defaulted to artifacts/azure-buildtest/<runId>;
        # AC2 requires the files to be THERE, not in the temp staging root that gets deleted.
        $case = New-Case
        $retained = Save-AzureBuildTestFailureArtifacts -StagingRoot $case.Staging -RunId $case.RunId -Destination $case.Destination

        $retained | Should -Be $case.Destination
        Test-Path -LiteralPath (Join-Path $retained 'result.json') -PathType Leaf | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $retained 'build.log') -PathType Leaf | Should -BeTrue
    }

    It 'survives the temp staging root being deleted afterwards, as the finally block does' {
        $case = New-Case
        $retained = Save-AzureBuildTestFailureArtifacts -StagingRoot $case.Staging -RunId $case.RunId -Destination $case.Destination
        # Delete ONLY the staging root - that is what the script's finally removes. The destination
        # is deliberately outside it, which is the whole point of retaining before cleanup.
        Remove-Item -LiteralPath $case.Staging -Recurse -Force -ErrorAction SilentlyContinue

        Test-Path -LiteralPath $case.Staging | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $retained 'result.json') -PathType Leaf | Should -BeTrue
    }

    It 'returns $null when nothing was downloaded, so the caller cannot print an empty path' {
        $empty = Join-Path $script:Scratch ("empty-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $empty -Force | Out-Null
        $dest = Join-Path $script:Scratch ("dest-" + [Guid]::NewGuid().ToString('N'))

        Save-AzureBuildTestFailureArtifacts -StagingRoot $empty -RunId 'r1' -Destination $dest | Should -BeNullOrEmpty
    }

    It 'returns $null when the staging root never existed (download failed before writing)' {
        $missing = Join-Path $script:Scratch ("never-" + [Guid]::NewGuid().ToString('N'))
        $dest = Join-Path $script:Scratch ("dest2-" + [Guid]::NewGuid().ToString('N'))

        Save-AzureBuildTestFailureArtifacts -StagingRoot $missing -RunId 'r1' -Destination $dest | Should -BeNullOrEmpty
    }
}

Describe 'Invoke-AzureBuildTest.ps1 failure-path wiring (#3805)' {
    BeforeAll { $script:Source = Get-Content -LiteralPath $script:ScriptPath -Raw }

    It 'no longer counts a possibly-null JSON collection directly (AC1)' {
        # The literal defect. If this idiom returns, the failure path throws on .Count again.
        $script:Source | Should -Not -Match '\$projectCosts = if \('
        $script:Source | Should -Match 'ConvertTo-CountableArray'
    }

    It 'retains downloaded artifacts in the finally block before deleting the temp root (AC2)' {
        $script:Source | Should -Match 'Save-AzureBuildTestFailureArtifacts'

        $retainAt = $script:Source.IndexOf('Save-AzureBuildTestFailureArtifacts -StagingRoot')
        $deleteAt = $script:Source.IndexOf('if (Test-Path $tempRoot) { Remove-Item $tempRoot')
        $retainAt | Should -BeGreaterThan 0
        $deleteAt | Should -BeGreaterThan 0
        $retainAt | Should -BeLessThan $deleteAt -Because 'retention after deletion retains nothing'
    }

    It 'declares the staging path and the placement flag outside the try, so finally can see them (AC2)' {
        $script:Source | Should -Match '(?m)^\$downloadStaging = Join-Path \$tempRoot'
        $script:Source | Should -Match '(?m)^\$artifactsPlaced = \$false'
        $script:Source | Should -Match '\$artifactsPlaced = \$true'
    }

    It 'names the execution status and the artifact path in the thrown failure message (AC1)' {
        $script:Source | Should -Match 'throw "Azure validation failed\. Execution status:'
        $script:Source | Should -Match 'Artifacts: \$OutputPath"'
    }

    It 'distinguishes a non-reporting test phase from a test failure in the thrown message (AC1)' {
        $script:Source | Should -Match 'tests: null'
    }

    It 'retains only when the normal placement did not happen, so a green run is unchanged (AC4)' {
        $script:Source | Should -Match 'if \(-not \$artifactsPlaced\)'
    }

    It 'never lets the retention diagnostic replace the original failure (AC1)' {
        # A throw from the finally block would substitute itself for the real error - the exact
        # class of bug this issue is about, reintroduced one layer out.
        $script:Source | Should -Match 'Could not retain downloaded artifacts'
    }
}
