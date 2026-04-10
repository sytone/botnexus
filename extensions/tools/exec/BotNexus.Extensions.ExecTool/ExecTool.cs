using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core.Models;
using System.IO.Abstractions;

namespace BotNexus.Extensions.ExecTool;

/// <summary>
/// Enhanced shell execution tool with configurable timeouts, background mode,
/// no-output timeout, stdin piping, environment merging, and Windows .cmd/.bat resolution.
/// </summary>
public sealed class ExecTool : IAgentTool
{
    private const int DefaultTimeoutMs = 120_000;
    private const int MaxOutputBytes = 100 * 1024;

    private static readonly ConcurrentDictionary<int, ProcessInfo> BackgroundProcesses = new();

    private readonly string? _workingDirectory;
    private readonly IFileSystem _fileSystem;

    public ExecTool(string? workingDirectory = null, IFileSystem? fileSystem = null)
    {
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? null
            : Path.GetFullPath(workingDirectory);
        _fileSystem = fileSystem ?? new FileSystem();
    }

    /// <inheritdoc />
    public string Name => "exec";

    /// <inheritdoc />
    public string Label => "Exec";

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Execute a command with advanced process management: timeouts, background mode, stdin piping, and environment variable merging.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "command": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Command and arguments as an array. First element is the command, rest are args."
                },
                "timeoutMs": {
                  "type": "integer",
                  "description": "Max execution time in milliseconds. Default: 120000 (2 min)."
                },
                "noOutputTimeoutMs": {
                  "type": "integer",
                  "description": "Kill if no output for this many ms. Default: none."
                },
                "input": {
                  "type": "string",
                  "description": "String to pipe to stdin."
                },
                "background": {
                  "type": "boolean",
                  "description": "If true, start in background and return PID immediately."
                },
                "env": {
                  "type": "object",
                  "description": "Additional environment variables to set."
                },
                "workingDir": {
                  "type": "string",
                  "description": "Working directory override."
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

        var command = ReadStringArray(arguments, "command");
        if (command.Count == 0)
        {
            throw new ArgumentException("command array must contain at least one element.");
        }

        var timeoutMs = ReadOptionalInt(arguments, "timeoutMs") ?? DefaultTimeoutMs;
        if (timeoutMs < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(arguments), "timeoutMs must be >= 1.");
        }

        var noOutputTimeoutMs = ReadOptionalInt(arguments, "noOutputTimeoutMs");
        if (noOutputTimeoutMs is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(arguments), "noOutputTimeoutMs must be >= 1.");
        }

        var input = ReadOptionalString(arguments, "input");
        var background = ReadOptionalBool(arguments, "background") ?? false;
        var env = ReadOptionalStringDictionary(arguments, "env");

        var workingDir = ReadOptionalString(arguments, "workingDir");
        if (!string.IsNullOrWhiteSpace(workingDir))
        {
            workingDir = Path.GetFullPath(workingDir);
        }

        IReadOnlyDictionary<string, object?> prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["command"] = command,
            ["timeoutMs"] = timeoutMs,
            ["noOutputTimeoutMs"] = noOutputTimeoutMs,
            ["input"] = input,
            ["background"] = background,
            ["env"] = env,
            ["workingDir"] = workingDir,
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
        var command = (IReadOnlyList<string>)arguments["command"]!;
        var timeoutMs = (int)arguments["timeoutMs"]!;
        var noOutputTimeoutMs = arguments["noOutputTimeoutMs"] as int?;
        var input = arguments["input"] as string;
        var background = (bool)arguments["background"]!;
        var env = arguments["env"] as IReadOnlyDictionary<string, string>;
        var workingDir = arguments["workingDir"] as string;

        var (fileName, processArgs) = ResolveCommand(command, _fileSystem);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir ?? _workingDirectory ?? string.Empty,
        };

        foreach (var arg in processArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start process.");
        }

        if (background)
        {
            var pid = process.Id;
            BackgroundProcesses[pid] = new ProcessInfo(pid, command[0], DateTime.UtcNow);

            // Write stdin if provided, then detach
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            var result = JsonSerializer.Serialize(new { pid, status = "running" });
            return new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, result)],
                new ExecToolDetails(0, Termination: "background", Pid: pid));
        }

        // Write stdin if provided
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        var outputBuffer = new StringBuilder();
        var totalBytes = 0;
        var outputLock = new object();
        var noOutputCts = noOutputTimeoutMs.HasValue
            ? new CancellationTokenSource(noOutputTimeoutMs.Value)
            : null;

        void OnDataReceived(string? data)
        {
            if (data is null) return;

            lock (outputLock)
            {
                var lineBytes = Encoding.UTF8.GetByteCount(data) + Environment.NewLine.Length;
                if (totalBytes + lineBytes <= MaxOutputBytes)
                {
                    outputBuffer.AppendLine(data);
                    totalBytes += lineBytes;
                }
            }

            // Reset no-output timer on each data event
            noOutputCts?.CancelAfter(noOutputTimeoutMs!.Value);
        }

        process.OutputDataReceived += (_, e) => OnDataReceived(e.Data);
        process.ErrorDataReceived += (_, e) => OnDataReceived(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        var tokens = noOutputCts is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, noOutputCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        using (tokens)
        using (noOutputCts)
        {
            string termination;
            try
            {
                await process.WaitForExitAsync(tokens.Token).ConfigureAwait(false);
                termination = "exit";
            }
            catch (OperationCanceledException)
            {
                TryKill(process);

                if (cancellationToken.IsCancellationRequested)
                {
                    termination = "cancelled";
                }
                else if (noOutputCts is not null && noOutputCts.IsCancellationRequested && !timeoutCts.IsCancellationRequested)
                {
                    termination = "no-output-timeout";
                }
                else
                {
                    termination = "timeout";
                }
            }
            catch
            {
                TryKill(process);
                throw;
            }

            string output;
            lock (outputLock)
            {
                output = outputBuffer.ToString().TrimEnd();
                if (totalBytes >= MaxOutputBytes)
                {
                    output = $"[output truncated at {MaxOutputBytes / 1024}KB]\n{output}";
                }
            }

            var exitCode = termination == "exit" ? process.ExitCode : -1;

            var message = termination switch
            {
                "timeout" => $"Process timed out after {timeoutMs}ms.{FormatOutput(output)}",
                "no-output-timeout" => $"Process killed: no output for {noOutputTimeoutMs}ms.{FormatOutput(output)}",
                "cancelled" => $"Process cancelled.{FormatOutput(output)}",
                _ when exitCode != 0 && !string.IsNullOrWhiteSpace(output) =>
                    $"{output}\n\n[exit code: {exitCode}]",
                _ when exitCode != 0 => $"[exit code: {exitCode}]",
                _ => string.IsNullOrWhiteSpace(output) ? "[no output]" : output,
            };

            return new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, message)],
                new ExecToolDetails(exitCode, termination));
        }
    }

    /// <summary>
    /// Gets information about tracked background processes.
    /// </summary>
    internal static IReadOnlyDictionary<int, ProcessInfo> GetBackgroundProcesses() => BackgroundProcesses;

    /// <summary>
    /// Clears the background process tracking dictionary. For testing only.
    /// </summary>
    internal static void ClearBackgroundProcesses() => BackgroundProcesses.Clear();

    private static string FormatOutput(string output)
    {
        return string.IsNullOrWhiteSpace(output) ? string.Empty : $"\n\n{output}";
    }

    /// <summary>
    /// Resolves command array into fileName and arguments, handling Windows .cmd/.bat shims.
    /// </summary>
    internal static (string FileName, IReadOnlyList<string> Args) ResolveCommand(IReadOnlyList<string> command)
        => ResolveCommand(command, new FileSystem());

    internal static (string FileName, IReadOnlyList<string> Args) ResolveCommand(
        IReadOnlyList<string> command,
        IFileSystem fileSystem)
    {
        var exe = command[0];
        var args = command.Count > 1 ? command.Skip(1).ToList() : new List<string>();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return (exe, args);
        }

        // On Windows, resolve .cmd/.bat files through cmd.exe
        var resolved = ResolveWindowsExecutable(exe, fileSystem);
        if (resolved is not null && IsWindowsBatchFile(resolved))
        {
            // Route through cmd.exe /c to handle .cmd/.bat
            var cmdArgs = new List<string> { "/d", "/s", "/c" };
            cmdArgs.Add(BuildCmdCommandLine(resolved, args));
            return (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", cmdArgs);
        }

        return (resolved ?? exe, args);
    }

    private static string? ResolveWindowsExecutable(string command, IFileSystem fileSystem)
    {
        if (Path.HasExtension(command))
        {
            return command;
        }

        // Look for common Windows script extensions
        string[] extensions = [".exe", ".cmd", ".bat"];
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        // Check current name first (might be in PATH as-is)
        foreach (var ext in extensions)
        {
            var candidate = command + ext;

            // Check in PATH directories
            foreach (var dir in pathDirs)
            {
                var fullPath = Path.Combine(dir, candidate);
                if (fileSystem.File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }

    private static bool IsWindowsBatchFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".cmd" or ".bat";
    }

    private static string BuildCmdCommandLine(string command, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        sb.Append(QuoteForCmd(command));
        foreach (var arg in args)
        {
            sb.Append(' ');
            sb.Append(QuoteForCmd(arg));
        }

        return sb.ToString();
    }

    private static string QuoteForCmd(string arg)
    {
        if (!arg.Contains(' ') && !arg.Contains('"'))
        {
            return arg;
        }

        return $"\"{arg.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
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
            // Best-effort process cleanup
        }
    }

    #region Argument helpers

    private static IReadOnlyList<string> ReadStringArray(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            throw new ArgumentException($"Missing required argument: {key}.");
        }

        return value switch
        {
            IReadOnlyList<string> list => list,
            JsonElement { ValueKind: JsonValueKind.Array } element =>
                element.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList(),
            _ => throw new ArgumentException($"Argument '{key}' must be a string array.")
        };
    }

    private static string? ReadOptionalString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
    }

    private static int? ReadOptionalInt(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

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

    private static bool? ReadOptionalBool(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => throw new ArgumentException($"Argument '{key}' must be a boolean.")
        };
    }

    private static IReadOnlyDictionary<string, string>? ReadOptionalStringDictionary(
        IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            IReadOnlyDictionary<string, string> dict => dict,
            JsonElement { ValueKind: JsonValueKind.Object } element =>
                element.EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty),
            _ => throw new ArgumentException($"Argument '{key}' must be an object with string values.")
        };
    }

    #endregion

    /// <summary>Details metadata returned alongside the tool result (not sent to the LLM).</summary>
    public sealed record ExecToolDetails(int ExitCode, string Termination, int? Pid = null);

    /// <summary>Tracks background processes launched by the exec tool.</summary>
    internal sealed record ProcessInfo(int Pid, string Command, DateTime StartedUtc);
}
