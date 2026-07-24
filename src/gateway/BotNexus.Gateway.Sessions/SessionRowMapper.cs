using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Data.Sqlite;
using ChannelKey = BotNexus.Domain.Primitives.ChannelKey;
using ConversationId = BotNexus.Domain.Primitives.ConversationId;
using MessageRole = BotNexus.Domain.Primitives.MessageRole;
using SessionId = BotNexus.Domain.Primitives.SessionId;
using SessionType = BotNexus.Domain.Primitives.SessionType;
using TriggerType = BotNexus.Domain.Primitives.TriggerType;
using MessageKind = BotNexus.Domain.Primitives.MessageKind;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Single source of truth for every positional <see cref="SqliteDataReader"/> mapping used by
/// <see cref="SqliteSessionStore"/> (issue #1627). Each method owns the ordinals for exactly one
/// row shape so the magic column indexes are never duplicated across the store's read methods.
/// </summary>
/// <remarks>
/// <para>
/// The runtime <c>reader.FieldCount &gt; N</c> defensive probing that previously guarded the
/// trailing <c>session_history</c> columns has been removed (#1627). Schema creation
/// (<c>EnsureCreatedAsync</c> / migrations) always runs before any read, so a missing expected
/// column is a genuine fault and must surface as a thrown exception from the reader rather than
/// being silently tolerated as a skipped field.
/// </para>
/// <para>
/// These mappers are deliberately per-store and have no cross-cutting abstraction - they are not
/// the persistence abstraction tracked by #1509/#1436; they exist only to centralise this store's
/// ordinals and remove the dead FieldCount probing.
/// </para>
/// </remarks>
internal static class SessionRowMapper
{
    /// <summary>
    /// The mapped result of a single <c>sessions</c> row: the hydrated domain <see cref="Session"/>
    /// plus the raw <c>caller_id</c>, which the store stamps onto the wrapping
    /// <see cref="GatewaySession"/> (it is not a property of <see cref="Session"/> itself). The
    /// row's <c>updated_at</c> is surfaced directly (<see cref="UpdatedAt"/>) so the store can
    /// re-stamp the persisted timestamp after AddEntries without reaching through to
    /// <c>Session.UpdatedAt</c> (F-9 / Phase 7).
    /// </summary>
    internal readonly record struct SessionRow(Session Session, string? CallerId, DateTimeOffset UpdatedAt);

    /// <summary>
    /// A single raw <c>sessions</c> summary row (transcript-free listing). The store applies the
    /// <c>updatedAfter</c> filter and resolves the owning agent from the conversation store after
    /// the reader is drained, so the mapper returns the parsed row verbatim.
    /// </summary>
    internal readonly record struct SessionSummaryRow(
        string Id,
        ChannelKey? Channel,
        SessionType Type,
        SessionStatus Status,
        DateTimeOffset Created,
        DateTimeOffset Updated,
        string? ConversationId,
        int Count);

