<#
.SYNOPSIS
    Runs the pre-commit Gateway test subset with temporary firewall rules.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot | Split-Path -Parent | Split-Path -Parent
$projectPath = Join-Path $repoRoot 'tests/gateway/BotNexus.Gateway.Tests/BotNexus.Gateway.Tests.csproj'
$testRunner = Join-Path $PSScriptRoot 'Invoke-TestWithFirewall.ps1'
$arguments = @(
    $projectPath,
    '--nologo',
    '--verbosity', 'minimal',
    '--tl:off',
    '--no-build'
)

& $testRunner -ProjectPath $projectPath -DotnetTestArguments $arguments
exit $LASTEXITCODE
