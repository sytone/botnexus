$ErrorActionPreference = 'Stop'
$p = 'Q:\repos\botnexus-wt\feat-2812-cli-session-commands\src\gateway\BotNexus.Cli\Commands\SessionCommands.cs'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$t = [System.IO.File]::ReadAllText($p)

$mode = $args[0]

$archiveOrig = @'
        await store.ArchiveAsync(sessionId, ct).ConfigureAwait(false);
'@
$archiveMut = @'
        // MUTATION: store mutation removed.
'@
$deleteOrig = @'
        var refusal = ValidateExplicitId(id);
        if (refusal is not null)
        {
            AnsiConsole.MarkupLine("[red]{0}[/]", Markup.Escape(refusal));
            return 2;
        }

        var sessionId = SessionId.From(id);
        var existing = await store.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (existing is null)
        {
            AnsiConsole.MarkupLine("[yellow]Session '{0}' not found.[/]", Markup.Escape(id));
            return 1;
        }

        await store.DeleteAsync(sessionId, ct).ConfigureAwait(false);
'@
$deleteMut = @'
        // MUTATION: selector guard removed.
        var sessionId = SessionId.From(id);
        var existing = await store.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (existing is null)
        {
            AnsiConsole.MarkupLine("[yellow]Session '{0}' not found.[/]", Markup.Escape(id));
            return 1;
        }

        await store.DeleteAsync(sessionId, ct).ConfigureAwait(false);
'@

# AC2 mutation: also drop the idempotence short-circuit so a second archive re-stamps UpdatedAt.
$idemOrig = @'
        if (existing.Status == SessionStatus.Sealed)
        {
            AnsiConsole.MarkupLine("[green]Session '{0}' is already archived.[/]", Markup.Escape(id));
            return 0;
        }
'@
$idemMut = @'
        // MUTATION: idempotence short-circuit removed.
'@

if ($mode -eq 'apply') {
    foreach ($pair in @(@($archiveOrig, $archiveMut), @($deleteOrig, $deleteMut), @($idemOrig, $idemMut))) {
        if (-not $t.Contains($pair[0])) { throw "MUTATION DID NOT APPLY - target not found:`n$($pair[0])" }
        $t = $t.Replace($pair[0], $pair[1])
    }
} elseif ($mode -eq 'restore') {
    foreach ($pair in @(@($archiveMut, $archiveOrig), @($deleteMut, $deleteOrig), @($idemMut, $idemOrig))) {
        if (-not $t.Contains($pair[0])) { throw "RESTORE DID NOT APPLY - target not found" }
        $t = $t.Replace($pair[0], $pair[1])
    }
} else { throw 'usage: mutate.ps1 apply|restore' }

[System.IO.File]::WriteAllText($p, $t, $utf8)
(Get-Item $p).LastWriteTime = Get-Date
Write-Host "$mode ok"
