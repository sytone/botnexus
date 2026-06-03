$path = 'Q:\repos\botnexus-wt\feat\webhooks-api\src\gateway\BotNexus.Gateway.Api\Program.cs'
$lines = [System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)
# Find last 'using' line at the top
$lastUsing = -1
for ($i = 0; $i -lt $lines.Length; $i++) {
    if ($lines[$i] -match '^using ') { $lastUsing = $i }
    elseif ($lastUsing -ge 0 -and $lines[$i] -notmatch '^using ') { break }
}
Write-Host "Last using at line $($lastUsing+1): $($lines[$lastUsing])"
$insert = @('using BotNexus.Gateway.Webhooks;')
$newLines = $lines[0..$lastUsing] + $insert + $lines[($lastUsing+1)..($lines.Length-1)]
[System.IO.File]::WriteAllLines($path, $newLines, [System.Text.Encoding]::UTF8)
Write-Host "Done"
