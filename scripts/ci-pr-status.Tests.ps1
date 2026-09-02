#Requires -Modules Pester
# Copyright (c) Microsoft Corporation. All rights reserved.

BeforeAll {
    . (Join-Path $PSScriptRoot 'ci-pr-status.ps1')

    # A CheckRun entry: carries 'conclusion' (+ 'status'), never 'state'.
    function New-CheckRun {
        param([string]$Name, [string]$Conclusion, [string]$Status = 'COMPLETED')
        $o = [pscustomobject]@{ __typename = 'CheckRun'; name = $Name; status = $Status }
        if ($Conclusion) { $o | Add-Member -NotePropertyName conclusion -NotePropertyValue $Conclusion }
        return $o
    }

    # A StatusContext entry: carries 'state', never 'conclusion'.
    function New-StatusContext {
        param([string]$Name, [string]$State)
        return [pscustomobject]@{ __typename = 'StatusContext'; name = $Name; context = $Name; state = $State }
    }

    # The exact check set observed live on PR #2290 (gh pr view 2290 --json statusCheckRollup).
    function New-Pr2290Checks {
        @(
            New-CheckRun -Name 'impacted-tests' -Conclusion 'CANCELLED'
            New-CheckRun -Name 'PR Conventions Guard' -Conclusion 'SUCCESS'
            New-CheckRun -Name 'Analyze (actions)' -Conclusion 'SUCCESS'
            New-CheckRun -Name 'Secret Scanning (TruffleHog)' -Conclusion 'SUCCESS'
            New-CheckRun -Name 'Sensitive File Guard' -Conclusion 'SUCCESS'
            New-CheckRun -Name 'Analyze (csharp)' -Conclusion 'SUCCESS'
            New-CheckRun -Name 'Analyze (javascript-typescript)' -Conclusion 'SUCCESS'
            New-CheckRun -Name 'Analyze (python)' -Conclusion 'SUCCESS'
            New-CheckRun -Name 'full-tests' -Conclusion 'SKIPPED'
            New-CheckRun -Name 'Code Pattern Checks' -Conclusion 'SUCCESS'
            New-CheckRun -Name 'e2e portal (Playwright, non-blocking)' -Conclusion 'SUCCESS'
            New-CheckRun -Name 'Dependency Security Audit' -Conclusion 'SUCCESS'
            New-CheckRun -Name 'Create Issue on Findings' -Conclusion 'SKIPPED'
            New-CheckRun -Name 'CodeQL' -Conclusion 'SUCCESS'
        )
    }
}

Describe 'Get-NormalizedCheckState' {

    Context 'CheckRun shape (conclusion present, state absent)' {
        It 'reads CANCELLED out of conclusion when state is absent' {
            $c = New-CheckRun -Name 'impacted-tests' -Conclusion 'CANCELLED'
            $c.PSObject.Properties.Name | Should -Not -Contain 'state'
            Get-NormalizedCheckState -Check $c | Should -Be 'CANCELLED'
        }

        It 'reads TIMED_OUT out of conclusion when state is absent' {
            Get-NormalizedCheckState -Check (New-CheckRun -Name 't' -Conclusion 'TIMED_OUT') | Should -Be 'TIMED_OUT'
        }

        It 'maps an IN_PROGRESS CheckRun with no conclusion to PENDING' {
            $c = New-CheckRun -Name 'running' -Conclusion $null -Status 'IN_PROGRESS'
            $c.PSObject.Properties.Name | Should -Not -Contain 'conclusion'
            Get-NormalizedCheckState -Check $c | Should -Be 'PENDING'
        }

        It 'maps a QUEUED CheckRun with no conclusion to PENDING' {
            Get-NormalizedCheckState -Check (New-CheckRun -Name 'q' -Conclusion $null -Status 'QUEUED') | Should -Be 'PENDING'
        }
    }

    Context 'StatusContext shape (state present, conclusion absent)' {
        It 'reads FAILURE out of state when conclusion is absent' {
            $c = New-StatusContext -Name 'legacy-ci' -State 'FAILURE'
            $c.PSObject.Properties.Name | Should -Not -Contain 'conclusion'
            Get-NormalizedCheckState -Check $c | Should -Be 'FAILURE'
        }

        It 'reads PENDING out of state when conclusion is absent' {
            Get-NormalizedCheckState -Check (New-StatusContext -Name 'legacy-ci' -State 'PENDING') | Should -Be 'PENDING'
        }
    }

    It 'returns UNKNOWN when neither conclusion, state nor status is populated' {
        Get-NormalizedCheckState -Check ([pscustomobject]@{ name = 'x' }) | Should -Be 'UNKNOWN'
    }
}

