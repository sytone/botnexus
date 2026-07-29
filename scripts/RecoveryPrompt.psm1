<#
.SYNOPSIS
    Priming-prompt and Copilot-invocation helpers for the gateway break-glass recovery script.

.DESCRIPTION
    `recover-gateway.ps1` hands off to the GitHub Copilot CLI after gathering diagnostics.
    The two decisions that handoff makes -- what priming text the assistant receives, and
    which CLI flags launch it -- are pure functions of the resolved paths and gateway URL,
    so they live here where they can be asserted directly instead of only through a live
    outage. See issue #2455.
#>

Set-StrictMode -Version Latest

function Get-GatewayPort {
    <#
    .SYNOPSIS
        Extracts the TCP port from a gateway base URL.

    .DESCRIPTION
        The recovery script needs the port in three places (the listening-socket probe, the
        priming prompt's process-discovery guidance, and diagnostics). Deriving it once here
        keeps those in agreement; issue #2455 called out a second hardcoded copy as a defect.

    .PARAMETER GatewayUrl
        The gateway base URL, e.g. 'http://localhost:5005'.
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)]
        [string]$GatewayUrl
    )

    $uri = $null
    if ([Uri]::TryCreate($GatewayUrl, [UriKind]::Absolute, [ref]$uri) -and $uri.Port -gt 0) {
        return [int]$uri.Port
    }

    throw "Cannot determine a gateway port from URL '$GatewayUrl'."
}

function New-RecoveryPrimingPrompt {
    <#
    .SYNOPSIS
        Builds the platform-aware priming text handed to the break-glass assistant.

    .DESCRIPTION
        Beyond the fault taxonomy, the text must explain the platform's *topology*: the CLI
        process and the gateway host process are distinct, discovery is by listening port
        rather than by process name, and the build/source root is not the config/state root.
        A live recovery run failed on exactly those points (issue #2455, finding 2).

    .PARAMETER RepoPath
        The source/build root the script was invoked from.

    .PARAMETER ConfigDir
        The runtime config/state root, normally '~/.botnexus'.

    .PARAMETER HealthUrl
        The resolved health endpoint URL.

    .PARAMETER GatewayPort
        The gateway listening port, as resolved by Get-GatewayPort.

    .PARAMETER ReportPath
        Path to the diagnostic report already written by the recovery script.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$RepoPath,
        [Parameter(Mandatory)][string]$ConfigDir,
        [Parameter(Mandatory)][string]$HealthUrl,
        [Parameter(Mandatory)][int]$GatewayPort,
        [Parameter(Mandatory)][string]$ReportPath
    )

    return @"
You are helping recover the BotNexus gateway, which is currently DOWN (its /health
endpoint is not returning 200). The built-in BotNexus helper agent (Nexus Trailguide)
cannot help because the gateway that hosts it is the thing that is down -- so you are
the break-glass assistant.

ABOUT BOTNEXUS
- BotNexus is a .NET 10 application: a Blazor Server UI + SignalR messaging gateway that
  hosts AI agents. The source/build root for this run is this directory ($RepoPath).
- The gateway is launched by the CLI ('botnexus.exe', a dotnet global tool). Startup runs
  either the native apphost 'BotNexus.Gateway.Api.exe' or 'dotnet BotNexus.Gateway.Api.dll'.
- Health endpoint: $HealthUrl. The CLI only shows a generic 10s health-check timeout,
  which HIDES the real fault -- always read the newest log for the true exception.

TOPOLOGY -- READ THIS BEFORE YOU GO LOOKING FOR ANYTHING
1. PROCESS IDENTITY: the CLI and the gateway host are DIFFERENT PROCESSES.
   - 'botnexus.exe' is the CLI/launcher. It starts the gateway and then returns; it is NOT
     the long-running listener. Do not diagnose it as though it were the gateway.
   - 'BotNexus.Gateway.Api.exe' (native apphost) is the long-running gateway host process.
   - Name-based discovery is UNRELIABLE: searching for 'botnexus' finds the CLI (the wrong
     process), and 'Get-Process -Name dotnet' finds NOTHING now that the native apphost is
     used instead of 'dotnet BotNexus.Gateway.Api.dll'.
   - The reliable discovery is BY LISTENING PORT:
       Get-NetTCPConnection -State Listen -LocalPort $GatewayPort | Select-Object LocalAddress,LocalPort,OwningProcess -Unique
     then resolve the owning PID with Get-Process -Id <OwningProcess>.
2. TWO ROOTS -- never conflate them:
   - SOURCE/BUILD ROOT ($RepoPath): where the solution is built and updated from. This is
     whichever checkout this script was run from. It contains source, never runtime state.
   - CONFIG/STATE ROOT ($ConfigDir): logs/ (hourly 'botnexus-YYYYMMDDHH.log'),
     extensions/ (deployed extension folders), agents/, sessions/, secrets/. It is never
     built from and contains no source. Looking for source here, or logs in the build root,
     is a dead end.
3. VERSION GAP IS EXPECTED: the checkout you are sitting in may not be the checkout the
   running gateway was built from. A commit gap between the dev checkout and the
   built/deployed source is NORMAL and is NOT by itself the fault. Do not chase it as the
   defect unless the log evidence actually points at a version mismatch.

KNOWN RECURRING CRASH CLASS (check this first)
- Extensions are loaded in an isolated ExtensionAssemblyLoadContext
  (src/gateway/BotNexus.Gateway/Extensions/ExtensionAssemblyLoadContext.cs).
- If an extension ships a PRIVATE copy of an assembly that defines a host-registered
  contract (e.g. IConfiguration, IFileSystem via System.IO.Abstractions), the type identity
  diverges from the host, DI stops recognising it, and the HOST ABORTS ON STARTUP.
  Signature: "System.InvalidOperationException: Body was inferred..." or a
  FileNotFoundException / "Could not load file or assembly ...".
- Fix pattern: add the assembly to the host-shared allow-list in
  ExtensionAssemblyLoadContext (HostAssemblies). See PR #2218 and issue #2184 for precedent.
- Tracking issues for a permanent fix: #2219 (categorical unification) and #2220 (boot smoke gate).

A DIAGNOSTIC REPORT HAS ALREADY BEEN GATHERED for you at:
  $ReportPath
Read it first -- it has the /health result, process/port state, the newest log's
ERR/FTL lines, the deployed extension set, and git HEAD.

WHAT I NEED FROM YOU
1. Read the diagnostic report and the newest gateway log to identify the real fault.
2. Explain the root cause in plain terms.
3. If it is the extension load-context class above (or any clear regression), propose a
   minimal fix, and offer to file a GitHub issue on sytone/botnexus (use 'gh') and/or open
   a PR following the repo's worktree + Conventional Commits workflow (see AGENTS.md).
4. Do NOT restart, rebuild, or push anything without asking me to confirm first.

Start by reading $ReportPath and the newest log under $ConfigDir/logs, then tell me what broke.
"@
}

