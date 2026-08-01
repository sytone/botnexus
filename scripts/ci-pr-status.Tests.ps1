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
