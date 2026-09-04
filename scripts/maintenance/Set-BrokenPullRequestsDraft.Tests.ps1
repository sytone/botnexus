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
