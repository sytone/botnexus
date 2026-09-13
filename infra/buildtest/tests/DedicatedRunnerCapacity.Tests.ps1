# Contract tests for the dedicated remote-validation runner capacity (#4164).
#
# The Consumption profile tops out at 4 vCPU / 8 GiB. The validation runner repeatedly reaches
# that ceiling, so the infrastructure contract deliberately places only this job on a D8 profile
# and allocates 8 vCPU / 24 GiB. The 20-minute timeout remains unchanged: capacity is intended to
# create headroom, not to conceal hangs.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:failures = @()
function Assert-True {
    param([bool]$Condition, [string]$Because)
    if (-not $Condition) { $script:failures += $Because }
}

$templatePath = (Resolve-Path (Join-Path $PSScriptRoot '../main.bicep')).Path
$outputPath = Join-Path ([System.IO.Path]::GetTempPath()) "buildtest-4164-$([Guid]::NewGuid().ToString('N')).json"

try {
    az bicep build --file $templatePath --outfile $outputPath --only-show-errors
    Assert-True ($LASTEXITCODE -eq 0) "0: main.bicep did not compile (exit $LASTEXITCODE)"
    Assert-True (Test-Path -LiteralPath $outputPath) '0: Bicep compilation produced no ARM template'

    if (Test-Path -LiteralPath $outputPath) {
        $template = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json -Depth 100
        $environment = @($template.resources | Where-Object { $_.type -eq 'Microsoft.App/managedEnvironments' })
        $job = @($template.resources | Where-Object { $_.type -eq 'Microsoft.App/jobs' })

        Assert-True ($environment.Count -eq 1) "1: expected one managed environment, found $($environment.Count)"
        Assert-True ($job.Count -eq 1) "2: expected one Container Apps job, found $($job.Count)"

        if ($environment.Count -eq 1) {
            $profiles = @($environment[0].properties.workloadProfiles)
            $dedicated = @($profiles | Where-Object { $_.name -eq 'BuildTestD8' })
            Assert-True ($dedicated.Count -eq 1) "1: expected one BuildTestD8 profile, found $($dedicated.Count)"
            if ($dedicated.Count -eq 1) {
                Assert-True ($dedicated[0].workloadProfileType -eq 'D8') '1: BuildTestD8 is not backed by the D8 profile type'
                Assert-True ($dedicated[0].minimumCount -eq 0) '1: BuildTestD8 minimumCount is not zero (idle nodes would be billed continuously)'
                Assert-True ($dedicated[0].maximumCount -eq 1) '1: BuildTestD8 maximumCount is not one (the manually triggered runner needs one bounded node)'
            }
        }

        if ($job.Count -eq 1) {
            $runner = @($job[0].properties.template.containers | Where-Object { $_.name -eq 'runner' })
            Assert-True ($job[0].properties.workloadProfileName -eq 'BuildTestD8') '2: runner job does not select BuildTestD8'
            Assert-True ($job[0].properties.configuration.replicaTimeout -eq 1200) '3: runner replicaTimeout changed from the deliberate 20-minute hang ceiling'
            Assert-True ($runner.Count -eq 1) "2: expected one runner container, found $($runner.Count)"
            if ($runner.Count -eq 1) {
                # Bicep preserves json('8.0') as an ARM expression so ACA receives a JSON number,
                # not the string "8.0". Pin that compiled shape as well as the intended value.
                Assert-True ($runner[0].resources.cpu -eq "[json('8.0')]") "2: runner CPU is '$($runner[0].resources.cpu)', expected the numeric 8.0 ARM expression"
                Assert-True ($runner[0].resources.memory -eq '24Gi') "2: runner memory is '$($runner[0].resources.memory)', expected 24Gi"
            }
        }
    }
}
finally {
    Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
}

if ($script:failures.Count) {
    Write-Host "FAILED ($($script:failures.Count)):" -ForegroundColor Red
    $script:failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host 'Dedicated runner capacity: all checks passed.' -ForegroundColor Green
exit 0
