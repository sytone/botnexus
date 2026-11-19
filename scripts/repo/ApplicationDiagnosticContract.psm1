Set-StrictMode -Version Latest

# Request validation and returned-source checks only. Process containment belongs to the runner.

function Resolve-DiagnosticProject {
    <#
    .SYNOPSIS
    Validates an application diagnostic project before submission.
    .DESCRIPTION
    Accepts only literal regular tests projects with a test SDK and no opt-out, traversal or linked path.
    .PARAMETER SourceRoot
    Candidate application root.
    .PARAMETER Project
    Literal tests-relative csproj path.
    .EXAMPLE
    Resolve-DiagnosticProject -SourceRoot $root -Project 'tests/unit/Example.Tests/Example.Tests.csproj'
    .OUTPUTS
    System.String. Validated project path.
    #>

    param([Parameter(Mandatory)][string]$SourceRoot, [Parameter(Mandatory)][string]$Project)
    if ($Project -cnotmatch '^tests/(?:[A-Za-z0-9_.-]+/)+[A-Za-z0-9_.-]+\.csproj$' -or $Project.Split('/') -contains '..' -or $Project.Split('/') -contains '.') {
        throw 'DiagnosticProject must be one repository-relative tests/.../*.csproj path.'
    }
    $root = [IO.Path]::GetFullPath($SourceRoot)
    $path = Join-Path $root $Project
    $cursor = $root
    foreach ($segment in $Project.Split('/')) {
        $cursor = Join-Path $cursor $segment
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Diagnostic project path cannot contain links.'
        }
    }
    [xml]$xml = Get-Content -LiteralPath $path -Raw
    $sdk = $xml.SelectNodes('//PackageReference[@Include="Microsoft.NET.Test.Sdk"]')
    $disabled = $xml.SelectNodes('//IsTestProject') | Where-Object { $_.InnerText.Trim() -ine 'true' }
    if ($sdk.Count -eq 0 -or @($disabled).Count -gt 0 -or $Project -match '/(bin|obj)/') {
        throw 'Diagnostic project must explicitly reference the test SDK and cannot opt out of tests.'
    }
    return $path
}

function Assert-DiagnosticSourceProof {
    <#
    .SYNOPSIS
    Checks returned diagnostic evidence against the submitted application snapshot.
    .DESCRIPTION
    Requires the result and diagnostic evidence to agree on schema, mode, verified source digest and run identity.
    .PARAMETER Result
    Returned result document.
    .PARAMETER Evidence
    Returned diagnostic evidence document.
    .PARAMETER Digest
    Expected exact-source digest.
    .PARAMETER RunId
    Expected submitted run identity.
    .EXAMPLE
    Assert-DiagnosticSourceProof -Result $result -Evidence $evidence -Digest $digest -RunId $runId
    .OUTPUTS
    None. Throws for missing or mismatched evidence.
    #>

    param($Result, $Evidence, [string]$Digest, [string]$RunId)
    foreach ($contract in @($Result, $Evidence)) {
        if ($null -eq $contract -or $null -eq $contract.PSObject.Properties['sourceSnapshot']) {
            throw 'Missing diagnostic exact-source proof.'
        }
        $proof = $contract.sourceSnapshot
        if ($null -eq $proof -or $proof.version -isnot [long] -and $proof.version -isnot [int] -or $proof.version -ne 1 -or $proof.verified -isnot [bool] -or $proof.verified -ne $true -or $proof.digest -isnot [string] -or $proof.digest -cne $Digest -or $proof.runId -isnot [string] -or $proof.runId -cne $RunId -or $contract.mode -cne 'diagnostic') {
            throw 'Diagnostic source proof mismatch.'
        }
    }
    if ($Result.runId -cne $RunId) {
        throw 'Diagnostic run identity mismatch.'
    }
}
