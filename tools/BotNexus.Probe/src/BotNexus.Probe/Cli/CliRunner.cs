using BotNexus.Probe.Gateway;
using BotNexus.Probe.LogIngestion;
using BotNexus.Probe.Otel;

namespace BotNexus.Probe.Cli;

public static class CliRunner
{
    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "logs",
        "sessions",
        "session",
        "correlate",
        "files",
        "gateway",
        "traces",
        "trace"
    };

    public static bool IsCliCommand(string value)
        => Commands.Contains(value);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            var options = ParseSharedOptions(args);
            if (options.RemainingArgs.Length == 0)
            {
                CliOutput.WriteError("No CLI command provided.");
                return 1;
            }

            var command = options.RemainingArgs[0];
            var commandArgs = options.RemainingArgs[1..];
            var logParser = new SerilogFileParser();
            var sessionReader = new JsonlSessionReader();
            var sessionDbReader = TryCreateSessionDbReader(options.SessionDbPath);
            var traceStore = new TraceStore(10_000);

            try
            {
                return command.ToLowerInvariant() switch
                {
                    "logs" => await LogsCommand.RunAsync(options, commandArgs, logParser, cancellationToken),
                    "sessions" => await SessionsCommand.ListAsync(options, commandArgs, sessionReader, sessionDbReader, cancellationToken),
                    "session" => await SessionsCommand.GetAsync(options, commandArgs, sessionReader, sessionDbReader, cancellationToken),
                    "correlate" => await CorrelateCommand.RunAsync(options, commandArgs, logParser, sessionReader, traceStore, cancellationToken),
                    "files" => await FilesCommand.RunAsync(options, commandArgs, cancellationToken),
                    "gateway" => await GatewayCommand.RunAsync(options, commandArgs, cancellationToken),
                    "traces" => await TracesCommand.ListAsync(options, commandArgs, traceStore),
                    "trace" => await TracesCommand.GetAsync(options, commandArgs, traceStore),
                    _ => UnknownCommand(command)
                };
            }
            finally
            {
                sessionDbReader?.Dispose();
            }
        }
        catch (ArgumentException exception)
        {
            CliOutput.WriteError(exception.Message);
            return 1;
        }
        catch (Exception exception)
        {
            CliOutput.WriteError($"CLI command failed: {exception.Message}");
            return 1;
        }
    }

    private static CliOptions ParseSharedOptions(string[] args)
    {
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".botnexus", "logs");
        var sessions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".botnexus", "sessions");
        var sessionDb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".botnexus", "sessions.db");
        string? gateway = null;
        var textOutput = false;
        var remaining = new List<string>();

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            var nextValue = index + 1 < args.Length ? args[index + 1] : null;

            switch (arg)
            {
                case "--logs":
                    logs = nextValue ?? throw new ArgumentException("Missing value for --logs.");
                    index++;
                    break;
                case "--sessions":
                    sessions = nextValue ?? throw new ArgumentException("Missing value for --sessions.");
                    index++;
                    break;
                case "--gateway":
                    gateway = nextValue ?? throw new ArgumentException("Missing value for --gateway.");
                    index++;
                    break;
                case "--session-db":
                    sessionDb = nextValue ?? throw new ArgumentException("Missing value for --session-db.");
                    index++;
                    break;
                case "--text":
                    textOutput = true;
                    break;
                case "--json":
                    textOutput = false;
                    break;
                default:
                    remaining.Add(arg);
                    break;
            }
        }

        return new CliOptions(logs, sessions, sessionDb, gateway, textOutput, [.. remaining]);
    }

    private static int UnknownCommand(string command)
    {
        CliOutput.WriteError($"Unknown command '{command}'.");
        return 1;
    }

    private static SessionDbReader? TryCreateSessionDbReader(string sessionDbPath)
    {
        if (!File.Exists(sessionDbPath))
        {
            return null;
        }

        try
        {
            return new SessionDbReader(sessionDbPath);
        }
        catch
        {
            return null;
        }
    }
}
