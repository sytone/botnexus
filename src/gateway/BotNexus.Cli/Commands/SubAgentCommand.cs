using System.CommandLine;
using System.IO.Abstractions;
using Microsoft.Data.Sqlite;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// <c>botnexus subagent workspace</c> command group: on-demand inspection and reclamation of the
/// sub-agent workspace directories that accumulate under the OS temp root on a long-lived gateway
/// (issue #1942). Sub-agent workspaces are created eagerly at spawn but are only reclaimed when the
/// owning gateway process runs its in-memory cleanup; a gateway that spawns many sub-agents (or is
/// restarted before cleanup) leaves the bulky workspace files behind forever.
/// <para>
/// This command reconciles the physical directories against the persisted <c>sub_agent_sessions</c>
/// status rows and reclaims only the directories whose sub-agent is in a terminal state
/// (completed/failed/killed/timed-out) or whose record no longer exists. A still-running sub-agent's
/// workspace is never pruned - the safe seam mandated by WORLD.md ("never touch the running
/// gateway's live files"). The persisted sub-agent record/transcript is retained; only the workspace
/// files are reclaimed.
/// </para>
/// </summary>
internal sealed class SubAgentCommand
{
    internal const string SubAgentWorkspaceDirectoryName = "botnexus-subagent-workspaces";

    private readonly IFileSystem _fileSystem;

    public SubAgentCommand()
        : this(new FileSystem())
    {
    }

    // Test seam: inject a MockFileSystem so the list/prune paths can be exercised without touching
    // the real temp directory.
    internal SubAgentCommand(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public Command Build(Option<string?> targetOption)
    {
        var command = new Command("subagent", "Inspect and maintain sub-agent runtime artifacts.");
        command.AddAlias("subagents");

        var workspaceCommand = new Command("workspace", "List and prune stale sub-agent workspace directories.");
        workspaceCommand.AddAlias("workspaces");

        // ── list ──
        var listCommand = new Command("list", "List sub-agent workspace directories and whether each is prunable.");
        listCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            context.ExitCode = ExecuteList(ResolveSessionsDb(target), ResolveWorkspaceRoot());
            return Task.CompletedTask;
        });

