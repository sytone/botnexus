Set-StrictMode -Version Latest

# Application-owned CORE execution and TRX attribution; no image/deployment policy.

function Get-CompletedTestAssembly {
    <#
    .SYNOPSIS
    Reads assembly and project identities from complete or truncated TRX text.
    .DESCRIPTION
    Scans result rows, not filenames, preserving attribution after interrupted test execution.
    .PARAMETER TrxPaths
    TRX files from this isolated application test run.
    .EXAMPLE
    Get-CompletedTestAssembly -TrxPaths $paths
    .OUTPUTS
    System.Object[]. Distinct assembly and project names.
    #>

    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $TrxPaths
    )

    $assemblies = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $TrxPaths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }
        $text = Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
        if ([string]::IsNullOrEmpty($text)) {
            continue
        }
        foreach ($match in [regex]::Matches($text, '(?:storage|codeBase)="([^"]+)"')) {
            $value = $match.Groups[1].Value.Replace('\', '/')
            $leaf = [System.IO.Path]::GetFileNameWithoutExtension($value)
            if (-not [string]::IsNullOrWhiteSpace($leaf)) {
                [void]$assemblies.Add($leaf)
            }

            # The project directory: the segment immediately before /bin/. Independent of
            # AssemblyName, so an assembly rename cannot make a project look unfinished.
            $binIndex = $value.LastIndexOf('/bin/', [StringComparison]::OrdinalIgnoreCase)
            if ($binIndex -gt 0) {
                $projectDir = $value.Substring(0, $binIndex).Split('/')[-1]
                if (-not [string]::IsNullOrWhiteSpace($projectDir)) {
                    [void]$assemblies.Add($projectDir)
                }
            }
        }
    }

    return @($assemblies | Sort-Object)
}

function Get-ExpectedTestProject {
    <#
    .SYNOPSIS
    Lists runnable application test projects.
    .DESCRIPTION
    Excludes generated output, harness-only projects and explicit IsTestProject=false support libraries.
    .PARAMETER TestsRoot
    Application tests directory to inspect.
    .EXAMPLE
    Get-ExpectedTestProject -TestsRoot ./tests
    .OUTPUTS
    System.Object[]. Runnable project names.
    #>

    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)][string] $TestsRoot
    )

    if (-not (Test-Path -LiteralPath $TestsRoot -PathType Container)) {
        return @()
    }

    return @(Get-ChildItem -LiteralPath $TestsRoot -Recurse -File -Filter '*.csproj' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName.Replace('\', '/') -notmatch '/(bin|obj)/' } |
            Where-Object {
                $text = Get-Content -LiteralPath $_.FullName -Raw -ErrorAction SilentlyContinue
                $text -and ($text -match 'Microsoft\.NET\.Test\.Sdk') -and ($text -notmatch '<IsTestProject>\s*false')
            } |
            ForEach-Object { $_.BaseName } |
            Sort-Object -Unique)
}

function Get-ExpectedProjectsForMode {
    <#
    .SYNOPSIS
    Selects application test projects for one validation scope.
    .DESCRIPTION
    Preserves CORE browser quarantine and single-project Playwright attribution without discovering runner infrastructure.
    .PARAMETER Mode
    Application validation scope.
    .PARAMETER ExpectedProjects
    Runnable project names from application source.
    .PARAMETER E2EProject
    Explicit application Playwright project name.
    .EXAMPLE
    Get-ExpectedProjectsForMode -Mode core -ExpectedProjects $projects -E2EProject 'Example.E2E.Tests'
    .OUTPUTS
    System.Object[]. Expected project names.
    #>
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)][string] $Mode,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $ExpectedProjects,
        [Parameter(Mandatory)][string] $E2EProject
    )

    switch ($Mode) {
        'playwright' {
            return @($ExpectedProjects | Where-Object { $_ -eq $E2EProject })
        }
        'core' {
            return @($ExpectedProjects | Where-Object {
                    $_ -notlike '*BotNexus.Integration.E2E*' -and $_ -notlike '*BotNexus.E2E*'
                })
        }
        default {
            return @($ExpectedProjects)
        }
    }
}