function Test-CopilotInteractiveSupport {
    <#
    .SYNOPSIS
        Reports whether the installed Copilot CLI advertises the interactive-prompt flag.

    .DESCRIPTION
        Issue #2455 requires the handoff to fail with an actionable message rather than
        silently degrading to a one-shot when the flag is unavailable. The help text is the
        only contract the CLI exposes, so it is what we probe.

    .PARAMETER HelpText
        The captured output of 'copilot --help'.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$HelpText
    )

    return $HelpText -match '(?m)(^|\s)(-i,\s*)?--interactive\b'
}

function Get-CopilotInteractiveArgument {
    <#
    .SYNOPSIS
        Builds the Copilot CLI argument list for an interactive, primed recovery session.

    .DESCRIPTION
        '--prompt' is documented by the CLI as "Execute a prompt in non-interactive mode
        (exits after completion)". '--interactive <prompt>' starts a session and runs the
        prompt as its opening turn, which is what recovery needs. The '--add-dir' grant is
        preserved so the assistant can read the config/state root.

    .PARAMETER ConfigDir
        The config/state root to grant file access to.

    .PARAMETER Prompt
        The priming text to deliver as the opening turn.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)][string]$ConfigDir,
        [Parameter(Mandatory)][string]$Prompt
    )

    return @('--add-dir', $ConfigDir, '--interactive', $Prompt)
}

Export-ModuleMember -Function Get-GatewayPort, New-RecoveryPrimingPrompt, Test-CopilotInteractiveSupport, Get-CopilotInteractiveArgument
