#Requires -Modules Pester

# Regression coverage for issue #2405: ci-pr-sync-main.ps1 must resolve the
# repository from its own location ($PSScriptRoot), not from the caller's
# current working directory. The test builds a self-contained fixture repo
# (bare "origin" + working clone), then invokes the script by absolute path
# from an unrelated cwd and asserts it succeeds.

BeforeAll {
    $script:SourceScript = Join-Path $PSScriptRoot 'ci-pr-sync-main.ps1'

    function Invoke-FixtureGit {
        param([string]$Dir, [string[]]$GitArgs)
        $out = & git -C $Dir @GitArgs 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "git $($GitArgs -join ' ') failed in ${Dir}: $out"
        }
        return $out
    }

    function New-SyncFixture {
        $root = Join-Path ([IO.Path]::GetTempPath()) ("bn2405-" + [Guid]::NewGuid().ToString('N'))
        $origin = Join-Path $root 'origin.git'
        $work = Join-Path $root 'work'
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        New-Item -ItemType Directory -Path $origin -Force | Out-Null
        & git init --bare --initial-branch=main $origin *>$null
        & git init --initial-branch=main $work *>$null

        Invoke-FixtureGit $work @('config', 'user.email', 'test@example.com') | Out-Null
        Invoke-FixtureGit $work @('config', 'user.name', 'Test User') | Out-Null

        # Mirror the real repo layout: scripts/ci-pr-sync-main.ps1 plus the
        # repo/*.ps1 helpers it dot-sources relative to $PSScriptRoot.
        $scriptsDir = Join-Path $work 'scripts'
        $repoDir = Join-Path $scriptsDir 'repo'
        New-Item -ItemType Directory -Path $repoDir -Force | Out-Null
        Copy-Item $script:SourceScript (Join-Path $scriptsDir 'ci-pr-sync-main.ps1') -Force
        Copy-Item (Join-Path $PSScriptRoot 'repo/Remove-Worktree.ps1') $repoDir -Force
        Copy-Item (Join-Path $PSScriptRoot 'repo/WorktreeSyncGuard.ps1') $repoDir -Force
        Copy-Item (Join-Path $PSScriptRoot 'repo/GitRemoteAuth.ps1') $repoDir -Force

        Set-Content -Path (Join-Path $work 'readme.txt') -Value 'fixture' -Encoding utf8
        Invoke-FixtureGit $work @('add', '-A') | Out-Null
        Invoke-FixtureGit $work @('commit', '-m', 'chore: fixture base') | Out-Null
        Invoke-FixtureGit $work @('remote', 'add', 'origin', $origin) | Out-Null
        Invoke-FixtureGit $work @('push', '-u', 'origin', 'main') | Out-Null

        return [pscustomobject]@{ Root = $root; Origin = $origin; Work = $work }
    }

    # Runs a script in a child pwsh with an explicitly-set working directory.
    # Start-Process is used because -WorkingDirectory is the only reliable way
    # to control the child's real process cwd (PowerShell's location is not
    # always mirrored into the native process working directory).
    function Invoke-ScriptFromDirectory {
        param([string]$ScriptPath, [string]$Branch, [string]$WorkingDirectory)
        $stdout = [IO.Path]::GetTempFileName()
        $stderr = [IO.Path]::GetTempFileName()
        try {
            $p = Start-Process -FilePath 'pwsh' `
                -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $ScriptPath, '-Branch', $Branch) `
                -WorkingDirectory $WorkingDirectory -NoNewWindow -Wait -PassThru `
                -RedirectStandardOutput $stdout -RedirectStandardError $stderr
            return [pscustomobject]@{
                ExitCode = $p.ExitCode
                StdOut   = (Get-Content $stdout -Raw)
                StdErr   = (Get-Content $stderr -Raw)
            }
        }
        finally {
            Remove-Item $stdout, $stderr -Force -ErrorAction SilentlyContinue
        }
    }
}

Describe 'ci-pr-sync-main.ps1 repository resolution (#2405)' {
    BeforeAll {
        $script:Fixture = New-SyncFixture
        $script:FixtureScript = Join-Path $script:Fixture.Work 'scripts/ci-pr-sync-main.ps1'
        # A directory that is deliberately NOT inside any git repository.
        $script:ForeignCwd = Join-Path ([IO.Path]::GetTempPath()) ("bn2405-cwd-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $script:ForeignCwd -Force | Out-Null
    }

    AfterAll {
        if ($script:Fixture) {
            Remove-Item $script:Fixture.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
        if ($script:ForeignCwd) {
            Remove-Item $script:ForeignCwd -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'succeeds when invoked by absolute path from an unrelated working directory' {
        $run = Invoke-ScriptFromDirectory -ScriptPath $script:FixtureScript -Branch 'main' -WorkingDirectory $script:ForeignCwd
        $text = ($run.StdOut + $run.StdErr).Trim()
        $json = $text | ConvertFrom-Json
        $json.success | Should -BeTrue -Because "the script must resolve its repo from its own location, not cwd. Output was: $text"
        $json.message | Should -Match 'up to date'
    }

    It 'behaves identically when invoked from inside the repository' {
        $run = Invoke-ScriptFromDirectory -ScriptPath $script:FixtureScript -Branch 'main' -WorkingDirectory $script:Fixture.Work
        $json = ($run.StdOut + $run.StdErr).Trim() | ConvertFrom-Json
        $json.success | Should -BeTrue
    }

    It 'issues no unqualified git commands' {
        $content = Get-Content $script:SourceScript -Raw
        # Drop the comment-based help block and line comments so prose that
        # mentions git commands does not produce false positives.
        $body = [regex]::Replace($content, '(?s)<#.*?#>', '')
        $body = ($body -split "`n" | ForEach-Object { ($_ -replace '#.*$', '') }) -join "`n"
        $unqualified = [regex]::Matches($body, '(?<![\w.-])git\s+(?!-C\b)[a-z]')
        $names = ($unqualified | ForEach-Object { $_.Value }) -join '; '
        $unqualified.Count | Should -Be 0 -Because "issue #2405: all git calls must pass -C <repoRoot>. Found: $names"
    }

    # Issue #2961 gotcha 1: pushing to an explicit URL argument makes
    # --force-with-lease fail with `(stale info)`, because the lease has no
    # remote-tracking ref to compare against for an anonymous destination.
    # Authentication must therefore be applied to the remote, and every push
    # must target the remote NAME.
    It 'never pushes to an explicit url' {
        $content = Get-Content $script:SourceScript -Raw
        $body = [regex]::Replace($content, '(?s)<#.*?#>', '')
        $body = ($body -split "`n" | ForEach-Object { ($_ -replace '#.*$', '') }) -join "`n"
        $urlPushes = [regex]::Matches($body, 'push[^\r\n]*https?://')
        $urlPushes.Count | Should -Be 0 -Because 'issue #2961: push must target the remote name so --force-with-lease has a tracking ref'
    }

    It 'routes every push through the authenticated-remote helper' {
        $content = Get-Content $script:SourceScript -Raw
        $content | Should -Match 'Remove-SecretFromText' -Because 'issue #2961: git output may echo the authenticated url; it must be redacted before output'
        $content | Should -Match 'Test-RemoteBranchExists' -Because 'issue #2961 gotcha 2: a deleted remote branch also reports "(stale info)" and must be reported distinctly'

        # AST check, not a substring check. A substring assertion passes as soon
        # as ONE call site is wrapped, which leaves the other push unauthenticated
        # -- exactly the defect #2961 reports. Walk every invocation of a
        # push-performing function and require it to be lexically enclosed by an
        # Invoke-WithAuthenticatedRemote -Body block.
        $tokens = $null; $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:SourceScript, [ref]$tokens, [ref]$errors)
        $errors.Count | Should -Be 0 -Because 'the script must parse'

        $pushers = @('Invoke-RebaseAndPush', 'Invoke-LeasedPush')
        $calls = $ast.FindAll({
                param($n)
                $n -is [System.Management.Automation.Language.CommandAst] -and
                $n.GetCommandName() -and $pushers -contains $n.GetCommandName()
            }, $true)

        # Non-vacuity: the candidate set must not be empty, or this test proves nothing.
        $calls.Count | Should -BeGreaterThan 0 -Because 'there must be at least one push call site to check'

        foreach ($call in $calls) {
            $wrapped = $false
            $node = $call.Parent
            while ($node) {
                if ($node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -eq 'Invoke-WithAuthenticatedRemote') {
                    $wrapped = $true
                    break
                }
                # A push helper invoked from inside another push helper inherits
                # that helper's wrapping; only the outermost call site needs one.
                if ($node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $pushers -contains $node.Name) {
                    $wrapped = $true
                    break
                }
                $node = $node.Parent
            }
            $wrapped | Should -BeTrue -Because "issue #2961: '$($call.GetCommandName())' at line $($call.Extent.StartLineNumber) pushes without an authenticated remote, so it fails with 'could not read Username'"
        }
    }
}