Describe 'Get-CheckBucket - every conclusion pinned by name' {

    It 'buckets SUCCESS as ok' {
        Get-CheckBucket -Check (New-CheckRun -Name 'n' -Conclusion 'SUCCESS') | Should -Be 'ok'
    }

    It 'buckets SKIPPED as ok, not failing' {
        Get-CheckBucket -Check (New-CheckRun -Name 'full-tests' -Conclusion 'SKIPPED') | Should -Be 'ok'
    }

    It 'buckets NEUTRAL as ok, not failing' {
        Get-CheckBucket -Check (New-CheckRun -Name 'n' -Conclusion 'NEUTRAL') | Should -Be 'ok'
    }

    It 'buckets FAILURE as failing' {
        Get-CheckBucket -Check (New-CheckRun -Name 'n' -Conclusion 'FAILURE') | Should -Be 'failing'
    }

    It 'buckets ACTION_REQUIRED as failing' {
        Get-CheckBucket -Check (New-CheckRun -Name 'n' -Conclusion 'ACTION_REQUIRED') | Should -Be 'failing'
    }

    It 'buckets ERROR as failing' {
        Get-CheckBucket -Check (New-StatusContext -Name 'n' -State 'ERROR') | Should -Be 'failing'
    }

    It 'buckets CANCELLED as stuck' {
        Get-CheckBucket -Check (New-CheckRun -Name 'impacted-tests' -Conclusion 'CANCELLED') | Should -Be 'stuck'
    }

    It 'buckets TIMED_OUT as stuck' {
        Get-CheckBucket -Check (New-CheckRun -Name 'n' -Conclusion 'TIMED_OUT') | Should -Be 'stuck'
    }

    It 'buckets STALE as stuck' {
        Get-CheckBucket -Check (New-CheckRun -Name 'n' -Conclusion 'STALE') | Should -Be 'stuck'
    }

    It 'buckets PENDING as pending' {
        Get-CheckBucket -Check (New-StatusContext -Name 'n' -State 'PENDING') | Should -Be 'pending'
    }
}

