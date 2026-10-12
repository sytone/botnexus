[CmdletBinding(SupportsShouldProcess)]
param(
  [Parameter(Mandatory)][int]$Issue,
  [ValidateSet('Admit','Unadmit','Claim','Release','Block')][string]$Action = 'Claim',
  [string]$Repository = 'Sytone/botnexus'
)
$ErrorActionPreference = 'Stop'
$issueJson = gh issue view $Issue --repo $Repository --json number,state,title,labels,url
if ($LASTEXITCODE -ne 0) { throw "Issue #$Issue was not found." }
$item = $issueJson | ConvertFrom-Json
$labels = @($item.labels.name)
if ($item.state -ne 'OPEN') { throw "Issue #$Issue is not open." }
$blocks = @('status:blocked','status:needs-jon-decision')
$blockedBy = @($labels | Where-Object { $_ -in $blocks })
if ($Action -in @('Admit','Claim') -and $blockedBy.Count -gt 0) { throw "Issue #$Issue is not ready: $($blockedBy -join ', ')." }
if ($Action -in @('Admit','Claim') -and 'status:in-progress' -in $labels) { throw "Issue #$Issue is not ready: status:in-progress." }
if ($Action -eq 'Admit' -and 'status:ready-for-agent' -in $labels) { throw "Issue #$Issue is already admitted." }
if ($Action -eq 'Claim' -and 'status:ready-for-agent' -notin $labels) { throw "Issue #$Issue is not admitted: status:ready-for-agent is missing." }
# Collect the switch output at the assignment boundary: branch arrays are enumerated,
# so zero/one labels would otherwise become null/a scalar in the JSON payload.
$targetLabels = @(switch ($Action) {
  'Admit' { @($labels + 'status:ready-for-agent' | Select-Object -Unique) }
  'Unadmit' { @($labels | Where-Object { $_ -ne 'status:ready-for-agent' }) }
  'Claim' { $withoutAdmission=@($labels | Where-Object { $_ -ne 'status:ready-for-agent' }); @($withoutAdmission + 'status:in-progress' | Select-Object -Unique) }
  'Release' { @($labels | Where-Object { $_ -ne 'status:in-progress' }) }
  'Block' { @($labels | Where-Object { $_ -notin @('status:ready-for-agent','status:in-progress','status:needs-jon-decision') }) + 'status:blocked' | Select-Object -Unique }
})
$updated = $false
$operation = switch ($Action) {'Admit'{'add status:ready-for-agent'};'Unadmit'{'remove status:ready-for-agent'};'Claim'{'consume status:ready-for-agent and add status:in-progress'};'Release'{'remove status:in-progress'};'Block'{'replace executable workflow state with status:blocked'}}
if ($PSCmdlet.ShouldProcess("issue #$Issue", $operation)) {
  $payloadPath = Join-Path $env:TEMP ("botnexus-claim-{0}.json" -f [guid]::NewGuid().ToString('N'))
  try {
    @{ labels = $targetLabels } |
      ConvertTo-Json -Depth 3 |
      Set-Content -LiteralPath $payloadPath -Encoding utf8
    $tokenScript = Join-Path $HOME '.botnexus/scripts/get-farnsworth-token.ps1'
    if (Test-Path -LiteralPath $tokenScript) {
      $token = @(& $tokenScript | Where-Object { $_ -match '^ghs_' })
      if ($token.Count -ne 1) { throw 'Could not resolve one Farnsworth GitHub App token.' }
      $env:GH_TOKEN = $token[0]
    }
    gh api --method PATCH "repos/$Repository/issues/$Issue" --input $payloadPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to $($Action.ToLowerInvariant()) issue #$Issue." }
    $observedJson = gh issue view $Issue --repo $Repository --json state,labels
    if ($LASTEXITCODE -ne 0) { throw "Issue #$Issue readback failed after $($Action.ToLowerInvariant())." }
    $observed = $observedJson | ConvertFrom-Json
    $observedLabels = @($observed.labels.name | Sort-Object)
    $expectedLabels = @($targetLabels | Sort-Object)
    if ($observed.state -ne 'OPEN' -or ($observedLabels -join "`n") -ne ($expectedLabels -join "`n")) {
      throw "Issue #$Issue claim readback drift: expected [$($expectedLabels -join ', ')], observed [$($observedLabels -join ', ')], state=$($observed.state)."
    }
    $updated = $true
  }
  finally { Remove-Item -LiteralPath $payloadPath -Force -ErrorAction SilentlyContinue }
}
[pscustomobject]@{ issue=$Issue; title=$item.title; url=$item.url; action=$Action; updated=$updated; labels=$targetLabels } | ConvertTo-Json -Depth 4
