[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$skillRoot = Split-Path -Parent $PSScriptRoot
$modulePath = Join-Path $skillRoot 'BotNexusUiEvidence.psm1'
$failures = [Collections.Generic.List[string]]::new()
function Assert-True([bool]$Value,[string]$Message) { if (-not $Value) { $failures.Add($Message) } }
function Assert-Equal($Expected,$Actual,[string]$Message) { if ($Expected -ne $Actual) { $failures.Add("$Message Expected '$Expected', got '$Actual'.") } }
function Assert-Throws([scriptblock]$Action,[string]$Pattern,[string]$Message) { try { & $Action; $failures.Add("$Message Expected an exception.") } catch { if ($_.Exception.Message -notmatch $Pattern) { $failures.Add("$Message Unexpected exception: $($_.Exception.Message)") } } }

if (-not (Test-Path -LiteralPath $modulePath)) { Write-Error "Production module missing: $modulePath"; exit 1 }
Import-Module $modulePath -Force

Assert-Equal 'docker' (Resolve-UiEvidenceContainerRuntime -CommandResolver { param($n) if ($n -eq 'docker') { [pscustomobject]@{Source='docker'} } }) 'Docker should be preferred.'
Assert-Equal 'podman' (Resolve-UiEvidenceContainerRuntime -CommandResolver { param($n) if ($n -eq 'podman') { [pscustomobject]@{Source='podman'} } }) 'Podman should be the fallback.'
Assert-Throws { Resolve-UiEvidenceContainerRuntime -CommandResolver { $null } } 'Docker or Podman' 'Missing runtime must fail early.'

$arguments = New-UiEvidenceContainerArguments -Image 'botnexus-ui-evidence:test' -ContainerName 'evidence-fixed' -RepositoryPath 'C:\repo' -OutputVolumeName 'evidence-output' -ScenarioJson '{"prompt":"SLOW_STREAM"}'
Assert-True ($arguments -notcontains 'C:\repo') 'Run arguments must not mount or expose the host repository path.'
Assert-True ($arguments -contains '--network=none') 'The disposable runtime must have no external network.'
Assert-True (($arguments -join ' ') -match 'source=evidence-output,target=/evidence-output') 'Only a named output volume may cross the container boundary.'
Assert-True ($arguments -contains '--read-only') 'The runtime container filesystem must be read-only outside tmpfs/output.'

$calls = [Collections.Generic.List[string]]::new()
$fake = { param([string]$Runtime,[string[]]$Arguments,[switch]$AllowFailure) $calls.Add("$Runtime $($Arguments -join ' ')"); if ($Arguments[0] -eq 'create') { return [pscustomobject]@{ExitCode=0;Output='cid'} }; if ($Arguments[0] -eq 'start') { return [pscustomobject]@{ExitCode=7;Output='boom'} }; return [pscustomobject]@{ExitCode=0;Output=''} }
Assert-Throws { Invoke-UiEvidenceContainer -Runtime 'docker' -Image 'image' -ContainerName 'evidence-fixed' -OutputVolumeName 'out-fixed' -ScenarioJson '{}' -RuntimeInvoker $fake } 'exited 7' 'Container failure must propagate.'
Assert-True (@($calls | Where-Object { $_ -match ' rm .*evidence-fixed' }).Count -eq 1) 'Container removal must run after failure.'
Assert-True (@($calls | Where-Object { $_ -match ' volume rm .*out-fixed' }).Count -eq 1) 'Output volume removal must run after failure.'

$area = Join-Path ([IO.Path]::GetTempPath()) ('ui-evidence-test-' + [guid]::NewGuid().ToString('N')); [IO.Directory]::CreateDirectory($area) | Out-Null
try {
  [IO.File]::WriteAllBytes((Join-Path $area 'portal.png'), [byte[]](1,2,3,4))
  $hash=(Get-FileHash -LiteralPath (Join-Path $area 'portal.png') -Algorithm SHA256).Hash.ToLowerInvariant()
  $manifest=[ordered]@{schemaVersion='1.0';source=[ordered]@{commit='0123456789012345678901234567890123456789';tree=('ab' * 32)};scenario=[ordered]@{promptKey='SLOW_STREAM';name='active-stream'};viewport=[ordered]@{width=1440;height=1000};screenshot=[ordered]@{path='portal.png';sha256=$hash};assertions=@([ordered]@{kind='selector';target='[data-testid="streaming-badge"]';passed=$true},[ordered]@{kind='activeRun';target='[data-testid="chat-abort-btn"]';passed=$true});accessibility=[ordered]@{activeRunControls=[ordered]@{'chat-steer-btn'='Steer';'chat-redirect-btn'='Redirect';'chat-followup-btn'='Follow Up';'chat-abort-btn'='Stop'}};timestamps=[ordered]@{startedUtc='2026-01-01T00:00:00.000Z';capturedUtc='2026-01-01T00:00:01.000Z';completedUtc='2026-01-01T00:00:02.000Z'};cleanup=[ordered]@{gatewayStopped=$true;browserClosed=$true;temporaryDataRemoved=$true;containerRemoved=$false}}
  $manifest.cleanup.containerRemoved=$true
  $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $area 'evidence.json') -Encoding utf8
  Assert-True (Test-UiEvidenceOutput -OutputDirectory $area) 'A complete matching evidence contract should validate.'
  $manifest.cleanup.containerRemoved=$false; $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $area 'evidence.json') -Encoding utf8
  Assert-Throws { Test-UiEvidenceOutput -OutputDirectory $area } 'containerRemoved' 'Incomplete cleanup must fail closed.'
  $manifest.cleanup.containerRemoved=$true; $manifest.accessibility.activeRunControls.'chat-abort-btn'=''; $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $area 'evidence.json') -Encoding utf8
  Assert-Throws { Test-UiEvidenceOutput -OutputDirectory $area } 'chat-abort-btn' 'Missing rendered accessible name must fail closed.'
  $manifest.accessibility.activeRunControls.'chat-abort-btn'='Stop'; $manifest.screenshot.sha256='00'; $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $area 'evidence.json') -Encoding utf8
  Assert-Throws { Test-UiEvidenceOutput -OutputDirectory $area } 'SHA-256' 'Hash mismatch must fail closed.'
} finally { Remove-Item -LiteralPath $area -Recurse -Force -ErrorAction SilentlyContinue }

