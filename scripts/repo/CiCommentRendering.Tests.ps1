#Requires -Modules Pester
# Regression coverage for issue #2997: ci-pr-comment.ps1 threw
# "Index operation failed; the array index evaluated to null" when any check row
# in the statusCheckRollup had a null 'name' (StatusContext entries expose
# 'context' instead). The throw aborted the entire comment, and because the
# maintenance loop treats commenting as best-effort the failure was silent.
#
# These tests exercise the pure rendering seam only -- no GitHub access.

BeforeAll {
    . (Join-Path $PSScriptRoot 'CiCommentRendering.ps1')

    # CheckRun shape: carries 'name'.
    function New-CheckRunRow {
        param([string]$Name, [string]$Status)
        [pscustomobject]@{ __typename = 'CheckRun'; name = $Name; status = $Status }
    }

    # StatusContext shape: carries 'context', and NO 'name' property at all.
    function New-StatusContextRow {
        param([string]$Context, [string]$Status)
        [pscustomobject]@{ __typename = 'StatusContext'; context = $Context; status = $Status }
    }

    # A row that has neither a usable name nor a context.
    function New-NamelessRow {
        param([string]$Status)
        [pscustomobject]@{ __typename = 'CheckRun'; name = $null; status = $Status }
    }
}

Describe 'Get-CheckDisplayKey - the single normalisation (AC4)' {

    It 'returns the name for a CheckRun row' {
        Get-CheckDisplayKey -Row (New-CheckRunRow -Name 'core-tests' -Status 'pass') |
            Should -Be 'core-tests'
    }

    It 'falls back to context when the row has no name property' {
        $row = New-StatusContextRow -Context 'legacy-ci' -Status 'pass'
        $row.PSObject.Properties.Name | Should -Not -Contain 'name'
        Get-CheckDisplayKey -Row $row | Should -Be 'legacy-ci'
    }

    It 'falls back to context when name is present but null' {
        $row = [pscustomobject]@{ name = $null; context = 'legacy-ci'; status = 'pass' }
        Get-CheckDisplayKey -Row $row | Should -Be 'legacy-ci'
    }

    It 'returns null for a row with neither name nor context' {
        Get-CheckDisplayKey -Row (New-NamelessRow -Status 'pass') | Should -BeNullOrEmpty
    }

    It 'treats a whitespace-only name as unusable' {
        Get-CheckDisplayKey -Row ([pscustomobject]@{ name = '   '; status = 'pass' }) |
            Should -BeNullOrEmpty
    }

    It 'returns null for a null row' {
        Get-CheckDisplayKey -Row $null | Should -BeNullOrEmpty
    }
}

Describe 'New-CiCheckTableBody - null-name rows never abort the render' {

    It 'AC1: renders a table when a row has a null name' {
        $rows = @(
            New-CheckRunRow -Name 'core-tests' -Status 'pass'
            New-NamelessRow -Status 'pass'
        )
        { New-CiCheckTableBody -CheckRows $rows } | Should -Not -Throw
        New-CiCheckTableBody -CheckRows $rows | Should -Match '\| core-tests \| pass \|'
    }

    It 'AC2: renders a StatusContext row under its context value' {
        $body = New-CiCheckTableBody -CheckRows @(New-StatusContextRow -Context 'legacy-ci' -Status 'fail')
        $body | Should -Match '\| legacy-ci \| FAIL \|'
    }

    It 'AC3: skips a row with neither name nor context and still renders the rest' {
        $rows = @(
            New-CheckRunRow -Name 'CodeQL' -Status 'pass'
            New-NamelessRow -Status 'fail'
            New-StatusContextRow -Context 'legacy-ci' -Status 'pending'
        )
        $body = New-CiCheckTableBody -CheckRows $rows
        $body | Should -Match '\| CodeQL \| pass \|'
        $body | Should -Match '\| legacy-ci \| pending \|'
        # The nameless row contributes no row of its own.
        @($body -split "`n" | Where-Object { $_ -match '^\|\s*\|' }).Count | Should -Be 0
    }

    It 'a row with a null status renders as unknown rather than throwing' {
        $body = New-CiCheckTableBody -CheckRows @(New-CheckRunRow -Name 'weird' -Status $null)
        $body | Should -Match '\| weird \| unknown \|'
    }

    It 'still marks a known check absent from the rows as skipped' {
        $body = New-CiCheckTableBody -CheckRows @(New-CheckRunRow -Name 'core-tests' -Status 'pass')
        $body | Should -Match '\| CodeQL \| skipped \|'
    }
}

