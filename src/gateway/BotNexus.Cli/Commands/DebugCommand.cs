using System.CommandLine;
namespace BotNexus.Cli.Commands;
/// <summary>
/// Top-level <c>botnexus debug</c> command that groups diagnostic subcommands
/// for platform inspection. Most subcommands operate directly on local files
/// (SQLite databases, log files) without requiring a running gateway.
/// The <c>gateway</c> subcommand connects to a live gateway instance via REST API.
/// </summary>
internal sealed class DebugCommand
{
    private readonly DebugCronCommand _cron = new();
    private readonly DebugDbCommand _db = new();
    private readonly DebugGatewayCommand _gateway = new();
    private readonly DebugLogsCommand _logs = new();
    private readonly DebugSessionsCommand _sessions = new();
    public Command Build(Option<bool> verboseOption, Option<string?> targetOption)
    {
        var command = new Command("debug", "Platform diagnostics - inspect sessions, logs, databases, and live gateway state.");
        command.AddCommand(_cron.Build(targetOption));
        command.AddCommand(_db.Build(targetOption));
        command.AddCommand(_gateway.Build(targetOption));
        command.AddCommand(_logs.Build(targetOption));
        command.AddCommand(_sessions.Build(targetOption));
        return command;
    }
}
