<#
.SYNOPSIS
    Runs the full BotNexus test suite with temporary testhost firewall rules.

.PARAMETER Configuration
    Build configuration. Defaults to Debug.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot | Split-Path -Parent | Split-Path -Parent
$testRunner = Join-Path $PSScriptRoot 'Invoke-TestWithFirewall.ps1'

# #2842: discover test projects from disk, matching how tests/dirs.proj defines the graph.
# Parsing BotNexus.slnx made this a second, hand-maintained spelling of the same set, so a
# project added to the traversal but absent from the solution was silently never run here.
$testProjects = @(Get-ChildItem -Path (Join-Path $repoRoot 'tests') -Filter '*.Tests.csproj' -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
    Select-Object -ExpandProperty FullName |
    Sort-Object)

$runnerParameters = @{
    ProjectPath = $testProjects
    Configuration = $Configuration
    DotnetTestArguments = @($solutionPath, '--nologo', '--tl:off', '-c', $Configuration)
}
& $testRunner @runnerParameters
exit $LASTEXITCODE
