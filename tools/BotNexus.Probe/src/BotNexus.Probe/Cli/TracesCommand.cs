using BotNexus.Probe.Otel;

namespace BotNexus.Probe.Cli;

public static class TracesCommand
{
    public static Task<int> ListAsync(
        CliOptions options,
        string[] args,
        TraceStore traceStore)
    {
        var limit = 100;
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].Equals("--limit", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 < args.Length && int.TryParse(args[index + 1], out var parsed))
            {
                limit = Math.Clamp(parsed, 1, 1_000);
                break;
            }
        }

        var spans = traceStore.GetTraces(limit);
        var payload = new
        {
            status = spans.Count > 0 ? "ok" : "empty",
            count = spans.Count,
            items = spans
        };

        CliOutput.Write(options, payload, () => CliOutput.FormatSpans("Recent Traces", spans));
        return Task.FromResult(spans.Count > 0 ? 0 : 2);
    }

    public static Task<int> GetAsync(
        CliOptions options,
        string[] args,
        TraceStore traceStore)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            CliOutput.WriteError("trace command requires an id. Usage: probe trace <traceId>");
            return Task.FromResult(1);
        }

        var traceId = args[0];
        var spans = traceStore.GetTraceById(traceId);
        var payload = new
        {
            status = spans.Count > 0 ? "ok" : "empty",
            traceId,
            count = spans.Count,
            items = spans
        };

        CliOutput.Write(options, payload, () => CliOutput.FormatSpans($"Trace {traceId}", spans));
        return Task.FromResult(spans.Count > 0 ? 0 : 2);
    }
}
