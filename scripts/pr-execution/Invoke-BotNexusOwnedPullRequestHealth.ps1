[CmdletBinding()]
param([string]$Repository='Sytone/botnexus', [Parameter(Mandatory)][string]$Token,
    [string]$GatewayToken, [string]$GatewayUrl='http://localhost:5005', [string]$AgentId='farnsworth', [Parameter(Mandatory)][string]$StatePath,
    [string]$CachePath, [string]$AdapterPath=(Join-Path $PSScriptRoot 'templates/HostAdapter.ps1'),
    [ValidateRange(1,110)][int]$BudgetSeconds=100, [ValidateRange(1,60)][int]$RequestTimeoutSeconds=10,
    [ValidateRange(1,5)][int]$MaximumAttempts=2, [ValidateRange(0,30)][int]$RetryDelaySeconds=1,
    [ValidateRange(1,1440)][int]$RetryAfterMinutes=60)
$ErrorActionPreference='Stop'
$timer=[Diagnostics.Stopwatch]::StartNew()
. (Join-Path $PSScriptRoot 'Reconciliation.Core.ps1')
$hostAdapter=$null
try {
    $adapterArguments=@{Token=$Token;CachePath=$CachePath;StatePath=$StatePath;GatewayUrl=$GatewayUrl}
    if(-not [string]::IsNullOrWhiteSpace($GatewayToken)){$adapterArguments.GatewayToken=$GatewayToken}
    $hostAdapter=& $AdapterPath @adapterArguments
    $ctx=$hostAdapter.Context
    if($hostAdapter.ContainsKey('Production')){$ctx.clock={ $timer.ElapsedMilliseconds }.GetNewClosure();$ctx.deadline=$BudgetSeconds*1000}
    $ctx.requestTimeout=$RequestTimeoutSeconds*1000;$ctx.attempts=$MaximumAttempts;$ctx.retryDelay=$RetryDelaySeconds*1000
    $packet=Invoke-OwnedPullRequestHealth $ctx $Repository $AgentId $hostAdapter.State $hostAdapter.SaveState $RetryAfterMinutes
    & $hostAdapter.SaveCache $ctx.cache
    $null=Get-ReconciliationRemaining $ctx 'cache-save'
    $packet | ConvertTo-Json -Depth 12 -Compress
} catch {
    if(Test-ReconciliationSafeError $_.Exception.Message){throw}
    throw 'router: operation-failed'
} finally {
    $Token=$null;$GatewayToken=$null;$adapterArguments=$null
    if($hostAdapter){$hostAdapter.Context.token=$null;& $hostAdapter.Dispose}
}
