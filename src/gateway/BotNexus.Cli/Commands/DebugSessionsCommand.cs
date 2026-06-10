using System.CommandLine;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// CLI subcommands for inspecting the sessions SQLite database directly
/// without requiring a running gateway instance. Provides list, get,
/// compaction, and stats operations for offline diagnostics.
/// </summary>
internal sealed class DebugSessionsCommand
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Command Build(Option<string?> targetOption)
    {
        var command = new Command("sessions", "Inspect session store directly (offline, no gateway required).");

        var formatOption = new Option<string>("--format", () => "table", "Output format: table or json.");
        command.AddOption(formatOption);

        // ── list ──
        var agentOption = new Option<string?>("--agent", "Filter by agent ID.");
        var statusOption = new Option<string?>("--status", "Filter by status: active, sealed, expired, all.");
        var limitOption = new Option<int>("--limit", () => 20, "Maximum sessions to return.");

        var listCommand = new Command("list", "List sessions from sessions.db.")
        {
            agentOption, statusOption, limitOption
        };
        listCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var agent = context.ParseResult.GetValueForOption(agentOption);
            var status = context.ParseResult.GetValueForOption(statusOption);
            var limit = context.ParseResult.GetValueForOption(limitOption);
            var dbPath = ResolveSessionsDb(target);
            context.ExitCode = ExecuteList(dbPath, agent, status, limit, format);
            return Task.CompletedTask;
        });

        // ── get ──
        var sessionIdArg = new Argument<string>("session-id", "Session ID to inspect.");
        var getCommand = new Command("get", "Show full session metadata.") { sessionIdArg };
        getCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var sessionId = context.ParseResult.GetValueForArgument(sessionIdArg);
            var dbPath = ResolveSessionsDb(target);
            context.ExitCode = ExecuteGet(dbPath, sessionId, format);
            return Task.CompletedTask;
        });

        // ── compaction ──
        var compactionCommand = new Command("compaction", "Show compaction summary for a session.") { sessionIdArg };
        compactionCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var sessionId = context.ParseResult.GetValueForArgument(sessionIdArg);
            var dbPath = ResolveSessionsDb(target);
            context.ExitCode = ExecuteCompaction(dbPath, sessionId, format);
            return Task.CompletedTask;
        });

        // ── stats ──
        var statsCommand = new Command("stats", "Show aggregate session statistics.");
        statsCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "table";
            var dbPath = ResolveSessionsDb(target);
            context.ExitCode = ExecuteStats(dbPath, format);
            return Task.CompletedTask;
        });

        command.AddCommand(listCommand);
        command.AddCommand(getCommand);
        command.AddCommand(compactionCommand);
        command.AddCommand(statsCommand);
        return command;
    }

    internal static string ResolveSessionsDb(string? target)
    {
        var home = CliPaths.ResolveTarget(target);
        return Path.Combine(home, "sessions.db");
    }

    internal static int ExecuteList(string dbPath, string? agent, string? status, int limit, string format)
    {
        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLine("[red]sessions.db not found at:[/] " + Markup.Escape(dbPath));
            return 1;
        }

        using var connection = OpenReadOnly(dbPath);
        var sql = BuildListQuery(agent, status, limit);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (agent is not null) cmd.Parameters.AddWithValue("@agent", agent);
        if (status is not null && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
            cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@limit", limit);

        var sessions = new List<SessionListEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(new SessionListEntry
            {
                Id = reader.GetString(0),
                ConversationId = reader.IsDBNull(1) ? null : reader.GetString(1),
                Status = reader.IsDBNull(2) ? "unknown" : reader.GetString(2),
                CreatedAt = reader.IsDBNull(3) ? null : reader.GetString(3),
                MessageCount = reader.GetInt32(4)
            });
        }

        if (format == "json")
        {
            AnsiConsole.Write(new Text(JsonSerializer.Serialize(sessions, JsonOpts)));
            AnsiConsole.WriteLine();
            return 0;
        }

        if (sessions.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No sessions found matching criteria.[/]");
            return 0;
        }

        var table = new Table()
            .AddColumn("Session ID")
            .AddColumn("Conversation")
            .AddColumn("Status")
            .AddColumn("Created")
            .AddColumn("Messages");

        foreach (var s in sessions)
        {
            table.AddRow(
                Markup.Escape(Truncate(s.Id, 24)),
                Markup.Escape(Truncate(s.ConversationId ?? "(none)", 20)),
                FormatStatus(s.Status),
                Markup.Escape(FormatDate(s.CreatedAt)),
                s.MessageCount.ToString());
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[dim]{sessions.Count} session(s) shown.[/]");
        return 0;
    }

    internal static int ExecuteGet(string dbPath, string sessionId, string format)
    {
        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLine("[red]sessions.db not found at:[/] " + Markup.Escape(dbPath));
            return 1;
        }

        using var connection = OpenReadOnly(dbPath);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, conversation_id, channel_type, caller_id, session_type, status, metadata, created_at, updated_at FROM sessions WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", sessionId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            AnsiConsole.MarkupLine($"[red]Session not found:[/] {Markup.Escape(sessionId)}");
            return 1;
        }

        var detail = new SessionDetail
        {
            Id = reader.GetString(0),
            ConversationId = reader.IsDBNull(1) ? null : reader.GetString(1),
            ChannelType = reader.IsDBNull(2) ? null : reader.GetString(2),
            CallerId = reader.IsDBNull(3) ? null : reader.GetString(3),
            SessionType = reader.IsDBNull(4) ? null : reader.GetString(4),
            Status = reader.IsDBNull(5) ? null : reader.GetString(5),
            Metadata = reader.IsDBNull(6) ? null : reader.GetString(6),
            CreatedAt = reader.IsDBNull(7) ? null : reader.GetString(7),
            UpdatedAt = reader.IsDBNull(8) ? null : reader.GetString(8)
        };

        // Get message count and compaction presence
        using var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM session_history WHERE session_id = @id";
        countCmd.Parameters.AddWithValue("@id", sessionId);
        detail.MessageCount = Convert.ToInt32(countCmd.ExecuteScalar());

        using var compCmd = connection.CreateCommand();
        compCmd.CommandText = "SELECT COUNT(*) FROM session_history WHERE session_id = @id AND is_compaction_summary = 1";
        compCmd.Parameters.AddWithValue("@id", sessionId);
        detail.HasCompaction = Convert.ToInt32(compCmd.ExecuteScalar()) > 0;

        if (format == "json")
        {
            AnsiConsole.Write(new Text(JsonSerializer.Serialize(detail, JsonOpts)));
            AnsiConsole.WriteLine();
            return 0;
        }

        var panel = new Panel(
            new Rows(
                new Markup($"[bold]ID:[/]              {Markup.Escape(detail.Id)}"),
                new Markup($"[bold]Conversation:[/]    {Markup.Escape(detail.ConversationId ?? "(none)")}"),
                new Markup($"[bold]Channel:[/]         {Markup.Escape(detail.ChannelType ?? "(none)")}"),
                new Markup($"[bold]Caller:[/]          {Markup.Escape(detail.CallerId ?? "(none)")}"),
                new Markup($"[bold]Type:[/]            {Markup.Escape(detail.SessionType ?? "(none)")}"),
                new Markup($"[bold]Status:[/]          {FormatStatus(detail.Status ?? "unknown")}"),
                new Markup($"[bold]Created:[/]         {Markup.Escape(FormatDate(detail.CreatedAt))}"),
                new Markup($"[bold]Updated:[/]         {Markup.Escape(FormatDate(detail.UpdatedAt))}"),
                new Markup($"[bold]Messages:[/]        {detail.MessageCount}"),
                new Markup($"[bold]Has Compaction:[/]  {(detail.HasCompaction ? "[green]yes[/]" : "[dim]no[/]")}")))
        {
            Header = new PanelHeader($" Session: {Truncate(detail.Id, 24)} "),
            Border = BoxBorder.Rounded
        };
        AnsiConsole.Write(panel);

        if (detail.Metadata is not null)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Metadata:[/]");
            try
            {
                var formatted = JsonSerializer.Serialize(
                    JsonSerializer.Deserialize<JsonElement>(detail.Metadata), JsonOpts);
                AnsiConsole.Write(new Text(formatted));
                AnsiConsole.WriteLine();
            }
            catch
            {
                AnsiConsole.Write(new Text(detail.Metadata));
                AnsiConsole.WriteLine();
            }
        }

        return 0;
    }

    internal static int ExecuteCompaction(string dbPath, string sessionId, string format)
    {
        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLine("[red]sessions.db not found at:[/] " + Markup.Escape(dbPath));
            return 1;
        }

        using var connection = OpenReadOnly(dbPath);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT content, timestamp FROM session_history WHERE session_id = @id AND is_compaction_summary = 1 ORDER BY timestamp DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@id", sessionId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            if (format == "json")
            {
                AnsiConsole.Write(new Text(JsonSerializer.Serialize(new { status = "none", content = (string?)null }, JsonOpts)));
                AnsiConsole.WriteLine();
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow][NONE — no compaction has occurred for this session][/]");
            }
            return 0;
        }

        var content = reader.IsDBNull(0) ? null : reader.GetString(0);
        var timestamp = reader.IsDBNull(1) ? null : reader.GetString(1);

        if (string.IsNullOrWhiteSpace(content))
        {
            if (format == "json")
            {
                AnsiConsole.Write(new Text(JsonSerializer.Serialize(new { status = "empty", content = "", timestamp }, JsonOpts)));
                AnsiConsole.WriteLine();
            }
            else
            {
                AnsiConsole.MarkupLine("[red][EMPTY — compaction produced no summary][/]");
                if (timestamp is not null)
                    AnsiConsole.MarkupLine($"[dim]Compaction timestamp: {Markup.Escape(timestamp)}[/]");
            }
            return 0;
        }

        if (format == "json")
        {
            AnsiConsole.Write(new Text(JsonSerializer.Serialize(new { status = "present", content, timestamp }, JsonOpts)));
            AnsiConsole.WriteLine();
        }
        else
        {
            AnsiConsole.MarkupLine($"[bold]Compaction Summary[/] [dim]({Markup.Escape(timestamp ?? "unknown")})[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Text(content));
            AnsiConsole.WriteLine();
        }

        return 0;
    }

    internal static int ExecuteStats(string dbPath, string format)
    {
        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLine("[red]sessions.db not found at:[/] " + Markup.Escape(dbPath));
            return 1;
        }

        using var connection = OpenReadOnly(dbPath);
        var stats = new SessionStats();

        // Total sessions
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sessions";
            stats.TotalSessions = Convert.ToInt32(cmd.ExecuteScalar());
        }

        // Per-status breakdown
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(status, 'unknown'), COUNT(*) FROM sessions GROUP BY status ORDER BY COUNT(*) DESC";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                stats.ByStatus[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        // Per-conversation agent breakdown (via conversations table if available)
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT conversation_id, COUNT(*) FROM sessions WHERE conversation_id IS NOT NULL GROUP BY conversation_id ORDER BY COUNT(*) DESC LIMIT 20";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                stats.TopConversations[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        // Total messages
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM session_history";
            stats.TotalMessages = Convert.ToInt32(cmd.ExecuteScalar());
        }

        // DB file size
        var fileInfo = new FileInfo(dbPath);
        stats.DbSizeBytes = fileInfo.Length;
        stats.DbSizeMb = Math.Round(fileInfo.Length / (1024.0 * 1024.0), 2);

        if (format == "json")
        {
            AnsiConsole.Write(new Text(JsonSerializer.Serialize(stats, JsonOpts)));
            AnsiConsole.WriteLine();
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold]Session Store Statistics[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  Total sessions:  [bold]{stats.TotalSessions}[/]");
        AnsiConsole.MarkupLine($"  Total messages:  [bold]{stats.TotalMessages}[/]");
        AnsiConsole.MarkupLine($"  DB size:         [bold]{stats.DbSizeMb} MB[/] ({stats.DbSizeBytes:N0} bytes)");
        AnsiConsole.WriteLine();

        if (stats.ByStatus.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold]By Status:[/]");
            foreach (var (status, count) in stats.ByStatus)
            {
                AnsiConsole.MarkupLine($"  {FormatStatus(status)}  {count}");
            }
        }

        return 0;
    }

    // ── Helpers ──

    internal static string BuildListQuery(string? agent, string? status, int limit)
    {
        // Agent filtering requires joining to conversations table which may not be in sessions.db
        // For now, filter by conversation_id prefix if agent is provided
        var whereClauses = new List<string>();

        if (agent is not null)
            whereClauses.Add("s.conversation_id LIKE @agent || '%'");

        if (status is not null && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
            whereClauses.Add("s.status = @status");

        var where = whereClauses.Count > 0 ? " WHERE " + string.Join(" AND ", whereClauses) : "";

        return $"""
            SELECT s.id, s.conversation_id, s.status, s.created_at,
                   (SELECT COUNT(*) FROM session_history h WHERE h.session_id = s.id) as msg_count
            FROM sessions s{where}
            ORDER BY s.created_at DESC
            LIMIT @limit
            """;
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        return connection;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";

    private static string FormatDate(string? isoDate)
    {
        if (isoDate is null) return "(none)";
        if (DateTimeOffset.TryParse(isoDate, out var dto))
            return dto.ToString("yyyy-MM-dd HH:mm:ss");
        return isoDate;
    }

    private static string FormatStatus(string status) => status.ToLowerInvariant() switch
    {
        "active" => "[green]active[/]",
        "sealed" => "[blue]sealed[/]",
        "expired" => "[dim]expired[/]",
        _ => Markup.Escape(status)
    };

    // ── DTOs ──

    internal sealed class SessionListEntry
    {
        public string Id { get; set; } = "";
        public string? ConversationId { get; set; }
        public string Status { get; set; } = "";
        public string? CreatedAt { get; set; }
        public int MessageCount { get; set; }
    }

    internal sealed class SessionDetail
    {
        public string Id { get; set; } = "";
        public string? ConversationId { get; set; }
        public string? ChannelType { get; set; }
        public string? CallerId { get; set; }
        public string? SessionType { get; set; }
        public string? Status { get; set; }
        public string? Metadata { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
        public int MessageCount { get; set; }
        public bool HasCompaction { get; set; }
    }

    internal sealed class SessionStats
    {
        public int TotalSessions { get; set; }
        public int TotalMessages { get; set; }
        public long DbSizeBytes { get; set; }
        public double DbSizeMb { get; set; }
        public Dictionary<string, int> ByStatus { get; set; } = new();
        public Dictionary<string, int> TopConversations { get; set; } = new();
    }
}
