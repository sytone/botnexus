# Synthetic pwsh-only containment and concurrent journal regressions; no gateway/testhost.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../RunnerDiagnostic.ps1')
. (Join-Path $PSScriptRoot 'DiagnosticFixtureClock.ps1')
$root=Join-Path ([IO.Path]::GetTempPath()) ('cleanup4777-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$passed=0; $failed=@()
function Case([string]$name,[scriptblock]$body) { try { & $body; $script:passed++; "PASS $name" } catch { $script:failed+=$name; "FAIL ${name}: $_" } }
function Require([bool]$ok,[string]$message) { if (-not $ok) { throw $message } }
function Clear-Journals { Get-ChildItem $root -Filter 'events-*.jsonl' | Remove-Item }
function Run([string]$script,[string[]]$prefix=@(),$fixture) { Invoke-DiagnosticProcess -FilePath 'pwsh' -ArgumentList @('-NoProfile','-File',$script,$root,(Join-Path $PSScriptRoot 'DiagnosticFixtureClock.ps1')) -LogPath (Join-Path $root 'run.log') -TimeoutSeconds 6 -EvidenceRoot $root -CaptureReserveSeconds 3 -DumpTimeoutSeconds 1 -KillReserveSeconds 1 -PollMilliseconds 50 -DumpFilePath 'pwsh' -DumpArgumentPrefix $prefix -ElapsedSecondsProvider $fixture.Provider }
try {
    Case 'live collector write handle permits journal snapshots' {
        $path=Join-Path $root 'events-open.jsonl'
        $writer=[IO.StreamWriter]::new([IO.FileStream]::new($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read))
        try {
            $e=@{event='TestCaseStart';sequence=1;utc=[DateTime]::UtcNow.ToString('o');processId=123;id=[guid]::NewGuid().ToString();source='Synthetic.dll';fullyQualifiedName='Synthetic.Open'}
            $writer.WriteLine(($e | ConvertTo-Json -Compress)); $writer.Flush()
            $snapshot=Read-DiagnosticEvents -Root $root -Live
            Require (-not $snapshot.Invalid -and $snapshot.Events.Count -eq 1) 'Live writer prevented journal snapshot.'
        } finally { $writer.Dispose(); Remove-Item $path }
    }
    Case 'live trailing fragment deferred but final certification remains strict' {
        $path=Join-Path $root 'events-live.jsonl'
        [IO.File]::WriteAllText($path,'{"event":')
        $live=Read-DiagnosticEvents -Root $root -Live
        Require (-not $live.Invalid -and $live.Incomplete) 'Live partial line was treated as corruption.'
        Require (Read-DiagnosticEvents -Root $root).Invalid 'Final partial JSON accepted.'
    }
    Case 'live malformed newline-terminated or middle lines remain invalid' {
        $path=Join-Path $root 'events-live.jsonl'
        [IO.File]::WriteAllText($path,('{broken'+[Environment]::NewLine+'{"event":'))
        Require (Read-DiagnosticEvents -Root $root -Live).Invalid 'Malformed complete middle line ignored.'
        [IO.File]::WriteAllText($path,('{broken'+[Environment]::NewLine))
        Require (Read-DiagnosticEvents -Root $root -Live).Invalid 'Malformed complete final line ignored.'
    }
    Case 'launcher exits first but inherited pipes and descendant are contained' {
        Clear-Journals
        $leaf=Join-Path $root 'leaf.ps1'; $launcher=Join-Path $root 'launcher.ps1'
        Set-Content $leaf '[Console]::WriteLine("leaf stdout"); [Console]::Error.WriteLine("leaf stderr"); [IO.File]::WriteAllText((Join-Path $args[0] "leaf.pid"),[string]$PID); [Threading.ManualResetEvent]::new($false).WaitOne() | Out-Null'
        Set-Content $launcher '. $args[1]; $i=[Diagnostics.ProcessStartInfo]::new("pwsh"); $i.UseShellExecute=$false; foreach($a in @("-NoProfile","-File",(Join-Path $args[0] "leaf.ps1"),$args[0])) { [void]$i.ArgumentList.Add($a) }; $p=[Diagnostics.Process]::Start($i); [DiagnosticFixtureSignal]::Wait((Join-Path $args[0] "leaf.pid")); exit 0'
        $fixture=New-FixtureElapsedProvider -ReadyPath (Join-Path $root 'leaf.pid') -Descendant
        try {
            $r=Run $launcher @() $fixture; Assert-FixtureClock $fixture $r; $leafId=[int](Get-Content (Join-Path $root 'leaf.pid'))
            $live=Get-Process -Id $leafId -ErrorAction SilentlyContinue
            if ($IsLinux -and $null -ne $live) { $stat=[IO.File]::ReadAllText("/proc/$leafId/stat"); if ($stat.Substring($stat.LastIndexOf([char]41)+2).StartsWith('Z ')) { $live.Dispose(); $live=$null } }
            Require ($null -eq $live) 'Descendant survived launcher exit.'
            Require ($r.CleanupComplete -and $r.DrainComplete -and $r.ReadersClosed) 'Containment or pipe cleanup not proven.'
            Require ($r.ElapsedSeconds -lt 7) 'Deadline extended during cleanup.'
            Require ((Get-Content (Join-Path $root 'run.log') -Raw).Contains('leaf stderr')) 'Pre-kill stderr lost.'
        } finally { if(Test-Path (Join-Path $root 'leaf.pid')) { Stop-Process -Id ([int](Get-Content (Join-Path $root 'leaf.pid'))) -Force -ErrorAction SilentlyContinue } }
    }
    Case 'partial append at first capture does not permanently suppress target' {
        Clear-Journals
        $child=Join-Path $root 'partial.ps1'; $dump=Join-Path $root 'complete.ps1'; $marker=Join-Path $root 'captured.txt'
        Set-Content $child '$path=Join-Path $args[0] "events-partial.jsonl"; $e=@{event="TestHostLaunched";sequence=1;utc=[DateTime]::UtcNow.ToString("o");processId=$PID;testHostProcessId=$PID;testHostStartUtc=(Get-Process -Id $PID).StartTime.ToUniversalTime().ToString("o");testHostStartToken=if($IsLinux){$stat=[IO.File]::ReadAllText("/proc/$PID/stat");$stat.Substring($stat.LastIndexOf([char]41)+2).Split([char]32,[StringSplitOptions]::RemoveEmptyEntries)[19]}else{(Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks.ToString()}}; [IO.File]::WriteAllText($path,($e | ConvertTo-Json -Compress)+[Environment]::NewLine+"{`"event`":"); [IO.File]::WriteAllText((Join-Path $args[0] "partial.ready"),"ready"); [Threading.ManualResetEvent]::new($false).WaitOne() | Out-Null'
        Set-Content $dump '$path=Join-Path $args[0] "events-partial.jsonl"; $first=(Get-Content $path | Select-Object -First 1 | ConvertFrom-Json); $e=@{event="TestCaseStart";sequence=2;utc=[DateTime]::UtcNow.ToString("o");processId=$first.processId;id=[guid]::NewGuid().ToString();source="Synthetic.dll";fullyQualifiedName="Synthetic.Partial"}; $json=$e | ConvertTo-Json -Compress; [IO.File]::WriteAllText($path,($first | ConvertTo-Json -Compress)+[Environment]::NewLine+$json+[Environment]::NewLine); [IO.File]::WriteAllText((Join-Path $args[0] "captured.txt"),"captured")'
        $fixture=New-FixtureElapsedProvider -ReadyPath (Join-Path $root 'partial.ready') -DumpReadyPath $marker
        $r=Run $child @('-NoProfile','-File',$dump,$root) $fixture
        Assert-FixtureClock $fixture $r -Dump
        Require (Test-Path $marker) "Partial line suppressed capture: $($r.Captures | ConvertTo-Json -Compress)"
        Require ($r.Captures.Count -eq 1 -and $r.Captures[0].startedSeconds -lt 5 -and $r.ElapsedSeconds -lt 7) 'Capture missed unchanged deadline.'
        Require (-not (Read-DiagnosticEvents $root).Invalid) 'Completed journal invalid at final certification.'
    }
    Case 'cleanup or drain failure cannot certify evidence' {
        Clear-Journals
        $e=@{event='TestCaseStart';sequence=1;utc=[DateTime]::UtcNow.ToString('o');processId=123;id=[guid]::NewGuid().ToString();source='Synthetic.dll';fullyQualifiedName='Synthetic.Test'}
        $e | ConvertTo-Json -Compress | Set-Content (Join-Path $root 'events-proof.jsonl')
        $run=[pscustomobject]@{TimedOut=$true;ExitCode=1;CleanupComplete=$false;DrainComplete=$true;ReadersClosed=$true}
        $proof=Write-DiagnosticEvidence -Root $root -Project 'tests/safe/Safe.Tests.csproj' -SourceSnapshot @{digest='exact'} -RunnerImage 'image' -RunnerDirectory $PSScriptRoot -Run $run -HangSeconds 120 -OuterSeconds 300
        Require (-not $proof.eventEvidenceComplete) 'Incomplete cleanup certified event evidence.'
        $run.CleanupComplete=$true; $run.DrainComplete=$false
        $proof=Write-DiagnosticEvidence -Root $root -Project 'tests/safe/Safe.Tests.csproj' -SourceSnapshot @{digest='exact'} -RunnerImage 'image' -RunnerDirectory $PSScriptRoot -Run $run -HangSeconds 120 -OuterSeconds 300
        Require (-not $proof.eventEvidenceComplete) 'Incomplete drain certified event evidence.'
    }
    "RESULT passed=$passed failed=$($failed.Count)"
    if($failed.Count) { throw "Regressions: $($failed -join ', ')" }
} finally { Remove-Item $root -Recurse -Force }
