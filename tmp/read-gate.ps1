$ErrorActionPreference='Stop'
$root='Q:\repos\botnexus-wt\fix-2758-skill-wrapper-hint\artifacts\azure-buildtest\20260813041735-6b7d812d'
$res = Get-ChildItem -Recurse -Filter result.json $root | Select-Object -First 1
if ($res) { Write-Host "== result.json: $($res.FullName)"; (Get-Content $res.FullName -Raw) }
Write-Host "== FAILED TESTS =="
Get-ChildItem -Recurse -Filter *.trx $root | ForEach-Object {
  Select-String -Path $_.FullName -Pattern 'outcome="Failed" testName="([^"]+)"' -AllMatches |
    ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value }
} | Sort-Object -Unique
