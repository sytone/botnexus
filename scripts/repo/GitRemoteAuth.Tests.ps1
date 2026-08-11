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
