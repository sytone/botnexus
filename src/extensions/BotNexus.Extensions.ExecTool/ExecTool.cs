using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
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

    /// <summary>
    /// Upper bound on the number of background-process entries retained in <see cref="BackgroundProcesses"/>.
    /// When a new background process is registered, dead entries are pruned first; if the map is still
    /// over this cap, the oldest entries (by start time) are evicted. This keeps the static registry
    /// bounded so a long-running gateway does not accumulate stale PIDs indefinitely.
    /// </summary>
    internal const int MaxBackgroundProcesses = 256;

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
    /// Exec tool can run long processes — default to 10 minutes.
    public TimeSpan? DefaultTimeout => TimeSpan.FromMinutes(10);

    /// <inheritdoc />
    public string Label => "Exec";

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Execute a command with advanced process management: timeouts, background mode, stdin piping, and environment variable merging. " +
        "On Windows PowerShell: wrap a variable followed by ':' as ${var} inside double-quoted strings (or use single quotes); " +
        "no backtick line-continuations; for multi-line/complex scripts write a tmp/*.ps1 file and run it. Inline Python prints " +
        "cp1252 by default (UnicodeEncodeError on emoji/em-dash/box glyphs) -- set $env:PYTHONUTF8=1 or write a tmp/*.py file " +
        "and run 'python -X utf8 file.py'. Never pipe a here-string into an interpreter; write a temp file and execute it.",
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
        if (env is not null)
        {
            foreach (var key in env.Keys)
            {
                ValidateEnvKey(key);
            }
        }

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

        // Preflight inline pwsh/powershell -Command scripts: reject syntax errors (empty pipe
        // elements, malformed ${...} references, unbalanced braces) BEFORE spawning a process so the
        // agent gets an immediate, actionable rejection instead of a late runtime ParserError. Only
        // inline -Command invocations are checked; -File invocations and non-PowerShell commands pass
        // through untouched, and valid one-liners are never altered.
        if (PowerShellPreflight.IsPowerShellExecutable(command[0])
            && PowerShellPreflight.TryGetInlineScript(processArgs, inlineScript: null, out var inlinePwshScript))
        {
            PowerShellPreflight.ThrowIfInvalid(inlinePwshScript);
        }

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

            // Keep the static registry bounded: drop dead PIDs and cap the retained count.
            PruneBackgroundProcesses();

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

            var clean = AnsiStripper.Strip(data);
            lock (outputLock)
            {
                var lineBytes = Encoding.UTF8.GetByteCount(clean) + Environment.NewLine.Length;
                if (totalBytes + lineBytes <= MaxOutputBytes)
                {
                    outputBuffer.AppendLine(clean);
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

    /// <summary>
    /// Bounds the background-process registry using the default <see cref="MaxBackgroundProcesses"/> cap.
    /// Drops dead PIDs and evicts the oldest entries when over the cap. Called after each background launch.
    /// </summary>
    internal static void PruneBackgroundProcesses()
    {
        PruneBackgroundProcesses(MaxBackgroundProcesses);
    }

    /// <summary>
    /// Bounds the background-process registry against an explicit cap. First removes entries whose
    /// underlying OS process is no longer alive (PID not found, or found but already exited). If the
    /// map is still larger than <paramref name="maxRetained"/>, evicts the oldest remaining entries
    /// (by start time) until it is within the cap. Safe to call concurrently. Exposed internally for tests.
    /// </summary>
    /// <param name="maxRetained">Maximum number of entries to retain after pruning dead PIDs.</param>
    internal static void PruneBackgroundProcesses(int maxRetained)
    {
        // Phase 1: remove dead PIDs.
        foreach (var kvp in BackgroundProcesses)
        {
            if (!IsPidAlive(kvp.Key))
            {
                BackgroundProcesses.TryRemove(kvp.Key, out _);
            }
        }

        // Phase 2: enforce the size cap, oldest-first.
        EvictOldestBackgroundProcesses(maxRetained);
    }

    /// <summary>
    /// Evicts the oldest background-process entries (by start time) until the registry holds at most
    /// <paramref name="maxRetained"/> entries. Does not perform liveness checks. Exposed internally so
    /// the cap behaviour can be tested deterministically with seeded entries.
    /// </summary>
    internal static void EvictOldestBackgroundProcesses(int maxRetained)
    {
        var overflow = BackgroundProcesses.Count - maxRetained;
        if (overflow <= 0)
        {
            return;
        }

        var oldest = BackgroundProcesses.Values
            .OrderBy(p => p.StartedUtc)
            .Take(overflow)
            .ToList();

        foreach (var info in oldest)
        {
            BackgroundProcesses.TryRemove(info.Pid, out _);
        }
    }

    /// <summary>
    /// Seeds a background-process entry directly. For testing only — lets tests populate the registry
    /// (e.g. with synthetic or already-dead PIDs) without spawning real processes.
    /// </summary>
    internal static void RegisterBackgroundForTest(int pid, string command, DateTime startedUtc)
        => BackgroundProcesses[pid] = new ProcessInfo(pid, command, startedUtc);

    /// <summary>
    /// Returns true when a process with the given PID is currently running. A PID that cannot be found,
    /// or that is found but has already exited, is treated as not alive.
    /// </summary>
    private static bool IsPidAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No process with that PID is running.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Process has already exited / terminated.
            return false;
        }
    }

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
            JsonElement { ValueKind: JsonValueKind.Number } element => (int)element.GetDouble(),
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } element when double.TryParse(element.GetString(), out var d) => (int)d,
            double d => (int)d,
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

    /// <summary>
    /// Blocked environment variable key prefixes and exact names that must not be overridden
    /// by agent-supplied <c>env</c> arguments.
    /// <list type="bullet">
    /// <item><c>LD_*</c> — Linux dynamic-linker control (e.g. <c>LD_PRELOAD</c>, <c>LD_LIBRARY_PATH</c>)</item>
    /// <item><c>DYLD_*</c> — macOS dynamic-linker control</item>
    /// <item><c>CLOUDSDK_*</c> — gcloud launcher runtime/interpreter controls (e.g. <c>CLOUDSDK_PYTHON</c>) that can hijack execution</item>
    /// <item><c>PATH</c> — executable search path; an agent override would redirect which binaries run</item>
    /// <item><c>PATHEXT</c> — Windows list of executable extensions; override could make .txt executable</item>
    /// <item><c>COMSPEC</c> — Windows path to cmd.exe; override redirects all cmd invocations</item>
    /// <item><c>SystemRoot</c> — Windows system directory; override can redirect DLL loading</item>
    /// <item><c>*_BASE_URL</c>, <c>*_API_HOST</c>, <c>*_ENDPOINT</c> — endpoint-redirection variables that can point a subprocess's API calls at an attacker-controlled host (credential exfiltration)</item>
    /// </list>
    /// </summary>
    public static readonly string[] BlockedEnvPrefixes = ["LD_", "DYLD_", "CLOUDSDK_"];
    public static readonly string[] BlockedEnvExact = ["PATH", "PATHEXT", "COMSPEC", "SYSTEMROOT"];
    public static readonly string[] BlockedEnvSuffixes = ["_BASE_URL", "_API_HOST", "_ENDPOINT"];

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when <paramref name="key"/> is a blocked
    /// environment variable name.
    /// </summary>
    /// <exception cref="ArgumentException">The key matches a blocked prefix or exact name.</exception>
    public static void ValidateEnvKey(string key)
    {
        foreach (var exact in BlockedEnvExact)
        {
            if (string.Equals(key, exact, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Environment variable '{key}' cannot be overridden via the exec env parameter. " +
                    $"{exact} overrides may redirect binary resolution or system paths.");
            }
        }

        foreach (var prefix in BlockedEnvPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Environment variable '{key}' cannot be overridden via the exec env parameter. " +
                    $"{prefix}* variables control the dynamic linker or launcher runtime and may be used for code injection.");
            }
        }

        foreach (var suffix in BlockedEnvSuffixes)
        {
            if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Environment variable '{key}' cannot be overridden via the exec env parameter. " +
                    $"*{suffix} variables can redirect a subprocess's API endpoint to an attacker-controlled host (credential exfiltration).");
            }
        }
    }

    /// <summary>Details metadata returned alongside the tool result (not sent to the LLM).</summary>
    public sealed record ExecToolDetails(int ExitCode, string Termination, int? Pid = null);

    /// <summary>Tracks background processes launched by the exec tool.</summary>
    internal sealed record ProcessInfo(int Pid, string Command, DateTime StartedUtc);
}
