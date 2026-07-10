<#
.SYNOPSIS
    Pre-creates Windows Firewall allow-rules for the testhost.exe binaries used by
    the given test projects, so `dotnet test` never triggers an interactive
    "Allow app through the firewall" prompt.

.DESCRIPTION
    The .NET test host (testhost.exe) opens a loopback socket to talk to the
    `dotnet test` runner. On Windows, the first time a given testhost.exe path
    runs, the Defender Firewall shows an interactive allow/deny popup. Because
    each git worktree lives at a different absolute path
    (e.g. Q:\repos\botnexus-wt\<branch>\tests\...\bin\Debug\net10.0\testhost.exe),
    a new prompt appears for every worktree — which blocks unattended/agent runs.

    This helper computes the *deterministic* testhost.exe path for each supplied
    test project (<projectDir>/bin/<Configuration>/<TFM>/testhost.exe), checks
    which paths do not already have a firewall rule, and — only if any are
    missing — creates inbound + outbound allow-rules for them.

    Creating firewall rules requires elevation. To keep unattended runs friction-
    free, all missing rules are batched into a SINGLE self-elevated child process,
    so at most one UAC prompt appears per run (and none when rules already exist).

    Everything here is BEST-EFFORT and Windows-only:
      * On non-Windows it returns immediately (no-op).
      * Any failure (declined UAC, policy block, etc.) is swallowed with a warning
        so it can never fail a build, a pre-commit hook, or a CI run.

.PARAMETER ProjectPath
    One or more test project (.csproj) paths. The testhost.exe path is derived
    from each project's directory. Accepts pipeline input.

.PARAMETER Configuration
    Build configuration segment of the bin path. Defaults to 'Debug'.

.PARAMETER TargetFramework
    Target framework segment of the bin path. Defaults to 'net10.0'.

.PARAMETER Quiet
    Suppress informational output (warnings still show).

.EXAMPLE
    .\Ensure-TesthostFirewallRules.ps1 -ProjectPath (Get-ChildItem -Recurse -Filter *.Tests.csproj).FullName
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromPipeline = $true, Position = 0)]
    [string[]]$ProjectPath,

    [string]$Configuration = 'Debug',

    [string]$TargetFramework = 'net10.0',

    [switch]$Quiet
)

begin {
    $ruleTag = 'BotNexus-Testhost'
    $collected = [System.Collections.Generic.List[string]]::new()

    function Write-Info {
        param([string]$Message, [string]$Color = 'DarkGray')
        if (-not $Quiet) { Write-Host $Message -ForegroundColor $Color }
    }

    # Best-effort guard: only meaningful on Windows.
    $isWindowsOs = $true
    if (Get-Variable -Name 'IsWindows' -ErrorAction SilentlyContinue) {
        $isWindowsOs = $IsWindows
    }
}

process {
    if (-not $ProjectPath) { return }
    foreach ($p in $ProjectPath) {
        if ($p) { $collected.Add($p) }
    }
}

end {
    if (-not $isWindowsOs) {
        Write-Info "Not Windows — skipping testhost firewall setup."
        return
    }

    if ($collected.Count -eq 0) { return }

    # --- Derive deterministic testhost.exe paths for each project ---
    $candidatePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($proj in $collected) {
        try {
            $projDir = Split-Path -Parent $proj
            if (-not $projDir) { continue }
            $thPath = Join-Path $projDir (Join-Path 'bin' (Join-Path $Configuration (Join-Path $TargetFramework 'testhost.exe')))
            [void]$candidatePaths.Add($thPath)
        }
        catch {
            Write-Warning "Could not derive testhost path for '$proj': $($_.Exception.Message)"
        }
    }

    if ($candidatePaths.Count -eq 0) { return }

    # --- Determine which paths already have a firewall rule ---
    $existing = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    try {
        Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue |
            Where-Object { $_.Program -and $_.Program -like '*testhost.exe' } |
            ForEach-Object { [void]$existing.Add($_.Program) }
    }
    catch {
        Write-Warning "Could not enumerate existing firewall rules: $($_.Exception.Message)"
    }

    $missing = @($candidatePaths | Where-Object { -not $existing.Contains($_) })

    if ($missing.Count -eq 0) {
        Write-Info "Testhost firewall rules already present ($($candidatePaths.Count) path(s)) — nothing to do." 'Green'
        return
    }

    Write-Info "Adding firewall allow-rules for $($missing.Count) testhost.exe path(s)..." 'Cyan'

    # --- Build a child script that creates all missing rules in one elevated pass ---
    # We batch every missing path into a single elevated invocation so UAC prompts
    # at most once. The child re-checks existence to stay idempotent under races.
    $ruleLines = foreach ($m in $missing) {
        $safe = $m -replace "'", "''"
        @"
try {
    if (-not (Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue | Where-Object { `$_.Program -eq '$safe' })) {
        `$dn = '$ruleTag ' + [System.IO.Path]::GetFileName([System.IO.Path]::GetDirectoryName([System.IO.Path]::GetDirectoryName([System.IO.Path]::GetDirectoryName([System.IO.Path]::GetDirectoryName('$safe')))))
        New-NetFirewallRule -DisplayName `$dn -Group '$ruleTag' -Direction Inbound  -Action Allow -Program '$safe' -Profile Any -ErrorAction Stop | Out-Null
        New-NetFirewallRule -DisplayName (`$dn + ' (out)') -Group '$ruleTag' -Direction Outbound -Action Allow -Program '$safe' -Profile Any -ErrorAction Stop | Out-Null
    }
} catch { }
"@
    }

    $childScript = @"
`$ErrorActionPreference = 'Continue'
$($ruleLines -join "`n")
"@

    $tmpFile = Join-Path ([System.IO.Path]::GetTempPath()) ("botnexus-fw-{0}.ps1" -f ([guid]::NewGuid().ToString('N')))
    try {
        Set-Content -Path $tmpFile -Value $childScript -Encoding UTF8

        $pwshExe = (Get-Process -Id $PID).Path
        if (-not $pwshExe) { $pwshExe = 'pwsh.exe' }

        # Self-elevate a single child that adds all missing rules, then waits.
        $proc = Start-Process -FilePath $pwshExe `
            -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $tmpFile) `
            -Verb RunAs -PassThru -Wait -WindowStyle Hidden -ErrorAction Stop

        if ($proc.ExitCode -eq 0) {
            Write-Info "Testhost firewall rules ensured." 'Green'
        }
        else {
            Write-Warning "Elevated firewall rule setup exited with code $($proc.ExitCode) — a testhost prompt may still appear."
        }
    }
    catch {
        Write-Warning "Could not create testhost firewall rules (continuing anyway): $($_.Exception.Message)"
        Write-Warning "If a firewall popup appears during tests, approve it once for this worktree, or run this script from an elevated shell."
    }
    finally {
        Remove-Item $tmpFile -Force -ErrorAction SilentlyContinue
    }
}
