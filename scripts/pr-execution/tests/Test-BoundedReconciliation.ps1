[CmdletBinding()]
param([string]$ScratchRoot = (Join-Path $PSScriptRoot 'scratch'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$core = Join-Path (Split-Path $PSScriptRoot -Parent) 'Reconciliation.Core.ps1'
if (-not (Test-Path $core)) {
    @{ total=1; passed=0; failed=1; failures=@('missing bounded reconciliation core contract') } | ConvertTo-Json -Compress
    exit 1
}
. $core
$script:passed = 0
$script:failures = [Collections.Generic.List[string]]::new()
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Case([string]$Name, [scriptblock]$Test) {
    try { & $Test; $script:passed++ } catch { $script:failures.Add("${Name}: $($_.Exception.Message)") }
}
function PR([int]$Number=1, [string]$Author='agent-farnsworth[bot]') {
    @{ number=$Number; user=@{login=$Author}; state='open'; title="PR $Number"; html_url="https://example.invalid/pr/$Number"; body='Fixes #42'; draft=$false; updated_at='2026-01-01T00:00:00Z'; head=@{ref='fix';sha="head$Number"}; base=@{ref='main';sha='captured-base'}; mergeable=$true; mergeable_state='clean' }
}
function Check([long]$Id=1,[string]$Conclusion='success') { @{id=$Id;name='CORE';status='completed';conclusion=$Conclusion;started_at='2026-01-01T00:00:00Z';html_url='https://example.invalid/check'} }
function Response($Body, [int]$Status=200, [hashtable]$Headers=@{}) { @{status=$Status;body=$Body;headers=$Headers} }
function Setup {
    $global:boundedFixture = @{ms=0L;calls=[Collections.Generic.List[object]]::new();prs=@((PR));checks=@((Check));etag='"checks-1"';hook=$null;gatewayHook=$null;saves=0;state=@{};cache=@{}}
    $transport = {
        param($Request)
        $global:boundedFixture.calls.Add($Request)
        $global:boundedFixture.ms += 5
        if ($global:boundedFixture.hook) { $override = & $global:boundedFixture.hook $Request; if ($null -ne $override) { return $override } }
        if ($Request.scope -eq 'gateway') {
            if ($global:boundedFixture.gatewayHook) { return & $global:boundedFixture.gatewayHook $Request }
            switch -Regex ($Request.path) {
                '^/api/conversations\?' { return Response @(@{conversationId='existing';title='42 - implementation';status='Active';agentId='farnsworth';updatedAt='2026-01-01'}) }
                '/messages$' { return Response @{conversationId='existing';sessionId='session';wake=$true} 202 }
                '^/api/conversations/existing$' { return Response @{conversationId='existing';agentId='farnsworth';status='Active';activeSessionId='session'} }
                default { throw 'Unexpected gateway operation' }
            }
        }
        switch -Regex ($Request.path) {
            '/pulls\?state=open' { return Response @($global:boundedFixture.prs) }
            '/pulls/(\d+)$' { return Response ($global:boundedFixture.prs | Where-Object number -eq ([int]$Matches[1]) | Select-Object -First 1) }
            '/commits/main$' { return Response @{sha='base-now'} }
            '/check-runs\?' {
                if ($Request.headers.ContainsKey('If-None-Match') -and $Request.headers['If-None-Match'] -eq $global:boundedFixture.etag) { return Response $null 304 @{ETag=$global:boundedFixture.etag} }
                return Response @{total_count=$global:boundedFixture.checks.Count;check_runs=@($global:boundedFixture.checks)} 200 @{ETag=$global:boundedFixture.etag}
            }
            default { throw "Unexpected request: $($Request.path)" }
        }
    }
    New-ReconciliationContext -Transport $transport -Clock { $global:boundedFixture.ms } -Delay { param($Ms) $global:boundedFixture.ms += $Ms } -Token 'fixture-secret' -Cache $global:boundedFixture.cache -BudgetMilliseconds 100000
}
function New-FixtureAdapter {
    param($Token)
    Assert ($Token -eq 'fixture-secret') 'token not scoped'
    @{Context=$global:boundedWrapperContext;State=$global:boundedFixture.state;SaveState={param($s) $global:boundedFixture.saves++};SaveCache={param($c)};Dispose={}}
}
function Collect($Context) { Get-OwnedPullRequestHealth -Context $Context -Repository 'Sytone/botnexus' }
function Route($Context) { Invoke-OwnedPullRequestHealth -Context $Context -Repository 'Sytone/botnexus' -AgentId 'farnsworth' -State $global:boundedFixture.state -SaveState { param($State) $global:boundedFixture.saves++ } }
function ExpectFailure([scriptblock]$Action,[string]$Stage) {
    $errorText = $null
    try { & $Action | Out-Null } catch { $errorText = $_.Exception.Message }
    Assert ($null -ne $errorText) 'must fail closed'
    Assert ($errorText -match $Stage) "missing stage $Stage in $errorText"
    Assert ($errorText -notmatch 'fixture-secret|sensitive-host|private/path') 'error leaked detail'
}
Case 'packet compatibility and exact authors' {
    $ctx=Setup; $global:boundedFixture.prs=@((PR),(PR 2 'app/agent-farnsworth'),(PR 3 'agent-farnsworth'),(PR 4 'external'))
    $packet=Collect $ctx
    Assert ($packet.scanned -eq 2) 'exact authors only'
    Assert ($packet.healthyPullRequests.Count -eq 2 -and $packet.attention.Count -eq 0) 'healthy packet'
    Assert ($packet.healthyPullRequests[0].base.sha -eq 'base-now') 'observed base tip'
    Assert (($packet.Keys | Sort-Object) -join ',' -eq 'attention,generatedAt,healthyPullRequests,repository,scanned') 'successful packet shape'
}
Case 'slow endpoint consumes overall deadline' {
    $ctx=Setup; $ctx.deadline=40
    $global:boundedFixture.hook={param($r) $global:boundedFixture.ms += $r.timeoutMilliseconds; throw 'timeout fixture-secret sensitive-host private/path'}
    ExpectFailure { Collect $ctx } 'inventory'
    Assert ($global:boundedFixture.calls.Count -eq 1) 'no request beyond deadline'
    Assert ($global:boundedFixture.calls[0].timeoutMilliseconds -le 40) 'timeout clamped to budget'
}
Case 'transient retries then recovery' {
    $ctx=Setup; $global:boundedFixture.hook={param($r) if($global:boundedFixture.calls.Count -eq 1){return Response $null 503}}
    $packet=Collect $ctx
    Assert ($packet.scanned -eq 1) 'recovered inventory'
    Assert ($global:boundedFixture.ms -ge 1000) 'bounded backoff executed'
}
Case 'authentication is not retried' {
    $ctx=Setup; $global:boundedFixture.hook={param($r) return Response @{detail='fixture-secret'} 401}
    ExpectFailure { Collect $ctx } 'inventory'
    Assert ($global:boundedFixture.calls.Count -eq 1) '401 no retry'
}
Case 'partial pull page is rejected' {
    $ctx=Setup; $global:boundedFixture.hook={param($r) if($r.path -match '/pulls\?'){return Response @((PR)) 200 @{Link='<https://api.github.com/repos/Sytone/botnexus/pulls?state=open&per_page=100&page=2>; rel="next"'}}}
    ExpectFailure { Collect $ctx } 'inventory'
}
Case 'duplicate pull identity is rejected' {
    $ctx=Setup; $global:boundedFixture.prs=@((PR),(PR))
    ExpectFailure { Collect $ctx } 'inventory'
}
Case 'inventory page drift is rejected' {
    $ctx=Setup; $global:boundedFixture.hook={param($r) if($r.stage -eq 'inventory-readback'){return Response @((PR),(PR 2))}}
    ExpectFailure { Collect $ctx } 'inventory-readback'
}
Case 'head drift is rejected' {
    $ctx=Setup; $global:boundedFixture.hook={param($r) if($r.stage -eq 'pr-readback'){ $p=PR; $p.head.sha='new-head'; return Response $p }}
    ExpectFailure { Collect $ctx } 'pr-readback'
}
Case 'base drift is rejected' {
    $ctx=Setup; $global:boundedFixture.hook={param($r) if($r.stage -eq 'base-readback'){return Response @{sha='new-base'}}}
    ExpectFailure { Collect $ctx } 'base-readback'
}
Case 'partial check pages rejected' {
    $ctx=Setup; $global:boundedFixture.hook={param($r) if($r.path -match '/check-runs'){return Response @{total_count=2;check_runs=@((Check))}}}
    ExpectFailure { Collect $ctx } 'checks'
}
Case 'duplicate check identity rejected' {
    $ctx=Setup; $global:boundedFixture.checks=@((Check),(Check))
    ExpectFailure { Collect $ctx } 'checks'
}
Case 'check page drift rejected' {
    $ctx=Setup; $global:boundedFixture.hook={param($r) if($r.stage -eq 'checks-readback'){return Response @{total_count=1;check_runs=@((Check 1 'failure'))} 200 @{ETag='"changed"'}}}
    ExpectFailure { Collect $ctx } 'checks-readback'
    Assert ($global:boundedFixture.cache.Count -eq 0) 'failed scan never publishes cache'
}
Case 'ETag revalidated every scan detects same-head failure' {
    $ctx=Setup; $first=Collect $ctx
    Assert ($first.healthyPullRequests.Count -eq 1) 'first healthy'
    $global:boundedFixture.checks=@((Check 1 'failure')); $global:boundedFixture.etag='"checks-2"'
    $second=Collect $ctx
    Assert ($second.attention.Count -eq 1 -and 'failed-checks' -in $second.attention[0].problemCodes) 'same-head changed checks detected'
    Assert (@($global:boundedFixture.calls | Where-Object { $_.stage -eq 'checks' -and $_.headers.ContainsKey('If-None-Match') }).Count -ge 1) 'second scan conditional request'
}
Case 'ETag unchanged reuses only validated payload' {
    $ctx=Setup; Collect $ctx | Out-Null; $packet=Collect $ctx
    Assert ($packet.healthyPullRequests.Count -eq 1) '304 payload valid'
    Assert (@($global:boundedFixture.calls | Where-Object { $_.stage -eq 'checks' }).Count -eq 2) 'checks requested on every scan'
}
Case 'base keyed cache no head-only reuse' {
    $ctx=Setup; Collect $ctx | Out-Null
    $global:boundedFixture.hook={param($r) if($r.path -match '/commits/main$'){return Response @{sha='other-base'}}}
    Collect $ctx | Out-Null
    $requests=@($global:boundedFixture.calls | Where-Object stage -eq 'checks')
    Assert (-not $requests[-1].headers.ContainsKey('If-None-Match')) 'new base cannot reuse cache'
}
Case 'transient collector failure preserves last good state' {
    $ctx=Setup; $global:boundedFixture.state['1']=@{marker='last-good';routedAt='2026-01-01T00:00:00Z'}
    $global:boundedFixture.hook={param($r) return Response $null 503}
    ExpectFailure { Route $ctx } 'inventory'
    Assert ($global:boundedFixture.state['1'].marker -eq 'last-good' -and $global:boundedFixture.saves -eq 0) 'last-good preserved'
}
Case 'successful wake and readback advance marker once' {
    $ctx=Setup; $global:boundedFixture.checks=@((Check 1 'failure'))
    $packet=Route $ctx
    Assert ($packet.routed.Count -eq 1) 'one routed'
    Assert ($global:boundedFixture.state['1'].marker -eq 'head1|base-now|failed-checks') 'success marker'
    Assert (-not $global:boundedFixture.state['1'].ContainsKey('pending')) 'uncertainty cleared after readback'
    Route $ctx | Out-Null
    Assert (@($global:boundedFixture.calls | Where-Object method -eq 'POST').Count -eq 1) 'deduplicated successful wake'
}
Case 'uncertain wake persisted and never resent' {
    $ctx=Setup; $global:boundedFixture.checks=@((Check 1 'failure')); $global:boundedFixture.state['1']=@{marker='last-good';routedAt='2026-01-01T00:00:00Z'}
    $global:boundedFixture.gatewayHook={param($r) if($r.method -eq 'POST'){Assert ($global:boundedFixture.saves -gt 0) 'write ahead persistence'; throw 'timeout fixture-secret'}; return Response @(@{conversationId='existing';title='42 - implementation';status='Active';agentId='farnsworth';updatedAt='2026-01-01'})}
    ExpectFailure { Route $ctx } 'wake'
    Assert ($global:boundedFixture.state['1'].marker -eq 'last-good' -and $global:boundedFixture.state['1'].pending.operation -eq 'wake') 'prior marker and uncertainty retained'
    ExpectFailure { Route $ctx } 'uncertain'
    Assert (@($global:boundedFixture.calls | Where-Object method -eq 'POST').Count -eq 1) 'no duplicate wake'
}
Case 'uncertain create persisted and never recreated' {
    $ctx=Setup; $global:boundedFixture.checks=@((Check 1 'failure'))
    $global:boundedFixture.gatewayHook={param($r) if($r.method -eq 'POST'){Assert ($global:boundedFixture.saves -gt 0) 'create write ahead'; throw 'timeout'}; return Response @()}
    ExpectFailure { Route $ctx } 'create'
    Assert ($global:boundedFixture.state['1'].pending.operation -eq 'create') 'create uncertainty persisted'
    ExpectFailure { Route $ctx } 'uncertain'
    Assert (@($global:boundedFixture.calls | Where-Object method -eq 'POST').Count -eq 1) 'no duplicate conversation'
}
Case 'readback failure cannot mark successful wake' {
    $ctx=Setup; $global:boundedFixture.checks=@((Check 1 'failure'))
    $global:boundedFixture.gatewayHook={param($r) if($r.path -match '/messages$'){return Response @{conversationId='existing';sessionId='session';wake=$true} 202}; if($r.path -eq '/api/conversations/existing'){return Response @{conversationId='existing';agentId='farnsworth';status='Active';activeSessionId='wrong'}}; return Response @(@{conversationId='existing';title='42 - implementation';status='Active';agentId='farnsworth';updatedAt='2026-01-01'})}
    ExpectFailure { Route $ctx } 'wake-readback'
    Assert ($global:boundedFixture.state['1'].pending.operation -eq 'wake' -and -not $global:boundedFixture.state['1'].ContainsKey('marker')) 'unverified wake is not successful'
}
Case 'gateway operations share collector deadline' {
    $ctx=Setup; $global:boundedFixture.checks=@((Check 1 'failure')); $ctx.deadline=100
    $global:boundedFixture.gatewayHook={param($r) $global:boundedFixture.ms += $r.timeoutMilliseconds; throw 'timeout'}
    ExpectFailure { Route $ctx } 'conversations'
    $r=@($global:boundedFixture.calls | Where-Object scope -eq 'gateway')[0]
    Assert ($r.timeoutMilliseconds -lt 100) 'collector budget was not reset'
    Assert (@($global:boundedFixture.calls | Where-Object method -eq 'POST').Count -eq 0) 'no side effect after deadline'
}
Case 'portable wrapper adapter integration' {
    $dir=Join-Path $ScratchRoot ([guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($dir) | Out-Null
    try {
        $ctx=Setup
        $adapter=Join-Path $dir 'adapter.ps1'
        [IO.File]::WriteAllText($adapter, 'param($Token,$StatePath,$CachePath,$GatewayUrl) New-FixtureAdapter $Token')
        $global:boundedWrapperContext=$ctx
        $root=Split-Path $PSScriptRoot -Parent
        $json=& (Join-Path $root 'Get-BotNexusOwnedPullRequestHealth.ps1') -Token 'fixture-secret' -AdapterPath $adapter -CachePath (Join-Path $dir 'cache.json')
        $packet=$json | ConvertFrom-Json
        Assert ($packet.scanned -eq 1) 'collector entrypoint JSON'
        $global:boundedFixture.checks=@((Check 1 'failure')); $global:boundedFixture.etag='"changed"'
        $json=& (Join-Path $root 'Invoke-BotNexusOwnedPullRequestHealth.ps1') -Token 'fixture-secret' -AdapterPath $adapter -StatePath (Join-Path $dir 'state.json')
        $packet=$json | ConvertFrom-Json
        Assert ($packet.status -eq 'ok' -and $packet.routed.Count -eq 1 -and $packet.archived.Count -eq 0) 'router entrypoint compatibility'
        Assert (@($global:boundedFixture.calls | Where-Object path -match 'issues|DELETE').Count -eq 0) 'no closed issue sweep'
    } finally { if(Test-Path $dir){Remove-Item -LiteralPath $dir -Recurse -Force} }
}
Case 'real host adapter isolated persistence and exclusive ownership' {
    $dir=Join-Path $ScratchRoot ([guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($dir) | Out-Null
    $host1=$null;$host2=$null
    try {
        $ctx=Setup
        $path=Join-Path $dir 'state.json';$cache=Join-Path $dir 'cache.json'
        $template=Join-Path (Split-Path $PSScriptRoot -Parent) 'templates/HostAdapter.ps1'
        $host1=& $template -Token 'fixture-secret' -GatewayToken 'gateway-secret' -StatePath $path -CachePath $cache -GatewayUrl 'http://unused.invalid' -TransportOverride $ctx.transport
        & $host1.SaveState @{ '1'=@{marker='last-good';routedAt='2026-01-01T00:00:00Z';pending=@{operation='wake';conversationId='existing'}} }
        & $host1.SaveCache @{ 'exact-head-base-endpoint'=@{etag='"etag"';body=@{total_count=1;check_runs=@((Check))};headers=@{ETag='"etag"'}} }
        $saved=[IO.File]::ReadAllText($path)
        Assert ($saved -notmatch 'fixture-secret|gateway-secret') 'credentials never persisted in state'
        Assert ([IO.File]::ReadAllText($cache) -notmatch 'fixture-secret|gateway-secret') 'credentials never persisted in cache'
        ExpectFailure { & $template -Token 'fixture-secret' -StatePath $path -CachePath $cache -GatewayUrl 'http://unused.invalid' } 'host-adapter'
        & $host1.Dispose;$host1=$null
        $host2=& $template -Token 'fixture-secret' -GatewayToken 'gateway-secret' -StatePath $path -CachePath $cache -GatewayUrl 'http://unused.invalid' -TransportOverride $ctx.transport
        Assert ($host2.State['1'].marker -eq 'last-good' -and $host2.State['1'].pending.operation -eq 'wake') 'durable uncertainty survives reload'
        Assert ($host2.Context.cache['exact-head-base-endpoint'].etag -eq '"etag"') 'cache reload'
        Assert (@(Get-ChildItem $dir -Filter '*.tmp').Count -eq 0) 'atomic write temp cleaned'
    } finally {
        if($host1){& $host1.Dispose};if($host2){& $host2.Dispose}
        if(Test-Path $dir){Remove-Item -LiteralPath $dir -Recurse -Force}
    }
}
Case 'multi-page pull and check inventory includes terminal pages' {
    $ctx=Setup
    $global:boundedFixture.hook={param($r)
        if($r.path -match '/pulls\?') {
            if($r.path -match 'page=1$'){return Response @(1..100 | ForEach-Object {PR $_ 'external'}) 200 @{Link='<https://api.github.com/unused?page=2>; rel="next"'}}
            return Response @((PR 101))
        }
        if($r.path -match '/pulls/101$'){return Response (PR 101)}
        if($r.path -match '/check-runs') {
            if($r.path -match 'page=1$'){return Response @{total_count=101;check_runs=@(1..100 | ForEach-Object {Check $_})} 200 @{Link='<https://api.github.com/unused?page=2>; rel="next"'}}
            return Response @{total_count=101;check_runs=@((Check 101 'failure'))}
        }
    }
    $packet=Collect $ctx
    Assert ($packet.scanned -eq 1 -and $packet.attention[0].number -eq 101) 'all PR pages scanned'
    Assert ($packet.attention[0].checks.failed.Count -eq 1) 'last check page included'
    Assert (@($global:boundedFixture.calls | Where-Object { $_.path -match 'page=2$' }).Count -eq 4) 'both inventories independently revalidated'
}
Case 'retry recovery on following scan does not consume last-good marker' {
    $ctx=Setup;$global:boundedFixture.state['1']=@{marker='last-good';routedAt='2026-01-01T00:00:00Z'}
    $global:boundedFixture.hook={param($r) return Response $null 503}
    ExpectFailure { Route $ctx } 'inventory'
    $global:boundedFixture.hook=$null;$global:boundedFixture.checks=@((Check 1 'failure'))
    $packet=Route $ctx
    Assert ($packet.routed.Count -eq 1 -and $global:boundedFixture.state['1'].marker -eq 'head1|base-now|failed-checks') 'recovery routes once'
}
Case 'injected wall clock controls packet and retry markers' {
    $ctx=Setup;$now=[datetimeoffset]'2026-02-01T00:00:00Z'
    $ctx.wallClock={ $now }.GetNewClosure()
    $packet=Collect $ctx
    Assert ($packet.generatedAt -eq $now) 'packet time injected'
    $global:boundedFixture.checks=@((Check 1 'failure'));$global:boundedFixture.etag='"failure"'
    Route $ctx | Out-Null
    Assert ([datetimeoffset]$global:boundedFixture.state['1'].routedAt -eq $now) 'routing marker time injected'
}
Case 'empty repository is complete and router compatible' {
    $ctx=Setup;$global:boundedFixture.prs=@()
    $packet=Route $ctx
    Assert ($packet.scanned -eq 0 -and $packet.routed.Count -eq 0) 'empty complete scan'
    Assert (@($global:boundedFixture.calls | Where-Object method -eq 'POST').Count -eq 0) 'empty scan no mutation'
}
Case 'unbacked 304 fails closed' {
    $ctx=Setup;$global:boundedFixture.hook={param($r) if($r.stage -eq 'checks'){return Response $null 304}}
    ExpectFailure { Collect $ctx } 'checks'
}
Case 'successful create verified before wake' {
    $ctx=Setup;$global:boundedFixture.checks=@((Check 1 'failure'))
    $global:boundedFixture.gatewayHook={param($r)
        if($r.path -match '^/api/conversations\?'){return Response @()}
        if($r.path -eq '/api/conversations' -and $r.method -eq 'POST'){return Response @{conversationId='created'} 201}
        if($r.path -match '/messages$'){return Response @{conversationId='created';sessionId='session';wake=$true} 202}
        return Response @{conversationId='created';agentId='farnsworth';status='Active';activeSessionId='session'}
    }
    $packet=Route $ctx
    Assert ($packet.routed.Count -eq 1 -and $packet.routed[0].conversationId -eq 'created') 'created route verified'
    Assert (@($global:boundedFixture.calls | Where-Object stage -eq 'create-readback').Count -eq 1) 'create independently read back'
}
Case 'failed gateway list preserves last good marker' {
    $ctx=Setup;$global:boundedFixture.checks=@((Check 1 'failure'));$global:boundedFixture.state['1']=@{marker='last-good';routedAt='2026-01-01T00:00:00Z'}
    $global:boundedFixture.gatewayHook={param($r) return Response $null 503}
    ExpectFailure { Route $ctx } 'conversations'
    Assert ($global:boundedFixture.state['1'].marker -eq 'last-good' -and $global:boundedFixture.saves -eq 0) 'gateway transient preservation'
}
Case 'latest check rerun wins without erasing unrelated failures' {
    $ctx=Setup;$older=Check 1 'failure';$newer=Check 2 'success';$other=Check 3 'failure';$other.name='OTHER'
    $global:boundedFixture.checks=@($older,$newer,$other)
    $packet=Collect $ctx
    Assert ($packet.attention[0].checks.total -eq 2 -and $packet.attention[0].checks.failed.Count -eq 1 -and $packet.attention[0].checks.failed[0].name -eq 'OTHER') 'latest checks grouped'
}
Case 'entrypoint using real host template and isolated paths' {
    $dir=Join-Path $ScratchRoot ([guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($dir) | Out-Null
    try {
        $ctx=Setup;$global:boundedFixture.checks=@((Check 1 'failure'))
        $global:boundedTemplateTransport=$ctx.transport
        $global:boundedTemplatePath=Join-Path (Split-Path $PSScriptRoot -Parent) 'templates/HostAdapter.ps1'
        $adapter=Join-Path $dir 'adapter.ps1'
        [IO.File]::WriteAllText($adapter,'param($Token,$StatePath,$CachePath,$GatewayUrl) & $global:boundedTemplatePath -Token $Token -StatePath $StatePath -CachePath $CachePath -GatewayUrl $GatewayUrl -TransportOverride $global:boundedTemplateTransport')
        $entrypoint=Join-Path (Split-Path $PSScriptRoot -Parent) 'Invoke-BotNexusOwnedPullRequestHealth.ps1'
        $statePath=Join-Path $dir 'state.json';$cachePath=Join-Path $dir 'cache.json'
        $json=& $entrypoint -Token 'fixture-secret' -AdapterPath $adapter -StatePath $statePath -CachePath $cachePath
        $packet=$json|ConvertFrom-Json
        Assert ($packet.routed.Count -eq 1) 'real host wrapper routed'
        $saved=ConvertFrom-Json -AsHashtable -InputObject ([IO.File]::ReadAllText($statePath))
        Assert ($saved['1'].marker -eq 'head1|base-now|failed-checks') 'real wrapper durable marker'
        Assert ([IO.File]::ReadAllText($cachePath) -notmatch 'fixture-secret') 'cache credential free'
        $json=& $entrypoint -Token 'fixture-secret' -AdapterPath $adapter -StatePath $statePath -CachePath $cachePath
        $packet=$json|ConvertFrom-Json
        Assert ($packet.routed.Count -eq 0) 'disk dedupe across invocations'
        Assert (@($global:boundedFixture.calls|Where-Object method -eq 'POST').Count -eq 1) 'one wake across reloaded host'
    } finally {if(Test-Path $dir){Remove-Item -LiteralPath $dir -Recurse -Force}}
}
Case 'unknown completed check results require attention' {
    foreach($conclusion in @('future-result','', $null)) {
        $ctx=Setup;$check=Check;$check.conclusion=$conclusion;$global:boundedFixture.checks=@($check)
        $packet=Collect $ctx
        Assert ($packet.healthyPullRequests.Count -eq 0 -and $packet.attention.Count -eq 1) 'unknown conclusion must not be healthy'
        Assert ('unknown-check-result' -in $packet.attention[0].problemCodes) 'unknown check code'
    }
}
Case 'unknown mergeability requires attention' {
    foreach($combination in @(@{mergeable=$null;state='unknown'},@{mergeable=$null;state='clean'},@{mergeable=$true;state='unknown'},@{mergeable=$true;state=''})) {
        $ctx=Setup;$global:boundedFixture.prs[0].mergeable=$combination.mergeable;$global:boundedFixture.prs[0].mergeable_state=$combination.state
        $packet=Collect $ctx
        Assert ($packet.healthyPullRequests.Count -eq 0 -and 'mergeability-unknown' -in $packet.attention[0].problemCodes) 'null mergeability must not be healthy'
    }
}
Case 'known successful conclusions and pending remain normal' {
    foreach($conclusion in @('success','neutral','skipped')) {
        $ctx=Setup;$global:boundedFixture.checks=@((Check 1 $conclusion));$packet=Collect $ctx
        Assert ($packet.healthyPullRequests.Count -eq 1) 'known success remains normal'
    }
    $ctx=Setup;$check=Check;$check.status='in_progress';$check.conclusion=$null;$global:boundedFixture.checks=@($check)
    $packet=Collect $ctx
    Assert ($packet.healthyPullRequests.Count -eq 1 -and $packet.healthyPullRequests[0].checks.pending.Count -eq 1) 'pending remains normal'
}
Case 'unrecognized status requires attention' {
    $ctx=Setup;$check=Check;$check.status='future-status';$global:boundedFixture.checks=@($check)
    $packet=Collect $ctx
    Assert ($packet.healthyPullRequests.Count -eq 0 -and 'unknown-check-result' -in $packet.attention[0].problemCodes) 'unknown status must not be healthy'
}
Case 'stage shaped arbitrary errors cannot escape sanitization' {
    $ctx=Setup;$ctx.wallClock={throw 'inventory: fixture-secret'}
    $text='';try {Collect $ctx | Out-Null}catch{$text=$_.Exception.Message}
    Assert ($text -eq 'inventory-complete: invalid-inventory' -or $text -eq 'base-readback: invalid-inventory') 'only fixed safe labels may escape'
    Assert ($text -notmatch 'fixture-secret') 'stage-shaped secret leaked'
    $ctx=Setup;$global:boundedFixture.gatewayHook={throw 'conversations: gateway-secret'}
    $text='';try {Route $ctx | Out-Null}catch{$text=$_.Exception.Message}
    Assert ($text -eq 'conversations: transport-failed') 'gateway transport error must be sanitized'
    Assert (-not (Test-ReconciliationSafeError 'inventory: fixture-secret')) 'arbitrary code rejected'
    Assert (-not (Test-ReconciliationSafeError 'fixture-secret: request-failed')) 'arbitrary stage rejected'
}
Case 'production requests isolate optional gateway bearer credentials' {
    $template=Join-Path (Split-Path $PSScriptRoot -Parent) 'templates/HostAdapter.ps1'
    foreach($withGatewayToken in @($true,$false)) {
        $global:boundedHttpRequests=[Collections.Generic.List[object]]::new()
        $send={param($message,$cancellation)
            $global:boundedHttpRequests.Add(@{uri=$message.RequestUri.AbsoluteUri;authorization=[string]$message.Headers.Authorization})
            $response=[Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::OK)
            $response.Content=[Net.Http.StringContent]::new('{}');return $response
        }
        $args=@{Token='fixture-secret';GatewayUrl='https://gateway.invalid';SendOverride=$send}
        # Before the fix, run the actual request builder without a nonexistent parameter: RED is missing auth, not binding failure.
        if($withGatewayToken -and (Get-Command $template).Parameters.ContainsKey('GatewayToken')){$args.GatewayToken='gateway-secret'}
        $hostAdapter=& $template @args
        try {
            $null=Invoke-ReconciliationRequest $hostAdapter.Context 'github' 'inventory' 'repos/Sytone/botnexus/pulls'
            $null=Invoke-ReconciliationRequest $hostAdapter.Context 'gateway' 'conversations' '/api/conversations'
            Assert ($global:boundedHttpRequests[0].authorization -eq 'Bearer fixture-secret') 'GitHub uses only GitHub token'
            $expected=if($withGatewayToken){'Bearer gateway-secret'}else{''}
            Assert ($global:boundedHttpRequests[1].authorization -eq $expected) 'gateway must use only optional gateway bearer'
            Assert ($global:boundedHttpRequests[1].uri -eq 'https://gateway.invalid/api/conversations') 'gateway request target'
        }finally{& $hostAdapter.Dispose}
    }
}
Case 'router forwards optional gateway token to adapter' {
    $dir=Join-Path $ScratchRoot ([guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($dir)|Out-Null
    try {
        $global:boundedWrapperContext=Setup;$global:boundedForwardedToken=$null
        $adapter=Join-Path $dir 'adapter.ps1'
        [IO.File]::WriteAllText($adapter,'param($Token,$StatePath,$CachePath,$GatewayUrl,$GatewayToken) $global:boundedForwardedToken=$GatewayToken; New-FixtureAdapter $Token')
        $entrypoint=Join-Path (Split-Path $PSScriptRoot -Parent) 'Invoke-BotNexusOwnedPullRequestHealth.ps1'
        $args=@{Token='fixture-secret';AdapterPath=$adapter;StatePath=(Join-Path $dir 'state.json')}
        if((Get-Command $entrypoint).Parameters.ContainsKey('GatewayToken')){$args.GatewayToken='gateway-secret'}
        $null=& $entrypoint @args
        Assert ($global:boundedForwardedToken -eq 'gateway-secret') 'router must forward distinct gateway token'
    }finally{Remove-Item -LiteralPath $dir -Recurse -Force}
}
@{ total=$script:passed+$script:failures.Count; passed=$script:passed; failed=$script:failures.Count; failures=@($script:failures) } | ConvertTo-Json -Depth 5 -Compress
if($script:failures.Count){exit 1}
