# Product release gate: validate declarations before publishing a contract-aware updater.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseVersion,
    [string]$ContractPath = (Join-Path $PSScriptRoot '../../updater-compatibility.json'),
    [string]$SupportPath = (Join-Path $PSScriptRoot '../../src/gateway/BotNexus.Cli/updater-support.json')
)
$ErrorActionPreference = 'Stop'
function Read-Declaration([string]$Path, [string[]]$Properties, [string[]]$CapabilityProperties, [switch]$Support) {
    $json = Get-Content -LiteralPath $Path -Raw
    $document = [System.Text.Json.JsonDocument]::Parse($json)
    try {
        $root = $document.RootElement
        if ($root.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw "Expected declaration object: $Path." }
        $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $root.EnumerateObject()) {
            if (-not $names.Add($property.Name)) { throw "Duplicate property: $($property.Name)." }
            if ($property.Name -cnotin $Properties) { throw "Unknown property: $($property.Name)." }
        }
        foreach ($property in $Properties) {
            if (-not $names.Contains($property)) { throw "Missing property: $property." }
        }
        foreach ($property in $CapabilityProperties) {
            $array = $root.GetProperty($property)
            if ($array.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or $array.GetArrayLength() -eq 0) { throw "Expected nonempty capability array: $property." }
            $values = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($value in $array.EnumerateArray()) {
                if ($value.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or [string]::IsNullOrWhiteSpace($value.GetString()) -or -not $values.Add($value.GetString())) { throw "Invalid or duplicate capability: $property." }
            }
        }
        if ($Support) {
            $schemas = $root.GetProperty('supportedContractSchemas')
            if ($schemas.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or $schemas.GetArrayLength() -eq 0) { throw 'Expected nonempty schema array.' }
            $seen = [System.Collections.Generic.HashSet[int]]::new()
            foreach ($schema in $schemas.EnumerateArray()) {
                $number = 0
                if ($schema.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or -not $schema.TryGetInt32([ref]$number) -or $number -le 0 -or -not $seen.Add($number)) { throw 'Invalid or duplicate supported schema.' }
            }
        } else {
            $schema = $root.GetProperty('schemaVersion')
            $number = 0
            if ($schema.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or -not $schema.TryGetInt32([ref]$number) -or $number -ne 1) { throw 'Source schema must be integer 1.' }
            $minimum = $root.GetProperty('minimumUpdaterVersion')
            $parsedVersion = $null
            if ($minimum.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or -not [version]::TryParse(($minimum.GetString() -split '\+', 2)[0], [ref]$parsedVersion)) { throw 'Invalid minimum updater version.' }
        }
        return $json | ConvertFrom-Json
    } finally { $document.Dispose() }
}
$contract = Read-Declaration $ContractPath @('schemaVersion', 'minimumUpdaterVersion', 'requiredUpdaterCapabilities', 'providedTargetCapabilities') @('requiredUpdaterCapabilities', 'providedTargetCapabilities')
$support = Read-Declaration $SupportPath @('supportedContractSchemas', 'supportedUpdaterCapabilities', 'requiredTargetCapabilities') @('supportedUpdaterCapabilities', 'requiredTargetCapabilities') -Support
$first = [version]'0.50.2'
$release = [version]$ReleaseVersion
$minimum = [version]($contract.minimumUpdaterVersion -split '\+', 2)[0]
if ($release -lt $first -or $minimum -lt $first -or $release -lt $minimum) {
    throw "Contract-aware releases must be >= $first and >= declared minimum $minimum; got $release."
}
if ($contract.schemaVersion -ne 1 -or $contract.schemaVersion -notin $support.supportedContractSchemas) {
    throw 'Source schema is unsupported by the packaged CLI.'
}
foreach ($capability in $contract.requiredUpdaterCapabilities) {
    if ($capability -cnotin $support.supportedUpdaterCapabilities) { throw "Packaged CLI lacks required source behavior: $capability." }
}
foreach ($capability in $support.requiredTargetCapabilities) {
    if ($capability -cnotin $contract.providedTargetCapabilities) { throw "Source lacks behavior required by packaged CLI: $capability." }
}
Write-Output "Updater release policy passed: package $release; schema $($contract.schemaVersion); minimum $minimum."