foreach ($relative in @('container/Dockerfile','container/entrypoint.mjs','container/capture.mjs','evidence.schema.json')) { Assert-True (Test-Path -LiteralPath (Join-Path $skillRoot $relative)) "Required skill asset missing: $relative" }
if (Test-Path -LiteralPath (Join-Path $skillRoot 'container/Dockerfile')) { $docker=[IO.File]::ReadAllText((Join-Path $skillRoot 'container/Dockerfile')); Assert-True ($docker -match 'mcr\.microsoft\.com/playwright/dotnet:v1\.44\.0-jammy') 'Dockerfile must use the proven Playwright image.'; Assert-True ($docker -match 'COPY --from=dotnet10 /usr/share/dotnet') 'Dockerfile must copy .NET 10.'; Assert-True ($docker -match 'SOURCE_COMMIT') 'Dockerfile must receive the source revision.'; Assert-True ($docker -match 'sha256sum.*source-tree') 'Dockerfile must hash the copied source tree.'; Assert-True ($docker -match 'microsoft\.playwright/1\.44\.0/\.playwright/package') 'Dockerfile must reuse the restored Playwright driver instead of npm downloading it.'; Assert-True ($docker -match 'BotNexus\.Extensions\.Channels\.SignalR') 'Evidence image must publish the portal extension.'; Assert-True ($docker -notmatch 'find src/extensions') 'Evidence image must not publish unrelated extensions.' }
$dockerIgnore=[IO.File]::ReadAllText((Join-Path (Split-Path -Parent $skillRoot) '..' '.dockerignore')); Assert-True ($dockerIgnore -notmatch '(?m)^tools/$') 'Container context must include the production source-generator project.'; Assert-True ($dockerIgnore -notmatch '(?m)^docs/$') 'Container context must include desktop portal guide content.'
if (Test-Path -LiteralPath (Join-Path $skillRoot 'container/capture.mjs')) { $capture=[IO.File]::ReadAllText((Join-Path $skillRoot 'container/capture.mjs')); foreach ($needle in @('SLOW_STREAM','streaming-badge','chat-abort-btn','pageerror','accessibility',"waitUntil: 'load'")) { Assert-True ($capture.Contains($needle)) "Capture script must contain $needle." }; Assert-True (-not $capture.Contains("waitUntil: 'networkidle'")) 'Capture must not wait for network idle while SignalR is connected.' }
if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }; exit 1 }
Write-Host 'BotNexus UI evidence tests passed.' -ForegroundColor Green
exit 0