function Get-UnfinishedTestProject {
    <#
    .SYNOPSIS
    Identifies expected projects without observed result rows.
    .DESCRIPTION
    Compares names case-insensitively and respects deliberate scope exclusions. Empty outstanding coverage stays empty.
    .PARAMETER ExpectedProjects
    Projects requested by this application run.
    .PARAMETER CompletedAssemblies
    Observed assembly and project names.
    .PARAMETER ExcludedProjects
    Explicit project-name patterns outside the requested scope.
    .EXAMPLE
    Get-UnfinishedTestProject -ExpectedProjects $projects -CompletedAssemblies $completed
    .OUTPUTS
    System.Object[]. Outstanding project names.
    #>

    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $ExpectedProjects,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $CompletedAssemblies,
        [AllowEmptyCollection()][string[]] $ExcludedProjects = @()
    )

    $completed = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $CompletedAssemblies) {
        if ($item) {
            [void]$completed.Add($item)
        }
    }

    return @($ExpectedProjects |
            Where-Object { $_ } |
            Where-Object { -not $completed.Contains($_) } |
            Where-Object {
                $candidate = $_
                -not ($ExcludedProjects | Where-Object { $_ -and $candidate -like "*$_*" })
            } |
            Sort-Object -Unique)
}

function Invoke-BoundedProcess {
    <#
    .SYNOPSIS
    Runs an application validation child under a wall-clock deadline.
    .DESCRIPTION
    Captures both output pipes concurrently, terminates the child tree at timeout and retains a merged log. Starts only the explicit caller command.
    .PARAMETER FilePath
    Executable to start.
    .PARAMETER ArgumentList
    Arguments preserved as separate child tokens.
    .PARAMETER LogPath
    Owned output log path.
    .PARAMETER TimeoutSeconds
    Maximum child execution time in seconds.
    .PARAMETER PollMilliseconds
    Interval for checking child completion and deadline.
    .EXAMPLE
    Invoke-BoundedProcess -FilePath 'pwsh' -ArgumentList @('-NoProfile','-Command','exit 0') -LogPath $log -TimeoutSeconds 10
    .OUTPUTS
    System.Management.Automation.PSCustomObject. ExitCode, TimedOut and ElapsedSeconds.
    #>

    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $ArgumentList,
        [Parameter(Mandatory)][string] $LogPath,
        [Parameter(Mandatory)][int] $TimeoutSeconds,
        [int] $PollMilliseconds = 1000
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $stdout = "$LogPath.stdout"
    $stderr = "$LogPath.stderr"
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($FilePath)
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $ArgumentList) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stdoutStream = $null
    $stderrStream = $null
    try {
        if (-not $process.Start()) {
            throw "Could not start $FilePath."
        }
        $stdoutStream = [System.IO.File]::Create($stdout)
        $stderrStream = [System.IO.File]::Create($stderr)
        # Drain both pipes while the child runs: synchronous reads can deadlock a verbose test host.
        $stdoutCopy = $process.StandardOutput.BaseStream.CopyToAsync($stdoutStream)
        $stderrCopy = $process.StandardError.BaseStream.CopyToAsync($stderrStream)

        $timedOut = $false
        while (-not $process.HasExited) {
            if ($stopwatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                $timedOut = $true
                try {
                    $process.Kill($true)
                }
                catch {
                    Write-Verbose 'Bounded process cleanup did not confirm completion.'
                }
                try {
                    $process.WaitForExit(30000) | Out-Null
                }
                catch {
                    Write-Verbose 'Bounded process cleanup did not confirm completion.'
                }
                break
            }
            Start-Sleep -Milliseconds $PollMilliseconds
        }
        $drain = [System.Threading.Tasks.Task]::WhenAll($stdoutCopy, $stderrCopy)
        if ($timedOut) {
            # A surviving descendant may retain an inherited pipe; do not undo the deadline
            # by waiting forever for its EOF after the process tree has been killed.
            try {
                [void]$drain.Wait(30000)
            }
            catch {
                Write-Verbose 'Bounded process cleanup did not confirm completion.'
            }
        }
        else {
            [void]$drain.GetAwaiter().GetResult()
        }
        $exitCode = if ($timedOut) {
            1
        }
        else {
            $process.ExitCode
        }
    }
    finally {
        if ($null -ne $stdoutStream) {
            $stdoutStream.Dispose()
        }
        if ($null -ne $stderrStream) {
            $stderrStream.Dispose()
        }
        $process.Dispose()
        $stopwatch.Stop()
    }

    # Merge the drained streams into the log the rest of the runner expects. Done after the
    # fact so a timeout still leaves two readable sections rather than a half-written interleave.
    $merged = @()
    foreach ($part in @($stdout, $stderr)) {
        if (Test-Path -LiteralPath $part) {
            $merged += (Get-Content -LiteralPath $part -Raw -ErrorAction SilentlyContinue)
            Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue
        }
    }
    Set-Content -Path $LogPath -Value ($merged -join [Environment]::NewLine) -ErrorAction SilentlyContinue

    return [pscustomobject]@{
        ExitCode = $exitCode
        TimedOut = $timedOut
        ElapsedSeconds = $stopwatch.Elapsed.TotalSeconds
    }
}
