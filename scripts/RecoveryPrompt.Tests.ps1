<#
.SYNOPSIS
    Assertions for the gateway recovery priming prompt and Copilot invocation contract.

.DESCRIPTION
    Covers issue #2455: the handoff must open an interactive Copilot session rather than a
    one-shot completion, and the priming text must explain the build/config topology.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'RecoveryPrompt.psm1') -Force

$failures = [Collections.Generic.List[string]]::new()

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $failures.Add($Message) }
}

function Assert-Equal([object]$Expected, [object]$Actual, [string]$Message) {
    if ($Expected -ne $Actual) { $failures.Add("$Message Expected '$Expected', got '$Actual'.") }
}

$recoverScript = Join-Path $PSScriptRoot 'recover-gateway.ps1'
$recoverSource = Get-Content -LiteralPath $recoverScript -Raw

$repoPath = Join-Path ([IO.Path]::GetTempPath()) 'botnexus-src-root'
$configDir = Join-Path ([IO.Path]::GetTempPath()) '.botnexus'
$reportPath = Join-Path $configDir 'recovery-report.md'
$gatewayUrl = 'http://localhost:5005'
$healthUrl = "$gatewayUrl/health"

# --- Get-GatewayPort ---------------------------------------------------------

Assert-Equal 5005 (Get-GatewayPort -GatewayUrl 'http://localhost:5005') 'An explicit port should be parsed from the gateway URL.'
Assert-Equal 5005 (Get-GatewayPort -GatewayUrl 'http://localhost:5005/') 'A trailing slash should not change the parsed port.'
Assert-Equal 8443 (Get-GatewayPort -GatewayUrl 'https://gateway.internal:8443/base') 'A port should be parsed from a URL with a path segment.'
Assert-Equal 80 (Get-GatewayPort -GatewayUrl 'http://localhost') 'An implicit http port should resolve to the scheme default.'

$portParseError = $null
try { Get-GatewayPort -GatewayUrl 'not-a-url' | Out-Null } catch { $portParseError = $_ }
Assert-True ($null -ne $portParseError) 'An unparseable gateway URL should throw rather than yield a bogus port.'

# --- Interactive invocation (issue #2455, finding 1) -------------------------

$port = Get-GatewayPort -GatewayUrl $gatewayUrl
$prompt = New-RecoveryPrimingPrompt -RepoPath $repoPath -ConfigDir $configDir -HealthUrl $healthUrl -GatewayPort $port -ReportPath $reportPath
$copilotArgs = @(Get-CopilotInteractiveArgument -ConfigDir $configDir -Prompt $prompt)

Assert-True ($copilotArgs -contains '--interactive') 'The Copilot handoff must request an interactive session.'
Assert-True (-not ($copilotArgs -contains '--prompt')) 'The Copilot handoff must not use the one-shot --prompt flag.'
Assert-True (-not ($copilotArgs -contains '-p')) 'The Copilot handoff must not use the one-shot -p flag.'
Assert-True ($copilotArgs -contains '--add-dir') 'The --add-dir grant must be preserved so the assistant can read the config/state root.'

$addDirIndex = [Array]::IndexOf($copilotArgs, '--add-dir')
Assert-Equal $configDir $copilotArgs[$addDirIndex + 1] 'The --add-dir grant must point at the config/state root.'

$interactiveIndex = [Array]::IndexOf($copilotArgs, '--interactive')
Assert-Equal $prompt $copilotArgs[$interactiveIndex + 1] 'The priming text must be delivered as the interactive session opening turn.'

Assert-True ($recoverSource -match '(?m)--interactive|Get-CopilotInteractiveArgument') 'recover-gateway.ps1 must invoke Copilot through the interactive argument builder.'
Assert-True ($recoverSource -notmatch '(?m)copilot\.Source\s+--add-dir\s+\$ConfigDir\s+--prompt') 'recover-gateway.ps1 must no longer invoke Copilot with the one-shot --prompt form.'

# --- Unsupported-flag detection ---------------------------------------------

$modernHelp = @'
Options:
  --add-dir <directory>        Add a directory to the allowed list
  -i, --interactive <prompt>   Start interactive mode and automatically execute this prompt
  -p, --prompt <text>          Execute a prompt in non-interactive mode (exits after completion)
'@
$legacyHelp = @'
Options:
  --add-dir <directory>        Add a directory to the allowed list
  -p, --prompt <text>          Execute a prompt and exit
