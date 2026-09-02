#Requires -Modules Pester
# Regression coverage for issue #3788: a remote gate run whose BUILD failed terminated with
# "The property 'Count' cannot be found on this object" instead of the script's own
# "Azure validation failed. ..." verdict, leaving the operator with exit code 1 and no statement
# of what the gate found.
#
# Root cause (verified by replaying the shipped tail, not assumed). The issue hypothesised a
# `.Count` dereference on `$result.tests`. It is not: the throwing site is the EXISTING
# `$projectCosts` guard on line ~227, and the guard is correct in shape but broken in binding.
#
#     $projectCosts = if (...) { @($result.projectCosts) } else { @() }
#
# `@()` and `@(<empty array>)` are both an EMPTY pipeline result. An `if` statement used as an
# expression yields its branch's pipeline output, and an empty pipeline assigns $null - the
# array subexpression inside the branch is discarded on the way out. So on a build-failed run,
# where the runner emits `"projectCosts": []`, `$projectCosts` is $null, and `$projectCosts.Count`
# throws under `Set-StrictMode -Version Latest` on the very next line. `"tests": null` is a
# red herring: it is simply the most visible null in the same result.json.
#
# The fix wraps the whole `if` in an array subexpression - `@(if (...) { ... } else { ... })` -
# which forces the empty pipeline back to an empty array. That is the same PSObject.Properties
# null-guard idiom, repaired, not a new helper (AC2).
#
# These tests execute the REAL shipped tail region of Invoke-AzureBuildTest.ps1, extracted from
# the file by source markers. Re-implementing the logic here would prove nothing about the script
# an operator actually runs.
BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Invoke-AzureBuildTest.ps1'

    # Extract the verdict tail verbatim: from the #3305 runner-timeout guard through the script's
    # own failure `throw`. Anchoring on source text rather than line numbers keeps this honest if
    # the file moves around it, and fails loudly rather than silently testing nothing.
    function Get-VerdictTailScriptBlock {
        $lines = Get-Content -LiteralPath $script:ScriptPath
        $start = ($lines | Select-String -SimpleMatch '$runnerTimeout = if ($result' | Select-Object -First 1).LineNumber
        $end = ($lines | Select-String -SimpleMatch 'throw "Azure validation failed.' | Select-Object -First 1).LineNumber
        if (-not $start -or -not $end -or $end -le $start) {
            throw "Could not locate the verdict tail in $script:ScriptPath - the markers moved and this test would otherwise pass vacuously."
        }
        # $lines is 0-based while LineNumber is 1-based, so index $end is the line AFTER the
        # throw - the brace closing the `if` block the throw lives inside. The slice is therefore
        # already balanced and must not have another brace appended.
        $body = $lines[($start - 1)..$end] -join [Environment]::NewLine
        [scriptblock]::Create(@'
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
param_placeholder
'@.Replace('param_placeholder', $body))
    }

    # Runs the extracted tail against a caller-supplied result contract and returns what the
    # operator would actually see: the terminating message, or $null when the tail completed.
    function Invoke-VerdictTail {
        param(
            [Parameter(Mandatory)][string]$ResultJson,
            [string]$Mode = 'core'
        )
        $outputPath = Join-Path ([IO.Path]::GetTempPath()) ("bn3788-" + [Guid]::NewGuid().ToString('N').Substring(0, 12))
        New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
        try {
            Set-Content -LiteralPath (Join-Path $outputPath 'result.json') -Value $ResultJson -Encoding utf8NoBOM
            $result = $ResultJson | ConvertFrom-Json
            $tail = Get-VerdictTailScriptBlock

            # The tail's remaining free variables, bound to the shape a real build-failed run has.
            # KeepRemoteArtifacts short-circuits the `az blob delete` calls; nothing else in the
            # region touches Azure.
            $vars = @{
                result = $result
                Mode = $Mode
                OutputPath = $outputPath
                status = [pscustomobject]@{ properties = [pscustomobject]@{ status = 'Succeeded' } }
                budgetMinutes = 30
                timedOut = $false
                KeepRemoteArtifacts = $true
            }
            $ps = [powershell]::Create()
            try {
                $ps.AddScript($tail.ToString()) | Out-Null
                foreach ($k in $vars.Keys) { $ps.Runspace.SessionStateProxy.SetVariable($k, $vars[$k]) }
                # The tail is EXPECTED to terminate via its own throw on a failing contract, so the
                # exception must not cost us the console output produced before it - that output is
                # exactly what AC4 asserts on.
                $host_out = @()
                $terminating = $null
                try { $host_out = $ps.Invoke() }
                catch { $terminating = $_.Exception.InnerException?.Message ?? $_.Exception.Message }
                if (-not $terminating -and $ps.Streams.Error.Count -gt 0) { $terminating = $ps.Streams.Error[0].Exception.Message }
                [pscustomobject]@{
                    Message = $terminating
                    Output = @($host_out | ForEach-Object { "$_" })
                    Information = @($ps.Streams.Information | ForEach-Object { "$_" })
                    ArtifactPath = $outputPath
                }
            }
            finally { $ps.Dispose() }
        }
        finally { Remove-Item -LiteralPath $outputPath -Recurse -Force -ErrorAction SilentlyContinue }
    }

    # The exact contract the reproducer run 20260902171009-8bd9c29d wrote: build failed, test
    # phase skipped, so `tests` is null and `projectCosts` is an empty array.
    $script:BuildFailedResult = '{ "tests": null, "projectCosts": [], "mode": "core", "exitCode": 1, "timings": { "build": { "status": "failed", "seconds": 241.41 }, "test": { "status": "skipped", "seconds": 0.0 } } }'
}

Describe 'Invoke-AzureBuildTest verdict on a build-failed contract (#3788)' {
    It 'reports its own "Azure validation failed" verdict naming the status and artifact path, not a Count property error (AC1)' {
        $run = Invoke-VerdictTail -ResultJson $script:BuildFailedResult

        $run.Message | Should -Not -BeNullOrEmpty -Because 'a build-failed run must still terminate non-zero via the script''s own throw'
        $run.Message | Should -Not -BeLike "*The property 'Count' cannot be found*" -Because 'that is the #3788 defect: an unhandled shape masquerading as a tooling breakage'
        $run.Message | Should -BeLike 'Azure validation failed.*'
        $run.Message | Should -BeLike '*Execution status: Succeeded*' -Because 'AC1 requires the execution status to be named'
        $run.Message | Should -BeLike "*Artifacts: $($run.ArtifactPath)*" -Because 'AC1 requires the artifact directory to be named so the operator can read build.log'
    }

    It 'behaves identically for -Mode core and -Mode full (AC5)' {
        foreach ($mode in @('core', 'full')) {
            $run = Invoke-VerdictTail -ResultJson $script:BuildFailedResult -Mode $mode
            $run.Message | Should -BeLike 'Azure validation failed.*' -Because "-Mode $mode must reach the same verdict"
            $run.Message | Should -Not -BeLike "*The property 'Count' cannot be found*"
        }
    }

    It 'still reports per-project costs when the test phase ran and populated them - no regression to the green path (AC4)' {
        $populated = '{ "tests": { "total": 13088, "isComplete": true }, "projectCosts": [ { "project": "botnexus.gateway.tests", "seconds": 240.0 } ], "mode": "core", "exitCode": 1 }'
        $run = Invoke-VerdictTail -ResultJson $populated

        ($run.Output + $run.Information) -join ' ' | Should -BeLike '*botnexus.gateway.tests*' -Because '#3314 cost reporting must survive the #3788 fix'
        $run.Message | Should -BeLike 'Azure validation failed.*'
    }
}
