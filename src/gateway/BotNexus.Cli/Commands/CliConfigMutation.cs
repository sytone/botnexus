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
    /// Edits the document in place through the canonical-path surface; returns <see langword="null"/>
    /// on success or a caller-presentable message to abort the write without touching the file.
    /// </param>
    /// <param name="namedSections">
    /// The top-level config section this mutation targets, declared to the destructive-section
    /// guard (#2816). This is not a bypass: naming <c>providers</c> permits a provider command to
    /// empty <c>providers</c> - which removing the last provider legitimately does - while still
    /// leaving it unable to flatten <c>channels</c>, which is the actual defect. Every call site
    /// already knows its section as a <c>const</c>, so passing it costs nothing and omitting it
    /// fails closed.
    /// </param>
    /// <returns>Process exit code: 0 when persisted, 1 when rejected.</returns>
    public static async Task<int> ApplyAsync(
        string configPath,
        Func<ConfigDocument, string?> mutation,
        string reason,
        bool verbose,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? namedSections = null)
    {
        var writer = CreateWriter(configPath);

        IReadOnlyList<string> errors;
        try
        {
            errors = await writer.MutateDocumentValidatedAsync(mutation, reason, cancellationToken, namedSections);
        }
        catch (PlatformConfigLockTimeoutException ex)
        {
            // #2134: another BotNexus process (a second CLI invocation, or the running gateway) is
            // inside the config critical section. Report the conflict explicitly and exit non-zero
            // rather than writing without the lock - a silently lost config edit is the defect.
            AnsiConsole.MarkupLine($"[red]Error:[/] {CliText.SafeDisplay(ex.Message)}");
            return 1;
        }

        if (errors.Count > 0)
        {
            AnsiConsole.MarkupLine("[red]Config validation failed; the existing config was not modified:[/]");
            foreach (var error in errors)
                AnsiConsole.MarkupLine($"  [red]\u2022[/] {CliText.SafeDisplay(error)}");
            return 1;
        }

        if (verbose)
            AnsiConsole.MarkupLine($"[dim]Saved config: {CliText.SafeDisplay(configPath)}[/]");

        return 0;
    }

    /// <summary>
    /// Reads the config document for a command that must inspect what is on disk (existing keys,
    /// unknown fields) before deciding what to mutate. Note this read is outside the writer lock and
    /// is therefore advisory only - the authoritative read happens inside
    /// <see cref="ApplyAsync"/>, so the mutation callback must re-check anything it depends on.
    /// </summary>
    public static async Task<ConfigDocument> ReadAsync(string configPath, CancellationToken cancellationToken)
        => await CreateWriter(configPath).ReadDocumentAsync(cancellationToken);

    /// <summary>
    /// Creates the writer for <paramref name="configPath"/> with the backup service wired up.
    /// Exposed so the few commands that perform a whole-document write (<c>init</c>) or need the
    /// non-validating mutate overload go through the same construction rather than re-deriving the
    /// backup directory.
    /// </summary>
    public static PlatformConfigWriter CreateWriter(string configPath)
    {
        var directory = Path.GetDirectoryName(configPath) ?? BotNexusHome.ResolveHomePath();
        PlatformConfigLoader.EnsureConfigDirectory(directory);

        var fileSystem = new System.IO.Abstractions.FileSystem();
        var backupsDir = Path.Combine(directory, "backups");
        return new PlatformConfigWriter(configPath, fileSystem, new ConfigBackupService(backupsDir, fileSystem));
    }

    /// <summary>
    /// Creates the restore service for <paramref name="configPath"/> (#2884), wiring the backup
    /// store and the writer to the <em>same</em> filesystem and backups directory the write path
    /// uses.
    /// </summary>
    /// <remarks>
    /// Constructed here rather than at the call site so the backups directory is derived in exactly
    /// one place. A restore that enumerated a different directory from the one
    /// <see cref="CreateWriter"/> writes into would list snapshots it could not restore, and
    /// restore snapshots it had never listed.
    /// </remarks>
    public static ConfigBackupRestoreService CreateRestoreService(string configPath)
    {
        var directory = Path.GetDirectoryName(configPath) ?? BotNexusHome.ResolveHomePath();
        PlatformConfigLoader.EnsureConfigDirectory(directory);

        var fileSystem = new System.IO.Abstractions.FileSystem();
        var backups = new ConfigBackupService(Path.Combine(directory, "backups"), fileSystem);
        var writer = new PlatformConfigWriter(configPath, fileSystem, backups);
        return new ConfigBackupRestoreService(backups, writer, fileSystem);
    }
}
