# Safe regression harness: JSON fixtures and synthetic pwsh processes only.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../RunnerDiagnostic.ps1')
. (Join-Path $PSScriptRoot '../RunnerTimeout.ps1')
. (Join-Path $PSScriptRoot 'DiagnosticFixtureClock.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('deadline4777-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$failed = @(); $passed = 0
function Case([string]$name, [scriptblock]$body) { try { & $body; $script:passed++; "PASS $name" } catch { $script:failed += $name; "FAIL ${name}: $_" } }
function Require([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
$id = [guid]::NewGuid().ToString()
function Event([string]$kind, [int]$sequence, [string]$source = 'Example.dll') { return [ordered]@{event=$kind; id=$id; sequence=$sequence; utc=[DateTime]::UtcNow.ToString('o'); fullyQualifiedName='Example.Test'; displayName='Example'; source=$source; processId=123} }
function Evidence { Write-DiagnosticEvidence -Root $root -Project 'tests/safe/Safe.Tests.csproj' -SourceSnapshot @{digest='exact'} -RunnerImage 'image' -RunnerDirectory $PSScriptRoot -Run ([pscustomobject]@{TimedOut=$true; ExitCode=1}) -HangSeconds 120 -OuterSeconds 300 }
function Journal($records, [string]$name='events-a.jsonl') { @($records | ForEach-Object { $_ | ConvertTo-Json -Compress }) | Set-Content (Join-Path $root $name) }
try {
    Case 'shutdown timeout requires stack evidence' { Journal @((Event 'TestCaseStart' 1),(Event 'TestCaseEnd' 2)); Require (-not (Evidence).hangEvidenceComplete) 'Shutdown timeout certified without dump.' }
    Case 'duplicate end is invalid' { Journal @((Event 'TestCaseStart' 1),(Event 'TestCaseEnd' 2),(Event 'TestCaseEnd' 3)); Require (-not (Evidence).eventEvidenceComplete) 'Duplicate end certified.' }
    Case 'end cannot pair across journals' { Journal @((Event 'TestCaseStart' 1)); Journal @((Event 'TestCaseEnd' 1)) 'events-b.jsonl'; Require (-not (Evidence).eventEvidenceComplete) 'Cross-journal end certified.'; Remove-Item (Join-Path $root 'events-b.jsonl') }
    Case 'source is part of identity' { Journal @((Event 'TestCaseStart' 1),(Event 'TestCaseEnd' 2 'Other.dll')); Require (-not (Evidence).eventEvidenceComplete) 'Different assembly paired.' }
    Case 'timestamp must be UTC' { $e=Event 'TestCaseStart' 1; $e.utc='not-a-timestamp'; Journal @($e); Require (-not (Evidence).eventEvidenceComplete) 'Invalid timestamp certified.' }
    Case 'sequence must be monotonic' { Journal @((Event 'TestCaseStart' 2),(Event 'TestCaseEnd' 1)); Require (-not (Evidence).eventEvidenceComplete) 'Reversed sequence certified.' }
    Case 'dump presence is not stack proof' { Journal @((Event 'TestCaseStart' 1)); [IO.File]::WriteAllBytes((Join-Path $root 'empty.dmp'),[byte[]]@(1,2,3)); Require (-not (Evidence).hangEvidenceComplete) 'Corrupt dump certified.' }
    Case 'verified must be boolean' { $proof=[pscustomobject]@{version=1;verified='true';digest='exact';runId='run'}; $r=[pscustomobject]@{mode='diagnostic';runId='run';sourceSnapshot=$proof}; $rejected=$false; try { Assert-DiagnosticSourceProof $r $r 'exact' 'run' } catch { $rejected=$true }; Require $rejected 'String verified accepted.' }
    Case 'existing adapter paths preserved once' { $src=Join-Path $root 'source.runsettings'; $dst=Join-Path $root 'merged.runsettings'; Set-Content $src '<RunSettings><RunConfiguration><TestAdaptersPaths>existing-adapters</TestAdaptersPaths></RunConfiguration></RunSettings>'; New-DiagnosticSettings $src $dst $root $root; [xml]$xml=Get-Content $dst -Raw; $nodes=$xml.SelectNodes('//TestAdaptersPaths'); Require ($nodes.Count -eq 1 -and $nodes[0].InnerText.Contains('existing-adapters') -and $nodes[0].InnerText.Contains($root)) 'Duplicate/lost adapter paths.' }
    Case 'valid native thread stack container is recognized, truncation rejected' {
        $path=Join-Path $root 'native.dmp'; $w=[IO.BinaryWriter]::new([IO.File]::Create($path))
        try { $w.Write([byte[]]::new(104)); $w.BaseStream.Position=0; $w.Write([uint32]0x504d444d); $w.BaseStream.Position=8; $w.Write([uint32]1); $w.Write([uint32]32); $w.BaseStream.Position=32; $w.Write([uint32]3); $w.Write([uint32]52); $w.Write([uint32]44); $w.Write([uint32]1); $w.BaseStream.Position=80; $w.Write([uint32]8); $w.Write([uint32]96) } finally { $w.Dispose() }
        Require (Test-DiagnosticStackDump $path) 'Valid thread/stack container rejected.'
        $s=[IO.File]::OpenWrite($path); $s.SetLength(100); $s.Dispose(); Require (-not (Test-DiagnosticStackDump $path)) 'Truncated stack accepted.'; Remove-Item $path
    }
    Case 'absolute watchdog captures ended-test shutdown before unchanged deadline' {
        Get-ChildItem $root -Filter 'events-*.jsonl' | Remove-Item
        $child=Join-Path $root 'child.ps1'; $dump=Join-Path $root 'dump.ps1'; $marker=Join-Path $root 'capture.json'
        Set-Content $child '$path=Join-Path $args[0] "events-host.jsonl"; $e=@{event="TestHostLaunched";sequence=1;utc=[DateTime]::UtcNow.ToString("o");processId=$PID;testHostProcessId=$PID;testHostStartUtc=(Get-Process -Id $PID).StartTime.ToUniversalTime().ToString("o");testHostStartToken=if($IsLinux){$stat=[IO.File]::ReadAllText("/proc/$PID/stat");$stat.Substring($stat.LastIndexOf([char]41)+2).Split([char]32,[StringSplitOptions]::RemoveEmptyEntries)[19]}else{(Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks.ToString()}}; $e | ConvertTo-Json -Compress | Set-Content $path; $id=[guid]::NewGuid().ToString(); foreach ($kind in @("TestCaseStart","TestCaseEnd")) { $e.sequence++; $e.event=$kind; $e.utc=[DateTime]::UtcNow.ToString("o"); $e.id=$id; $e.source="Synthetic.dll"; $e.fullyQualifiedName="Synthetic.Ended"; $e | ConvertTo-Json -Compress | Add-Content $path }; [IO.File]::WriteAllText((Join-Path $args[0] "host.ready"),"ready"); [Threading.ManualResetEvent]::new($false).WaitOne() | Out-Null'
        Set-Content $dump '[IO.File]::WriteAllText($args[0],($args | Select-Object -Skip 1 | ConvertTo-Json -Compress)); [Console]::WriteLine("synthetic capture");'
        $fixture=New-FixtureElapsedProvider -ReadyPath (Join-Path $root 'host.ready') -DumpReadyPath $marker
        $run=Invoke-DiagnosticProcess -FilePath 'pwsh' -ArgumentList @('-NoProfile','-File',$child,$root) -LogPath (Join-Path $root 'child.log') -TimeoutSeconds 6 -EvidenceRoot $root -CaptureReserveSeconds 3 -DumpTimeoutSeconds 1 -KillReserveSeconds 1 -PollMilliseconds 50 -DumpFilePath 'pwsh' -DumpArgumentPrefix @('-NoProfile','-File',$dump,$marker) -ElapsedSecondsProvider $fixture.Provider
        Assert-FixtureClock $fixture $run -Dump
        Require ($run.TimedOut -and $run.ElapsedSeconds -lt 7) 'Outer deadline increased.'
        Require (Test-Path $marker) "No absolute capture attempt: $($run.Captures | ConvertTo-Json -Compress)"
        $actual=@(Get-Content $marker -Raw | ConvertFrom-Json); $hostEvent=Get-Content (Join-Path $root 'events-host.jsonl') | Select-Object -First 1 | ConvertFrom-Json
        Require ($actual[0] -eq '--normal' -and $actual[1] -eq '--name' -and $actual[-1] -eq $hostEvent.testHostProcessId.ToString()) 'Wrong dump options or PID.'
        Require ($run.Captures.Count -eq 1 -and $run.Captures[0].startedSeconds -lt 5) 'Capture too late/unrecorded.'
        Require (-not (Get-Process -Id $hostEvent.testHostProcessId -ErrorAction SilentlyContinue)) 'Synthetic child survived.'
        Require (Test-Path (Join-Path $root 'events-host.jsonl')) 'Journal lost at kill.'
    }
    Case 'dump subprocess hang is bounded and failure recorded' {
        $child=Join-Path $root 'child.ps1'; $dump=Join-Path $root 'dump.ps1'
        Get-ChildItem $root -Filter 'events-*.jsonl' | Remove-Item
        Remove-Item (Join-Path $root 'host.ready')
        $blockedMarker=Join-Path $root 'blocked.ready'
        $fixture=New-FixtureElapsedProvider -ReadyPath (Join-Path $root 'host.ready') -DumpReadyPath $blockedMarker -BlockedDump
        Set-Content $dump '[IO.File]::WriteAllText($args[0],[string]$PID); [Console]::WriteLine("dump blocked"); [Threading.ManualResetEvent]::new($false).WaitOne() | Out-Null'
        $run=Invoke-DiagnosticProcess -FilePath 'pwsh' -ArgumentList @('-NoProfile','-File',$child,$root) -LogPath (Join-Path $root 'blocked.log') -TimeoutSeconds 6 -EvidenceRoot $root -CaptureReserveSeconds 3 -DumpTimeoutSeconds 1 -KillReserveSeconds 1 -PollMilliseconds 50 -DumpFilePath 'pwsh' -DumpArgumentPrefix @('-NoProfile','-File',$dump,$blockedMarker) -ElapsedSecondsProvider $fixture.Provider
        Assert-FixtureClock $fixture $run -Dump -Blocked
        $blockedId=[int](Get-Content $blockedMarker); Require (-not (Get-DiagnosticFixtureLiveProcess $blockedId)) 'Blocked dump process survived.'
        Require ($run.TimedOut -and $run.ElapsedSeconds -lt 7 -and $run.Captures.Count -eq 1 -and $run.Captures[0].timedOut -and $run.Captures[0].exitCode -eq 1) 'Dump child escaped bound or failure hidden.'
        Require (-not (Test-DiagnosticStackDump $run.Captures[0].dumpPath)) 'Failed dump certified.'
    }
    Case 'missing dump executable is recorded without losing deadline or journal' {
        $child=Join-Path $root 'child.ps1'
        Get-ChildItem $root -Filter 'events-*.jsonl' | Remove-Item
        Remove-Item (Join-Path $root 'host.ready')
        $fixture=New-FixtureElapsedProvider -ReadyPath (Join-Path $root 'host.ready') -MissingDump
        $run=Invoke-DiagnosticProcess -FilePath 'pwsh' -ArgumentList @('-NoProfile','-File',$child,$root) -LogPath (Join-Path $root 'missing.log') -TimeoutSeconds 6 -EvidenceRoot $root -CaptureReserveSeconds 3 -DumpTimeoutSeconds 1 -KillReserveSeconds 1 -PollMilliseconds 50 -DumpFilePath (Join-Path $root 'missing-executable') -ElapsedSecondsProvider $fixture.Provider
        Assert-FixtureClock $fixture $run
        Require ($run.TimedOut -and $run.ElapsedSeconds -lt 7 -and $run.Captures.Count -eq 1 -and -not [string]::IsNullOrWhiteSpace($run.Captures[0].error)) 'Missing executable failure hidden.'
        Require (Test-Path (Join-Path $root 'events-host.jsonl')) 'Journal lost on capture failure.'
    }
    "RESULT passed=$passed failed=$($failed.Count)"
    if ($failed.Count) { throw "Regressions: $($failed -join ', ')" }
}
finally { Remove-Item $root -Recurse -Force }
