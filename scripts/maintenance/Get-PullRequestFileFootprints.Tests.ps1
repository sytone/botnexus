[CmdletBinding()]
param(
    [string]$CollectorPath = (Join-Path $PSScriptRoot 'Get-PullRequestFileFootprints.ps1'),
    [string]$ScratchRoot = ([IO.Path]::GetTempPath())
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$results = [Collections.Generic.List[object]]::new()
$scratch = Join-Path $ScratchRoot ('pr-footprints-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($scratch)
$environmentBefore = @{}
foreach ($name in @('GH_TOKEN', 'GITHUB_TOKEN', 'GIT_CONFIG_COUNT', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_SYSTEM')) {
    $environmentBefore[$name] = [Environment]::GetEnvironmentVariable($name)
}

function Assert-That([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Test-Case([string]$Name, [scriptblock]$Body) {
    try { & $Body; $results.Add([pscustomobject]@{ name = $Name; passed = $true; error = $null }) }
    catch { $results.Add([pscustomobject]@{ name = $Name; passed = $false; error = $_.Exception.Message }) }
}
function New-Files([int]$Count) {
    return @(for ($i = 1; $i -le $Count; $i++) { 'src/file-{0:D3}.cs' -f $i })
}
function New-Fixture([string[]]$Files, [int]$Expected = -1) {
    if ($Expected -lt 0) { $Expected = $Files.Count }
    $metadata = @{ number = 3557; changed_files = $Expected; head = @{ sha = ('a' * 40) }; base = @{ sha = ('b' * 40) } } | ConvertTo-Json -Depth 5 -Compress
    $responses = @{ 'repos/owner/repo/pulls/3557' = $metadata }
    for ($page = 1; $page -le [Math]::Floor($Files.Count / 100) + 1; $page++) {
        $rows = @($Files | Select-Object -Skip (($page - 1) * 100) -First 100 | ForEach-Object { @{ filename = $_; status = 'modified' } })
        $responses["repos/owner/repo/pulls/3557/files?per_page=100&page=$page"] = ConvertTo-Json -InputObject $rows -Compress
    }
    return @{ responses = $responses; calls = [Collections.Generic.List[string]]::new() }
}
function Invoke-Fixture([hashtable]$Fixture, [int[]]$Numbers = @(3557)) {
    $request = {
        param([string]$Endpoint)
        $Fixture.calls.Add($Endpoint)
        if (-not $Fixture.responses.ContainsKey($Endpoint)) { throw "Fixture missing response: $Endpoint" }
        $value = $Fixture.responses[$Endpoint]
        if ($value -is [scriptblock]) { return & $value }
        return $value
    }.GetNewClosure()
    return & $CollectorPath -Repository 'owner/repo' -PullRequestNumbers $Numbers -ApiRequest $request
}
function Assert-Rejected([hashtable]$Fixture, [string]$Pattern, [int[]]$Numbers = @(3557)) {
    $output = [Collections.Generic.List[object]]::new()
    $caught = $null
    try { Invoke-Fixture $Fixture $Numbers | ForEach-Object { $output.Add($_) } }
    catch { $caught = $_.Exception.Message }
    Assert-That ($null -ne $caught) 'Expected collection to fail, but it succeeded.'
    Assert-That ($caught -match $Pattern) "Wrong rejection: $caught"
    Assert-That ($output.Count -eq 0) 'Failure leaked a partial or empty-success ownership map.'
}

try {
    Test-Case 'Collect122AndPlannerBlocksPath122' {
        $files = New-Files 122
        $fixture = New-Fixture $files
        $map = Invoke-Fixture $fixture
        Assert-That ($map.isComplete -eq $true) 'Map not complete.'
        Assert-That ($map.pullRequests.Count -eq 1) 'Missing PR evidence.'
        $pr = $map.pullRequests[0]
        Assert-That ($pr.expectedCount -eq 122 -and $pr.actualCount -eq 122 -and $pr.isComplete) '122-path count evidence incorrect.'
        Assert-That (($pr.files -join "`n") -ceq ($files -join "`n")) 'Exact ordered paths not retained.'
        Assert-That ($pr.pages.Count -eq 2 -and $pr.pages[0].actualCount -eq 100 -and $pr.pages[1].actualCount -eq 22) 'Page evidence incorrect.'
        Assert-That ($fixture.calls.Contains('repos/owner/repo/pulls/3557/files?per_page=100&page=2')) 'Later page was not requested.'
        $state = @{
            cycleId = 'footprint-regression'; validationMode = 'remote'; openPrCount = 1
            budgets = @{ implementation = 2; repair = 1; recovery = 1; maxImplementationStartsPerCycle = 4; openPrSoftCap = 5 }
            remoteValidation = @{ active = 0; maxConcurrent = 2; committedCost = 0; maxCost = 120 }
            reservedFiles = @($map.reservedFiles); workers = @()
            candidates = @(@{ id = 'path-122'; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @($files[121]) })
        }
        $statePath = Join-Path $scratch 'planner-state.json'
        $state | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $statePath -Encoding utf8NoBOM
        $plan = & (Join-Path $PSScriptRoot 'Get-MaintenanceDispatchPlan.ps1') -StatePath $statePath
        Assert-That ($plan.dispatch.Count -eq 0) 'Planner admitted the candidate on path 122.'
        Assert-That ($plan.blockers.Count -eq 1 -and $plan.blockers[0].reason -eq 'file-overlap') 'Planner did not report exact overlap.'
    }
    foreach ($count in @(1, 99, 100, 101, 200, 301)) {
        Test-Case "PreserveExactPaths$count" {
            $files = New-Files $count
            $fixture = New-Fixture $files
            $map = Invoke-Fixture $fixture
            $pr = $map.pullRequests[0]
            Assert-That ($map.isComplete -and $pr.isComplete -and $pr.actualCount -eq $count -and $pr.expectedCount -eq $count) 'Wrong count/completion.'
            Assert-That (($pr.files -join "`n") -ceq ($files -join "`n")) 'Paths altered or omitted.'
            Assert-That ($pr.pages.Count -eq [Math]::Floor($count / 100) + 1) 'Missing terminal-page evidence.'
        }
    }
    Test-Case 'PreserveCaseUnicodeWhitespaceAndDeletedPaths' {
        $files = @('src/A.cs', 'src/a.cs', 'docs/space name.md', 'docs/文書.md', ' old/deleted.cs ')
        $map = Invoke-Fixture (New-Fixture $files)
        Assert-That (($map.reservedFiles -join "`n") -ceq ($files -join "`n")) 'Path normalization lost exact names.'
    }
    Test-Case 'VerifiedZeroRequiresMetadataAndEmptyPage' {
        $fixture = New-Fixture @()
        $map = Invoke-Fixture $fixture
        Assert-That ($map.isComplete -and $map.pullRequests[0].actualCount -eq 0 -and $map.pullRequests[0].pages.Count -eq 1) 'Zero-count PR was not verified.'
        Assert-That ($fixture.calls.Count -eq 3) 'Zero-count PR skipped metadata/page verification.'
    }
    Test-Case 'RejectMissingLaterPage' {
        $fixture = New-Fixture (New-Files 122)
        $fixture.responses.Remove('repos/owner/repo/pulls/3557/files?per_page=100&page=2')
        Assert-Rejected $fixture 'missing response'
    }
    Test-Case 'RejectApiFailure' {
        $fixture = New-Fixture (New-Files 122)
        $fixture.responses['repos/owner/repo/pulls/3557/files?per_page=100&page=2'] = { throw 'HTTP 503 fixture failure' }
        Assert-Rejected $fixture '503'
    }
    Test-Case 'RejectCountMismatch' {
        Assert-Rejected (New-Fixture (New-Files 99) 122) 'count mismatch'
    }
    Test-Case 'RejectEmptyLaterPage' {
        $fixture = New-Fixture (New-Files 122)
        $fixture.responses['repos/owner/repo/pulls/3557/files?per_page=100&page=2'] = '[]'
        Assert-Rejected $fixture 'count mismatch'
    }
    Test-Case 'RejectDuplicatePage' {
        $fixture = New-Fixture (New-Files 200)
        $fixture.responses['repos/owner/repo/pulls/3557/files?per_page=100&page=2'] = $fixture.responses['repos/owner/repo/pulls/3557/files?per_page=100&page=1']
        Assert-Rejected $fixture 'duplicate'
    }
    Test-Case 'RejectDuplicateWithinPage' {
        Assert-Rejected (New-Fixture @('src/a.cs', 'src/a.cs')) 'duplicate'
    }
    foreach ($bad in @('', 'null', '{}', '{"message":"API failure"}', 'not json', '[{"filename":""}]', '[{"filename":null}]', '[{}]', '[{"filename":123}]')) {
        Test-Case "RejectMalformedPage:$bad" {
            $fixture = New-Fixture @('src/a.cs')
            $fixture.responses['repos/owner/repo/pulls/3557/files?per_page=100&page=1'] = $bad
            Assert-Rejected $fixture 'JSON|array|filename|response'
        }
    }
    foreach ($bad in @('{}', 'null', '[]', '{"number":3557,"changed_files":"1"}', '{"number":3557,"changed_files":-1}', '{"number":3557,"changed_files":1.5}')) {
        Test-Case "RejectMalformedMetadata:$bad" {
            $fixture = New-Fixture @('src/a.cs')
            $fixture.responses['repos/owner/repo/pulls/3557'] = $bad
            Assert-Rejected $fixture 'metadata|JSON'
        }
    }
    foreach ($field in @('head', 'base', 'changed_files')) {
        Test-Case "RejectChangedSnapshot:$field" {
            $fixture = New-Fixture @('src/a.cs')
            $original = $fixture.responses['repos/owner/repo/pulls/3557']
            $counter = @{ value = 0 }
            $fixture.responses['repos/owner/repo/pulls/3557'] = {
                $counter.value++
                $metadata = $original | ConvertFrom-Json -AsHashtable
                if ($counter.value -gt 1) {
                    if ($field -eq 'changed_files') { $metadata.changed_files = 2 }
                    else { $metadata[$field].sha = 'c' * 40 }
                }
                $metadata | ConvertTo-Json -Depth 5 -Compress
            }.GetNewClosure()
            Assert-Rejected $fixture 'changed during collection'
        }
    }
    Test-Case 'NoPartialMapWhenSecondPrFails' {
        $fixture = New-Fixture @('src/a.cs')
        Assert-Rejected $fixture 'missing response' @(3557, 3558)
    }
    Test-Case 'MultiplePrsKeepPerPrEvidenceAndUnion' {
        $fixture = New-Fixture @('shared.cs', 'first.cs')
        $fixture.responses['repos/owner/repo/pulls/3558'] = '{"number":3558,"changed_files":2,"head":{"sha":"cccccccccccccccccccccccccccccccccccccccc"},"base":{"sha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}'
        $fixture.responses['repos/owner/repo/pulls/3558/files?per_page=100&page=1'] = '[{"filename":"shared.cs","status":"modified"},{"filename":"second.cs","status":"added"}]'
        $map = Invoke-Fixture $fixture @(3557, 3558)
        Assert-That ($map.pullRequests.Count -eq 2 -and $map.reservedFiles.Count -eq 3) 'PR records or union lost.'
        Assert-That ($map.pullRequests[1].actualCount -eq 2 -and $map.pullRequests[1].isComplete) 'Second PR evidence incorrect.'
    }
    Test-Case 'RejectDuplicatePrInput' { Assert-Rejected (New-Fixture @('src/a.cs')) 'duplicate' @(3557, 3557) }
    Test-Case 'RejectEmptyPrInput' { Assert-Rejected (New-Fixture @()) 'empty|at least|argument' @() }
    Test-Case 'RejectOversizedPage' {
        $fixture = New-Fixture (New-Files 101)
        $fixture.responses['repos/owner/repo/pulls/3557/files?per_page=100&page=1'] = ConvertTo-Json -InputObject @((New-Files 101) | ForEach-Object { @{ filename = $_ } }) -Compress
        Assert-Rejected $fixture 'oversized'
    }
    Test-Case 'RejectMoreFilesThanExpected' { Assert-Rejected (New-Fixture (New-Files 101) 1) 'count mismatch' }
    Test-Case 'RejectMultipleTransportOutputs' {
        $fixture = New-Fixture @('src/a.cs')
        $fixture.responses['repos/owner/repo/pulls/3557/files?per_page=100&page=1'] = { '[]'; '[]' }
        Assert-Rejected $fixture 'JSON response'
    }
    Test-Case 'RejectObjectTransportOutput' {
        $fixture = New-Fixture @('src/a.cs')
        $fixture.responses['repos/owner/repo/pulls/3557/files?per_page=100&page=1'] = { @{ filename = 'src/a.cs' } }
        Assert-Rejected $fixture 'JSON response'
    }
    Test-Case 'RejectNonpositivePrInput' { Assert-Rejected (New-Fixture @('src/a.cs')) 'invalid PR number' @(0) }
    Test-Case 'RenamePlannerBlocksBothSidesAndAdmitsDisjoint' {
        $fixture = New-Fixture @('new.cs')
        $fixture.responses['repos/owner/repo/pulls/3557/files?per_page=100&page=1'] = '[{"filename":"new.cs","status":"renamed","previous_filename":"old.cs"}]'
        $map = Invoke-Fixture $fixture
        $state = @{
            cycleId = 'rename-regression'; validationMode = 'remote'; openPrCount = 1
            budgets = @{ implementation = 3; repair = 1; recovery = 1; maxImplementationStartsPerCycle = 4; openPrSoftCap = 5 }
            remoteValidation = @{ active = 0; maxConcurrent = 2; committedCost = 0; maxCost = 120 }
            reservedFiles = @($map.reservedFiles); workers = @()
            candidates = @(foreach ($path in @('old.cs', 'new.cs', 'disjoint.cs')) {
                @{ id = $path; lane = 'implementation'; trusted = $true; decisionFree = $true; files = @($path) }
            })
        }
        $statePath = Join-Path $scratch 'rename-state.json'
        $state | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $statePath -Encoding utf8NoBOM
        $plan = & (Join-Path $PSScriptRoot 'Get-MaintenanceDispatchPlan.ps1') -StatePath $statePath
        Assert-That (@($plan.blockers | Where-Object { $_.id -eq 'old.cs' -and $_.reason -eq 'file-overlap' }).Count -eq 1) 'Planner admitted the old side of a rename.'
        Assert-That (@($plan.blockers | Where-Object { $_.id -eq 'new.cs' -and $_.reason -eq 'file-overlap' }).Count -eq 1) 'Planner admitted the new side of a rename.'
        Assert-That ($plan.dispatch.Count -eq 1 -and $plan.dispatch[0].id -eq 'disjoint.cs') 'Disjoint candidate should be admitted alone.'
        Assert-That ($map.isComplete -and $map.pullRequests[0].expectedCount -eq 1 -and $map.pullRequests[0].actualCount -eq 1) 'Rename must count as one changed record.'
        Assert-That ($map.reservedFiles.Count -eq 2 -and $map.pullRequests[0].reservedFiles.Count -eq 2) 'Both ownership aliases must be evidenced.'
        Assert-That ($map.pullRequests[0].files.Count -eq 1 -and $map.pullRequests[0].files[0] -ceq 'new.cs') 'Record filename compatibility changed.'
    }
    foreach ($bad in @(
        '{"filename":"new.cs","status":"renamed"}',
        '{"filename":"new.cs","status":"renamed","previous_filename":null}',
        '{"filename":"new.cs","status":"renamed","previous_filename":" "}',
        '{"filename":"new.cs","status":"renamed","previous_filename":42}',
        '{"filename":"new.cs","status":"renamed","previous_filename":["old.cs"]}',
        '{"filename":"new.cs","status":"renamed","previous_filename":"new.cs"}',
        '{"filename":"new.cs"}',
        '{"filename":"new.cs","status":null}',
        '{"filename":"new.cs","status":42}',
        '{"filename":"new.cs","status":"mystery"}',
        '{"filename":"new.cs","status":"modified","previous_filename":"old.cs"}'
    )) {
        Test-Case "RejectRenameMetadata:$bad" {
            $fixture = New-Fixture @('new.cs')
            $fixture.responses['repos/owner/repo/pulls/3557/files?per_page=100&page=1'] = "[$bad]"
            Assert-Rejected $fixture 'status|alias'
        }
    }
    Test-Case 'RejectDuplicateRenameAliases' {
        $fixture = New-Fixture @('new1.cs', 'new2.cs')
        $fixture.responses['repos/owner/repo/pulls/3557/files?per_page=100&page=1'] = '[{"filename":"new1.cs","status":"renamed","previous_filename":"old.cs"},{"filename":"new2.cs","status":"renamed","previous_filename":"old.cs"}]'
        Assert-Rejected $fixture 'duplicate.*alias'
    }
    Test-Case 'RenameCaseOnlyPreservesExactAliases' {
        $fixture = New-Fixture @('src/a.cs')
        $fixture.responses['repos/owner/repo/pulls/3557/files?per_page=100&page=1'] = '[{"filename":"src/a.cs","status":"renamed","previous_filename":"src/A.cs"}]'
        $map = Invoke-Fixture $fixture
        Assert-That (($map.reservedFiles -join '|') -ceq 'src/a.cs|src/A.cs') 'Case-only rename aliases collapsed.'
    }
    Test-Case 'RenameChainDeduplicatesOwnershipNotRecords' {
        $fixture = New-Fixture @('middle.cs', 'new.cs')
        $fixture.responses['repos/owner/repo/pulls/3557/files?per_page=100&page=1'] = '[{"filename":"middle.cs","status":"renamed","previous_filename":"old.cs"},{"filename":"new.cs","status":"renamed","previous_filename":"middle.cs"}]'
        $map = Invoke-Fixture $fixture
        Assert-That ($map.pullRequests[0].actualCount -eq 2 -and $map.reservedFiles.Count -eq 3) 'Shared aliases must not inflate record counts.'
        Assert-That (($map.reservedFiles -join '|') -ceq 'middle.cs|old.cs|new.cs') 'Rename-chain reservation missing.'
    }
    Test-Case 'RejectDuplicateAliasAcrossPages' {
        $fixture = New-Fixture (New-Files 101)
        $rows = @((New-Files 100) | ForEach-Object { @{ filename = $_; status = 'modified' } })
        $rows[0] = @{ filename = 'src/file-001.cs'; status = 'renamed'; previous_filename = 'old.cs' }
        $fixture.responses['repos/owner/repo/pulls/3557/files?per_page=100&page=1'] = ConvertTo-Json -InputObject $rows -Compress
        $fixture.responses['repos/owner/repo/pulls/3557/files?per_page=100&page=2'] = '[{"filename":"src/file-101.cs","status":"renamed","previous_filename":"old.cs"}]'
        Assert-Rejected $fixture 'duplicate.*alias'
    }
    Test-Case 'PreserveCallerEnvironment' {
        foreach ($name in $environmentBefore.Keys) {
            Assert-That ([Environment]::GetEnvironmentVariable($name) -ceq $environmentBefore[$name]) "Caller environment changed: $name"
        }
    }
}
finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
$failed = @($results | Where-Object { -not $_.passed })
[pscustomobject]@{ total = $results.Count; passed = $results.Count - $failed.Count; failed = $failed.Count; skipped = 0; failures = $failed; scratchRemoved = (-not (Test-Path -LiteralPath $scratch)) } | ConvertTo-Json -Depth 6
if ($failed.Count -gt 0) { exit 1 }
