[CmdletBinding()]
param([Parameter(Mandatory)][string]$StatePath,[Parameter(Mandatory)][string]$OutputPath,[string]$EventTracePath)
Set-StrictMode -Version Latest; $ErrorActionPreference='Stop'
$plan = & (Join-Path $PSScriptRoot 'Get-MaintenanceDispatchPlan.ps1') -StatePath $StatePath
$now=[DateTimeOffset]::UtcNow
$events=@([pscustomobject]@{sequence=1;at=$now.ToString('O');event='cycle-planned';cycleId=$plan.cycleId})
$i=1
foreach($d in $plan.dispatch){$i++;$events += [pscustomobject]@{sequence=$i;at=$now.AddMilliseconds($i).ToString('O');event='worker-dispatched';id=$d.id;lane=$d.lane}}
$result=[pscustomobject]@{cycleId=$plan.cycleId;trigger=$plan.trigger;dispatch=@($plan.dispatch);blockers=@($plan.blockers);events=$events;telemetry=$plan.telemetry}
$dir=Split-Path $OutputPath -Parent;if($dir){New-Item $dir -ItemType Directory -Force|Out-Null};$result|ConvertTo-Json -Depth 20|Set-Content $OutputPath -Encoding utf8NoBOM
if($EventTracePath){$events|ForEach-Object{$_|ConvertTo-Json -Compress}|Set-Content $EventTracePath -Encoding utf8NoBOM}
$result