Describe 'New-CiHealthCheckBody - AC5 end-to-end mixed rollup' {

    BeforeAll {
        $script:mixed = @(
            New-CheckRunRow -Name 'core-tests' -Status 'pass'
            New-StatusContextRow -Context 'legacy-ci' -Status 'pass'
            New-NamelessRow -Status 'pass'
        )
    }

    It 'emits a body without throwing the index-operation failure' {
        $err = $null
        try {
            $body = New-CiHealthCheckBody -PR 2997 -CheckRows $script:mixed -Branch 'fix/x' `
                -Actions @('none') -Blockers @('None') -NowUtc '2026-01-01 00:00 UTC'
        } catch {
            $err = $_
        }
        $err | Should -BeNullOrEmpty
        $body | Should -Not -BeNullOrEmpty
        $body | Should -Not -Match 'Index operation failed'
    }

    It 'contains rows for the check run and the status context' {
        $body = New-CiHealthCheckBody -PR 2997 -CheckRows $script:mixed -Branch 'fix/x' `
            -Actions @('none') -Blockers @('None') -NowUtc '2026-01-01 00:00 UTC'
        $body | Should -Match '\| core-tests \| pass \|'
        $body | Should -Match '\| legacy-ci \| pass \|'
    }

    It 'keeps the marker so the comment can still be found for patching' {
        $body = New-CiHealthCheckBody -PR 2997 -CheckRows $script:mixed `
            -Actions @('none') -Blockers @('None') -NowUtc '2026-01-01 00:00 UTC'
        $body | Should -Match '<!-- farnsworth:ci-monitor-2997 -->'
    }
}

Describe 'AC4 - exactly one place computes a display key' {

    BeforeAll {
        $script:RendererPath = Join-Path $PSScriptRoot 'CiCommentRendering.ps1'
        $script:CommentPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'ci-pr-comment.ps1'
    }

    It 'ci-pr-comment.ps1 and the renderer never index a row by .name directly' {
        $files = @($script:RendererPath, $script:CommentPath)
        # Non-vacuity: both files must exist and be non-empty.
        foreach ($f in $files) { (Get-Item $f).Length | Should -BeGreaterThan 0 }

        $offenders = foreach ($f in $files) {
            Get-Content $f | Where-Object { $_ -match '\$\w+\[\s*\$row\.name' -or $_ -match '\$checkMap\[\s*\$\w+\.name' }
        }
        @($offenders).Count | Should -Be 0
    }

    # Without this, the renderer tests above would stay green if someone
    # re-inlined the table rendering back into ci-pr-comment.ps1 -- the tests
    # would then be exercising a seam the production script no longer uses.
    It 'ci-pr-comment.ps1 actually consumes the shared renderer' {
        $text = Get-Content $script:CommentPath -Raw
        $text | Should -Match 'CiCommentRendering\.ps1'
        $text | Should -Match 'New-CiHealthCheckBody'
    }

    It 'ci-pr-comment.ps1 does not re-declare its own rendering helpers' {
        $text = Get-Content $script:CommentPath -Raw
        $text | Should -Not -Match '(?m)^\s*\$knownOrder\s*='
        $text | Should -Not -Match '(?m)^\s*\$statusIcon\s*='
        $text | Should -Not -Match '(?m)^\s*\$checkMap\s*='
    }

    It 'Get-CheckDisplayKey is defined exactly once in the renderer' {
        $defs = @(Get-Content $script:RendererPath | Where-Object { $_ -match '^\s*function\s+Get-CheckDisplayKey\b' })
        $defs.Count | Should -Be 1
    }
}
