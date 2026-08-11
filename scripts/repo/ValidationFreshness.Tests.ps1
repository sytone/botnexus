#Requires -Modules Pester
# Regression coverage for issue #2785: the strict validation gate ran test assemblies that
# predated the commit under validation, and computed its impacted set from a two-dot diff
# against an unfetched `origin/main`.
#
# Observed live on 2026-08-03 (branch fix/2772-update-gateway-liveness): the gate reported
# 564 tests / 3 failed from a Debug assembly stamped 14:51, while a forced-clean run of the
# same source on the same commit reported 591 / 0. 27 tests did not exist in the executed
# assembly. In the same session a 7-commit-stale `origin/main` turned a true 10-file diff
# into a 26-file one spanning projects the branch never touched.
#
# These tests exercise the properties the fix must hold:
#   1. an assembly older than the validated commit is STALE and the gate refuses to run it;
#   2. an assembly newer than the validated commit is FRESH and the gate proceeds;
#   3. an ABSENT assembly is `missing`, never `fresh` - it fails closed;
#   4. staleness is evaluated per CONFIGURATION, so a Debug build cannot certify a Release run;
#   5. the base ref resolves to the MERGE-BASE, so commits landing on the base after the fork
#      point cannot enter the change set, and the staleness is reported rather than silent.
#
# Everything runs against real temporary git repositories and real files on disk. A freshness
# guard verified against a fake clock or a mocked filesystem is not evidence of anything.

