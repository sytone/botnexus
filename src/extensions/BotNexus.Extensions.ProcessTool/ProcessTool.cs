using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.ProcessTool;

/// <summary>
/// Agent tool for managing background processes by PID.
/// Supports listing, status checks, output reading, stdin writes, and termination.
/// </summary>
public sealed class ProcessTool : IAgentTool
{
    private readonly ProcessManager _manager;
    private readonly ProcessToolOptions _options;

    public ProcessTool() : this(ProcessManager.Instance, ProcessToolOptions.Default) { }

    internal ProcessTool(ProcessManager manager) : this(manager, ProcessToolOptions.Default) { }

    internal ProcessTool(ProcessManager manager, ProcessToolOptions options)
    {
        _manager = manager;
        _options = options ?? ProcessToolOptions.Default;
    }

    public string Name => "process";
    public string Label => "Process Manager";

    public Tool Definition => new(
        Name,
        "Manage background processes by PID. List, inspect, send input, read output, or kill processes.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["status", "output", "input", "kill", "list"],
                  "description": "Action to perform on a background process."
                },
                "pid": {
                  "type": "integer",
                  "description": "Process ID (required for status/output/input/kill)."
                },
                "content": {
                  "type": "string",
                  "description": "Content to send to stdin (for input action)."
                },
                "tail": {
                  "type": "integer",
                  "description": "Number of lines from end of output (for output action). Default: 50. Values above the configured ceiling are clamped."
                },
                "timeout": {
                  "type": "integer",
                  "description": "For status action: wait up to N ms for process to produce output before returning. Default: 0 (no wait)."
                }
              },
              "required": ["action"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(arguments);
    }

    public Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var action = ReadString(arguments, "action") ?? string.Empty;

        var result = action.ToLowerInvariant() switch
        {
            "list" => HandleList(),
            "status" => HandleStatus(arguments),
            "output" => HandleOutput(arguments),
            "input" => HandleInput(arguments),
            "kill" => HandleKill(arguments),
            _ => TextResult($"Unknown action: {action}. Valid actions: list, status, output, input, kill.")
        };

        return Task.FromResult(result);
    }

    private AgentToolResult HandleList()
    {
        var processes = _manager.List();
        if (processes.Count == 0)
            return TextResult("No tracked processes.");

        var sb = new StringBuilder();
        sb.AppendLine("PID | Command | Status | Started | Exit Code");
        sb.AppendLine("--- | ------- | ------ | ------- | ---------");
        foreach (var p in processes)
        {
            var status = p.IsRunning ? "running" : "exited";
            var exitCode = p.ExitCode?.ToString() ?? "-";
            sb.AppendLine($"{p.Pid} | {p.Command} | {status} | {p.StartedAt:u} | {exitCode}");
        }

        return TextResult(sb.ToString());
    }

    private AgentToolResult HandleStatus(IReadOnlyDictionary<string, object?> arguments)
    {
        var pid = ReadInt(arguments, "pid");
        if (pid is null)
            return TextResult("Error: pid is required for status action.");

        var process = _manager.Get(pid.Value);
        if (process is null)
            return TextResult($"No tracked process with PID {pid}.");

        var timeout = ReadInt(arguments, "timeout") ?? 0;
        if (timeout > 0 && process.IsRunning)
            process.WaitForExit(timeout);

        var status = process.IsRunning ? "running" : "exited";
        var exitCode = process.ExitCode;

        var sb = new StringBuilder();
        sb.AppendLine($"PID: {process.Pid}");
        sb.AppendLine($"Command: {process.Command}");
        sb.AppendLine($"Status: {status}");
        sb.AppendLine($"Started: {process.StartedAt:u}");
        if (exitCode is not null)
            sb.AppendLine($"Exit Code: {exitCode}");

        return TextResult(sb.ToString());
    }

    private AgentToolResult HandleOutput(IReadOnlyDictionary<string, object?> arguments)
    {
        var pid = ReadInt(arguments, "pid");
        if (pid is null)
            return TextResult("Error: pid is required for output action.");

        var process = _manager.Get(pid.Value);
        if (process is null)
            return TextResult($"No tracked process with PID {pid}.");

        // Preserve the "tail <= 0 means full output" convention; only bound the upper end so a
        // huge positive value cannot request more than the configured ceiling of trailing lines.
        var requestedTail = ReadInt(arguments, "tail") ?? 50;
        var tail = requestedTail > _options.MaxTail ? _options.MaxTail : requestedTail;
        var output = process.GetOutput(tail);

        return TextResult(string.IsNullOrEmpty(output)
            ? $"No output captured for PID {pid}."
            : output);
    }

    private AgentToolResult HandleInput(IReadOnlyDictionary<string, object?> arguments)
    {
        var pid = ReadInt(arguments, "pid");
        if (pid is null)
            return TextResult("Error: pid is required for input action.");

        var content = ReadString(arguments, "content");
        if (string.IsNullOrEmpty(content))
            return TextResult("Error: content is required for input action.");

        var process = _manager.Get(pid.Value);
        if (process is null)
            return TextResult($"No tracked process with PID {pid}.");

        try
        {
            process.WriteInput(content);
            return TextResult($"Sent {content.Length} characters to PID {pid}.");
        }
        catch (InvalidOperationException ex)
        {
            return TextResult($"Error: {ex.Message}");
        }
    }

    private AgentToolResult HandleKill(IReadOnlyDictionary<string, object?> arguments)
    {
        var pid = ReadInt(arguments, "pid");
        if (pid is null)
            return TextResult("Error: pid is required for kill action.");

        var process = _manager.Get(pid.Value);
        if (process is null)
            return TextResult($"No tracked process with PID {pid}.");

        // Already exited — report as no-op.
        if (!process.IsRunning)
        {
            var code = process.ExitCode;
            return TextResult($"Process {pid} already exited (code {code}).");
        }

        process.Kill();

        var exitCode = process.ExitCode;
        return TextResult($"Process {pid} terminated (exit code {exitCode}).");
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            JsonElement el => el.ToString(),
            _ => value.ToString()
        };
    }

    private static int? ReadInt(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } el => ReadJsonNumber(el),
            JsonElement el when int.TryParse(el.ToString(), out var n) => n,
            int n => n,
            long l => ClampLong(l),
            _ when int.TryParse(value.ToString(), out var n) => n,
            _ => null
        };
    }

    // Tolerate out-of-range JSON numbers (e.g. tail: 99999999999) instead of throwing from
    // GetInt32(); the caller clamps the value, so saturating to int bounds is the safe behaviour.
    private static int? ReadJsonNumber(JsonElement element)
    {
        if (element.TryGetInt32(out var i)) return i;
        if (element.TryGetInt64(out var l)) return ClampLong(l);
        if (element.TryGetDouble(out var d))
            return (int)Math.Clamp(d, int.MinValue, (double)int.MaxValue);
        return null;
    }

    private static int ClampLong(long value)
        => (int)Math.Clamp(value, int.MinValue, int.MaxValue);

    private static AgentToolResult TextResult(string text)
        => new([new AgentToolContent(AgentToolContentType.Text, text)]);
}
