using System.CommandLine;
using BotNexus.Cli.Services;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Security;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// Manages the <c>sqlite:</c> secret store - the only backend BotNexus itself owns, and therefore
/// the only one it can populate.
/// </summary>
/// <remarks>
/// <para>
/// <b>A value is never taken as an argument.</b> Anything on a command line lands in shell history,
/// in <c>ps</c> output for the life of the process, and in any CI log that echoes commands. It is
/// read from stdin instead - piped, or prompted for without echo when attached to a terminal.
/// </para>
/// <para>
/// <c>list</c> prints names and never values. There is deliberately no <c>get</c>: BotNexus
/// resolves a secret when it needs one, and a command whose entire purpose is to print a credential
/// to a terminal is a facility for exfiltrating it. Use the platform's own tooling if you genuinely
/// need to read one back.
/// </para>
/// </remarks>
public sealed class SecretCommand
{
    /// <summary>Builds the <c>secret</c> verb group.</summary>
    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var command = new Command("secret", "Manage the sqlite: secret store.");
        command.AddAlias("secrets");

        var nameArgument = new Argument<string>("name", "Name the secret is referenced by, as in sqlite:<name>.");

        var setCommand = new Command("set", "Store a secret, reading the value from stdin.") { nameArgument };
        setCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var name = context.ParseResult.GetValueForArgument(nameArgument);
            context.ExitCode = await SetAsync(StorePath(target), name, CancellationToken.None);
        });

        var listCommand = new Command("list", "List stored secret names. Never prints values.");
        listCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            context.ExitCode = await ListAsync(StorePath(target), CancellationToken.None);
        });

        var removeCommand = new Command("remove", "Remove a stored secret.") { nameArgument };
        removeCommand.AddAlias("rm");
        removeCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var name = context.ParseResult.GetValueForArgument(nameArgument);
            context.ExitCode = await RemoveAsync(StorePath(target), name, CancellationToken.None);
        });

        command.AddCommand(setCommand);
        command.AddCommand(listCommand);
        command.AddCommand(removeCommand);
        return command;
    }

    private static string StorePath(string? target)
        => SqliteStorePathPolicy.ResolveOwnedStorePath(CliPaths.ResolveTarget(target), "secrets");

    internal static async Task<int> SetAsync(string storePath, string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            AnsiConsole.MarkupLine("[red]A secret name is required.[/]");
            return 1;
        }

        var value = ReadValue();
        if (string.IsNullOrEmpty(value))
        {
            AnsiConsole.MarkupLine("[red]No value supplied. Pipe it in, or type it when prompted.[/]");
            return 1;
        }

        try
        {
            await EnsureStoreAsync(storePath, cancellationToken).ConfigureAwait(false);

            await using var connection = SqliteConnectionFactory.Create(
                new SqliteConnectionStringBuilder { DataSource = storePath }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO secrets (name, value, updated_utc) VALUES ($name, $value, $updated)
                ON CONFLICT(name) DO UPDATE SET value = excluded.value, updated_utc = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            AnsiConsole.MarkupLine($"[red]Could not write the secret store:[/] {CliText.SafeDisplay(ex.Message)}");
            return 1;
        }

        // Re-applied after every write: the guard-rail is worthless if it only holds for the file's
        // first version, and SQLite recreates journal siblings as it goes.
        SecureFilePermissions.RestrictToOwner(storePath);

        AnsiConsole.MarkupLine($"[green]Stored[/] {CliText.SafeDisplay(name)} — reference it as [blue]sqlite:{CliText.SafeDisplay(name)}[/]");
        return 0;
    }

    internal static async Task<int> ListAsync(string storePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(storePath))
        {
            AnsiConsole.MarkupLine("[dim]No secret store yet. Create one with: botnexus secret set <name>[/]");
            return 0;
        }

        var names = new List<string>();
        try
        {
            await using var connection = SqliteConnectionFactory.Create(
                new SqliteConnectionStringBuilder { DataSource = storePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name, updated_utc FROM secrets ORDER BY name;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                names.Add($"{reader.GetString(0)}  [dim]{reader.GetString(1)}[/]");
        }
        catch (SqliteException ex)
        {
            AnsiConsole.MarkupLine($"[red]Could not read the secret store:[/] {CliText.SafeDisplay(ex.Message)}");
            return 1;
        }

        if (names.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]The secret store is empty.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold]Secrets[/] [dim]({storePath})[/]");
        foreach (var line in names)
            AnsiConsole.MarkupLine($"  {line}");

        if (SecureFilePermissions.IsReadableByOthers(new System.IO.Abstractions.FileSystem(), storePath))
            AnsiConsole.MarkupLine("[yellow]Warning:[/] the store is readable by other users. Run: chmod 600 " + storePath);

        return 0;
    }

    internal static async Task<int> RemoveAsync(string storePath, string name, CancellationToken cancellationToken)
    {
        if (!File.Exists(storePath))
        {
            AnsiConsole.MarkupLine("[red]No secret store exists.[/]");
            return 1;
        }

        try
        {
            await using var connection = SqliteConnectionFactory.Create(
                new SqliteConnectionStringBuilder { DataSource = storePath }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM secrets WHERE name = $name;";
            command.Parameters.AddWithValue("$name", name);
            var removed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (removed == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]No secret named[/] {CliText.SafeDisplay(name)}");
                return 1;
            }
        }
        catch (SqliteException ex)
        {
            AnsiConsole.MarkupLine($"[red]Could not update the secret store:[/] {CliText.SafeDisplay(ex.Message)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]Removed[/] {CliText.SafeDisplay(name)}");
        return 0;
    }

    /// <summary>
    /// Reads the value from stdin - piped, or prompted without echo on a terminal. Never from an
    /// argument, so it cannot reach shell history, <c>ps</c>, or a CI log.
    /// </summary>
    private static string? ReadValue()
    {
        if (Console.IsInputRedirected)
            return Console.In.ReadToEnd().TrimEnd('\r', '\n');

        return AnsiConsole.Prompt(new TextPrompt<string>("Secret value:").Secret().AllowEmpty());
    }

    internal static async Task EnsureStoreAsync(string storePath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(storePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var isNew = !File.Exists(storePath);

        await using var connection = SqliteConnectionFactory.Create(
            new SqliteConnectionStringBuilder { DataSource = storePath }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS secrets (
                name        TEXT PRIMARY KEY,
                value       TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Narrowed as soon as the file exists, before a value is ever written into it.
        if (isNew)
            SecureFilePermissions.RestrictToOwner(storePath);
    }
}
