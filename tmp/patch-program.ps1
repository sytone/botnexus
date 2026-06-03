$path = 'Q:\repos\botnexus-wt\feat\webhooks-api\src\gateway\BotNexus.Gateway.Api\Program.cs'
$lines = [System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)
$idx = ($lines | Select-String -SimpleMatch 'AddBotNexusCron').LineNumber - 1
Write-Host "AddBotNexusCron at line $($idx+1): $($lines[$idx])"
# Insert after AddBotNexusCron and AddPlatformConfiguration lines
$insertAfter = $idx + 1  # line after AddBotNexusCron
# Find AddPlatformConfiguration line (should be right after)
$platIdx = ($lines[$insertAfter..($insertAfter+3)] | Select-String 'AddPlatformConfiguration').LineNumber
if ($platIdx) { $insertAfter = $insertAfter + $platIdx }
Write-Host "Inserting after line $($insertAfter+1)"
$insert = @(
    '',
    '// Webhook stores - SQLite co-located with the config directory.',
    'var webhookDbPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(resolvedConfigPath)!, "webhooks.sqlite");',
    'builder.Services.AddBotNexusWebhooks(webhookDbPath);'
)
$newLines = $lines[0..$insertAfter] + $insert + $lines[($insertAfter+1)..($lines.Length-1)]
[System.IO.File]::WriteAllLines($path, $newLines, [System.Text.Encoding]::UTF8)
Write-Host "Done - total lines: $($newLines.Length)"
