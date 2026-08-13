# AC4 mutation check for #2758.
# Removes the near-match suggestion block from the rejection message and asserts the mutation
# actually applied on disk (a mutation that does not apply is INVALID, never a pass).
param([ValidateSet('apply','restore')][string]$Action = 'apply')

$ErrorActionPreference = 'Stop'
$file = 'Q:\repos\botnexus-wt\fix-2758-skill-wrapper-hint\src\agent\BotNexus.Agent.Core\Tools\SkillScriptPreflight.cs'
$backup = 'Q:\repos\botnexus-wt\fix-2758-skill-wrapper-hint\tmp\SkillScriptPreflight.orig.cs'

if ($Action -eq 'restore') {
    if (-not (Test-Path $backup)) { throw 'no backup to restore' }
    Copy-Item $backup $file -Force
    (Get-Item $file).LastWriteTime = Get-Date
    $now = Get-Content $file -Raw
    $orig = Get-Content $backup -Raw
    Write-Host "restored: $($now -eq $orig)"
    return
}

New-Item -ItemType Directory -Force -Path (Split-Path $backup) | Out-Null
Copy-Item $file $backup -Force
$text = Get-Content $file -Raw

$target = '        if (suggestions.Count > 0)'
if (-not $text.Contains($target)) { throw 'MUTATION DID NOT APPLY: anchor not found' }
$mutated = $text.Replace($target, '        if (false && suggestions.Count > 0)')
if ($mutated -eq $text) { throw 'MUTATION DID NOT APPLY: text unchanged' }

Set-Content -Path $file -Value $mutated -NoNewline
(Get-Item $file).LastWriteTime = Get-Date
$check = Get-Content $file -Raw
Write-Host "mutation applied on disk: $($check.Contains('if (false && suggestions.Count > 0)'))"
