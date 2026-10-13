[CmdletBinding()]
param([string]$Repository='Sytone/botnexus', [Parameter(Mandatory)][string]$Token,
    [string]$CachePath, [string]$AdapterPath=(Join-Path $PSScriptRoot 'templates/HostAdapter.ps1'),
    [ValidateRange(1,110)][int]$BudgetSeconds=100, [ValidateRange(1,60)][int]$RequestTimeoutSeconds=10,
    [ValidateRange(1,5)][int]$MaximumAttempts=2, [ValidateRange(0,30)][int]$RetryDelaySeconds=1)
$ErrorActionPreference='Stop'
$timer=[Diagnostics.Stopwatch]::StartNew()
. (Join-Path $PSScriptRoot 'Reconciliation.Core.ps1')
$hostAdapter=$null
try {
    $hostAdapter=& $AdapterPath -Token $Token -CachePath $CachePath -StatePath '' -GatewayUrl ''
    $ctx=$hostAdapter.Context
    # Production uses the entrypoint clock so adapter startup and state loading consume the same budget.
    if($hostAdapter.ContainsKey('Production')){$ctx.clock={ $timer.ElapsedMilliseconds }.GetNewClosure();$ctx.deadline=$BudgetSeconds*1000}
    $ctx.requestTimeout=$RequestTimeoutSeconds*1000;$ctx.attempts=$MaximumAttempts;$ctx.retryDelay=$RetryDelaySeconds*1000
    $packet=Get-OwnedPullRequestHealth $ctx $Repository
    & $hostAdapter.SaveCache $ctx.cache
    $null=Get-ReconciliationRemaining $ctx 'cache-save'
    $packet | ConvertTo-Json -Depth 12 -Compress
} catch {
    if(Test-ReconciliationSafeError $_.Exception.Message){throw}
    throw 'collector: operation-failed'
} finally {
    $Token=$null
    if($hostAdapter){$hostAdapter.Context.token=$null;& $hostAdapter.Dispose}
}
