# Portable policy core. Transport, monotonic milliseconds, credentials and persistence are injected.
Set-StrictMode -Version Latest
function New-ReconciliationContext {
    param([scriptblock]$Transport, [scriptblock]$Clock, [scriptblock]$Delay, [string]$Token,
        [hashtable]$Cache=@{}, [ValidateRange(1,110000)][long]$BudgetMilliseconds=100000,
        [ValidateRange(1,60000)][int]$RequestTimeoutMilliseconds=10000,
        [ValidateRange(1,5)][int]$MaximumAttempts=2, [ValidateRange(0,30000)][int]$RetryDelayMilliseconds=1000,
        [scriptblock]$WallClock={ [datetimeoffset]::UtcNow })
    if (-not $Transport -or -not $Clock -or -not $Delay -or [string]::IsNullOrWhiteSpace($Token)) { throw 'configuration: invalid dependencies' }
    @{transport=$Transport;clock=$Clock;wallClock=$WallClock;delay=$Delay;token=$Token;cache=$Cache;deadline=([long](& $Clock)+$BudgetMilliseconds);
        requestTimeout=$RequestTimeoutMilliseconds;attempts=$MaximumAttempts;retryDelay=$RetryDelayMilliseconds}
}
function Stop-Reconciliation([string]$Stage,[string]$Code) { throw "${Stage}: $Code" }
function Test-ReconciliationSafeError([string]$Message) {
    if($Message -cnotmatch '^([a-z-]+): ([a-z0-9-]+)$'){return $false}
    $stages=@('configuration','inventory','pr-detail','base','checks','checks-readback','inventory-readback','pr-readback','base-readback','inventory-complete','conversations','uncertain','create','create-readback','wake','wake-readback','routing-complete','cache-save','storage','host-adapter','collector','router')
    $codes=@('deadline-exhausted','transport-failed','invalid-response','request-failed','unbacked-not-modified','invalid-check-page','check-total-drift','invalid-pull-page','oversized-page','duplicate-or-invalid-identity','partial-page','incomplete-check-inventory','page-limit','pull-request-drift','invalid-repository','missing-base','check-page-drift','inventory-drift','mergeability-drift','base-drift','invalid-inventory','state-write-failed','invalid-list','operator-readback-required','missing-conversation-id','conversation-mismatch','invalid-wake-receipt','wake-readback-mismatch','invalid-routing-response','invalid-object','initialization-failed','operation-failed')
    return $Matches[1] -cin $stages -and ($Matches[2] -cin $codes -or $Matches[2] -cmatch '^http-[1-5][0-9]{2}$')
}
function Get-ReconciliationRemaining($Context,[string]$Stage) {
    $remaining=$Context.deadline-[long](& $Context.clock)
    if ($remaining -le 0) { Stop-Reconciliation $Stage 'deadline-exhausted' }
    $remaining
}
function Invoke-ReconciliationRequest {
    param($Context,[string]$Scope,[string]$Stage,[string]$Path,[string]$Method='GET',$Body=$null,[hashtable]$Headers=@{})
    $attemptLimit=if($Method -eq 'GET'){$Context.attempts}else{1} # Never retry a non-idempotent gateway mutation.
    for($attempt=1;$attempt -le $attemptLimit;$attempt++) {
        $remaining=Get-ReconciliationRemaining $Context $Stage
        $request=@{scope=$Scope;stage=$Stage;path=$Path;method=$Method;body=$Body;headers=$Headers;
            timeoutMilliseconds=[int][Math]::Min($Context.requestTimeout,$remaining);token=if($Scope -eq 'github'){$Context.token}else{''}}
        $response=$null; $transient=$false
        try { $response=& $Context.transport $request }
        catch {
            # Classify internally, but never relay exception strings, URLs, headers or response bodies.
            $transient=$_.Exception -is [TimeoutException] -or $_.Exception.Message -match '(?i)timeout|timed out|connection (reset|closed)|temporarily unavailable'
            if(-not $transient){Stop-Reconciliation $Stage 'transport-failed'}
        }
        $null=Get-ReconciliationRemaining $Context $Stage
        if($null -ne $response) {
            if(-not $response.ContainsKey('status') -or -not $response.ContainsKey('body') -or -not $response.ContainsKey('headers')){Stop-Reconciliation $Stage 'invalid-response'}
            if($response.status -in @(200,201,202,204,304)){return $response}
            $transient=$response.status -in @(429,502,503,504)
            if(-not $transient){Stop-Reconciliation $Stage "http-$([int]$response.status)"}
        }
        if(-not $transient -or $attempt -eq $attemptLimit){Stop-Reconciliation $Stage 'request-failed'}
        $remaining=Get-ReconciliationRemaining $Context $Stage
        if($remaining -le $Context.retryDelay){Stop-Reconciliation $Stage 'deadline-exhausted'}
        & $Context.delay $Context.retryDelay
    }
}
function Get-ReconciliationFingerprint($Value) { ConvertTo-Json -InputObject $Value -Depth 30 -Compress }
function Get-ReconciliationPages {
    param($Context,[string]$Path,[string]$Stage,[string]$Kind='pulls',[string]$CachePrefix='',[hashtable]$CandidateCache=@{})
    $rows=[Collections.Generic.List[object]]::new();$identities=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $expectedTotal=$null
    for($page=1;$page -le 100;$page++) {
        $separator=if($Path.Contains('?')){'&'}else{'?'}
        $endpoint="${Path}${separator}per_page=100&page=$page"
        $headers=@{};$cached=$null;$key="${CachePrefix}|$endpoint"
        if($CachePrefix -and $Context.cache.ContainsKey($key)) {
            $cached=$Context.cache[$key]
            if($cached.ContainsKey('etag') -and $cached.etag){$headers['If-None-Match']=[string]$cached.etag}
        }
        $response=Invoke-ReconciliationRequest $Context 'github' $Stage $endpoint -Headers $headers
        if($response.status -eq 304) {
            if(-not $cached -or -not $headers.ContainsKey('If-None-Match')){Stop-Reconciliation $Stage 'unbacked-not-modified'}
            $payload=$cached.body;$responseHeaders=$cached.headers
        } else { $payload=$response.body;$responseHeaders=$response.headers }
        if($Kind -eq 'checks') {
            if($null -eq $payload -or -not $payload.ContainsKey('total_count') -or -not $payload.ContainsKey('check_runs')){Stop-Reconciliation $Stage 'invalid-check-page'}
            if($null -eq $expectedTotal){$expectedTotal=[int]$payload.total_count}
            if([int]$payload.total_count -ne $expectedTotal -or $expectedTotal -lt 0){Stop-Reconciliation $Stage 'check-total-drift'}
            $batch=@($payload.check_runs)
        } else {
            if($payload -isnot [array]){Stop-Reconciliation $Stage 'invalid-pull-page'}
            $batch=@($payload)
        }
        if($batch.Count -gt 100){Stop-Reconciliation $Stage 'oversized-page'}
        foreach($row in $batch) {
            $id=if($Kind -eq 'checks'){[string]$row.id}else{[string]$row.number}
            if($id -notmatch '^[1-9]\d*$' -or -not $identities.Add($id)){Stop-Reconciliation $Stage 'duplicate-or-invalid-identity'}
            $rows.Add($row)
        }
        # Count and Link must agree. Never silently truncate a short page with a next link.
        $hasNext=$responseHeaders.ContainsKey('Link') -and [string]$responseHeaders.Link -match 'rel="next"'
        if($hasNext -and $batch.Count -lt 100){Stop-Reconciliation $Stage 'partial-page'}
        if($CachePrefix) {
            $etag=if($responseHeaders.ContainsKey('ETag')){[string]$responseHeaders.ETag}else{''}
            if($etag){$CandidateCache[$key]=@{etag=$etag;body=$payload;headers=$responseHeaders}}
        }
        if($batch.Count -lt 100) {
            if($Kind -eq 'checks' -and $rows.Count -ne $expectedTotal){Stop-Reconciliation $Stage 'incomplete-check-inventory'}
            return ,@($rows.ToArray())
        }
        # A full last page still receives an explicit empty terminal request, including when total_count is exact.
    }
    Stop-Reconciliation $Stage 'page-limit'
}
function Get-ReconciliationInventorySignature($Rows) {
    $normalized=@($Rows | Sort-Object number | ForEach-Object {
        @{number=$_.number;author=$_.user.login;state=$_.state;head=$_.head.sha;baseRef=$_.base.ref;base=$_.base.sha;updated=$_.updated_at;draft=$_.draft}
    })
    Get-ReconciliationFingerprint $normalized
}
function Assert-ReconciliationPr($Summary,$Detail,[string]$Stage) {
    if($Detail.number -ne $Summary.number -or $Detail.state -ne 'open' -or $Detail.user.login -cne $Summary.user.login -or
        $Detail.head.sha -cne $Summary.head.sha -or $Detail.base.ref -cne $Summary.base.ref -or $Detail.base.sha -cne $Summary.base.sha -or
        $Detail.updated_at -cne $Summary.updated_at -or $Detail.draft -ne $Summary.draft){Stop-Reconciliation $Stage 'pull-request-drift'}
}
function Get-ReconciliationChecks($Checks) {
    $failedConclusions=@('failure','cancelled','timed_out','action_required','startup_failure','stale')
    $successfulConclusions=@('success','neutral','skipped')
    $pendingStatuses=@('queued','in_progress','waiting','requested','pending')
    $latest=@($Checks | Group-Object name | ForEach-Object {
        $_.Group | Sort-Object @{Expression={if($_.started_at){[datetimeoffset]$_.started_at}else{[datetimeoffset]::MinValue}};Descending=$true},@{Expression={[long]$_.id};Descending=$true} | Select-Object -First 1
    })
    $project={ @{name=$_.name;status=$_.status;conclusion=$_.conclusion;url=$_.html_url} }
    @{total=$latest.Count;failed=@($latest | Where-Object {$_.status -eq 'completed' -and $_.conclusion -in $failedConclusions} | ForEach-Object $project);
        pending=@($latest | Where-Object {$_.status -in $pendingStatuses} | ForEach-Object $project);
        unknown=@($latest | Where-Object {($_.status -eq 'completed' -and $_.conclusion -notin ($failedConclusions+$successfulConclusions)) -or ($_.status -ne 'completed' -and $_.status -notin $pendingStatuses)} | ForEach-Object $project)}
}
function Get-OwnedPullRequestHealth {
    param($Context,[string]$Repository='Sytone/botnexus')
    $stage='configuration'
    try {
        if($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$'){Stop-Reconciliation $stage 'invalid-repository'}
        $prefix="repos/$Repository";$stage='inventory'
        $summaries=Get-ReconciliationPages $Context "$prefix/pulls?state=open" $stage
        $owned=@($summaries | Where-Object { $_.user.login -cin @('agent-farnsworth[bot]','app/agent-farnsworth') })
        $bases=@{};$candidate=@{};$rows=[Collections.Generic.List[object]]::new();$details=@{}
        foreach($summary in $owned) {
            $stage='pr-detail'
            $pr=(Invoke-ReconciliationRequest $Context 'github' $stage "$prefix/pulls/$($summary.number)").body
            Assert-ReconciliationPr $summary $pr $stage
            $details[[string]$summary.number]=$pr
            $baseRef=[string]$pr.base.ref
            if(-not $bases.ContainsKey($baseRef)) {
                $stage='base';$base=(Invoke-ReconciliationRequest $Context 'github' $stage "$prefix/commits/$([uri]::EscapeDataString($baseRef))").body.sha
                if([string]::IsNullOrWhiteSpace($base)){Stop-Reconciliation $stage 'missing-base'}
                $bases[$baseRef]=$base
            }
            $base=$bases[$baseRef];$stage='checks'
            $checkPath="$prefix/commits/$($pr.head.sha)/check-runs?filter=latest"
            $cachePrefix="$Repository|$($pr.head.sha)|$baseRef|$base"
            $checks=Get-ReconciliationPages $Context $checkPath $stage 'checks' $cachePrefix $candidate
            # Re-read all check pages to catch count-preserving drift, not just changes in total_count.
            # Candidate is temporarily used for conditional readback, but published only on complete inventory success.
            $readContext=$Context.Clone();$readContext.cache=$candidate
            $stage='checks-readback';$readChecks=Get-ReconciliationPages $readContext $checkPath $stage 'checks' $cachePrefix @{}
            if((Get-ReconciliationFingerprint @($checks | Sort-Object id)) -cne (Get-ReconciliationFingerprint @($readChecks | Sort-Object id))){Stop-Reconciliation $stage 'check-page-drift'}
            $summaryChecks=Get-ReconciliationChecks $checks
            $codes=[Collections.Generic.List[string]]::new()
            if($pr.draft){$codes.Add('draft-reconciliation-required')}
            if($summaryChecks.failed.Count){$codes.Add('failed-checks')}
            if($summaryChecks.unknown.Count){$codes.Add('unknown-check-result')}
            if($null -eq $pr.mergeable -or $pr.mergeable_state -eq 'unknown' -or [string]::IsNullOrWhiteSpace($pr.mergeable_state)){$codes.Add('mergeability-unknown')}
            if($pr.mergeable -eq $false -or $pr.mergeable_state -eq 'dirty'){$codes.Add('merge-conflict')}
            elseif($pr.mergeable_state -eq 'behind'){$codes.Add('base-behind')}
            $issue=0;$match=[regex]::Match([string]$pr.body,'(?im)\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?|refs?|references?)\s*:?\s*#(\d+)')
            if($match.Success){$issue=[int]$match.Groups[1].Value}
            $rows.Add(@{number=[int]$pr.number;primaryIssue=$issue;title=[string]$pr.title;url=[string]$pr.html_url;ownership='agent-owned';draft=[bool]$pr.draft;
                head=@{ref=[string]$pr.head.ref;sha=[string]$pr.head.sha};base=@{ref=$baseRef;sha=$base};mergeable=$pr.mergeable;mergeState=[string]$pr.mergeable_state;
                checks=$summaryChecks;commentsNeedingAttention=@();problemCodes=@($codes | Sort-Object -Unique)})
        }
        $stage='inventory-readback';$readSummaries=Get-ReconciliationPages $Context "$prefix/pulls?state=open" $stage
        if((Get-ReconciliationInventorySignature $summaries) -cne (Get-ReconciliationInventorySignature $readSummaries)){Stop-Reconciliation $stage 'inventory-drift'}
        foreach($summary in $owned) {
            $stage='pr-readback';$pr=(Invoke-ReconciliationRequest $Context 'github' $stage "$prefix/pulls/$($summary.number)").body
            Assert-ReconciliationPr $details[[string]$summary.number] $pr $stage
            if($pr.mergeable -ne $details[[string]$summary.number].mergeable -or $pr.mergeable_state -ne $details[[string]$summary.number].mergeable_state){Stop-Reconciliation $stage 'mergeability-drift'}
        }
        foreach($ref in $bases.Keys) {
            $stage='base-readback';$base=(Invoke-ReconciliationRequest $Context 'github' $stage "$prefix/commits/$([uri]::EscapeDataString($ref))").body.sha
            if($base -cne $bases[$ref]){Stop-Reconciliation $stage 'base-drift'}
        }
        $null=Get-ReconciliationRemaining $Context 'inventory-complete'
        $Context.cache.Clear();foreach($key in $candidate.Keys){$Context.cache[$key]=$candidate[$key]}
        @{generatedAt=(& $Context.wallClock);repository=$Repository;scanned=$owned.Count;
            attention=@($rows | Where-Object {$_.problemCodes.Count});healthyPullRequests=@($rows | Where-Object {-not $_.problemCodes.Count})}
    } catch {
        # Malformed response/property errors must also be stage-safe. Never serialize partial rows as healthy.
        if(Test-ReconciliationSafeError $_.Exception.Message){throw}
        Stop-Reconciliation $stage 'invalid-inventory'
    }
}
function Save-ReconciliationState($Context,$State,[scriptblock]$SaveState,[string]$Stage) {
    $null=Get-ReconciliationRemaining $Context $Stage
    try { & $SaveState $State } catch { Stop-Reconciliation $Stage 'state-write-failed' }
    $null=Get-ReconciliationRemaining $Context $Stage
}
function Invoke-OwnedPullRequestHealth {
    param($Context,[string]$Repository='Sytone/botnexus',[string]$AgentId='farnsworth',[hashtable]$State,[scriptblock]$SaveState,[int]$RetryAfterMinutes=60)
    $packet=Get-OwnedPullRequestHealth $Context $Repository
    $stage='conversations'
    try {
        $conversations=(Invoke-ReconciliationRequest $Context 'gateway' $stage "/api/conversations?agentId=$([uri]::EscapeDataString($AgentId))").body
        if($conversations -isnot [array]){Stop-Reconciliation $stage 'invalid-list'}
        $routed=[Collections.Generic.List[object]]::new()
        foreach($pr in $packet.attention) {
            $key=[string]$pr.number;$codes=@($pr.problemCodes | Sort-Object -Unique)
            $marker="$($pr.head.sha)|$($pr.base.sha)|$($codes -join ',')"
            $prior=if($State.ContainsKey($key)){$State[$key]}else{@{}}
            if($prior -is [string]){$prior=@{marker=$prior;routedAt=[datetimeoffset]::MinValue.ToString('o')}}
            if($prior.ContainsKey('pending')){Stop-Reconciliation 'uncertain' 'operator-readback-required'}
            if($prior.ContainsKey('marker') -and $prior.marker -eq $marker -and $prior.ContainsKey('routedAt') -and
                (& $Context.wallClock) -lt ([datetimeoffset]$prior.routedAt).AddMinutes($RetryAfterMinutes)){continue}
            $issue=[int]$pr.primaryIssue
            $matchingConversations=@($conversations | Where-Object {
                $_.status -eq 'Active' -and (($issue -gt 0 -and $_.title -match "^$issue\s+-\s+") -or ($issue -eq 0 -and $_.title -eq "PR $($pr.number) health"))
            } | Sort-Object updatedAt -Descending)
            $conversation=if($matchingConversations.Count){$matchingConversations[0]}else{$null}
            # Write-ahead uncertainty survives timeout, ambiguous response, process termination and next scan.
            # The gateway offers neither client-selected create IDs nor message idempotency keys.
            if(-not $conversation) {
                $stage='create';$prior.pending=@{operation='create';marker=$marker;title=if($issue -gt 0){"$issue - PR $($pr.number) health"}else{"PR $($pr.number) health"}}
                $State[$key]=$prior;Save-ReconciliationState $Context $State $SaveState $stage
                $conversation=(Invoke-ReconciliationRequest $Context 'gateway' $stage '/api/conversations' 'POST' @{agentId=$AgentId;title=$prior.pending.title;purpose="Repair owned PR #$($pr.number) through terminal CI and independent readback. Never merge without Jon's authorization."}).body
                if(-not $conversation.conversationId){Stop-Reconciliation $stage 'missing-conversation-id'}
                $stage='create-readback';$created=(Invoke-ReconciliationRequest $Context 'gateway' $stage "/api/conversations/$([uri]::EscapeDataString($conversation.conversationId))").body
                if($created.conversationId -ne $conversation.conversationId -or $created.agentId -ne $AgentId -or $created.status -ne 'Active'){Stop-Reconciliation $stage 'conversation-mismatch'}
                $conversations+=@($created)
            }
            $stage='wake';$prior.pending=@{operation='wake';marker=$marker;conversationId=$conversation.conversationId}
            $State[$key]=$prior;Save-ReconciliationState $Context $State $SaveState $stage
            $failed=@($pr.checks.failed | ForEach-Object {$_.name})
            $message="UNATTENDED COMMAND-CRON LANE: Never call ask_user or create a durable conversational question. For human-permission or architecture decisions, record the blocked step and verified options on the relevant GitHub issue or PR. Do not infer approval or bypass security/merge gates. Continue independent safe work. Owned PR #$($pr.number) has actionable state on head $($pr.head.sha), base $($pr.base.sha): $($codes -join ', '). Failed checks: $($failed -join ', '). Resume only this existing PR in its current branch/worktree. Inspect each exact problem once, repair attributable failures, synchronize without rewriting published history, run exact-source CORE if the head changes, update only the existing PR through canonical helpers, and verify terminal CI plus readback. Do not merge and do not create a PR-specific cron."
            $wake=(Invoke-ReconciliationRequest $Context 'gateway' $stage "/api/agents/$([uri]::EscapeDataString($AgentId))/conversations/$([uri]::EscapeDataString($conversation.conversationId))/messages" 'POST' @{message=$message;wake=$true;sender='owned-pr-health'}).body
            if(-not $wake.wake -or -not $wake.sessionId -or $wake.conversationId -ne $conversation.conversationId){Stop-Reconciliation $stage 'invalid-wake-receipt'}
            $stage='wake-readback';$read=(Invoke-ReconciliationRequest $Context 'gateway' $stage "/api/conversations/$([uri]::EscapeDataString($conversation.conversationId))").body
            if($read.conversationId -ne $conversation.conversationId -or $read.agentId -ne $AgentId -or $read.status -ne 'Active' -or $read.activeSessionId -ne $wake.sessionId){Stop-Reconciliation $stage 'wake-readback-mismatch'}
            $State[$key]=@{marker=$marker;routedAt=([datetimeoffset](& $Context.wallClock)).ToString('o')}
            Save-ReconciliationState $Context $State $SaveState 'routing-complete'
            $routed.Add(@{pr=[int]$pr.number;conversationId=[string]$conversation.conversationId;sessionId=[string]$wake.sessionId;problemCodes=$codes})
        }
        # Closed PR markers may be pruned only after a complete scan. Never prune unresolved side effects.
        $open=@($packet.attention | ForEach-Object {$_.number})+@($packet.healthyPullRequests | ForEach-Object {$_.number})
        foreach($key in @($State.Keys)){if([int]$key -notin $open -and -not ($State[$key] -is [hashtable] -and $State[$key].ContainsKey('pending'))){$State.Remove($key)}}
        Save-ReconciliationState $Context $State $SaveState 'routing-complete'
        @{status='ok';code=if($routed.Count){'owned-pr-problems-routed'}else{'no-new-owned-pr-problems'};scanned=$packet.scanned;ownedActionable=$packet.attention.Count;routed=@($routed.ToArray());archived=@()}
    } catch {
        if(Test-ReconciliationSafeError $_.Exception.Message){throw}
        Stop-Reconciliation $stage 'invalid-routing-response'
    }
}
