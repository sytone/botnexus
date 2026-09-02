<#
.SYNOPSIS
    Helpers that give unattended git pushes an authentication path without
    ever persisting or logging the token.

.DESCRIPTION
    Issue #2961: scripts that rebase a branch locally and then push had no
    credential path at all. The remote is `https://github.com/Sytone/botnexus.git`,
    so git falls back to an interactive credential prompt, which in an
    unattended/agent context fails with:

        bash: line 1: /dev/tty: No such device or address
        fatal: could not read Username for 'https://github.com/Sytone/botnexus.git'

    The only pattern that works is to temporarily rewrite the *remote* URL on
    the main repository to embed `x-access-token:$GH_TOKEN`, push to the remote
    NAME, and then scrub the URL back in a `finally`. Two measured constraints
    shape this design:

    1. The token must go on the REMOTE, not on the push command line. Pushing
       to an explicit URL argument makes `--force-with-lease` fail with
       `(stale info)`, because a lease has no tracked remote-tracking ref to
       compare against when the destination is an anonymous URL. Worktrees
       inherit the main repository's remotes, so setting it once on the repo
       root covers every worktree.
    2. `--force-with-lease` also fails with `(stale info)` when the remote
       branch does not exist at all (its PR merged and the branch was deleted).
       That is a distinct condition from a genuine lease violation and must be
       reported as such rather than surfacing an opaque push error.
    3. Issue #3782: a remote may carry BOTH `remote.<name>.url` and
       `remote.<name>.pushurl`, and git resolves a push against `pushurl`
       whenever one is set — `url` is never consulted for that operation. A
       plain `git remote set-url` writes `url` only, so on such a remote the
       credential-free `pushurl` shadowed the authenticated `url` and every
       push died with `fatal: unable to get password from user`. Both keys must
       therefore be authenticated, and both restored in the `finally`.
       `git push --dry-run` SUCCEEDS in that broken configuration, so it is
       worthless as a preflight for this class of failure.

    Secrecy rules enforced here:
    - the authenticated URL is only ever live for the duration of the callback;
      the `finally` restores a credential-free URL even when the body throws;
    - every string that may carry git output is passed through
      `Remove-SecretFromText` before it reaches a log or a result payload.
#>

Set-StrictMode -Version Latest

<#
.SYNOPSIS
    Returns the remote URL with any embedded credentials removed.
.DESCRIPTION
    `https://x-access-token:ghs_xxx@github.com/o/r.git` ->
    `https://github.com/o/r.git`. Non-https (ssh, file, relative path) URLs are
    returned unchanged: they carry no userinfo component to strip.
#>
function ConvertTo-SanitizedRemoteUrl {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Url
    )

    if ([string]::IsNullOrWhiteSpace($Url)) { return $Url }
    $trimmed = $Url.Trim()
    if ($trimmed -notmatch '^(https?)://') { return $trimmed }

    # Strip everything between the scheme separator and the last '@' before the
    # first path separator, i.e. the userinfo component only.
    return [regex]::Replace($trimmed, '^(https?://)[^/@]*@', '$1')
}

<#
.SYNOPSIS
    Returns the remote URL with an x-access-token credential embedded.
.DESCRIPTION
    Any pre-existing credential is replaced, never appended to, so calling this
    twice is idempotent and cannot produce a doubled userinfo component.
#>
function ConvertTo-AuthenticatedRemoteUrl {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Url,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Token
    )

    $clean = ConvertTo-SanitizedRemoteUrl -Url $Url
    if ([string]::IsNullOrWhiteSpace($Token)) { return $clean }
    if ($clean -notmatch '^(https?)://') { return $clean }

    return [regex]::Replace($clean, '^(https?://)', "`$1x-access-token:$Token@")
}

<#
.SYNOPSIS
    Redacts secrets from text destined for a log, a result payload, or stdout.
.PARAMETER Secret
    One or more secret values. Null/empty entries are ignored so callers can
    pass an unset $env:GH_TOKEN without a guard.
#>
function Remove-SecretFromText {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter()][AllowNull()][object]$Text,
        [Parameter()][AllowNull()][string[]]$Secret
    )

    if ($null -eq $Text) { return '' }
    $value = ($Text | Out-String).TrimEnd("`r", "`n")

    foreach ($s in ($Secret | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        $value = $value.Replace($s, '***')
    }

    # Belt and braces: redact any userinfo that survived (e.g. a token embedded
    # in a URL git echoed back that did not match the supplied secret list).
    return [regex]::Replace($value, '(https?://)[^/\s@]*@', '$1***@')
}

<#
.SYNOPSIS
    Reports whether a branch still exists on the remote.
.DESCRIPTION
    Used to distinguish "remote branch was deleted" from a genuine
    `--force-with-lease` rejection: both surface as `(stale info)`, but only
    one of them is a lease violation.