    /// <summary>
    /// Maps the current row of the per-session <c>SELECT</c> in <c>LoadSessionAsync</c> to a
    /// <see cref="Session"/> (plus its caller id). The <c>participants_json</c> column is
    /// intentionally read-and-discarded (P9-F): participants are no longer persisted on Session.
    /// A NULL <c>conversation_id</c> leaves <see cref="Session.ConversationId"/> at its uninitialized
    /// sentinel (writing <c>default</c> is prohibited by the Vogen analyzer), exactly as before.
    /// </summary>
    internal static SessionRow MapSession(SqliteDataReader reader)
    {
        var sessionId = SessionId.From(reader.GetString(reader.GetOrdinal("id")));
        var channelType = GetNullableChannelKey(reader, "channel_type");
        var sessionType = ParseSessionType(GetNullableString(reader, "session_type"), sessionId, channelType);
        var status = ParseStatus(GetNullableString(reader, "status"));
        var metadata = DeserializeMetadata(GetNullableString(reader, "metadata"));
        var createdAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("created_at")));
        var updatedAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("updated_at")));

        var session = new Session
        {
            SessionId = sessionId,
            ChannelType = channelType,
            SessionType = sessionType,
            Status = status,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Metadata = metadata
        };

        var conversationOrdinal = reader.GetOrdinal("conversation_id");
        if (!reader.IsDBNull(conversationOrdinal))
            session.ConversationId = ConversationId.From(reader.GetString(conversationOrdinal));

        return new SessionRow(session, GetNullableString(reader, "caller_id"), updatedAt);
    }

    /// <summary>
    /// Maps the current row of the <c>session_history</c> <c>SELECT</c> in <c>LoadSessionAsync</c>
    /// to a <see cref="SessionEntry"/>. Every column in that projection is required; the prior
    /// <c>FieldCount &gt; N</c> tolerance on the trailing columns has been removed (#1627).
    /// </summary>
    internal static SessionEntry MapHistoryEntry(SqliteDataReader reader)
        => new()
        {
            Role = MessageRole.FromString(reader.IsDBNull(reader.GetOrdinal("role")) ? "user" : reader.GetString(reader.GetOrdinal("role"))),
            Content = GetNullableString(reader, "content") ?? string.Empty,
            Timestamp = ParseTimestamp(GetNullableString(reader, "timestamp")),
            ToolName = GetNullableString(reader, "tool_name"),
            ToolCallId = GetNullableString(reader, "tool_call_id"),
            IsCompactionSummary = GetBool(reader, "is_compaction_summary"),
            ToolArgs = GetNullableString(reader, "tool_args"),
            ToolIsError = GetBool(reader, "tool_is_error"),
            IsCrashSentinel = GetBool(reader, "is_crash_sentinel"),
            IsHistory = GetBool(reader, "is_history"),
            Trigger = GetNullableString(reader, "trigger_type") is { } trigger ? TriggerType.FromString(trigger) : null,
            ThinkingContent = GetNullableString(reader, "thinking_content"),
            // #2149: a NULL/absent message_kind reads back as an unstamped (null) Kind so an entry
            // saved with the default kind round-trips as null; SessionEntry.ResolveKind() maps null
            // to MessageKind.Message for consumers, keeping legacy rows and ordinary responses safe.
            Kind = GetNullableString(reader, "message_kind") is { } messageKind ? MessageKind.FromString(messageKind) : null
        };

    /// <summary>
    /// Maps the current row of the transcript-free summary <c>SELECT</c> in <c>ListSummariesAsync</c>
    /// to a <see cref="SessionSummaryRow"/>.
    /// </summary>
    internal static SessionSummaryRow MapSummaryRow(SqliteDataReader reader)
    {
        var id = reader.GetString(reader.GetOrdinal("id"));
        var channel = GetNullableChannelKey(reader, "channel_type");
        var type = ParseSessionType(GetNullableString(reader, "session_type"), SessionId.From(id), channel);
        var status = ParseStatus(GetNullableString(reader, "status"));
        var created = ParseTimestamp(reader.GetString(reader.GetOrdinal("created_at")));
        var updated = ParseTimestamp(reader.GetString(reader.GetOrdinal("updated_at")));
        var conversationId = GetNullableString(reader, "conversation_id");
        var messageCountOrdinal = reader.GetOrdinal("message_count");
        var count = reader.IsDBNull(messageCountOrdinal) ? 0 : (int)reader.GetInt64(messageCountOrdinal);
        return new SessionSummaryRow(id, channel, type, status, created, updated, conversationId, count);
    }

    /// <summary>
    /// Maps the current row of the <c>sub_agent_sessions</c> <c>SELECT</c> in
    /// <c>ListSubAgentSessionsAsync</c> to a <see cref="SubAgentSessionSummary"/>.
    /// </summary>
    internal static SubAgentSessionSummary MapSubAgentSession(SqliteDataReader reader)
        => new()
        {
            SubAgentId = reader.GetString(reader.GetOrdinal("id")),
            ParentSessionId = reader.GetString(reader.GetOrdinal("parent_session_id")),
            ParentAgentId = reader.GetString(reader.GetOrdinal("parent_agent_id")),
            ChildAgentId = reader.GetString(reader.GetOrdinal("child_agent_id")),
            Archetype = GetNullableString(reader, "archetype"),
            StartedAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("started_at"))),
            EndedAt = GetNullableTimestamp(reader, "ended_at"),
            Status = reader.GetString(reader.GetOrdinal("status"))
        };

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static ChannelKey? GetNullableChannelKey(SqliteDataReader reader, string column)
    {
        var raw = GetNullableString(reader, column);
        return raw is null ? (ChannelKey?)null : ChannelKey.From(raw);
    }

    private static DateTimeOffset? GetNullableTimestamp(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));
    }

    private static bool GetBool(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return !reader.IsDBNull(ordinal) && reader.GetInt64(ordinal) != 0;
    }

    private static DateTimeOffset ParseTimestamp(string? timestamp)
        => DateTimeOffset.TryParse(timestamp, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

    private static SessionStatus ParseStatus(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "closed" => SessionStatus.Sealed,
            _ when Enum.TryParse<SessionStatus>(status, ignoreCase: true, out var parsed) => parsed,
            _ => SessionStatus.Active
        };

    private static SessionType ParseSessionType(string? raw, SessionId sessionId, ChannelKey? channelType)
    {
        if (!string.IsNullOrWhiteSpace(raw))
            return SessionType.FromString(raw);

        return SessionStoreBase.InferSessionType(sessionId, channelType);
    }

    private static Dictionary<string, object?> DeserializeMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return [];

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataJson, SqliteSessionStore.JsonOptions) ?? [];
    }
}
