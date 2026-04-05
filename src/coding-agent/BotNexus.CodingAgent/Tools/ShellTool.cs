using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core.Models;

namespace BotNexus.CodingAgent.Tools;

/// <summary>
/// Executes shell commands and returns normalized process output.
/// </summary>
/// <remarks>
/// <para>
/// Platform contract: Windows prefers Git Bash when available, falling back to PowerShell;
/// non-Windows executes through bash. This prioritizes portable command semantics across platforms.
/// </para>
/// <para>
/// Output is capped at 50 * 1024 (51,200) bytes to protect downstream token budgets and prevent
/// runaway responses from large command streams while preserving tail errors.
/// </para>
/// </remarks>
public sealed class ShellTool : IAgentTool
{
    private const int DefaultTimeoutSeconds = 120;
    private const int MaxOutputBytes = 50 * 1024;
    private const int MaxOutputLines = 2000;
    private static readonly Lazy<string?> WindowsBashPath = new(FindBashExecutable);

    /// <inheritdoc />
    public string Name => "bash";

    /// <inheritdoc />
    public string Label => "Bash";

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Execute a bash command in the current working directory and return stdout/stderr.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "command": {
                  "type": "string",
                  "description": "Shell command text to execute."
                },
                "timeout": {
                  "type": "integer",
                  "description": "Optional timeout in seconds. Defaults to 120."
                }
              },
              "required": ["command"]
            }
            """).RootElement.Clone());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var command = ReadRequiredString(arguments, "command");
        var timeoutSeconds = DefaultTimeoutSeconds;

        if (arguments.TryGetValue("timeout", out var rawTimeout) && rawTimeout is not null)
        {
            timeoutSeconds = ReadInt(rawTimeout, "timeout");
            if (timeoutSeconds < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(arguments), "timeout must be >= 1 second.");
            }
        }

        IReadOnlyDictionary<string, object?> prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["command"] = command,
            ["timeout"] = timeoutSeconds
        };

        return Task.FromResult(prepared);
    }

    /// <inheritdoc />
    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var command = arguments["command"]?.ToString()
                      ?? throw new ArgumentException("Missing required argument: command.");
        var timeoutSeconds = arguments.TryGetValue("timeout", out var timeoutObj) && timeoutObj is int timeout
            ? timeout
            : DefaultTimeoutSeconds;

        var invocation = BuildShellInvocation(command);
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            Arguments = invocation.Args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start shell process.");
        }

        var outputBuffer = new List<(long Seq, string Line)>();
        var sequence = 0L;
        void Append(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            lock (outputBuffer)
            {
                outputBuffer.Add((Interlocked.Increment(ref sequence), text));
            }
        }

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                Append(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                Append(eventArgs.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var timeoutOutput = BuildOutput(outputBuffer, includeTruncationNotes: true);
            timeoutOutput = PrependWarning(timeoutOutput, invocation.WarningPrefix);
            TryKill(process);
            var timeoutMessage = string.IsNullOrWhiteSpace(timeoutOutput)
                ? $"Command timed out after {timeoutSeconds} seconds."
                : $"Command timed out after {timeoutSeconds} seconds.{Environment.NewLine}{Environment.NewLine}{timeoutOutput}";
            return new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, timeoutMessage)],
                new ShellToolDetails(-1, TimedOut: true, IsError: true));
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var output = BuildOutput(outputBuffer, includeTruncationNotes: true);
        output = PrependWarning(output, invocation.WarningPrefix);
        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(output))
        {
            output = $"{output}{Environment.NewLine}{Environment.NewLine}[command exited with code {process.ExitCode}]";
        }

        return new AgentToolResult(
            [new AgentToolContent(AgentToolContentType.Text, output)],
            new ShellToolDetails(process.ExitCode, TimedOut: false, IsError: process.ExitCode != 0));
    }

    private static ShellInvocation BuildShellInvocation(string command)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var bashPath = WindowsBashPath.Value;
            if (!string.IsNullOrWhiteSpace(bashPath))
            {
                var bashEscaped = command.Replace("'", "'\"'\"'", StringComparison.Ordinal);
                return new ShellInvocation(bashPath, $"-lc '{bashEscaped}'", null);
            }

            var escaped = command.Replace("\"", "`\"", StringComparison.Ordinal);
            return new ShellInvocation(
                "powershell",
                $"-NoLogo -NoProfile -NonInteractive -Command \"{escaped}\"",
                "[warning: bash not found, using PowerShell — install Git for Windows for best compatibility]\n");
        }

        var unixBashEscaped = command.Replace("'", "'\"'\"'", StringComparison.Ordinal);
        return new ShellInvocation("/bin/bash", $"-lc '{unixBashEscaped}'", null);
    }

    private static string? FindBashExecutable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        var candidates = new[]
        {
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe"
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        try
        {
            var whereStartInfo = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "bash",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(whereStartInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            var firstPath = output
                .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return !string.IsNullOrWhiteSpace(firstPath) && File.Exists(firstPath)
                ? firstPath
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort process cleanup; propagate original execution exception.
        }
    }

    private static string BuildOutput(IReadOnlyList<(long Seq, string Line)> outputBuffer, bool includeTruncationNotes)
    {
        var ordered = outputBuffer.OrderBy(item => item.Seq).Select(item => item.Line).ToList();
        if (ordered.Count == 0)
        {
            return string.Empty;
        }

        var lineLimitReached = ordered.Count > MaxOutputLines;
        var lines = lineLimitReached ? ordered.Take(MaxOutputLines).ToList() : ordered;

        var builder = new StringBuilder();
        var bytes = 0;
        var bytesLimitReached = false;
        foreach (var line in lines)
        {
            var text = line + Environment.NewLine;
            var lineBytes = Encoding.UTF8.GetByteCount(text);
            if (bytes + lineBytes > MaxOutputBytes)
            {
                bytesLimitReached = true;
                break;
            }

            builder.Append(text);
            bytes += lineBytes;
        }

        if (includeTruncationNotes)
        {
            if (lineLimitReached)
            {
                builder.AppendLine($"[Output truncated at {MaxOutputLines} lines]");
            }

            if (bytesLimitReached)
            {
                builder.AppendLine($"[Output truncated at {MaxOutputBytes} bytes]");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string PrependWarning(string output, string? warningPrefix)
    {
        if (string.IsNullOrWhiteSpace(warningPrefix))
        {
            return output;
        }

        return string.IsNullOrWhiteSpace(output)
            ? warningPrefix.TrimEnd()
            : $"{warningPrefix}{output}";
    }

    private static string ReadRequiredString(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            throw new ArgumentException($"Missing required argument: {key}.");
        }

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()
                ?? throw new ArgumentException($"Argument '{key}' cannot be null."),
            JsonElement element => element.ToString(),
            _ => value.ToString() ?? throw new ArgumentException($"Argument '{key}' is invalid.")
        };
    }

    private static int ReadInt(object value, string key)
    {
        return value switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var parsed) => parsed,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => throw new ArgumentException($"Argument '{key}' must be an integer.")
        };
    }

    public sealed record ShellToolDetails(int ExitCode, bool TimedOut, bool IsError);

    private sealed record ShellInvocation(string FileName, string Args, string? WarningPrefix);
}
