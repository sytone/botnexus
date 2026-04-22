using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Tools;

/// <summary>
/// Controls which shell is used for command execution.
/// On all platforms, <see cref="ShellPreference.Pwsh"/> uses PowerShell Core (pwsh).
/// <see cref="ShellPreference.Auto"/> and <see cref="ShellPreference.Bash"/> use bash.
/// </summary>
public enum ShellPreference
{
    /// <summary>Auto-detect: prefer bash (Git/WSL) when available, fall back to PowerShell.</summary>
    Auto,

    /// <summary>Always use PowerShell Core (pwsh) or Windows PowerShell.</summary>
    Pwsh,

    /// <summary>Always use bash (Git Bash, WSL, or /bin/bash).</summary>
    Bash,
}

/// <summary>
/// Executes shell commands and returns normalized process output.
/// </summary>
/// <remarks>
/// <para>
/// Platform contract controlled by <see cref="ShellPreference"/>:
/// <list type="bullet">
///   <item><see cref="ShellPreference.Auto"/> (default) — Windows prefers bash when available,
///         falling back to PowerShell; non-Windows uses bash.</item>
///   <item><see cref="ShellPreference.Pwsh"/> — Uses pwsh on all platforms.
///         Requires pwsh to be installed.</item>
///   <item><see cref="ShellPreference.Bash"/> — Always uses bash on all platforms.</item>
/// </list>
/// </para>
/// <para>
/// Output is capped at 50 * 1024 (51,200) bytes to protect downstream token budgets and prevent
/// runaway responses from large command streams while preserving tail errors.
/// </para>
/// </remarks>
public sealed class ShellTool : IAgentTool
{
    private const int MaxOutputBytes = 50 * 1024;
    private const int MaxOutputLines = 2000;
    private static readonly Lazy<string?> WindowsBashPath = new(FindBashExecutable);
    private readonly string? _workingDirectory;
    private readonly int? _defaultTimeoutSeconds;
    private readonly ShellPreference _shellPreference;

