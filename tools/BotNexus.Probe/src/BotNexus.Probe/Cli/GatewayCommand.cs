using BotNexus.Probe.Gateway;
using System.Text.Json;

namespace BotNexus.Probe.Cli;

public static class GatewayCommand
{
    public static async Task<int> RunAsync(CliOptions options, string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            CliOutput.WriteError("gateway command requires a subcommand: status|logs|agents|sessions");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(options.GatewayUrl))
        {
            CliOutput.WriteError("Gateway URL not configured. Pass --gateway <url>.");
            return 1;
        }

        using var client = new GatewayClient(options.GatewayUrl);
        return args[0].ToLowerInvariant() switch
        {
            "status" => await StatusAsync(options, client, cancellationToken),
            "logs" => await LogsAsync(options, args[1..], client, cancellationToken),
            "agents" => await ProxyAsync(options, "agents", () => client.GetAgentsAsync(cancellationToken)),
            "sessions" => await ProxyAsync(options, "sessions", () => client.GetSessionsAsync(cancellationToken)),
            _ => UnknownSubcommand(args[0])
        };
    }

    private static async Task<int> StatusAsync(CliOptions options, GatewayClient client, CancellationToken cancellationToken)
    {
        var health = await client.CheckHealthAsync(cancellationToken);
        var payload = new
        {
            status = "ok",
            configured = true,
            url = options.GatewayUrl,
            reachable = health.Reachable,
            healthy = health.Healthy,
            statusCode = health.StatusCode,
            payload = health.Payload
        };

        CliOutput.Write(options, payload, () =>
            $"""
            🌐 Gateway Status
            ━━━━━━━━━━━━━━━━━
              URL:        {options.GatewayUrl}
              Reachable:  {(health.Reachable ? "✅ Yes" : "❌ No")}
              Healthy:    {(health.Healthy ? "✅ Yes" : "❌ No")}
              Status:     {health.StatusCode?.ToString() ?? "n/a"}
            """);
        return 0;
    }

    private static async Task<int> LogsAsync(CliOptions options, string[] args, GatewayClient client, CancellationToken cancellationToken)
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

        return await ProxyAsync(options, "logs", () => client.GetRecentLogsAsync(limit, cancellationToken));
    }

    private static async Task<int> ProxyAsync(CliOptions options, string label, Func<Task<string?>> fetch)
    {
        var raw = await fetch();
        var element = CliOutput.ParseJsonElementOrNull(raw);
        if (element is null)
        {
            var emptyPayload = new { status = "empty", itemType = label, count = 0, items = Array.Empty<object>() };
            CliOutput.Write(options, emptyPayload, () => $"No gateway {label} results.");
            return 2;
        }

        var parsed = element.Value;
        var count = CliOutput.DetectCount(parsed);
        var payload = new
        {
            status = count > 0 ? "ok" : "empty",
            itemType = label,
            count,
            items = parsed
        };

        CliOutput.Write(options, payload, () => JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true }));
        return count > 0 ? 0 : 2;
    }

    private static int UnknownSubcommand(string name)
    {
        CliOutput.WriteError($"Unknown gateway subcommand '{name}'. Use status|logs|agents|sessions.");
        return 1;
    }
}
