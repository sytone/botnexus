<#
.SYNOPSIS
Tests application diagnostic request and source-proof checks without a runner.
.DESCRIPTION
Creates synthetic projects and result objects. Rejects invalid paths, opted-out
projects and mismatched returned source evidence. Starts no product host or cloud job.
.EXAMPLE
./Test-ApplicationDiagnosticContract.ps1
.OUTPUTS
System.String. Assertion count and failure status.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ApplicationDiagnosticContract.psm1') -Force
$root = Join-Path ([IO.Path]::GetTempPath()) ('application-diagnostic-' + [guid]::NewGuid().ToString('N'))
$script:passed = 0
function Assert-ContractRejection {
    <#
    .SYNOPSIS
    Requires an invalid synthetic input to fail closed.
    .DESCRIPTION
    Fails the fixture if the supplied contract operation unexpectedly succeeds.
    .PARAMETER Operation
    Isolated contract call expected to throw.
    .EXAMPLE
    Assert-ContractRejection -Operation { Resolve-DiagnosticProject -SourceRoot $root -Project '../outside.csproj' }
    .OUTPUTS
    None. Increments the assertion count or throws.
    #>
    param([scriptblock]$Operation)
    $rejected = $false
    try {
        & $Operation | Out-Null
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw 'Invalid diagnostic contract was accepted.'
    }
    $script:passed++
}
try {
    $directory = Join-Path $root 'tests/unit/Example.Tests'
    [void][IO.Directory]::CreateDirectory($directory)
    $project = 'tests/unit/Example.Tests/Example.Tests.csproj'
    $path = Join-Path $root $project
    [IO.File]::WriteAllText($path, '<Project><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>')
    if ((Resolve-DiagnosticProject -SourceRoot $root -Project $project) -ne $path) {
        throw 'Valid diagnostic project rejected.'
    }
    $script:passed++
    foreach ($invalid in @('../outside.csproj', '/absolute.csproj', 'tests/*/Example.csproj', 'tests/../Example.csproj', 'tests/unit/Missing.csproj')) {
        Assert-ContractRejection -Operation { Resolve-DiagnosticProject -SourceRoot $root -Project $invalid }
    }
    [IO.File]::WriteAllText($path, '<Project><PropertyGroup><IsTestProject>false</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>')
    Assert-ContractRejection -Operation { Resolve-DiagnosticProject -SourceRoot $root -Project $project }
    [IO.File]::WriteAllText($path, '<Project />')
    Assert-ContractRejection -Operation { Resolve-DiagnosticProject -SourceRoot $root -Project $project }
    $proof = [pscustomobject]@{ version = 1; verified = $true; digest = 'fixture-digest'; runId = 'fixture-run' }
    $result = [pscustomobject]@{ mode = 'diagnostic'; runId = 'fixture-run'; sourceSnapshot = $proof }
    $evidence = [pscustomobject]@{ mode = 'diagnostic'; sourceSnapshot = $proof }
    Assert-DiagnosticSourceProof -Result $result -Evidence $evidence -Digest 'fixture-digest' -RunId 'fixture-run'
    $script:passed++
    foreach ($field in @('version', 'verified', 'digest', 'runId')) {
        $original = $proof.$field
        $proof.$field = 'invalid'
        Assert-ContractRejection -Operation { Assert-DiagnosticSourceProof -Result $result -Evidence $evidence -Digest 'fixture-digest' -RunId 'fixture-run' }
        $proof.$field = $original
    }
    $result.runId = 'wrong-run'
    Assert-ContractRejection -Operation { Assert-DiagnosticSourceProof -Result $result -Evidence $evidence -Digest 'fixture-digest' -RunId 'fixture-run' }
    $result.runId = 'fixture-run'
    $evidence.mode = 'core'
    Assert-ContractRejection -Operation { Assert-DiagnosticSourceProof -Result $result -Evidence $evidence -Digest 'fixture-digest' -RunId 'fixture-run' }
    Assert-ContractRejection -Operation { Assert-DiagnosticSourceProof -Result $result -Evidence $null -Digest 'fixture-digest' -RunId 'fixture-run' }
    Write-Output "Application diagnostic contract: passed=$script:passed failed=0"
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
