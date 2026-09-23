using System.Globalization;
using System.Text;
using BotNexus.Domain.Text;
using BotNexus.Memory.Embeddings;
using BotNexus.Memory.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions;
using BotNexus.Persistence.Sqlite;

namespace BotNexus.Memory;

public sealed class SqliteMemoryStore(
    string dbPath,
    IFileSystem? fileSystem = null,
    MemoryLikeFallbackOptions? likeFallbackOptions = null,
    IMemoryEmbeddingService? embeddingService = null,
    MemoryVectorSearchOptions? vectorSearchOptions = null,
    ILogger<SqliteMemoryStore>? logger = null,
    Func<MemoryTemporalDecayPolicy>? temporalDecayPolicy = null) : IMemoryStore
{
    private const int MaxReembeddingErrorLength = 2048;
    private static readonly TimeSpan ReembeddingClaimLease = TimeSpan.FromMinutes(5);
    private readonly string _dbPath = dbPath;
    private readonly SqliteWalMaintenance _walMaintenance = new(fileSystem);
    private readonly string _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate";
    private readonly IFileSystem _fileSystem = fileSystem ?? new FileSystem();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // The LIKE fallback (used only when FTS errors out or the DB is transiently busy)
    // is an unindexable full scan, so it is bounded by a recency window + row ceiling
    // to keep degraded-mode cost finite. The FTS primary path is unaffected.
    private readonly MemoryLikeFallbackOptions _likeFallbackOptions =
        likeFallbackOptions ?? MemoryLikeFallbackOptions.Default;

    // Optional by construction: when no embedding service is supplied the store behaves
    // exactly as it did before hybrid retrieval existed - writes store no vector and search
    // is BM25-only. This is the supported degraded mode, not an error path.
    private readonly IMemoryEmbeddingService _embeddingService = embeddingService ?? MemoryEmbeddingService.Disabled;

    private readonly MemoryVectorSearchOptions _vectorSearchOptions =
        vectorSearchOptions ?? MemoryVectorSearchOptions.Default;

    private readonly ILogger<SqliteMemoryStore> _logger = logger ?? NullLogger<SqliteMemoryStore>.Instance;
    private readonly Func<MemoryTemporalDecayPolicy> _temporalDecayPolicy =
        temporalDecayPolicy ?? (() => MemoryTemporalDecayPolicy.Default);

    private bool _initialized;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
            return;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            _fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(_dbPath) ?? ".");
            await SqliteRetryHelper.ExecuteWithRetryAsync(async token =>
            {
                await using var connection = CreateConnection();
                await connection.OpenAsync(token).ConfigureAwait(false);

                // Journal mode must be selected outside a transaction. The schema transaction
                // then takes SQLite's cross-connection write lock before inspecting or changing
                // schema state, so independent store instances cannot interleave transitions.
                await _walMaintenance.ApplyJournalModeAsync(connection, _dbPath, cancellationToken: token)
                    .ConfigureAwait(false);
                await using var transaction = connection.BeginTransaction(deferred: false);

                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                CREATE TABLE IF NOT EXISTS memories (
                    rowid INTEGER PRIMARY KEY AUTOINCREMENT,
                    id TEXT NOT NULL UNIQUE,
                    agent_id TEXT NOT NULL,
                    session_id TEXT NULL,
                    turn_index INTEGER NULL,
                    source_type TEXT NOT NULL,
                    content TEXT NOT NULL,
                    metadata_json TEXT NULL,
                    embedding BLOB NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NULL,
                    expires_at TEXT NULL,
                    is_archived INTEGER NOT NULL DEFAULT 0,
                    provenance TEXT NULL,
                    origin_conversation_id TEXT NULL,
                    origin_session_id TEXT NULL,
                    role TEXT NULL,
                    category TEXT NULL,
                    tags_json TEXT NULL,
                    revision INTEGER NOT NULL DEFAULT 1,
                    archived_at TEXT NULL,
                    corrects_id TEXT NULL,
                    supersedes_id TEXT NULL,
                    superseded_by_id TEXT NULL,
                    origin_kind TEXT NULL,
                    origin_reference TEXT NULL,
                    embedding_status TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_memories_agent_id ON memories(agent_id);
                CREATE INDEX IF NOT EXISTS idx_memories_session_id ON memories(session_id);
                CREATE INDEX IF NOT EXISTS idx_memories_created_at ON memories(created_at);

                CREATE TABLE IF NOT EXISTS schema_version (
                    version INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS memory_reembedding_job (
                    singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
                    job_id TEXT NOT NULL UNIQUE,
                    target_model_id TEXT NOT NULL,
                    target_model_fingerprint TEXT NOT NULL,
                    target_dimensions INTEGER NOT NULL,
                    state TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    last_error TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS memory_reembedding_items (
                    job_id TEXT NOT NULL,
                    memory_id TEXT NOT NULL,
                    failure_count INTEGER NOT NULL DEFAULT 0,
                    last_error TEXT NULL,
                    next_attempt_at TEXT NULL,
                    claim_revision INTEGER NULL,
                    PRIMARY KEY (job_id, memory_id),
                    FOREIGN KEY (job_id) REFERENCES memory_reembedding_job(job_id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_memory_reembedding_items_claim
                    ON memory_reembedding_items(job_id, next_attempt_at, memory_id);

                INSERT INTO schema_version(version)
                SELECT 1
                WHERE NOT EXISTS (SELECT 1 FROM schema_version);
                """;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                // CREATE TABLE IF NOT EXISTS is a no-op for an older memories table. Add every
                // durable column and complete the FTS transition under the same SQLite write lock.
                await EnsureDurableRecordColumnsAsync(connection, transaction, token).ConfigureAwait(false);
                await EnsureReembeddingItemColumnsAsync(connection, transaction, token).ConfigureAwait(false);
                await UpgradeSearchContractAsync(connection, transaction, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);

                // #3244: report the scan ceiling being exceeded once per store open.
                await WarnIfEmbeddedRowsExceedScanCeilingAsync(connection, token).ConfigureAwait(false);
                return true;
            }, ct).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static readonly (string Name, string Definition)[] DurableRecordColumns =
    [
        ("provenance", "TEXT NULL"),
        ("origin_conversation_id", "TEXT NULL"),
        ("origin_session_id", "TEXT NULL"),
        ("role", "TEXT NULL"),
        ("category", "TEXT NULL"),
        ("tags_json", "TEXT NULL"),
        ("revision", "INTEGER NOT NULL DEFAULT 1"),
        ("archived_at", "TEXT NULL"),
        ("corrects_id", "TEXT NULL"),
        ("supersedes_id", "TEXT NULL"),
        ("superseded_by_id", "TEXT NULL"),
        ("origin_kind", "TEXT NULL"),
        ("origin_reference", "TEXT NULL"),
        ("embedding_status", "TEXT NULL")
    ];

    private static async Task EnsureReembeddingItemColumnsAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "ALTER TABLE memory_reembedding_items ADD COLUMN claim_revision INTEGER NULL";
        try
        {
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // A concurrent opener or a current schema already supplied the additive claim column.
        }
    }

    private static async Task UpgradeSearchContractAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (version >= 2)
        {
            // Version two is normally all-or-nothing because its transition is transactional.
            // IF NOT EXISTS also repairs a database left incomplete by an older non-atomic opener.
            command.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts
                USING fts5(content, content='memories', content_rowid='rowid');
                CREATE TRIGGER IF NOT EXISTS memories_ai AFTER INSERT ON memories WHEN new.is_archived = 0 BEGIN
                    INSERT INTO memories_fts(rowid, content) VALUES (new.rowid, new.content);
                END;
                CREATE TRIGGER IF NOT EXISTS memories_ad AFTER DELETE ON memories WHEN old.is_archived = 0 BEGIN
                    INSERT INTO memories_fts(memories_fts, rowid, content) VALUES('delete', old.rowid, old.content);
                END;
                CREATE TRIGGER IF NOT EXISTS memories_au AFTER UPDATE ON memories BEGIN
                    INSERT INTO memories_fts(memories_fts, rowid, content)
                    SELECT 'delete', old.rowid, old.content WHERE old.is_archived = 0;
                    INSERT INTO memories_fts(rowid, content)
                    SELECT new.rowid, new.content WHERE new.is_archived = 0;
                END;
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return;
        }

        command.CommandText = """
            DROP TRIGGER IF EXISTS memories_ai;
            DROP TRIGGER IF EXISTS memories_ad;
            DROP TRIGGER IF EXISTS memories_au;
            DROP TABLE IF EXISTS memories_fts;
            CREATE VIRTUAL TABLE memories_fts USING fts5(content, content='memories', content_rowid='rowid');
            INSERT INTO memories_fts(rowid, content)
            SELECT rowid, content FROM memories WHERE is_archived = 0;
            CREATE TRIGGER memories_ai AFTER INSERT ON memories WHEN new.is_archived = 0 BEGIN
                INSERT INTO memories_fts(rowid, content) VALUES (new.rowid, new.content);
            END;
            CREATE TRIGGER memories_ad AFTER DELETE ON memories WHEN old.is_archived = 0 BEGIN
                INSERT INTO memories_fts(memories_fts, rowid, content) VALUES('delete', old.rowid, old.content);
            END;
            CREATE TRIGGER memories_au AFTER UPDATE ON memories BEGIN
                INSERT INTO memories_fts(memories_fts, rowid, content)
                SELECT 'delete', old.rowid, old.content WHERE old.is_archived = 0;
                INSERT INTO memories_fts(rowid, content)
                SELECT new.rowid, new.content WHERE new.is_archived = 0;
            END;
            DELETE FROM schema_version;
            INSERT INTO schema_version(version) VALUES (2);
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Counts live embedded rows at store open and warns once if they exceed the vector scan
    /// ceiling, so an operator learns that semantic recall is bounded before a user notices
    /// missing memories (#3244, acceptance criterion 4).
    /// </summary>
    /// <remarks>
    /// Called from inside the initialize critical section, which runs at most once per store
    /// instance - that is the "once per store open" guarantee, not a separate latch that could
    /// drift from it. The count is a cheap indexed-free aggregate and failure to compute it must
    /// never prevent the store from opening, so any SQLite error is swallowed.
    /// </remarks>
    private async Task WarnIfEmbeddedRowsExceedScanCeilingAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (_vectorSearchOptions.MaxScanRows is not { } ceiling || ceiling <= 0)
            return;

        try
        {
            var embedded = await CountEmbeddedRowsAsync(connection, ct).ConfigureAwait(false);
            if (embedded <= ceiling)
                return;

            _logger.LogWarning(
                "Memory store '{DbPath}' holds {EmbeddedRowCount} embedded rows but a single vector search "
                + "scans at most {VectorScanCeiling} (newest-first), so approximately {UnreachableRowCount} "
                + "older row(s) can only be recalled lexically. Raise MemoryVectorSearchOptions.MaxScanRows "
                + "or reduce the corpus.",
                _dbPath,
                embedded,
                ceiling,
                embedded - ceiling);
        }
        catch (SqliteException)
        {
            // Diagnostics must never be the reason a store fails to open.
        }
    }

    /// <summary>Live (non-archived) rows carrying an embedding vector.</summary>
    private static async Task<int> CountEmbeddedRowsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM memories WHERE is_archived = 0 AND embedding IS NOT NULL";
        var scalar = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is null or DBNull ? 0 : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Lazily adds the durable-record columns to an existing <c>memories</c> table.
    /// </summary>
    /// <remarks>
    /// A store must never reject an older DB at open. Each column is added independently and a
    /// duplicate-column error is swallowed, so the upgrade is idempotent and safe against a
    /// concurrent process that added the column first. Nullable semantic columns intentionally
    /// preserve missing evidence rather than assigning a role, origin, or provenance retroactively.
    /// </remarks>
    private static async Task EnsureDurableRecordColumnsAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        HashSet<string> existing = new(StringComparer.OrdinalIgnoreCase);
        await using (var probe = connection.CreateCommand())
        {
            probe.Transaction = transaction;
            probe.CommandText = "PRAGMA table_info(memories);";
            await using var reader = await probe.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                existing.Add(reader.GetString(1));
        }

        foreach (var (column, definition) in DurableRecordColumns)
        {
            if (existing.Contains(column))
                continue;

            try
            {
                await using var alter = connection.CreateCommand();
                alter.Transaction = transaction;
                alter.CommandText = $"ALTER TABLE memories ADD COLUMN {column} {definition};";
                await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (
                ex.SqliteErrorCode == 1
                && ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
            {
                // A supported pre-transaction opener may already have supplied the column.
            }
        }
    }

    public async Task<MemoryEntry> InsertAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id;
            var createdAt = entry.CreatedAt == default ? DateTimeOffset.UtcNow : entry.CreatedAt;
            var toInsert = entry with { Id = id, CreatedAt = createdAt, Revision = 1 };

            // Populate the embedding BLOB on write. A failure here must never fail the write:
            // TryGenerateAsync returns null instead of throwing, and the row is simply stored
            // without a vector, remaining fully retrievable through BM25.
            if (toInsert.Embedding is null)
            {
                var generated = await _embeddingService.TryGenerateAsync(toInsert.Content, ct).ConfigureAwait(false);
                if (generated is { } stamped)
                    toInsert = toInsert with { Embedding = EmbeddingBlob.Encode(stamped.Identity, stamped.Vector) };
            }

            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO memories (
                    id, agent_id, session_id, turn_index, source_type, content, metadata_json,
                    embedding, created_at, updated_at, expires_at, is_archived,
                    provenance, origin_conversation_id, origin_session_id, role, category, tags_json,
                    revision, archived_at, corrects_id, supersedes_id, superseded_by_id,
                    origin_kind, origin_reference, embedding_status)
                VALUES (
                    $id, $agentId, $sessionId, $turnIndex, $sourceType, $content, $metadataJson,
                    $embedding, $createdAt, $updatedAt, $expiresAt, $isArchived,
                    $provenance, $originConversationId, $originSessionId, $role, $category, $tagsJson,
                    $revision, $archivedAt, $correctsId, $supersedesId, $supersededById,
                    $originKind, $originReference, $embeddingStatus)
                """;
            BindParameters(command, toInsert);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return toInsert;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<MemoryEntry?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await InitializeAsync(ct).ConfigureAwait(false);

        return await SqliteRetryHelper.ExecuteWithRetryAsync(async token =>
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, agent_id, session_id, turn_index, source_type, content, metadata_json,
                       embedding, created_at, updated_at, expires_at, is_archived,
                       provenance, origin_conversation_id, origin_session_id,
                       role, category, tags_json, revision, archived_at, corrects_id, supersedes_id,
                       superseded_by_id, origin_kind, origin_reference, embedding_status
                FROM memories
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", id);

            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            return await reader.ReadAsync(token).ConfigureAwait(false)
                ? ReadMemory(reader)
                : null;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryEntry>> GetBySessionAsync(string sessionId, int limit = 20, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await InitializeAsync(ct).ConfigureAwait(false);

        var cappedLimit = Math.Clamp(limit, 1, int.MaxValue);
        return await SqliteRetryHelper.ExecuteWithRetryAsync(async token =>
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, agent_id, session_id, turn_index, source_type, content, metadata_json,
                       embedding, created_at, updated_at, expires_at, is_archived,
                       provenance, origin_conversation_id, origin_session_id,
                       role, category, tags_json, revision, archived_at, corrects_id, supersedes_id,
                       superseded_by_id, origin_kind, origin_reference, embedding_status
                FROM memories
                WHERE session_id = $sessionId
                ORDER BY created_at DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId);
            command.Parameters.AddWithValue("$limit", cappedLimit);

            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            List<MemoryEntry> results = [];
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                results.Add(ReadMemory(reader));

            return results as IReadOnlyList<MemoryEntry>;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default)
    {
        var scored = await SearchScoredAsync(query, topK, filter, ct).ConfigureAwait(false);
        return scored.Select(item => item.Entry).ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// This is the real search implementation; <see cref="SearchAsync"/> is the projection that drops
    /// the score. Keeping it that way round means the rendered/thresholded score is by construction
    /// the one that produced the ordering, with no second relevance definition to drift (#2781).
    /// </remarks>
    public async Task<IReadOnlyList<ScoredMemoryEntry>> SearchScoredAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default)
    {
        var result = await SearchWithReportAsync(query, topK, filter, ct).ConfigureAwait(false);
        return result.Entries;
    }

    /// <inheritdoc />
    public async Task<MemorySearchResult> SearchWithReportAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        var policy = ResolveTemporalDecayPolicy();
        var sanitized = SanitizeFtsQuery(query);
        if (string.IsNullOrWhiteSpace(sanitized))
            return new MemorySearchResult([], MemoryVectorScanReport.NotAttempted, policy);

        var limit = Math.Clamp(topK, 1, 100);
        var lambda = policy.Lambda;
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            // Two-pass by design (#2740). Pass one uses the explicit conjunction, which is
            // the most precise reading of the caller's intent and preserves the exact top
            // result for the short exact queries that already worked. Pass two only runs
            // when the conjunction under-returns, and widens to a disjunction so a single
            // rare term can no longer collapse an otherwise reasonable query to zero rows.
            // The union is then ranked as one candidate set, so BM25 still rewards rows
            // hitting more terms - the recall cliff disappears without inverting precedence.
            Dictionary<string, MemoryRankingCandidate> candidates = new(StringComparer.Ordinal);
            await ExecuteFtsMatchAsync(
                connection, BuildFtsMatchExpression(sanitized, requireAllTerms: true), filter, limit, candidates, ct)
                .ConfigureAwait(false);

            if (candidates.Count < limit)
            {
                await ExecuteFtsMatchAsync(
                    connection, BuildFtsMatchExpression(sanitized, requireAllTerms: false), filter, limit, candidates, ct)
                    .ConfigureAwait(false);
            }

            // The lexical ids are captured BEFORE the vector pass so the union rescue in
            // AugmentWithVectorCandidatesAsync knows exactly which rows earned a similarity score
            // on lexical evidence alone (#3244 AC5).
            var lexicalIds = candidates.Keys.ToArray();
            var report = await AugmentWithVectorCandidatesAsync(connection, query, candidates, filter, lexicalIds, ct)
                .ConfigureAwait(false);

            return new MemorySearchResult(
                HybridMemoryRanker.RankWithScores(candidates.Values, limit, lambda),
                report,
                policy);
        }
        catch (SqliteException ex) when (SqliteRetryHelper.IsTransient(ex))
        {
            // Transient lock/busy — retry the whole search once via LIKE fallback
            return await SearchWithLikeFallbackAsync(sanitized, limit, filter, lambda, ct).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // FTS syntax or corruption — fall back to LIKE search
            return await SearchWithLikeFallbackAsync(sanitized, limit, filter, lambda, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<MemoryMutationResult> UpdateAsync(
        string id,
        int expectedRevision,
        MemoryUpdate update,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(update);
        if (expectedRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));

        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SqliteRetryHelper.ExecuteWithRetryAsync(async token =>
            {
                await using var connection = CreateConnection();
                await connection.OpenAsync(token).ConfigureAwait(false);
                await using var transaction = connection.BeginTransaction(deferred: false);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"""
                    UPDATE memories
                    SET content = CASE WHEN $contentSpecified = 1 THEN $content ELSE content END,
                        role = CASE WHEN $roleSpecified = 1 THEN $role ELSE role END,
                        category = CASE WHEN $categorySpecified = 1 THEN $category ELSE category END,
                        tags_json = CASE WHEN $tagsSpecified = 1 THEN $tags ELSE tags_json END,
                        corrects_id = CASE WHEN $correctsSpecified = 1 THEN $corrects ELSE corrects_id END,
                        supersedes_id = CASE WHEN $supersedesSpecified = 1 THEN $supersedes ELSE supersedes_id END,
                        superseded_by_id = CASE WHEN $supersededBySpecified = 1 THEN $supersededBy ELSE superseded_by_id END,
                        embedding = CASE WHEN $contentSpecified = 1 THEN NULL ELSE embedding END,
                        embedding_status = CASE
                            WHEN $contentSpecified = 1 THEN NULL
                            WHEN $embeddingStatusSpecified = 1 THEN $embeddingStatus
                            ELSE embedding_status END,
                        updated_at = $updatedAt,
                        revision = revision + 1
                    WHERE id = $id AND revision = $expectedRevision
                    RETURNING {MemoryColumnList};
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
                command.Parameters.AddWithValue("$contentSpecified", update.Content is null ? 0 : 1);
                command.Parameters.AddWithValue("$content", (object?)update.Content ?? DBNull.Value);
                AddUpdateParameter(command, "$role", update.Role);
                AddUpdateParameter(command, "$category", update.Category);
                AddUpdateParameter(command, "$tags", update.TagsJson);
                AddUpdateParameter(command, "$corrects", update.CorrectsId);
                AddUpdateParameter(command, "$supersedes", update.SupersedesId);
                AddUpdateParameter(command, "$supersededBy", update.SupersededById);
                AddUpdateParameter(command, "$embeddingStatus", update.EmbeddingStatus);
                command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                MemoryEntry? updated = null;
                await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    if (await reader.ReadAsync(token).ConfigureAwait(false))
                        updated = ReadMemory(reader);
                }

                var result = updated is not null
                    ? new MemoryMutationResult(MemoryMutationStatus.Applied, updated)
                    : await ReadMutationMissAsync(connection, transaction, id, expectedRevision, archive: false, token)
                        .ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return result;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<MemoryMutationResult> ArchiveAsync(string id, int expectedRevision, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (expectedRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));

        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SqliteRetryHelper.ExecuteWithRetryAsync(async token =>
            {
                await using var connection = CreateConnection();
                await connection.OpenAsync(token).ConfigureAwait(false);
                await using var transaction = connection.BeginTransaction(deferred: false);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"""
                    UPDATE memories
                    SET is_archived = 1, archived_at = $now, updated_at = $now, revision = revision + 1
                    WHERE id = $id AND revision = $expectedRevision AND is_archived = 0
                    RETURNING {MemoryColumnList};
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                MemoryEntry? archived = null;
                await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    if (await reader.ReadAsync(token).ConfigureAwait(false))
                        archived = ReadMemory(reader);
                }

                var result = archived is not null
                    ? new MemoryMutationResult(MemoryMutationStatus.Applied, archived)
                    : await ReadMutationMissAsync(connection, transaction, id, expectedRevision, archive: true, token)
                        .ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return result;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<MemoryMutationResult> DeleteAsync(string id, int expectedRevision, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (expectedRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));

        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SqliteRetryHelper.ExecuteWithRetryAsync(async token =>
            {
                await using var connection = CreateConnection();
                await connection.OpenAsync(token).ConfigureAwait(false);
                await using var transaction = connection.BeginTransaction(deferred: false);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM memories WHERE id = $id AND revision = $expectedRevision";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
                MemoryMutationResult result;
                if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1)
                {
                    result = new MemoryMutationResult(MemoryMutationStatus.Applied, null);
                }
                else
                {
                    result = await ReadMutationMissAsync(
                        connection, transaction, id, expectedRevision, archive: false, token).ConfigureAwait(false);
                }

                await transaction.CommitAsync(token).ConfigureAwait(false);
                return result;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM memories WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> DeleteBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        // A blank id is treated as "nothing to do" rather than an argument fault: session delete
        // is an idempotent, best-effort cleanup path and must never widen into a broad delete.
        // `WHERE session_id = ''` would also not match NULL rows in SQLite, but short-circuiting
        // makes that guarantee independent of SQL comparison semantics.
        if (string.IsNullOrWhiteSpace(sessionId))
            return 0;

        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            // Uses idx_memories_session_id. The memories_ad trigger mirrors each deletion into
            // the FTS index, so the rows stop being searchable and not merely stop being listed.
            command.CommandText = "DELETE FROM memories WHERE session_id = $sessionId";
            command.Parameters.AddWithValue("$sessionId", sessionId);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListSessionIdsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        return await SqliteRetryHelper.ExecuteWithRetryAsync(async token =>
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            // IS NOT NULL is load-bearing: a NULL session_id marks a non-session memory, and
            // surfacing it here would make the reconciler treat it as an unresolvable orphan.
            command.CommandText = """
                SELECT DISTINCT session_id
                FROM memories
                WHERE session_id IS NOT NULL
                """;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            List<string> ids = [];
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                ids.Add(reader.GetString(0));
            return ids as IReadOnlyList<string>;
        }, ct).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM memories";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<MemoryStoreStats> GetStatsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        return await SqliteRetryHelper.ExecuteWithRetryAsync(async token =>
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*), MAX(created_at)
                FROM memories
                """;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            await reader.ReadAsync(token).ConfigureAwait(false);

            var count = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            DateTimeOffset? lastIndexedAt = reader.IsDBNull(1)
                ? null
                : DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
            await reader.CloseAsync().ConfigureAwait(false);

            // #3244 AC3: the embedded row count and the ceiling it is measured against are store
            // diagnostics, not search diagnostics - they are true between queries and are what an
            // operator needs to see the truncation condition coming rather than discovering it from
            // a user complaint about missing memories.
            var embeddedCount = await CountEmbeddedRowsAsync(connection, token).ConfigureAwait(false);
            var ceiling = _vectorSearchOptions.MaxScanRows is { } maxRows && maxRows > 0 ? maxRows : (int?)null;

            var sizeBytes = _fileSystem.File.Exists(_dbPath) ? _fileSystem.FileInfo.New(_dbPath).Length : 0L;
            return new MemoryStoreStats(count, sizeBytes, lastIndexedAt, embeddedCount, ceiling);
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ReembeddingJob> EnsureReembeddingJobAsync(
        EmbeddingIdentity targetIdentity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targetIdentity);
        if (targetIdentity.Dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetIdentity), "Embedding dimensions must be positive.");

        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var existing = await ReadReembeddingJobRowAsync(connection, transaction, ct).ConfigureAwait(false);
            if (existing is null || !existing.TargetIdentity.Matches(targetIdentity))
            {
                var now = DateTimeOffset.UtcNow.ToString("O");
                var jobId = Guid.NewGuid().ToString("N");
                await ExecuteReembeddingCommandAsync(connection, transaction,
                    "DELETE FROM memory_reembedding_items", [], ct).ConfigureAwait(false);
                await ExecuteReembeddingCommandAsync(connection, transaction,
                    "DELETE FROM memory_reembedding_job", [], ct).ConfigureAwait(false);
                await ExecuteReembeddingCommandAsync(connection, transaction,
                    """
                    INSERT INTO memory_reembedding_job (
                        singleton_id, job_id, target_model_id, target_model_fingerprint,
                        target_dimensions, state, created_at, updated_at)
                    VALUES (1, $jobId, $modelId, $fingerprint, $dimensions, 'Running', $now, $now)
                    """,
                    [("$jobId", jobId), ("$modelId", targetIdentity.ModelId),
                     ("$fingerprint", targetIdentity.ModelFingerprint), ("$dimensions", targetIdentity.Dimensions),
                     ("$now", now)], ct).ConfigureAwait(false);
                existing = new ReembeddingJob(jobId, targetIdentity, ReembeddingJobState.Running, 0, 0, 0, 0, null);
            }

            var result = await BuildReembeddingJobAsync(connection, transaction, existing, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ReembeddingJob?> GetReembeddingJobAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var row = await ReadReembeddingJobRowAsync(connection, transaction, ct).ConfigureAwait(false);
            if (row is null)
                return null;

            var result = await BuildReembeddingJobAsync(connection, transaction, row, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReembeddingItem>> ClaimReembeddingBatchAsync(
        string jobId,
        int batchSize,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        if (batchSize <= 0)
            return [];

        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var row = await ReadReembeddingJobRowAsync(connection, transaction, ct).ConfigureAwait(false);
            if (row is null || row.JobId != jobId || row.State != ReembeddingJobState.Running)
                return [];

            _ = await BuildReembeddingJobAsync(connection, transaction, row, ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT m.id, m.content, i.failure_count, m.revision
                FROM memory_reembedding_items i
                INNER JOIN memories m ON m.id = i.memory_id
                WHERE i.job_id = $jobId
                  AND m.is_archived = 0
                  AND (i.next_attempt_at IS NULL OR i.next_attempt_at <= $now)
                ORDER BY m.created_at, m.id
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$jobId", jobId);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$limit", batchSize);
            List<ReembeddingItem> claimed = [];
            await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    claimed.Add(new ReembeddingItem(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
            }

            foreach (var item in claimed)
            {
                await ExecuteReembeddingCommandAsync(connection, transaction,
                    "UPDATE memory_reembedding_items SET next_attempt_at = $leaseUntil, claim_revision = $claimRevision WHERE job_id = $jobId AND memory_id = $memoryId",
                    [("$leaseUntil", now.Add(ReembeddingClaimLease).ToString("O")), ("$claimRevision", item.Revision),
                     ("$jobId", jobId), ("$memoryId", item.MemoryId)], ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return claimed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task CompleteReembeddingItemAsync(
        string jobId,
        string memoryId,
        int claimedRevision,
        byte[] embedding,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryId);
        ArgumentNullException.ThrowIfNull(embedding);
        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var job = await ReadReembeddingJobRowAsync(connection, transaction, ct).ConfigureAwait(false);
            if (job is null || job.JobId != jobId || job.State != ReembeddingJobState.Running ||
                !EmbeddingBlob.TryDecode(embedding, out var identity, out _) || !job.TargetIdentity.Matches(identity))
                return;

            var changed = await ExecuteReembeddingCommandAsync(connection, transaction,
                """
                UPDATE memories
                SET embedding = $embedding, embedding_status = 'ready'
                WHERE id = $memoryId AND is_archived = 0 AND revision = $claimedRevision
                  AND EXISTS (
                      SELECT 1 FROM memory_reembedding_items
                      WHERE job_id = $jobId AND memory_id = $memoryId
                        AND next_attempt_at IS NOT NULL AND claim_revision = $claimedRevision)
                """,
                [("$embedding", embedding), ("$memoryId", memoryId), ("$jobId", jobId),
                 ("$claimedRevision", claimedRevision)], ct).ConfigureAwait(false);
            if (changed == 1)
            {
                await ExecuteReembeddingCommandAsync(connection, transaction,
                    "DELETE FROM memory_reembedding_items WHERE job_id = $jobId AND memory_id = $memoryId",
                    [("$jobId", jobId), ("$memoryId", memoryId)], ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task FailReembeddingItemAsync(
        string jobId,
        string memoryId,
        string error,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryId);
        var normalizedError = error ?? string.Empty;
        var boundedError = normalizedError.SafeTruncate(MaxReembeddingErrorLength) ?? string.Empty;
        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var job = await ReadReembeddingJobRowAsync(connection, transaction, ct).ConfigureAwait(false);
            if (job is null || job.JobId != jobId || job.State != ReembeddingJobState.Running)
                return;

            var changed = await ExecuteReembeddingCommandAsync(connection, transaction,
                """
                UPDATE memory_reembedding_items
                SET failure_count = failure_count + 1, last_error = $error, next_attempt_at = NULL, claim_revision = NULL
                WHERE job_id = $jobId AND memory_id = $memoryId
                  AND next_attempt_at IS NOT NULL
                """,
                [("$error", boundedError), ("$jobId", jobId), ("$memoryId", memoryId)], ct).ConfigureAwait(false);
            if (changed == 1)
            {
                await ExecuteReembeddingCommandAsync(connection, transaction,
                    "UPDATE memory_reembedding_job SET last_error = $error, updated_at = $now WHERE job_id = $jobId",
                    [("$error", boundedError), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$jobId", jobId)],
                    ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public Task PauseReembeddingJobAsync(string jobId, CancellationToken ct = default)
        => SetReembeddingJobStateAsync(jobId, ReembeddingJobState.Running, ReembeddingJobState.Paused, ct);

    /// <inheritdoc />
    public Task ResumeReembeddingJobAsync(string jobId, CancellationToken ct = default)
        => SetReembeddingJobStateAsync(jobId, ReembeddingJobState.Paused, ReembeddingJobState.Running, ct);

    /// <inheritdoc />
    public async Task CancelReembeddingJobAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ExecuteReembeddingCommandAsync(connection, null,
                """
                UPDATE memory_reembedding_job
                SET state = 'Cancelled', updated_at = $now
                WHERE job_id = $jobId AND state <> 'Cancelled'
                """,
                [("$now", DateTimeOffset.UtcNow.ToString("O")), ("$jobId", jobId)], ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SetReembeddingJobStateAsync(
        string jobId,
        ReembeddingJobState expected,
        ReembeddingJobState replacement,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ExecuteReembeddingCommandAsync(connection, null,
                """
                UPDATE memory_reembedding_job
                SET state = $replacement, updated_at = $now
                WHERE job_id = $jobId AND state = $expected
                """,
                [("$replacement", replacement.ToString()), ("$now", DateTimeOffset.UtcNow.ToString("O")),
                 ("$jobId", jobId), ("$expected", expected.ToString())], ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<ReembeddingJob?> ReadReembeddingJobRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT job_id, target_model_id, target_model_fingerprint, target_dimensions, state, last_error
            FROM memory_reembedding_job
            WHERE singleton_id = 1
            """;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var state = Enum.Parse<ReembeddingJobState>(reader.GetString(4), ignoreCase: false);
        return new ReembeddingJob(
            reader.GetString(0),
            new EmbeddingIdentity(reader.GetString(1), reader.GetString(2), reader.GetInt32(3)),
            state, 0, 0, 0, 0, reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static async Task<ReembeddingJob> BuildReembeddingJobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReembeddingJob row,
        CancellationToken ct)
    {
        List<string> pending = [];
        var total = 0;
        var covered = 0;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id, embedding FROM memories WHERE is_archived = 0";
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                total++;
                var blob = reader.IsDBNull(1) ? null : (byte[])reader[1];
                if (EmbeddingBlob.TryDecode(blob, out var identity, out _) && row.TargetIdentity.Matches(identity))
                    covered++;
                else
                    pending.Add(reader.GetString(0));
            }
        }

        await ExecuteReembeddingCommandAsync(connection, transaction,
            "DELETE FROM memory_reembedding_items WHERE job_id = $jobId AND memory_id NOT IN (SELECT id FROM memories WHERE is_archived = 0)",
            [("$jobId", row.JobId)], ct).ConfigureAwait(false);
        foreach (var memoryId in pending)
        {
            await ExecuteReembeddingCommandAsync(connection, transaction,
                """
                INSERT INTO memory_reembedding_items (job_id, memory_id)
                VALUES ($jobId, $memoryId)
                ON CONFLICT(job_id, memory_id) DO NOTHING
                """,
                [("$jobId", row.JobId), ("$memoryId", memoryId)], ct).ConfigureAwait(false);
        }

        // Rows repaired by another process or ordinary write no longer belong in this target's queue.
        await using (var deleteCovered = connection.CreateCommand())
        {
            deleteCovered.Transaction = transaction;
            deleteCovered.CommandText = "SELECT memory_id FROM memory_reembedding_items WHERE job_id = $jobId";
            deleteCovered.Parameters.AddWithValue("$jobId", row.JobId);
            List<string> stale = [];
            await using (var reader = await deleteCovered.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                var pendingSet = pending.ToHashSet(StringComparer.Ordinal);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var id = reader.GetString(0);
                    if (!pendingSet.Contains(id))
                        stale.Add(id);
                }
            }
            foreach (var memoryId in stale)
            {
                await ExecuteReembeddingCommandAsync(connection, transaction,
                    "DELETE FROM memory_reembedding_items WHERE job_id = $jobId AND memory_id = $memoryId",
                    [("$jobId", row.JobId), ("$memoryId", memoryId)], ct).ConfigureAwait(false);
            }
        }

        await using var failureCommand = connection.CreateCommand();
        failureCommand.Transaction = transaction;
        failureCommand.CommandText = "SELECT COUNT(*) FROM memory_reembedding_items WHERE job_id = $jobId AND failure_count > 0";
        failureCommand.Parameters.AddWithValue("$jobId", row.JobId);
        var failureScalar = await failureCommand.ExecuteScalarAsync(ct).ConfigureAwait(false);
        var failed = Convert.ToInt32(failureScalar, CultureInfo.InvariantCulture);
        return row with { TotalCount = total, CoveredCount = covered, PendingCount = pending.Count, FailedCount = failed };
    }

    private static async Task<int> ExecuteReembeddingCommandAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        IReadOnlyList<(string Name, object Value)> parameters,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private const string MemoryColumnList = """
        id, agent_id, session_id, turn_index, source_type, content, metadata_json,
        embedding, created_at, updated_at, expires_at, is_archived,
        provenance, origin_conversation_id, origin_session_id, role, category, tags_json,
        revision, archived_at, corrects_id, supersedes_id, superseded_by_id,
        origin_kind, origin_reference, embedding_status
        """;

    private static void AddUpdateParameter(
        SqliteCommand command,
        string parameterName,
        MemoryUpdateValue<string?> update)
    {
        command.Parameters.AddWithValue(parameterName + "Specified", update.IsSpecified ? 1 : 0);
        command.Parameters.AddWithValue(parameterName, (object?)update.Value ?? DBNull.Value);
    }

    private static async Task<MemoryMutationResult> ReadMutationMissAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        int expectedRevision,
        bool archive,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {MemoryColumnList} FROM memories WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return new MemoryMutationResult(MemoryMutationStatus.NotFound, null);

        var current = ReadMemory(reader);
        var status = archive && current.IsArchived
            ? MemoryMutationStatus.AlreadyArchived
            : current.Revision != expectedRevision
                ? MemoryMutationStatus.RevisionConflict
                : MemoryMutationStatus.NotFound;
        return new MemoryMutationResult(status, current);
    }

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private SqliteConnection CreateConnection()
        => SqliteConnectionFactory.Create(_connectionString);

    /// <summary>
    /// Runs one FTS <c>MATCH</c> pass and folds its rows into <paramref name="candidates"/>.
    /// Rows already present keep their earlier (higher-precision) lexical score, so a later
    /// widening pass can only add recall, never demote a precise hit.
    /// </summary>
    private async Task ExecuteFtsMatchAsync(
        SqliteConnection connection,
        string matchExpression,
        MemorySearchFilter? filter,
        int limit,
        Dictionary<string, MemoryRankingCandidate> candidates,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(matchExpression))
            return;

        await using var command = connection.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT m.id, m.agent_id, m.session_id, m.turn_index, m.source_type, m.content, m.metadata_json,
                   m.embedding, m.created_at, m.updated_at, m.expires_at, m.is_archived,
                   m.provenance, m.origin_conversation_id, m.origin_session_id,
                   m.role, m.category, m.tags_json, m.revision, m.archived_at, m.corrects_id,
                   m.supersedes_id, m.superseded_by_id, m.origin_kind, m.origin_reference, m.embedding_status,
                   -bm25(memories_fts) AS bm25_rank,
                   (julianday('now') - julianday(m.created_at)) AS age_days
            FROM memories_fts
            INNER JOIN memories m ON m.rowid = memories_fts.rowid
            WHERE memories_fts MATCH $query
              AND m.is_archived = 0
            """);

        command.Parameters.AddWithValue("$query", matchExpression);

        // The raw string literal above has no trailing newline, so the next clause must
        // start on a fresh line. Without this the unfiltered query emitted
        // "AND m.is_archived = 0ORDER BY ..." - invalid SQL that threw and silently
        // demoted every unfiltered search to the LIKE fallback.
        sql.AppendLine();

        AppendFilters(sql, command, filter);

        sql.AppendLine("ORDER BY bm25_rank DESC LIMIT $limit");
        command.Parameters.AddWithValue("$limit", limit * 5);
        command.CommandText = sql.ToString();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var entry = ReadMemory(reader);
            // bm25() is negative-is-better, so the query already negates it; clamp because
            // the ranker normalises by magnitude and a negative lexical score is meaningless.
            var bm25Rank = reader.IsDBNull(26) ? 0d : Math.Max(0d, reader.GetDouble(26));
            var ageDays = reader.IsDBNull(27) ? 0d : Math.Max(0d, reader.GetDouble(27));
            if (!candidates.ContainsKey(entry.Id))
                candidates[entry.Id] = new MemoryRankingCandidate(entry, bm25Rank, Similarity: null, ageDays);
        }
    }

    /// <summary>
    /// Builds the FTS5 <c>MATCH</c> expression explicitly instead of inheriting FTS5's
    /// default, in which a bare space between terms means AND (issue #2740).
    /// </summary>
    /// <remarks>
    /// Nothing in the original code expressed an intent to require every term; the
    /// conjunction was simply the parser default, and it made recall fall off a cliff as
    /// term count rose - one rare word was enough to guarantee zero rows. Each term is
    /// quoted so it is treated as a literal string token rather than an operator, and the
    /// terms are joined with an explicit <c>AND</c> or <c>OR</c> so the intent is visible in
    /// the expression itself and can be varied per pass by <see cref="SearchAsync"/>.
    /// </remarks>
    /// <param name="sanitizedQuery">Query text already run through the FTS sanitizer.</param>
    /// <param name="requireAllTerms">
    /// <see langword="true"/> for the precise conjunction, <see langword="false"/> for the
    /// wider disjunction used as the recall fallback.
    /// </param>
    internal static string BuildFtsMatchExpression(string sanitizedQuery, bool requireAllTerms)
    {
        var terms = SplitTerms(sanitizedQuery);
        if (terms.Length == 0)
            return string.Empty;

        var op = requireAllTerms ? " AND " : " OR ";
        return string.Join(op, terms.Select(term => $"\"{term}\""));
    }

    private static string[] SplitTerms(string sanitizedQuery)
        => string.IsNullOrWhiteSpace(sanitizedQuery)
            ? []
            : sanitizedQuery
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    /// <summary>
    /// Explains what a query did or did not match, so an empty result set is diagnosable
    /// rather than silently ambiguous (issue #2740, AC5). Reports the live row count, the
    /// MATCH expression actually used, per-term hit counts, and how many rows the strict
    /// conjunction would have matched - which is what distinguishes "nothing was ever
    /// stored" from "this query could not match by construction".
    /// </summary>
    public async Task<MemorySearchDiagnostics> ExplainSearchAsync(
        string query,
        MemorySearchFilter? filter = null,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        var sanitized = SanitizeFtsQuery(query);
        var terms = SplitTerms(sanitized);
        var conjunction = BuildFtsMatchExpression(sanitized, requireAllTerms: true);
        var disjunction = BuildFtsMatchExpression(sanitized, requireAllTerms: false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var liveRows = await CountLiveRowsAsync(connection, filter, ct).ConfigureAwait(false);

        List<MemoryTermHit> termHits = [];
        foreach (var term in terms)
        {
            var hits = await CountMatchesAsync(connection, $"\"{term}\"", filter, ct).ConfigureAwait(false);
            termHits.Add(new MemoryTermHit(term, hits));
        }

        var conjunctionRows = await CountMatchesAsync(connection, conjunction, filter, ct).ConfigureAwait(false);
        var matchedRows = conjunctionRows > 0
            ? conjunctionRows
            : await CountMatchesAsync(connection, disjunction, filter, ct).ConfigureAwait(false);
        var expressionUsed = conjunctionRows > 0 ? conjunction : disjunction;

        return new MemorySearchDiagnostics(
            query, expressionUsed, liveRows, termHits, conjunctionRows, matchedRows);
    }

    private static async Task<int> CountLiveRowsAsync(
        SqliteConnection connection, MemorySearchFilter? filter, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT COUNT(*)
            FROM memories m
            WHERE m.is_archived = 0
            """);
        sql.AppendLine();
        AppendFilters(sql, command, filter);
        command.CommandText = sql.ToString();
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountMatchesAsync(
        SqliteConnection connection, string matchExpression, MemorySearchFilter? filter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(matchExpression))
            return 0;

        await using var command = connection.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT COUNT(*)
            FROM memories_fts
            INNER JOIN memories m ON m.rowid = memories_fts.rowid
            WHERE memories_fts MATCH $query
              AND m.is_archived = 0
            """);
        sql.AppendLine();
        command.Parameters.AddWithValue("$query", matchExpression);
        AppendFilters(sql, command, filter);
        command.CommandText = sql.ToString();
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static string SanitizeFtsQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var sanitized = query
            .Replace("\"", " ", StringComparison.Ordinal)
            .Replace("'", " ", StringComparison.Ordinal)
            .Replace("(", " ", StringComparison.Ordinal)
            .Replace(")", " ", StringComparison.Ordinal)
            .Replace(":", " ", StringComparison.Ordinal)
            .Replace("*", " ", StringComparison.Ordinal)
            .Replace("+", " ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal);

        return string.Join(" ", sanitized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<MemorySearchResult> SearchWithLikeFallbackAsync(
        string sanitizedQuery,
        int limit,
        MemorySearchFilter? filter,
        double lambda,
        CancellationToken ct)
        => await SearchWithLikeFallbackWithReportAsync(
                sanitizedQuery, limit, filter, lambda, ResolveTemporalDecayPolicy(), _likeFallbackOptions, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Score-dropping projection of <see cref="SearchWithLikeFallbackScoredAsync"/>, kept so existing
    /// callers and tests that only assert ordering are unaffected by the score plumbing (#2781).
    /// </summary>
    internal async Task<IReadOnlyList<MemoryEntry>> SearchWithLikeFallbackAsync(
        string sanitizedQuery,
        int limit,
        MemorySearchFilter? filter,
        double lambda,
        MemoryLikeFallbackOptions fallbackOptions,
        CancellationToken ct)
    {
        var scored = await SearchWithLikeFallbackScoredAsync(sanitizedQuery, limit, filter, lambda, fallbackOptions, ct)
            .ConfigureAwait(false);
        return scored.Select(item => item.Entry).ToList();
    }

    /// <summary>
    /// Best-effort LIKE-based search used only when the FTS primary path errors out
    /// (syntax/corruption) or the database is transiently busy. Because
    /// <c>content LIKE '%term%'</c> uses a leading wildcard it cannot use an index and
    /// would otherwise full-scan the entire <c>memories</c> table on a path that is hit
    /// precisely when the store is already degraded. It is therefore bounded by a recency
    /// window (<see cref="MemoryLikeFallbackOptions.RecencyWindowDays"/>) and a hard scan
    /// ceiling (<see cref="MemoryLikeFallbackOptions.MaxScanRows"/>) so degraded-mode cost
    /// stays finite. This makes the fallback non-exhaustive by design; the FTS primary
    /// path is unaffected. The internal overload exists so tests can drive the fallback
    /// directly with a tight window/ceiling.
    /// </summary>
    internal async Task<IReadOnlyList<ScoredMemoryEntry>> SearchWithLikeFallbackScoredAsync(
        string sanitizedQuery,
        int limit,
        MemorySearchFilter? filter,
        double lambda,
        MemoryLikeFallbackOptions fallbackOptions,
        CancellationToken ct)
    {
        var result = await SearchWithLikeFallbackWithReportAsync(
                sanitizedQuery, limit, filter, lambda, ResolveTemporalDecayPolicy(), fallbackOptions, ct)
            .ConfigureAwait(false);
        return result.Entries;
    }

    /// <summary>
    /// The degraded-mode search core, returning the vector-scan report alongside the rows so the
    /// truncation signal survives a fall back to LIKE rather than being silently dropped there
    /// (#3244) - the degraded path is exactly where a caller most needs to know what was covered.
    /// </summary>
    private async Task<MemorySearchResult> SearchWithLikeFallbackWithReportAsync(
        string sanitizedQuery,
        int limit,
        MemorySearchFilter? filter,
        double lambda,
        MemoryTemporalDecayPolicy temporalDecayPolicy,
        MemoryLikeFallbackOptions fallbackOptions,
        CancellationToken ct)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        var terms = sanitizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (terms.Length == 0)
            return new MemorySearchResult([], MemoryVectorScanReport.NotAttempted, temporalDecayPolicy);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT m.id, m.agent_id, m.session_id, m.turn_index, m.source_type, m.content, m.metadata_json,
                   m.embedding, m.created_at, m.updated_at, m.expires_at, m.is_archived,
                   m.provenance, m.origin_conversation_id, m.origin_session_id,
                   m.role, m.category, m.tags_json, m.revision, m.archived_at, m.corrects_id,
                   m.supersedes_id, m.superseded_by_id, m.origin_kind, m.origin_reference, m.embedding_status,
                   (julianday('now') - julianday(m.created_at)) AS age_days
            FROM memories m
            WHERE m.is_archived = 0
            """);

        // See the note on the FTS path: the raw string literal has no trailing newline.
        sql.AppendLine();

        for (var i = 0; i < terms.Length; i++)
        {
            var parameterName = $"$term{i}";
            sql.AppendLine($"  AND m.content LIKE '%' || {parameterName} || '%'");
            command.Parameters.AddWithValue(parameterName, terms[i]);
        }

        // Bound the unindexable full scan to a recency window so the degraded-mode path
        // cannot drift into an unbounded table scan on a large memories table.
        if (fallbackOptions.RecencyWindowDays is { } windowDays && windowDays > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-windowDays);
            sql.AppendLine("  AND m.created_at >= $fallbackCutoff");
            command.Parameters.AddWithValue("$fallbackCutoff", cutoff.ToString("O"));
        }

        AppendFilters(sql, command, filter);

        // Hard ceiling on the candidate scan (kept >= the caller's requested slice so
        // ranking still has enough rows to order). The result is non-exhaustive by design.
        var scanCeiling = Math.Max(limit * 5, 1);
        if (fallbackOptions.MaxScanRows is { } maxRows && maxRows > 0)
            scanCeiling = Math.Min(scanCeiling, maxRows);

        sql.AppendLine("ORDER BY m.created_at DESC LIMIT $limit");
        command.Parameters.AddWithValue("$limit", scanCeiling);
        command.CommandText = sql.ToString();

        Dictionary<string, MemoryRankingCandidate> candidates = new(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var entry = ReadMemory(reader);
                var ageDays = reader.IsDBNull(26) ? 0d : Math.Max(0d, reader.GetDouble(26));
                var textScore = terms.Count(term => entry.Content.Contains(term, StringComparison.OrdinalIgnoreCase));
                candidates[entry.Id] = new MemoryRankingCandidate(entry, textScore, Similarity: null, ageDays);
            }
        }

        // The FTS index being unavailable says nothing about the embedding column, so the
        // degraded path still contributes vector evidence when a model is active. With no
        // model this collapses to exactly the previous lexical ordering.
        var lexicalIds = candidates.Keys.ToArray();
        var report = await AugmentWithVectorCandidatesAsync(connection, sanitizedQuery, candidates, filter, lexicalIds, ct)
            .ConfigureAwait(false);

        return new MemorySearchResult(
            HybridMemoryRanker.RankWithScores(candidates.Values, limit, lambda),
            report,
            temporalDecayPolicy);
    }

    private MemoryTemporalDecayPolicy ResolveTemporalDecayPolicy()
        => _temporalDecayPolicy()
            ?? throw new InvalidOperationException("The memory temporal-decay policy resolver returned null.");

    /// <summary>
    /// Adds cosine-similarity evidence to the lexical candidate set, and pulls in semantically
    /// close rows that the lexical query missed entirely.
    /// </summary>
    /// <remarks>
    /// This is where the paraphrase gap is closed: BM25 can only return rows sharing surface
    /// terms with the query, so a semantically identical memory phrased differently is
    /// invisible to it. The scan is brute-force and bounded (see
    /// <see cref="MemoryVectorSearchOptions"/>) and applies the *same*
    /// <see cref="AppendFilters"/> predicates as the lexical path, so scope, source, session,
    /// date-range and tag filtering hold identically across both halves of hybrid retrieval.
    /// <para>
    /// Every exit is a silent no-op: no configured model, a generation failure, an
    /// undecodable BLOB, or a vector stamped with a different <see cref="EmbeddingIdentity"/>
    /// all simply leave the candidate without a similarity, which the ranker reads as "no
    /// evidence" and falls back to the lexical signal for that row.
    /// </para>
    /// <para>
    /// #3244 adds two things to that. First, the scan <em>counts</em> what it examined and returns a
    /// <see cref="MemoryVectorScanReport"/>, so hitting the ceiling is a fact the caller can read
    /// rather than an invisible event. Second, a bounded second pass scores any lexical candidate
    /// the recency window excluded: without it an old row could match the query lexically yet be
    /// structurally incapable of ever receiving a similarity score, which is the asymmetry that made
    /// the truncation so hard to explain from the outside.
    /// </para>
    /// </remarks>
    /// <param name="lexicalCandidateIds">
    /// Ids already in the candidate set from the lexical pass, captured before this method runs.
    /// Any of them missed by the bounded recency scan are scored by the union pass.
    /// </param>
    private async Task<MemoryVectorScanReport> AugmentWithVectorCandidatesAsync(
        SqliteConnection connection,
        string query,
        Dictionary<string, MemoryRankingCandidate> candidates,
        MemorySearchFilter? filter,
        IReadOnlyList<string> lexicalCandidateIds,
        CancellationToken ct)
    {
        // AC6: with embeddings off there is no query vector to compare against, so the scan is not
        // merely skipped - it is never issued, and the report says so rather than claiming coverage.
        if (_embeddingService.ActiveIdentity is null)
            return MemoryVectorScanReport.NotAttempted;

        var generated = await _embeddingService.TryGenerateAsync(query, ct).ConfigureAwait(false);
        if (generated is not { } queryEmbedding)
            return MemoryVectorScanReport.NotAttempted;

        var ceiling = _vectorSearchOptions.MaxScanRows is { } maxRows && maxRows > 0 ? maxRows : (int?)null;

        HashSet<string> scanned = new(StringComparer.Ordinal);
        var rowsScanned = await ScoreVectorRowsAsync(
            connection, queryEmbedding, candidates, filter, restrictToIds: null, ceiling, scanned, ct)
            .ConfigureAwait(false);

        // AC5: rescue lexical candidates the recency window cut off. Bounded by the lexical
        // candidate count, which the FTS/LIKE passes already cap, so this cannot reintroduce an
        // unbounded scan. Skipped entirely when the first pass was exhaustive, because then every
        // eligible row - lexical or not - was already scored.
        var missedLexicalIds = ceiling is not null && rowsScanned >= ceiling
            ? lexicalCandidateIds.Where(id => !scanned.Contains(id)).ToArray()
            : [];

        var unionRows = 0;
        if (missedLexicalIds.Length > 0)
        {
            unionRows = await ScoreVectorRowsAsync(
                connection, queryEmbedding, candidates, filter, missedLexicalIds, maxScanRows: null, scanned, ct)
                .ConfigureAwait(false);
        }

        // Equality, not ">", is the whole signal: SQLite returns at most the LIMIT, so "exactly the
        // ceiling" is the only observable evidence that more rows may have existed. It deliberately
        // over-reports the boundary case where the corpus is exactly the ceiling - claiming coverage
        // we do not have would be the failure this issue exists to remove.
        var status = ceiling is not null && rowsScanned >= ceiling
            ? MemoryVectorScanStatus.PossiblyTruncated
            : MemoryVectorScanStatus.Complete;

        return new MemoryVectorScanReport(status, rowsScanned, ceiling, unionRows);
    }

    /// <summary>
    /// Runs one vector-scoring pass and folds its similarities into <paramref name="candidates"/>,
    /// returning the number of rows the database actually returned.
    /// </summary>
    /// <remarks>
    /// Single implementation for both the recency-bounded pass and the lexical-union rescue pass so
    /// the two cannot drift on filters, decoding, identity checking, or merge semantics. The row
    /// count is the count of rows READ, not of rows scored: a corrupt or identity-mismatched blob
    /// still consumed scan budget, so counting only successful scores would under-report the scan
    /// and hide a truncation.
    /// </remarks>
    /// <param name="restrictToIds">When non-null, scores only these ids and applies no row ceiling.</param>
    /// <param name="maxScanRows">Row ceiling for the recency-ordered pass, or null for no ceiling.</param>
    /// <param name="scanned">Accumulates every id the scan read, so the union pass can skip duplicates.</param>
    private async Task<int> ScoreVectorRowsAsync(
        SqliteConnection connection,
        (EmbeddingIdentity Identity, float[] Vector) queryEmbedding,
        Dictionary<string, MemoryRankingCandidate> candidates,
        MemorySearchFilter? filter,
        IReadOnlyList<string>? restrictToIds,
        int? maxScanRows,
        HashSet<string> scanned,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT m.id, m.agent_id, m.session_id, m.turn_index, m.source_type, m.content, m.metadata_json,
                   m.embedding, m.created_at, m.updated_at, m.expires_at, m.is_archived,
                   m.provenance, m.origin_conversation_id, m.origin_session_id,
                   m.role, m.category, m.tags_json, m.revision, m.archived_at, m.corrects_id,
                   m.supersedes_id, m.superseded_by_id, m.origin_kind, m.origin_reference, m.embedding_status,
                   (julianday('now') - julianday(m.created_at)) AS age_days
            FROM memories m
            WHERE m.is_archived = 0
              AND m.embedding IS NOT NULL
            """);

        // See the note on the FTS path: the raw string literal has no trailing newline.
        sql.AppendLine();

        AppendFilters(sql, command, filter);

        if (restrictToIds is { Count: > 0 })
        {
            // Parameterised id list - never string-concatenated - so a memory id can no more reach
            // the SQL text here than it can on any other path.
            var names = new string[restrictToIds.Count];
            for (var i = 0; i < restrictToIds.Count; i++)
            {
                names[i] = $"$unionId{i}";
                command.Parameters.AddWithValue(names[i], restrictToIds[i]);
            }

            sql.AppendLine($"  AND m.id IN ({string.Join(", ", names)})");
        }

        sql.AppendLine("ORDER BY m.created_at DESC");
        if (maxScanRows is { } limitRows)
        {
            sql.AppendLine("LIMIT $vectorScanLimit");
            command.Parameters.AddWithValue("$vectorScanLimit", limitRows);
        }

        command.CommandText = sql.ToString();

        var rowsRead = 0;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var entry = ReadMemory(reader);
            rowsRead++;
            scanned.Add(entry.Id);

            if (!EmbeddingBlob.TryDecode(entry.Embedding, out var storedIdentity, out var storedVector))
                continue;

            var similarity = VectorSimilarity.TryCosine(
                queryEmbedding.Identity, queryEmbedding.Vector, storedIdentity, storedVector);
            if (similarity is null)
                continue;

            var ageDays = reader.IsDBNull(26) ? 0d : Math.Max(0d, reader.GetDouble(26));
            candidates[entry.Id] = candidates.TryGetValue(entry.Id, out var existing)
                ? existing with { Similarity = similarity }
                : new MemoryRankingCandidate(entry, LexicalScore: 0d, similarity, ageDays);
        }

        return rowsRead;
    }

    /// <summary>
    /// Appends the shared <see cref="MemorySearchFilter"/> predicates (source type,
    /// session, date range, tags) and their parameters to <paramref name="sql"/> /
    /// <paramref name="command"/>. Single source of truth for the filter SQL used by both
    /// the FTS primary path and the LIKE fallback so the two cannot silently diverge.
    /// </summary>
    private static void AppendFilters(StringBuilder sql, SqliteCommand command, MemorySearchFilter? filter)
    {
        if (!string.IsNullOrWhiteSpace(filter?.SourceType))
        {
            sql.AppendLine("  AND m.source_type = $sourceType");
            command.Parameters.AddWithValue("$sourceType", filter.SourceType);
        }

        if (!string.IsNullOrWhiteSpace(filter?.SessionId))
        {
            sql.AppendLine("  AND m.session_id = $sessionId");
            command.Parameters.AddWithValue("$sessionId", filter.SessionId);
        }

        if (filter?.AfterDate is not null)
        {
            sql.AppendLine("  AND m.created_at >= $afterDate");
            command.Parameters.AddWithValue("$afterDate", filter.AfterDate.Value.ToString("O"));
        }

        if (filter?.BeforeDate is not null)
        {
            sql.AppendLine("  AND m.created_at <= $beforeDate");
            command.Parameters.AddWithValue("$beforeDate", filter.BeforeDate.Value.ToString("O"));
        }

        if (filter?.Tags is { Count: > 0 })
        {
            for (var i = 0; i < filter.Tags.Count; i++)
            {
                var parameterName = $"$tag{i}";
                sql.AppendLine("  AND EXISTS (");
                sql.AppendLine("      SELECT 1");
                sql.AppendLine("      FROM json_each(CASE WHEN m.tags_json IS NOT NULL THEN m.tags_json ELSE COALESCE(json_extract(m.metadata_json, '$.tags'), '[]') END) t");
                sql.AppendLine($"      WHERE t.value = {parameterName}");
                sql.AppendLine("  )");
                command.Parameters.AddWithValue(parameterName, filter.Tags[i]);
            }
        }
    }

    private static void BindParameters(SqliteCommand command, MemoryEntry entry)
    {
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$agentId", entry.AgentId);
        command.Parameters.AddWithValue("$sessionId", (object?)entry.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$turnIndex", (object?)entry.TurnIndex ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceType", entry.SourceType);
        command.Parameters.AddWithValue("$content", entry.Content);
        command.Parameters.AddWithValue("$metadataJson", (object?)entry.MetadataJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$embedding", (object?)entry.Embedding ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", entry.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", (object?)entry.UpdatedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$expiresAt", (object?)entry.ExpiresAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$isArchived", entry.IsArchived ? 1 : 0);
        // Provenance is normalised on write as well as on read, so a caller cannot persist a
        // value outside the closed vocabulary and have it survive to a later trust decision.
        command.Parameters.AddWithValue("$provenance", MemoryProvenance.Normalize(entry.Provenance));
        command.Parameters.AddWithValue("$originConversationId", (object?)entry.OriginConversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$originSessionId", (object?)entry.OriginSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$role", (object?)entry.Role ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", (object?)entry.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("$tagsJson", (object?)entry.TagsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", entry.Revision);
        command.Parameters.AddWithValue("$archivedAt", (object?)entry.ArchivedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$correctsId", (object?)entry.CorrectsId ?? DBNull.Value);
        command.Parameters.AddWithValue("$supersedesId", (object?)entry.SupersedesId ?? DBNull.Value);
        command.Parameters.AddWithValue("$supersededById", (object?)entry.SupersededById ?? DBNull.Value);
        command.Parameters.AddWithValue("$originKind", (object?)entry.OriginKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$originReference", (object?)entry.OriginReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$embeddingStatus", (object?)entry.EmbeddingStatus ?? DBNull.Value);
    }

    private static MemoryEntry ReadMemory(SqliteDataReader reader)
    {
        return new MemoryEntry
        {
            Id = reader.GetString(0),
            AgentId = reader.GetString(1),
            SessionId = reader.IsDBNull(2) ? null : reader.GetString(2),
            TurnIndex = reader.IsDBNull(3) ? null : reader.GetInt32(3),
            SourceType = reader.GetString(4),
            Content = reader.GetString(5),
            MetadataJson = reader.IsDBNull(6) ? null : reader.GetString(6),
            Embedding = reader.IsDBNull(7) ? null : (byte[])reader[7],
            CreatedAt = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
            UpdatedAt = reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
            ExpiresAt = reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
            IsArchived = !reader.IsDBNull(11) && reader.GetInt32(11) != 0,
            // Columns 12-14 are the additive provenance trio. A pre-provenance row (or a row from
            // a DB upgraded in place) has NULL here, which Normalize resolves to `unknown` - the
            // fail-safe, non-first-party default.
            Provenance = reader.IsDBNull(12) ? null : reader.GetString(12),
            OriginConversationId = reader.IsDBNull(13) ? null : reader.GetString(13),
            OriginSessionId = reader.IsDBNull(14) ? null : reader.GetString(14),
            Role = reader.IsDBNull(15) ? null : reader.GetString(15),
            Category = reader.IsDBNull(16) ? null : reader.GetString(16),
            TagsJson = reader.IsDBNull(17) ? null : reader.GetString(17),
            Revision = reader.GetInt32(18),
            ArchivedAt = reader.IsDBNull(19) ? null : DateTimeOffset.Parse(reader.GetString(19), CultureInfo.InvariantCulture),
            CorrectsId = reader.IsDBNull(20) ? null : reader.GetString(20),
            SupersedesId = reader.IsDBNull(21) ? null : reader.GetString(21),
            SupersededById = reader.IsDBNull(22) ? null : reader.GetString(22),
            OriginKind = reader.IsDBNull(23) ? null : reader.GetString(23),
            OriginReference = reader.IsDBNull(24) ? null : reader.GetString(24),
            EmbeddingStatus = reader.IsDBNull(25) ? null : reader.GetString(25)
        };
    }
}
