#Requires -Modules Pester

# Coverage for issue #2961: unattended pushes had no authentication path, so
# every force-push failed with "could not read Username". These tests pin the
# three properties of the fix:
#   1. the token is embedded in the REMOTE URL (not passed as a push argument),
#   2. the remote is scrubbed back to a credential-free URL in a `finally`,
#   3. the token never appears in any emitted text.

BeforeAll {
    . (Join-Path $PSScriptRoot 'GitRemoteAuth.ps1')

    $script:CleanUrl = 'https://github.com/Sytone/botnexus.git'
    $script:Token = 'ghs_TESTTOKEN1234567890'

    function New-BareOriginFixture {
        $root = Join-Path ([IO.Path]::GetTempPath()) ("bn2961-" + [Guid]::NewGuid().ToString('N'))
        $origin = Join-Path $root 'origin.git'
        $work = Join-Path $root 'work'
        New-Item -ItemType Directory -Path $origin -Force | Out-Null
        & git init --bare --initial-branch=main $origin *>$null
        & git init --initial-branch=main $work *>$null
        & git -C $work config user.email 'test@example.com' *>$null
        & git -C $work config user.name 'Test User' *>$null
        Set-Content -Path (Join-Path $work 'readme.txt') -Value 'fixture' -Encoding utf8
        & git -C $work add -A *>$null
        & git -C $work commit -m 'chore: base' *>$null
        & git -C $work remote add origin $origin *>$null
        & git -C $work push -u origin main *>$null
        return [pscustomobject]@{ Root = $root; Origin = $origin; Work = $work }
    }

    # Issue #3782 fixture: a remote carrying BOTH url and pushurl, which is the
    # shape of Q:/repos/botnexus/.git/config. git resolves a push against
    # pushurl whenever one is set, so a helper that authenticates url alone
    # leaves the push unauthenticated.
    function Set-FixtureUrlAndPushUrl {
        param(
            [Parameter(Mandatory)][string]$Work,
            [Parameter(Mandatory)][string]$Url,
            [Parameter(Mandatory)][string]$PushUrl
        )
        & git -C $Work remote set-url origin $Url *>$null
        & git -C $Work config remote.origin.pushurl $PushUrl *>$null
    }

    function Get-RemoteUrlKey {
        param([Parameter(Mandatory)][string]$Work, [Parameter(Mandatory)][string]$Key)
        # `git config --get` distinguishes an ABSENT pushurl from one that equals
        # url; `remote get-url --push` silently falls back to url and cannot.
        $v = (& git -C $Work config --get "remote.origin.$Key" 2>$null | Out-String).Trim()
        return $v
    }
}

Describe 'ConvertTo-SanitizedRemoteUrl' {
    It 'strips an embedded x-access-token credential' {
        ConvertTo-SanitizedRemoteUrl -Url "https://x-access-token:$script:Token@github.com/Sytone/botnexus.git" |
            Should -Be $script:CleanUrl
    }

    It 'strips a user:password credential' {
        ConvertTo-SanitizedRemoteUrl -Url 'https://alice:hunter2@github.com/Sytone/botnexus.git' |
            Should -Be $script:CleanUrl
    }

    It 'leaves an already-clean https url unchanged' {
        ConvertTo-SanitizedRemoteUrl -Url $script:CleanUrl | Should -Be $script:CleanUrl
    }

    It 'leaves non-https remotes unchanged' {
        ConvertTo-SanitizedRemoteUrl -Url 'git@github.com:Sytone/botnexus.git' |
            Should -Be 'git@github.com:Sytone/botnexus.git'
    }
}

Describe 'ConvertTo-AuthenticatedRemoteUrl' {
    It 'embeds the token as x-access-token userinfo' {
        ConvertTo-AuthenticatedRemoteUrl -Url $script:CleanUrl -Token $script:Token |
            Should -Be "https://x-access-token:$script:Token@github.com/Sytone/botnexus.git"
    }

    It 'is idempotent: re-authenticating replaces rather than doubles the credential' {
        $once = ConvertTo-AuthenticatedRemoteUrl -Url $script:CleanUrl -Token $script:Token
        $twice = ConvertTo-AuthenticatedRemoteUrl -Url $once -Token $script:Token
        $twice | Should -Be $once
        ([regex]::Matches($twice, '@')).Count | Should -Be 1
    }

    It 'returns a clean url unchanged when no token is available' {
        ConvertTo-AuthenticatedRemoteUrl -Url $script:CleanUrl -Token '' | Should -Be $script:CleanUrl
    }
}

