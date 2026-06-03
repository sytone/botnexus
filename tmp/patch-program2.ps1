$path = 'Q:\repos\botnexus-wt\feat\webhooks-api\src\gateway\BotNexus.Gateway.Api\Program.cs'
$lines = [System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)

# Remove the bad 4 lines that were inserted (lines 87-90, 0-indexed 86-89)
# "blank, // Webhook, var webhookDbPath, builder.Services.AddBotNexusWebhooks"
$badStart = 86
$badEnd = 89
$cleaned = $lines[0..($badStart-1)] + $lines[($badEnd+1)..($lines.Length-1)]

# Find "});  // end of Configure<CronOptions>" - find last "})" before "static string? ResolveCronModel"
$resolveIdx = ($cleaned | Select-String -SimpleMatch 'static string? ResolveCronModel').LineNumber - 1
# The }); is 2 lines before that (blank line + });)
$insertAfterIdx = $resolveIdx - 2  # the }); line

Write-Host "CronOptions block ends at line $($insertAfterIdx+1): $($cleaned[$insertAfterIdx])"

$insert = @(
    '',
    '// Webhook stores - SQLite co-located with the config directory.',
    'var webhookDbPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(resolvedConfigPath)!, "webhooks.sqlite");',
    'builder.Services.AddBotNexusWebhooks(webhookDbPath);'
)

$newLines = $cleaned[0..$insertAfterIdx] + $insert + $cleaned[($insertAfterIdx+1)..($cleaned.Length-1)]
[System.IO.File]::WriteAllLines($path, $newLines, [System.Text.Encoding]::UTF8)
Write-Host "Done - total lines: $($newLines.Length)"
