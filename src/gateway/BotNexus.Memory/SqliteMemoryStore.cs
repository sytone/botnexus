using System.Globalization;
using System.Text;
using BotNexus.Memory.Embeddings;
using BotNexus.Memory.Models;
using Microsoft.Data.Sqlite;
using System.IO.Abstractions;
using BotNexus.Persistence.Sqlite;

namespace BotNexus.Memory;

public sealed class SqliteMemoryStore(
    string dbPath,
    IFileSystem? fileSystem = null,
    MemoryLikeFallbackOptions? likeFallbackOptions = null,
    IMemoryEmbeddingService? embeddingService = null,
    MemoryVectorSearchOptions? vectorSearchOptions = null) : IMemoryStore
{
    private const double DefaultHalfLifeDays = 30d;
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
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            // #1436: filesystem-aware journal mode (WAL on local disk, DELETE on network
            // mounts) with bounded wal_autocheckpoint, consolidated into the shared helper.
            await _walMaintenance.ApplyJournalModeAsync(connection, _dbPath, cancellationToken: ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
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
                    origin_session_id TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_memories_agent_id ON memories(agent_id);
                CREATE INDEX IF NOT EXISTS idx_memories_session_id ON memories(session_id);
                CREATE INDEX IF NOT EXISTS idx_memories_created_at ON memories(created_at);

                CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts
                USING fts5(content, content='memories', content_rowid='rowid');

                CREATE TRIGGER IF NOT EXISTS memories_ai AFTER INSERT ON memories BEGIN
                    INSERT INTO memories_fts(rowid, content) VALUES (new.rowid, new.content);
                END;

                CREATE TRIGGER IF NOT EXISTS memories_ad AFTER DELETE ON memories BEGIN
                    INSERT INTO memories_fts(memories_fts, rowid, content) VALUES('delete', old.rowid, old.content);
                END;

                CREATE TRIGGER IF NOT EXISTS memories_au AFTER UPDATE ON memories BEGIN
                    INSERT INTO memories_fts(memories_fts, rowid, content) VALUES('delete', old.rowid, old.content);
                    INSERT INTO memories_fts(rowid, content) VALUES (new.rowid, new.content);
                END;

                CREATE TABLE IF NOT EXISTS schema_version (
                    version INTEGER NOT NULL
                );

                INSERT INTO schema_version(version)
                SELECT 1
                WHERE NOT EXISTS (SELECT 1 FROM schema_version);
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // #2480: an agent DB created before provenance existed has a `memories` table without
            // these columns, and CREATE TABLE IF NOT EXISTS above is a no-op against it. Adding
            // them here - additively, nullably, after the fact - is what makes a pre-provenance DB
            // open successfully instead of failing on the first SELECT naming a missing column.
            // Deliberately no backfill UPDATE: NULL is the honest record that provenance was never
            // captured for those rows, and it reads back as the fail-safe `unknown`.
            await EnsureProvenanceColumnsAsync(connection, ct).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static readonly string[] ProvenanceColumns =
        ["provenance", "origin_conversation_id", "origin_session_id"];

    /// <summary>
    /// Lazily adds the additive nullable provenance columns to an existing <c>memories</c> table.
    /// </summary>
    /// <remarks>
    /// The upstream lesson this implements (#2480) is that a store must never reject an older DB at
    /// open. Each column is added independently and a duplicate-column error is swallowed, so the
    /// upgrade is idempotent and safe against a concurrent process that added the column first.
    /// </remarks>
    private static async Task EnsureProvenanceColumnsAsync(SqliteConnection connection, CancellationToken ct)
    {
        HashSet<string> existing = new(StringComparer.OrdinalIgnoreCase);
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "PRAGMA table_info(memories);";
            await using var reader = await probe.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                existing.Add(reader.GetString(1));
        }

        foreach (var column in ProvenanceColumns)
        {
            if (existing.Contains(column))
                continue;

            try
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE memories ADD COLUMN {column} TEXT NULL;";
                await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                // Another process won the race and added it. The end state is what matters.
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
            var toInsert = entry with { Id = id, CreatedAt = createdAt };

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
                    provenance, origin_conversation_id, origin_session_id)
                VALUES (
                    $id, $agentId, $sessionId, $turnIndex, $sourceType, $content, $metadataJson,
                    $embedding, $createdAt, $updatedAt, $expiresAt, $isArchived,
                    $provenance, $originConversationId, $originSessionId)
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
                       provenance, origin_conversation_id, origin_session_id
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
                       provenance, origin_conversation_id, origin_session_id
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
        await InitializeAsync(ct).ConfigureAwait(false);
        var sanitized = SanitizeFtsQuery(query);
        if (string.IsNullOrWhiteSpace(sanitized))
            return [];

        var limit = Math.Clamp(topK, 1, 100);
        var lambda = Math.Log(2d) / DefaultHalfLifeDays;
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

            await AugmentWithVectorCandidatesAsync(connection, query, candidates, filter, ct).ConfigureAwait(false);

            return HybridMemoryRanker.RankWithScores(candidates.Values, limit, lambda);
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
            var sizeBytes = _fileSystem.File.Exists(_dbPath) ? _fileSystem.FileInfo.New(_dbPath).Length : 0L;
            return new MemoryStoreStats(count, sizeBytes, lastIndexedAt);
        }, ct).ConfigureAwait(false);
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
            var bm25Rank = reader.IsDBNull(15) ? 0d : Math.Max(0d, reader.GetDouble(15));
            var ageDays = reader.IsDBNull(16) ? 0d : Math.Max(0d, reader.GetDouble(16));
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

    private Task<IReadOnlyList<ScoredMemoryEntry>> SearchWithLikeFallbackAsync(
        string sanitizedQuery,
        int limit,
        MemorySearchFilter? filter,
        double lambda,
        CancellationToken ct)
        => SearchWithLikeFallbackScoredAsync(sanitizedQuery, limit, filter, lambda, _likeFallbackOptions, ct);

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
        await InitializeAsync(ct).ConfigureAwait(false);

        var terms = sanitizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (terms.Length == 0)
            return [];

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT m.id, m.agent_id, m.session_id, m.turn_index, m.source_type, m.content, m.metadata_json,
                   m.embedding, m.created_at, m.updated_at, m.expires_at, m.is_archived,
                   m.provenance, m.origin_conversation_id, m.origin_session_id,
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
                var ageDays = reader.IsDBNull(15) ? 0d : Math.Max(0d, reader.GetDouble(15));
                var textScore = terms.Count(term => entry.Content.Contains(term, StringComparison.OrdinalIgnoreCase));
                candidates[entry.Id] = new MemoryRankingCandidate(entry, textScore, Similarity: null, ageDays);
            }
        }

        // The FTS index being unavailable says nothing about the embedding column, so the
        // degraded path still contributes vector evidence when a model is active. With no
        // model this collapses to exactly the previous lexical ordering.
        await AugmentWithVectorCandidatesAsync(connection, sanitizedQuery, candidates, filter, ct).ConfigureAwait(false);

        return HybridMemoryRanker.RankWithScores(candidates.Values, limit, lambda);
    }

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
    /// </remarks>
    private async Task AugmentWithVectorCandidatesAsync(
        SqliteConnection connection,
        string query,
        Dictionary<string, MemoryRankingCandidate> candidates,
        MemorySearchFilter? filter,
        CancellationToken ct)
    {
        if (_embeddingService.ActiveIdentity is null)
            return;

        var generated = await _embeddingService.TryGenerateAsync(query, ct).ConfigureAwait(false);
        if (generated is not { } queryEmbedding)
            return;

        await using var command = connection.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT m.id, m.agent_id, m.session_id, m.turn_index, m.source_type, m.content, m.metadata_json,
                   m.embedding, m.created_at, m.updated_at, m.expires_at, m.is_archived,
                   m.provenance, m.origin_conversation_id, m.origin_session_id,
                   (julianday('now') - julianday(m.created_at)) AS age_days
            FROM memories m
            WHERE m.is_archived = 0
              AND m.embedding IS NOT NULL
            """);

        // See the note on the FTS path: the raw string literal has no trailing newline.
        sql.AppendLine();

        AppendFilters(sql, command, filter);

        sql.AppendLine("ORDER BY m.created_at DESC");
        if (_vectorSearchOptions.MaxScanRows is { } maxRows && maxRows > 0)
        {
            sql.AppendLine("LIMIT $vectorScanLimit");
            command.Parameters.AddWithValue("$vectorScanLimit", maxRows);
        }

        command.CommandText = sql.ToString();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var entry = ReadMemory(reader);
            if (!EmbeddingBlob.TryDecode(entry.Embedding, out var storedIdentity, out var storedVector))
                continue;

            var similarity = VectorSimilarity.TryCosine(
                queryEmbedding.Identity, queryEmbedding.Vector, storedIdentity, storedVector);
            if (similarity is null)
                continue;

            var ageDays = reader.IsDBNull(15) ? 0d : Math.Max(0d, reader.GetDouble(15));
            candidates[entry.Id] = candidates.TryGetValue(entry.Id, out var existing)
                ? existing with { Similarity = similarity }
                : new MemoryRankingCandidate(entry, LexicalScore: 0d, similarity, ageDays);
        }
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
                sql.AppendLine("      FROM json_each(COALESCE(m.metadata_json, '{}'), '$.tags') t");
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
            OriginSessionId = reader.IsDBNull(14) ? null : reader.GetString(14)
        };
    }
}
