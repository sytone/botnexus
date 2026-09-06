#Requires -Modules Pester

BeforeAll {
    . (Join-Path $PSScriptRoot 'Set-BrokenPullRequestsDraft.ps1')

    function New-Check {
        param([string]$Name, [string]$Conclusion, [string]$Status = 'COMPLETED')
        [pscustomobject]@{ name = $Name; conclusion = $Conclusion; status = $Status }
    }

    function New-Pr {
        param(
            [int]$Number = 42,
            [bool]$IsDraft = $false,
            [string]$Mergeable = 'MERGEABLE',
            [string]$MergeStateStatus = 'CLEAN',
            [object[]]$Checks = @()
        )
        [pscustomobject]@{
            number = $Number
            title = "PR $Number"
            isDraft = $IsDraft
            mergeable = $Mergeable
            mergeStateStatus = $MergeStateStatus
            statusCheckRollup = $Checks
        }
    }
}

Describe 'Get-DraftReason' {
    It 'quarantines a confirmed merge conflict' {
        Get-DraftReason -PullRequest (New-Pr -Mergeable 'CONFLICTING' -MergeStateStatus 'DIRTY') |
            Should -Be 'merge-conflict'
    }

    It 'quarantines terminal build failures' -ForEach @('FAILURE', 'ERROR', 'ACTION_REQUIRED', 'STARTUP_FAILURE') {
        Get-DraftReason -PullRequest (New-Pr -Checks @(New-Check -Name 'core-tests' -Conclusion $_)) |
            Should -Be 'failed-check:core-tests'
    }

    It 'quarantines cancelled, timed-out, and stale checks' -ForEach @('CANCELLED', 'TIMED_OUT', 'STALE') {
        Get-DraftReason -PullRequest (New-Pr -Checks @(New-Check -Name 'core-tests' -Conclusion $_)) |
            Should -Be 'stuck-check:core-tests'
    }

    It 'does not quarantine checks still in progress' -ForEach @('PENDING', 'QUEUED', 'IN_PROGRESS', 'WAITING', 'REQUESTED', 'EXPECTED') {
        Get-DraftReason -PullRequest (New-Pr -Checks @(New-Check -Name 'core-tests' -Conclusion '' -Status $_)) |
            Should -BeNullOrEmpty
    }

    It 'does not quarantine an unknown merge or check state' {
        Get-DraftReason -PullRequest (New-Pr -Mergeable 'UNKNOWN' -MergeStateStatus 'UNKNOWN' -Checks @(
            New-Check -Name 'core-tests' -Conclusion '' -Status 'COMPLETED'
        )) | Should -BeNullOrEmpty
    }

    It 'does not quarantine when the Sensitive File Guard is the sole failure' {
        Get-DraftReason -PullRequest (New-Pr -Checks @(
            New-Check -Name 'Security: Sensitive File Guard' -Conclusion 'FAILURE'
            New-Check -Name 'core-tests' -Conclusion 'SUCCESS'
            New-Check -Name 'full-tests' -Conclusion 'SKIPPED'
        )) | Should -BeNullOrEmpty
    }

    It 'still quarantines a real failure beside the Sensitive File Guard' {
        Get-DraftReason -PullRequest (New-Pr -Checks @(
            New-Check -Name 'Sensitive File Guard' -Conclusion 'FAILURE'
            New-Check -Name 'core-tests' -Conclusion 'FAILURE'
        )) | Should -Be 'failed-check:core-tests'
    }
}

