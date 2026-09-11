[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InputDirectory,
    [Parameter(Mandatory)][string]$OutputPath,
    [int]$MinimumProductionCycles = 3
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$records = @(
    foreach ($file in Get-ChildItem $InputDirectory -Filter '*.json' -File -ErrorAction SilentlyContinue) {
        try {
            $artifact = Get-Content $file.FullName -Raw | ConvertFrom-Json
        }
        catch {
            continue
        }

        $properties = $artifact.PSObject.Properties
        if (-not $properties['cycleId']) {
            continue
        }

        $isOrchestrationArtifact = $properties['trigger'] -and
            $properties['dispatch'] -and
            $properties['blockers'] -and
            $properties['events']
        if ($isOrchestrationArtifact) {
            continue
        }

        $missingProperties = @(
            @('environment', 'criterionMet') |
                Where-Object { -not $properties[$_] }
        )
        if ($missingProperties.Count -gt 0) {
            throw "Cycle record '$($file.Name)' is missing required properties: $($missingProperties -join ', ')."
        }

        $artifact
    }
)

$productionRecords = @($records | Where-Object { $_.environment -eq 'production' })
$qualifyingProductionRecords = @($productionRecords | Where-Object { $_.criterionMet -eq $true })
$result = [pscustomobject]@{
    schemaVersion = '1.0'
    productionCycles = $productionRecords.Count
    qualifyingProductionCycles = $qualifyingProductionRecords.Count
    minimumProductionCycles = $MinimumProductionCycles
    productionCriterionMet = $qualifyingProductionRecords.Count -ge $MinimumProductionCycles
    records = @($records)
}

$outputDirectory = Split-Path $OutputPath -Parent
if ($outputDirectory) {
    New-Item $outputDirectory -ItemType Directory -Force | Out-Null
}
$result | ConvertTo-Json -Depth 30 | Set-Content $OutputPath -Encoding utf8NoBOM
$result
