Set-StrictMode -Version Latest

# Shared sender/runner policy. No wildcard resolution, evaluation, or shell interpolation.
function Resolve-DiagnosticProject {
    param([Parameter(Mandatory)][string]$SourceRoot, [Parameter(Mandatory)][string]$Project)
    if ($Project -cnotmatch '^tests/(?:[A-Za-z0-9_.-]+/)+[A-Za-z0-9_.-]+\.csproj$' -or $Project.Split('/') -contains '..' -or $Project.Split('/') -contains '.') { throw 'DiagnosticProject must be one repository-relative tests/.../*.csproj path.' }
    $root = [IO.Path]::GetFullPath($SourceRoot)
    $path = Join-Path $root $Project
    $cursor = $root
    foreach ($segment in $Project.Split('/')) {
        $cursor = Join-Path $cursor $segment
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Diagnostic project path cannot contain links.' }
    }
    [xml]$xml = Get-Content -LiteralPath $path -Raw
    $sdk = $xml.SelectNodes('//PackageReference[@Include="Microsoft.NET.Test.Sdk"]')
    $disabled = $xml.SelectNodes('//IsTestProject') | Where-Object { $_.InnerText.Trim() -ine 'true' }
    if ($sdk.Count -eq 0 -or @($disabled).Count -gt 0 -or $Project -match '/(bin|obj)/') { throw 'Diagnostic project must explicitly reference the test SDK and cannot opt out of tests.' }
    return $path
}

function Get-DiagnosticArguments {
    param([string]$Project, [string]$ResultsRoot, [string]$SettingsPath, [int]$OuterSeconds, [int]$HangSeconds)
    # Leave 90s inside the existing test deadline for dumping/collector shutdown/tree kill.
    if ($HangSeconds -le 0 -or $OuterSeconds -le ($HangSeconds + 90)) { throw 'Insufficient remaining budget for hang collection; diagnostic will not start.' }
    return @('test', $Project, '--nologo', '--tl:off', '-c', 'Debug', '--no-build',
        '--logger', 'trx;LogFilePrefix=diagnostic', '--results-directory', $ResultsRoot,
        '--settings', $SettingsPath, '--blame-hang', '--blame-hang-dump-type', 'mini',
        '--blame-hang-timeout', "${HangSeconds}s", '--diag', (Join-Path $ResultsRoot 'vstest.log'))
}

function New-DiagnosticSettings {
    param([string]$SourceSettings, [string]$Destination, [string]$CollectorDirectory, [string]$EvidenceDirectory)
    [xml]$xml = Get-Content -LiteralPath $SourceSettings -Raw
    $run = $xml.DocumentElement
    $config = $run.SelectSingleNode('RunConfiguration')
    if ($null -eq $config) { $config = $xml.CreateElement('RunConfiguration'); [void]$run.AppendChild($config) }
    $adapterNodes = @($config.SelectNodes('TestAdaptersPaths'))
    $paths = @($adapterNodes | ForEach-Object { $_.InnerText.Split(';') } | Where-Object { $_ })
    $adapters = $xml.CreateElement('TestAdaptersPaths')
    $adapters.InnerText = (@($paths) + @($CollectorDirectory) | Select-Object -Unique) -join ';'
    foreach ($node in $adapterNodes) { [void]$config.RemoveChild($node) }
    [void]$config.AppendChild($adapters)
    $collections = $run.SelectSingleNode('DataCollectionRunSettings')
    if ($null -eq $collections) { $collections = $xml.CreateElement('DataCollectionRunSettings'); [void]$run.AppendChild($collections) }
    $collectors = $collections.SelectSingleNode('DataCollectors')
    if ($null -eq $collectors) { $collectors = $xml.CreateElement('DataCollectors'); [void]$collections.AppendChild($collectors) }
    $collector = $xml.CreateElement('DataCollector')
    $collector.SetAttribute('friendlyName', 'BotNexus Test Events'); $collector.SetAttribute('enabled','true')
    $collector.SetAttribute('uri','datacollector://BotNexus/TestEvents/1')
    $collector.SetAttribute('assemblyQualifiedName','BotNexus.TestEventCollector.TestEventCollector, BotNexus.TestEventCollector')
    $collector.SetAttribute('codebase',(Join-Path $CollectorDirectory 'BotNexus.TestEventCollector.dll'))
    $configuration = $xml.CreateElement('Configuration'); $directory = $xml.CreateElement('Directory'); $directory.InnerText = $EvidenceDirectory
    [void]$configuration.AppendChild($directory); [void]$collector.AppendChild($configuration); [void]$collectors.AppendChild($collector)
    $xml.Save($Destination)
}

