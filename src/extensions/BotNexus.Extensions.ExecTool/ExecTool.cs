using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    /// Retention cap on captured child output, in bytes. Exposed internally so tests can drive a
    /// child over the cap and compute the expected overage from the same constant the collector uses,
    /// rather than hard-coding a number that would silently stop matching if the cap changed.
    /// </summary>
    internal static int MaxOutputBytesForTest => MaxOutputBytes;

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

    /// <summary>
    /// Creates the tool bound to an agent workspace. <paramref name="workingDirectory"/> deliberately
    /// has NO default value: that makes the constructor non-auto-resolvable, so the extension loader
    /// skips registering this tool as a bare DI singleton and it can only reach an agent through
    /// <see cref="ExecToolContributor"/>, which supplies the session workspace. Before #2416 the
    /// optional parameter allowed a workspace-less singleton whose children inherited the gateway
    /// process's current directory, diverging from the shell tool. Pass <see langword="null"/>
    /// explicitly to opt into process-relative resolution (tests and standalone hosts).
    /// </summary>
    /// <param name="workingDirectory">The agent workspace, or null for process-relative resolution.</param>
    /// <param name="fileSystem">File system used for Windows .cmd/.bat resolution.</param>
    public ExecTool(string? workingDirectory, IFileSystem? fileSystem = null)
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

    /// <summary>
    /// The per-call <c>timeoutMs</c> argument is milliseconds. Declared explicitly because the
    /// executor no longer infers a unit from the argument name (issue #2955).
    /// </summary>
    public ToolTimeoutArgument? TimeoutArgument => new("timeoutMs", ToolTimeoutUnit.Milliseconds);

    /// <inheritdoc />
    public string Label => "Exec";

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Execute a command with advanced process management: timeouts, background mode, stdin piping, and environment variable merging. " +
        "Commands run in the agent workspace by default - the same directory the shell tool uses - so workspace-relative " +
        "paths such as 'tmp/q.py' resolve correctly; pass workingDir to run elsewhere. " +
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
                  "description": "Working directory override. Defaults to the agent workspace; a relative value resolves against it."
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

        // #2407: reject escaped-newline shell words before anything is spawned.
        foreach (var segment in command)
        {
            ValidateCommandText(segment);
        }

        ValidateCommandText(string.Join(' ', command));

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

        // #2416: resolve a relative workingDir against the agent workspace rather than the gateway
        // process's current directory. Path.GetFullPath(relative) alone silently rebased onto the host
        // process directory (the user profile on Windows), which is the same divergence from `shell`
        // that broke the documented "write tmp/q.py then run it" recipe. A rooted path is unaffected,
        // and with no workspace configured the previous process-relative behaviour is preserved.
        var workingDir = ReadOptionalString(arguments, "workingDir");
        if (!string.IsNullOrWhiteSpace(workingDir))
        {
            workingDir = _workingDirectory is not null
                ? Path.GetFullPath(workingDir, _workingDirectory)
                : Path.GetFullPath(workingDir);
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

        // Same guard for inline `python -c` one-liners (issue #2417): unterminated string literals
        // and unbalanced brackets are rejected before the interpreter is spawned.
        if (PythonPreflight.IsPythonExecutable(command[0])
            && PythonPreflight.TryGetInlineScript(processArgs, inlineScript: null, out var inlinePyScript))
        {
            PythonPreflight.ThrowIfInvalid(inlinePyScript);
        }
        // Same guard for inline `node -e` one-liners (issue #2762): unterminated string/template
        // literals and unbalanced brackets are rejected before the runtime is spawned.
        if (NodePreflight.IsNodeExecutable(command[0])
            && NodePreflight.TryGetInlineScript(processArgs, inlineScript: null, out var inlineJsScript))
        {
            NodePreflight.ThrowIfInvalid(inlineJsScript);
        }

        // File-based `pwsh -File <path>` invocations (issue #2758): pwsh reports a missing script as
        // an ARGUMENT-parsing error plus its generic usage banner, naming neither the skill nor any
        // candidate. Diagnose it here instead - name the skill and the closest existing wrapper names
        // enumerated from the skill's scripts/ directory. A near match is reported, never substituted.
        if (SkillScriptPreflight.TryGetFileTarget(processArgs, out var scriptTarget))
        {
            var probeRoot = workingDir ?? _workingDirectory;
            var resolvedTarget = Path.IsPathRooted(scriptTarget) || string.IsNullOrWhiteSpace(probeRoot)
                ? scriptTarget
                : Path.GetFullPath(scriptTarget, probeRoot);
            SkillScriptPreflight.ThrowIfMissing(resolvedTarget);
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
            // Route through the shared merge seam so an override replaces - rather than
            // duplicates - an inherited variable whose key differs only by case on Windows (#2892).
            ProcessEnvironment.Merge(startInfo.Environment, env);
        }

        // Re-check cancellation immediately before Start(). Everything above - command resolution,
        // PowerShell preflight, working-directory resolution and environment merging - can take arbitrary
        // time, so a token cancelled during that window must not be allowed to spawn a child at all.
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start process.");
        }

        StartedTestHook?.Invoke(process);

        // Cancellation observed after Start() - the child is live. Kill the entire process tree via the
        // existing TryKill path and propagate; the process is never registered in BackgroundProcesses, so
        // it cannot outlive its turn or count against MaxBackgroundProcesses.
        if (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new OperationCanceledException(cancellationToken);
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
        var discardedBytes = 0;
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
                else
                {
                    // Count what the cap costs us. Without this the discarded volume is unknowable:
                    // the dropped lines are never buffered, so nothing downstream can reconstruct it.
                    discardedBytes += lineBytes;
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
                if (discardedBytes > 0)
                {
                    output = $"{FormatTruncationBanner(totalBytes, discardedBytes)}\n{output}";
                }
            }

            var exitCode = ResolveExitCode(TryGetProcessExitCode(process));

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
    /// Test-only seam invoked immediately after the OS process is started and before the post-start
    /// cancellation check. Lets tests deterministically exercise the "cancelled after start" branch
    /// and observe the resulting PID. Always null in production.
    /// </summary>
    internal static Action<Process>? StartedTestHook { get; set; }

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
    /// Leading token every truncation banner starts with. Tests and callers match on this rather
    /// than on the full sentence so the wording can evolve without becoming unrecognisable.
    /// </summary>
    internal const string TruncationBannerPrefix = "[output truncated:";

    /// <summary>
    /// Renders the retention-cap banner for issue #2895.
    /// </summary>
    /// <remarks>
    /// The old banner restated the compile-time cap (<c>[output truncated at 100KB]</c>) and so
    /// answered a question nobody asked: the cap is fixed and knowable, whereas the LOSS is not.
    /// A caller deciding whether to re-run with a narrower command needs to know how much went
    /// missing and which end of the stream survived - one dropped line and fifty dropped megabytes
    /// warrant very different responses, and the old wording was identical for both.
    /// </remarks>
    /// <param name="retainedBytes">Bytes actually kept in the returned output.</param>
    /// <param name="discardedBytes">Bytes produced by the child but dropped once the cap was hit.</param>
    internal static string FormatTruncationBanner(int retainedBytes, int discardedBytes)
    {
        var produced = (long)retainedBytes + discardedBytes;

        // Collection is head-first: lines are appended until one no longer fits, after which every
        // subsequent line is dropped. The surviving portion is therefore always the head.
        return $"{TruncationBannerPrefix} retained {retainedBytes} bytes (head) of {produced} bytes produced, " +
               $"discarded {discardedBytes} bytes (tail) at the {MaxOutputBytes / 1024}KB cap]";
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

    /// <summary>
    /// Reads an optional string-valued map argument (<c>env</c>) regardless of how the tool pipeline
    /// delivered it. The previous implementation accepted only a
    /// <see cref="IReadOnlyDictionary{TKey,TValue}"/> of <c>string</c> values or a whole
    /// <see cref="JsonElement"/> object, but deserialization commonly yields
    /// <c>Dictionary&lt;string, object?&gt;</c> whose values are boxed scalars or per-value
    /// <see cref="JsonElement"/>s. The verbatim payload from issue #2415 - <c>{"PYTHONUTF8":"1"}</c>,
    /// an object with string values - was therefore rejected by a message stating the exact
    /// requirement the payload already satisfied, which sends the model into blind retries.
    /// <para>
    /// Widening the accepted shapes is not "anything goes": a value with no unambiguous
    /// environment-variable string form (a nested object or an array) is still rejected, and the
    /// rejection names the offending key so the model can fix the one entry at fault.
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ReadOptionalStringDictionary(
        IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        switch (value)
        {
            case IReadOnlyDictionary<string, string> dict:
                return dict;

            case JsonElement { ValueKind: JsonValueKind.Object } element:
                return element.EnumerateObject()
                    .ToDictionary(p => p.Name, p => ConvertEnvValue(key, p.Name, p.Value));

            // The shape the tool pipeline actually delivers after deserialization.
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                return pairs.ToDictionary(p => p.Key, p => ConvertEnvValue(key, p.Key, p.Value));

            default:
                throw new ArgumentException(
                    $"Argument '{key}' must be an object mapping names to string values. " +
                    $"Received {DescribeArgumentShape(value)}; expected an object such as " +
                    "{\"PYTHONUTF8\": \"1\"}.");
        }
    }

    /// <summary>
    /// Converts one map entry to its environment-variable string form. Scalars have an unambiguous
    /// string form and are accepted (environment variables are strings by definition); JSON null
    /// becomes an empty string, preserving the prior behaviour of <c>GetString()</c> on a null
    /// element. Objects and arrays have no such form and are rejected with the offending key named.
    /// </summary>
    private static string ConvertEnvValue(string argumentKey, string entryKey, object? entryValue)
    {
        switch (entryValue)
        {
            case null:
                return string.Empty;

            case string text:
                return text;

            case JsonElement element:
                return element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString() ?? string.Empty,
                    JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
                    _ => throw new ArgumentException(
                        $"Argument '{argumentKey}' entry '{entryKey}' must be a string or a scalar with an " +
                        $"unambiguous string form. Received a JSON {element.ValueKind.ToString().ToLowerInvariant()}; " +
                        "expected a string such as \"1\". Nested objects and arrays are not valid environment " +
                        "variable values - flatten the value or serialize it yourself.")
                };

            case bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                return Convert.ToString(entryValue, CultureInfo.InvariantCulture) ?? string.Empty;

            default:
                throw new ArgumentException(
                    $"Argument '{argumentKey}' entry '{entryKey}' must be a string or a scalar with an " +
                    $"unambiguous string form. Received {DescribeArgumentShape(entryValue)}; expected a string " +
                    "such as \"1\". Nested objects and arrays are not valid environment variable values - " +
                    "flatten the value or serialize it yourself.");
        }
    }

    /// <summary>
    /// Renders what the caller actually sent so a rejection diagnoses rather than merely asserts.
    /// #2415's core complaint was messages that restated a requirement without saying what was
    /// received, leaving the model no signal about what to change.
    /// </summary>
    private static string DescribeArgumentShape(object value)
        => value switch
        {
            JsonElement element => $"a JSON {element.ValueKind.ToString().ToLowerInvariant()}",
            string text => $"a string '{text}'",
            _ => $"a {value.GetType().Name}"
        };

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
    /// <item><c>BASH_FUNC_*</c> - bash exported-function definitions (shellshock-style injection into any child bash)</item>
    /// <item><c>CC</c>, <c>CXX</c>, <c>CPP</c>, <c>CXXCPP</c>, <c>LD</c>, <c>AR</c> - compiler/preprocessor/linker selectors; an override substitutes an attacker-chosen binary into any build the child runs</item>
    /// <item><c>*_BASE_URL</c>, <c>*_API_HOST</c>, <c>*_ENDPOINT</c> — endpoint-redirection variables that can point a subprocess's API calls at an attacker-controlled host (credential exfiltration)</item>
    /// </list>
    /// </summary>
    public static readonly string[] BlockedEnvPrefixes = ["LD_", "DYLD_", "CLOUDSDK_", "BASH_FUNC_", "AWS_", "BOTNEXUS_"];
    public static readonly string[] BlockedEnvExact = ["PATH", "PATHEXT", "COMSPEC", "SYSTEMROOT", "CC", "CXX", "CPP", "CXXCPP", "LD", "AR"];
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

        // #2407: token-sequence matching. Prefix/suffix rules cannot express "turn the safety off"
        // names such as FOO_DISABLE_TLS_VERIFY or CLIENT_SKIP_EXTRA_AUTH, because the dangerous part
        // sits in the middle. We split on '_' and look for an ORDERED subsequence of whole tokens, so
        // AUTH_DISABLE_MODE (wrong order) and DISABLED_AUTHORITY (substring, not a token) stay legal.
        var tokens = key.Split('_', StringSplitOptions.RemoveEmptyEntries);
        foreach (var sequence in BlockedEnvTokenSequences)
        {
            if (ContainsTokenSequence(tokens, sequence))
            {
                throw new ArgumentException(
                    $"Environment variable '{key}' cannot be overridden via the exec env parameter. " +
                    $"Names containing the token sequence '{string.Join("_", sequence)}' disable authentication, " +
                    "certificate, signature or transport-security checks in the child process.");
            }
        }
    }

    /// <summary>
    /// Ordered whole-token subsequences that mark an environment variable as a safety-disabling
    /// switch. A key is rejected when its '_'-separated tokens contain one of these sequences in
    /// order (case-insensitive); intervening tokens are permitted, reordering is not.
    /// </summary>
    public static readonly string[][] BlockedEnvTokenSequences =
    [
        ["DANGEROUSLY"],
        ["DISABLE", "AUTH"],
        ["DISABLE", "CERT"],
        ["DISABLE", "SIGNATURE"],
        ["DISABLE", "SSL"],
        ["DISABLE", "TLS"],
        ["SKIP", "AUTH"],
    ];

    private static bool ContainsTokenSequence(string[] tokens, string[] sequence)
    {
        var next = 0;
        foreach (var token in tokens)
        {
            if (string.Equals(token, sequence[next], StringComparison.OrdinalIgnoreCase))
            {
                next++;
                if (next == sequence.Length)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Transparent dispatch carriers: programs whose entire job is to launch another program with
    /// modified process attributes. A policy keyed on argv[0] sees the carrier, not the payload, so
    /// <c>proxychains curl ...</c> would present as "proxychains" while running curl. Resolving
    /// through this table first is what makes any future allowlist (see #2391) meaningful.
    /// </summary>
    public static readonly string[] DispatchWrappers =
    [
        "sudo", "nohup", "setsid", "nice", "ionice", "time", "timeout", "env", "stdbuf",
        "catchsegv", "linux32", "linux64", "numactl", "proxychains", "proxychains4",
        "setarch", "torify", "torsocks", "unbuffer", "xargs",
    ];

    /// <summary>Short options of the wrapper table that consume the following token as their value.</summary>
    private static readonly string[] WrapperValueOptions = ["-u", "-g", "-p", "-n", "-c", "-s", "-k", "-i", "-o", "-e", "-C"];

    private static readonly Regex DurationLikeArgument = new(@"^\d+(\.\d+)?[smhd]?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Word-boundary escaped newline. The prior guard only caught a line continuation at program
    /// level; this catches a backslash immediately after a newline anywhere in the command text,
    /// which splices a following word into the previous one and hides the real payload.
    /// </summary>
    private static readonly Regex EscapedNewlineWord = new(@"(?:\r\n|[\r\n])\\", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns the effective executable for <paramref name="command"/> by peeling off any
    /// transparent dispatch carriers (see <see cref="DispatchWrappers"/>) and their options, so a
    /// caller reasoning about "what will actually run" is not fooled by a carrier prefix.
    /// Pure and side-effect free; the returned value is the program's base name with any Windows
    /// executable extension removed, in its original casing.
    /// </summary>
    /// <param name="command">Full command text, e.g. <c>timeout 30s proxychains4 curl https://x</c>.</param>
    /// <returns>The unwrapped program name, or an empty string when there is no program.</returns>
    public static string ResolveEffectiveExecutable(string command)
    {
        var tokens = TokenizeCommand(command);
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        var index = 0;
        while (index < tokens.Count)
        {
            var program = NormalizeProgramName(tokens[index]);
            if (!DispatchWrappers.Contains(program, StringComparer.OrdinalIgnoreCase))
            {
                return program;
            }

            // The carrier itself is the answer when it wraps nothing.
            var wrapperIndex = index;
            index++;
            while (index < tokens.Count && IsWrapperArgument(tokens[index]))
            {
                if (tokens[index].StartsWith('-') && WrapperValueOptions.Contains(tokens[index], StringComparer.Ordinal))
                {
                    index++;
                }

                index++;
            }

            if (index >= tokens.Count)
            {
                return NormalizeProgramName(tokens[wrapperIndex]);
            }
        }

        return string.Empty;
    }

    private static bool IsWrapperArgument(string token)
        => token.StartsWith('-') || token.Contains('=', StringComparison.Ordinal) || DurationLikeArgument.IsMatch(token);

    private static string NormalizeProgramName(string token)
    {
        var name = token.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        foreach (var extension in new[] { ".exe", ".cmd", ".bat", ".com" })
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^extension.Length];
            }
        }

        return name;
    }

    private static List<string> TokenizeCommand(string command)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(command))
        {
            return tokens;
        }

        var current = new StringBuilder();
        var quote = '\0';
        foreach (var c in command)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>
    /// Rejects command text containing an escaped newline at a word boundary (#2407). Such a splice
    /// lets a reviewer or a policy check see one word while the shell executes another.
    /// </summary>
    /// <exception cref="ArgumentException">The command contains a newline followed by a backslash.</exception>
    public static void ValidateCommandText(string command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return;
        }

        if (EscapedNewlineWord.IsMatch(command))
        {
            throw new ArgumentException(
                "Command contains an escaped newline (a backslash immediately following a line break). " +
                "This splices words together and hides the executed program from review. " +
                "Put the command on a single line, or write a script file and execute it.");
        }
    }

    /// <summary>
    /// Sentinel reported in <see cref="ExecToolDetails.ExitCode"/> when the operating system never
    /// produced a numeric status for the child (for example the process was still running when we
    /// gave up on it, so <see cref="Process.ExitCode"/> is not readable). It is deliberately a single
    /// value for every such case: the reason a run ended is carried by
    /// <see cref="ExecToolDetails.Termination"/>, never encoded into the number.
    /// </summary>
    internal const int UnknownExitCode = -1;

    /// <summary>
    /// Maps the operating system's view of a finished child process onto the numeric status reported
    /// to callers. Mirrors the upstream <c>resolveSubprocessExitCode</c> contract: whenever the OS gave
    /// us a real status it wins, including the POSIX <c>128 + signum</c> values .NET surfaces on Linux
    /// for signal deaths (137 = SIGKILL, e.g. the OOM killer). This matters because a timeout kill and
    /// an OOM kill are different incidents, and collapsing both to a sentinel destroys that evidence.
    /// The termination reason is intentionally NOT an input here - it stays a separate field so a
    /// consumer can correlate the two rather than reverse-engineer one from the other.
    /// </summary>
    /// <param name="processExitCode">
    /// The status read from the child, or <see langword="null"/> when none was available.
    /// </param>
    /// <returns>The real status when known, otherwise <see cref="UnknownExitCode"/>.</returns>
    internal static int ResolveExitCode(int? processExitCode) => processExitCode ?? UnknownExitCode;

    /// <summary>
    /// Reads <see cref="Process.ExitCode"/> defensively. After a timeout or cancellation we kill the
    /// child and then race it: the kill may not have been reaped yet, in which case the property throws
    /// <see cref="InvalidOperationException"/>. Returning <see langword="null"/> keeps that race off the
    /// caller's crash path while still preserving the status in the common case where the child has
    /// already gone (which is where the signal-derived <c>128 + signum</c> codes live).
    /// Exposed internally so the throwing path is directly testable.
    /// </summary>
    /// <returns>The child's status, or <see langword="null"/> if it is not readable yet.</returns>
    internal static int? TryGetProcessExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            // Process was never started, or was already disposed/detached - no status to report.
            return null;
        }
        catch (SystemException)
        {
            // Platform-level failure reading the status (e.g. Win32Exception on a handle we lost).
            return null;
        }
    }

    /// <summary>Details metadata returned alongside the tool result (not sent to the LLM).</summary>
    /// <param name="ExitCode">
    /// The child's real operating-system status when one was available, preserved verbatim - including
    /// POSIX <c>128 + signum</c> signal deaths such as 137 (SIGKILL). Only when no status could be read
    /// at all is <see cref="UnknownExitCode"/> (-1) reported. Never infer why a run ended from this
    /// number; use <paramref name="Termination"/>.
    /// </param>
    /// <param name="Termination">
    /// The authoritative classifier for how the run ended: <c>exit</c>, <c>timeout</c>,
    /// <c>no-output-timeout</c>, or <c>cancelled</c>. Independent of <paramref name="ExitCode"/>.
    /// </param>
    /// <param name="Pid">The child's process id for background launches, otherwise <see langword="null"/>.</param>
    public sealed record ExecToolDetails(int ExitCode, string Termination, int? Pid = null);

    /// <summary>Tracks background processes launched by the exec tool.</summary>
    internal sealed record ProcessInfo(int Pid, string Command, DateTime StartedUtc);
}
