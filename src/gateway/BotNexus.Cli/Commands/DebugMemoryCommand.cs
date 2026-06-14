using System.CommandLine;
using System.Text.Json;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// CLI subcommand for inspecting agent memory directories — daily notes,
/// consolidated memory files, and disk usage. Operates on local filesystem
/// without requiring a running gateway.
/// </summary>
internal sealed class DebugMemoryCommand
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Command Build(Option<string?> targetOption)
    {
        var command = new Command("memory", "Inspect agent memory directories (offline, no gateway required).");

        var formatOption = new Option<string>("--format", () => "table", "Output format: table or json.");
        var agentOption = new Option<string?>("--agent", "Show detailed view for a single agent.");

        command.AddOption(formatOption);
        command.AddOption(agentOption);

        command.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var agent = context.ParseResult.GetValueForOption(agentOption);
            var agentsDir = ResolveAgentsDir(target);
            context.ExitCode = Execute(agentsDir, agent, format);
            return Task.CompletedTask;
        });

        return command;
    }

    internal static string ResolveAgentsDir(string? target)
    {
        var home = CliPaths.ResolveTarget(target);
        return Path.Combine(home, "agents");
    }

    internal static int Execute(string agentsDir, string? agentFilter, string format)
    {
        if (!Directory.Exists(agentsDir))
        {
            AnsiConsole.MarkupLine("[red]Agents directory not found:[/] {0}", Markup.Escape(agentsDir));
            return 1;
        }

        var agents = CollectAgentMemoryInfo(agentsDir, agentFilter);

        if (agents.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No agents with memory directories found.[/]");
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(agentFilter))
            return RenderDetail(agents[0], format);

        return RenderSummary(agents, format);
    }

    internal static List<AgentMemoryInfo> CollectAgentMemoryInfo(string agentsDir, string? agentFilter)
    {
        var results = new List<AgentMemoryInfo>();
        var agentDirs = Directory.GetDirectories(agentsDir);

        foreach (var agentDir in agentDirs)
        {
            var agentId = Path.GetFileName(agentDir);
            if (!string.IsNullOrWhiteSpace(agentFilter) &&
                !string.Equals(agentId, agentFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var workspaceDir = Path.Combine(agentDir, "workspace");
            var memoryDir = Path.Combine(workspaceDir, "memory");
            var memoryMdPath = Path.Combine(workspaceDir, "MEMORY.md");

            if (!Directory.Exists(memoryDir) && !File.Exists(memoryMdPath))
                continue;

            var info = new AgentMemoryInfo { AgentId = agentId, WorkspacePath = workspaceDir };

            if (File.Exists(memoryMdPath))
            {
                var fi = new FileInfo(memoryMdPath);
                info.MemoryMdSizeBytes = fi.Length;
                info.HasMemoryMd = true;
            }

            if (Directory.Exists(memoryDir))
            {
                var dailyNotes = Directory.GetFiles(memoryDir, "*.md")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.Name)
                    .ToList();

                info.DailyNoteCount = dailyNotes.Count;
                info.TotalMemoryDirSizeBytes = dailyNotes.Sum(f => f.Length);
                if (dailyNotes.Count > 0)
                {
                    info.LastDailyNote = Path.GetFileNameWithoutExtension(dailyNotes[0].Name);
                    info.DailyNotes = dailyNotes.Select(f => new DailyNoteInfo
                    {
                        FileName = f.Name,
                        SizeBytes = f.Length,
                        LastModified = f.LastWriteTimeUtc
                    }).ToList();
                }
            }

            info.TotalSizeBytes = info.MemoryMdSizeBytes + info.TotalMemoryDirSizeBytes;
            results.Add(info);
        }

        return results.OrderBy(a => a.AgentId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int RenderSummary(List<AgentMemoryInfo> agents, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var output = agents.Select(a => new
            {
                a.AgentId,
                a.HasMemoryMd,
                memoryMdSizeBytes = a.MemoryMdSizeBytes,
                a.DailyNoteCount,
                a.LastDailyNote,
                totalSizeBytes = a.TotalSizeBytes
            });
            AnsiConsole.WriteLine(JsonSerializer.Serialize(output, JsonOpts));
            return 0;
        }

        var table = new Table();
        table.AddColumn("Agent");
        table.AddColumn("MEMORY.md");
        table.AddColumn("Daily Notes");
        table.AddColumn("Last Note");
        table.AddColumn("Total Size");

        foreach (var agent in agents)
        {
            table.AddRow(
                agent.AgentId,
                agent.HasMemoryMd ? FormatSize(agent.MemoryMdSizeBytes) : "[dim]—[/]",
                agent.DailyNoteCount.ToString(),
                agent.LastDailyNote ?? "[dim]—[/]",
                FormatSize(agent.TotalSizeBytes));
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static int RenderDetail(AgentMemoryInfo agent, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(agent, JsonOpts));
            return 0;
        }

        AnsiConsole.MarkupLine("[bold]{0}[/]", Markup.Escape(agent.AgentId));
        AnsiConsole.MarkupLine("  Workspace: {0}", Markup.Escape(agent.WorkspacePath));
        AnsiConsole.MarkupLine("  MEMORY.md: {0}",
            agent.HasMemoryMd ? FormatSize(agent.MemoryMdSizeBytes) : "[dim]not present[/]");
        AnsiConsole.MarkupLine("  Daily notes: {0}", agent.DailyNoteCount);
        AnsiConsole.MarkupLine("  Last daily note: {0}", agent.LastDailyNote ?? "[dim]none[/]");
        AnsiConsole.MarkupLine("  Total size: {0}", FormatSize(agent.TotalSizeBytes));

        if (agent.DailyNotes is { Count: > 0 })
        {
            AnsiConsole.WriteLine();
            var table = new Table();
            table.AddColumn("File");
            table.AddColumn("Size");
            table.AddColumn("Modified (UTC)");

            foreach (var note in agent.DailyNotes.Take(30))
            {
                table.AddRow(
                    note.FileName,
                    FormatSize(note.SizeBytes),
                    note.LastModified.ToString("yyyy-MM-dd HH:mm"));
            }

            if (agent.DailyNotes.Count > 30)
                table.Caption = new TableTitle($"Showing 30 of {agent.DailyNotes.Count} daily notes");

            AnsiConsole.Write(table);
        }

        return 0;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };

    internal sealed class AgentMemoryInfo
    {
        public string AgentId { get; set; } = "";
        public string WorkspacePath { get; set; } = "";
        public bool HasMemoryMd { get; set; }
        public long MemoryMdSizeBytes { get; set; }
        public int DailyNoteCount { get; set; }
        public string? LastDailyNote { get; set; }
        public long TotalMemoryDirSizeBytes { get; set; }
        public long TotalSizeBytes { get; set; }
        public List<DailyNoteInfo>? DailyNotes { get; set; }
    }

    internal sealed class DailyNoteInfo
    {
        public string FileName { get; set; } = "";
        public long SizeBytes { get; set; }
        public DateTimeOffset LastModified { get; set; }
    }
}
