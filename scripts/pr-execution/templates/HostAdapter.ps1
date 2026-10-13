# Template: explicit operation-scoped token, explicit isolated storage paths. No auth helper or environment mutation.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Token,[string]$StatePath,[string]$CachePath,[string]$GatewayUrl,
    [scriptblock]$TransportOverride, [scriptblock]$SendOverride, [string]$GatewayToken)
$ErrorActionPreference='Stop'
$locks=[Collections.Generic.List[IO.FileStream]]::new()
$client=$null
try {
    foreach($path in @($StatePath,$CachePath) | Where-Object {$_} | Sort-Object -Unique) {
        $parent=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path))
        [IO.Directory]::CreateDirectory($parent) | Out-Null
        # Non-blocking exclusive ownership prevents overlapping router invocations from duplicating side effects.
        $locks.Add([IO.File]::Open("$path.lock",[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None))
    }
    $read={param($Path)
        if($Path -and [IO.File]::Exists($Path)) {
            $value=ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($Path)) -AsHashtable
            if($value -isnot [hashtable]){throw 'storage: invalid-object'}
            return $value
        }
        return @{}
    }
    $state=& $read $StatePath;$cache=& $read $CachePath
    $atomicWrite={param($Path,$Value)
        if(-not $Path){return}
        $temporary="$Path.$([guid]::NewGuid().ToString('N')).tmp"
        try {
            [IO.File]::WriteAllText($temporary,($Value | ConvertTo-Json -Depth 40 -Compress),[Text.UTF8Encoding]::new($false))
            # Replace atomically on the same filesystem; failed writes leave the previous durable file untouched.
            [IO.File]::Move($temporary,$Path,$true)
        } finally {if([IO.File]::Exists($temporary)){[IO.File]::Delete($temporary)}}
    }
    $saveState={param($Value) & $atomicWrite $StatePath $Value}.GetNewClosure()
    $saveCache={param($Value) & $atomicWrite $CachePath $Value}.GetNewClosure()
    $client=[Net.Http.HttpClient]::new()
    $client.Timeout=[Threading.Timeout]::InfiniteTimeSpan
    $gateway=$GatewayUrl.TrimEnd('/')
    $transport={param($Request)
        $uri=if($Request.scope -eq 'github'){"https://api.github.com/$($Request.path)"}else{"$gateway$($Request.path)"}
        $message=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Request.method),$uri)
        $cancellation=[Threading.CancellationTokenSource]::new([int]$Request.timeoutMilliseconds)
        $response=$null
        try {
            if($Request.scope -eq 'github') {
                $message.Headers.Authorization=[Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$Request.token)
                $message.Headers.Accept.ParseAdd('application/vnd.github+json')
                $message.Headers.Add('X-GitHub-Api-Version','2022-11-28')
                $message.Headers.UserAgent.ParseAdd('agent-farnsworth')
            }
            elseif($Request.scope -eq 'gateway' -and -not [string]::IsNullOrWhiteSpace($GatewayToken)) {
                $message.Headers.Authorization=[Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$GatewayToken)
            }
            foreach($key in $Request.headers.Keys){$message.Headers.Add($key,[string]$Request.headers[$key])}
            if($null -ne $Request.body){$message.Content=[Net.Http.StringContent]::new(($Request.body | ConvertTo-Json -Depth 12 -Compress),[Text.Encoding]::UTF8,'application/json')}
            $response=if($SendOverride){& $SendOverride $message $cancellation.Token}else{$client.SendAsync($message,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$cancellation.Token).GetAwaiter().GetResult()}
            $text=$response.Content.ReadAsStringAsync($cancellation.Token).GetAwaiter().GetResult()
            $headers=@{}
            foreach($header in $response.Headers){$headers[$header.Key]=[string]::Join(',',$header.Value)}
            $body=$null
            if($response.IsSuccessStatusCode -and $text){$body=ConvertFrom-Json -InputObject $text -AsHashtable -NoEnumerate}
            @{status=[int]$response.StatusCode;headers=$headers;body=$body}
        } catch [OperationCanceledException] {throw [TimeoutException]::new('timeout')}
        finally {if($response){$response.Dispose()};$message.Dispose();$cancellation.Dispose()}
    }.GetNewClosure()
    if($TransportOverride){$transport=$TransportOverride}
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $context=New-ReconciliationContext -Transport $transport -Clock { $timer.ElapsedMilliseconds }.GetNewClosure() -Delay {param($Ms) Start-Sleep -Milliseconds $Ms} -Token $Token -Cache $cache
    $dispose={ $client.Dispose();foreach($handle in $locks){$handle.Dispose()} }.GetNewClosure()
    @{Production=$true;Context=$context;State=$state;SaveState=$saveState;SaveCache=$saveCache;Dispose=$dispose}
} catch {
    if($client){$client.Dispose()};foreach($handle in $locks){$handle.Dispose()}
    throw 'host-adapter: initialization-failed'
} finally {$Token=$null;$GatewayToken=$null}
