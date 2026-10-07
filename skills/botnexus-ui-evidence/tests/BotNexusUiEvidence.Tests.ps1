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

$arguments = New-UiEvidenceContainerArguments -Image 'botnexus-ui-evidence:test' -ContainerName 'evidence-fixed' -RepositoryPath 'C:\repo' -OutputVolumeName 'evidence-output' -ScenarioJson '{"prompt":"SLOW_STREAM","pagePath":"/release-history"}'
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
  $matrixNames=@('desktop-idle','mobile-idle','mobile-active','active-pre-token','active-tool-gap','focused-active','reduced-motion','returned-idle','animated-active-low','animated-active-high','preference-disabled-active')
  $manifest.captures=@($matrixNames | ForEach-Object { $captureName=$_; $captureFile="$captureName.png"; [IO.File]::WriteAllBytes((Join-Path $area $captureFile),[byte[]](1,2,3,4)); $captureHash=(Get-FileHash -LiteralPath (Join-Path $area $captureFile) -Algorithm SHA256).Hash.ToLowerInvariant(); $captureAssertions=[Collections.Generic.List[object]]::new(); $captureAssertions.Add([ordered]@{kind='browserState';target="${captureName}:aria-busy";passed=$true;detail='observed DOM state'}); if ($captureName -in @('mobile-idle','mobile-active')) { foreach ($suffix in @('route','composer-selector')) { $captureAssertions.Add([ordered]@{kind='browserState';target="${captureName}:$suffix";passed=$true;detail='actual mobile app state'}) }; $statusSuffix=if ($captureName -eq 'mobile-active') {'stable-status'} else {'stable-status-absent'}; $captureAssertions.Add([ordered]@{kind='browserState';target="${captureName}:$statusSuffix";passed=$true;detail='actual mobile app status'}) }; if ($captureName -in @('animated-active-low','animated-active-high')) { $captureAssertions.Add([ordered]@{kind='browserState';target="${captureName}:bottom-edge";passed=$true;detail='computed ::before bottom edge'}) }; if ($captureName -eq 'preference-disabled-active') { $captureAssertions.Add([ordered]@{kind='browserState';target='preference-disabled-active:static-bottom-edge';passed=$true;detail='static bottom edge'}) }; [ordered]@{name=$captureName;viewport=[ordered]@{width=390;height=844};screenshot=[ordered]@{path=$captureFile;sha256=$captureHash};assertions=@($captureAssertions)} })
  $manifest.assertions+=,[ordered]@{kind='browserState';target='animation:bottom-edge-opacity-changes';passed=$true;detail='start and midpoint differ'}
  $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $area 'evidence.json') -Encoding utf8
  Assert-True (Test-UiEvidenceOutput -OutputDirectory $area) 'A complete matching browser capture matrix should validate.'
  $validCaptures=$manifest.captures; $validAssertions=$manifest.assertions
  $manifest.captures[1].assertions=@($manifest.captures[1].assertions | Where-Object { $_.target -ne 'mobile-idle:route' }); $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $area 'evidence.json') -Encoding utf8
  Assert-Throws { Test-UiEvidenceOutput -OutputDirectory $area } 'Mobile capture.*route' 'A mobile capture without actual route evidence must fail closed.'
  $manifest.captures=$validCaptures; $manifest.assertions=@($validAssertions | Where-Object { $_.target -ne 'animation:bottom-edge-opacity-changes' }); $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $area 'evidence.json') -Encoding utf8
  Assert-Throws { Test-UiEvidenceOutput -OutputDirectory $area } 'bottom-edge opacity change assertion' 'Animation pulse without a measured low/high opacity difference must fail closed.'
  $manifest.assertions=$validAssertions; $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $area 'evidence.json') -Encoding utf8
  $manifest.captures=$manifest.captures | Where-Object { $_.name -ne 'animated-active-high' }; $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $area 'evidence.json') -Encoding utf8
  Assert-Throws { Test-UiEvidenceOutput -OutputDirectory $area } 'capture matrix must contain each required state exactly once' 'A missing capture-matrix state must fail closed.'
  $manifest.captures=@($matrixNames | ForEach-Object { $captureName=$_; $captureFile="$captureName.png"; $captureAssertions=[Collections.Generic.List[object]]::new(); $captureAssertions.Add([ordered]@{kind='browserState';target="${captureName}:aria-busy";passed=$true;detail='observed DOM state'}); if ($captureName -in @('mobile-idle','mobile-active')) { foreach ($suffix in @('route','composer-selector')) { $captureAssertions.Add([ordered]@{kind='browserState';target="${captureName}:$suffix";passed=$true;detail='actual mobile app state'}) }; $statusSuffix=if ($captureName -eq 'mobile-active') {'stable-status'} else {'stable-status-absent'}; $captureAssertions.Add([ordered]@{kind='browserState';target="${captureName}:$statusSuffix";passed=$true;detail='actual mobile app status'}) }; if ($captureName -in @('animated-active-low','animated-active-high')) { $captureAssertions.Add([ordered]@{kind='browserState';target="${captureName}:bottom-edge";passed=$true;detail='computed ::before bottom edge'}) }; if ($captureName -eq 'preference-disabled-active') { $captureAssertions.Add([ordered]@{kind='browserState';target='preference-disabled-active:static-bottom-edge';passed=$true;detail='static bottom edge'}) }; $captureHash=(Get-FileHash -LiteralPath (Join-Path $area $captureFile) -Algorithm SHA256).Hash.ToLowerInvariant(); [ordered]@{name=$captureName;viewport=[ordered]@{width=390;height=844};screenshot=[ordered]@{path=$captureFile;sha256=$captureHash};assertions=@($captureAssertions)} }); $manifest.assertions+=,[ordered]@{kind='browserState';target='animation:bottom-edge-opacity-changes';passed=$true;detail='start and midpoint differ'}; $manifest.cleanup.containerRemoved=$false; $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $area 'evidence.json') -Encoding utf8
  Assert-Throws { Test-UiEvidenceOutput -OutputDirectory $area } 'containerRemoved' 'Incomplete cleanup must fail closed.'
  $manifest.cleanup.containerRemoved=$true; $manifest.accessibility.activeRunControls.'chat-abort-btn'=''; $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $area 'evidence.json') -Encoding utf8
  Assert-Throws { Test-UiEvidenceOutput -OutputDirectory $area } 'chat-abort-btn' 'Missing rendered accessible name must fail closed.'
  $manifest.accessibility.activeRunControls.'chat-abort-btn'='Stop'; $manifest.screenshot.sha256='00'; $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $area 'evidence.json') -Encoding utf8
  Assert-Throws { Test-UiEvidenceOutput -OutputDirectory $area } 'SHA-256' 'Hash mismatch must fail closed.'
} finally { Remove-Item -LiteralPath $area -Recurse -Force -ErrorAction SilentlyContinue }

