[CmdletBinding()]param([Parameter(Mandatory)][string]$InputDirectory,[Parameter(Mandatory)][string]$OutputPath,[int]$MinimumProductionCycles=3)
Set-StrictMode -Version Latest;$ErrorActionPreference='Stop'
$records=@(Get-ChildItem $InputDirectory -Filter '*.json' -File -ErrorAction SilentlyContinue|%{try{Get-Content $_.FullName -Raw|ConvertFrom-Json}catch{}}|?{$_.PSObject.Properties['cycleId']})
$prod=@($records|? environment -eq 'production');$valid=@($prod|?{$_.criterionMet-eq$true})
$result=[pscustomobject]@{schemaVersion='1.0';productionCycles=$prod.Count;qualifyingProductionCycles=$valid.Count;minimumProductionCycles=$MinimumProductionCycles;productionCriterionMet=($valid.Count-ge$MinimumProductionCycles);records=@($records)}
$d=Split-Path $OutputPath -Parent;if($d){New-Item $d -ItemType Directory -Force|Out-Null};$result|ConvertTo-Json -Depth 30|Set-Content $OutputPath -Encoding utf8NoBOM;$result

