[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Get-ValidationMode.ps1')

$failures = [Collections.Generic.List[string]]::new()
function Assert-Equal([object]$Expected, [object]$Actual, [string]$Message) {
    if ($Expected -ne $Actual) { $failures.Add("$Message Expected '$Expected', got '$Actual'.") }
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    try { & $Action; $failures.Add("$Message Expected an exception.") }
    catch { if ($_.Exception.Message -notmatch $Pattern) { $failures.Add("$Message Unexpected exception: $($_.Exception.Message)") } }
}

$empty = [ordered]@{ Process = $null; User = $null; Machine = $null }
# Both injection points must be supplied to describe an unconfigured caller. Passing only
# -EnvironmentValues leaves -LegacyFallbackValues bound to the REAL environment, so the case
# silently reads the developer's own BOTNEXUS_VALIDATION_LOCAL_FALLBACK - the same ambient-
# leakage defect as #2400. On a host with LOCAL_FALLBACK=1 that turns this assertion into a
# no-op that passes for the wrong reason.
$noLegacy = [ordered]@{ Process = $null; User = $null; Machine = $null }
# #2158: an unconfigured caller must get REMOTE. Local gates leaked orphan gateway processes
# that starved the live gateway and stole its cron jobs, so local is opt-in only. This
# assertion is the fence: flipping the default back flips this test red.
Assert-Equal 'remote' (Resolve-BotNexusValidationMode -EnvironmentValues $empty -LegacyFallbackValues $noLegacy) 'Selector should default to remote.'
Assert-Equal 'local' (Resolve-BotNexusValidationMode -RequestedMode local -EnvironmentValues $empty -LegacyFallbackValues $noLegacy) 'Explicit local should remain available as an opt-in.'
Assert-Equal 'local' (Resolve-BotNexusValidationMode -EnvironmentValues ([ordered]@{ Process = 'local'; User = $null; Machine = $null }) -LegacyFallbackValues $noLegacy) 'Environment-selected local must still be honoured.'
Assert-Equal 'local' (Resolve-BotNexusValidationMode -EnvironmentValues $empty -LegacyFallbackValues ([ordered]@{ Process = '1'; User = $null; Machine = $null })) 'Legacy LOCAL_FALLBACK=1 escape hatch must survive the default flip.'
Assert-Equal 'remote' (Resolve-BotNexusValidationMode -RequestedMode remote -EnvironmentValues $empty -LegacyFallbackValues $noLegacy) 'Explicit mode should win.'
Assert-Equal 'local' (Resolve-BotNexusValidationMode -LocalFallback -EnvironmentValues ([ordered]@{ Process = 'remote'; User = $null; Machine = $null })) 'Legacy fallback should remain local.'
Assert-Equal 'remote' (Resolve-BotNexusValidationMode -EnvironmentValues ([ordered]@{ Process = 'REMOTE'; User = 'local'; Machine = 'local' })) 'Process scope should have highest environment precedence.'
Assert-Equal 'remote' (Resolve-BotNexusValidationMode -EnvironmentValues ([ordered]@{ Process = $null; User = 'remote'; Machine = 'local' })) 'User scope should survive new process startup.'
Assert-Equal 'remote' (Resolve-BotNexusValidationMode -EnvironmentValues ([ordered]@{ Process = $null; User = $null; Machine = 'remote' })) 'Machine scope should be supported.'
Assert-Throws { Resolve-BotNexusValidationMode -RequestedMode invalid -EnvironmentValues $empty -LegacyFallbackValues $noLegacy } 'local.*remote' 'Invalid explicit mode should fail closed.'
Assert-Throws { Resolve-BotNexusValidationMode -EnvironmentValues ([ordered]@{ Process = 'secret-looking-invalid-value'; User = $null; Machine = $null }) -LegacyFallbackValues $noLegacy } 'BOTNEXUS_VALIDATION_MODE.*local.*remote' 'Invalid environment mode should fail without echoing its value.'
Assert-Throws { Resolve-BotNexusValidationMode -RequestedMode remote -LocalFallback -EnvironmentValues $empty -LegacyFallbackValues $noLegacy } 'cannot be combined' 'Conflicting legacy and explicit selectors should fail.'

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}
Write-Host 'Get-ValidationMode tests passed.' -ForegroundColor Green
exit 0