Describe 'Remove-SecretFromText' {
    It 'redacts the raw token' {
        $text = "fatal: push rejected using $script:Token"
        $redacted = Remove-SecretFromText -Text $text -Secret @($script:Token)
        $redacted | Should -Not -Match ([regex]::Escape($script:Token))
        $redacted | Should -Match '\*\*\*'
    }

    It 'redacts userinfo in a url even when the secret list is empty' {
        $text = "remote: https://x-access-token:ghs_LEAKED@github.com/Sytone/botnexus.git"
        $redacted = Remove-SecretFromText -Text $text -Secret @()
        $redacted | Should -Not -Match 'ghs_LEAKED'
    }

    It 'tolerates a null secret entry' {
        Remove-SecretFromText -Text 'ok' -Secret @($null, '') | Should -Be 'ok'
    }
}

Describe 'Invoke-WithAuthenticatedRemote' {
    BeforeEach {
        $script:Fixture = New-BareOriginFixture
    }

    AfterEach {
        if ($script:Fixture) { Remove-Item $script:Fixture.Root -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'exposes the authenticated url to the body and scrubs it afterwards' {
        & git -C $script:Fixture.Work remote set-url origin $script:CleanUrl
        $script:seen = $null
        Invoke-WithAuthenticatedRemote -RepoRoot $script:Fixture.Work -Token $script:Token -Body {
            $script:seen = (& git -C $script:Fixture.Work remote get-url origin | Out-String).Trim()
        }
        $script:seen | Should -Be "https://x-access-token:$script:Token@github.com/Sytone/botnexus.git"

        $after = (& git -C $script:Fixture.Work remote get-url origin | Out-String).Trim()
        $after | Should -Be $script:CleanUrl
    }

    It 'scrubs the remote even when the body throws' {
        & git -C $script:Fixture.Work remote set-url origin $script:CleanUrl
        { Invoke-WithAuthenticatedRemote -RepoRoot $script:Fixture.Work -Token $script:Token -Body {
                throw 'boom'
            } } | Should -Throw 'boom'

        $after = (& git -C $script:Fixture.Work remote get-url origin | Out-String).Trim()
        $after | Should -Be $script:CleanUrl
        $after | Should -Not -Match ([regex]::Escape($script:Token))
    }

    It 'cleans up a credential left behind by a previous interrupted run' {
        & git -C $script:Fixture.Work remote set-url origin "https://x-access-token:ghs_STALE@github.com/Sytone/botnexus.git"
        Invoke-WithAuthenticatedRemote -RepoRoot $script:Fixture.Work -Token $script:Token -Body { }
        $after = (& git -C $script:Fixture.Work remote get-url origin | Out-String).Trim()
        $after | Should -Be $script:CleanUrl
    }

    It 'still runs the body when no token is available' {
        $script:ran = $false
        Invoke-WithAuthenticatedRemote -RepoRoot $script:Fixture.Work -Token '' -Body { $script:ran = $true }
        $script:ran | Should -BeTrue
    }
}

Describe 'Test-RemoteBranchExists' {
    BeforeAll {
        $script:BranchFixture = New-BareOriginFixture
    }

    AfterAll {
        if ($script:BranchFixture) { Remove-Item $script:BranchFixture.Root -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'returns true for a branch that exists on the remote' {
        Test-RemoteBranchExists -RepoRoot $script:BranchFixture.Work -Branch 'main' | Should -BeTrue
    }

    It 'returns false for a branch that was deleted from the remote' {
        Test-RemoteBranchExists -RepoRoot $script:BranchFixture.Work -Branch 'feat/never-existed' | Should -BeFalse
    }
}

# ---------------------------------------------------------------------------
# Issue #3782: `git remote set-url` writes remote.<name>.url ONLY. A remote that
# also carries remote.<name>.pushurl resolves every push against the pushurl, so
# authenticating url alone left the push credential-free and it died with
# `fatal: unable to get password from user`. Note `git push --dry-run` SUCCEEDS
# in the broken configuration, so no assertion below may use a dry-run.
# ---------------------------------------------------------------------------
Describe 'Invoke-WithAuthenticatedRemote pushurl handling (#3782)' {
    BeforeEach {
        $script:Fixture = New-BareOriginFixture
    }

    AfterEach {
        if ($script:Fixture) { Remove-Item $script:Fixture.Root -Recurse -Force -ErrorAction SilentlyContinue }
    }

    # Premise check. Pins the git behaviour the whole fix rests on WITHOUT any
    # token: url points at a path that does not exist, pushurl at the real bare
    # origin. A push that succeeds proves git consulted pushurl and ignored url.
    It 'git resolves a push against pushurl and ignores url when both are set' {
        $dead = Join-Path $script:Fixture.Root 'no-such-origin.git'
        Set-FixtureUrlAndPushUrl -Work $script:Fixture.Work -Url $dead -PushUrl $script:Fixture.Origin

        & git -C $script:Fixture.Work checkout -b probe/pushurl *>$null
        & git -C $script:Fixture.Work push origin probe/pushurl *>$null
        $LASTEXITCODE | Should -Be 0

        # Query the bare origin directly. `git -C <bare>` is refused under
        # safe.bareRepository=explicit, and the --git-dir value must be bound to
        # a plain variable first: PowerShell does not expand a property access
        # inside a bare `--git-dir=$obj.Prop` argument token.
        $originDir = $script:Fixture.Origin
        $onRemote = (& git --git-dir=$originDir show-ref --verify 'refs/heads/probe/pushurl' 2>$null | Out-String).Trim()
        $onRemote | Should -Match '^[0-9a-f]{40} refs/heads/probe/pushurl$'
    }

    # AC4 (non-vacuity target): reverting the `set-url --push` write MUST turn
    # this red. Asserted from inside the body, where the authenticated state is
    # live -- the `finally` has deliberately erased it by the time the call
    # returns, so this cannot be checked afterwards.
    It 'AC4: authenticates the pushurl, which is the key git actually pushes to' {
        Set-FixtureUrlAndPushUrl -Work $script:Fixture.Work -Url $script:CleanUrl -PushUrl $script:CleanUrl
        $script:seenPush = $null
        $script:seenUrl = $null
        Invoke-WithAuthenticatedRemote -RepoRoot $script:Fixture.Work -Token $script:Token -Body {
            $script:seenPush = Get-RemoteUrlKey -Work $script:Fixture.Work -Key 'pushurl'
            $script:seenUrl = Get-RemoteUrlKey -Work $script:Fixture.Work -Key 'url'
        }

        $script:seenPush | Should -Be "https://x-access-token:$script:Token@github.com/Sytone/botnexus.git"
        $script:seenUrl | Should -Be "https://x-access-token:$script:Token@github.com/Sytone/botnexus.git"
    }

    # AC4, second half: the push TARGET git would use is authenticated. Uses
    # `git remote get-url --push`, i.e. git's own resolution, rather than the
    # raw config key, so the assertion follows git's precedence rules and not
    # this test's belief about them.
    It 'AC4: the resolved push target carries the credential inside the body' {
        Set-FixtureUrlAndPushUrl -Work $script:Fixture.Work -Url $script:CleanUrl -PushUrl $script:CleanUrl
        $script:resolved = $null
        Invoke-WithAuthenticatedRemote -RepoRoot $script:Fixture.Work -Token $script:Token -Body {
            $script:resolved = (& git -C $script:Fixture.Work remote get-url --push origin | Out-String).Trim()
        }
        $script:resolved | Should -Match ([regex]::Escape($script:Token))
    }

    # AC1 (restore half).
    It 'AC1: restores BOTH url and pushurl to their sanitized values' {
        Set-FixtureUrlAndPushUrl -Work $script:Fixture.Work -Url $script:CleanUrl -PushUrl $script:CleanUrl
        Invoke-WithAuthenticatedRemote -RepoRoot $script:Fixture.Work -Token $script:Token -Body { }

        (Get-RemoteUrlKey -Work $script:Fixture.Work -Key 'url') | Should -Be $script:CleanUrl
        (Get-RemoteUrlKey -Work $script:Fixture.Work -Key 'pushurl') | Should -Be $script:CleanUrl
    }

    # AC2: unchanged behaviour on a remote with no pushurl. The helper must not
    # synthesize the key -- `remote get-url --push` falls back to url, so a
    # naive implementation reading it would create a pushurl that never existed.
    It 'AC2: does not create a pushurl on a remote that has none' {
        & git -C $script:Fixture.Work remote set-url origin $script:CleanUrl *>$null
        & git -C $script:Fixture.Work config --unset remote.origin.pushurl *>$null

        Invoke-WithAuthenticatedRemote -RepoRoot $script:Fixture.Work -Token $script:Token -Body {
            (Get-RemoteUrlKey -Work $script:Fixture.Work -Key 'pushurl') | Should -BeNullOrEmpty
        }

        (Get-RemoteUrlKey -Work $script:Fixture.Work -Key 'pushurl') | Should -BeNullOrEmpty
        (Get-RemoteUrlKey -Work $script:Fixture.Work -Key 'url') | Should -Be $script:CleanUrl
    }

    # AC3: no token survives in EITHER key, including on the throwing path.
    It 'AC3: leaves no token in url or pushurl after the body throws' {
        Set-FixtureUrlAndPushUrl -Work $script:Fixture.Work -Url $script:CleanUrl -PushUrl $script:CleanUrl
        { Invoke-WithAuthenticatedRemote -RepoRoot $script:Fixture.Work -Token $script:Token -Body {
                throw 'boom'
            } } | Should -Throw 'boom'

        $url = Get-RemoteUrlKey -Work $script:Fixture.Work -Key 'url'
        $push = Get-RemoteUrlKey -Work $script:Fixture.Work -Key 'pushurl'
        $url | Should -Not -Match ([regex]::Escape($script:Token))
        $push | Should -Not -Match ([regex]::Escape($script:Token))
        $url | Should -Be $script:CleanUrl
        $push | Should -Be $script:CleanUrl
    }

    # AC3, leak-cleanup half: a pushurl left credential-bearing by an
    # interrupted previous run must be scrubbed, exactly as url already was.
    It 'AC3: scrubs a credential a previous interrupted run left in pushurl' {
        Set-FixtureUrlAndPushUrl -Work $script:Fixture.Work `
            -Url "https://x-access-token:ghs_STALE@github.com/Sytone/botnexus.git" `
            -PushUrl "https://x-access-token:ghs_STALE@github.com/Sytone/botnexus.git"

        Invoke-WithAuthenticatedRemote -RepoRoot $script:Fixture.Work -Token $script:Token -Body { }

        (Get-RemoteUrlKey -Work $script:Fixture.Work -Key 'url') | Should -Be $script:CleanUrl
        (Get-RemoteUrlKey -Work $script:Fixture.Work -Key 'pushurl') | Should -Be $script:CleanUrl
    }

    It 'leaves pushurl untouched when no token is available' {
        Set-FixtureUrlAndPushUrl -Work $script:Fixture.Work -Url $script:CleanUrl -PushUrl $script:CleanUrl
        Invoke-WithAuthenticatedRemote -RepoRoot $script:Fixture.Work -Token '' -Body { }
        (Get-RemoteUrlKey -Work $script:Fixture.Work -Key 'pushurl') | Should -Be $script:CleanUrl
    }
}

Describe 'Add-PushUrlShadowDiagnostic (#3782 AC5)' {
    BeforeEach {
        $script:DiagFixture = New-BareOriginFixture
    }

    AfterEach {
        if ($script:DiagFixture) { Remove-Item $script:DiagFixture.Root -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'names the shadowing pushurl on a credential failure when one is configured' {
        Set-FixtureUrlAndPushUrl -Work $script:DiagFixture.Work -Url $script:CleanUrl -PushUrl $script:CleanUrl
        $out = Add-PushUrlShadowDiagnostic -PushOutput 'fatal: unable to get password from user' -RepoRoot $script:DiagFixture.Work
        $out | Should -Match 'pushurl'
        $out | Should -Match '#3782'
        $out | Should -Match 'fatal: unable to get password from user'
    }

    It 'stays silent when the remote has no pushurl, so a real token failure is not misattributed' {
        & git -C $script:DiagFixture.Work remote set-url origin $script:CleanUrl *>$null
        & git -C $script:DiagFixture.Work config --unset remote.origin.pushurl *>$null
        $out = Add-PushUrlShadowDiagnostic -PushOutput 'fatal: unable to get password from user' -RepoRoot $script:DiagFixture.Work
        $out | Should -Be 'fatal: unable to get password from user'
    }

    It 'stays silent on a non-credential push failure even when a pushurl is set' {
        Set-FixtureUrlAndPushUrl -Work $script:DiagFixture.Work -Url $script:CleanUrl -PushUrl $script:CleanUrl
        $out = Add-PushUrlShadowDiagnostic -PushOutput '! [rejected] main -> main (stale info)' -RepoRoot $script:DiagFixture.Work
        $out | Should -Be '! [rejected] main -> main (stale info)'
    }

    It 'never echoes a credential embedded in the pushurl it reports' {
        Set-FixtureUrlAndPushUrl -Work $script:DiagFixture.Work -Url $script:CleanUrl `
            -PushUrl "https://x-access-token:$script:Token@github.com/Sytone/botnexus.git"
        $out = Add-PushUrlShadowDiagnostic -PushOutput 'fatal: unable to get password from user' -RepoRoot $script:DiagFixture.Work
        $out | Should -Not -Match ([regex]::Escape($script:Token))
    }
}

