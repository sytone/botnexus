using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// The single entry point every targeted CLI config mutation goes through.
/// </summary>
/// <remarks>
/// <para>
/// Before #2057 each command loaded a typed <see cref="PlatformConfig"/> outside the writer lock,
/// edited it, and asked the writer to replace the whole document. That silently dropped anything
/// the typed graph does not model - unknown root and child keys, extension-owned JSON, and the
/// reserved <c>agents.defaults</c> entry the loader lifts out of <c>Agents</c> - and it validated
/// only <em>after</em> the file had already been overwritten, so a rejected edit still left an
/// invalid file on disk.
/// </para>
/// <para>
/// Commands now express their change as a raw-JSON path mutation and hand it here. Read, mutate,
/// validate, and replace all happen inside the writer lock, and the live file is replaced only
/// once the <em>complete</em> candidate document has validated.
/// </para>
/// </remarks>
internal static class CliConfigMutation
{
    /// <summary>
    /// Applies <paramref name="mutation"/> to the raw config document at
    /// <paramref name="configPath"/> and persists it only if the resulting document validates.
    /// Rejection messages are rendered to the console.
    /// </summary>
    /// <param name="mutation">
    /// Edits the raw root in place; returns <see langword="null"/> on success or a
    /// caller-presentable message to abort the write without touching the file.
    /// </param>
    /// <returns>Process exit code: 0 when persisted, 1 when rejected.</returns>
    public static async Task<int> ApplyAsync(
        string configPath,
        Func<JsonObject, string?> mutation,
        string reason,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var writer = CreateWriter(configPath);

        IReadOnlyList<string> errors;
        try
        {
            errors = await writer.MutateValidatedAsync(mutation, reason, cancellationToken);
        }
        catch (PlatformConfigLockTimeoutException ex)
        {
            // #2134: another BotNexus process (a second CLI invocation, or the running gateway) is
            // inside the config critical section. Report the conflict explicitly and exit non-zero
            // rather than writing without the lock - a silently lost config edit is the defect.
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        if (errors.Count > 0)
        {
            AnsiConsole.MarkupLine("[red]Config validation failed; the existing config was not modified:[/]");
            foreach (var error in errors)
                AnsiConsole.MarkupLine($"  [red]\u2022[/] {Markup.Escape(error)}");
            return 1;
        }

        if (verbose)
            AnsiConsole.MarkupLine($"[dim]Saved config: {Markup.Escape(configPath)}[/]");

        return 0;
    }

    /// <summary>
    /// Reads the raw config document for a command that must inspect the on-disk JSON (existing
    /// keys, unknown fields) before deciding what to mutate. Note this read is outside the writer
    /// lock and is therefore advisory only - the authoritative read happens inside
    /// <see cref="ApplyAsync"/>, so the mutation callback must re-check anything it depends on.
    /// </summary>
    public static async Task<JsonObject> ReadAsync(string configPath, CancellationToken cancellationToken)
        => await CreateWriter(configPath).ReadAsync(cancellationToken);

    private static PlatformConfigWriter CreateWriter(string configPath)
    {
        var directory = Path.GetDirectoryName(configPath) ?? BotNexusHome.ResolveHomePath();
        PlatformConfigLoader.EnsureConfigDirectory(directory);

        var fileSystem = new System.IO.Abstractions.FileSystem();
        var backupsDir = Path.Combine(directory, "backups");
        return new PlatformConfigWriter(configPath, fileSystem, new ConfigBackupService(backupsDir, fileSystem));
    }
}
