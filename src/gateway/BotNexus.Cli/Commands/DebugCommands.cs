using System.CommandLine;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// CLI sub-commands for offline platform diagnostics.
/// Reads SQLite databases and log files directly -- no gateway connection required for most subcommands.
/// </summary>
internal sealed class DebugCommands
{
    // ──────────────────────────────────────────────────────────────────────
    //  Command tree
    // ──────────────────────────────────────────────────────────────────────

    public Command Build(Option<bool> verboseOption)
    {
        var command = new Command("debug", "Offline platform diagnostics -- read sessions, cron, and log files directly.");

        var targetOption = new Option<string?>("--target", "BotNexus home directory (default: ~/.botnexus or BOTNEXUS_HOME env var).");

        // ── sessions ──────────────────────────────────────────────────────
        var sessionsCommand = new Command("sessions", "Inspect session store (reads sessions.db directly, no gateway needed).")
        {
            targetOption
        };

        // sessions list
        var agentFilterOption = new Option<string?>("--agent", "Filter by agent ID.");
        var statusFilterOption = new Option<string?>("--status", "Filter by status (active, sealed, expired, all).") { };
        var limitOption = new Option<int>("--limit", () => 20, "Maximum rows to return.");
        var formatOption = new Option<string>("--format", () => "table", "Output format: table or json.");

        var sessionsListCommand = new Command("list", "List sessions from sessions.db.")
        {
            agentFilterOption, statusFilterOption, limitOption, formatOption
        };
        sessionsListCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var agentFilter = context.ParseResult.GetValueForOption(agentFilterOption);
            var statusFilter = context.ParseResult.GetValueForOption(statusFilterOption);
            var limit = context.ParseResult.GetValueForOption(limitOption);
            var format = context.ParseResult.GetValueForOption(formatOption)!;
            context.ExitCode = await ExecuteSessionsListAsync(target, agentFilter, statusFilter, limit, format, context.GetCancellationToken());
        });

        // sessions get <id>
        var sessionIdArg = new Argument<string>("session-id", "Session ID.");
        var sessionsGetCommand = new Command("get", "Show full metadata for a single session.") { sessionIdArg, formatOption };
        sessionsGetCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var id = context.ParseResult.GetValueForArgument(sessionIdArg);
            var format = context.ParseResult.GetValueForOption(formatOption)!;
            context.ExitCode = await ExecuteSessionsGetAsync(target, id, format, context.GetCancellationToken());
        });

        // sessions compaction <id>
        var compactionIdArg = new Argument<string>("session-id", "Session ID.");
        var sessionsCompactionCommand = new Command("compaction", "Show compaction summary content for a session.") { compactionIdArg };
        sessionsCompactionCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var id = context.ParseResult.GetValueForArgument(compactionIdArg);
            context.ExitCode = await ExecuteSessionsCompactionAsync(target, id, context.GetCancellationToken());
        });

        // sessions stats
        var sessionsStatsCommand = new Command("stats", "Show aggregate session statistics.") { formatOption };
        sessionsStatsCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var format = context.ParseResult.GetValueForOption(formatOption)!;
            context.ExitCode = await ExecuteSessionsStatsAsync(target, format, context.GetCancellationToken());
        });

        sessionsCommand.AddCommand(sessionsListCommand);
        sessionsCommand.AddCommand(sessionsGetCommand);
        sessionsCommand.AddCommand(sessionsCompactionCommand);
        sessionsCommand.AddCommand(sessionsStatsCommand);

        // ── db ────────────────────────────────────────────────────────────
        var dbCommand = new Command("db", "Raw database introspection (no gateway needed).") { targetOption };
        var dbNameOption = new Option<string>("--db", () => "sessions", "Database to inspect: sessions, cron, or memory.");

        // db tables
        var dbTablesCommand = new Command("tables", "List tables and row counts.") { dbNameOption, formatOption };
        dbTablesCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var db = context.ParseResult.GetValueForOption(dbNameOption)!;
            var format = context.ParseResult.GetValueForOption(formatOption)!;
            context.ExitCode = await ExecuteDbTablesAsync(target, db, format, context.GetCancellationToken());
        });

        // db schema
        var dbSchemaCommand = new Command("schema", "Show CREATE TABLE statements.") { dbNameOption };
        dbSchemaCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var db = context.ParseResult.GetValueForOption(dbNameOption)!;
            context.ExitCode = await ExecuteDbSchemaAsync(target, db, context.GetCancellationToken());
        });

        // db size
        var dbSizeCommand = new Command("size", "Show file sizes for all .db files.");
        dbSizeCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            context.ExitCode = ExecuteDbSizeAsync(target);
        });

        dbCommand.AddCommand(dbTablesCommand);
        dbCommand.AddCommand(dbSchemaCommand);
        dbCommand.AddCommand(dbSizeCommand);

        // ── logs ──────────────────────────────────────────────────────────
        var logsCommand = new Command("logs", "Inspect log files directly from ~/.botnexus/logs/.") { targetOption };
        var logLevelOption = new Option<string?>("--level", "Filter by log level: debug, info, warn, error.");
        var logLimitOption = new Option<int>("--limit", () => 50, "Maximum lines to return.");
        var searchTermOption = new Option<string?>("--term", "Keyword to search for.");
        var sessionSearchOption = new Option<string?>("--session-id", "Session ID to extract log lines for.");

        var logsTailCommand = new Command("tail", "Show recent log entries.") { logLevelOption, logLimitOption };
        logsTailCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var level = context.ParseResult.GetValueForOption(logLevelOption);
            var limit = context.ParseResult.GetValueForOption(logLimitOption);
            context.ExitCode = ExecuteLogsTailAsync(target, level, limit);
        });

        var logsErrorsCommand = new Command("errors", "Show recent error log entries.") { logLimitOption };
        logsErrorsCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var limit = context.ParseResult.GetValueForOption(logLimitOption);
            context.ExitCode = ExecuteLogsTailAsync(target, "error", limit);
        });

        var logsSearchCommand = new Command("search", "Search log files for a keyword.") { searchTermOption, logLimitOption };
        logsSearchCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var term = context.ParseResult.GetValueForOption(searchTermOption);
            var limit = context.ParseResult.GetValueForOption(logLimitOption);
            context.ExitCode = ExecuteLogsSearchAsync(target, term, limit);
        });

        var logsSessionCommand = new Command("session", "Show all log lines for a given session ID.") { sessionSearchOption };
        logsSessionCommand.SetHandler(context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var sessionId = context.ParseResult.GetValueForOption(sessionSearchOption);
            context.ExitCode = ExecuteLogsSearchAsync(target, sessionId, 500);
        });

        logsCommand.AddCommand(logsTailCommand);
        logsCommand.AddCommand(logsErrorsCommand);
        logsCommand.AddCommand(logsSearchCommand);
        logsCommand.AddCommand(logsSessionCommand);

        command.AddCommand(sessionsCommand);
        command.AddCommand(dbCommand);
        command.AddCommand(logsCommand);

        return command;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Sessions implementation
    // ──────────────────────────────────────────────────────────────────────

    internal static async Task<int> ExecuteSessionsListAsync(
        string? target, string? agentFilter, string? statusFilter, int limit, string format,
        CancellationToken cancellationToken)
    {
        var dbPath = ResolveDbPath(target, "sessions");
        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLine($"[red]sessions.db not found at {dbPath}[/]");
            return 1;
        }

        var rows = new List<Dictionary<string, string>>();
        await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync(cancellationToken);

        var whereClause = BuildSessionsWhereClause(agentFilter, statusFilter);
        var sql = $"""
            SELECT id, agent_id, session_type, status, created_at, updated_at, conversation_id
            FROM sessions
            {whereClause}
            ORDER BY updated_at DESC
            LIMIT $limit
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$limit", limit);
        if (!string.IsNullOrWhiteSpace(agentFilter))
            cmd.Parameters.AddWithValue("$agentId", agentFilter);
        if (!string.IsNullOrWhiteSpace(statusFilter) && statusFilter != "all")
            cmd.Parameters.AddWithValue("$status", statusFilter);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new Dictionary<string, string>
            {
                ["id"] = reader.IsDBNull(0) ? "" : reader.GetString(0),
                ["agent_id"] = reader.IsDBNull(1) ? "" : reader.GetString(1),
                ["type"] = reader.IsDBNull(2) ? "" : reader.GetString(2),
                ["status"] = reader.IsDBNull(3) ? "" : reader.GetString(3),
                ["created_at"] = reader.IsDBNull(4) ? "" : reader.GetString(4),
                ["updated_at"] = reader.IsDBNull(5) ? "" : reader.GetString(5),
                ["conversation_id"] = reader.IsDBNull(6) ? "" : reader.GetString(6)
            });
        }

        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        var table = new Table();
        table.AddColumn("ID");
        table.AddColumn("Agent");
        table.AddColumn("Type");
        table.AddColumn("Status");
        table.AddColumn("Updated At");
        foreach (var row in rows)
        {
            var shortId = row["id"].Length > 16 ? row["id"][..16] + "…" : row["id"];
            table.AddRow(shortId, row["agent_id"], row["type"], row["status"], row["updated_at"]);
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]{rows.Count} session(s) returned[/]");
        return 0;
    }

    internal static async Task<int> ExecuteSessionsGetAsync(
        string? target, string sessionId, string format, CancellationToken cancellationToken)
    {
        var dbPath = ResolveDbPath(target, "sessions");
        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLine($"[red]sessions.db not found at {dbPath}[/]");
            return 1;
        }

        await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, agent_id, channel_type, caller_id, session_type, status, metadata, created_at, updated_at, conversation_id,
                   (SELECT COUNT(*) FROM session_history WHERE session_id = $id) AS message_count,
                   (SELECT COUNT(*) FROM session_history WHERE session_id = $id AND is_compaction_summary = 1) AS has_compaction
            FROM sessions WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            AnsiConsole.MarkupLine($"[red]Session '{sessionId}' not found.[/]");
            return 1;
        }

        var data = new
        {
            id = reader.IsDBNull(0) ? null : reader.GetString(0),
            agentId = reader.IsDBNull(1) ? null : reader.GetString(1),
            channelType = reader.IsDBNull(2) ? null : reader.GetString(2),
            callerId = reader.IsDBNull(3) ? null : reader.GetString(3),
            sessionType = reader.IsDBNull(4) ? null : reader.GetString(4),
            status = reader.IsDBNull(5) ? null : reader.GetString(5),
            metadata = reader.IsDBNull(6) ? null : reader.GetString(6),
            createdAt = reader.IsDBNull(7) ? null : reader.GetString(7),
            updatedAt = reader.IsDBNull(8) ? null : reader.GetString(8),
            conversationId = reader.IsDBNull(9) ? null : reader.GetString(9),
            messageCount = reader.GetInt64(10),
            hasCompactionSummary = reader.GetInt64(11) > 0
        };

        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold]Session:[/] {data.id}");
        AnsiConsole.MarkupLine($"  Agent:          {data.agentId}");
        AnsiConsole.MarkupLine($"  Type:           {data.sessionType}");
        AnsiConsole.MarkupLine($"  Status:         {data.status}");
        AnsiConsole.MarkupLine($"  Channel:        {data.channelType ?? "—"}");
        AnsiConsole.MarkupLine($"  ConversationId: {data.conversationId ?? "—"}");
        AnsiConsole.MarkupLine($"  Created:        {data.createdAt}");
        AnsiConsole.MarkupLine($"  Updated:        {data.updatedAt}");
        AnsiConsole.MarkupLine($"  Messages:       {data.messageCount}");
        AnsiConsole.MarkupLine($"  Compacted:      {(data.hasCompactionSummary ? "yes" : "no")}");
        return 0;
    }

    internal static async Task<int> ExecuteSessionsCompactionAsync(
        string? target, string sessionId, CancellationToken cancellationToken)
    {
        var dbPath = ResolveDbPath(target, "sessions");
        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLine($"[red]sessions.db not found at {dbPath}[/]");
            return 1;
        }

        await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync(cancellationToken);

        // Check session exists
        await using var existCmd = conn.CreateCommand();
        existCmd.CommandText = "SELECT COUNT(*) FROM sessions WHERE id = $id";
        existCmd.Parameters.AddWithValue("$id", sessionId);
        var exists = (long)(await existCmd.ExecuteScalarAsync(cancellationToken))! > 0;
        if (!exists)
        {
            AnsiConsole.MarkupLine($"[red]Session '{sessionId}' not found.[/]");
            return 1;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT content FROM session_history
            WHERE session_id = $id AND is_compaction_summary = 1
            ORDER BY id DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);

        var content = await cmd.ExecuteScalarAsync(cancellationToken) as string;
        if (content is null)
        {
            Console.WriteLine("[NONE -- no compaction has occurred for this session]");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            Console.WriteLine("[EMPTY -- compaction produced no summary]");
            AnsiConsole.MarkupLine("[dim]This is the bug described in #366 / #402. The LLM returned empty content during compaction.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine("[bold]Compaction summary:[/]");
        AnsiConsole.WriteLine(content);
        return 0;
    }

    internal static async Task<int> ExecuteSessionsStatsAsync(
        string? target, string format, CancellationToken cancellationToken)
    {
        var dbPath = ResolveDbPath(target, "sessions");
        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLine($"[red]sessions.db not found at {dbPath}[/]");
            return 1;
        }

        await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync(cancellationToken);

        var stats = new Dictionary<string, object>();

        // Total count
        await using var totalCmd = conn.CreateCommand();
        totalCmd.CommandText = "SELECT COUNT(*) FROM sessions";
        stats["totalSessions"] = (long)(await totalCmd.ExecuteScalarAsync(cancellationToken))!;

        // Per-agent counts
        await using var agentCmd = conn.CreateCommand();
        agentCmd.CommandText = "SELECT agent_id, COUNT(*) FROM sessions GROUP BY agent_id ORDER BY COUNT(*) DESC";
        var perAgentRaw = new List<(string agentId, long count)>();
        await using var agentReader = await agentCmd.ExecuteReaderAsync(cancellationToken);
        while (await agentReader.ReadAsync(cancellationToken))
            perAgentRaw.Add((agentReader.GetString(0), agentReader.GetInt64(1)));
        stats["perAgent"] = perAgentRaw.Select(x => new { agentId = x.agentId, count = x.count }).ToList();

        // Per-status counts
        await using var statusCmd = conn.CreateCommand();
        statusCmd.CommandText = "SELECT status, COUNT(*) FROM sessions GROUP BY status";
        var perStatus = new Dictionary<string, long>();
        await using var statusReader = await statusCmd.ExecuteReaderAsync(cancellationToken);
        while (await statusReader.ReadAsync(cancellationToken))
            perStatus[statusReader.IsDBNull(0) ? "null" : statusReader.GetString(0)] = statusReader.GetInt64(1);
        stats["perStatus"] = perStatus;

        // DB file size
        stats["dbFileSizeBytes"] = new FileInfo(dbPath).Length;

        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold]Session Stats[/]");
        AnsiConsole.MarkupLine($"  Total sessions: {stats["totalSessions"]}");
        AnsiConsole.MarkupLine($"  DB size:        {FormatBytes((long)stats["dbFileSizeBytes"])}");

        var statusTable = new Table();
        statusTable.AddColumn("Status");
        statusTable.AddColumn("Count");
        foreach (var (k, v) in perStatus)
            statusTable.AddRow(k, v.ToString());
        AnsiConsole.Write(statusTable);

        var agentTable = new Table();
        agentTable.AddColumn("Agent");
        agentTable.AddColumn("Sessions");
        foreach (var a in perAgentRaw)
            agentTable.AddRow(a.agentId, a.count.ToString());
        AnsiConsole.Write(agentTable);

        return 0;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  DB implementation
    // ──────────────────────────────────────────────────────────────────────

    internal static async Task<int> ExecuteDbTablesAsync(
        string? target, string db, string format, CancellationToken cancellationToken)
    {
        var dbPath = ResolveDbPath(target, db);
        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLine($"[red]{db}.db not found at {dbPath}[/]");
            return 1;
        }

        await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var tables = new List<Dictionary<string, object>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var tableName = reader.GetString(0);
            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\"";
            var count = (long)(await countCmd.ExecuteScalarAsync(cancellationToken))!;
            tables.Add(new Dictionary<string, object> { ["table"] = tableName, ["rows"] = count });
        }

        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(tables, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        var table = new Table();
        table.AddColumn("Table");
        table.AddColumn("Rows");
        foreach (var row in tables)
            table.AddRow(row["table"].ToString()!, row["rows"].ToString()!);
        AnsiConsole.Write(table);
        return 0;
    }

    internal static async Task<int> ExecuteDbSchemaAsync(
        string? target, string db, CancellationToken cancellationToken)
    {
        var dbPath = ResolveDbPath(target, db);
        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLine($"[red]{db}.db not found at {dbPath}[/]");
            return 1;
        }

        await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND sql IS NOT NULL ORDER BY name";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            Console.WriteLine(reader.GetString(0));
            Console.WriteLine();
        }
        return 0;
    }

    internal static int ExecuteDbSizeAsync(string? target)
    {
        var homeDir = CliPaths.ResolveTarget(target);
        var dbFiles = Directory.GetFiles(homeDir, "*.db", SearchOption.TopDirectoryOnly);

        if (dbFiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No .db files found in {homeDir}[/]");
            return 0;
        }

        var table = new Table();
        table.AddColumn("File");
        table.AddColumn("Size");
        foreach (var f in dbFiles.OrderBy(f => f))
            table.AddRow(Path.GetFileName(f), FormatBytes(new FileInfo(f).Length));
        AnsiConsole.Write(table);
        return 0;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Logs implementation
    // ──────────────────────────────────────────────────────────────────────

    internal static int ExecuteLogsTailAsync(string? target, string? level, int limit)
    {
        var logsDir = Path.Combine(CliPaths.ResolveTarget(target), "logs");
        if (!Directory.Exists(logsDir))
        {
            AnsiConsole.MarkupLine($"[yellow]Logs directory not found: {logsDir}[/]");
            return 0;
        }

        var logFiles = Directory.GetFiles(logsDir, "botnexus-*.log")
            .OrderByDescending(f => f)
            .Take(2)
            .ToArray();

        if (logFiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No log files found.[/]");
            return 0;
        }

        var lines = logFiles
            .SelectMany(f => ReadLogFileLines(f))
            .Where(l => level is null || l.Contains($"[{level.ToUpperInvariant()[0]}") || l.Contains($"[{level}]", StringComparison.OrdinalIgnoreCase))
            .TakeLast(limit)
            .ToList();

        foreach (var line in lines)
            Console.WriteLine(line);
        return 0;
    }

    internal static int ExecuteLogsSearchAsync(string? target, string? term, int limit)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            AnsiConsole.MarkupLine("[red]--term is required for search[/]");
            return 1;
        }

        var logsDir = Path.Combine(CliPaths.ResolveTarget(target), "logs");
        if (!Directory.Exists(logsDir))
        {
            AnsiConsole.MarkupLine($"[yellow]Logs directory not found: {logsDir}[/]");
            return 0;
        }

        var logFiles = Directory.GetFiles(logsDir, "botnexus-*.log")
            .OrderByDescending(f => f)
            .ToArray();

        var matches = logFiles
            .SelectMany(f => ReadLogFileLines(f))
            .Where(l => l.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();

        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No matches for '{term}'[/]");
            return 0;
        }

        foreach (var line in matches)
            Console.WriteLine(line);
        return 0;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────────

    internal static string ResolveDbPath(string? target, string dbName)
    {
        var home = CliPaths.ResolveTarget(target);
        return Path.Combine(home, $"{dbName}.db");
    }

    private static string BuildSessionsWhereClause(string? agentFilter, string? statusFilter)
    {
        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(agentFilter))
            conditions.Add("agent_id = $agentId");
        if (!string.IsNullOrWhiteSpace(statusFilter) && statusFilter != "all")
            conditions.Add("status = $status");
        return conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
    }

    private static IEnumerable<string> ReadLogFileLines(string path)
    {
        try
        {
            return File.ReadLines(path);
        }
        catch
        {
            return [];
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024):F1} MB";
    }
}