    public ShellTool(string? workingDirectory = null, int? defaultTimeoutSeconds = 600, ShellPreference shellPreference = ShellPreference.Auto)
    {
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? null
            : Path.GetFullPath(workingDirectory);

        if (defaultTimeoutSeconds is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultTimeoutSeconds), "defaultTimeoutSeconds must be >= 1 second when set.");
        }

        _defaultTimeoutSeconds = defaultTimeoutSeconds;
        _shellPreference = shellPreference;
    }

    /// <inheritdoc />
    public string Name => _shellPreference == ShellPreference.Pwsh
        ? "shell"
        : "bash";

    /// <inheritdoc />
    public string Label => _shellPreference == ShellPreference.Pwsh
        ? "Shell (PowerShell)"
        : "Bash";

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        _shellPreference == ShellPreference.Pwsh
            ? "Execute a PowerShell command in the current working directory and return stdout/stderr."
            : "Execute a bash command in the current working directory and return stdout/stderr.",
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
                  "description": "Optional timeout in seconds. Defaults to configured shell timeout."
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
        int? timeoutSeconds = _defaultTimeoutSeconds;

        if (arguments.TryGetValue("timeout", out var rawTimeout) && rawTimeout is not null)
        {
            timeoutSeconds = ReadInt(rawTimeout, "timeout");
            ValidateTimeoutSeconds(timeoutSeconds, nameof(arguments));
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
        int? timeoutSeconds = null;
        if (arguments.TryGetValue("timeout", out var timeoutObj) && timeoutObj is not null)
        {
            timeoutSeconds = ReadInt(timeoutObj, "timeout");
            ValidateTimeoutSeconds(timeoutSeconds, nameof(arguments));
        }
        else
        {
            timeoutSeconds = _defaultTimeoutSeconds;
        }

        var invocation = BuildShellInvocation(command, _shellPreference);
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workingDirectory ?? string.Empty
        };

        // Handle different argument passing methods
        if (!string.IsNullOrEmpty(invocation.Args))
        {
            startInfo.Arguments = invocation.Args;
        }
        else if (!string.IsNullOrEmpty(invocation.Command))
        {
            // For Unix bash with commands, use ArgumentList to avoid escaping issues
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(invocation.Command);
        }

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

        using var timeoutCts = timeoutSeconds.HasValue
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds.Value))
            : null;
        using var linkedCts = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested != true)
        {
            var cancelledOutput = BuildOutput(outputBuffer, includeTruncationNotes: true);
            cancelledOutput = PrependWarning(cancelledOutput, invocation.WarningPrefix);
            TryKill(process);
            var cancelledMessage = string.IsNullOrWhiteSpace(cancelledOutput)
                ? "Command cancelled."
                : $"Command cancelled.{Environment.NewLine}{Environment.NewLine}{cancelledOutput}";
            return new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, cancelledMessage)],
                new ShellToolDetails(-1, TimedOut: false, IsError: true));
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
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

    private static ShellInvocation BuildShellInvocation(string command, ShellPreference preference)
    {
        // Pwsh preference works on all platforms
        if (preference == ShellPreference.Pwsh)
        {
            return BuildPwshInvocation(command, warningPrefix: null);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (preference == ShellPreference.Bash)
            {
                var bashPath = WindowsBashPath.Value;
                if (!string.IsNullOrWhiteSpace(bashPath))
                {
                    var bashEscaped = command.Replace("'", "'\"'\"'", StringComparison.Ordinal);
                    return new ShellInvocation(bashPath, $"-lc '{bashEscaped}'", null, null);
                }

                // Bash explicitly requested but not found — fall back to pwsh with warning.
                return BuildPwshInvocation(command,
                    "[warning: bash not found, using PowerShell — install Git for Windows for bash support]\n");
            }

            // Auto: try bash first, fall back to pwsh.
            var autoBashPath = WindowsBashPath.Value;
            if (!string.IsNullOrWhiteSpace(autoBashPath))
            {
                var bashEscaped = command.Replace("'", "'\"'\"'", StringComparison.Ordinal);
                return new ShellInvocation(autoBashPath, $"-lc '{bashEscaped}'", null, null);
            }

            return BuildPwshInvocation(command,
                "[warning: bash not found, using PowerShell — install Git for Windows for best compatibility]\n");
        }

        // Unix with Auto or Bash preference: use bash with ArgumentList
        return new ShellInvocation("/bin/bash", null, command, null);
    }

    private static ShellInvocation BuildPwshInvocation(string command, string? warningPrefix)
    {
        // Prefer pwsh (PowerShell Core) over legacy powershell.exe when available.
        var pwshPath = FindPwshExecutable();
        var escaped = command.Replace("\"", "`\"", StringComparison.Ordinal);
        return new ShellInvocation(
            pwshPath,
            $"-NoLogo -NoProfile -NonInteractive -Command \"{escaped}\"",
            null,
            warningPrefix);
    }

    private static string FindPwshExecutable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "pwsh";
        }

        try
        {
            var whereStartInfo = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "pwsh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(whereStartInfo);
            if (process is null)
            {
                return "powershell";
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                return "pwsh";
            }
        }
        catch
        {
            // Fall through to legacy PowerShell.
        }

        return "powershell";
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

        var totalLines = ordered.Count;
        var lineLimitReached = ordered.Count > MaxOutputLines;
        var tailLines = lineLimitReached ? ordered.Skip(totalLines - MaxOutputLines).ToList() : ordered;

        var bytes = 0;
        var bytesLimitReached = false;
        var selected = new List<string>(tailLines.Count);
        for (var i = tailLines.Count - 1; i >= 0; i--)
        {
            var line = tailLines[i];
            var text = line + Environment.NewLine;
            var lineBytes = Encoding.UTF8.GetByteCount(text);
            if (bytes + lineBytes > MaxOutputBytes)
            {
                bytesLimitReached = true;
                break;
            }

            selected.Add(line);
            bytes += lineBytes;
        }

        selected.Reverse();
        var shownLines = selected.Count;
        var wasTruncated = lineLimitReached || bytesLimitReached || shownLines < totalLines;

        var builder = new StringBuilder();
        if (includeTruncationNotes && wasTruncated)
        {
            builder.AppendLine($"[output truncated — showing last {shownLines} lines of {totalLines}]");
        }

        foreach (var line in selected)
        {
            builder.AppendLine(line);
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
            double d => (int)d,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.Number } element => (int)element.GetDouble(),
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } element when double.TryParse(element.GetString(), out var parsedDouble) => (int)parsedDouble,
            string text when int.TryParse(text, out var parsed) => parsed,
            string text when double.TryParse(text, out var parsedDouble) => (int)parsedDouble,
            _ => throw new ArgumentException($"Argument '{key}' must be an integer.")
        };
    }

    private static void ValidateTimeoutSeconds(int? timeoutSeconds, string parameterName)
    {
        if (timeoutSeconds is < 1)
        {
            throw new ArgumentOutOfRangeException(parameterName, "timeout must be >= 1 second.");
        }
    }

    /// <summary>
    /// Represents shell tool details.
    /// </summary>
    public sealed record ShellToolDetails(int ExitCode, bool TimedOut, bool IsError);

    private sealed record ShellInvocation(string FileName, string? Args, string? Command, string? WarningPrefix);
}
