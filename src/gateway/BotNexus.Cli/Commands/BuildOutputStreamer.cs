using System.Diagnostics;
using System.Text.RegularExpressions;
using BotNexus.Domain.Text;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// Streams MSBuild output line-by-line and renders a compact, color-coded
/// progress view instead of dumping raw build logs to the console.
/// </summary>
internal static partial class BuildOutputStreamer
{
    /// <summary>
    /// Exit code returned by an attempt that was aborted because MSBuild reported locked output
    /// files. Deliberately distinct from a normal build failure (1) so <see cref="RunAsync"/> can
    /// tell a lock apart from a real compile error and only spend a second build on the former.
    /// </summary>
    internal const int LockedFilesExitCode = 75;

    /// <summary>
    /// Runs <c>dotnet build</c> with redirected output, parsing and rendering
    /// each line as it arrives so the user sees live progress without raw noise.
    /// When the terminal is interactive and not in verbose mode, wraps the build
    /// in a spinner and renders a summary Table on completion.
    /// </summary>
    /// <remarks>
    /// The first attempt leaves the Roslyn compiler server and MSBuild node reuse ENABLED.
    /// They were previously disabled unconditionally to defend against Windows file locks, but
    /// the update path now stops the gateway and waits for the port to drain before building,
    /// and the locked-file monitor below still aborts the build the instant a lock is observed.
    /// Rather than pay that defence on every build, a lock now TRIGGERS a retry with isolated
    /// compilation. Locked-file detection keeps its full coverage - it changed from an
    /// unconditional tax into a fallback trigger, and the user-facing guidance is still printed
    /// if the isolated retry also hits locks.
    /// </remarks>
    internal static async Task<int> RunAsync(
        string solution,
        string workingDirectory,
        string commitSha,
        bool verbose,
        CancellationToken cancellationToken)
    {
        return await RunWithRetryAsync((isolated, final) => RunAttemptAsync(
            solution, workingDirectory, commitSha, verbose, isolated, final, cancellationToken), cancellationToken);
    }

    internal static async Task<int> RunWithRetryAsync(Func<bool, bool, Task<int>> attempt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exitCode = await attempt(false, false);
        cancellationToken.ThrowIfCancellationRequested();
        if (exitCode != LockedFilesExitCode)
            return exitCode;

        AnsiConsole.MarkupLine("[yellow][[build]][/] File locks detected - retrying with isolated compilation (no compiler server, no node reuse)...");
        var retryCode = await attempt(true, true);
        cancellationToken.ThrowIfCancellationRequested();
        return retryCode == LockedFilesExitCode ? 1 : retryCode;
    }

    internal static Task<int> RunExtensionAsync(
        string projectPath,
        string botNexusRepoRoot,
        string stagingDirectory,
        bool verbose,
        CancellationToken cancellationToken)
        => RunProcessAsync(
            BuildExtensionStartInfo(projectPath, botNexusRepoRoot, stagingDirectory),
            verbose,
            cancellationToken);