function Get-DiagnosticEvidenceInventory {
    param([string]$Root)
    return @(Get-ChildItem -LiteralPath $Root -File -Recurse | Where-Object { $_.Name -ne 'diagnostic-evidence.json' } | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($Root,$_.FullName).Replace('\','/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}

function Write-DiagnosticEvidence {
    param([string]$Root, [string]$Project, $SourceSnapshot, [string]$RunnerImage, [string]$RunnerDirectory, $Run, [int]$HangSeconds, [int]$OuterSeconds)
    $parsed = Read-DiagnosticEvents -Root $Root
    $events = @($parsed.Events); $invalid = $parsed.Invalid
    $starts = @($events | Where-Object event -CEQ 'TestCaseStart')
    $ends = @($events | Where-Object event -CEQ 'TestCaseEnd')
    $inflight = @($parsed.InFlight)
    $inventory = @(Get-DiagnosticEvidenceInventory $Root)
    $dumps = @($inventory | Where-Object { $_.path -match '\.(dmp|core)$' })
    $stackDumps = @($dumps | Where-Object { Test-DiagnosticStackDump (Join-Path $Root $_.path) })
    $complete = $null -ne $Run -and -not $invalid -and $starts.Count -gt 0 -and ($Run.TimedOut -or $Run.ExitCode -ne 0 -or $inflight.Count -eq 0)
    foreach ($cleanupProperty in @('CleanupComplete','DrainComplete','ReadersClosed')) {
        if ($null -ne $Run -and $null -ne $Run.PSObject.Properties[$cleanupProperty] -and $Run.$cleanupProperty -ne $true) { $complete=$false }
    }
    $needsStack = $null -ne $Run -and ($Run.TimedOut -or $inflight.Count -gt 0)
    $record = [ordered]@{
        version = 1; mode = 'diagnostic'; qualifiesForCoreReceipt = $false
        project = $Project; sourceSnapshot = $SourceSnapshot; runnerImage = $RunnerImage
        runnerFiles = @(Get-DiagnosticEvidenceInventory $RunnerDirectory)
        completedUtc = [DateTime]::UtcNow.ToString('o'); hangSeconds = $HangSeconds; outerSeconds = $OuterSeconds
        process = $Run; eventEvidenceComplete = $complete; startCount = $starts.Count; endCount = $ends.Count
        hangEvidenceComplete = $complete -and (-not $needsStack -or $stackDumps.Count -gt 0)
        stackDumpValidated = $stackDumps.Count -gt 0
        inventoryScope = 'Files present before result.json and artifact-upload timing are finalized; journals are the authoritative event sequence.'
        inFlight = $inflight; hangDumpPresent = $dumps.Count -gt 0; files = $inventory
        sensitivity = 'Private diagnostic evidence: dumps and traces may contain secrets. Do not publish raw artifacts.'
    }
    $record | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Root 'diagnostic-evidence.json') -Encoding utf8NoBOM
    return $record
}

# Failed/hung diagnostic runs still need source attribution, not successful-test certification.
function Assert-DiagnosticSourceProof {
    param($Result, $Evidence, [string]$Digest, [string]$RunId)
    foreach ($contract in @($Result, $Evidence)) {
        if ($null -eq $contract -or $null -eq $contract.PSObject.Properties['sourceSnapshot']) { throw 'Missing diagnostic exact-source proof.' }
        $proof = $contract.sourceSnapshot
        if ($null -eq $proof -or $proof.version -isnot [long] -and $proof.version -isnot [int] -or $proof.version -ne 1 -or $proof.verified -isnot [bool] -or $proof.verified -ne $true -or $proof.digest -isnot [string] -or $proof.digest -cne $Digest -or $proof.runId -isnot [string] -or $proof.runId -cne $RunId -or $contract.mode -cne 'diagnostic') { throw 'Diagnostic source proof mismatch.' }
    }
    if ($Result.runId -cne $RunId) { throw 'Diagnostic run identity mismatch.' }
}

# One parser for final certification and watchdog target discovery. Pair only within the
# collector journal, exact source/name/PID, and strictly increasing sequence/UTC order.
function Read-DiagnosticEvents {
    param([string]$Root, [switch]$Live)
    $events = @(); $hosts = @(); $inflight = @(); $invalid = $false; $incomplete = $false
    foreach ($file in @(Get-ChildItem -LiteralPath $Root -Filter 'events-*.jsonl' -Recurse -File)) {
        $active = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        $sequence = 0L; $lastUtc = [DateTimeOffset]::MinValue; $journalPid = 0L
        # Read one immutable snapshot. Only an unterminated last line is deferred;
        # malformed complete lines remain invalid, and final certification is strict.
        $readStream=[IO.FileStream]::new($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
        $reader=[IO.StreamReader]::new($readStream)
        try { $text=$reader.ReadToEnd() } finally { $reader.Dispose() }
        $lines = @($text -split '\r?\n')
        if ($text.EndsWith("`n")) { $lines = @($lines | Select-Object -SkipLast 1) }
        elseif ($Live -and $text.Length -gt 0) { $incomplete = $true; $lines = @($lines | Select-Object -SkipLast 1) }
        if ($text.Length -eq 0) { $lines = @() }
        foreach ($line in $lines) {
            try {
                $jsonOptions = @{ ErrorAction = 'Stop' }
                # PowerShell 7.6 began coercing ISO strings into DateTime by default.
                if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { $jsonOptions.DateKind = 'String' }
                $e = $line | ConvertFrom-Json @jsonOptions
                foreach ($p in @('event','utc','sequence','processId')) { if ($null -eq $e.PSObject.Properties[$p]) { throw "Missing $p" } }
                if (($e.sequence -isnot [long] -and $e.sequence -isnot [int]) -or $e.sequence -le $sequence -or ($e.processId -isnot [long] -and $e.processId -isnot [int]) -or $e.processId -le 0) { throw 'Invalid sequence/PID.' }
                $utc = [DateTimeOffset]::MinValue
                if ($e.utc -isnot [string] -or $e.utc -cnotmatch 'Z$' -or -not [DateTimeOffset]::TryParse($e.utc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$utc) -or $utc -lt $lastUtc) { throw 'Invalid UTC order.' }
                if ($journalPid -ne 0 -and $journalPid -ne $e.processId) { throw 'Journal process changed.' }
                $sequence = $e.sequence; $lastUtc = $utc; $journalPid = $e.processId
                $e | Add-Member -NotePropertyName journal -NotePropertyValue ([IO.Path]::GetRelativePath($Root,$file.FullName))
                if ($e.event -ceq 'TestHostLaunched') {
                    if (($e.testHostProcessId -isnot [long] -and $e.testHostProcessId -isnot [int]) -or $e.testHostProcessId -le 0 -or $e.testHostStartUtc -isnot [string] -or $e.testHostStartUtc -cnotmatch 'Z$') { throw 'Invalid testhost identity.' }
                    $hostUtc = [DateTimeOffset]::Parse($e.testHostStartUtc, [Globalization.CultureInfo]::InvariantCulture)
                    $hosts += $e; continue
                }
                foreach ($p in @('id','fullyQualifiedName','source')) { if ($null -eq $e.PSObject.Properties[$p]) { throw "Missing $p" } }
                $guid = [guid]::Empty
                if ($e.id -isnot [string] -or -not [guid]::TryParse($e.id,[ref]$guid) -or $guid -eq [guid]::Empty -or $e.source -isnot [string] -or [string]::IsNullOrWhiteSpace($e.source) -or $e.fullyQualifiedName -isnot [string] -or [string]::IsNullOrWhiteSpace($e.fullyQualifiedName)) { throw 'Invalid test identity.' }
                $key = @($guid.ToString(),$e.source,$e.fullyQualifiedName) | ConvertTo-Json -Compress
                if ($e.event -ceq 'TestCaseStart') { if ($active.ContainsKey($key)) { throw 'Duplicate active start.' }; $active.Add($key,$e) }
                elseif ($e.event -ceq 'TestCaseEnd') { if (-not $active.Remove($key)) { throw 'Unpaired/duplicate end.' } }
                else { throw 'Unknown collector event.' }
                $events += $e
            } catch { $invalid = $true }
        }
        $inflight += @($active.Values)
    }
    return [pscustomobject]@{ Events=$events; Hosts=$hosts; InFlight=$inflight; Invalid=$invalid; Incomplete=$incomplete }
}

# Validate the native dump container and actual thread/stack payload, not an extension
# or magic prefix alone. createdump on Linux emits ELF core; Windows blame emits MDMP.
function Test-DiagnosticStackDump {
    param([string]$Path)
    $reader = $null
    try {
        $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path)); $s = $reader.BaseStream
        if ($s.Length -lt 64) { return $false }
        $magic = $reader.ReadUInt32()
        if ($magic -eq 0x464c457f) {
            if ($reader.ReadByte() -ne 2 -or $reader.ReadByte() -ne 1) { return $false }
            $s.Position=16; if ($reader.ReadUInt16() -ne 4) { return $false }
            $s.Position=32; $offset=$reader.ReadUInt64(); $s.Position=54; $size=$reader.ReadUInt16(); $count=$reader.ReadUInt16()
            if ($size -lt 56 -or $count -eq 0 -or $offset + [long]$size*$count -gt $s.Length) { return $false }
            $threads=$false; $memory=$false
            for ($i=0; $i -lt $count; $i++) {
                $s.Position=[long]$offset+$i*$size; $type=$reader.ReadUInt32(); $s.Position+=4; $data=$reader.ReadUInt64(); $s.Position+=16; $bytes=$reader.ReadUInt64()
                if ($data -gt $s.Length -or $bytes -gt $s.Length-$data) { return $false }
                if ($type -eq 1 -and $bytes -gt 0) { $memory=$true }
                if ($type -eq 4) {
                    $s.Position=$data; $end=$data+$bytes
                    while ($s.Position+12 -le $end) {
                        $name=$reader.ReadUInt32(); $desc=$reader.ReadUInt32(); $note=$reader.ReadUInt32()
                        $next=$s.Position+([long][Math]::Ceiling($name/4.0)*4)+([long][Math]::Ceiling($desc/4.0)*4)
                        if ($next -gt $end) { return $false }
                        if ($note -eq 1 -and $desc -ge 112) { $threads=$true }
                        $s.Position=$next
                    }
                }
            }
            return $threads -and $memory
        }
        if ($magic -eq 0x504d444d) {
            $s.Position=8; $count=$reader.ReadUInt32(); $offset=$reader.ReadUInt32()
            if ($count -eq 0 -or [long]$offset+12L*$count -gt $s.Length) { return $false }
            for ($i=0; $i -lt $count; $i++) {
                $s.Position=[long]$offset+12L*$i; $type=$reader.ReadUInt32(); $bytes=$reader.ReadUInt32(); $data=$reader.ReadUInt32()
                if ([long]$data+$bytes -gt $s.Length) { return $false }
                if ($type -eq 3 -and $bytes -ge 52) {
                    $s.Position=$data; $threads=$reader.ReadUInt32()
                    if ($threads -eq 0 -or 4L+48L*$threads -gt $bytes) { return $false }
                    for ($t=0; $t -lt $threads; $t++) {
                        $s.Position=[long]$data+4+48L*$t+32; $stack=$reader.ReadUInt32(); $rva=$reader.ReadUInt32()
                        if ($stack -gt 0 -and [long]$rva+$stack -le $s.Length) { return $true }
                    }
                }
            }
        }
        return $false
    } catch { return $false } finally { if ($null -ne $reader) { $reader.Dispose() } }
}

