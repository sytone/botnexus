[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StatePath,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$EventTracePath,
    [string]$CorrelationId = [Guid]::NewGuid().ToString('N'),
    [int]$StartSequence = 0,
    [string]$Provenance = 'Invoke-MaintenanceOrchestration'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$plan = & (Join-Path $PSScriptRoot 'Get-MaintenanceDispatchPlan.ps1') -StatePath $StatePath
$events = [Collections.Generic.List[object]]::new()
$sequence = $StartSequence
function Add-OrchestrationEvent([string]$Name, [string]$Id, [string]$Lane, [object]$Details) {
    $script:sequence++
    $events.Add([pscustomobject]@{
        sequence = $script:sequence
        timestampUtc = [DateTimeOffset]::UtcNow.ToString('O')
        event = $Name
        id = $Id
        lane = $Lane
        cycleId = $plan.cycleId
        correlationId = $CorrelationId
        provenance = $Provenance
        details = $Details
    })
}
Add-OrchestrationEvent 'cycle-planned' $plan.cycleId 'planner' ([pscustomobject]@{ trigger = $plan.trigger; blockerCount = $plan.blockers.Count })
foreach ($dispatch in $plan.dispatch) {
    Add-OrchestrationEvent 'worker-dispatched' $dispatch.id $dispatch.lane ([pscustomobject]@{ files = @($dispatch.files); validation = $dispatch.validation })
}
foreach ($blocker in $plan.blockers) {
    Add-OrchestrationEvent 'dispatch-blocked' $blocker.id $blocker.lane ([pscustomobject]@{ reason = $blocker.reason })
}
$result = [pscustomobject]@{
    cycleId = $plan.cycleId
    trigger = $plan.trigger
    correlationId = $CorrelationId
    provenance = $Provenance
    dispatch = @($plan.dispatch)
    blockers = @($plan.blockers)
    idleCapacity = $plan.idleCapacity
    events = @($events)
    telemetry = $plan.telemetry
}
$directory = Split-Path $OutputPath -Parent
if ($directory) { New-Item $directory -ItemType Directory -Force | Out-Null }
$result | ConvertTo-Json -Depth 30 | Set-Content $OutputPath -Encoding utf8NoBOM
if ($EventTracePath) { $events | ForEach-Object { $_ | ConvertTo-Json -Depth 20 -Compress } | Set-Content $EventTracePath -Encoding utf8NoBOM }
$result
