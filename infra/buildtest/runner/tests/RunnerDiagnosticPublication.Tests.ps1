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
    foreach ($scenario in @(@{Name='success'; Code=0},@{Name='nonzero'; Code=23},@{Name='launch failure'; Code=1})) {
        Case "owned completion is atomic: $($scenario.Name)" {
            $directory=Join-Path $root ([guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($directory)
            $result=Join-Path $directory 'command.result'; $configuration=Join-Path $directory 'command.json'
            $config=@{file='pwsh';arguments=@('-NoProfile','-Command',"exit $($scenario.Code)");release=(Join-Path $directory 'release');result=$result;identity=(Join-Path $directory 'identity');error=(Join-Path $directory 'error')}
            if ($scenario.Name -eq 'launch failure') { $config.file=Join-Path $directory 'missing-executable' }
            $config | ConvertTo-Json | Set-Content $configuration
            [IO.File]::WriteAllText($config.release,'go')
            # Execute the candidate wrapper, changing only its WriteAllText binding.
            # The test double exposes the create-before-write window deterministically.
            $driver=Join-Path $directory 'publication.ps1'
            Set-Content $driver '. $args[0]; [DiagnosticFixturePublication]::Result=$args[3]; $source=[IO.File]::ReadAllText($args[1]).Replace("[IO.File]::WriteAllText(","[DiagnosticFixturePublication]::WriteAllText("); & ([scriptblock]::Create($source)) -Configuration $args[2]'
            $owner=New-DiagnosticOwnedProcess 'pwsh' @('-NoProfile','-File',$driver,(Join-Path $PSScriptRoot 'DiagnosticFixtureClock.ps1'),(Join-Path $PSScriptRoot '../DiagnosticOwnedCommand.ps1'),$configuration,$result) $directory 'publication'
            try {
                [DiagnosticFixtureSignal]::Wait("$result.opened")
                $opened=if ([IO.File]::Exists("$result.tmp")) { "$result.tmp" } else { $result }
                $visible=[IO.File]::Exists($result)
                if ($visible) {
                    $reader=[IO.StreamReader]::new([IO.FileStream]::new($result,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite))
                    try { $partial=$reader.ReadToEnd(); "PROOF partialVisible=True bytes=$($partial.Length) legacyExitCode=$([int]$partial)" }
                    finally { $reader.Dispose() }
                }
                Require (-not $visible) 'Completion became visible before exit-code bytes were written.'
                Require ([IO.File]::Exists($opened) -and ([IO.FileInfo]::new($opened)).Length -eq 0) 'Handshake did not pause an empty publication file.'
                [IO.File]::WriteAllText("$result.continue",'go')
                [DiagnosticFixtureSignal]::Wait($result)
                $reader=[IO.StreamReader]::new([IO.FileStream]::new($result,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)))
                try { $published=$reader.ReadToEnd() } finally { $reader.Dispose() }
                Require ($published -ceq [string]$scenario.Code) 'Published exit code was lost or changed.'
                Require (-not [IO.File]::Exists($opened)) 'Publication temporary file was not renamed.'
                if ($scenario.Name -eq 'launch failure') { Require ([IO.File]::Exists($config.error) -and [IO.File]::ReadAllText($config.error).Length -gt 0) 'Launch failure error was lost.' }
                "PROOF partialVisible=False publishedExitCode=$published"
            } finally {
                [IO.File]::WriteAllText("$result.continue",'go')
                Stop-DiagnosticOwnedProcess $owner
                Require ($owner.Process.WaitForExit(5000)) 'Publication containment anchor survived cleanup.'
                if ($IsWindows) { Require ($owner.Job.Active -eq 0) 'Publication descendants survived cleanup.' }
                else { Require (@(Get-DiagnosticOwnedMembers $owner).Count -eq 0) 'Publication descendants survived cleanup.' }
                $owner.Process.Dispose(); if ($null -ne $owner.Job) { $owner.Job.Dispose() }
            }
        }
    }
    "RESULT passed=$passed failed=$($failed.Count)"
    if($failed.Count) { throw "Regressions: $($failed -join ', ')" }
} finally { Remove-Item $root -Recurse -Force }
