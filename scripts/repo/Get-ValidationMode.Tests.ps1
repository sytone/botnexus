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
Assert-Equal 'local' (Resolve-BotNexusValidationMode -EnvironmentValues $empty) 'Selector should default to local.'
Assert-Equal 'remote' (Resolve-BotNexusValidationMode -RequestedMode remote -EnvironmentValues $empty) 'Explicit mode should win.'
Assert-Equal 'local' (Resolve-BotNexusValidationMode -LocalFallback -EnvironmentValues ([ordered]@{ Process = 'remote'; User = $null; Machine = $null })) 'Legacy fallback should remain local.'
Assert-Equal 'remote' (Resolve-BotNexusValidationMode -EnvironmentValues ([ordered]@{ Process = 'REMOTE'; User = 'local'; Machine = 'local' })) 'Process scope should have highest environment precedence.'
Assert-Equal 'remote' (Resolve-BotNexusValidationMode -EnvironmentValues ([ordered]@{ Process = $null; User = 'remote'; Machine = 'local' })) 'User scope should survive new process startup.'
Assert-Equal 'remote' (Resolve-BotNexusValidationMode -EnvironmentValues ([ordered]@{ Process = $null; User = $null; Machine = 'remote' })) 'Machine scope should be supported.'
Assert-Throws { Resolve-BotNexusValidationMode -RequestedMode invalid -EnvironmentValues $empty } 'local.*remote' 'Invalid explicit mode should fail closed.'
Assert-Throws { Resolve-BotNexusValidationMode -EnvironmentValues ([ordered]@{ Process = 'secret-looking-invalid-value'; User = $null; Machine = $null }) } 'BOTNEXUS_VALIDATION_MODE.*local.*remote' 'Invalid environment mode should fail without echoing its value.'
Assert-Throws { Resolve-BotNexusValidationMode -RequestedMode remote -LocalFallback -EnvironmentValues $empty } 'cannot be combined' 'Conflicting legacy and explicit selectors should fail.'

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}
Write-Host 'Get-ValidationMode tests passed.' -ForegroundColor Green
exit 0