    internal static ProcessStartInfo BuildExtensionStartInfo(
        string projectPath,
        string botNexusRepoRoot,
        string stagingDirectory)
    {
        var startInfo = CreateStartInfo(Path.GetDirectoryName(projectPath)
            ?? throw new ArgumentException("The extension project must have a parent directory.", nameof(projectPath)));
        foreach (var argument in new[]
        {
            "build",
            projectPath,
            "-c",
            "Release",
            "--nologo",
            "--tl:off",
            $"/p:BotNexusRepoRoot={botNexusRepoRoot}",
            $"/p:OutputPath={EnsureTrailingSeparator(stagingDirectory)}",
            "/p:AppendTargetFrameworkToOutputPath=false",
            "/p:AppendRuntimeIdentifierToOutputPath=false"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    /// <summary>
    /// Builds the MSBuild argument string for one attempt. Internal so tests can assert the exact
    /// flag set for each attempt without spawning a real build.
    /// </summary>
    /// <remarks>
    /// <c>SourceRevisionId</c> is deliberately NOT passed. Setting it solution-wide changes
    /// generated <c>AssemblyInfo</c> for every project that produces assembly info, which
    /// invalidates their incremental build on every new commit even when no source changed.
    /// The SHA is passed as <c>BotNexusSourceRevisionId</c> instead and only the projects whose
    /// version output is actually consumed (the gateway host and the CLI) opt in to it.
    /// </remarks>
    internal static string BuildArguments(string solution, string commitSha, bool isolatedCompilation)
    {
        var isolation = isolatedCompilation
            ? " /nodeReuse:false /p:UseSharedCompilation=false"
            : string.Empty;

        return $"build \"{solution}\" -c Release --nologo --tl:off{isolation} /p:SkipTests=true /p:SkipCli=true /p:BotNexusSourceRevisionId={commitSha}";
    }

    private static async Task<int> RunAttemptAsync(
        string solution,
        string workingDirectory,
        string commitSha,
        bool verbose,
        bool isolatedCompilation,
        bool isFinalAttempt,
        CancellationToken cancellationToken)
    {
        var psi = CreateStartInfo(workingDirectory);
        psi.Arguments = BuildArguments(solution, commitSha, isolatedCompilation);
        return await RunProcessAsync(psi, verbose, cancellationToken, monitorLocks: true, isFinalAttempt: isFinalAttempt);
    }

    private static ProcessStartInfo CreateStartInfo(string workingDirectory) => new()
    {
        FileName = "dotnet",
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    private static string EnsureTrailingSeparator(string path)
        => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    internal static async Task<int> RunProcessAsync(
        ProcessStartInfo startInfo,
        bool verbose,
        CancellationToken cancellationToken,
        Action<int>? onStarted = null,
        bool monitorLocks = false,
        bool isFinalAttempt = true,
        Func<StreamReader, BuildState, bool, bool, CancellationToken, Task>? readStream = null,
        Func<Process, CancellationToken, Task>? terminateTree = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var interactive = AnsiConsole.Profile.Capabilities.Interactive;
        var state = new BuildState();
        using var lifetime = new CancellationTokenSource();
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start dotnet build.");
        readStream ??= ReadStreamAsync;
        terminateTree ??= BuildProcessTree.TerminateAsync;
        using var cleanup = new CancellationTokenSource();
        Task? treeTermination = null;
        Task TerminateTreeAsync()
        {
            lock (state.Sync)
            {
                if (treeTermination is null)
                {
                    cleanup.CancelAfter(TimeSpan.FromSeconds(10));
                    treeTermination = terminateTree(process, cleanup.Token);
                }
                return treeTermination;
            }
        }
        var stdout = Task.Run(() => readStream(process.StandardOutput, state, verbose, false, lifetime.Token), CancellationToken.None);
        var stderr = Task.Run(() => readStream(process.StandardError, state, verbose, true, lifetime.Token), CancellationToken.None);
        var exit = process.WaitForExitAsync(CancellationToken.None);
        var monitor = MonitorLocksAsync(process, state, monitorLocks, lifetime.Token, TerminateTreeAsync);
        var completion = Task.WhenAll(stdout, stderr, exit);
        try
        {
            onStarted?.Invoke(process.Id);
            // Observe each pump independently: WhenAll alone withholds a pump/render fault
            // until the other pump and live child also finish, preventing cleanup indefinitely.
            var pending = new List<Task> { stdout, stderr, exit, monitor };
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending).WaitAsync(cancellationToken);
                await finished;
                pending.Remove(finished);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            // Capture before cancelling readers: their cancellation can settle inherited pipes
            // without establishing that a descendant exited. Process.Kill(tree) cannot retain
            // ownership after its root exits; fail closed rather than claim ordinary cancellation.
            var rootExitedWithOpenReaders = treeTermination is null && process.HasExited && (!stdout.IsCompleted || !stderr.IsCompleted);
            lifetime.Cancel();
            // Cleanup has its own deadline, independent of the cancelled caller. Do not report
            // cancellation as complete unless the owned root has exited and both pumps settled.
            if (treeTermination is null)
                cleanup.CancelAfter(TimeSpan.FromSeconds(10));
            Exception? killFailure = null;
            try
            {
                if (treeTermination is not null || !process.HasExited)
                    await TerminateTreeAsync();
                else if (!stdout.IsCompleted || !stderr.IsCompleted)
                    rootExitedWithOpenReaders = true;
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                rootExitedWithOpenReaders = true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or AggregateException or NotSupportedException
                or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                killFailure = ex;
                // Capture/confirmation failure must not abandon the still-owned root. This
                // fallback requests termination only; the failure remains explicitly unresolved.
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception fallback) when (fallback is System.ComponentModel.Win32Exception or InvalidOperationException
                    or AggregateException or NotSupportedException)
                {
                    killFailure = new AggregateException(ex, fallback);
                }
            }
            try
            {
                await process.WaitForExitAsync(cleanup.Token);
                try { await completion.WaitAsync(cleanup.Token); }
                catch (Exception) when (completion.IsCompleted)
                {
                    // WhenAll has observed every pump fault/cancellation. Preserve the original
                    // failure below, but only after checking whether cleanup itself is unresolved.
                }
            }
            catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
            {
                throw new IOException("Build cleanup timed out; owned process exit or output-reader termination could not be confirmed.");
            }
            if (killFailure is not null)
                throw new IOException("Build process-tree termination failed; descendant cleanup could not be confirmed.", killFailure);
            if (rootExitedWithOpenReaders)
                throw new IOException("Build root exited with unsettled output readers; descendant cleanup could not be confirmed.");
            throw;
        }
        finally
        {
            lifetime.Cancel();
            try { await monitor; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or AggregateException
                or NotSupportedException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // The main wait observes monitor faults and performs bounded cleanup first.
                // Do not mask its cancellation/cleanup failure while settling this task.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (monitorLocks && state.HasLockedFiles)
        {
            if (isFinalAttempt)
            {
                AnsiConsole.MarkupLine("[red][[build]][/] Build aborted - file locks detected.");
                AnsiConsole.MarkupLine("[yellow][[build]][/] The gateway must be fully stopped before building on Windows.");
                AnsiConsole.MarkupLine("[yellow][[build]][/] Run [dim]botnexus gateway stop[/] and wait a few seconds, then retry.");
            }
            return LockedFilesExitCode;
        }
        RenderSummary(state, process.ExitCode, interactive);
        return process.ExitCode;
    }

    private static async Task MonitorLocksAsync(Process process, BuildState state, bool enabled, CancellationToken cancellationToken,
        Func<Task> terminateTree)
    {
        if (!enabled)
            return;
        try
        {
            while (!process.HasExited)
            {
                if (state.HasLockedFiles)
                {
                    await terminateTree();
                    return;
                }
                await Task.Delay(100, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    internal static async Task ReadStreamAsync(
        StreamReader reader, BuildState state, bool verbose, bool isError, CancellationToken cancellationToken)
    {
        // Bound memory even for a child writing a huge line without newline. Drain the rest of
        // that line rather than accumulating it, and keep each retained/rendered entry finite.
        var buffer = new char[1024];
        var line = new System.Text.StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = buffer[index];
                if (value == '\n')
                {
                    ProcessLine(line.ToString().TrimEnd('\r'), state, verbose, isError);
                    line.Clear();
                }
                // Keep one lookahead code unit so truncation can see a surrogate pair
                // straddling the retention limit, even across separate ReadAsync calls.
                else if (line.Length < MaxLineCharacters + 1)
                    line.Append(value);
            }
        }
        if (line.Length > 0)
            ProcessLine(line.ToString(), state, verbose, isError);
    }

    internal const int MaxLineCharacters = 512;
    internal const int MaxRecentLines = 12;
    internal const int MaxDiagnostics = 100;

    internal static void ProcessLine(string line, BuildState state, bool verbose, bool isError)
    {
        lock (state.Sync)
        {
            ProcessLineCore(line, state, verbose, isError);
        }
    }

    private static void ProcessLineCore(string line, BuildState state, bool verbose, bool isError)
    {
        line = TextTruncation.SafeTruncate(line, MaxLineCharacters) ?? string.Empty;
        var recent = isError ? state.RecentStderr : state.RecentStdout;
        recent.Enqueue((isError ? "stderr: " : "stdout: ") + line);
        while (recent.Count > MaxRecentLines)
            recent.Dequeue();
        if (string.IsNullOrWhiteSpace(line))
            return;

        var trimmed = line.TrimStart();

        // Restore phase
        if (trimmed.StartsWith("Determining projects to restore", StringComparison.Ordinal))
        {
            if (!state.RestoreShown)
            {
                AnsiConsole.MarkupLine("[blue][[build]][/] Restoring packages...");
                state.RestoreShown = true;
            }
            return;
        }

        if (trimmed.StartsWith("All projects are up-to-date for restore", StringComparison.Ordinal)
            || trimmed.StartsWith("Restored ", StringComparison.Ordinal)
            || trimmed.StartsWith("Nothing to do. None of the projects", StringComparison.Ordinal))
        {
            return;
        }

        // Project completed: "  ProjectName -> path/to/output.dll"
        var arrowMatch = ProjectArrowRegex().Match(line);
        if (arrowMatch.Success)
        {
            state.ProjectsBuilt++;
            var projectName = arrowMatch.Groups[1].Value;
            AnsiConsole.MarkupLine($"[blue][[build]][/] [green]\u2713[/] {CliText.SafeDisplay(projectName)}");
            return;
        }

        // Locked file detection: MSB3027 means the gateway is still running and holding DLL locks
        if (trimmed.Contains("MSB3027", StringComparison.Ordinal) || trimmed.Contains("MSB3021", StringComparison.Ordinal))
        {
            if (!state.HasLockedFiles)
            {
                state.HasLockedFiles = true;
                AnsiConsole.MarkupLine("[red][[build]][/] [red]\u2717[/] File locked - the gateway is still running.");
                AnsiConsole.MarkupLine("[yellow][[build]][/] Run [dim]botnexus gateway stop[/] first, then retry.");
            }
            state.ErrorCount++;
            return;
        }

        // Error: "path(line,col): error CS1234: message"
        var errorMatch = ErrorRegex().Match(trimmed);
        if (errorMatch.Success)
        {
            state.ErrorCount++;
            var file = ShortenPath(errorMatch.Groups["file"].Value.Trim());
            var code = errorMatch.Groups["code"].Value;
            var message = errorMatch.Groups["message"].Value;
            state.AddDiagnostic(new DiagnosticEntry("error", code, message, file));
            AnsiConsole.MarkupLine($"[blue][[build]][/] [red]\u2717[/] {CliText.SafeDisplay(file)}: [red]{CliText.SafeDisplay(code.Length == 0 ? "error" : code)}[/] - {CliText.SafeDisplay(message)}");
            return;
        }

        // Warning: "path(line,col): warning CS1234: message"
        var warnMatch = WarningRegex().Match(line);
        if (warnMatch.Success)
        {
            state.WarningCount++;
            var file = ShortenPath(warnMatch.Groups[1].Value);
            var code = warnMatch.Groups[2].Value;
            var message = warnMatch.Groups[3].Value;
            state.AddDiagnostic(new DiagnosticEntry("warning", code, message, file));

            // Show first few warnings inline; suppress the rest to avoid wall of text
            if (state.WarningCount <= 5)
            {
                AnsiConsole.MarkupLine($"[blue][[build]][/] [yellow]\u26A0[/] {CliText.SafeDisplay(file)}: [yellow]{CliText.SafeDisplay(code)}[/] - {CliText.SafeDisplay(message)}");
            }
            else if (state.WarningCount == 6)
            {
                AnsiConsole.MarkupLine("[blue][[build]][/] [dim]Further warnings suppressed (see summary)[/]");
            }
            return;
        }

        // Build time line: "Time Elapsed 00:00:12.34"
        var timeMatch = TimeElapsedRegex().Match(trimmed);
        if (timeMatch.Success)
        {
            state.Elapsed = timeMatch.Groups[1].Value;
            return;
        }

        // MSBuild summary lines we handle ourselves
        if (trimmed.StartsWith("Build succeeded", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Build FAILED", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Warning(s)", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Error(s)", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // In verbose mode, show everything else; otherwise suppress infrastructure noise
        if (verbose)
        {
            var prefix = isError ? "[red]err[/] " : "[dim]   [/] ";
            AnsiConsole.MarkupLine($"[blue][[build]][/] {prefix}{CliText.SafeDisplay(trimmed)}");
        }
    }

    internal static void RenderSummary(BuildState state, int exitCode, bool interactive)
    {
        lock (state.Sync)
        {
            RenderSummaryCore(state, exitCode, interactive);
        }
    }

    private static void RenderSummaryCore(BuildState state, int exitCode, bool interactive)
    {
        AnsiConsole.WriteLine();

        if (exitCode == 0)
        {
            if (interactive)
            {
                // Render a compact summary Table
                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn(new TableColumn("[bold]Result[/]"))
                    .AddColumn(new TableColumn("[bold]Projects[/]") { Alignment = Justify.Right })
                    .AddColumn(new TableColumn("[bold]Warnings[/]") { Alignment = Justify.Right })
                    .AddColumn(new TableColumn("[bold]Time[/]"));

                var warnStr = state.WarningCount > 0 ? $"[yellow]{state.WarningCount}[/]" : $"[dim]{state.WarningCount}[/]";
                table.AddRow(
                    "[green]\u2713 Build succeeded[/]",
                    $"[green]{state.ProjectsBuilt}[/]",
                    warnStr,
                    state.Elapsed is not null ? $"[dim]{CliText.SafeDisplay(state.Elapsed)}[/]" : "[dim]-[/]");
                AnsiConsole.Write(table);
            }
            else
            {
                var parts = new List<string> { $"[green]{state.ProjectsBuilt}[/] project(s)" };
                if (state.WarningCount > 0)
                    parts.Add($"[yellow]{state.WarningCount}[/] warning(s)");
                if (state.Elapsed is not null)
                    parts.Add($"[dim]{CliText.SafeDisplay(state.Elapsed)}[/]");
                AnsiConsole.MarkupLine($"[blue][[build]][/] [green]Build succeeded[/] - {string.Join(", ", parts)}");
            }
        }
        else
        {
            var parts = new List<string> { $"exit code {exitCode}" };
            if (state.ErrorCount > 0)
                parts.Add($"[red]{state.ErrorCount}[/] recognized error(s)");
            if (state.WarningCount > 0)
                parts.Add($"[yellow]{state.WarningCount}[/] warning(s)");
            AnsiConsole.MarkupLine($"[blue][[build]][/] [red]Build FAILED[/] - {string.Join(", ", parts)}");

            if (state.ErrorCount == 0)
            {
                AnsiConsole.MarkupLine("[yellow][[build]][/] No recognized error diagnostic; recent output (bounded):");
                if (state.RecentStdout.Count + state.RecentStderr.Count == 0)
                    AnsiConsole.MarkupLine("[dim]No output was captured from stdout or stderr.[/]");
                foreach (var line in state.RecentOutput)
                    AnsiConsole.MarkupLine($"  {CliText.SafeDisplay(line)}");
            }

            var errors = state.Diagnostics.Where(d => d.Severity == "error").ToList();
            if (errors.Count > 0)
            {
                AnsiConsole.WriteLine();
                foreach (var err in errors)
                    AnsiConsole.MarkupLine($"  [red]\u2717[/] {CliText.SafeDisplay(err.File)}: [red]{CliText.SafeDisplay(err.Code)}[/] - {CliText.SafeDisplay(err.Message)}");
            }
        }

        if (state.WarningCount > 5)
        {
            AnsiConsole.WriteLine();
            var warnings = state.Diagnostics.Where(d => d.Severity == "warning").ToList();
            var grouped = warnings.GroupBy(w => w.Code).OrderByDescending(g => g.Count());
            AnsiConsole.MarkupLine($"[blue][[build]][/] [yellow]Warning summary ({state.WarningCount} total; retained sample):[/]");
            foreach (var group in grouped)
                AnsiConsole.MarkupLine($"  [yellow]{CliText.SafeDisplay(group.Key)}[/] x{group.Count()}: {CliText.SafeDisplay(group.First().Message)}");
        }
    }

    /// <summary>
    /// Strips common path prefixes so diagnostics show relative paths.
    /// </summary>
    private static string ShortenPath(string fullPath)
    {
        // Remove drive + common repo roots for readability
        var cwd = Directory.GetCurrentDirectory();
        if (fullPath.StartsWith(cwd, StringComparison.OrdinalIgnoreCase))
            return fullPath[cwd.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return fullPath;
    }

    // Matches: "  ProjectName -> path/to/output.dll"
    [GeneratedRegex(@"^\s+(\S+)\s+->\s+")]
    private static partial Regex ProjectArrowRegex();

    // Optional source location and optional letter/digit diagnostic code. A word such as
    // "Value" is part of a code-less message, never a diagnostic code.
    [GeneratedRegex(@"^(?:(?<file>.+?(?:\(\d+(?:,\d+)?\))?)\s*:\s*)?error\s+(?:(?<code>[A-Za-z]+[0-9]+)\s*:\s*|:\s*|(?=\S))(?<message>.+)$")]
    private static partial Regex ErrorRegex();

    // Matches: "path(line,col): warning CODE: message"
    [GeneratedRegex(@"^(.+?)\(\d+(?:,\d+)?\):\s+warning\s+(\w+)\s*:\s*(.+)$")]
    private static partial Regex WarningRegex();

    // Matches: "Time Elapsed 00:00:12.34"
    [GeneratedRegex(@"Time Elapsed\s+(\S+)")]
    private static partial Regex TimeElapsedRegex();

    internal sealed class BuildState
    {
        public int ProjectsBuilt;
        public int WarningCount;
        public int ErrorCount;
        private int _hasLockedFiles;
        public bool HasLockedFiles
        {
            get => Volatile.Read(ref _hasLockedFiles) != 0;
            set => Volatile.Write(ref _hasLockedFiles, value ? 1 : 0);
        }
        internal object Sync { get; } = new();
        internal Queue<string> RecentStdout { get; } = new();
        internal Queue<string> RecentStderr { get; } = new();
        internal IReadOnlyList<string> RecentOutput => RecentStdout.Concat(RecentStderr).ToArray();
        internal void AddDiagnostic(DiagnosticEntry diagnostic)
        {
            if (Diagnostics.Count < MaxDiagnostics)
                Diagnostics.Add(diagnostic);
        }
        public string? Elapsed;
        public bool RestoreShown;
        public List<DiagnosticEntry> Diagnostics { get; } = [];
    }

    internal sealed record DiagnosticEntry(string Severity, string Code, string Message, string File);
}