Describe 'Get-PrCiStatus' {

    Context 'AC2 - the exact live check set from PR #2290' {
        BeforeAll { $script:c2290 = @(New-Pr2290Checks) }

        It 'contains exactly one cancelled impacted-tests entry' {
            @($script:c2290 | Where-Object { $_.conclusion -eq 'CANCELLED' }).Count | Should -Be 1
            @($script:c2290 | Where-Object { $_.conclusion -eq 'CANCELLED' })[0].name | Should -Be 'impacted-tests'
        }

        It 'does NOT report passing' {
            Get-PrCiStatus -Checks $script:c2290 | Should -Not -Be 'passing'
        }

        It 'reports stuck' {
            Get-PrCiStatus -Checks $script:c2290 | Should -Be 'stuck'
        }
    }

    Context 'AC1 - cancelled and timed-out runs are never passing' {
        It 'classifies a lone CANCELLED CheckRun among successes as stuck' {
            $checks = @(
                New-CheckRun -Name 'a' -Conclusion 'SUCCESS'
                New-CheckRun -Name 'impacted-tests' -Conclusion 'CANCELLED'
            )
            Get-PrCiStatus -Checks $checks | Should -Be 'stuck'
        }

        It 'classifies a lone TIMED_OUT CheckRun among successes as stuck' {
            $checks = @(
                New-CheckRun -Name 'a' -Conclusion 'SUCCESS'
                New-CheckRun -Name 'slow' -Conclusion 'TIMED_OUT'
            )
            Get-PrCiStatus -Checks $checks | Should -Be 'stuck'
        }

        It 'prefers failing over stuck when both are present' {
            $checks = @(
                New-CheckRun -Name 'boom' -Conclusion 'FAILURE'
                New-CheckRun -Name 'hung' -Conclusion 'CANCELLED'
            )
            Get-PrCiStatus -Checks $checks | Should -Be 'failing'
        }
    }

    Context 'AC6 - regression: existing classifications are unchanged' {
        It 'still reports passing for an all-SUCCESS PR' {
            $checks = @(
                New-CheckRun -Name 'a' -Conclusion 'SUCCESS'
                New-CheckRun -Name 'b' -Conclusion 'SUCCESS'
            )
            Get-PrCiStatus -Checks $checks | Should -Be 'passing'
        }

        It 'still reports passing when SKIPPED and NEUTRAL sit alongside SUCCESS' {
            $checks = @(
                New-CheckRun -Name 'a' -Conclusion 'SUCCESS'
                New-CheckRun -Name 'full-tests' -Conclusion 'SKIPPED'
                New-CheckRun -Name 'advisory' -Conclusion 'NEUTRAL'
            )
            Get-PrCiStatus -Checks $checks | Should -Be 'passing'
        }

        It 'still reports failing for a genuinely failing PR' {
            $checks = @(
                New-CheckRun -Name 'a' -Conclusion 'SUCCESS'
                New-CheckRun -Name 'b' -Conclusion 'FAILURE'
            )
            Get-PrCiStatus -Checks $checks | Should -Be 'failing'
        }

        It 'still reports pending while a check is in flight' {
            $checks = @(
                New-CheckRun -Name 'a' -Conclusion 'SUCCESS'
                New-StatusContext -Name 'b' -State 'PENDING'
            )
            Get-PrCiStatus -Checks $checks | Should -Be 'pending'
        }

        It 'still reports unknown for an empty check set' {
            Get-PrCiStatus -Checks @() | Should -Be 'unknown'
        }
    }

    Context 'AC1/AC3/AC7 (#2978) - awaiting maintainer ack is distinct from failing' {

        # The exact check set observed live on PR #2972: the guard is the only
        # failure, everything that tests the change is green.
        It 'classifies the live PR #2972 check set as awaiting-ack, not failing' {
            $checks = @(
                New-CheckRun -Name 'Security: Sensitive File Guard' -Conclusion 'FAILURE'
                New-CheckRun -Name 'CI: Docs lint' -Conclusion 'SUCCESS'
                New-CheckRun -Name 'PR Conventions Guard' -Conclusion 'SUCCESS'
                New-CheckRun -Name 'Security: Secrets & Dependencies' -Conclusion 'SUCCESS'
                New-CheckRun -Name 'Security: CodeQL Analysis' -Conclusion 'SUCCESS'
                New-CheckRun -Name 'CI: Build & Test' -Conclusion 'SUCCESS'
            )
            $status = Get-PrCiStatus -Checks $checks
            $status | Should -Be 'awaiting-ack'
            $status | Should -Not -Be 'failing'
        }

        It 'recognises the bare job name as well as the workflow-qualified form' {
            $checks = @(
                New-CheckRun -Name 'Sensitive File Guard' -Conclusion 'FAILURE'
                New-CheckRun -Name 'CI: Build & Test' -Conclusion 'SUCCESS'
            )
            Get-PrCiStatus -Checks $checks | Should -Be 'awaiting-ack'
        }

        It 'treats SKIPPED and NEUTRAL siblings as non-blocking alongside the guard' {
            $checks = @(
                New-CheckRun -Name 'Security: Sensitive File Guard' -Conclusion 'FAILURE'
                New-CheckRun -Name 'full-tests' -Conclusion 'SKIPPED'
                New-CheckRun -Name 'advisory' -Conclusion 'NEUTRAL'
            )
            Get-PrCiStatus -Checks $checks | Should -Be 'awaiting-ack'
        }

        It 'AC3 - guard failing AND a genuine failure is still failing, never awaiting-ack' {
            $checks = @(
                New-CheckRun -Name 'Security: Sensitive File Guard' -Conclusion 'FAILURE'
                New-CheckRun -Name 'CI: Build & Test' -Conclusion 'FAILURE'
                New-CheckRun -Name 'PR Conventions Guard' -Conclusion 'SUCCESS'
            )
            Get-PrCiStatus -Checks $checks | Should -Be 'failing'
            Test-AwaitingMaintainerAck -Checks $checks | Should -BeFalse
        }

        It 'AC3 - guard failing beside a StatusContext ERROR is still failing' {
            $checks = @(
                New-CheckRun -Name 'Sensitive File Guard' -Conclusion 'FAILURE'
                New-StatusContext -Name 'legacy-ci' -State 'ERROR'
            )
            Get-PrCiStatus -Checks $checks | Should -Be 'failing'
        }

        It 'does not claim awaiting-ack while a sibling check is still pending' {
            $checks = @(
                New-CheckRun -Name 'Sensitive File Guard' -Conclusion 'FAILURE'
                New-CheckRun -Name 'CI: Build & Test' -Conclusion $null -Status 'IN_PROGRESS'
            )
            Get-PrCiStatus -Checks $checks | Should -Be 'failing'
        }

        It 'does not claim awaiting-ack when a sibling check is stuck' {
            $checks = @(
                New-CheckRun -Name 'Sensitive File Guard' -Conclusion 'FAILURE'
                New-CheckRun -Name 'impacted-tests' -Conclusion 'CANCELLED'
            )
            Get-PrCiStatus -Checks $checks | Should -Be 'failing'
        }

        It 'does not claim awaiting-ack when the guard itself passed' {
            $checks = @(
                New-CheckRun -Name 'Sensitive File Guard' -Conclusion 'SUCCESS'
                New-CheckRun -Name 'CI: Build & Test' -Conclusion 'FAILURE'
            )
            Get-PrCiStatus -Checks $checks | Should -Be 'failing'
        }

        It 'does not mistake a differently-named check for the guard' {
            $checks = @(
                New-CheckRun -Name 'Sensitive File Guard Lint' -Conclusion 'FAILURE'
                New-CheckRun -Name 'CI: Build & Test' -Conclusion 'SUCCESS'
            )
            Get-PrCiStatus -Checks $checks | Should -Be 'failing'
        }
    }

    Context 'mixed entry shapes in one array' {
        It 'classifies a StatusContext FAILURE beside a CheckRun SUCCESS as failing' {
            $checks = @(
                New-CheckRun -Name 'checkrun' -Conclusion 'SUCCESS'
                New-StatusContext -Name 'statuscontext' -State 'FAILURE'
            )
            Get-PrCiStatus -Checks $checks | Should -Be 'failing'
        }

        It 'classifies a CheckRun CANCELLED beside a StatusContext SUCCESS as stuck' {
            $checks = @(
                New-StatusContext -Name 'statuscontext' -State 'SUCCESS'
                New-CheckRun -Name 'checkrun' -Conclusion 'CANCELLED'
            )
            Get-PrCiStatus -Checks $checks | Should -Be 'stuck'
        }

        It 'does not swallow an uninterpretable entry into passing' {
            $checks = @(
                New-CheckRun -Name 'a' -Conclusion 'SUCCESS'
                [pscustomobject]@{ name = 'mystery' }
            )
            Get-PrCiStatus -Checks $checks | Should -Be 'unknown'
        }
    }
}

