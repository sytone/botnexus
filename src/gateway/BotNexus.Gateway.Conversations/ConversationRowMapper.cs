using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Conversations;

/// <summary>
/// Single source of truth for every positional <see cref="SqliteDataReader"/> mapping used by
/// <see cref="SqliteConversationStore"/> (issue #1627). Each method owns the ordinals for exactly
/// one row shape so the magic column indexes are never duplicated across the store's read methods.
/// </summary>
/// <remarks>
/// <para>
/// Ordinals are resolved by column name (<see cref="SqliteDataReader.GetOrdinal(string)"/>) once
/// per mapper call rather than as bare integer literals, so the C# mapping is self-documenting and
/// resilient to additive SELECT changes - but the SELECT column set itself is unchanged; this is a
/// behaviour-preserving extraction, not a schema or projection change.
/// </para>
/// <para>
/// The runtime <c>reader.FieldCount &gt; N</c> defensive probing that previously guarded the
/// trailing columns has been removed (#1627). Schema creation (<c>EnsureCreatedAsync</c> /
/// migrations) always runs before any read, so a missing expected column is a genuine fault and
/// must surface as a thrown <see cref="IndexOutOfRangeException"/> / <see cref="InvalidOperationException"/>
/// from the reader rather than being silently tolerated as a skipped field.
/// </para>
/// </remarks>
internal static class ConversationRowMapper
{
    /// <summary>
    /// Maps the current row of a <c>conversations</c> reader to a <see cref="Conversation"/>. The
    /// column projection must match the <c>SELECT</c> in
    /// <see cref="SqliteConversationStore"/>'s single-row and batched conversation loaders so both
    /// paths hydrate identically. Child collections (bindings / participants) are NOT populated
    /// here - the caller attaches them.
    /// </summary>
    internal static Conversation MapConversation(SqliteDataReader reader)
    {
        var conversation = new Conversation
        {
            ConversationId = ConversationId.From(reader.GetString(reader.GetOrdinal("id"))),
            AgentId = AgentId.From(reader.GetString(reader.GetOrdinal("agent_id"))),
            Title = reader.GetString(reader.GetOrdinal("title")),
            Purpose = GetNullableString(reader, "purpose"),
            IsDefault = reader.GetInt64(reader.GetOrdinal("is_default")) != 0,
            Status = ParseConversationStatus(reader.GetString(reader.GetOrdinal("status"))),
            Metadata = DeserializeMetadata(GetNullableString(reader, "metadata")),
            CreatedAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("updated_at"))),
            Instructions = GetNullableString(reader, "instructions"),
            CanvasHtml = GetNullableString(reader, "canvas_html"),
            Initiator = DeserializeInitiator(GetNullableString(reader, "initiator")),
            Kind = ParseConversationKind(GetNullableString(reader, "kind")),
            WorldId = GetNullableString(reader, "world_id") ?? string.Empty,
            IsPinned = !reader.IsDBNull(reader.GetOrdinal("is_pinned")) && reader.GetInt64(reader.GetOrdinal("is_pinned")) != 0,
            PinnedAt = GetNullableTimestamp(reader, "pinned_at"),
            TodoJson = GetNullableString(reader, "todo_json"),
            PendingAskUserJson = GetNullableString(reader, "pending_ask_user_json")
        };

        var activeSessionOrdinal = reader.GetOrdinal("active_session_id");
        if (!reader.IsDBNull(activeSessionOrdinal))
            conversation.ActiveSessionId = SessionId.From(reader.GetString(activeSessionOrdinal));

        return conversation;
    }

    /// <summary>
    /// Maps the current row of the active-conversation summary <c>SELECT</c> (used by
    /// <c>GetSummariesAsync</c>) to a <see cref="ConversationSummary"/>. The supplied
    /// <paramref name="roster"/> is the participant list resolved by the caller's batch query and
    /// is attached verbatim, mirroring the prior inline shape.
    /// </summary>
    internal static ConversationSummary MapSummary(SqliteDataReader reader, IReadOnlyList<ParticipantSummary> roster)
        => new(
            reader.GetString(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("agent_id")),
            reader.GetString(reader.GetOrdinal("title")),
            reader.GetInt64(reader.GetOrdinal("is_default")) != 0,
            reader.GetString(reader.GetOrdinal("status")),
            GetNullableString(reader, "active_session_id"),
            checked((int)reader.GetInt64(reader.GetOrdinal("binding_count"))),
            ParseTimestamp(reader.GetString(reader.GetOrdinal("created_at"))),
            ParseTimestamp(reader.GetString(reader.GetOrdinal("updated_at"))),
            GetNullableString(reader, "purpose"),
            GetNullableString(reader, "kind") ?? ConversationKind.HumanAgent.ToString(),
            !reader.IsDBNull(reader.GetOrdinal("is_pinned")) && reader.GetInt64(reader.GetOrdinal("is_pinned")) != 0,
            GetNullableTimestamp(reader, "pinned_at"),
            roster);

    /// <summary>
    /// Maps a <c>conversation_participants</c> row (<c>citizen_kind, citizen_id, role</c>) to a
    /// <see cref="SessionParticipant"/>, or returns <see langword="null"/> when the citizen kind is
    /// unknown (the same skip behaviour as the per-conversation read). <paramref name="offset"/>
    /// shifts the ordinals so the batched loader's <c>conversation_id</c>-prefixed projection can
    /// share this mapper.
    /// </summary>
    internal static SessionParticipant? MapParticipant(SqliteDataReader reader, int offset)
    {
        var kindRaw = reader.GetString(offset + 0);
        var idValue = reader.GetString(offset + 1);
        var role = reader.IsDBNull(offset + 2) ? null : reader.GetString(offset + 2);
        if (!TryComposeCitizen(kindRaw, idValue, out var citizen))
            return null;
        return new SessionParticipant
        {
            CitizenId = citizen,
            Role = role
        };
    }

    /// <summary>
    /// Maps a roster row from the active-participant batch query to a lightweight
    /// <see cref="ParticipantSummary"/>. <paramref name="offset"/> shifts the ordinals past the
    /// leading <c>conversation_id</c> grouping column projected by that query.
    /// </summary>
    internal static ParticipantSummary MapParticipantSummary(SqliteDataReader reader, int offset)
        => new(
            reader.GetString(offset + 0),
            reader.GetString(offset + 1),
            reader.IsDBNull(offset + 2) ? null : reader.GetString(offset + 2));

    /// <summary>
    /// Maps a <c>conversation_bindings</c> row to a <see cref="ChannelBinding"/>. <paramref name="offset"/>
    /// shifts the column ordinals so a projection that prefixes <c>conversation_id</c> (the batched
    /// loader) can reuse the same mapper as the per-conversation projection. The trailing column
    /// order must stay identical to the per-conversation bindings <c>SELECT</c>.
    /// </summary>
    internal static ChannelBinding MapBinding(SqliteDataReader reader, int offset) => new()
    {
        BindingId = BindingId.From(reader.GetString(offset + 0)),
        ChannelType = ChannelKey.From(reader.GetString(offset + 1)),
        ChannelAddress = ChannelAddress.From(reader.GetString(offset + 2)),
        Mode = ParseBindingMode(reader.GetString(offset + 3)),
        ThreadingMode = ParseThreadingMode(reader.GetString(offset + 4)),
        DisplayPrefix = reader.IsDBNull(offset + 5) ? null : reader.GetString(offset + 5),
        BoundAt = ParseTimestamp(reader.GetString(offset + 6)),
        LastInboundAt = reader.IsDBNull(offset + 7) ? null : ParseTimestamp(reader.GetString(offset + 7)),
        LastOutboundAt = reader.IsDBNull(offset + 8) ? null : ParseTimestamp(reader.GetString(offset + 8))
    };

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? GetNullableTimestamp(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));
    }

    private static DateTimeOffset ParseTimestamp(string? value)
        => DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

    private static ConversationStatus ParseConversationStatus(string? value)
        => Enum.TryParse<ConversationStatus>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ConversationStatus.Active;

    private static ConversationKind ParseConversationKind(string? value)
        => Enum.TryParse<ConversationKind>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ConversationKind.HumanAgent;

    private static BindingMode ParseBindingMode(string? value)
        => Enum.TryParse<BindingMode>(value, ignoreCase: true, out var parsed)
            ? parsed
            : BindingMode.Interactive;

    private static ThreadingMode ParseThreadingMode(string? value)
        => Enum.TryParse<ThreadingMode>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ThreadingMode.Single;

    private static Dictionary<string, object?> DeserializeMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return [];

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataJson, SqliteConversationStore.JsonOptions) ?? [];
    }

    private static CitizenId? DeserializeInitiator(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return CitizenId.TryParse(raw, out var citizen) ? citizen : null;
    }

    private static bool TryComposeCitizen(string kindRaw, string idValue, out CitizenId citizen)
    {
        citizen = default;
        if (!Enum.TryParse<CitizenKind>(kindRaw, ignoreCase: true, out var kind))
            return false;
        switch (kind)
        {
            case CitizenKind.User:
                citizen = CitizenId.Of(UserId.From(idValue));
                return true;
            case CitizenKind.Agent:
                citizen = CitizenId.Of(AgentId.From(idValue));
                return true;
            default:
                return false;
        }
    }
}