Describe 'Invoke-BrokenPullRequestDrafting' {
    BeforeEach {
        Mock Get-OpenPullRequests { @() }
        Mock Set-PullRequestDraft {}
    }

    It 'never mutates an already-draft pull request' {
        Mock Get-OpenPullRequests { @(New-Pr -IsDraft $true -Mergeable 'CONFLICTING') }
        $result = @(Invoke-BrokenPullRequestDrafting -Repo 'Sytone/botnexus')
        $result[0].action | Should -Be 'already-draft'
        Should -Invoke Set-PullRequestDraft -Times 0
    }

    It 'reports but does not mutate in dry-run mode' {
        Mock Get-OpenPullRequests { @(New-Pr -Mergeable 'CONFLICTING') }
        $result = @(Invoke-BrokenPullRequestDrafting -Repo 'Sytone/botnexus' -DryRun)
        $result[0].action | Should -Be 'would-convert'
        Should -Invoke Set-PullRequestDraft -Times 0
    }

    It 'converts each conclusively broken non-draft pull request' {
        Mock Get-OpenPullRequests {
            @(
                New-Pr -Number 10 -Mergeable 'CONFLICTING'
                New-Pr -Number 11 -Checks @(New-Check -Name 'core-tests' -Conclusion 'FAILURE')
                New-Pr -Number 12 -Checks @(New-Check -Name 'core-tests' -Conclusion 'SUCCESS')
            )
        }
        $result = @(Invoke-BrokenPullRequestDrafting -Repo 'Sytone/botnexus')
        @($result | Where-Object action -eq 'converted').Count | Should -Be 2
        Should -Invoke Set-PullRequestDraft -Times 2 -Exactly
    }
}

