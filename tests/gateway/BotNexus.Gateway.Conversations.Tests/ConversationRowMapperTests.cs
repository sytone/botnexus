using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// Direct unit tests for <see cref="ConversationRowMapper"/> (issue #1627). The mappers are the
/// single source of truth for every positional ordinal each <c>conversations</c>-family reader
/// uses, so they are exercised here against a real in-memory <see cref="SqliteDataReader"/> whose
/// projection mirrors the production <c>SELECT</c> column order exactly. NULL-handling paths and
/// the now-removed <c>FieldCount</c> tolerance (a short/malformed row must throw loudly rather
/// than silently skip a field) are asserted explicitly.
/// </summary>
public sealed class ConversationRowMapperTests
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
    public void MapConversation_FullRow_MapsEveryColumn()
    {
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT
                'conv-1' AS id,
                'agent-a' AS agent_id,
                'Title here' AS title,
                'A purpose' AS purpose,
                1 AS is_default,
                'Archived' AS status,
                'sess-9' AS active_session_id,
                '{"k":"v"}' AS metadata,
                '2026-01-02T03:04:05.0000000+00:00' AS created_at,
                '2026-02-03T04:05:06.0000000+00:00' AS updated_at,
                'Be terse' AS instructions,
                '<b>hi</b>' AS canvas_html,
                'user:bob' AS initiator,
                'HumanAgent' AS kind,
                'world-7' AS world_id,
                1 AS is_pinned,
                '2026-03-04T05:06:07.0000000+00:00' AS pinned_at,
                '[{"id":"t"}]' AS todo_json,
                '{"q":1}' AS pending_ask_user_json
            """);

        var conversation = ConversationRowMapper.MapConversation(reader);

        conversation.ConversationId.ShouldBe(ConversationId.From("conv-1"));
        conversation.AgentId.ShouldBe(AgentId.From("agent-a"));
        conversation.Title.ShouldBe("Title here");
        conversation.Purpose.ShouldBe("A purpose");
        conversation.IsDefault.ShouldBeTrue();
        conversation.Status.ShouldBe(ConversationStatus.Archived);
        conversation.ActiveSessionId.ShouldBe(SessionId.From("sess-9"));
        conversation.Metadata["k"]!.ToString().ShouldBe("v");
        conversation.CreatedAt.ShouldBe(DateTimeOffset.Parse("2026-01-02T03:04:05.0000000+00:00"));
        conversation.UpdatedAt.ShouldBe(DateTimeOffset.Parse("2026-02-03T04:05:06.0000000+00:00"));
        conversation.Instructions.ShouldBe("Be terse");
        conversation.CanvasHtml.ShouldBe("<b>hi</b>");
        conversation.WorldId.ShouldBe("world-7");
        conversation.Kind.ShouldBe(ConversationKind.HumanAgent);
        conversation.IsPinned.ShouldBeTrue();
        conversation.PinnedAt.ShouldBe(DateTimeOffset.Parse("2026-03-04T05:06:07.0000000+00:00"));
        conversation.TodoJson.ShouldBe("[{\"id\":\"t\"}]");
        conversation.PendingAskUserJson.ShouldBe("{\"q\":1}");
    }

    [Fact]
    public void MapConversation_NullableColumnsNull_MapToNullDefaults()
    {
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT
                'conv-2' AS id,
                'agent-b' AS agent_id,
                'T' AS title,
                NULL AS purpose,
                0 AS is_default,
                'Active' AS status,
                NULL AS active_session_id,
                NULL AS metadata,
                '2026-01-02T03:04:05.0000000+00:00' AS created_at,
                '2026-01-02T03:04:05.0000000+00:00' AS updated_at,
                NULL AS instructions,
                NULL AS canvas_html,
                NULL AS initiator,
                NULL AS kind,
                NULL AS world_id,
                0 AS is_pinned,
                NULL AS pinned_at,
                NULL AS todo_json,
                NULL AS pending_ask_user_json
            """);

        var conversation = ConversationRowMapper.MapConversation(reader);

        conversation.Purpose.ShouldBeNull();
        conversation.ActiveSessionId.ShouldBeNull();
        conversation.Metadata.ShouldBeEmpty();
        conversation.Instructions.ShouldBeNull();
        conversation.CanvasHtml.ShouldBeNull();
        conversation.Initiator.ShouldBeNull();
        conversation.Kind.ShouldBe(ConversationKind.HumanAgent);
        conversation.WorldId.ShouldBe(string.Empty);
        conversation.IsPinned.ShouldBeFalse();
        conversation.PinnedAt.ShouldBeNull();
        conversation.TodoJson.ShouldBeNull();
        conversation.PendingAskUserJson.ShouldBeNull();
    }

    [Fact]
    public void MapConversation_ShortRowMissingTrailingColumns_ThrowsLoud()
    {
        // The FieldCount > N tolerance has been removed (#1627): a projection that omits an
        // expected column must surface as a thrown exception, not a silently-skipped field.
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT 'conv-3' AS id, 'agent-c' AS agent_id, 'T' AS title
            """);

        Should.Throw<Exception>(() => ConversationRowMapper.MapConversation(reader));
    }

    [Fact]
    public void MapSummary_FullRow_MapsEveryColumn()
    {
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT
                'conv-1' AS id,
                'agent-a' AS agent_id,
                'Sum title' AS title,
                'purpose!' AS purpose,
                1 AS is_default,
                'Active' AS status,
                'sess-2' AS active_session_id,
                '2026-01-02T03:04:05.0000000+00:00' AS created_at,
                '2026-02-03T04:05:06.0000000+00:00' AS updated_at,
                3 AS binding_count,
                'instr' AS instructions,
                'AgentAgent' AS kind,
                1 AS is_pinned,
                '2026-03-04T05:06:07.0000000+00:00' AS pinned_at
            """);

        var roster = new[] { new ParticipantSummary("Agent", "agent-a", "initiator") };
        var summary = ConversationRowMapper.MapSummary(reader, roster);

        summary.ConversationId.ShouldBe("conv-1");
        summary.AgentId.ShouldBe("agent-a");
        summary.Title.ShouldBe("Sum title");
        summary.Purpose.ShouldBe("purpose!");
        summary.IsDefault.ShouldBeTrue();
        summary.Status.ShouldBe("Active");
        summary.ActiveSessionId.ShouldBe("sess-2");
        summary.BindingCount.ShouldBe(3);
        summary.CreatedAt.ShouldBe(DateTimeOffset.Parse("2026-01-02T03:04:05.0000000+00:00"));
        summary.UpdatedAt.ShouldBe(DateTimeOffset.Parse("2026-02-03T04:05:06.0000000+00:00"));
        summary.Kind.ShouldBe("AgentAgent");
        summary.IsPinned.ShouldBeTrue();
        summary.PinnedAt.ShouldBe(DateTimeOffset.Parse("2026-03-04T05:06:07.0000000+00:00"));
        summary.Participants.ShouldBe(roster);
    }

    [Fact]
    public void MapSummary_NullableColumnsNull_MapToDefaults()
    {
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT
                'conv-2' AS id,
                'agent-b' AS agent_id,
                'T' AS title,
                NULL AS purpose,
                0 AS is_default,
                'Active' AS status,
                NULL AS active_session_id,
                '2026-01-02T03:04:05.0000000+00:00' AS created_at,
                '2026-01-02T03:04:05.0000000+00:00' AS updated_at,
                0 AS binding_count,
                NULL AS instructions,
                NULL AS kind,
                0 AS is_pinned,
                NULL AS pinned_at
            """);

        var summary = ConversationRowMapper.MapSummary(reader, roster: []);

        summary.Purpose.ShouldBeNull();
        summary.ActiveSessionId.ShouldBeNull();
        summary.BindingCount.ShouldBe(0);
        // A NULL kind falls back to the HumanAgent default, matching the prior inline behaviour.
        summary.Kind.ShouldBe(ConversationKind.HumanAgent.ToString());
        summary.IsPinned.ShouldBeFalse();
        summary.PinnedAt.ShouldBeNull();
        summary.Participants.ShouldBeEmpty();
    }

    [Fact]
    public void MapSummary_ShortRowMissingTrailingColumns_ThrowsLoud()
    {
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT 'conv-3' AS id, 'agent-c' AS agent_id, 'T' AS title
            """);

        Should.Throw<Exception>(() => ConversationRowMapper.MapSummary(reader, roster: []));
    }

    [Fact]
    public void MapBinding_Row_MapsEveryColumn()
    {
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT
                'bind-1' AS binding_id,
                'telegram' AS channel_type,
                '12345' AS channel_address,
                'Interactive' AS mode,
                'Single' AS threading_mode,
                'prefix' AS display_prefix,
                '2026-01-02T03:04:05.0000000+00:00' AS bound_at,
                '2026-02-03T04:05:06.0000000+00:00' AS last_inbound_at,
                '2026-03-04T05:06:07.0000000+00:00' AS last_outbound_at
            """);

        var binding = ConversationRowMapper.MapBinding(reader, offset: 0);

        binding.BindingId.ShouldBe(BindingId.From("bind-1"));
        binding.ChannelType.ShouldBe(ChannelKey.From("telegram"));
        binding.ChannelAddress.ShouldBe(ChannelAddress.From("12345"));
        binding.DisplayPrefix.ShouldBe("prefix");
        binding.BoundAt.ShouldBe(DateTimeOffset.Parse("2026-01-02T03:04:05.0000000+00:00"));
        binding.LastInboundAt.ShouldBe(DateTimeOffset.Parse("2026-02-03T04:05:06.0000000+00:00"));
        binding.LastOutboundAt.ShouldBe(DateTimeOffset.Parse("2026-03-04T05:06:07.0000000+00:00"));
    }

    [Fact]
    public void MapBinding_WithOffset_ShiftsOrdinals()
    {
        // The batched loader prefixes conversation_id, so a mapper offset of 1 must read the
        // binding columns from ordinal 1 onward - the single source of truth for both shapes.
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT
                'conv-1' AS conversation_id,
                'bind-2' AS binding_id,
                'signal' AS channel_type,
                '999' AS channel_address,
                'Interactive' AS mode,
                'Single' AS threading_mode,
                NULL AS display_prefix,
                '2026-01-02T03:04:05.0000000+00:00' AS bound_at,
                NULL AS last_inbound_at,
                NULL AS last_outbound_at
            """);

        var binding = ConversationRowMapper.MapBinding(reader, offset: 1);

        binding.BindingId.ShouldBe(BindingId.From("bind-2"));
        binding.ChannelType.ShouldBe(ChannelKey.From("signal"));
        binding.DisplayPrefix.ShouldBeNull();
        binding.LastInboundAt.ShouldBeNull();
        binding.LastOutboundAt.ShouldBeNull();
    }

    [Fact]
    public void MapParticipant_KnownKind_MapsCitizenAndRole()
    {
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT 'User' AS citizen_kind, 'bob' AS citizen_id, 'peer' AS role
            """);

        var participant = ConversationRowMapper.MapParticipant(reader, offset: 0);

        participant.ShouldNotBeNull();
        participant!.Role.ShouldBe("peer");
        participant.CitizenId.ShouldBe(CitizenId.Of(UserId.From("bob")));
    }

    [Fact]
    public void MapParticipant_UnknownKind_ReturnsNull()
    {
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT 'Martian' AS citizen_kind, 'x' AS citizen_id, NULL AS role
            """);

        ConversationRowMapper.MapParticipant(reader, offset: 0).ShouldBeNull();
    }

    [Fact]
    public void MapParticipantSummary_Row_MapsKindIdRole()
    {
        // The active-roster query projects conversation_id first, then kind/id/role.
        using var connection = OpenMemory();
        using var reader = Read(connection, """
            SELECT 'conv-1' AS conversation_id, 'Agent' AS citizen_kind, 'agent-z' AS citizen_id, 'initiator' AS role
            """);

        var summary = ConversationRowMapper.MapParticipantSummary(reader, offset: 1);

        summary.Kind.ShouldBe("Agent");
        summary.Id.ShouldBe("agent-z");
        summary.Role.ShouldBe("initiator");
    }
}