BeforeAll {
    $script:ModulePath = Join-Path $PSScriptRoot 'ValidationFreshness.psm1'
    Import-Module $script:ModulePath -Force

    $script:Scratch = Join-Path ([IO.Path]::GetTempPath()) ("bn2785-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $script:Scratch -Force | Out-Null

    # Creates a project directory with a compiled-assembly stand-in stamped at a chosen time.
    # The guard reads LastWriteTimeUtc off a real file, so the stamp is set on a real file.
    function New-FakeTestProject {
        param(
            [Parameter(Mandatory)][string]$Name,
            [string]$Configuration = 'Debug',
            [Nullable[DateTime]]$AssemblyTimeUtc = $null
        )
        $dir = Join-Path $script:Scratch ("proj-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $csproj = Join-Path $dir "$Name.csproj"
        Set-Content -LiteralPath $csproj -Value '<Project />' -Encoding utf8

        if ($null -ne $AssemblyTimeUtc) {
            $binDir = Join-Path $dir (Join-Path 'bin' (Join-Path $Configuration 'net10.0'))
            New-Item -ItemType Directory -Path $binDir -Force | Out-Null
            $dll = Join-Path $binDir "$Name.dll"
            Set-Content -LiteralPath $dll -Value 'not-a-real-assembly' -Encoding utf8
            (Get-Item -LiteralPath $dll).LastWriteTimeUtc = $AssemblyTimeUtc
        }
        return $csproj
    }

    function Invoke-Git {
        param([string]$RepoRoot, [string[]]$Arguments)
        $out = & git -C $RepoRoot @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed: $out" }
        return ($out | Out-String).Trim()
    }

    # Builds an origin repo plus a clone whose branch forked BEFORE two later commits landed
    # on origin/main. That is the exact shape of the observed 7-commits-behind checkout.
    function New-ForkedRepoPair {
        $origin = Join-Path $script:Scratch ("origin-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $origin -Force | Out-Null
        Invoke-Git $origin @('init', '--initial-branch=main', '--quiet') | Out-Null
        Invoke-Git $origin @('config', 'user.email', 'test@example.com') | Out-Null
        Invoke-Git $origin @('config', 'user.name', 'Test') | Out-Null
        Set-Content -LiteralPath (Join-Path $origin 'base.txt') -Value 'base' -Encoding utf8
        Invoke-Git $origin @('add', '-A') | Out-Null
        Invoke-Git $origin @('commit', '-m', 'base', '--quiet') | Out-Null

        $clone = Join-Path $script:Scratch ("clone-" + [Guid]::NewGuid().ToString('N'))
        Invoke-Git $script:Scratch @('clone', '--quiet', $origin, $clone) | Out-Null
        Invoke-Git $clone @('config', 'user.email', 'test@example.com') | Out-Null
        Invoke-Git $clone @('config', 'user.name', 'Test') | Out-Null

        # Branch work in the clone: touches exactly one file.
        Invoke-Git $clone @('checkout', '-q', '-b', 'feature') | Out-Null
        Set-Content -LiteralPath (Join-Path $clone 'branch-only.txt') -Value 'branch' -Encoding utf8
        Invoke-Git $clone @('add', '-A') | Out-Null
        Invoke-Git $clone @('commit', '-m', 'branch change', '--quiet') | Out-Null

        # Unrelated commits land on origin/main AFTER the fork point.
        foreach ($n in 1..2) {
            Set-Content -LiteralPath (Join-Path $origin "unrelated-$n.txt") -Value "u$n" -Encoding utf8
            Invoke-Git $origin @('add', '-A') | Out-Null
            Invoke-Git $origin @('commit', '-m', "unrelated $n", '--quiet') | Out-Null
        }

        return [pscustomobject]@{ Origin = $origin; Clone = $clone }
    }
}

AfterAll {
    Remove-Module ValidationFreshness -Force -ErrorAction SilentlyContinue
    if (Test-Path $script:Scratch) { Remove-Item $script:Scratch -Recurse -Force -ErrorAction SilentlyContinue }
}

Describe 'Get-BotNexusTestAssemblyState (#2785 stale test assemblies)' {

    It 'classifies an assembly compiled BEFORE the validated commit as stale' {
        # The literal observed case: assembly at 14:51, commit later than that.
        $commit = [DateTime]::UtcNow
        $proj = New-FakeTestProject -Name 'Stale.Tests' -AssemblyTimeUtc $commit.AddMinutes(-15)

        $state = Get-BotNexusTestAssemblyState -ProjectPath @($proj) -Configuration 'Debug' -ReferenceTimeUtc $commit

        $state.Count | Should -Be 1
        $state[0].State | Should -Be 'stale'
        $state[0].Name | Should -Be 'Stale.Tests'
    }

    It 'classifies an assembly compiled AFTER the validated commit as fresh' {
        $commit = [DateTime]::UtcNow
        $proj = New-FakeTestProject -Name 'Fresh.Tests' -AssemblyTimeUtc $commit.AddMinutes(5)

        $state = Get-BotNexusTestAssemblyState -ProjectPath @($proj) -Configuration 'Debug' -ReferenceTimeUtc $commit

        $state[0].State | Should -Be 'fresh'
    }

    It 'classifies an ABSENT assembly as missing, never fresh - absence fails closed' {
        $commit = [DateTime]::UtcNow
        $proj = New-FakeTestProject -Name 'Never.Built.Tests' -AssemblyTimeUtc $null

        $state = Get-BotNexusTestAssemblyState -ProjectPath @($proj) -Configuration 'Debug' -ReferenceTimeUtc $commit

        $state[0].State | Should -Be 'missing'
        $state[0].State | Should -Not -Be 'fresh'
        $state[0].AssemblyPath | Should -BeNullOrEmpty
    }

    It 'evaluates freshness per configuration, so a Debug build cannot certify a Release run' {
        # Defect 1's configuration split: the build step passed no -c (Debug) while Release
        # artifacts also existed in the tree. Only one artifact set was ever refreshed.
        $commit = [DateTime]::UtcNow
        $proj = New-FakeTestProject -Name 'Split.Tests' -Configuration 'Debug' -AssemblyTimeUtc $commit.AddMinutes(5)

        $debugState = Get-BotNexusTestAssemblyState -ProjectPath @($proj) -Configuration 'Debug' -ReferenceTimeUtc $commit
        $releaseState = Get-BotNexusTestAssemblyState -ProjectPath @($proj) -Configuration 'Release' -ReferenceTimeUtc $commit

        $debugState[0].State | Should -Be 'fresh'
        $releaseState[0].State | Should -Be 'missing'
    }
}

Describe 'Assert-BotNexusTestAssemblyFreshness (#2785 gate refuses stale artifacts)' {

    It 'reports IsFresh false and names the offender when any assembly predates the commit' {
        $commit = [DateTime]::UtcNow
        $stale = New-FakeTestProject -Name 'Old.Tests' -AssemblyTimeUtc $commit.AddMinutes(-15)
        $fresh = New-FakeTestProject -Name 'New.Tests' -AssemblyTimeUtc $commit.AddMinutes(5)

        $result = Assert-BotNexusTestAssemblyFreshness -ProjectPath @($stale, $fresh) -Configuration 'Debug' -ReferenceTimeUtc $commit

        $result.IsFresh | Should -BeFalse
        $result.Offenders.Count | Should -Be 1
        $result.Offenders[0].Name | Should -Be 'Old.Tests'
        # Naming the stale artifact is the point: the original occurrence cost two hours
        # precisely because the failure did not say which assembly was old.
        $result.Message | Should -BeLike '*Old.Tests*'
        $result.Message | Should -BeLike '*--no-build*'
    }

    It 'reports IsFresh true when every assembly postdates the commit' {
        $commit = [DateTime]::UtcNow
        $a = New-FakeTestProject -Name 'A.Tests' -AssemblyTimeUtc $commit.AddMinutes(1)
        $b = New-FakeTestProject -Name 'B.Tests' -AssemblyTimeUtc $commit.AddMinutes(2)

        $result = Assert-BotNexusTestAssemblyFreshness -ProjectPath @($a, $b) -Configuration 'Debug' -ReferenceTimeUtc $commit

        $result.IsFresh | Should -BeTrue
        $result.Offenders.Count | Should -Be 0
    }

    It 'fails closed on a project that was never built at all' {
        $commit = [DateTime]::UtcNow
        $proj = New-FakeTestProject -Name 'Absent.Tests' -AssemblyTimeUtc $null

        $result = Assert-BotNexusTestAssemblyFreshness -ProjectPath @($proj) -Configuration 'Debug' -ReferenceTimeUtc $commit

        $result.IsFresh | Should -BeFalse
        $result.Message | Should -BeLike '*no assembly on disk*'
    }

    It 'treats an empty project set as fresh - there is nothing to run stale' {
        $result = Assert-BotNexusTestAssemblyFreshness -ProjectPath @() -Configuration 'Debug' -ReferenceTimeUtc ([DateTime]::UtcNow)
        $result.IsFresh | Should -BeTrue
    }
}

Describe 'Resolve-BotNexusValidationBaseRef (#2785 stale base ref / two-dot diff)' {

    It 'resolves the merge-base, not the base tip, when the base has moved on' {
        $pair = New-ForkedRepoPair
        $resolved = Resolve-BotNexusValidationBaseRef -RepoRoot $pair.Clone -BaseRef 'origin/main'

        # After the fetch, origin/main carries two commits the branch never saw.
        $resolved.BaseCommit | Should -Not -Be $resolved.MergeBase
        $resolved.BehindCount | Should -Be 2
        $resolved.IsStale | Should -BeTrue
    }

    It 'computes a change set containing ONLY the branch file when diffed from the merge-base' {
        # This is the assertion that encodes the observed 26-files-vs-10-files inflation.
        $pair = New-ForkedRepoPair
        $resolved = Resolve-BotNexusValidationBaseRef -RepoRoot $pair.Clone -BaseRef 'origin/main'

        # The @() must wrap the WHOLE pipeline: `@(git ...) | Where-Object` unwraps back to a
        # scalar when exactly one path survives the filter, and reading .Count off a scalar
        # string is a terminating error under Set-StrictMode.
        $fromMergeBase = @(@(& git -C $pair.Clone diff --name-only $resolved.MergeBase HEAD) | Where-Object { $_ })
        $fromTip = @(@(& git -C $pair.Clone diff --name-only $resolved.BaseCommit HEAD) | Where-Object { $_ })

        $fromMergeBase | Should -Be @('branch-only.txt')
        # The two-dot diff against the tip drags in the unrelated base commits - the defect.
        $fromTip.Count | Should -BeGreaterThan $fromMergeBase.Count
        $fromTip | Should -Contain 'unrelated-1.txt'
    }

    It 'fetches the remote-tracking base ref so the impacted set is not computed from a cache' {
        $pair = New-ForkedRepoPair
        # Before the guard runs, the clone's origin/main is the pre-fork commit.
        $before = (& git -C $pair.Clone rev-parse 'origin/main').Trim()

        $resolved = Resolve-BotNexusValidationBaseRef -RepoRoot $pair.Clone -BaseRef 'origin/main'

        $resolved.Fetched | Should -BeTrue
        $resolved.BaseCommit | Should -Not -Be $before
    }

    It 'does not fetch, but still measures staleness, when -NoFetch is supplied' {
        $pair = New-ForkedRepoPair
        $before = (& git -C $pair.Clone rev-parse 'origin/main').Trim()

        $resolved = Resolve-BotNexusValidationBaseRef -RepoRoot $pair.Clone -BaseRef 'origin/main' -NoFetch

        $resolved.Fetched | Should -BeFalse
        $resolved.BaseCommit | Should -Be $before
        # The unfetched cache still resolves - it is simply reported honestly.
        $resolved.MergeBase | Should -Be $before
    }

    It 'does not attempt a fetch for a local ref, and reports it as not stale' {
        $pair = New-ForkedRepoPair
        $resolved = Resolve-BotNexusValidationBaseRef -RepoRoot $pair.Clone -BaseRef 'HEAD'

        $resolved.Fetched | Should -BeFalse
        $resolved.BehindCount | Should -Be 0
        $resolved.IsStale | Should -BeFalse
    }

    It 'throws rather than guessing when the base ref cannot be resolved at all' {
        $pair = New-ForkedRepoPair
        { Resolve-BotNexusValidationBaseRef -RepoRoot $pair.Clone -BaseRef 'origin/nonexistent-branch' } |
            Should -Throw '*cannot be resolved*'
    }
}
