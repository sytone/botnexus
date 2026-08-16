using System.Diagnostics;
using System.Text.RegularExpressions;
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
        var exitCode = await RunAttemptAsync(
            solution, workingDirectory, commitSha, verbose,
            isolatedCompilation: false, isFinalAttempt: false, cancellationToken);

        if (exitCode != LockedFilesExitCode)
            return exitCode;

        AnsiConsole.MarkupLine("[yellow][[build]][/] File locks detected - retrying with isolated compilation (no compiler server, no node reuse)...");

        var retryCode = await RunAttemptAsync(
            solution, workingDirectory, commitSha, verbose,
            isolatedCompilation: true, isFinalAttempt: true, cancellationToken);

        // Never leak the internal sentinel to callers; a lock that survives the isolated retry is
        // a plain build failure as far as the update/serve pipeline is concerned.
        return retryCode == LockedFilesExitCode ? 1 : retryCode;
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
        var interactive = AnsiConsole.Profile.Capabilities.Interactive;

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = BuildArguments(solution, commitSha, isolatedCompilation),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // When interactive and not verbose, suppress per-line output and show a brief message instead.
        var suppressLive = interactive && !verbose;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet build.");

        var state = new BuildState();

        if (suppressLive)
        {
            AnsiConsole.MarkupLine("[dim]  Building...[/]");
        }

        var stdoutTask = ReadStreamAsync(process.StandardOutput, state, suppressLive ? false : verbose, isError: false);
        var stderrTask = ReadStreamAsync(process.StandardError, state, suppressLive ? false : verbose, isError: true);

        // Monitor for locked files and kill the build immediately if detected.
        // MSBuild retries 10 times per file - without this we'd wait minutes.
        var lockedFileMonitor = Task.Run(async () =>
        {
            while (!process.HasExited)
            {
                if (state.HasLockedFiles)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return;
                }
                await Task.Delay(100, CancellationToken.None);
            }
        }, CancellationToken.None);

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(cancellationToken);

        if (state.HasLockedFiles)
        {
            if (isFinalAttempt)
            {
                AnsiConsole.MarkupLine("[red][[build]][/] [red]\u2717[/] Build aborted - file locks detected.");
                AnsiConsole.MarkupLine("[yellow][[build]][/] The gateway must be fully stopped before building on Windows.");
                AnsiConsole.MarkupLine("[yellow][[build]][/] Run [dim]botnexus gateway stop[/] and wait a few seconds, then retry.");
            }

            return LockedFilesExitCode;
        }

        RenderSummary(state, process.ExitCode, interactive);
        return process.ExitCode;
    }

    private static async Task ReadStreamAsync(
        StreamReader reader,
        BuildState state,
        bool verbose,
        bool isError)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            ProcessLine(line, state, verbose, isError);
        }
    }

    internal static void ProcessLine(string line, BuildState state, bool verbose, bool isError)
    {
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
        var errorMatch = ErrorRegex().Match(line);
        if (errorMatch.Success)
        {
            state.ErrorCount++;
            var file = ShortenPath(errorMatch.Groups[1].Value);
            var code = errorMatch.Groups[2].Value;
            var message = errorMatch.Groups[3].Value;
            state.Diagnostics.Add(new DiagnosticEntry("error", code, message, file));
            AnsiConsole.MarkupLine($"[blue][[build]][/] [red]\u2717[/] {CliText.SafeDisplay(file)}: [red]{CliText.SafeDisplay(code)}[/] - {CliText.SafeDisplay(message)}");
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
            state.Diagnostics.Add(new DiagnosticEntry("warning", code, message, file));

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

    private static void RenderSummary(BuildState state, int exitCode, bool interactive)
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
            var parts = new List<string> { $"[red]{state.ErrorCount}[/] error(s)" };
            if (state.WarningCount > 0)
                parts.Add($"[yellow]{state.WarningCount}[/] warning(s)");
            AnsiConsole.MarkupLine($"[blue][[build]][/] [red]Build FAILED[/] - {string.Join(", ", parts)}");

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
            AnsiConsole.MarkupLine($"[blue][[build]][/] [yellow]Warning summary ({state.WarningCount} total):[/]");
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

    // Matches: "path(line,col): error CODE: message"
    [GeneratedRegex(@"^(.+?)\(\d+,\d+\):\s+error\s+(\w+)\s*:\s*(.+)$")]
    private static partial Regex ErrorRegex();

    // Matches: "path(line,col): warning CODE: message"
    [GeneratedRegex(@"^(.+?)\(\d+,\d+\):\s+warning\s+(\w+)\s*:\s*(.+)$")]
    private static partial Regex WarningRegex();

    // Matches: "Time Elapsed 00:00:12.34"
    [GeneratedRegex(@"Time Elapsed\s+(\S+)")]
    private static partial Regex TimeElapsedRegex();

    internal sealed class BuildState
    {
        public int ProjectsBuilt;
        public int WarningCount;
        public int ErrorCount;
        public bool HasLockedFiles;
        public string? Elapsed;
        public bool RestoreShown;
        public List<DiagnosticEntry> Diagnostics { get; } = [];
    }

    internal sealed record DiagnosticEntry(string Severity, string Code, string Message, string File);
}
