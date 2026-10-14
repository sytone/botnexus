# Classify PR changes, not base-branch drift. Run from the candidate merge checkout.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Git([string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $text = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "git $($Arguments[0]) failed: $errorText" }
        return $text
    } finally { $process.Dispose() }
}

try {
    foreach ($name in @('BASE_SHA', 'HEAD_SHA')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value -cnotmatch '^[0-9a-fA-F]{40}$') { throw "$name must be a full commit SHA" }
    }
    if ([string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) { throw 'GITHUB_OUTPUT is required' }
    $base = (Invoke-Git @('rev-parse', '--verify', "$($env:BASE_SHA)^{commit}")).Trim()
    $head = (Invoke-Git @('rev-parse', '--verify', "$($env:HEAD_SHA)^{commit}")).Trim()
    $mergeBase = (Invoke-Git @('merge-base', $base, $head)).Trim()
    if ($mergeBase -cnotmatch '^[0-9a-fA-F]{40}$') { throw 'git merge-base returned no single commit' }
    # Disable rename detection so both the old (deleted) and new paths are classified.
    # NUL delimiters preserve spaces, tabs, newlines and Unicode in filenames.
    $diff = Invoke-Git @('diff', '--no-ext-diff', '--no-renames', '--name-only', '-z', $mergeBase, $head, '--')
    $inputs = @(
        'package.json', 'package-lock.json',
        '.github/workflows/docs-site.yml', '.github/workflows/deploy-docs.yml',
        'scripts/repo/Get-DocsSiteImpact.ps1',
        'tests/architecture/BotNexus.Architecture.Tests/DocsSiteFocusedTests.ps1',
        'tests/architecture/BotNexus.Architecture.Tests/DocsSiteWorkflowArchitectureTests.cs'
    )
    $relevant = $false
    foreach ($path in $diff.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
        if ($path.StartsWith('docs/', [StringComparison]::Ordinal) -or $inputs -ccontains $path) {
            $relevant = $true
            break
        }
    }
    # Publish only after every Git operation succeeded. A failure never becomes a successful noop.
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "relevant=$($relevant.ToString().ToLowerInvariant())" -Encoding utf8
    Write-Output "Documentation-site inputs changed: $relevant"
} catch {
    [Console]::Error.WriteLine("Documentation-site classification failed: $($_.Exception.Message)")
    exit 1
}
