<#
.SYNOPSIS
    Leases Windows Firewall rules for BotNexus testhost binaries.

.DESCRIPTION
    Ensure starts one elevated background helper that creates tagged rules,
    prunes orphaned legacy BotNexus testhost rules, and waits while tests run.
    Cleanup signals that same helper to remove every rule owned by the lease.
    The helper also observes the calling process, so it cleans up if a test run
    is interrupted before Cleanup executes.

    This avoids both interactive testhost firewall prompts and an accumulating
    collection of per-worktree rules. It is best-effort and a no-op outside
    Windows; firewall policy never changes the test command's result.

.PARAMETER ProjectPath
    Test project paths used to derive testhost.exe locations.

.PARAMETER Configuration
    Build configuration segment in the output path. Defaults to Debug.

.PARAMETER TargetFramework
    Target framework segment in the output path. Defaults to net10.0.

.PARAMETER Action
    Ensure starts the lease. Cleanup releases it.

.PARAMETER LeasePath
    Shared lease directory used by Ensure and Cleanup for synchronization.

.PARAMETER Quiet
    Suppresses informational output. Warnings are still emitted.
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromPipeline = $true, Position = 0)]
    [string[]]$ProjectPath,

    [string]$Configuration = 'Debug',

    [string]$TargetFramework = 'net10.0',

    [ValidateSet('Ensure', 'Cleanup')]
    [string]$Action = 'Ensure',

    [Parameter(Mandatory = $true)]
    [string]$LeasePath,

    [switch]$Quiet
)

begin {
    $ruleGroup = 'BotNexus-Testhost'
    $collected = [System.Collections.Generic.List[string]]::new()

    function Write-Info {
        param([string]$Message, [string]$Color = 'DarkGray')
        if (-not $Quiet) { Write-Host $Message -ForegroundColor $Color }
    }

    $isWindowsOs = $true
    if (Get-Variable -Name 'IsWindows' -ErrorAction SilentlyContinue) {
        $isWindowsOs = $IsWindows
    }
}

process {
    foreach ($path in @($ProjectPath)) {
        if ($path) { $collected.Add($path) }
    }
}

