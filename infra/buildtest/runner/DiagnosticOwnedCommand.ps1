# Dedicated containment anchor. Launch is gated until the supervisor establishes
# a Windows job or verifies a Linux session. Arguments are data, never shell text.
param([Parameter(Mandatory)][string]$Configuration)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$config=Get-Content -LiteralPath $Configuration -Raw | ConvertFrom-Json
while (-not [IO.File]::Exists($config.release)) { Start-Sleep -Milliseconds 10 }
$command=$null
try {
    $info=[Diagnostics.ProcessStartInfo]::new([string]$config.file); $info.UseShellExecute=$false
    foreach ($argument in $config.arguments) { [void]$info.ArgumentList.Add([string]$argument) }
    $command=[Diagnostics.Process]::Start($info)
    [IO.File]::WriteAllText($config.identity,(@{processId=$command.Id; startUtc=$command.StartTime.ToUniversalTime().ToString('o')} | ConvertTo-Json -Compress))
    $command.WaitForExit()
    [IO.File]::WriteAllText($config.result,([string]$command.ExitCode))
} catch { [Console]::Error.WriteLine($_.Exception.Message); [IO.File]::WriteAllText($config.error,$_.Exception.Message); [IO.File]::WriteAllText($config.result,'1') }
finally { if ($null -ne $command) { $command.Dispose() } }
# Stay alive even when the launcher exits; containment remains independently killable.
[Threading.ManualResetEvent]::new($false).WaitOne() | Out-Null
