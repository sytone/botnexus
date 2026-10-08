param([string]$ScratchRoot = [System.IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$classifier = Join-Path $root 'scripts/repo/Get-DocsSiteImpact.ps1'
$workflowPath = Join-Path $root '.github/workflows/docs-site.yml'
$scratch = Join-Path ([IO.Path]::GetFullPath($ScratchRoot)) ('docs-site-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$passed = 0
$failed = 0
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Case([string]$Name, [scriptblock]$Body) {
    try { & $Body; $script:passed++; Write-Output "PASS $Name" }
    catch { $script:failed++; Write-Output "FAIL ${Name}: $($_.Exception.Message)" }
}
function Run([string]$Command, [string[]]$Arguments, [string]$Directory, [hashtable]$Environment = @{}) {
    $start = [System.Diagnostics.ProcessStartInfo]::new($Command)
    $start.WorkingDirectory = $Directory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $start.Environment[$key] = $Environment[$key] }
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        Assert ($process.WaitForExit(30000)) "Process timed out: $Command"
        return @{ Code = $process.ExitCode; Text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult() }
    } finally { if (-not $process.HasExited) { $process.Kill($true) }; $process.Dispose() }
}
function Git([string[]]$Arguments) {
    $result = Run 'git' $Arguments $script:repo
    Assert ($result.Code -eq 0) "Fixture git failed: $($result.Text)"
    return $result.Text.Trim()
}
function Put([string]$Path, [string]$Content = 'fixture') {
    $target = Join-Path $script:repo $Path
    New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
    [IO.File]::WriteAllText($target, $Content)
}
function Commit {
    Git @('add', '-A') | Out-Null
    Git @('-c', 'user.name=Docs Site Test', '-c', 'user.email=docs-site@botnexus.invalid', 'commit', '-qm', 'fixture') | Out-Null
    return Git @('rev-parse', 'HEAD')
}
function Fixture {
    $script:repo = Join-Path $scratch ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $script:repo | Out-Null
    Git @('init', '-q') | Out-Null
    Put 'docs/existing.md'
    Put 'src/existing.cs'
    $script:base = Commit
    $script:head = $script:base
}
function Check([string]$Expected, [bool]$Success = $true, [hashtable]$Overrides = @{}) {
    Assert (Test-Path $classifier) 'Classifier implementation is missing'
    $output = Join-Path $scratch ([guid]::NewGuid().ToString('N') + '.output')
    $environment = @{ BASE_SHA = $script:base; HEAD_SHA = $script:head; GITHUB_OUTPUT = $output }
    foreach ($key in $Overrides.Keys) { $environment[$key] = $Overrides[$key] }
    $result = Run 'pwsh' @('-NoProfile', '-File', $classifier) $script:repo $environment
    $text = if (Test-Path $output -PathType Leaf) { [IO.File]::ReadAllText($output).Trim() } else { '' }
    if ($Success) {
        Assert ($result.Code -eq 0) "Classifier failed: $($result.Text)"
        Assert ($text -ceq "relevant=$Expected") "Expected relevant=$Expected, got '$text'"
    } else {
        Assert ($result.Code -ne 0) 'Classifier must fail closed'
        Assert ($text -eq '') "Failure must not publish a successful classification: $text"
    }
}
try {
    # Infrastructure preflight is deliberately outside Case: do not count launch failures as RED.
    $preflight = Run 'pwsh' @('-NoProfile', '-Command', 'exit 0') $scratch
    Assert ($preflight.Code -eq 0) 'PowerShell harness cannot start'
    Fixture
    foreach ($path in @('docs/guide.md', 'docs/.vitepress/config.mts', 'docs/public/image.svg', 'package.json', 'package-lock.json', '.github/workflows/docs-site.yml', '.github/workflows/deploy-docs.yml', 'scripts/repo/Get-DocsSiteImpact.ps1', 'tests/architecture/BotNexus.Architecture.Tests/DocsSiteFocusedTests.ps1')) {
        Case "relevant $path" { Fixture; Put $path; $script:head = Commit; Check 'true' }
    }
    Case 'deletion' { Fixture; Remove-Item (Join-Path $repo 'docs/existing.md'); $script:head = Commit; Check 'true' }
    Case 'rename out of docs' { Fixture; Git @('mv', 'docs/existing.md', 'src/moved.md') | Out-Null; $script:head = Commit; Check 'true' }
    Case 'rename into docs' { Fixture; Git @('mv', 'src/existing.cs', 'docs/moved.md') | Out-Null; $script:head = Commit; Check 'true' }
    Case 'unrelated source and root markdown' { Fixture; Put 'src/new.cs'; Put 'README.md'; $script:head = Commit; Check 'false' }
    Case 'empty diff' { Fixture; Check 'false' }
    Case 'base-only drift is not PR impact' {
        Fixture; Git @('checkout', '-qb', 'candidate') | Out-Null; Put 'src/new.cs'; $script:head = Commit
        Git @('checkout', '--detach', $script:base) | Out-Null; Put 'docs/base-only.md'; $script:base = Commit; Check 'false'
    }
    Case 'PR docs remain relevant after base drift' {
        Fixture; Git @('checkout', '-qb', 'candidate') | Out-Null; Put 'docs/pr.md'; $script:head = Commit
        Git @('checkout', '--detach', $script:base) | Out-Null; Put 'src/base-only.cs'; $script:base = Commit; Check 'true'
    }
    foreach ($key in @('BASE_SHA', 'HEAD_SHA', 'GITHUB_OUTPUT')) {
        Case "missing $key" { Fixture; Check '' $false @{ $key = '' } }
    }
    foreach ($key in @('BASE_SHA', 'HEAD_SHA')) {
        Case "missing object $key" { Fixture; Check '' $false @{ $key = ('f' * 40) } }
        Case "option injection $key" { Fixture; Check '' $false @{ $key = '--help' } }
    }
    Case 'no merge base' {
        Fixture; Git @('checkout', '--orphan', 'disconnected') | Out-Null; Git @('rm', '-rf', '.') | Out-Null
        Put 'other.md'; $script:head = Commit; Check '' $false
    }
    Case 'git diff failure' {
        Fixture; Put 'src/new.cs'; $script:head = Commit
        $tree = Git @('rev-parse', 'HEAD^{tree}')
        Remove-Item -Force (Join-Path $repo ".git/objects/$($tree.Substring(0,2))/$($tree.Substring(2))")
        Check '' $false
    }
    Case 'output write failure' { Fixture; Check '' $false @{ GITHUB_OUTPUT = $scratch } }
    Case 'not a git repo' { Fixture; $script:repo = $scratch; Check '' $false }
    Case 'workflow unfiltered PR and read-only permissions' {
        Assert (Test-Path $workflowPath) 'Workflow implementation is missing'
        $yaml = [IO.File]::ReadAllText($workflowPath)
        Assert ($yaml -match '(?m)^on:\r?\n  pull_request:\s*\r?\n\r?\npermissions:') 'PR event must be unfiltered'
        Assert ($yaml -match '(?m)^permissions:\r?\n  contents: read\r?\n\r?\n') 'Only contents: read allowed'
        Assert ($yaml -notmatch 'pull_request_target|pages:|id-token:|issues:|configure-pages|upload-pages|deploy-pages|continue-on-error') 'No elevated permission, deployment, or ignored failures'
    }
    Case 'workflow stable unconditional terminal job' {
        Assert (Test-Path $workflowPath) 'Workflow implementation is missing'
        $yaml = [IO.File]::ReadAllText($workflowPath)
        Assert ($yaml -match '(?m)^jobs:\r?\n  docs-site:\r?\n    name: docs-site\r?\n') 'Stable docs-site job required'
        Assert ($yaml -notmatch '(?m)^    (if|needs|strategy):') 'Job must not be skipped or multiplied'
        Assert ($yaml -match 'timeout-minutes: 15') 'Bounded job required'
        Assert ($yaml -match "if: steps.impact.outputs.relevant == 'false'") 'Explicit noop required'
        Assert ($yaml -match 'No documentation-site inputs changed') 'Explain noop success'
    }
    Case 'workflow merge checkout and safe env inputs' {
        Assert (Test-Path $workflowPath) 'Workflow implementation is missing'
        $yaml = [IO.File]::ReadAllText($workflowPath)
        Assert ($yaml -match 'uses: actions/checkout@v4') 'Checkout required'
        Assert ($yaml -notmatch '(?m)^          ref:') 'Use default candidate merge checkout, not head checkout'
        Assert ($yaml -match 'fetch-depth: 0' -and $yaml -match 'persist-credentials: false') 'Full history and no persisted token required'
        Assert ($yaml -match 'BASE_SHA: \$\{\{ github.event.pull_request.base.sha \}\}' -and $yaml -match 'HEAD_SHA: \$\{\{ github.event.pull_request.head.sha \}\}') 'SHAs must enter through env'
        Assert ($yaml -match 'shell: pwsh' -and $yaml -match 'run: ./scripts/repo/Get-DocsSiteImpact.ps1') 'Real classifier must run'
        Assert ($yaml -notmatch 'run:.*\$\{\{') 'No expression interpolation in shell'
    }
    Case 'workflow conditional install and site build match deployment' {
        Assert (Test-Path $workflowPath) 'Workflow implementation is missing'
        $yaml = [IO.File]::ReadAllText($workflowPath)
        Assert ($yaml -match "(?s)uses: actions/setup-node@v4\r?\n        if: steps.impact.outputs.relevant == 'true'\r?\n        with:\r?\n          node-version: '20'\r?\n          cache: npm") 'Node20 only for relevant PRs'
        Assert ($yaml -match "(?s)if: steps.impact.outputs.relevant == 'true'\r?\n        run: npm ci.*if: steps.impact.outputs.relevant == 'true'\r?\n        run: npm run docs:build") 'Install before build, both conditional'
        $deploy = [IO.File]::ReadAllText((Join-Path $root '.github/workflows/deploy-docs.yml'))
        Assert ($deploy -match "node-version: '20'" -and $deploy -match 'run: npm ci' -and $deploy -match 'run: npm run docs:build') 'Deployment parity'
    }
} finally { Remove-Item -Recurse -Force $scratch }
Write-Output "RESULT passed=$passed failed=$failed"
if ($failed -gt 0) { exit 1 }
