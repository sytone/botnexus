using BotNexus.Probe.LogIngestion;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace BotNexus.Probe.Tests;

public sealed class SessionDbReaderTests
{
    [Fact]
    public async Task SessionDbReader_ReadsSessionMetadataAndHistory()
    {
        var dbPath = CreateTempDbPath();
        await SeedAsync(dbPath);

        using var reader = new SessionDbReader(dbPath);

        var sessions = await reader.ListSessionsAsync(agentId: "nova");
        sessions.Should().ContainSingle();
        sessions[0].Id.Should().Be("s-1");
        sessions[0].MessageCount.Should().Be(2);

        var detail = await reader.GetSessionAsync("s-1");
        detail.Should().NotBeNull();
        detail!.ChannelType.Should().Be("signalr");

        var history = await reader.GetHistoryAsync("s-1");
        history.Should().HaveCount(2);
        history[1].ToolName.Should().Be("search");

        var matches = await reader.SearchHistoryAsync("world", "s-1");
        matches.Should().ContainSingle();
        matches[0].Content.Should().Contain("world");
    }

    [Fact]
    public async Task SessionDbReader_ReturnsStatusCounts()
    {
        var dbPath = CreateTempDbPath();
        await SeedAsync(dbPath);

        using var reader = new SessionDbReader(dbPath);
        var counts = await reader.GetCountsAsync();

        counts.Total.Should().Be(2);
        counts.Active.Should().Be(1);
        counts.Sealed.Should().Be(1);
        counts.Expired.Should().Be(0);
        counts.Suspended.Should().Be(0);
    }

    private static string CreateTempDbPath()
    {
        var parent = Path.Combine(Path.GetTempPath(), "BotNexus.Probe.Tests", "sqlite");
        Directory.CreateDirectory(parent);
        return Path.Combine(parent, $"{Guid.NewGuid():N}.db");
    }

    private static async Task SeedAsync(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };

        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync();

        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                agent_id TEXT,
                channel_type TEXT,
                caller_id TEXT,
                session_type TEXT,
                status TEXT,
                participants_json TEXT,
                metadata TEXT,
                created_at TEXT,
                updated_at TEXT
            );

            CREATE TABLE session_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT,
                role TEXT,
                content TEXT,
                timestamp TEXT,
                tool_name TEXT,
                tool_call_id TEXT,
                is_compaction_summary INTEGER
            );
            """;
        await schema.ExecuteNonQueryAsync();

        await using var insertSessions = connection.CreateCommand();
        insertSessions.CommandText = """
            INSERT INTO sessions (id, agent_id, channel_type, caller_id, session_type, status, participants_json, metadata, created_at, updated_at)
            VALUES
            ('s-1', 'nova', 'signalr', 'caller-a', 'user-agent', 'Active', '["nova","user"]', '{"foo":"bar"}', '2026-04-14T05:50:00Z', '2026-04-14T05:55:00Z'),
            ('s-2', 'aurum', 'cron', 'scheduler', 'cron', 'Sealed', '["aurum"]', '{"job":"x"}', '2026-04-13T05:50:00Z', '2026-04-13T05:55:00Z');
            """;
        await insertSessions.ExecuteNonQueryAsync();

        await using var insertHistory = connection.CreateCommand();
        insertHistory.CommandText = """
            INSERT INTO session_history (session_id, role, content, timestamp, tool_name, tool_call_id, is_compaction_summary)
            VALUES
            ('s-1', 'user', 'hello world', '2026-04-14T05:51:00Z', NULL, NULL, 0),
            ('s-1', 'tool', 'tool output', '2026-04-14T05:52:00Z', 'search', 'call-1', 1),
            ('s-2', 'assistant', 'sealed session', '2026-04-13T05:51:00Z', NULL, NULL, 0);
            """;
        await insertHistory.ExecuteNonQueryAsync();
    }
}
