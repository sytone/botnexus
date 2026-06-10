using System.CommandLine;

namespace BotNexus.Cli.Commands;

/// <summary>
/// Top-level <c>botnexus debug</c> command that groups diagnostic subcommands
/// for offline platform inspection. All debug subcommands operate directly on
/// local files (SQLite databases, log files) without requiring a running gateway.
/// </summary>
internal sealed class DebugCommand
{
    private readonly DebugSessionsCommand _sessions = new();

    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var command = new Command("debug", "Platform diagnostics — inspect sessions, logs, and databases offline.");

        command.AddCommand(_sessions.Build(targetOption));

        return command;
    }
}
