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
$solutionPath = Join-Path $repoRoot 'BotNexus.slnx'
$testRunner = Join-Path $PSScriptRoot 'Invoke-TestWithFirewall.ps1'

[xml]$solution = Get-Content $solutionPath -Raw
$testProjects = @($solution.SelectNodes('//Project[@Path]') |
    ForEach-Object { $_.Path -replace '\\', '/' } |
    Where-Object { $_ -match '\.Tests\.csproj$' } |
    ForEach-Object { Join-Path $repoRoot ($_ -replace '/', [IO.Path]::DirectorySeparatorChar) })

$runnerParameters = @{
    ProjectPath = $testProjects
    Configuration = $Configuration
    DotnetTestArguments = @($solutionPath, '--nologo', '--tl:off', '-c', $Configuration)
}
& $testRunner @runnerParameters
exit $LASTEXITCODE
