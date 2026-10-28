using System.Globalization;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Gateway.Sessions;

public sealed partial class SqliteSessionStore
{
    /// <inheritdoc />
    public override async Task<SessionHistoryScanPage> ScanHistoryAsync(SessionId sessionId, SessionHistoryScan scan,
        CancellationToken cancellationToken = default)
    {
        scan.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var ownerCommand = connection.CreateCommand();
        ownerCommand.CommandText = "SELECT conversation_id FROM sessions WHERE id=$id";
        ownerCommand.Parameters.AddWithValue("$id", sessionId.Value);
        var conversation = await ownerCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (conversation is null) return new([], true, 0);
        var ownership = await _conversationStore.GetOwnershipAsync(ConversationId.From(conversation), cancellationToken).ConfigureAwait(false);
        if (ownership?.AgentId != scan.ExpectedAgentId)
            throw new UnauthorizedAccessException("Session owner changed.");

        // SQLite computes the length before GetString. We never materialize a rejected row.
        // The fixed per-row SQL guard permits at most one extra bounded native result; it prevents
        // a single huge transcript/tool result from entering managed memory. The owner predicate
        // is repeated so a concurrent rebind cannot turn this into an unauthorized content read.
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.role, h.timestamp, length(CAST(COALESCE(h.content,
                       CASE WHEN h.message_kind='tool-result' THEN i.result_content END, '') AS BLOB)),
                   CASE WHEN length(CAST(COALESCE(h.content,
                       CASE WHEN h.message_kind='tool-result' THEN i.result_content END, '') AS BLOB)) <= $characters
                       THEN COALESCE(h.content, CASE WHEN h.message_kind='tool-result' THEN i.result_content END, '') END
            FROM session_history h
            JOIN sessions s ON s.id=h.session_id AND s.conversation_id=$conversation
            LEFT JOIN tool_invocations i ON i.id=h.tool_invocation_id AND i.session_id=h.session_id
            WHERE h.session_id=$id ORDER BY h.id LIMIT $entries
            """;
        command.Parameters.AddWithValue("$id", sessionId.Value);
        command.Parameters.AddWithValue("$conversation", conversation);
        command.Parameters.AddWithValue("$characters", scan.MaxCharacters);
        command.Parameters.AddWithValue("$entries", scan.MaxEntries + 1);
        var entries = new List<SessionEntry>();
        var characters = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.Count == scan.MaxEntries || reader.GetInt64(2) > scan.MaxCharacters - characters)
                return new(entries, false, characters);
            var content = reader.IsDBNull(3) ? "" : reader.GetString(3);
            // Count UTF-8 bytes conservatively as characters: the managed allocation remains bounded.
            characters += checked((int)reader.GetInt64(2));
            entries.Add(new SessionEntry
            {
                Role = MessageRole.FromString(reader.GetString(0)), Content = content,
                Timestamp = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture)
            });
        }
        return new(entries, true, characters);
    }
}
