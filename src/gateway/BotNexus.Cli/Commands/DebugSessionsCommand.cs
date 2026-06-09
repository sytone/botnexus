using System.CommandLine;
using Microsoft.Data.Sqlite;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>
/// CLI subcommands for inspecting session data directly from sessions.db.
/// Does not require a running gateway — reads SQLite directly.
/// </summary>
internal sealed class DebugSessionsCommand
{
    private readonly Func<string> _dbPathResolver;

    /// <summary>
    /// Test constructor — accepts a fixed path resolver.
    /// </summary>
    public DebugSessionsCommand(Func<string> dbPathResolver)
        => _dbPathResolver = dbPathResolver;

    /// <summary>
    /// Production constructor — resolves DB path from --target option at call time.
    /// </summary>
    public DebugSessionsCommand() : this(() => ResolveSessionsDb(null)) { }

    private static string ResolveSessionsDb(string? target)
        => Path.Combine(CliPaths.ResolveTarget(target), "sessions.db");

    // ──────────────────────────────────────────────────────────────────────
    //  Command tree
    // ──────────────────────────────────────────────────────────────────────

    public Command Build(Option<string?> targetOption)
    {
        var command = new Command("sessions", "Inspect session data directly from sessions.db (no gateway needed).");

        // ── list ──────────────────────────────────────────────────────────
        var statusOption = new Option<string?>("--status", "Filter by session status (Active, Sealed, Expired).");
        var limitOption = new Option<int>("--limit", () => 50, "Maximum sessions to return.");
        var listCommand = new Command("list", "List sessions from sessions.db.") { statusOption, limitOption };
        listCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var status = context.ParseResult.GetValueForOption(statusOption);
            var limit = context.ParseResult.GetValueForOption(limitOption);
            var cmd = new DebugSessionsCommand(() => ResolveSessionsDb(target));
            var result = await cmd.ExecuteListAsync(status, limit, context.GetCancellationToken());
            PrintListResult(result);
            context.ExitCode = result.ExitCode;
        });

        // ── get ───────────────────────────────────────────────────────────
        var sessionIdArg = new Argument<string>("session-id", "Session ID to inspect.");
        var getCommand = new Command("get", "Show full metadata for a session.") { sessionIdArg };
        getCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var id = context.ParseResult.GetValueForArgument(sessionIdArg);
            var cmd = new DebugSessionsCommand(() => ResolveSessionsDb(target));
            var result = await cmd.ExecuteGetAsync(id, context.GetCancellationToken());
            PrintGetResult(result);
            context.ExitCode = result.ExitCode;
        });

        // ── compaction ────────────────────────────────────────────────────
        var compactionCommand = new Command("compaction", "Show compaction summary for a session.") { sessionIdArg };
        compactionCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var id = context.ParseResult.GetValueForArgument(sessionIdArg);
            var cmd = new DebugSessionsCommand(() => ResolveSessionsDb(target));
            var result = await cmd.ExecuteCompactionAsync(id, context.GetCancellationToken());
            PrintCompactionResult(result);
            context.ExitCode = result.ExitCode;
        });

        // ── stats ─────────────────────────────────────────────────────────
        var statsCommand = new Command("stats", "Aggregate session statistics and DB size.");
        statsCommand.SetHandler(async context =>
        {
            var target = context.ParseResult.GetValueForOption(targetOption);
            var cmd = new DebugSessionsCommand(() => ResolveSessionsDb(target));
            var result = await cmd.ExecuteStatsAsync(context.GetCancellationToken());
            PrintStatsResult(result);
            context.ExitCode = result.ExitCode;
        });

        command.AddCommand(listCommand);
        command.AddCommand(getCommand);
        command.AddCommand(compactionCommand);
        command.AddCommand(statsCommand);
        return command;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Internal execution methods (testable without CLI harness)
    // ──────────────────────────────────────────────────────────────────────

    internal async Task<ListResult> ExecuteListAsync(string? status, int limit, CancellationToken ct)
    {
        var dbPath = _dbPathResolver();
        if (!File.Exists(dbPath))
            return new ListResult(1, [], $"Database not found: {dbPath}");

        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await connection.OpenAsync(ct);

        var sql = "SELECT id, channel_type, caller_id, status, conversation_id, created_at FROM sessions";
        if (!string.IsNullOrWhiteSpace(status))
            sql += " WHERE status = $status";
        sql += " ORDER BY created_at DESC LIMIT $limit";

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (!string.IsNullOrWhiteSpace(status))
            cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$limit", limit);

        var sessions = new List<SessionSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            sessions.Add(new SessionSummary(
                Id: reader.GetString(0),
                ChannelType: reader.IsDBNull(1) ? null : reader.GetString(1),
                CallerId: reader.IsDBNull(2) ? null : reader.GetString(2),
                Status: reader.IsDBNull(3) ? null : reader.GetString(3),
                ConversationId: reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAt: reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return new ListResult(0, sessions, null);
    }

    internal async Task<GetResult> ExecuteGetAsync(string sessionId, CancellationToken ct)
    {
        var dbPath = _dbPathResolver();
        if (!File.Exists(dbPath))
            return new GetResult(1, null, $"Database not found: {dbPath}");

        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await connection.OpenAsync(ct);

        // Fetch session metadata
        await using var sessionCmd = connection.CreateCommand();
        sessionCmd.CommandText = "SELECT id, channel_type, caller_id, status, conversation_id, created_at, updated_at, metadata FROM sessions WHERE id = $id";
        sessionCmd.Parameters.AddWithValue("$id", sessionId);

        await using var reader = await sessionCmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new GetResult(1, null, $"Session '{sessionId}' not found.");

        var session = new SessionDetail(
            Id: reader.GetString(0),
            ChannelType: reader.IsDBNull(1) ? null : reader.GetString(1),
            CallerId: reader.IsDBNull(2) ? null : reader.GetString(2),
            Status: reader.IsDBNull(3) ? null : reader.GetString(3),
            ConversationId: reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt: reader.IsDBNull(5) ? null : reader.GetString(5),
            UpdatedAt: reader.IsDBNull(6) ? null : reader.GetString(6),
            Metadata: reader.IsDBNull(7) ? null : reader.GetString(7),
            MessageCount: 0,
            HasCompaction: false);

        // Fetch message count and compaction flag
        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*), MAX(is_compaction_summary) FROM session_history WHERE session_id = $id";
        countCmd.Parameters.AddWithValue("$id", sessionId);

        await using var countReader = await countCmd.ExecuteReaderAsync(ct);
        if (await countReader.ReadAsync(ct))
        {
            session = session with
            {
                MessageCount = countReader.GetInt32(0),
                HasCompaction = !countReader.IsDBNull(1) && countReader.GetInt32(1) == 1
            };
        }

        return new GetResult(0, session, null);
    }

    internal async Task<CompactionResult> ExecuteCompactionAsync(string sessionId, CancellationToken ct)
    {
        var dbPath = _dbPathResolver();
        if (!File.Exists(dbPath))
            return new CompactionResult(1, CompactionStatus.None, null, $"Database not found: {dbPath}");

        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await connection.OpenAsync(ct);

        // Verify session exists
        await using var existsCmd = connection.CreateCommand();
        existsCmd.CommandText = "SELECT COUNT(*) FROM sessions WHERE id = $id";
        existsCmd.Parameters.AddWithValue("$id", sessionId);
        var exists = (long)(await existsCmd.ExecuteScalarAsync(ct) ?? 0L) > 0;
        if (!exists)
            return new CompactionResult(1, CompactionStatus.None, null, $"Session '{sessionId}' not found.");

        // Fetch compaction summary
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT content FROM session_history WHERE session_id = $id AND is_compaction_summary = 1 ORDER BY timestamp DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$id", sessionId);

        var content = (string?)await cmd.ExecuteScalarAsync(ct);

        if (content is null)
            return new CompactionResult(0, CompactionStatus.None, null, null);

        if (string.IsNullOrWhiteSpace(content))
            return new CompactionResult(0, CompactionStatus.Empty, null, null);

        return new CompactionResult(0, CompactionStatus.HasContent, content, null);
    }

    internal async Task<StatsResult> ExecuteStatsAsync(CancellationToken ct)
    {
        var dbPath = _dbPathResolver();
        if (!File.Exists(dbPath))
            return new StatsResult(1, 0, 0, 0, 0, 0, $"Database not found: {dbPath}");

        var dbSize = new FileInfo(dbPath).Length;

        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) as total,
                SUM(CASE WHEN status = 'Active' THEN 1 ELSE 0 END) as active,
                SUM(CASE WHEN status = 'Sealed' THEN 1 ELSE 0 END) as sealed,
                SUM(CASE WHEN status NOT IN ('Active', 'Sealed') THEN 1 ELSE 0 END) as other
            FROM sessions
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new StatsResult(0, 0, 0, 0, 0, dbSize, null);

        var total = reader.GetInt32(0);
        var active = reader.GetInt32(1);
        var sealed_ = reader.GetInt32(2);
        var other = reader.GetInt32(3);

        return new StatsResult(0, total, active, sealed_, other, dbSize, null);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Print helpers
    // ──────────────────────────────────────────────────────────────────────

    private static void PrintListResult(ListResult result)
    {
        if (result.Error is not null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(result.Error)}");
            return;
        }

        if (result.Sessions.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No sessions found.[/]");
            return;
        }

        var table = new Table()
            .AddColumn("ID")
            .AddColumn("Channel")
            .AddColumn("Status")
            .AddColumn("Conversation")
            .AddColumn("Created");

        foreach (var s in result.Sessions)
        {
            table.AddRow(
                Markup.Escape(s.Id),
                Markup.Escape(s.ChannelType ?? "-"),
                FormatStatus(s.Status),
                Markup.Escape(TruncateId(s.ConversationId)),
                Markup.Escape(s.CreatedAt ?? "-"));
        }

        AnsiConsole.Write(table);
    }

    private static void PrintGetResult(GetResult result)
    {
        if (result.Error is not null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(result.Error)}");
            return;
        }

        var s = result.Session!;
        AnsiConsole.MarkupLine($"[bold]ID:[/]           {Markup.Escape(s.Id)}");
        AnsiConsole.MarkupLine($"[bold]Channel:[/]      {Markup.Escape(s.ChannelType ?? "-")}");
        AnsiConsole.MarkupLine($"[bold]Caller:[/]       {Markup.Escape(s.CallerId ?? "-")}");
        AnsiConsole.MarkupLine($"[bold]Status:[/]       {FormatStatus(s.Status)}");
        AnsiConsole.MarkupLine($"[bold]Conversation:[/] {Markup.Escape(s.ConversationId ?? "-")}");
        AnsiConsole.MarkupLine($"[bold]Created:[/]      {Markup.Escape(s.CreatedAt ?? "-")}");
        AnsiConsole.MarkupLine($"[bold]Updated:[/]      {Markup.Escape(s.UpdatedAt ?? "-")}");
        AnsiConsole.MarkupLine($"[bold]Messages:[/]     {s.MessageCount}");
        AnsiConsole.MarkupLine($"[bold]Compaction:[/]   {(s.HasCompaction ? "[green]yes[/]" : "[dim]none[/]")}");
    }

    private static void PrintCompactionResult(CompactionResult result)
    {
        if (result.Error is not null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(result.Error)}");
            return;
        }

        switch (result.Status)
        {
            case CompactionStatus.HasContent:
                AnsiConsole.MarkupLine("[bold]Compaction Summary:[/]");
                AnsiConsole.WriteLine(result.Content!);
                break;
            case CompactionStatus.Empty:
                AnsiConsole.MarkupLine("[yellow]Compaction entry exists but content is empty.[/]");
                break;
            case CompactionStatus.None:
                AnsiConsole.MarkupLine("[dim]No compaction has occurred for this session.[/]");
                break;
        }
    }

    private static void PrintStatsResult(StatsResult result)
    {
        if (result.Error is not null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(result.Error)}");
            return;
        }

        AnsiConsole.MarkupLine($"[bold]Total Sessions:[/] {result.TotalSessions}");
        AnsiConsole.MarkupLine($"[bold]Active:[/]         {result.ActiveSessions}");
        AnsiConsole.MarkupLine($"[bold]Sealed:[/]         {result.SealedSessions}");
        AnsiConsole.MarkupLine($"[bold]Other:[/]          {result.OtherSessions}");
        AnsiConsole.MarkupLine($"[bold]DB Size:[/]        {FormatBytes(result.DbSizeBytes)}");
    }

    private static string FormatStatus(string? status) => status switch
    {
        "Active" => "[green]Active[/]",
        "Sealed" => "[yellow]Sealed[/]",
        _ => Markup.Escape(status ?? "-")
    };

    private static string TruncateId(string? id) =>
        id is null ? "-" : id.Length > 16 ? id[..16] + "..." : id;

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
    };

    // ──────────────────────────────────────────────────────────────────────
    //  Result types
    // ──────────────────────────────────────────────────────────────────────

    internal record ListResult(int ExitCode, IReadOnlyList<SessionSummary> Sessions, string? Error);
    internal record GetResult(int ExitCode, SessionDetail? Session, string? Error);
    internal record CompactionResult(int ExitCode, CompactionStatus Status, string? Content, string? Error);
    internal record StatsResult(int ExitCode, int TotalSessions, int ActiveSessions, int SealedSessions, int OtherSessions, long DbSizeBytes, string? Error);

    internal record SessionSummary(string Id, string? ChannelType, string? CallerId, string? Status, string? ConversationId, string? CreatedAt);
    internal record SessionDetail(string Id, string? ChannelType, string? CallerId, string? Status, string? ConversationId, string? CreatedAt, string? UpdatedAt, string? Metadata, int MessageCount, bool HasCompaction);
}

internal enum CompactionStatus
{
    None,
    Empty,
    HasContent
}
