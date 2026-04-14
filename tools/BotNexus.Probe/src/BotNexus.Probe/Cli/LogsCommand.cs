using BotNexus.Probe.LogIngestion;

namespace BotNexus.Probe.Cli;

public static class LogsCommand
{
    public static async Task<int> RunAsync(
        CliOptions options,
        string[] args,
        SerilogFileParser parser,
        CancellationToken cancellationToken)
    {
        var commandOptions = Parse(args);
        var query = new LogQuery(
            commandOptions.Level,
            commandOptions.From,
            commandOptions.To,
            commandOptions.CorrelationId,
            commandOptions.SessionId,
            commandOptions.AgentId,
            commandOptions.SearchText);

        var results = new List<LogEntry>(commandOptions.Take);
        var seen = 0;

        await foreach (var entry in parser.ParseDirectoryAsync(options.LogsPath, query, cancellationToken))
        {
            if (seen++ < commandOptions.Skip)
            {
                continue;
            }

            results.Add(entry);
            if (results.Count >= commandOptions.Take)
            {
                break;
            }
        }

        var payload = new
        {
            status = results.Count > 0 ? "ok" : "empty",
            skip = commandOptions.Skip,
            take = commandOptions.Take,
            count = results.Count,
            items = results
        };

        CliOutput.Write(options, payload, () => CliOutput.FormatLogs(results));
        return results.Count > 0 ? 0 : 2;
    }

    private static LogsCommandOptions Parse(string[] args)
    {
        string? level = null;
        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        string? sessionId = null;
        string? correlationId = null;
        string? agentId = null;
        string? searchText = null;
        var skip = 0;
        var take = 100;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            var nextValue = index + 1 < args.Length ? args[index + 1] : null;

            switch (arg)
            {
                case "--level":
                    level = nextValue;
                    index++;
                    break;
                case "--from" when DateTimeOffset.TryParse(nextValue, out var parsedFrom):
                    from = parsedFrom;
                    index++;
                    break;
                case "--to" when DateTimeOffset.TryParse(nextValue, out var parsedTo):
                    to = parsedTo;
                    index++;
                    break;
                case "--session":
                    sessionId = nextValue;
                    index++;
                    break;
                case "--correlation":
                    correlationId = nextValue;
                    index++;
                    break;
                case "--agent":
                    agentId = nextValue;
                    index++;
                    break;
                case "--search":
                    searchText = nextValue;
                    index++;
                    break;
                case "--skip" when int.TryParse(nextValue, out var parsedSkip):
                    skip = Math.Max(0, parsedSkip);
                    index++;
                    break;
                case "--take" when int.TryParse(nextValue, out var parsedTake):
                    take = Math.Clamp(parsedTake, 1, 1_000);
                    index++;
                    break;
            }
        }

        return new LogsCommandOptions(level, from, to, sessionId, correlationId, agentId, searchText, skip, take);
    }

    private sealed record LogsCommandOptions(
        string? Level,
        DateTimeOffset? From,
        DateTimeOffset? To,
        string? SessionId,
        string? CorrelationId,
        string? AgentId,
        string? SearchText,
        int Skip,
        int Take);
}