foreach ($relative in @('container/Dockerfile','container/entrypoint.mjs','container/capture.mjs','evidence.schema.json')) { Assert-True (Test-Path -LiteralPath (Join-Path $skillRoot $relative)) "Required skill asset missing: $relative" }
if (Test-Path -LiteralPath (Join-Path $skillRoot 'container/capture.mjs')) {
  $capture=[IO.File]::ReadAllText((Join-Path $skillRoot 'container/capture.mjs'))
  foreach ($needle in @('desktop-idle','mobile-idle','active-pre-token','active-tool-gap','focused-active','reduced-motion','returned-idle','animated-active-low','preference-disabled-active','aria-busy','document.activeElement','prefers-reduced-motion','page.screenshot','rendered-tool-call',"waitFor({ state: 'visible', timeout: 15000 })")) { Assert-True ($capture.Contains($needle)) "Capture matrix must assert and screenshot $needle." }
  Assert-True ($capture.Contains('scenario.captureMatrix')) 'Capture matrix must be opt-in to preserve single-capture behavior.'
  Assert-True ($capture -match 'activeRunControls') 'Matrix must retain active-run accessible-name assertions.'
  foreach ($needle in @('/mobile/agent/evidence-agent/conversation/','mobile-active','mobile-composer','mobile-composer-status','animated-active-high','opacity','getComputedStyle')) { Assert-True ($capture.Contains($needle)) "Capture matrix must prove mobile routing/state and computed bottom-edge pulse: $needle." }
  Assert-True ($capture -match 'timing\s*\*\s*0\.5') 'Animation high point must be set from half the measured iteration duration.'
  Assert-True ($capture.Contains(':bottom-edge')) 'Animation captures must record the computed bottom edge.'
  Assert-True ($capture.Contains("['animated-active-low', 0]") -and $capture.Contains("['animated-active-high', timing * 0.5]")) 'Animation captures must include low and measured-iteration high positions.'
}
if (Test-Path -LiteralPath (Join-Path $skillRoot 'container/entrypoint.mjs')) {
  $entrypoint=[IO.File]::ReadAllText((Join-Path $skillRoot 'container/entrypoint.mjs'))
  foreach ($needle in @('MATRIX_PRE_TOKEN','MATRIX_TOOL_GAP','MATRIX_LONG_ACTIVE')) { Assert-True ($entrypoint.Contains($needle)) "Deterministic integration-mock fixture missing $needle." }
}
if (Test-Path -LiteralPath (Join-Path $skillRoot 'evidence.schema.json')) {
  $schema=[IO.File]::ReadAllText((Join-Path $skillRoot 'evidence.schema.json'))
  foreach ($needle in @('captures','desktop-idle','mobile-idle','mobile-active','active-pre-token','active-tool-gap','focused-active','reduced-motion','returned-idle','animated-active-low','animated-active-high','preference-disabled-active')) { Assert-True ($schema.Contains($needle)) "Evidence schema must define matrix state $needle." }
}
if (Test-Path -LiteralPath (Join-Path $skillRoot 'container/Dockerfile')) { $docker=[IO.File]::ReadAllText((Join-Path $skillRoot 'container/Dockerfile')); Assert-True ($docker -match 'mcr\.microsoft\.com/playwright/dotnet:v1\.44\.0-jammy') 'Dockerfile must use the proven Playwright image.'; Assert-True ($docker -match 'COPY --from=dotnet10 /usr/share/dotnet') 'Dockerfile must copy .NET 10.'; Assert-True ($docker -match 'SOURCE_COMMIT') 'Dockerfile must receive the source revision.'; Assert-True ($docker -match 'sha256sum.*source-tree') 'Dockerfile must hash the copied source tree.'; Assert-True ($docker -match 'microsoft\.playwright/1\.44\.0/\.playwright/package') 'Dockerfile must reuse the restored Playwright driver instead of npm downloading it.'; Assert-True ($docker -match 'BotNexus\.Extensions\.Channels\.SignalR') 'Evidence image must publish the portal extension.'; Assert-True ($docker -match '/app/source') 'Evidence image must contain a local source repository for source-backed Portal pages.'; Assert-True ($docker -notmatch 'find src/extensions') 'Evidence image must not publish unrelated extensions.' }
$dockerIgnore=[IO.File]::ReadAllText((Join-Path (Split-Path -Parent $skillRoot) '..' '.dockerignore')); Assert-True ($dockerIgnore -notmatch '(?m)^tools/$') 'Container context must include the production source-generator project.'; Assert-True ($dockerIgnore -notmatch '(?m)^docs/$') 'Container context must include desktop portal guide content.'
if (Test-Path -LiteralPath (Join-Path $skillRoot 'container/capture.mjs')) { $capture=[IO.File]::ReadAllText((Join-Path $skillRoot 'container/capture.mjs')); foreach ($needle in @('SLOW_STREAM','streaming-badge','chat-abort-btn','pageerror','accessibility','scenario.pagePath',"waitUntil: 'load'")) { Assert-True ($capture.Contains($needle)) "Capture script must contain $needle." }; Assert-True (-not $capture.Contains("waitUntil: 'networkidle'")) 'Capture must not wait for network idle while SignalR is connected.' }
if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }; exit 1 }
Write-Host 'BotNexus UI evidence tests passed.' -ForegroundColor Green
exit 0
