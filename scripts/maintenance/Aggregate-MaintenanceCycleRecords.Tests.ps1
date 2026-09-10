[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

BeforeAll {
    $scriptPath = Join-Path $PSScriptRoot 'Aggregate-MaintenanceCycleRecords.ps1'

    function New-TestDirectory {
        $path = Join-Path ([IO.Path]::GetTempPath()) "maintenance-cycle-aggregate-$([Guid]::NewGuid().ToString('N'))"
        New-Item -Path $path -ItemType Directory | Out-Null
        return $path
    }

    function Write-JsonArtifact([string]$Directory, [string]$Name, [hashtable]$Value) {
        $Value | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $Directory $Name) -Encoding utf8NoBOM
    }
}

Describe 'Aggregate-MaintenanceCycleRecords' {
    It 'aggregates a valid production record beside an orchestration trace' {
        $directory = New-TestDirectory
        try {
            Write-JsonArtifact $directory 'cycle.json' @{
                schemaVersion = '1.0'
                cycleId = 'maintenance-1'
                environment = 'production'
                criterionMet = $true
            }
            Write-JsonArtifact $directory 'orchestration.json' @{
                cycleId = 'maintenance-1'
                trigger = 'cycle-started'
                correlationId = 'correlation-1'
                dispatch = @()
                blockers = @()
                events = @()
            }

            $result = & $scriptPath -InputDirectory $directory -OutputPath (Join-Path $directory 'aggregate.json') -MinimumProductionCycles 1

            $result.productionCycles | Should -Be 1
            $result.qualifyingProductionCycles | Should -Be 1
            $result.productionCriterionMet | Should -BeTrue
            @($result.records).Count | Should -Be 1
            $result.records[0].cycleId | Should -Be 'maintenance-1'
            @($result.records | Where-Object { $_.PSObject.Properties['trigger'] }).Count | Should -Be 0
        }
        finally {
            Remove-Item $directory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'reports the artifact name and missing fields for a malformed cycle record' {
        $directory = New-TestDirectory
        try {
            Write-JsonArtifact $directory 'malformed-cycle.json' @{
                cycleId = 'maintenance-bad'
                environment = 'production'
            }

            { & $scriptPath -InputDirectory $directory -OutputPath (Join-Path $directory 'aggregate.json') } |
                Should -Throw "Cycle record 'malformed-cycle.json' is missing required propert*criterionMet*"
        }
        finally {
            Remove-Item $directory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'preserves production-only aggregation results' {
        $directory = New-TestDirectory
        try {
            Write-JsonArtifact $directory 'qualifying.json' @{
                schemaVersion = '1.0'
                cycleId = 'maintenance-qualifying'
                environment = 'production'
                criterionMet = $true
            }
            Write-JsonArtifact $directory 'not-qualifying.json' @{
                schemaVersion = '1.0'
                cycleId = 'maintenance-not-qualifying'
                environment = 'production'
                criterionMet = $false
            }
            Write-JsonArtifact $directory 'preproduction.json' @{
                schemaVersion = '1.0'
                cycleId = 'maintenance-preproduction'
                environment = 'preproduction'
                criterionMet = $true
            }

            $result = & $scriptPath -InputDirectory $directory -OutputPath (Join-Path $directory 'aggregate.json') -MinimumProductionCycles 2

            $result.schemaVersion | Should -Be '1.0'
            $result.productionCycles | Should -Be 2
            $result.qualifyingProductionCycles | Should -Be 1
            $result.minimumProductionCycles | Should -Be 2
            $result.productionCriterionMet | Should -BeFalse
            @($result.records).Count | Should -Be 3
            @($result.records | ForEach-Object cycleId | Sort-Object) | Should -Be @(
                'maintenance-not-qualifying'
                'maintenance-preproduction'
                'maintenance-qualifying'
            )
        }
        finally {
            Remove-Item $directory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