Describe 'Get-AwaitingAckDetail (#2978 AC2)' {

    It 'carries the exact ack command bound to the head SHA' {
        $d = Get-AwaitingAckDetail -Number 2972 -HeadSha 'e494a2948a8760c57511e9df8fdb1d5ceee2b89f'
        $d.headSha | Should -Be 'e494a2948a8760c57511e9df8fdb1d5ceee2b89f'
        $d.ackCommand | Should -Be '/allow-security-sensitive-change e494a2948a8760c57511e9df8fdb1d5ceee2b89f'
        $d.reason | Should -Be 'sensitive-file-guard'
    }

    It 'AC5 - the notification key is stable per head SHA and changes when the SHA does' {
        $a = Get-AwaitingAckDetail -Number 2972 -HeadSha 'aaaa1111'
        $b = Get-AwaitingAckDetail -Number 2972 -HeadSha 'aaaa1111'
        $c = Get-AwaitingAckDetail -Number 2972 -HeadSha 'bbbb2222'
        $a.notificationKey | Should -Be $b.notificationKey
        $a.notificationKey | Should -Not -Be $c.notificationKey
        $a.notificationKey | Should -Be 'awaiting-ack:2972:aaaa1111'
    }

    It 'degrades to a marked-unknown SHA rather than emitting a bare unbound command' {
        $d = Get-AwaitingAckDetail -Number 1 -HeadSha ''
        $d.headSha | Should -Be 'unknown'
        $d.ackCommand | Should -Be '/allow-security-sensitive-change unknown'
    }

    It 'records that the ack must come from a maintainer, not automation' {
        (Get-AwaitingAckDetail -Number 1 -HeadSha 'abc').ackRequiredFrom |
            Should -Match 'admin/maintain/write'
    }
}

