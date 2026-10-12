# Safe focused TDD: pure fixtures, a synthetic pwsh child, and a framework-only journal.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
$helper = Join-Path $PSScriptRoot '../RunnerDiagnostic.ps1'
if (-not (Test-Path -LiteralPath $helper)) { throw 'RED #4777: RunnerDiagnostic.ps1 does not exist.' }
. $helper
. (Join-Path $PSScriptRoot '../RunnerTimeout.ps1')
$count = 0
function Check([bool]$condition, [string]$reason) { if (-not $condition) { throw $reason }; $script:count++ }
function Reject([scriptblock]$body) { $threw = $false; try { & $body | Out-Null } catch { $threw = $true }; Check $threw 'Expected fail-closed rejection.' }
$root = Join-Path ([IO.Path]::GetTempPath()) ('diagnostic4777-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory((Join-Path $root 'tests/safe')) | Out-Null
try {
    $project = 'tests/safe/Example.Tests.csproj'
    Set-Content -LiteralPath (Join-Path $root $project) -Value '<Project><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>'
    Check ((Resolve-DiagnosticProject $root $project) -eq (Join-Path $root $project)) 'Named test project not resolved.'
    foreach ($invalid in @('../escape.csproj','/tests/safe/Example.Tests.csproj','tests/*/*.csproj','tests/safe/../safe/Example.Tests.csproj','tests\safe\Example.Tests.csproj','tests/safe/missing.csproj','tests/safe/Example.Tests.csproj;bad')) { Reject { Resolve-DiagnosticProject $root $invalid } }
    Set-Content -LiteralPath (Join-Path $root 'tests/safe/Support.csproj') -Value '<Project><PropertyGroup><IsTestProject>false</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>'
    Reject { Resolve-DiagnosticProject $root 'tests/safe/Support.csproj' }
    Set-Content -LiteralPath (Join-Path $root 'tests/safe/Library.csproj') -Value '<Project />'
    Reject { Resolve-DiagnosticProject $root 'tests/safe/Library.csproj' }
    Reject { Get-DiagnosticArguments -Project $project -ResultsRoot $root -SettingsPath 'settings with spaces' -OuterSeconds 180 -HangSeconds 120 }
    Reject { Get-DiagnosticArguments -Project $project -ResultsRoot $root -SettingsPath 'settings' -OuterSeconds 100 -HangSeconds 0 }
    $argsList = @(Get-DiagnosticArguments -Project $project -ResultsRoot $root -SettingsPath 'settings with spaces' -OuterSeconds 300 -HangSeconds 120)
    Check ($argsList -contains '--blame-hang') 'Hang collector missing.'
    Check ($argsList[$argsList.IndexOf('--blame-hang-dump-type') + 1] -eq 'mini') 'Dump must be mini.'
    Check ($argsList[$argsList.IndexOf('--blame-hang-timeout') + 1] -eq '120s') 'Hang timeout changed.'
    Check ($argsList -contains '--diag') 'Platform trace missing.'
    $child = Join-Path $root 'echo.ps1'
    Set-Content -LiteralPath $child -Value '[Environment]::GetCommandLineArgs() | Select-Object -Skip 4 | ConvertTo-Json -Compress'
    $echo = Invoke-BoundedProcess -FilePath 'pwsh' -ArgumentList (@('-NoProfile','-File',$child) + $argsList) -TimeoutSeconds 20 -LogPath (Join-Path $root 'echo.log') -PollMilliseconds 50
    Check ($echo.ExitCode -eq 0 -and -not $echo.TimedOut) 'Synthetic argument child failed.'
    $actual = @(Get-Content -LiteralPath (Join-Path $root 'echo.log') -Raw | ConvertFrom-Json)
    Check (($actual | ConvertTo-Json -Compress) -ceq ($argsList | ConvertTo-Json -Compress)) "Arguments were split or changed. actual=$($actual | ConvertTo-Json -Compress) expected=$($argsList | ConvertTo-Json -Compress)"
    Add-Type -Path (Join-Path $repo 'tools/BotNexus.TestEventCollector/EventJournal.cs')
    $journalPath = Join-Path $root 'events.jsonl'
    $journal = [BotNexus.TestEventCollector.EventJournal]::new($journalPath)
    $id = [guid]::NewGuid()
    $journal.Record('TestCaseStart',$id,'Example.Hangs','Case "quoted"','Example.Tests.dll')
    $first = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    Check ($first.event -eq 'TestCaseStart' -and $first.id -eq $id.ToString()) 'Start identity not durably flushed before disposal.'
    Check ((Get-Content -LiteralPath $journalPath -Raw) -match '"utc":"[^"]+Z"' -and $first.sequence -eq 1 -and $first.fullyQualifiedName -eq 'Example.Hangs') 'UTC/sequence/name absent.'
    $journal.Record('TestCaseEnd',$id,'Example.Hangs','Case "quoted"','Example.Tests.dll')
    $journal.Dispose()
    $lines = @(Get-Content -LiteralPath $journalPath)
    Check ($lines.Count -eq 2 -and ($lines[1] | ConvertFrom-Json).event -eq 'TestCaseEnd') 'End event missing.'
    [IO.File]::WriteAllBytes((Join-Path $root 'hang.dmp'),[byte[]]@(1,2,3))
    $inventory = @(Get-DiagnosticEvidenceInventory -Root $root)
    Check (@($inventory | Where-Object path -eq 'hang.dmp').Count -eq 1) 'Dump missing from evidence inventory.'
    Check (($inventory | Where-Object path -eq 'hang.dmp').sha256.Length -eq 64) 'Evidence not content addressed.'
    $collector = Get-Content -LiteralPath (Join-Path $repo 'tools/BotNexus.TestEventCollector/TestEventCollector.cs') -Raw
    Check ($collector.Contains('events.TestCaseStart +=') -and $collector.Contains('events.TestCaseEnd +=')) 'Collector does not subscribe to actual test events.'
    $sender = Get-Content -LiteralPath (Join-Path $repo 'scripts/repo/Invoke-AzureBuildTest.ps1') -Raw
    Check ($sender.Contains("`$Mode -ne 'diagnostic' -and")) 'Diagnostic may write validation receipt.'
    Check ($sender.Contains("name = 'DIAGNOSTIC_PROJECT'") -and $sender.Contains("name = 'RUNNER_IMAGE'")) 'Project/image not forwarded.'
    $entry = Get-Content -LiteralPath (Join-Path $repo 'infra/buildtest/runner/entrypoint.ps1') -Raw
    Check ($entry.Contains("'diagnostic' {") -and $entry.Contains('Write-DiagnosticEvidence')) 'Diagnostic path/evidence finalization absent.'
    $settings = Join-Path $root 'diagnostic.runsettings'
    $sourceSettings = Join-Path $root 'original.runsettings'
    Set-Content -LiteralPath $sourceSettings -Value '<RunSettings><RunConfiguration><EnvironmentVariables><BOTNEXUS_HOME>isolated-home</BOTNEXUS_HOME></EnvironmentVariables></RunConfiguration></RunSettings>'
    New-DiagnosticSettings -SourceSettings $sourceSettings -Destination $settings -CollectorDirectory $root -EvidenceDirectory $root
    [xml]$generated = Get-Content -LiteralPath $settings -Raw
    Check ($generated.SelectSingleNode('//DataCollector[@friendlyName="BotNexus Test Events"]').GetAttribute('enabled') -eq 'true') 'Custom collector disabled.'
    Check ($generated.SelectSingleNode('//EnvironmentVariables/BOTNEXUS_HOME').InnerText -eq 'isolated-home') 'Existing test isolation settings lost.'
    $fakeRun = [pscustomobject]@{ ExitCode = 1; TimedOut = $true }
    Copy-Item -LiteralPath $journalPath -Destination (Join-Path $root 'events-fixture.jsonl')
    $evidence = Write-DiagnosticEvidence -Root $root -Project $project -SourceSnapshot @{digest='exact'} -RunnerImage 'image@sha256:example' -RunnerDirectory (Join-Path $repo 'infra/buildtest/runner') -Run $fakeRun -HangSeconds 120 -OuterSeconds 300
    Check ($evidence.eventEvidenceComplete -and $evidence.hangDumpPresent -and -not $evidence.qualifiesForCoreReceipt) 'Timeout evidence/dump/receipt separation lost.'
    Set-Content -LiteralPath (Join-Path $root 'events-fixture.jsonl') -Value ($lines[0])
    $evidence = Write-DiagnosticEvidence -Root $root -Project $project -SourceSnapshot @{digest='exact'} -RunnerImage 'image' -RunnerDirectory (Join-Path $repo 'infra/buildtest/runner') -Run $fakeRun -HangSeconds 120 -OuterSeconds 300
    Check ($evidence.inFlight.Count -eq 1 -and $evidence.inFlight[0].id -eq $id.ToString()) 'In-flight identity lost on timeout.'
    Remove-Item -LiteralPath (Join-Path $root 'hang.dmp')
    $missingDump = Write-DiagnosticEvidence -Root $root -Project $project -SourceSnapshot @{digest='exact'} -RunnerImage 'image' -RunnerDirectory (Join-Path $repo 'infra/buildtest/runner') -Run $fakeRun -HangSeconds 120 -OuterSeconds 300
    Check (-not $missingDump.hangEvidenceComplete) 'In-flight test without hang dump certified.'
    Set-Content -LiteralPath (Join-Path $root 'events-fixture.jsonl') -Value '{broken'
    $evidence = Write-DiagnosticEvidence -Root $root -Project $project -SourceSnapshot @{digest='exact'} -RunnerImage 'image' -RunnerDirectory (Join-Path $repo 'infra/buildtest/runner') -Run $fakeRun -HangSeconds 120 -OuterSeconds 300
    Check (-not $evidence.eventEvidenceComplete) 'Malformed evidence certified.'
    Remove-Item -LiteralPath (Join-Path $root 'events-fixture.jsonl')
    $evidence = Write-DiagnosticEvidence -Root $root -Project $project -SourceSnapshot @{digest='exact'} -RunnerImage 'image' -RunnerDirectory (Join-Path $repo 'infra/buildtest/runner') -Run $fakeRun -HangSeconds 120 -OuterSeconds 300
    Check (-not $evidence.eventEvidenceComplete) 'Missing collector events certified.'
    $hangChild = Join-Path $root 'synthetic-hang.ps1'
    Set-Content -LiteralPath $hangChild -Value '[IO.File]::WriteAllText($args[0],"started"); [Console]::WriteLine("synthetic-hang-started"); [Threading.ManualResetEvent]::new($false).WaitOne() | Out-Null'
    $marker = Join-Path $root 'started.txt'
    $hangLog = Join-Path $root 'hang.log'
    $hang = Invoke-BoundedProcess -FilePath 'pwsh' -ArgumentList @('-NoProfile','-File',$hangChild,$marker) -TimeoutSeconds 3 -PollMilliseconds 50 -LogPath $hangLog
    Check ($hang.TimedOut -and $hang.ExitCode -eq 1 -and $hang.ElapsedSeconds -lt 10) 'Synthetic hang not bounded.'
    Check ((Test-Path -LiteralPath $marker) -and (Get-Content -LiteralPath $hangLog -Raw).Contains('synthetic-hang-started')) 'Synthetic timeout lost pre-kill evidence.'
    $proof = [pscustomobject]@{ version=1; verified=$true; digest='exact'; runId='run-4777' }
    $failedResult = [pscustomobject]@{ mode='diagnostic'; runId='run-4777'; exitCode=1; sourceSnapshot=$proof }
    $proofEvidence = [pscustomobject]@{ mode='diagnostic'; sourceSnapshot=$proof }
    Assert-DiagnosticSourceProof -Result $failedResult -Evidence $proofEvidence -Digest 'exact' -RunId 'run-4777'
    Check $true 'Failed diagnostic source proof should be accepted without successful-test certification.'
    Reject { Assert-DiagnosticSourceProof -Result $failedResult -Evidence $proofEvidence -Digest 'different' -RunId 'run-4777' }
    Reject { Assert-DiagnosticSourceProof -Result $failedResult -Evidence $proofEvidence -Digest 'exact' -RunId 'different' }
    $bicep = Get-Content -LiteralPath (Join-Path $repo 'infra/buildtest/main.bicep') -Raw
    Check ($bicep.Contains('allowBlobPublicAccess: false') -and $bicep.Contains('allowSharedKeyAccess: false')) 'Sensitive storage privacy weakened.'
    Write-Output "RESULT passed=$count failed=0"
}
finally {
    if (Get-Variable journal -ErrorAction SilentlyContinue) { $journal.Dispose() }
    Remove-Item -LiteralPath $root -Recurse -Force
}