end {
    if (-not $isWindowsOs) {
        Write-Info 'Not Windows — skipping testhost firewall management.'
        return
    }

    $readyFile = Join-Path $LeasePath 'ready'
    $releaseFile = Join-Path $LeasePath 'release'
    $doneFile = Join-Path $LeasePath 'done'

    if ($Action -eq 'Cleanup') {
        if (-not (Test-Path $LeasePath)) { return }
        try {
            New-Item -ItemType File -Path $releaseFile -Force | Out-Null
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            while (-not (Test-Path $doneFile) -and [DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 100
            }
            if (-not (Test-Path $doneFile)) {
                Write-Warning 'Timed out waiting for testhost firewall lease cleanup; the elevated helper will continue cleanup.'
            }
            else {
                Write-Info 'Testhost firewall rules released.' 'Green'
            }
        }
        finally {
            if (Test-Path $doneFile) {
                Remove-Item $LeasePath -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        return
    }

    if ($collected.Count -eq 0) { return }

    $candidatePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($project in $collected) {
        try {
            $projectDirectory = Split-Path -Parent $project
            if (-not $projectDirectory) { continue }
            $testhostPath = Join-Path $projectDirectory (Join-Path 'bin' (Join-Path $Configuration (Join-Path $TargetFramework 'testhost.exe')))
            [void]$candidatePaths.Add([System.IO.Path]::GetFullPath($testhostPath))
        }
        catch {
            Write-Warning "Could not derive testhost path for '$project': $($_.Exception.Message)"
        }
    }

    if ($candidatePaths.Count -eq 0) { return }

    New-Item -ItemType Directory -Path $LeasePath -Force | Out-Null
    $encodedPaths = @($candidatePaths | ForEach-Object {
        [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($_))
    })
    $encodedPathLiteral = ($encodedPaths | ForEach-Object { "'$_'" }) -join ', '
    $safeGroup = $ruleGroup -replace "'", "''"
    $safeLeasePath = $LeasePath -replace "'", "''"
    $leaseId = [IO.Path]::GetFileName($LeasePath) -replace '[^A-Za-z0-9-]', ''
    $callerProcess = Get-Process -Id $PID
    $callerProcessId = $callerProcess.Id
    $callerStartTicks = $callerProcess.StartTime.ToUniversalTime().Ticks

    $childScript = @"
`$ErrorActionPreference = 'Stop'
`$ruleGroup = '$safeGroup'
`$leasePath = '$safeLeasePath'
`$callerProcessId = $callerProcessId
`$callerStartTicks = $callerStartTicks
`$targetPaths = @($encodedPathLiteral) | ForEach-Object { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(`$_)) }
`$createdRuleNames = [System.Collections.Generic.List[string]]::new()
try {
    # Remove persistent rules from the previous implementation and lease rules
    # abandoned by terminated runs. Active lease helpers retain their rules.
    foreach (`$rule in @(Get-NetFirewallRule -Group `$ruleGroup -ErrorAction SilentlyContinue)) {
        if (`$rule.Name -match '^BotNexus-Testhost-(\d+)-(\d+)-') {
            `$ownerPid = [int]`$Matches[1]
            `$ownerStartTicks = [long]`$Matches[2]
            `$owner = Get-Process -Id `$ownerPid -ErrorAction SilentlyContinue
            if (-not `$owner -or `$owner.StartTime.ToUniversalTime().Ticks -ne `$ownerStartTicks) {
                `$rule | Remove-NetFirewallRule -ErrorAction SilentlyContinue
            }
        }
        elseif (`$rule.Name -notlike 'BotNexus-Testhost-*') {
            `$rule | Remove-NetFirewallRule -ErrorAction SilentlyContinue
        }
    }

    `$index = 0
    foreach (`$program in `$targetPaths) {
        `$projectName = Split-Path (Split-Path (Split-Path (Split-Path `$program -Parent) -Parent) -Parent) -Leaf
        `$displayName = "BotNexus testhost lease - `$projectName"
        foreach (`$direction in @('Inbound', 'Outbound')) {
            `$ruleName = 'BotNexus-Testhost-$callerProcessId-$callerStartTicks-$leaseId-' + `$index
            New-NetFirewallRule -Name `$ruleName -DisplayName `$displayName -Group `$ruleGroup -Direction `$direction -Action Allow -Program `$program -Profile Any | Out-Null
            `$createdRuleNames.Add(`$ruleName)
            `$index++
        }
    }

    New-Item -ItemType File -Path (Join-Path `$leasePath 'ready') -Force | Out-Null
    while (-not (Test-Path (Join-Path `$leasePath 'release'))) {
        `$caller = Get-Process -Id `$callerProcessId -ErrorAction SilentlyContinue
        if (-not `$caller -or `$caller.StartTime.ToUniversalTime().Ticks -ne `$callerStartTicks) { break }
        Start-Sleep -Milliseconds 250
    }
}
finally {
    foreach (`$ruleName in `$createdRuleNames) {
        Remove-NetFirewallRule -Name `$ruleName -ErrorAction SilentlyContinue
    }
    New-Item -ItemType File -Path (Join-Path `$leasePath 'done') -Force | Out-Null
}
"@

    $process = $null
    try {
        $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childScript))
        $pwshExecutable = (Get-Process -Id $PID).Path
        if (-not $pwshExecutable) { $pwshExecutable = 'pwsh.exe' }
        $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
        $isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        $startParameters = @{
            FilePath = $pwshExecutable
            ArgumentList = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encodedCommand)
            PassThru = $true
            WindowStyle = 'Hidden'
            ErrorAction = 'Stop'
        }
        if (-not $isAdministrator) { $startParameters.Verb = 'RunAs' }
        $process = Start-Process @startParameters

        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (-not (Test-Path $readyFile) -and -not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 100
            $process.Refresh()
        }
        if (-not (Test-Path $readyFile)) {
            throw "Elevated firewall lease did not become ready (exit code: $(if ($process.HasExited) { $process.ExitCode } else { 'timeout' }))."
        }
        Write-Info "Testhost firewall rules leased for $($candidatePaths.Count) path(s); orphaned rules pruned." 'Green'
    }
    catch {
        if ($null -ne $process -and -not $process.HasExited) {
            New-Item -ItemType File -Path $releaseFile -Force -ErrorAction SilentlyContinue | Out-Null
            $releaseDeadline = [DateTime]::UtcNow.AddSeconds(5)
            while (-not (Test-Path $doneFile) -and -not $process.HasExited -and [DateTime]::UtcNow -lt $releaseDeadline) {
                Start-Sleep -Milliseconds 100
                $process.Refresh()
            }
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        }
        New-Item -ItemType File -Path $doneFile -Force -ErrorAction SilentlyContinue | Out-Null
        Write-Warning "Could not lease testhost firewall rules (continuing anyway): $($_.Exception.Message)"
    }
}
