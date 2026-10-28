using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Contracts.Agents;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Separate versioned admission ledger, opened only at an explicitly supplied database path.
/// SQLite's immediate writer transaction serializes reservations across store instances/processes;
/// no in-memory lock or replaceable session owns the shared budget. No dispatcher is registered.
/// </summary>
public sealed class SqlitePeerDialogueStore : IPeerDialogueStore
{
    /// <summary>Schema understood by this admission-only foundation.</summary>
    public const int CurrentSchemaVersion = 1;
    private readonly string _connectionString;
    private readonly TimeProvider _clock;

    // Deterministic fault/cancellation boundary: both rows exist inside the transaction, neither
    // is visible to an independent reader. Production leaves this internal seam unset.
    internal Func<CancellationToken, Task>? BeforeAdmissionCommitAsync { get; set; }

    /// <summary>
    /// Uses the standard connection policy against a dedicated, caller-provisioned store.
    /// In-memory databases and read-only connections are not durable admission stores.
    /// </summary>
    public SqlitePeerDialogueStore(string connectionString, TimeProvider? timeProvider = null)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource == ":memory:"
            || builder.Mode is SqliteOpenMode.Memory or SqliteOpenMode.ReadOnly)
            throw new ArgumentException("A dedicated durable SQLite path is required.", nameof(connectionString));
        _connectionString = builder.ToString();
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<PeerDialogue> CreateAsync(AgentId authenticatedAgentId,
        PeerDialogueSpecification specification, CancellationToken cancellationToken = default)
    {
        ValidateSpecification(specification);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await ReadDialogueAsync(connection, transaction, specification.DialogueConversationId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Specification.Origin.AgentId != authenticatedAgentId) Fail(PeerDialogueRejection.Unauthorized);
            if (existing.Specification != specification
                || !existing.Specification.ExpiresAt.EqualsExact(specification.ExpiresAt)) Fail(PeerDialogueRejection.Conflict);
            return existing;
        }
        if (specification.Origin.AgentId != authenticatedAgentId) Fail(PeerDialogueRejection.Unauthorized);
        if (specification.ExpiresAt <= _clock.GetUtcNow()) Fail(PeerDialogueRejection.Invalid);
        await ExecuteAsync(connection, transaction,
            "INSERT INTO peer_dialogues(dialogue_id,specification,reserved_turns,is_closed) VALUES($id,$spec,0,0)", cancellationToken,
            ("$id", specification.DialogueConversationId.Value), ("$spec", JsonSerializer.Serialize(specification))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return new(specification, 0, false);
    }

    /// <inheritdoc />
    public async Task<PeerDialogue> GetAsync(AgentId authenticatedAgentId, ConversationId dialogueConversationId,
        CancellationToken cancellationToken = default)
    {
        ValidateConversation(dialogueConversationId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await RequireDialogueAsync(connection, null, authenticatedAgentId, dialogueConversationId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PeerDialogueMessage> AdmitAsync(AgentId authenticatedAgentId,
        PeerDialogueMessageRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        if (request.Sender.AgentId != authenticatedAgentId) Fail(PeerDialogueRejection.Unauthorized);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // Acquire the write reservation before reading budget or message identity. A deferred
        // transaction would allow competing snapshots and unsafe read-then-write upgrades.
        using var transaction = connection.BeginTransaction(deferred: false);
        var dialogue = await RequireDialogueAsync(connection, transaction, authenticatedAgentId,
            request.DialogueConversationId, cancellationToken).ConfigureAwait(false);
        var spec = dialogue.Specification;
        var opposite = request.Sender == spec.First ? spec.Second
            : request.Sender == spec.Second ? spec.First : null;
        if (opposite is null) Fail(PeerDialogueRejection.Unauthorized);
        if (request.Recipient != opposite) Fail(PeerDialogueRejection.Invalid);

        // Authenticate and validate membership BEFORE looking up the globally stable ID. An
        // unauthorized caller must not use reconciliation as an oracle into another dialogue.
        var existing = await ReadMessageAsync(connection, transaction, request.MessageId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Request != request) Fail(PeerDialogueRejection.Conflict);
            return existing;
        }
        if (dialogue.IsClosed) Fail(PeerDialogueRejection.Closed);
        var now = _clock.GetUtcNow();
        if (spec.ExpiresAt <= now) Fail(PeerDialogueRejection.Expired);
        if (dialogue.ReservedTurns >= spec.MaxAutomaticMessages) Fail(PeerDialogueRejection.Exhausted);
        if (request.ReplyTo is { } replyTo)
        {
            var parent = await ReadMessageAsync(connection, transaction, replyTo, cancellationToken).ConfigureAwait(false);
            if (parent is null || parent.Request.DialogueConversationId != request.DialogueConversationId
                || parent.Request.Sender != request.Recipient || parent.Request.Recipient != request.Sender)
                Fail(PeerDialogueRejection.Invalid);
        }
        var message = new PeerDialogueMessage(request, dialogue.ReservedTurns + 1, now, PeerDialogueDeliveryState.Pending);
        await ExecuteAsync(connection, transaction,
            "UPDATE peer_dialogues SET reserved_turns=reserved_turns+1 WHERE dialogue_id=$id", cancellationToken,
            ("$id", request.DialogueConversationId.Value)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO peer_messages(message_id,dialogue_id,sequence,recipient_agent,delivery_state,message)
            VALUES($messageId,$dialogueId,$sequence,$recipient,0,$message)
            """, cancellationToken,
            ("$messageId", Key(request.MessageId)), ("$dialogueId", request.DialogueConversationId.Value),
            ("$sequence", message.Sequence), ("$recipient", request.Recipient.AgentId.Value),
            ("$message", JsonSerializer.Serialize(message))).ConfigureAwait(false);
        if (BeforeAdmissionCommitAsync is { } observer)
            await observer(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return message;
    }

    /// <inheritdoc />
    public async Task CloseAsync(AgentId authenticatedAgentId, ConversationId dialogueConversationId,
        CancellationToken cancellationToken = default)
    {
        ValidateConversation(dialogueConversationId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await RequireDialogueAsync(connection, transaction, authenticatedAgentId, dialogueConversationId, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "UPDATE peer_dialogues SET is_closed=1 WHERE dialogue_id=$id",
            cancellationToken, ("$id", dialogueConversationId.Value)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PeerDialogueMessage>> ListPendingAsync(AgentId authenticatedAgentId,
        ConversationId dialogueConversationId, int limit, long afterSequence = 0, CancellationToken cancellationToken = default)
    {
        ValidateConversation(dialogueConversationId);
        if (limit is < 1 or > 100 || afterSequence < 0) Fail(PeerDialogueRejection.Invalid);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await RequireDialogueAsync(connection, null, authenticatedAgentId, dialogueConversationId, cancellationToken).ConfigureAwait(false);
        using var command = Command(connection, null, """
            SELECT message FROM peer_messages
            WHERE dialogue_id=$id AND recipient_agent=$recipient AND delivery_state=0 AND sequence>$after
            ORDER BY sequence ASC LIMIT $limit
            """, ("$id", dialogueConversationId.Value), ("$recipient", authenticatedAgentId.Value),
            ("$after", afterSequence), ("$limit", limit));
        var messages = new List<PeerDialogueMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            messages.Add(Deserialize<PeerDialogueMessage>(reader.GetString(0)));
        return messages;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = SqliteConnectionFactory.CreateForStoreKind(_connectionString, "peer-dialogues");
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            // Ordinary opens, including rollback observers, do not perform schema writes or
            // journal maintenance. Bootstrap is restart-safe and serialized by SQLite itself.
            using var check = Command(connection, null,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('peer_dialogues','peer_messages')");
            var count = Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (count == 2)
            {
                connection.ValidateReadOnly(CurrentSchemaVersion);
                return connection;
            }
            await new SqliteWalMaintenance().ApplyJournalModeAsync(connection, connection.DataSource,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            connection.Apply(CurrentSchemaVersion, []);
            using var transaction = connection.BeginTransaction(deferred: false);
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE IF NOT EXISTS peer_dialogues(
                    dialogue_id TEXT PRIMARY KEY NOT NULL,
                    specification TEXT NOT NULL,
                    reserved_turns INTEGER NOT NULL CHECK(reserved_turns >= 0),
                    is_closed INTEGER NOT NULL CHECK(is_closed IN (0,1)));
                CREATE TABLE IF NOT EXISTS peer_messages(
                    message_id TEXT PRIMARY KEY NOT NULL,
                    dialogue_id TEXT NOT NULL REFERENCES peer_dialogues(dialogue_id),
                    sequence INTEGER NOT NULL CHECK(sequence > 0),
                    recipient_agent TEXT NOT NULL,
                    delivery_state INTEGER NOT NULL CHECK(delivery_state = 0),
                    message TEXT NOT NULL,
                    UNIQUE(dialogue_id,sequence));
                CREATE INDEX IF NOT EXISTS ix_peer_pending ON peer_messages(dialogue_id,recipient_agent,delivery_state,sequence);
                """, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<PeerDialogue?> ReadDialogueAsync(SqliteConnection connection, SqliteTransaction? transaction,
        ConversationId dialogueConversationId, CancellationToken cancellationToken)
    {
        using var command = Command(connection, transaction,
            "SELECT specification,reserved_turns,is_closed FROM peer_dialogues WHERE dialogue_id=$id", ("$id", dialogueConversationId.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PeerDialogue(Deserialize<PeerDialogueSpecification>(reader.GetString(0)), reader.GetInt64(1), reader.GetBoolean(2)) : null;
    }

    private static async Task<PeerDialogue> RequireDialogueAsync(SqliteConnection connection, SqliteTransaction? transaction,
        AgentId authenticatedAgentId, ConversationId dialogueConversationId, CancellationToken cancellationToken)
    {
        var dialogue = await ReadDialogueAsync(connection, transaction, dialogueConversationId, cancellationToken).ConfigureAwait(false)
            ?? throw new PeerDialogueException(PeerDialogueRejection.NotFound);
        Authorize(dialogue, authenticatedAgentId);
        return dialogue;
    }

    private static void Authorize(PeerDialogue dialogue, AgentId authenticatedAgentId)
    {
        if (dialogue.Specification.First.AgentId != authenticatedAgentId && dialogue.Specification.Second.AgentId != authenticatedAgentId)
            Fail(PeerDialogueRejection.Unauthorized);
    }

    private static async Task<PeerDialogueMessage?> ReadMessageAsync(SqliteConnection connection, SqliteTransaction transaction,
        PeerDialogueMessageId messageId, CancellationToken cancellationToken)
    {
        using var command = Command(connection, transaction,
            "SELECT message FROM peer_messages WHERE message_id=$id", ("$id", Key(messageId)));
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return json is string text ? Deserialize<PeerDialogueMessage>(text) : null;
    }

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json)
        ?? throw new InvalidDataException("Invalid peer dialogue ledger record.");

    private static string Key(PeerDialogueMessageId messageId) => messageId.Value.ToString("N");

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateSpecification(PeerDialogueSpecification specification)
    {
        if (specification is null) Fail(PeerDialogueRejection.Invalid);
        ValidateConversation(specification.DialogueConversationId);
        ValidateEndpoint(specification.First);
        ValidateEndpoint(specification.Second);
        ValidateEndpoint(specification.Origin);
        if (specification.First.AgentId == specification.Second.AgentId
            || specification.First.ConversationId == specification.Second.ConversationId
            || (specification.Origin != specification.First && specification.Origin != specification.Second)
            || specification.MaxAutomaticMessages <= 0)
            Fail(PeerDialogueRejection.Invalid);
    }

    private static void ValidateRequest(PeerDialogueMessageRequest request)
    {
        if (request is null) Fail(PeerDialogueRejection.Invalid);
        ValidateConversation(request.DialogueConversationId);
        ValidateEndpoint(request.Sender);
        ValidateEndpoint(request.Recipient);
        if (!request.MessageId.IsInitialized() || (request.ReplyTo is { } reply && !reply.IsInitialized())
            || string.IsNullOrWhiteSpace(request.Content) || request.Content.Length > PeerDialogueMessageRequest.MaxContentLength
            || !IsWellFormedUtf16(request.Content))
            Fail(PeerDialogueRejection.Invalid);
    }

    private static void ValidateEndpoint(PeerDialogueEndpoint endpoint)
    {
        if (endpoint is null) Fail(PeerDialogueRejection.Invalid);
        if (!endpoint.AgentId.IsInitialized()
            || endpoint.AgentId.Value.Length > PeerDialogueSpecification.MaxEndpointIdentityLength
            || !IsWellFormedUtf16(endpoint.AgentId.Value))
            Fail(PeerDialogueRejection.Invalid);
        ValidateConversation(endpoint.ConversationId);
    }

    private static void ValidateConversation(ConversationId value)
    {
        if (!value.IsInitialized() || value.Value.Length > PeerDialogueSpecification.MaxEndpointIdentityLength
            || !IsWellFormedUtf16(value.Value))
            Fail(PeerDialogueRejection.Invalid);
    }

    private static readonly System.Text.Encoding StrictUtf8 = new System.Text.UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static bool IsWellFormedUtf16(string value)
    {
        // Validate lossless encoding with the framework; this does not select a truncation
        // boundary or introduce another surrogate/grapheme scanning policy.
        try
        {
            _ = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (System.Text.EncoderFallbackException)
        {
            return false;
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(PeerDialogueRejection reason) => throw new PeerDialogueException(reason);
}