# Every command (including dump tools) owns a gated wrapper and OS containment.
function New-DiagnosticOwnedProcess {
    param([string]$FilePath, [string[]]$ArgumentList, [string]$Root, [string]$Token)
    if (-not ('DiagnosticContainment' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'DiagnosticContainment.cs') }
    if (-not $IsLinux -and -not $IsWindows) { throw 'Diagnostic containment is unsupported on this platform; command not launched.' }
    $setsid=$null
    if ($IsLinux) { $setsid=Get-Command setsid -CommandType Application -ErrorAction Stop | Select-Object -First 1 }
    $base=Join-Path $Root "owned-$Token"
    $config=@{file=$FilePath; arguments=@($ArgumentList); release="$base.release"; result="$base.result"; identity="$base.identity"; error="$base.error"}
    $config | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$base.json" -Encoding utf8NoBOM
    $pwsh=(Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $info=[Diagnostics.ProcessStartInfo]::new(); $info.UseShellExecute=$false; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    if ($IsLinux) { $info.FileName=$setsid.Source; [void]$info.ArgumentList.Add('--'); [void]$info.ArgumentList.Add($pwsh) } else { $info.FileName=$pwsh }
    foreach ($a in @('-NoProfile','-File',(Join-Path $PSScriptRoot 'DiagnosticOwnedCommand.ps1'),"$base.json")) { [void]$info.ArgumentList.Add($a) }
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$info; $job=$null
    try {
        if ($IsWindows) { $job=[DiagnosticContainment]::new() }
        if (-not $process.Start()) { throw 'Containment wrapper did not start.' }
        if ($IsWindows) { $job.Assign($process.Handle) }
        else {
            $verify=[Diagnostics.Stopwatch]::StartNew()
            while (-not [DiagnosticContainment]::IsSessionLeader($process.Id) -and -not $process.HasExited -and $verify.ElapsedMilliseconds -lt 1000) { Start-Sleep -Milliseconds 10 }
            if ($process.HasExited -or -not [DiagnosticContainment]::IsSessionLeader($process.Id)) { throw 'Linux owned session was not established; command not launched.' }
        }
        # This is the only release boundary. No target can spawn before containment.
        [IO.File]::WriteAllText($config.release,'go')
        return [pscustomobject]@{Process=$process; Job=$job; GroupId=$process.Id; Result=$config.result; Identity=$config.identity; ErrorPath=$config.error; Base=$base; Observed=@{}; Stopped=$false}
    } catch {
        if ($null -ne $job) { try { $job.Kill() } catch { }; $job.Dispose() }
        $launchError=$_
        try { if (-not $process.HasExited) { $process.Kill($true) } } catch { }
        $process.Dispose(); throw $launchError
    }
}

function Get-DiagnosticOwnedMembers {
    param($Owner)
    $members=@()
    if ($IsLinux) {
        foreach ($directory in [IO.Directory]::EnumerateDirectories('/proc')) {
            $processId=0
            if (-not [int]::TryParse([IO.Path]::GetFileName($directory),[ref]$processId)) { continue }
            try {
                $stat=[IO.File]::ReadAllText("$directory/stat"); $fields=$stat.Substring($stat.LastIndexOf(')')+2).Split(' ')
                # Zombies have no running code or open descriptors; init may reap later.
                if ([int]$fields[2] -eq $Owner.GroupId -and [int]$fields[3] -eq $Owner.GroupId -and $fields[0] -ne 'Z') { $members+=$processId }
            } catch [IO.IOException] { } catch [UnauthorizedAccessException] { }
        }
    } else {
        $members=@($Owner.Job.MemberIds)
    }
    foreach ($processId in $members) {
        if ($Owner.Observed.ContainsKey($processId)) { continue }
        $p=$null
        try { $p=[Diagnostics.Process]::GetProcessById($processId); $Owner.Observed[$processId]=[ordered]@{processId=$processId; startUtc=$p.StartTime.ToUniversalTime().ToString('o')} } catch { } finally { if ($null -ne $p) { $p.Dispose(); $p=$null } }
    }
    return $members
}

function Stop-DiagnosticOwnedProcess {
    param($Owner)
    # Always terminate the ownership boundary, even if the command/launcher exited.
    if ($IsLinux) { [DiagnosticContainment]::KillGroup($Owner.GroupId) } else { $Owner.Job.Kill() }
    $Owner.Stopped=$true
}

# Diagnostic-only supervisor. All starts, retries, ownership teardown and drains
# share one clock. Root exit is not completion while owned descendants remain.
function Invoke-DiagnosticProcess {
    param([string]$FilePath, [string[]]$ArgumentList, [string]$LogPath, [int]$TimeoutSeconds,
        [string]$EvidenceRoot, [int]$CaptureReserveSeconds=90, [int]$DumpTimeoutSeconds=60,
        [int]$KillReserveSeconds=5, [int]$PollMilliseconds=100,
        [string]$DumpFilePath='', [string[]]$DumpArgumentPrefix=@(),
        [scriptblock]$ElapsedSecondsProvider=$null)
    if ($TimeoutSeconds -le $CaptureReserveSeconds -or $CaptureReserveSeconds -le ($DumpTimeoutSeconds+$KillReserveSeconds) -or $DumpTimeoutSeconds -le 0 -or $KillReserveSeconds -le 0 -or $PollMilliseconds -le 0) { throw 'Insufficient absolute capture budget.' }
    $clock=[Diagnostics.Stopwatch]::StartNew(); $captures=@(); $owners=@(); $dumpChildren=@(); $pipes=@(); $seen=@{}; $attempts=@{}; $errors=@(); $owner=$null
    # Public helper seam for synthetic fixtures only. Production callers omit it:
    # the same running Stopwatch governs every launch, capture, kill and drain.
    $readElapsed={ param([string]$phase='read')
        if ($null -eq $ElapsedSecondsProvider) { return $clock.Elapsed.TotalSeconds }
        return [double](& $ElapsedSecondsProvider $phase $owner $dumpChildren $captures)
    }
    $timedOut=$false; $exitCode=1; $cleanupComplete=$true; $drainComplete=$true; $readersClosed=$true; $journalInvalid=$false
    try {
        $owner=New-DiagnosticOwnedProcess $FilePath $ArgumentList $EvidenceRoot ([guid]::NewGuid().ToString('N')); $owners+=$owner
        foreach ($pair in @(@($owner.Process.StandardOutput,"$LogPath.stdout"),@($owner.Process.StandardError,"$LogPath.stderr"))) {
            $destination=[IO.File]::Create($pair[1]); $pipes += [pscustomobject]@{Reader=$pair[0]; Destination=$destination; Copy=$pair[0].BaseStream.CopyToAsync($destination)}
        }
        while ($true) {
            $elapsed=& $readElapsed 'poll'
            $members=@(Get-DiagnosticOwnedMembers $owner)
            if (Test-Path -LiteralPath $owner.Result) {
                if (@($members | Where-Object { $_ -ne $owner.GroupId }).Count -eq 0) { $exitCode=[int][IO.File]::ReadAllText($owner.Result); break }
            }
            if ($owner.Process.HasExited) { $errors+='Containment anchor exited unexpectedly.'; $cleanupComplete=$false; break }
            if ($elapsed -ge ($TimeoutSeconds-$KillReserveSeconds)) { $timedOut=$true; break }
            if ($elapsed -ge ($TimeoutSeconds-$CaptureReserveSeconds)) {
                try { $parsed=Read-DiagnosticEvents -Root $EvidenceRoot -Live } catch { $errors+="Live journal read failed: $($_.Exception.Message)"; $parsed=$null }
                if ($null -ne $parsed) {
                    $journalInvalid=$journalInvalid -or $parsed.Invalid
                    foreach ($hostEvent in @($parsed.Hosts)) {
                        $key="$($hostEvent.testHostProcessId):$($hostEvent.testHostStartUtc)"
                        if ($seen.ContainsKey($key)) { continue }
                        # Completed corrupt lines are terminal, not transient append races.
                        if ($parsed.Invalid) { $seen[$key]=$true; $captures += [ordered]@{testHostProcessId=$hostEvent.testHostProcessId; journal=$hostEvent.journal; startedSeconds=$elapsed; exitCode=$null; timedOut=$false; error='Collector journal validation failed (complete line).'; dumpPath=$null}; continue }
                        if ($attempts.ContainsKey($key) -and $attempts[$key] -ge 3) { continue }
                        if (-not $attempts.ContainsKey($key)) { $attempts[$key]=0 }; $attempts[$key]++
                        $record=[ordered]@{testHostProcessId=$hostEvent.testHostProcessId; journal=$hostEvent.journal; startedSeconds=$elapsed; exitCode=$null; timedOut=$false; error=$null; dumpPath=$null; attempts=$attempts[$key]}
                        try {
                            $target=[Diagnostics.Process]::GetProcessById($hostEvent.testHostProcessId)
                            try {
                                $reported=[DateTimeOffset]::Parse($hostEvent.testHostStartUtc,[Globalization.CultureInfo]::InvariantCulture)
                                $actual=[DateTimeOffset]::new($target.StartTime.ToUniversalTime())
                                # Kernel starttime is exact across Linux observers; wall-clock
                                # StartTime is not. Never replace PID-reuse proof with tolerance.
                                $identityMatches=if ($IsLinux) {
                                    if ($null -eq $hostEvent.PSObject.Properties['testHostStartToken']) { $false }
                                    else {
                                        $stat=[IO.File]::ReadAllText("/proc/$($target.Id)/stat")
                                        $token=$stat.Substring($stat.LastIndexOf(')')+2).Split(' ',[StringSplitOptions]::RemoveEmptyEntries)[19]
                                        $token -ceq [string]$hostEvent.testHostStartToken
                                    }
                                } else { $actual.UtcDateTime.Ticks.ToString([Globalization.CultureInfo]::InvariantCulture) -ceq [string]$hostEvent.testHostStartToken }
                                if (-not $identityMatches) { throw 'Testhost PID was reused.' }
                                if ($members -notcontains $target.Id) { throw 'Recorded testhost is outside diagnostic containment.' }
                            } finally { $target.Dispose() }
                            $tool=$DumpFilePath
                            if (-not $tool) {
                                if (-not $IsLinux) { throw 'Absolute native capture currently requires Linux createdump.' }
                                $maps=Get-Content "/proc/$($hostEvent.testHostProcessId)/maps"
                                $core=@($maps | Where-Object { $_ -match '/libcoreclr\.so$' } | Select-Object -First 1)
                                if (-not $core.Count) { throw 'Testhost runtime mapping not found.' }
                                $runtime=($core[0] -split '\s+',6)[5]; $tool=Join-Path (Split-Path $runtime -Parent) 'createdump'
                                if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw 'Runtime createdump is unavailable.' }
                            }
                            $dumpPath=Join-Path $EvidenceRoot "absolute-$($hostEvent.testHostProcessId).dmp"; $record.dumpPath=$dumpPath
                            $dumpOwner=New-DiagnosticOwnedProcess $tool (@($DumpArgumentPrefix)+@('--normal','--name',$dumpPath,[string]$hostEvent.testHostProcessId)) $EvidenceRoot ([guid]::NewGuid().ToString('N')); $owners+=$dumpOwner
                            $dumpChildren += [pscustomobject]@{Owner=$dumpOwner; Record=$record; Deadline=$TimeoutSeconds-$KillReserveSeconds; LaunchConfirmed=$false}
                            foreach ($pair in @(@($dumpOwner.Process.StandardOutput,"$dumpPath.stdout.log"),@($dumpOwner.Process.StandardError,"$dumpPath.stderr.log"))) { $destination=[IO.File]::Create($pair[1]); $pipes += [pscustomobject]@{Reader=$pair[0]; Destination=$destination; Copy=$pair[0].BaseStream.CopyToAsync($destination)} }
                            $seen[$key]=$true; $captures+=$record
                        } catch {
                            $record.error=$_.Exception.Message
                            # Failed launch may retry, bounded to three attempts on the same clock.
                            if ($attempts[$key] -ge 3) { $seen[$key]=$true; $captures+=$record }
                        }
                    }
                }
            }
            foreach ($child in $dumpChildren) {
                $null=Get-DiagnosticOwnedMembers $child.Owner
                if ($null -ne $child.Record.exitCode) { continue }
                if (-not $child.LaunchConfirmed -and (Test-Path -LiteralPath $child.Owner.Identity)) { $child.LaunchConfirmed=$true; $child.Deadline=[Math]::Min((& $readElapsed)+$DumpTimeoutSeconds,$TimeoutSeconds-$KillReserveSeconds) }
                if (Test-Path -LiteralPath $child.Owner.Result) { $child.Record.exitCode=[int][IO.File]::ReadAllText($child.Owner.Result); if (Test-Path -LiteralPath $child.Owner.ErrorPath) { $child.Record.error=[IO.File]::ReadAllText($child.Owner.ErrorPath) }; Stop-DiagnosticOwnedProcess $child.Owner }
                elseif ((& $readElapsed) -ge $child.Deadline) { $child.Record.timedOut=$true; $child.Record.exitCode=1; try { Stop-DiagnosticOwnedProcess $child.Owner } catch { $child.Record.error=$_.Exception.Message; $cleanupComplete=$false } }
            }
            Start-Sleep -Milliseconds ([int][Math]::Min($PollMilliseconds,[Math]::Max(1,($TimeoutSeconds-$KillReserveSeconds-(& $readElapsed))*1000)))
        }
    } finally {
        foreach ($child in $dumpChildren) { if ($null -eq $child.Record.exitCode) { $child.Record.timedOut=$true; $child.Record.exitCode=1 } }
        foreach ($owned in $owners) { try { $null=Get-DiagnosticOwnedMembers $owned; Stop-DiagnosticOwnedProcess $owned } catch { $cleanupComplete=$false; $errors+="Ownership kill failed: $($_.Exception.Message)" } }
        # Confirm termination and pipe completion within, not after, the outer deadline.
        do {
            $remaining=0
            foreach ($owned in $owners) { if ($IsWindows) { $remaining += $owned.Job.Active } else { $remaining += @(Get-DiagnosticOwnedMembers $owned).Count } }
            if ($remaining -eq 0) { break }
            Start-Sleep -Milliseconds 10
        } while ((& $readElapsed) -lt $TimeoutSeconds)
        if ($remaining -gt 0) { $cleanupComplete=$false; $errors+='Owned processes still live at cleanup deadline.' }
        if ($pipes.Count) {
            try { $drainComplete=[Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($pipes | ForEach-Object Copy)).Wait([int][Math]::Max(0,($TimeoutSeconds-(& $readElapsed))*1000)) } catch { $drainComplete=$false; $errors+="Pipe copy failed: $($_.Exception.Message)" }
        }
        foreach ($pipe in $pipes) { try { $pipe.Reader.Dispose() } catch { $readersClosed=$false; $errors+="Reader close failed: $($_.Exception.Message)" } }
        # Reader disposal cancels blocked source reads. Never claim success on a
        # false Wait result, faulted copy or a destination closed while still copying.
        foreach ($pipe in $pipes) { if (-not $pipe.Copy.IsCompletedSuccessfully) { $drainComplete=$false }; $pipe.Destination.Dispose() }
        foreach ($owned in $owners) { $owned.Process.Dispose(); if ($null -ne $owned.Job) { $owned.Job.Dispose() }; foreach ($suffix in @('.json','.release','.result','.identity','.error')) { Remove-Item -LiteralPath ($owned.Base+$suffix) -ErrorAction SilentlyContinue } }
        $clock.Stop()
        $parts=@(); foreach ($part in @("$LogPath.stdout","$LogPath.stderr")) { if (Test-Path $part) { $parts+=Get-Content $part -Raw; Remove-Item $part } }; Set-Content $LogPath ($parts -join [Environment]::NewLine)
        if ($timedOut -and $captures.Count -eq 0) { $captures += [ordered]@{testHostProcessId=$null; startedSeconds=$null; exitCode=$null; timedOut=$false; error='No valid TestHostLaunched target was available before the absolute deadline.'; dumpPath=$null} }
        ConvertTo-Json -InputObject @($captures) -Depth 6 | Set-Content (Join-Path $EvidenceRoot 'absolute-captures.json')
    }
    if (-not $cleanupComplete -or -not $drainComplete -or -not $readersClosed -or $journalInvalid) { $exitCode=1 }
    return [pscustomobject]@{ExitCode=$exitCode; TimedOut=$timedOut; ElapsedSeconds=(& $readElapsed); Captures=@($captures); CleanupComplete=$cleanupComplete; DrainComplete=$drainComplete; ReadersClosed=$readersClosed; CleanupErrors=@($errors); OwnedProcesses=@($owners | ForEach-Object { @($_.Observed.Values) })}
}