        // ── prune ──
        var dryRunOption = new Option<bool>("--dry-run", "List what would be pruned without deleting anything.");
        var pruneCommand = new Command("prune", "Delete workspace directories for terminal (completed/failed/timed-out) sub-agents.")
        {
            dryRunOption
        };
        pruneCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            context.ExitCode = ExecutePrune(ResolveSessionsDb(target), ResolveWorkspaceRoot(), dryRun);
            return Task.CompletedTask;
        });

        workspaceCommand.AddCommand(listCommand);
        workspaceCommand.AddCommand(pruneCommand);
        command.AddCommand(workspaceCommand);
        return command;
    }

    /// <summary>
    /// Resolves the sessions database path from the (optional) global <c>--target</c> home override,
    /// mirroring <see cref="DebugSessionsCommand.ResolveSessionsDb"/>.
    /// </summary>
    internal static string ResolveSessionsDb(string? target)
        => Path.Combine(CliPaths.ResolveTarget(target), "sessions.db");

    /// <summary>
    /// Resolves the OS temp sub-agent workspace root, kept in lock-step with
    /// <c>FileAgentWorkspaceManager.GetSubAgentWorkspaceRoot</c> so the CLI reaper targets exactly the
    /// directories the gateway creates.
    /// </summary>
    internal string ResolveWorkspaceRoot()
        => _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), SubAgentWorkspaceDirectoryName);

    /// <summary>
    /// Lists every workspace directory found under the root, tagged with its disposition. Prints a
    /// helpful message (and still succeeds) when there is nothing to show. Returns 0 on success.
    /// </summary>
    internal int ExecuteList(string sessionsDbPath, string workspaceRoot)
    {
        var reaper = new SubAgentWorkspaceReaper(_fileSystem);
        var statuses = LoadStatusesByAgentDirectory(sessionsDbPath);
        var plan = reaper.BuildPlan(workspaceRoot, statuses);

        if (plan.Count == 0)
        {
            AnsiConsole.MarkupLine($"[green]No sub-agent workspaces found under[/] [dim]{Markup.Escape(workspaceRoot)}[/].");
            return 0;
        }

        var table = new Table()
            .AddColumn("Workspace")
            .AddColumn("Status")
            .AddColumn("Disposition");

        foreach (var entry in plan)
        {
            table.AddRow(
                Markup.Escape(entry.AgentDirectoryName),
                Markup.Escape(entry.Status ?? "(no record)"),
                RenderDisposition(entry.Disposition));
        }

        AnsiConsole.Write(table);

        var prunable = plan.Count(entry => entry.IsPrunable);
        AnsiConsole.MarkupLine(
            $"\n{plan.Count} workspace(s): [green]{prunable} prunable[/], [yellow]{plan.Count - prunable} running (retained)[/].");
        return 0;
    }

    /// <summary>
    /// Prunes (or, under <paramref name="dryRun"/>, previews) the prunable workspace directories.
    /// Returns 0 on success.
    /// </summary>
    internal int ExecutePrune(string sessionsDbPath, string workspaceRoot, bool dryRun)
    {
        var reaper = new SubAgentWorkspaceReaper(_fileSystem);
        var statuses = LoadStatusesByAgentDirectory(sessionsDbPath);
        var plan = reaper.BuildPlan(workspaceRoot, statuses);

        var prunable = plan.Where(entry => entry.IsPrunable).ToList();
        if (prunable.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Nothing to prune - no terminal or orphaned sub-agent workspaces.[/]");
            return 0;
        }

        foreach (var entry in prunable)
        {
            var verb = dryRun ? "[dim]would delete[/]" : "[red]deleting[/]";
            AnsiConsole.MarkupLine($"  {verb} [dim]{Markup.Escape(entry.AgentDirectoryName)}[/] ({RenderDisposition(entry.Disposition)})");
        }

        var count = reaper.Prune(plan, dryRun);

        var retained = plan.Count - prunable.Count;
        if (dryRun)
        {
            AnsiConsole.MarkupLine(
                $"\n[yellow]Dry run:[/] {count} workspace(s) would be pruned; {retained} running workspace(s) retained. No files were deleted.");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"\n[green]\u2713[/] Pruned {count} workspace(s); {retained} running workspace(s) retained.");
        }

        return 0;
    }

    private static string RenderDisposition(SubAgentWorkspaceDisposition disposition)
        => disposition switch
        {
            SubAgentWorkspaceDisposition.Terminal => "[green]terminal[/]",
            SubAgentWorkspaceDisposition.Orphan => "[grey]orphan[/]",
            _ => "[yellow]running[/]"
        };

    /// <summary>
    /// Reads the persisted <c>sub_agent_sessions</c> rows and folds them into a
    /// sanitized-child-agent-directory -> status map. A missing database is not an error (the
    /// gateway may never have run): an empty map is returned so every on-disk directory is treated as
    /// an orphan and therefore prunable. Opened read-only so the running gateway is never disturbed.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> LoadStatusesByAgentDirectory(string sessionsDbPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(sessionsDbPath))
            return map;

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = sessionsDbPath,
            Mode = SqliteOpenMode.ReadOnly
        };

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT child_agent_id, status FROM sub_agent_sessions";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
                continue;

            var childAgentId = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(childAgentId))
                continue;

            var status = reader.IsDBNull(1) ? "Active" : reader.GetString(1);
            var dirName = SubAgentWorkspaceReaper.SanitizeAgentDirectoryName(childAgentId);
            // Last write wins if two rows share a sanitized directory name (practically impossible -
            // child agent ids embed a GUID); a terminal status is the more useful one to surface, so
            // prefer it over an Active duplicate.
            if (!map.TryGetValue(dirName, out var existing) ||
                string.Equals(existing, "Active", StringComparison.OrdinalIgnoreCase))
            {
                map[dirName] = status;
            }
        }

        return map;
    }
}