#>
function Test-RemoteBranchExists {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$Branch,
        [Parameter()][string]$Remote = 'origin'
    )

    $out = git -C $RepoRoot ls-remote --heads $Remote ("refs/heads/" + $Branch) 2>&1
    if ($LASTEXITCODE -ne 0) { return $false }
    return -not [string]::IsNullOrWhiteSpace(($out | Out-String))
}

<#
.SYNOPSIS
    Annotates a failed push message when a configured pushurl is the likely cause.
.DESCRIPTION
    Issue #3782: when a push dies on credential resolution the raw git output is
    `fatal: unable to get password from user`, which reads as a token-acquisition
    failure. If the remote also carries a `pushurl`, the far more likely cause is
    that the credential-free `pushurl` shadowed the authenticated `url`. Name the
    shadowing key explicitly so the next occurrence is diagnosable from the log
    line alone instead of costing another diagnostic cycle.

    Returns $PushOutput unchanged when either condition is absent, so this never
    speculates: a credential failure on a remote with no pushurl is a genuine
    token problem and must not be misattributed.
.OUTPUTS
    The push output, optionally suffixed with the diagnostic.
#>
function Add-PushUrlShadowDiagnostic {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$PushOutput,
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter()][string]$Remote = 'origin'
    )

    if ($PushOutput -notmatch 'unable to get password|could not read Username|could not read Password|Authentication failed') {
        return $PushOutput
    }

    $pushUrl = (git -C $RepoRoot config --get "remote.$Remote.pushurl" 2>$null | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($pushUrl)) { return $PushOutput }

    $safe = ConvertTo-SanitizedRemoteUrl -Url $pushUrl
    return $PushOutput + [Environment]::NewLine +
        "Diagnostic (#3782): remote '$Remote' has a configured remote.$Remote.pushurl ($safe), " +
        "which shadows remote.$Remote.url for every push. If that pushurl carries no credential the " +
        "push cannot authenticate regardless of the token. Note that 'git push --dry-run' succeeds in this " +
        'state and is not a valid preflight.'
}

<#
.SYNOPSIS
    Runs a scriptblock with an authenticated origin URL, then scrubs it.
.DESCRIPTION
    Sets the remote URL on $RepoRoot (worktrees inherit it), invokes $Body, and
    ALWAYS restores a credential-free URL in a `finally` — including when $Body
    throws or the process is interrupted mid-push. When no token is available
    the body still runs, unchanged, against the existing remote: the push will
    then fail with the usual credential error rather than this helper silently
    swallowing the misconfiguration.
.OUTPUTS
    Whatever $Body emits.
#>
function Invoke-WithAuthenticatedRemote {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][scriptblock]$Body,
        [Parameter()][AllowNull()][AllowEmptyString()][string]$Token,
        [Parameter()][string]$Remote = 'origin'
    )

    $originalUrl = (git -C $RepoRoot remote get-url $Remote 2>$null | Out-String).Trim()
    # `git config --get` (not `remote get-url --push`) is the only way to tell a
    # remote that has NO pushurl from one whose pushurl merely equals its url:
    # `get-url --push` falls back to `url` and would make us synthesize a
    # pushurl key on a remote that never had one (#3782 AC2).
    $originalPushUrl = (git -C $RepoRoot config --get "remote.$Remote.pushurl" 2>$null | Out-String).Trim()
    $hasPushUrl = -not [string]::IsNullOrWhiteSpace($originalPushUrl)

    # Never restore a URL that itself carries a credential: if a previous run
    # died before its own scrub, this is where the leak gets cleaned up.
    $safeUrl = ConvertTo-SanitizedRemoteUrl -Url $originalUrl
    $safePushUrl = ConvertTo-SanitizedRemoteUrl -Url $originalPushUrl
    $applied = $false
    $appliedPush = $false

    try {
        if (-not [string]::IsNullOrWhiteSpace($Token) -and -not [string]::IsNullOrWhiteSpace($safeUrl)) {
            $authUrl = ConvertTo-AuthenticatedRemoteUrl -Url $safeUrl -Token $Token
            if ($authUrl -ne $safeUrl) {
                git -C $RepoRoot remote set-url $Remote $authUrl 2>&1 | Out-Null
                $applied = $true
            }
        }

        # A configured pushurl shadows url for every push, so authenticating url
        # alone leaves the operation this helper exists to enable unauthenticated.
        if ($hasPushUrl -and -not [string]::IsNullOrWhiteSpace($Token)) {
            $authPushUrl = ConvertTo-AuthenticatedRemoteUrl -Url $safePushUrl -Token $Token
            if ($authPushUrl -ne $safePushUrl) {
                git -C $RepoRoot remote set-url --push $Remote $authPushUrl 2>&1 | Out-Null
                $appliedPush = $true
            }
        }

        & $Body
    }
    finally {
        if ($applied) {
            git -C $RepoRoot remote set-url $Remote $safeUrl 2>&1 | Out-Null
        }
        if ($appliedPush) {
            git -C $RepoRoot remote set-url --push $Remote $safePushUrl 2>&1 | Out-Null
        }
    }
}