Describe 'source invariants' {
    BeforeAll { $source = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Set-BrokenPullRequestsDraft.ps1') }

    It 'requests enough open PRs to avoid silent default pagination' {
        $source | Should -Match 'gh\s+pr\s+list[^\r\n]*--limit\s+500'
    }

    It 'uses the supported GitHub CLI draft transition' {
        $source | Should -Match 'gh\s+pr\s+ready[^\r\n]*--undo'
    }

    It 'scrubs GH_TOKEN in a finally block' {
        $source | Should -Match '(?s)finally\s*\{.*GH_TOKEN\s*=\s*\$null'
    }

    It 'has no ready-for-review transition' {
        $source | Should -Not -Match 'gh\s+pr\s+ready(?![^\r\n]*--undo)'
    }
}

Describe 'current check chronology' {
    BeforeAll {
        function New-TimedCheck {
            param([string]$Conclusion, [string]$Started, [string]$Completed = '', [string]$Head = 'current', [int]$Attempt = 1, [long]$Run = 10)
            [pscustomobject]@{
                name = 'PR Conventions Guard'; workflowName = 'PR Conventions Guard'
                conclusion = $Conclusion; status = $(if ($Conclusion) { 'COMPLETED' } else { 'IN_PROGRESS' })
                startedAt = $Started; completedAt = $Completed; head_sha = $Head
                run_attempt = $Attempt; detailsUrl = "https://github.com/sytone/botnexus/actions/runs/$Run/job/100"
            }
        }
        function Get-TimedReason([object[]]$Checks) {
            $pr = New-Pr -Checks $Checks
            $pr | Add-Member headRefOid 'current'
            Get-DraftReason $pr
        }
    }

    It 'uses temporal direction rather than input order: newest=<Newest> reverse=<Reverse>' -ForEach @(
        @{ Newest = 'SUCCESS'; Reverse = $false; Expected = '' }
        @{ Newest = 'SUCCESS'; Reverse = $true; Expected = '' }
        @{ Newest = 'CANCELLED'; Reverse = $false; Expected = 'stuck-check:PR Conventions Guard' }
        @{ Newest = 'CANCELLED'; Reverse = $true; Expected = 'stuck-check:PR Conventions Guard' }
        @{ Newest = 'FAILURE'; Reverse = $false; Expected = 'failed-check:PR Conventions Guard' }
        @{ Newest = 'FAILURE'; Reverse = $true; Expected = 'failed-check:PR Conventions Guard' }
        @{ Newest = ''; Reverse = $false; Expected = '' }
        @{ Newest = ''; Reverse = $true; Expected = '' }
    ) {
        $old = New-TimedCheck 'CANCELLED' '2026-09-06T00:14:25Z' '2026-09-06T00:14:27Z'
        if ($Newest -in @('CANCELLED', 'FAILURE')) { $old.conclusion = 'SUCCESS' }
        $new = New-TimedCheck $Newest '2026-09-06T00:14:30Z' '2026-09-06T00:14:37Z' -Run 11
        $checks = if ($Reverse) { @($new, $old) } else { @($old, $new) }
        [string](Get-TimedReason $checks) | Should -Be $Expected
    }

    It 'reproduces the actual three-entry CLI rollup without synthetic head or attempt fields' -ForEach @($false, $true) {
        $checks = @(
            New-TimedCheck 'CANCELLED' '2026-09-06T00:14:25Z' '2026-09-06T00:14:27Z' -Run 34000761814
            New-TimedCheck 'SUCCESS' '2026-09-06T00:14:30Z' '2026-09-06T00:14:37Z' -Run 34000762695
            New-TimedCheck 'SUCCESS' '2026-09-06T01:46:54Z' '2026-09-06T01:47:01Z' -Run 34004771251
        )
        foreach ($c in $checks) { $c.PSObject.Properties.Remove('head_sha'); $c.PSObject.Properties.Remove('run_attempt') }
        if ($_) { [array]::Reverse($checks) }
        Get-TimedReason $checks | Should -BeNullOrEmpty
    }

    It 'ignores a failed check belonging to an older head' {
        Get-TimedReason @(
            (New-TimedCheck 'FAILURE' '2026-09-06T02:00:00Z' -Head 'old')
            (New-TimedCheck 'SUCCESS' '2026-09-06T01:00:00Z')
        ) | Should -BeNullOrEmpty
    }

    It 'prefers the higher attempt of the same run even when old completion arrives later' -ForEach @($false, $true) {
        $checks = @(
            New-TimedCheck 'CANCELLED' '2026-09-06T00:00:00Z' '2026-09-06T02:00:00Z' -Attempt 1
            New-TimedCheck 'SUCCESS' '2026-09-06T01:00:00Z' '2026-09-06T01:01:00Z' -Attempt 2
        )
        if ($_) { [array]::Reverse($checks) }
        Get-TimedReason $checks | Should -BeNullOrEmpty
    }

    It 'does not compare attempt counters across different runs' {
        Get-TimedReason @(
            (New-TimedCheck 'FAILURE' '2026-09-06T00:00:00Z' -Attempt 9 -Run 10)
            (New-TimedCheck 'SUCCESS' '2026-09-06T01:00:00Z' -Attempt 1 -Run 11)
        ) | Should -BeNullOrEmpty
    }

    It 'does not let later completion of an older execution override a newer start' {
        Get-TimedReason @(
            (New-TimedCheck 'FAILURE' '2026-09-06T00:00:00Z' '2026-09-06T02:00:00Z')
            (New-TimedCheck 'SUCCESS' '2026-09-06T01:00:00Z' '2026-09-06T01:01:00Z' -Run 11)
        ) | Should -BeNullOrEmpty
    }

    It 'fails open for ambiguous duplicate chronology' -ForEach @('', 'invalid', '2026-09-06T01:00:00Z') {
        Get-TimedReason @(
            (New-TimedCheck 'FAILURE' $_)
            (New-TimedCheck 'SUCCESS' $_)
        ) | Should -BeNullOrEmpty
    }

    It 'does not collapse distinct workflow or app identities' -ForEach @('workflowName', 'app') {
        $old = New-TimedCheck 'FAILURE' '2026-09-06T00:00:00Z'
        $new = New-TimedCheck 'SUCCESS' '2026-09-06T01:00:00Z'
        if ($_ -eq 'workflowName') { $new.workflowName = 'other' }
        else { $old | Add-Member app ([pscustomobject]@{id=1}); $new | Add-Member app ([pscustomobject]@{id=2}) }
        Get-TimedReason @($old, $new) | Should -Be 'failed-check:PR Conventions Guard'
    }

    It 'normalizes legacy status context names and creation times' {
        Get-TimedReason @(
            [pscustomobject]@{context='legacy'; state='FAILURE'; createdAt='2026-09-06T00:00:00Z'}
            [pscustomobject]@{context='legacy'; state='SUCCESS'; createdAt='2026-09-06T01:00:00Z'}
        ) | Should -BeNullOrEmpty
    }
}

Describe 'enumeration completeness before writes' {
    BeforeEach { Mock Set-PullRequestDraft {} }

    It 'refuses a saturated PR list before drafting any result' {
        Mock gh { $global:LASTEXITCODE = 0; ConvertTo-Json -Depth 8 -InputObject @(1..500 | ForEach-Object { New-Pr -Number $_ -Mergeable 'CONFLICTING' }) }
        { Invoke-BrokenPullRequestDrafting -Repo 'test/fixture' } | Should -Throw '*cap*500*'
        Should -Invoke Set-PullRequestDraft -Times 0
    }

    It 'accepts a list strictly below its explicit cap' {
        Mock gh { $global:LASTEXITCODE = 0; ConvertTo-Json -Depth 8 -InputObject @(1..499 | ForEach-Object { New-Pr -Number $_ }) }
        @(Get-OpenPullRequests -Repo 'test/fixture').Count | Should -Be 499
    }

    It 'refuses a possibly truncated nested rollup before any earlier conflict is drafted' {
        Mock gh {
            $global:LASTEXITCODE = 0
            ConvertTo-Json -Depth 8 -InputObject @(
                New-Pr -Number 1 -Mergeable 'CONFLICTING'
                New-Pr -Number 2 -Checks @(1..100 | ForEach-Object { New-Check -Name "check$_" -Conclusion 'SUCCESS' })
            )
        }
        { Invoke-BrokenPullRequestDrafting -Repo 'test/fixture' } | Should -Throw '*check*cap*100*'
        Should -Invoke Set-PullRequestDraft -Times 0
    }

    It 'propagates CLI failures instead of claiming an empty scan' {
        Mock gh { $global:LASTEXITCODE = 1; '[]' }
        { Get-OpenPullRequests -Repo 'test/fixture' } | Should -Throw '*gh pr list failed*'
        Should -Invoke Set-PullRequestDraft -Times 0
    }
}

Describe 'executable authentication cleanup' {
    BeforeAll { $entryPath = Join-Path $PSScriptRoot 'Set-BrokenPullRequestsDraft.ps1' }
    BeforeEach {
        $savedToken = $env:GH_TOKEN
        $env:GH_TOKEN = 'caller-fixture-token'
        Mock gh { $global:LASTEXITCODE = 0; '[]' }
        Mock pwsh { $global:LASTEXITCODE = 0; 'minted-fixture-token' }
    }
    AfterEach { $env:GH_TOKEN = $savedToken }

    It 'scrubs a successfully minted token after a successful command invocation' {
        $result = & $entryPath -Repo 'test/fixture' -DryRun -TokenScript 'stub-only.ps1' | ConvertFrom-Json
        $result.ok | Should -BeTrue
        $result.scanned | Should -Be 0
        $env:GH_TOKEN | Should -BeNullOrEmpty
        Should -Invoke pwsh -Times 1 -Exactly
    }

    It 'scrubs partial token output on a nonzero mint exit' {
        Mock pwsh { $global:LASTEXITCODE = 1; 'partial-fixture-token' }
        { & $entryPath -Repo 'test/fixture' -DryRun -TokenScript 'stub-only.ps1' } | Should -Throw '*failed to mint*'
        $env:GH_TOKEN | Should -BeNullOrEmpty
        Should -Invoke gh -Times 0
    }

    It 'scrubs the inherited token when minting throws before assignment completes' {
        Mock pwsh { throw 'mint fixture failure' }
        { & $entryPath -Repo 'test/fixture' -DryRun -TokenScript 'stub-only.ps1' } | Should -Throw '*mint fixture failure*'
        $env:GH_TOKEN | Should -BeNullOrEmpty
        Should -Invoke gh -Times 0
    }

    It 'scrubs the minted token when the scan fails' {
        Mock gh { $global:LASTEXITCODE = 1; '[]' }
        { & $entryPath -Repo 'test/fixture' -DryRun -TokenScript 'stub-only.ps1' } | Should -Throw '*gh pr list failed*'
        $env:GH_TOKEN | Should -BeNullOrEmpty
    }

    It 'preserves a caller-owned token when authentication is explicitly skipped' {
        $result = & $entryPath -Repo 'test/fixture' -DryRun -SkipAuthentication | ConvertFrom-Json
        $result.ok | Should -BeTrue
        $env:GH_TOKEN | Should -Be 'caller-fixture-token'
        Should -Invoke pwsh -Times 0
    }
}

Describe 'chronology compatibility boundaries' {
    It 'retains nonbreaking newest states and maintainer acknowledgement across reruns' -ForEach @(
        @{ Conclusion='SKIPPED'; Status='COMPLETED'; Name='core-tests' }
        @{ Conclusion='NEUTRAL'; Status='COMPLETED'; Name='core-tests' }
        @{ Conclusion=''; Status='QUEUED'; Name='core-tests' }
        @{ Conclusion=''; Status='COMPLETED'; Name='core-tests' }
        @{ Conclusion='FAILURE'; Status='COMPLETED'; Name='Sensitive File Guard' }
    ) {
        $checks = @(
            [pscustomobject]@{ name=$Name; conclusion='CANCELLED'; status='COMPLETED'; startedAt='2026-09-06T00:00:00Z' }
            [pscustomobject]@{ name=$Name; conclusion=$Conclusion; status=$Status; startedAt='2026-09-06T01:00:00Z' }
        )
        Get-DraftReason (New-Pr -Checks $checks) | Should -BeNullOrEmpty
    }

    It 'uses higher same-run attempts in both outcome directions and input orders' -ForEach @(
        @{ Newest='FAILURE'; Reverse=$false; Expected='failed-check:core-tests' }
        @{ Newest='FAILURE'; Reverse=$true; Expected='failed-check:core-tests' }
        @{ Newest='SUCCESS'; Reverse=$false; Expected='' }
        @{ Newest='SUCCESS'; Reverse=$true; Expected='' }
    ) {
        $older = if ($Newest -eq 'SUCCESS') { 'FAILURE' } else { 'SUCCESS' }
        $checks = @(
            [pscustomobject]@{ name='core-tests'; conclusion=$older; run_id=123; run_attempt=1 }
            [pscustomobject]@{ name='core-tests'; conclusion=$Newest; run_id=123; run_attempt=2 }
        )
        if ($Reverse) { [array]::Reverse($checks) }
        [string](Get-DraftReason (New-Pr -Checks $checks)) | Should -Be $Expected
    }

    It 'accepts REST snake-case chronology and scopes it to the current head' {
        $pr = New-Pr -Checks @(
            [pscustomobject]@{name='core-tests'; conclusion='failure'; head_sha='current'; started_at='2026-09-06T00:00:00Z'}
            [pscustomobject]@{name='core-tests'; conclusion='success'; head_sha='current'; started_at='2026-09-06T01:00:00Z'}
        )
        $pr | Add-Member headRefOid 'current'
        Get-DraftReason $pr | Should -BeNullOrEmpty
    }

    It 'treats an undated duplicate as unknown rather than guessing it is older' {
        Get-DraftReason (New-Pr -Checks @(
            [pscustomobject]@{name='core-tests'; conclusion='failure'; startedAt='2026-09-06T00:00:00Z'}
            [pscustomobject]@{name='core-tests'; conclusion='success'}
        )) | Should -BeNullOrEmpty
    }

    It 'accepts 99 nested check entries below the explicit cap' {
        Mock gh {
            $global:LASTEXITCODE = 0
            ConvertTo-Json -Depth 8 -InputObject @(
                New-Pr -Checks @(1..99 | ForEach-Object { New-Check -Name "check$_" -Conclusion 'SUCCESS' })
            )
        }
        @(Get-OpenPullRequests -Repo 'test/fixture')[0].statusCheckRollup.Count | Should -Be 99
    }
}
