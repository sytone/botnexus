<#
.SYNOPSIS
    Runs dotnet test while leasing firewall rules for its testhost binaries.

.DESCRIPTION
    Ensures Windows Firewall rules immediately before launching tests and always
    releases them afterward, including when dotnet test fails or is interrupted.
    On non-Windows platforms the firewall helper is a no-op.

.PARAMETER ProjectPath
    Test projects whose output paths need temporary firewall authorization.

.PARAMETER DotnetTestArguments
    Complete argument list following `dotnet test`.

.PARAMETER Configuration
    Build configuration used to derive testhost output paths.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$ProjectPath,

    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
    [string[]]$DotnetTestArguments,

    [string]$Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$firewallHelper = Join-Path $PSScriptRoot 'Ensure-TesthostFirewallRules.ps1'
$leasePath = Join-Path ([IO.Path]::GetTempPath()) ("botnexus-fw-lease-{0}" -f [guid]::NewGuid().ToString('N'))
$exitCode = 1

try {
    try {
        & $firewallHelper -ProjectPath $ProjectPath -Configuration $Configuration -Action Ensure -LeasePath $leasePath
    }
    catch {
        Write-Warning "Testhost firewall setup skipped: $($_.Exception.Message)"
    }
    & dotnet test @DotnetTestArguments
    $exitCode = $LASTEXITCODE
}
finally {
    try {
        & $firewallHelper -ProjectPath $ProjectPath -Configuration $Configuration -Action Cleanup -LeasePath $leasePath
    }
    catch {
        Write-Warning "Testhost firewall cleanup skipped: $($_.Exception.Message)"
    }
}

exit $exitCode
