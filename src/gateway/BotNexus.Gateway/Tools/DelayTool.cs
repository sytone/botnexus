using System.Diagnostics;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Configuration;
using BotNexus.Agent.Providers.Core.Models;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tools;

public sealed class DelayTool(IOptions<DelayToolOptions> options) : IAgentTool
{
    private readonly DelayToolOptions _options = options?.Value ?? new DelayToolOptions();

    public string Name => "delay";
    public string Label => "Delay / Wait";

    public Tool Definition => new(
        Name,
        "Pause execution for a specified duration. Use to wait before performing an action, or to create polling loops (e.g., review a document every few minutes).",
        JsonDocument.Parse($$"""
            {
              "type": "object",
              "properties": {
                "seconds": {
                  "type": "integer",
                  "description": "Duration to wait in seconds (1 to maxDelay).",
                  "minimum": 1,
                  "maximum": {{Math.Max(1, _options.MaxDelaySeconds)}}
                },
                "reason": {
                  "type": "string",
                  "description": "Why the agent is waiting (for logging/display)."
                }
              },
              "required": ["seconds"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = ReadRequiredInt(arguments, "seconds");
        _ = ReadString(arguments, "reason");
        return Task.FromResult(arguments);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var requestedSeconds = ReadRequiredInt(arguments, "seconds");
        var maxDelaySeconds = Math.Max(1, _options.MaxDelaySeconds);
        var seconds = Math.Clamp(requestedSeconds, 1, maxDelaySeconds);
        var reason = ReadString(arguments, "reason");

        var waitMessage = $"Waiting {seconds} seconds{(string.IsNullOrWhiteSpace(reason) ? "." : $" (reason: {reason}).")}";
        onUpdate?.Invoke(TextResult(waitMessage));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
            var reasonSuffix = string.IsNullOrWhiteSpace(reason) ? "" : $" Reason: {reason}";
            return TextResult($"Waited {seconds} seconds.{reasonSuffix} Ready to continue.");
        }
        catch (OperationCanceledException)
        {
            var elapsed = Math.Max(0, (int)stopwatch.Elapsed.TotalSeconds);
            return TextResult($"Wait was interrupted after {elapsed} seconds. Reason: {reason ?? "cancellation requested"}.");
        }
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
    }

    private static int ReadRequiredInt(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            throw new ArgumentException($"Missing required argument: {key}.");

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var number) => number,
            JsonElement { ValueKind: JsonValueKind.Number } element => (int)element.GetDouble(),
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } element when double.TryParse(element.GetString(), out var d) => (int)d,
            int number => number,
            long l => (int)l,
            double d => (int)d,
            string text when int.TryParse(text, out var parsed) => parsed,
            string text when double.TryParse(text, out var d) => (int)d,
            _ => throw new ArgumentException($"Argument '{key}' must be an integer.")
        };
    }

    private static AgentToolResult TextResult(string text)
        => new([new AgentToolContent(AgentToolContentType.Text, text)]);
}
