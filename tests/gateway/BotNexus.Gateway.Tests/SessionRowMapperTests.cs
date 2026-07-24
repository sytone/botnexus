using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Direct unit tests for <see cref="SessionRowMapper"/> (issue #1627). Each mapper is the single
/// source of truth for the positional ordinals of one <c>sessions</c>-family reader, so the tests
/// drive a real in-memory <see cref="SqliteDataReader"/> whose projection mirrors the production
/// <c>SELECT</c> column order exactly. NULL-handling paths and the now-removed <c>FieldCount</c>
/// tolerance (a short/malformed history row must throw loudly, not silently drop a field) are
/// asserted explicitly.
/// </summary>
public sealed class SessionRowMapperTests
{
    private static SqliteConnection OpenMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static SqliteDataReader Read(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        var reader = command.ExecuteReader();
        reader.Read().ShouldBeTrue();
        return reader;
    }

    [Fact]
    public void MapSession_FullRow_MapsEveryColumn()
    {
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT
                'sess-1' AS id,
                'telegram' AS channel_type,
                'caller-7' AS caller_id,
                'user-agent' AS session_type,
                '[]' AS participants_json,
                'Active' AS status,
                '{"k":"v"}' AS metadata,
                '2026-01-02T03:04:05.0000000+00:00' AS created_at,
                '2026-02-03T04:05:06.0000000+00:00' AS updated_at,
                'conv-9' AS conversation_id
            """);

        var row = SessionRowMapper.MapSession(reader);

        row.Session.SessionId.ShouldBe(SessionId.From("sess-1"));
        row.Session.ChannelType.ShouldBe(ChannelKey.From("telegram"));
        row.Session.SessionType.ShouldBe(SessionType.UserAgent);
        row.Session.Status.ShouldBe(SessionStatus.Active);
        row.Session.Metadata["k"]!.ToString().ShouldBe("v");
        row.Session.CreatedAt.ShouldBe(DateTimeOffset.Parse("2026-01-02T03:04:05.0000000+00:00"));
        row.Session.UpdatedAt.ShouldBe(DateTimeOffset.Parse("2026-02-03T04:05:06.0000000+00:00"));
        // #1627: SessionRow surfaces the row's updated_at directly so the store can re-stamp
        // it without reaching through to Session.UpdatedAt (F-9). It must equal Session.UpdatedAt.
        row.UpdatedAt.ShouldBe(DateTimeOffset.Parse("2026-02-03T04:05:06.0000000+00:00"));
        row.UpdatedAt.ShouldBe(row.Session.UpdatedAt);
        row.Session.ConversationId.ShouldBe(ConversationId.From("conv-9"));
        row.CallerId.ShouldBe("caller-7");
    }

    [Fact]
    public void MapSession_NullableColumnsNull_MapToDefaults()
    {
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT
                'sess-2' AS id,
                NULL AS channel_type,
                NULL AS caller_id,
                NULL AS session_type,
                NULL AS participants_json,
                NULL AS status,
                NULL AS metadata,
                '2026-01-02T03:04:05.0000000+00:00' AS created_at,
                '2026-01-02T03:04:05.0000000+00:00' AS updated_at,
                NULL AS conversation_id
            """);

        var row = SessionRowMapper.MapSession(reader);

        row.Session.ChannelType.ShouldBeNull();
        row.CallerId.ShouldBeNull();
        row.Session.Status.ShouldBe(SessionStatus.Active);
        row.Session.Metadata.ShouldBeEmpty();
        // A NULL conversation_id leaves ConversationId uninitialized (the unset sentinel) - it
        // is not assigned, exactly as the prior inline mapping behaved.
        row.Session.ConversationId.IsInitialized().ShouldBeFalse();
    }

    [Fact]
    public void MapSession_StatusClosed_NormalizesToSealed()
    {
        // ParseStatus maps the legacy "closed" string to SessionStatus.Sealed.
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT
                'sess-3' AS id, NULL AS channel_type, NULL AS caller_id, NULL AS session_type,
                NULL AS participants_json, 'closed' AS status, NULL AS metadata,
                '2026-01-02T03:04:05.0000000+00:00' AS created_at,
                '2026-01-02T03:04:05.0000000+00:00' AS updated_at, NULL AS conversation_id
            """);

        var row = SessionRowMapper.MapSession(reader);

        row.Session.Status.ShouldBe(SessionStatus.Sealed);
    }

    [Fact]
    public void MapHistoryEntry_FullRow_MapsEveryColumn()
    {
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT
                'assistant' AS role,
                'hello' AS content,
                '2026-01-02T03:04:05.0000000+00:00' AS timestamp,
                'web_search' AS tool_name,
                'call-1' AS tool_call_id,
                1 AS is_compaction_summary,
                '{"q":1}' AS tool_args,
                1 AS tool_is_error,
                1 AS is_crash_sentinel,
                1 AS is_history,
                'cron' AS trigger_type,
                'thinking...' AS thinking_content,
                'subagent-response' AS message_kind
            """);

        var entry = SessionRowMapper.MapHistoryEntry(reader);

        entry.Role.ShouldBe(MessageRole.Assistant);
        entry.Content.ShouldBe("hello");
        entry.Timestamp.ShouldBe(DateTimeOffset.Parse("2026-01-02T03:04:05.0000000+00:00"));
        entry.ToolName.ShouldBe("web_search");
        entry.ToolCallId.ShouldBe("call-1");
        entry.IsCompactionSummary.ShouldBeTrue();
        entry.ToolArgs.ShouldBe("{\"q\":1}");
        entry.ToolIsError.ShouldBeTrue();
        entry.IsCrashSentinel.ShouldBeTrue();
        entry.IsHistory.ShouldBeTrue();
        entry.Trigger.ShouldBe(TriggerType.Cron);
        entry.ThinkingContent.ShouldBe("thinking...");
        // #2149: the orthogonal typed message kind maps from the message_kind column.
        entry.Kind.ShouldBe(MessageKind.SubAgentResponse);
        entry.ResolveKind().ShouldBe(MessageKind.SubAgentResponse);
    }

    [Fact]
    public void MapHistoryEntry_NullableColumnsNull_MapToDefaults()
    {
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT
                NULL AS role, NULL AS content, NULL AS timestamp, NULL AS tool_name,
                NULL AS tool_call_id, 0 AS is_compaction_summary, NULL AS tool_args,
                0 AS tool_is_error, 0 AS is_crash_sentinel, 0 AS is_history,
                NULL AS trigger_type, NULL AS thinking_content, NULL AS message_kind
            """);

        var entry = SessionRowMapper.MapHistoryEntry(reader);

        entry.Role.ShouldBe(MessageRole.User);
        entry.Content.ShouldBe(string.Empty);
        entry.ToolName.ShouldBeNull();
        entry.ToolCallId.ShouldBeNull();
        entry.IsCompactionSummary.ShouldBeFalse();
        entry.ToolArgs.ShouldBeNull();
        entry.ToolIsError.ShouldBeFalse();
        entry.IsCrashSentinel.ShouldBeFalse();
        entry.IsHistory.ShouldBeFalse();
        entry.Trigger.ShouldBeNull();
        entry.ThinkingContent.ShouldBeNull();
        // #2149: a NULL message_kind maps to a null Kind, resolving to MessageKind.Message.
        entry.Kind.ShouldBeNull();
        entry.ResolveKind().ShouldBe(MessageKind.Message);
    }

    [Fact]
    public void MapHistoryEntry_ShortRowMissingTrailingColumns_ThrowsLoud()
    {
        // The FieldCount > N tolerance has been removed (#1627): a projection missing an
        // expected history column must throw rather than silently drop the field.
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT 'user' AS role, 'hi' AS content, NULL AS timestamp
            """);

        Should.Throw<Exception>(() => SessionRowMapper.MapHistoryEntry(reader));
    }

    [Fact]
    public void MapSummaryRow_FullRow_MapsEveryColumn()
    {
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT
                'sess-1' AS id,
                'signal' AS channel_type,
                'user-agent' AS session_type,
                'Active' AS status,
                '2026-01-02T03:04:05.0000000+00:00' AS created_at,
                '2026-02-03T04:05:06.0000000+00:00' AS updated_at,
                'conv-1' AS conversation_id,
                5 AS message_count
            """);

        var row = SessionRowMapper.MapSummaryRow(reader);

        row.Id.ShouldBe("sess-1");
        row.Channel.ShouldBe(ChannelKey.From("signal"));
        row.Type.ShouldBe(SessionType.UserAgent);
        row.Status.ShouldBe(SessionStatus.Active);
        row.Created.ShouldBe(DateTimeOffset.Parse("2026-01-02T03:04:05.0000000+00:00"));
        row.Updated.ShouldBe(DateTimeOffset.Parse("2026-02-03T04:05:06.0000000+00:00"));
        row.ConversationId.ShouldBe("conv-1");
        row.Count.ShouldBe(5);
    }

    [Fact]
    public void MapSummaryRow_NullableColumnsNull_MapToDefaults()
    {
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT
                'sess-2' AS id, NULL AS channel_type, NULL AS session_type, NULL AS status,
                '2026-01-02T03:04:05.0000000+00:00' AS created_at,
                '2026-01-02T03:04:05.0000000+00:00' AS updated_at,
                NULL AS conversation_id, 0 AS message_count
            """);

        var row = SessionRowMapper.MapSummaryRow(reader);

        row.Channel.ShouldBeNull();
        row.Status.ShouldBe(SessionStatus.Active);
        row.ConversationId.ShouldBeNull();
        row.Count.ShouldBe(0);
    }

    [Fact]
    public void MapSubAgentSession_FullRow_MapsEveryColumn()
    {
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT
                'sub-1' AS id,
                'parent-sess' AS parent_session_id,
                'parent-agent' AS parent_agent_id,
                'child-agent' AS child_agent_id,
                'coder' AS archetype,
                '2026-01-02T03:04:05.0000000+00:00' AS started_at,
                '2026-02-03T04:05:06.0000000+00:00' AS ended_at,
                'completed' AS status
            """);

        var summary = SessionRowMapper.MapSubAgentSession(reader);

        summary.SubAgentId.ShouldBe("sub-1");
        summary.ParentSessionId.ShouldBe("parent-sess");
        summary.ParentAgentId.ShouldBe("parent-agent");
        summary.ChildAgentId.ShouldBe("child-agent");
        summary.Archetype.ShouldBe("coder");
        summary.StartedAt.ShouldBe(DateTimeOffset.Parse("2026-01-02T03:04:05.0000000+00:00"));
        summary.EndedAt.ShouldBe(DateTimeOffset.Parse("2026-02-03T04:05:06.0000000+00:00"));
        summary.Status.ShouldBe("completed");
    }

    [Fact]
    public void MapSubAgentSession_NullableColumnsNull_MapToDefaults()
    {
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT
                'sub-2' AS id, 'p' AS parent_session_id, 'pa' AS parent_agent_id,
                'ca' AS child_agent_id, NULL AS archetype,
                '2026-01-02T03:04:05.0000000+00:00' AS started_at,
                NULL AS ended_at, 'running' AS status
            """);

        var summary = SessionRowMapper.MapSubAgentSession(reader);

        summary.Archetype.ShouldBeNull();
        summary.EndedAt.ShouldBeNull();
        summary.Status.ShouldBe("running");
    }
}