'@

Assert-Equal $true (Test-CopilotInteractiveSupport -HelpText $modernHelp) 'A CLI advertising --interactive must be detected as supported.'
Assert-Equal $false (Test-CopilotInteractiveSupport -HelpText $legacyHelp) 'A CLI without --interactive must be detected as unsupported.'
Assert-Equal $false (Test-CopilotInteractiveSupport -HelpText '') 'Empty help output must be treated as unsupported rather than assumed good.'
Assert-True ($recoverSource -match 'Test-CopilotInteractiveSupport') 'recover-gateway.ps1 must probe interactive support before launching.'
Assert-True ($recoverSource -match 'exit 1') 'recover-gateway.ps1 must fail rather than silently degrade when interactive is unsupported.'

# --- Topology in the priming prompt (issue #2455, finding 2) -----------------

Assert-True ($prompt -match 'botnexus\.exe') 'The priming prompt must name the CLI process.'
Assert-True ($prompt -match 'BotNexus\.Gateway\.Api\.exe') 'The priming prompt must name the gateway host process.'
Assert-True ($prompt -cmatch 'DIFFERENT PROCESSES') 'The priming prompt must state that the CLI and gateway host are distinct processes.'
Assert-True ($prompt -match 'Get-NetTCPConnection') 'The priming prompt must give port-based process discovery.'
Assert-True ($prompt -cmatch 'UNRELIABLE') 'The priming prompt must warn that name-based process discovery is unreliable.'
Assert-True ($prompt -match "Get-Process -Name dotnet") 'The priming prompt must call out that dotnet name discovery finds nothing under the native apphost.'
Assert-True ($prompt -cmatch 'SOURCE/BUILD ROOT') 'The priming prompt must identify the source/build root as a labelled topology root.'
Assert-True ($prompt -cmatch 'CONFIG/STATE ROOT') 'The priming prompt must identify the config/state root as a labelled topology root.'
Assert-True ($prompt -match 'never conflate them') 'The priming prompt must instruct that the two roots are never conflated.'
Assert-True ($prompt -match 'contains no source') 'The priming prompt must state that the config/state root holds no source.'
Assert-True ($prompt -match 'commit gap') 'The priming prompt must state that a commit gap between checkouts is expected.'
Assert-True ($prompt -match 'NOT by itself the fault') 'The priming prompt must state that a version gap is not itself the defect.'

# The port used for discovery guidance must come from the same resolved value as $healthUrl.
Assert-True ($prompt -match "-LocalPort 5005") 'The priming prompt must embed the resolved gateway port in the discovery command.'
$altPrompt = New-RecoveryPrimingPrompt -RepoPath $repoPath -ConfigDir $configDir -HealthUrl 'http://localhost:7777/health' -GatewayPort (Get-GatewayPort -GatewayUrl 'http://localhost:7777') -ReportPath $reportPath
Assert-True ($altPrompt -match '-LocalPort 7777') 'A non-default gateway port must flow through to the discovery command rather than being hardcoded.'
Assert-True ($altPrompt -notmatch '-LocalPort 5005') 'The discovery port must not be hardcoded to the default.'

# --- Preserved priming content ----------------------------------------------

Assert-True ($prompt -match 'ExtensionAssemblyLoadContext') 'The priming prompt must retain the known crash-class guidance.'
Assert-True ($prompt -match [regex]::Escape($reportPath)) 'The priming prompt must reference the generated diagnostic report.'
Assert-True ($prompt -match [regex]::Escape($healthUrl)) 'The priming prompt must reference the resolved health endpoint.'
Assert-True ($prompt -match [regex]::Escape($configDir)) 'The priming prompt must reference the config/state root path.'
Assert-True ($prompt -match [regex]::Escape($repoPath)) 'The priming prompt must reference the source/build root path.'
Assert-True ($prompt -match 'Do NOT restart, rebuild, or push') 'The priming prompt must retain the confirmation gate.'

# --- Messaging matches behaviour --------------------------------------------

Assert-True ($recoverSource -match 'Launching an interactive Copilot session') 'The launch message must describe an interactive session.'

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    Write-Host "RecoveryPrompt tests FAILED: $($failures.Count) assertion(s)." -ForegroundColor Red
    exit 1
}

Write-Host "RecoveryPrompt tests passed ($($MyInvocation.MyCommand.Name))." -ForegroundColor Green
