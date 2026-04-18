using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.ProcessTool;

/// <summary>
/// Agent tool for managing background processes spawned by the exec tool.
/// Supports listing, status checks, output reading, stdin writes, and termination.
/// </summary>
public sealed class ProcessTool : IAgentTool
{
    private readonly ProcessManager _manager;

    public ProcessTool() : this(ProcessManager.Instance) { }

    internal ProcessTool(ProcessManager manager)
    {
        _manager = manager;
    }

    public string Name => "process";
    public string Label => "Process Manager";

    public Tool Definition => new(
        Name,
        "Manage background processes spawned by the exec tool. List, inspect, send input, read output, or kill processes.",
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
                  "description": "Number of lines from end of output (for output action). Default: 50."
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

        var tail = ReadInt(arguments, "tail") ?? 50;
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
            JsonElement { ValueKind: JsonValueKind.Number } el => el.GetInt32(),
            JsonElement el when int.TryParse(el.ToString(), out var n) => n,
            int n => n,
            long l => (int)l,
            _ when int.TryParse(value.ToString(), out var n) => n,
            _ => null
        };
    }

    private static AgentToolResult TextResult(string text)
        => new([new AgentToolContent(AgentToolContentType.Text, text)]);
}
