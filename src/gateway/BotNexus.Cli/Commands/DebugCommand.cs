using System.CommandLine;

namespace BotNexus.Cli.Commands;

/// <summary>
/// Top-level <c>botnexus debug</c> command group for platform diagnostics.
/// Subcommands read SQLite and log files directly — no running gateway required.
/// </summary>
internal sealed class DebugCommand
{
    private readonly DebugSessionsCommand _sessions;

    public DebugCommand(DebugSessionsCommand sessions) => _sessions = sessions;

    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var command = new Command("debug", "Platform diagnostics — inspect sessions, cron, logs, and DB state directly.");
        command.AddCommand(_sessions.Build(targetOption));
        return command;
    }
}