Describe 'AC6 (#2978) - the script cannot post the ack itself' {

    BeforeAll {
        $script:SourceText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ci-pr-status.ps1') -Raw
    }

    # DELIBERATE NON-CAPABILITY. The Sensitive File Guard requires an
    # admin/maintain/write maintainer. agent-farnsworth[bot] posting its own
    # approval would defeat the control entirely - the same trust-boundary
    # violation as authoring content as the repository owner. This test fails
    # closed if anyone ever adds a comment-write path to this script.
    It 'contains no GitHub comment-write invocation' {
        $script:SourceText | Should -Not -Match 'gh\s+pr\s+comment'
        $script:SourceText | Should -Not -Match 'gh\s+issue\s+comment'
        $script:SourceText | Should -Not -Match 'Publish-GitHubComment'
        $script:SourceText | Should -Not -Match '/issues/\d*.*comments'
    }

    It 'never emits the ack command through a gh api write verb' {
        $script:SourceText | Should -Not -Match 'gh\s+api[^\r\n]*(-X|--method)\s*(POST|PATCH|PUT)'
    }

    It 'documents the non-capability in the source so it is not silently reversed' {
        $script:SourceText | Should -Match 'DELIBERATE NON-CAPABILITY'
    }
}

Describe 'AC1/AC2 (#3773) - every gh list call carries an explicit --limit' {
    # gh applies a default --limit 30 to list verbs and emits no page-boundary
    # signal. A truncated board is indistinguishable from a small one, so the
    # only durable defence is a source-text assertion that the flag is present.
    # These tests read the script's own source and assert on the CALL SITE, not
    # on any live data shape, so they cannot go vacuously green when the board
    # happens to be under 30.
    BeforeAll {
        $script:GhListLines = @(
            Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ci-pr-status.ps1') |
                Where-Object { $_ -match 'gh\s+(pr|issue|run|release|repo|search)\s+list' -and $_ -notmatch '^\s*#' }
        )
    }

    It 'still contains the gh pr list call site these tests pin' {
        $script:GhListLines.Count | Should -BeGreaterThan 0
    }

    It 'passes an explicit --limit on the gh pr list invocation' {
        $prList = @($script:GhListLines | Where-Object { $_ -match 'gh\s+pr\s+list' })
        $prList.Count | Should -BeGreaterThan 0
        foreach ($line in $prList) { $line | Should -Match '--limit\s+\d+' }
    }

    It 'uses a --limit of at least 200 so the live board cannot truncate' {
        foreach ($line in $script:GhListLines) {
            # Do NOT rely on $Matches from Should -Match: the automatic variable
            # is left over from whichever -match ran most recently in this
            # scope (the Where-Object filter's own capture group), which
            # silently yields the verb name instead of the limit.
            $m = [regex]::Match($line, '--limit\s+(\d+)')
            $m.Success | Should -BeTrue -Because "unbounded gh list call: $line"
            [int]$m.Groups[1].Value | Should -BeGreaterOrEqual 200
        }
    }

    It 'has no unbounded gh list call anywhere in the script' {
        $unbounded = @($script:GhListLines | Where-Object { $_ -notmatch '--limit\s+\d+' })
        ($unbounded -join "`n") | Should -BeNullOrEmpty
    }
}
