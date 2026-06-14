using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using System.IO.Abstractions;
using AgentId = BotNexus.Domain.Primitives.AgentId;
using SessionId = BotNexus.Domain.Primitives.SessionId;
using ChannelKey = BotNexus.Domain.Primitives.ChannelKey;
using SessionType = BotNexus.Domain.Primitives.SessionType;
using SessionParticipant = BotNexus.Domain.Primitives.SessionParticipant;
using ConversationId = BotNexus.Domain.Primitives.ConversationId;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Sessions;

// Gateway.Sessions is a reusable library — it uses ConfigureAwait(false) on all awaited
// tasks to prevent deadlocks when consumed by callers with a synchronization context.
// The BotNexus.Gateway library omits ConfigureAwait(false) because its classes are hosted
// exclusively on the generic host thread pool which has no synchronization context.
// The BotNexus.Gateway.Api ASP.NET host also omits it (no sync context in ASP.NET Core).

/// <summary>
/// File-backed session store using JSONL format (one entry per line) with a JSON metadata sidecar.
/// Inspired by the archive's <c>SessionManager</c> but implements the new <see cref="ISessionStore"/> contract.
/// </summary>
/// <remarks>
/// <para>Storage layout:</para>
/// <list type="bullet">
///   <item><c>{storePath}/{sessionId}.jsonl</c> — Session history, one JSON entry per line.</item>
///   <item><c>{storePath}/{sessionId}.meta.json</c> — Metadata (agent, channel, caller, timestamps).</item>
/// </list>
/// <para>Thread-safe via <see cref="SemaphoreSlim"/>. Suitable for single-instance deployments.</para>
/// </remarks>
public sealed class FileSessionStore : SessionStoreBase
{
    private static readonly ActivitySource ActivitySource = new("BotNexus.Gateway");
    private readonly string _storePath;
    private readonly IFileSystem _fileSystem;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<SessionId, GatewaySession> _cache = [];
    private readonly ILogger<FileSessionStore> _logger;
    private readonly ISecretRedactor? _redactor;
    private readonly LegacyConversationResolver _legacyResolver;
    private readonly IConversationStore _conversationStore;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ConversationId, AgentId> _agentIdCache = new();

    // Phase 9 / P9-B-2 (#627): eager startup sweep mirrors
    // SqliteSessionStore.MigrateOrphanedSessionsAsync. A SemaphoreSlim + bool flag
    // (rather than a cached Task) so a cancelled first caller cannot poison subsequent
    // calls — if the first attempt throws OperationCanceledException the next caller
    // re-enters the lock and tries again with its own token.
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private bool _migrated;

    // Test probe (#627): increments exactly once per MigrateOrphanedSessionsAsync entry.
    // Counted via Interlocked because EnsureMigratedAsync is called from multiple
    // concurrent task contexts before the lock is acquired. Exposed via
    // <see cref="MigrationInvocationCount"/> so the concurrent-callers test can prove
    // the sweep ran exactly once, decoupled from inner resolver ListAsync call counts
    // (the resolver may issue 1-3 ListAsync calls per single sweep depending on cache
    // state, so counting ListAsync at the conversation store conflates correctness
    // with resolver implementation detail).
    private int _migrationInvocationCount;
    internal int MigrationInvocationCount => Volatile.Read(ref _migrationInvocationCount);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Initialises a new <see cref="FileSessionStore"/>.
    /// </summary>
    /// <param name="storePath">Directory used for session JSONL + metadata sidecar files.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="fileSystem">Abstracted file system (real or mocked).</param>
    /// <param name="conversationStore">
    /// Mandatory post-P9-I (issue #674): durable agent ownership lives on
    /// <see cref="Conversation.AgentId"/>, and sidecar JSON no longer carries an
    /// <c>agentId</c> field for new writes. The store uses the conversation store to
    /// (a) backfill any legacy orphan sidecars to the agent's <c>legacy:{agentId}</c>
    /// conversation, (b) hydrate <see cref="GatewaySession.AgentId"/> on every load
    /// from <see cref="Conversation.AgentId"/> (cached per <see cref="ConversationId"/>;
    /// safe because <c>Conversation.AgentId</c> is init-only per
    /// <c>ConversationAgentIdImmutabilityArchitectureTests</c>), and (c) stamp
    /// <see cref="Session.ConversationId"/> defensively at save time.
    /// </param>
    /// <param name="redactor">When provided, secrets in content are redacted before storage.</param>
    public FileSessionStore(
        string storePath,
        ILogger<FileSessionStore> logger,
        IFileSystem fileSystem,
        IConversationStore conversationStore,
        ISecretRedactor? redactor = null)
        : base(conversationStore)
    {
        _storePath = storePath;
        _logger = logger;
        _fileSystem = fileSystem;
        _redactor = redactor;
        _conversationStore = conversationStore ?? throw new ArgumentNullException(nameof(conversationStore));
        _legacyResolver = new LegacyConversationResolver(conversationStore, logger: null);
        _fileSystem.Directory.CreateDirectory(storePath);
    }

    /// <inheritdoc />
    public override async Task<GatewaySession?> GetAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.get", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", sessionId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(sessionId, out var cached))
                return cached;

            return await LoadFromFileAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public override async Task<GatewaySession> GetOrCreateAsync(SessionId sessionId, AgentId agentId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.get_or_create", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", sessionId);
        activity?.SetTag("botnexus.agent.id", agentId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(sessionId, out var cached))
                return cached;

            var loaded = await LoadFromFileAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (loaded is not null)
            {
                _cache[sessionId] = loaded;
                return loaded;
            }

            var session = CreateSession(sessionId, agentId, null, _redactor);
            _cache[sessionId] = session;
            return session;
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public override async Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.save", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", session.SessionId);
        activity?.SetTag("botnexus.agent.id", session.AgentId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        // Backfill outside the per-store lock — the resolver may call the conversation
        // store which has its own locking; nesting would risk lock-ordering issues
        // and there's no shared mutable state between resolution and write here.
        await EnsureConversationIdStampedAsync(session, cancellationToken).ConfigureAwait(false);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cache[session.SessionId] = session;
            await WriteToFileAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public override async Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.delete", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", sessionId);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cache.Remove(sessionId);
            var historyPath = GetHistoryPath(sessionId);
            var metaPath = GetMetaPath(sessionId);
            if (_fileSystem.File.Exists(historyPath)) _fileSystem.File.Delete(historyPath);
            if (_fileSystem.File.Exists(metaPath)) _fileSystem.File.Delete(metaPath);
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public override async Task ArchiveAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cache.Remove(sessionId);
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
            var historyPath = GetHistoryPath(sessionId);
            var metaPath = GetMetaPath(sessionId);

            if (_fileSystem.File.Exists(historyPath))
                _fileSystem.File.Move(historyPath, $"{historyPath}.archived.{timestamp}");
            if (_fileSystem.File.Exists(metaPath))
                _fileSystem.File.Move(metaPath, $"{metaPath}.archived.{timestamp}");
        }
        finally { _lock.Release(); }
    }

    protected override async Task<IReadOnlyList<GatewaySession>> EnumerateSessionsAsync(CancellationToken cancellationToken)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sessions = new List<GatewaySession>();
            foreach (var metaFile in _fileSystem.Directory.GetFiles(_storePath, "*.meta.json"))
            {
                var sessionId = SessionId.From(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(metaFile)));
                var session = _cache.GetValueOrDefault(sessionId) ?? await LoadFromFileAsync(sessionId, cancellationToken).ConfigureAwait(false);
                if (session is null)
                    continue;
                
                sessions.Add(session);
            }

            return sessions;
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Indexed-by-sidecar override of <see cref="SessionStoreBase.ListByConversationAsync"/>.
    /// The base default enumerates (and fully hydrates the history JSONL of) <em>every</em>
    /// session in the store and filters in memory — O(total sessions) with per-session file
    /// I/O. Here we read only the lightweight <c>.meta.json</c> sidecars (which carry
    /// <see cref="SessionMeta.ConversationId"/>) to pick the matching session ids first, and
    /// fully load <em>only</em> the matches. That turns the full-history reads from N (every
    /// session) into M (sessions in this conversation), removing the asymptotic cliff while
    /// preserving identical results and ordering to the base implementation (#1386).
    /// </summary>
    /// <remarks>
    /// Runs <see cref="EnsureMigratedAsync"/> first so pre-Phase-9 orphan sidecars have been
    /// stamped with their legacy conversation (and rewritten) — after migration the sidecar
    /// <c>ConversationId</c> is authoritative. A residual orphan with no persisted
    /// <c>ConversationId</c> but a recorded <c>AgentId</c> is still treated as a candidate and
    /// routed through the full load path, where legacy-orphan recovery stamps and matches it
    /// exactly as the base path would — so a query for a <c>legacy:{agentId}</c> conversation
    /// is not silently dropped. AgentId filtering uses the load-time <em>hydrated</em> AgentId
    /// (Conversation.AgentId), identical to the base.
    /// </remarks>
    public override async Task<IReadOnlyList<GatewaySession>> ListByConversationAsync(
        ConversationId conversationId,
        AgentId? agentId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Phase 1: cheap sidecar scan — pick candidate session ids without reading any
            // history JSONL. meta.ConversationId is authoritative post-migration; a
            // null-conversation-but-has-AgentId orphan is kept as a candidate so the full
            // load path's legacy-orphan recovery can stamp + match it (base parity).
            var candidateIds = new List<SessionId>();
            foreach (var metaFile in _fileSystem.Directory.GetFiles(_storePath, "*.meta.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sessionId = SessionId.From(
                    Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(metaFile)));

                // A cached (already-hydrated) session is the cheapest discriminator.
                if (_cache.TryGetValue(sessionId, out var cached))
                {
                    if (cached.ConversationId.IsInitialized() && cached.ConversationId == conversationId)
                        candidateIds.Add(sessionId);
                    continue;
                }

                SessionMeta? meta;
                try
                {
                    meta = await SessionMetadataSidecar.ReadAsync<SessionMeta>(
                        _fileSystem,
                        metaFile,
                        JsonOptions,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
                {
                    // Unreadable sidecar — let the full load path surface it (mirrors
                    // EnumerateSessionsAsync, which routes everything through LoadFromFileAsync).
                    candidateIds.Add(sessionId);
                    continue;
                }

                if (meta is null)
                    continue;

                if (meta.ConversationId is { } convId && convId == conversationId)
                {
                    candidateIds.Add(sessionId);
                }
                else if (meta.ConversationId is null && meta.AgentId is not null)
                {
                    // Residual orphan: legacy-stamp happens on full load. Defer the match
                    // decision to LoadFromFileAsync so we never silently drop a legacy hit.
                    candidateIds.Add(sessionId);
                }
            }

            // Phase 2: full-load only the candidates (history + AgentId hydration), then
            // apply the exact base contract: keep only sessions whose hydrated ConversationId
            // matches, optionally narrow to the owning agent, ordered chronologically.
            var matched = new List<GatewaySession>(candidateIds.Count);
            foreach (var sessionId in candidateIds)
            {
                var session = _cache.GetValueOrDefault(sessionId)
                    ?? await LoadFromFileAsync(sessionId, cancellationToken).ConfigureAwait(false);
                if (session is null)
                    continue;
                if (!session.ConversationId.IsInitialized() || session.ConversationId != conversationId)
                    continue;
                if (agentId is not null && session.AgentId != agentId)
                    continue;
                matched.Add(session);
            }

            return matched
                .OrderBy(session => session.CreatedAt)
                .ThenBy(session => session.SessionId.Value, StringComparer.Ordinal)
                .ToList();
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// One-time eager sweep that mirrors <c>SqliteSessionStore.MigrateOrphanedSessionsAsync</c>:
    /// every pre-Phase-9 sidecar on disk with no <c>ConversationId</c> is grouped by agent,
    /// linked to that agent's <c>legacy:{agentId}</c> conversation via
    /// <see cref="LegacyConversationResolver"/>, and the most-recently-updated Active orphan
    /// per agent is bound as the conversation's <see cref="Conversation.ActiveSessionId"/>.
    /// Safe to run on every startup — no-op when no orphans exist or no resolver is wired.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="SemaphoreSlim"/> + <c>_migrated</c> flag (NOT a cached
    /// <see cref="Task"/>) so a cancelled first caller does not poison subsequent callers
    /// — they re-enter the lock and retry with their own token.
    /// </remarks>
    private async Task EnsureMigratedAsync(CancellationToken cancellationToken)
    {
        if (_migrated) return;
        if (_legacyResolver is null) { _migrated = true; return; }

        await _migrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_migrated) return;
            try
            {
                Interlocked.Increment(ref _migrationInvocationCount);
                await MigrateOrphanedSessionsAsync(cancellationToken).ConfigureAwait(false);
                // P9-F (#657): forward legacy sidecar Participants entries into the
                // conversation store's normalised participant set. Idempotent — runs once
                // per process, mirrors the Sqlite-side backfill.
                if (_conversationStore is not null)
                    await BackfillParticipantsToConversationsAsync(_conversationStore, cancellationToken).ConfigureAwait(false);
                _migrated = true;
            }
            catch (OperationCanceledException)
            {
                // Do NOT set _migrated — let the next caller retry with their own token.
                throw;
            }
        }
        finally { _migrationLock.Release(); }
    }

    /// <summary>
    /// Scans the store directory for sidecar files with no persisted <c>ConversationId</c>,
    /// groups by agent, stamps each group with the agent's legacy conversation, rewrites
    /// every orphan sidecar so the stamp is durable, and binds the most-recently-updated
    /// Active orphan per agent as the conversation's active session pointer. Mirrors
    /// <c>SqliteSessionStore.MigrateOrphanedSessionsAsync</c>; runs once per process via
    /// <see cref="EnsureMigratedAsync"/>.
    /// </summary>
    private async Task MigrateOrphanedSessionsAsync(CancellationToken cancellationToken)
    {
        // P9-I (#674): _legacyResolver is non-null post-P9-I (conversationStore is mandatory).
        // Pass 1: enumerate orphan sidecars (ConversationId is null or absent on disk).
        // Hold the read lock briefly to avoid racing with concurrent SaveAsync writes.
        var orphans = new List<(SessionId SessionId, AgentId AgentId, SessionStatus Status, DateTimeOffset UpdatedAt)>();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var metaFile in _fileSystem.Directory.GetFiles(_storePath, "*.meta.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                SessionMeta? meta;
                try
                {
                    meta = await SessionMetadataSidecar.ReadAsync<SessionMeta>(
                        _fileSystem,
                        metaFile,
                        JsonOptions,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Skip unreadable sidecars; do not fail the entire sweep on a single
                    // corrupted file. The load path will surface the same error if the
                    // sidecar is later requested by id.
                    _logger.LogWarning(ex,
                        "Skipping unreadable sidecar {SidecarPath} during orphan sweep.",
                        metaFile);
                    continue;
                }

                if (meta is null) continue;
                if (meta.ConversationId is not null) continue;

                // P9-I (#674): post-P9-I sidecars write AgentId=null. An orphan with no
                // AgentId is unrecoverable here (no anchor to find a legacy conversation).
                // Skip — LoadFromFileAsync will throw a precise error if the session is
                // ever requested.
                if (meta.AgentId is null)
                {
                    _logger.LogWarning(
                        "Skipping sidecar {SidecarPath} during orphan sweep: ConversationId is null AND AgentId is null (unrecoverable).",
                        metaFile);
                    continue;
                }

                var sessionId = SessionId.From(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(metaFile)));
                orphans.Add((sessionId, meta.AgentId.Value, meta.Status, meta.UpdatedAt));
            }
        }
        finally { _lock.Release(); }

        if (orphans.Count == 0) return;

        // Pass 2: group by agent, resolve legacy conversation per agent, bind the
        // most-recently-updated Active orphan as the conversation's active pointer,
        // then stamp each orphan sidecar in turn. Bind MUST run BEFORE the stamp loop
        // because LoadFromFileAsync has its own load-time backfill that calls
        // BindActiveSessionIfNoneAsync(firstSessionLoaded). Without this ordering, the
        // alphabetically-first orphan would silently win the bind race against the
        // most-recently-updated orphan. Resolver call must be outside the per-store
        // lock — it acquires its own per-agent semaphore plus the conversation store's
        // lock; nesting risks deadlock.
        var totalMigrated = 0;
        foreach (var group in orphans.GroupBy(o => o.AgentId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var agentId = group.Key;
            var legacy = await _legacyResolver.ResolveAsync(agentId, cancellationToken).ConfigureAwait(false);

            // Bind first: pick the most-recently-updated Active orphan. Sealed/Suspended/
            // Expired orphans are not bound — they're history, not live work. The bind is
            // first-wins via the resolver's per-agent semaphore, so subsequent
            // load-time-backfill BindActiveSessionIfNoneAsync calls in this loop are
            // no-ops once this completes.
            var activeOrphans = group
                .Where(o => o.Status == SessionStatus.Active)
                .OrderByDescending(o => o.UpdatedAt)
                .ToList();
            if (activeOrphans.Count > 0)
            {
                await _legacyResolver.BindActiveSessionIfNoneAsync(
                    legacy,
                    activeOrphans[0].SessionId,
                    cancellationToken).ConfigureAwait(false);
            }

            // Reload + rewrite each orphan sidecar so the stamp is durable on disk.
            foreach (var orphan in group)
            {
                cancellationToken.ThrowIfCancellationRequested();

                GatewaySession? session;
                await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_cache.TryGetValue(orphan.SessionId, out var cached))
                    {
                        session = cached;
                    }
                    else
                    {
                        session = await LoadFromFileAsync(orphan.SessionId, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally { _lock.Release(); }

                if (session is null) continue;

                // LoadFromFileAsync already backfills via the resolver path — but only when
                // the session is loaded individually. The eager sweep guarantees every orphan
                // gets touched even when no caller ever explicitly requests it.
                if (!session.ConversationId.IsInitialized())
                {
                    session.ConversationId = legacy.ConversationId;
                    await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await WriteToFileAsync(session, cancellationToken).ConfigureAwait(false);
                    }
                    finally { _lock.Release(); }
                }
                totalMigrated++;
            }
        }

        _logger.LogInformation(
            "Orphaned session migration (file store): linked {Count} session(s) across {AgentCount} agent(s) to their legacy conversations.",
            totalMigrated, orphans.Select(o => o.AgentId).Distinct().Count());
    }

    /// <summary>
    /// P9-F (#657): one-shot startup backfill that forwards legacy sidecar
    /// <c>SessionMeta.Participants</c> entries into the conversation store's normalised
    /// participant set. Mirrors the SqliteSessionStore-side scan; reads each sidecar,
    /// groups by ConversationId and dispatches to
    /// <see cref="IConversationStore.AddParticipantsAsync"/>. Idempotent — safe to re-run.
    /// Sidecars with no <c>ConversationId</c> are skipped here because the orphan
    /// migration immediately preceding this call already stamps a legacy conversation
    /// onto them.
    /// </summary>
    private async Task BackfillParticipantsToConversationsAsync(
        IConversationStore conversationStore,
        CancellationToken cancellationToken)
    {
        var byConversation = new Dictionary<string, List<SessionParticipant>>(StringComparer.Ordinal);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var metaFile in _fileSystem.Directory.GetFiles(_storePath, "*.meta.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var meta = await SessionMetadataSidecar.ReadAsync<SessionMeta>(
                    _fileSystem,
                    metaFile,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                if (meta is null) continue;
                if (meta.ConversationId is null) continue;
                if (meta.Participants is null || meta.Participants.Count == 0) continue;

                var key = meta.ConversationId.Value.Value;
                if (!byConversation.TryGetValue(key, out var list))
                {
                    list = [];
                    byConversation[key] = list;
                }
                list.AddRange(meta.Participants);
            }
        }
        finally { _lock.Release(); }

        if (byConversation.Count == 0)
            return;

        var totalRows = 0;
        foreach (var (convIdString, participants) in byConversation)
        {
            var convId = ConversationId.From(convIdString);
            var deduped = participants
                .GroupBy(p => p.CitizenId)
                .Select(g => g.First())
                .ToList();
            await conversationStore.AddParticipantsAsync(convId, deduped, cancellationToken).ConfigureAwait(false);
            totalRows += deduped.Count;
        }

        _logger.LogInformation(
            "Participant backfill (file session store): forwarded {Count} participant entries across {ConvCount} conversation(s).",
            totalRows, byConversation.Count);
    }

    /// <summary>
    /// P9-I (#674): hydrates <see cref="GatewaySession.AgentId"/> on a loaded session
    /// from <see cref="Conversation.AgentId"/>. Uses a positive-only
    /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
    /// cache keyed by <see cref="ConversationId"/>; safe because
    /// <c>Conversation.AgentId</c> is init-only.
    /// </summary>
    private async Task HydrateAgentIdAsync(GatewaySession session, CancellationToken cancellationToken)
    {
        if (!session.ConversationId.IsInitialized())
        {
            throw new InvalidOperationException(
                $"Session '{session.SessionId.Value}' has an unset ConversationId after load. " +
                "Post-P9-I, every session sidecar is guaranteed to carry a non-null conversationId " +
                "(the eager startup sweep runs before this load could be served). " +
                "Either the sidecar was modified externally or the migration failed silently — " +
                "inspect FileSessionStore logs at startup.");
        }

        if (_agentIdCache.TryGetValue(session.ConversationId, out var cached))
        {
            session.HydrateAgentId(cached);
            return;
        }

        var conversation = await _conversationStore.GetAsync(session.ConversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
        {
            throw new InvalidOperationException(
                $"Session '{session.SessionId.Value}' references conversation '{session.ConversationId.Value}' " +
                "which does not exist in the conversation store. AgentId cannot be hydrated. " +
                "This indicates that the conversation was deleted while the sidecar remained — " +
                "the session is unrecoverable.");
        }

        _agentIdCache[session.ConversationId] = conversation.AgentId;
        session.HydrateAgentId(conversation.AgentId);
    }

    private async Task<GatewaySession?> LoadFromFileAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        var metaPath = GetMetaPath(sessionId);
        var meta = await SessionMetadataSidecar.ReadAsync<SessionMeta>(
            _fileSystem,
            metaPath,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (meta is null) return null;

        var session = new GatewaySession(new Session
        {
            SessionId = sessionId,
            ChannelType = meta.ChannelType,
            SessionType = meta.SessionType ?? InferSessionType(sessionId, meta.ChannelType),
            // P9-F (#657): meta.Participants is read-and-discarded. The legacy field is
            // retained on SessionMeta for the one-shot backfill into the conversation
            // store; participants are no longer assigned to Session.
            CreatedAt = meta.CreatedAt,
            UpdatedAt = meta.UpdatedAt,
            Status = meta.Status,
            ExpiresAt = meta.ExpiresAt
            // ConversationId is intentionally omitted when meta.ConversationId is null --
            // the property defaults to an uninitialized ConversationId (the "unset" sentinel)
            // and the legacy-orphan recovery below fires on it. Writing
            // `default(ConversationId)` explicitly is prohibited by Vogen analyzer VOG009.
        }, _redactor)
        {
            // P9-I (#674): CallerId is the only sidecar-sourced runtime field. AgentId is
            // hydrated below (post legacy-orphan recovery) from Conversation.AgentId.
            CallerId = meta.CallerId
        };
        if (meta.ConversationId is not null)
        {
            session.ConversationId = meta.ConversationId.Value;
        }
        if (meta.Metadata is not null)
        {
            foreach (var pair in meta.Metadata)
                session.Metadata[pair.Key] = pair.Value;
        }
        session.StreamReplay.SetState(meta.PersistedNextSequenceId, meta.StreamEvents);

        var historyPath = GetHistoryPath(sessionId);
        var entries = await SessionJsonl.ReadAllAsync<SessionEntry>(
            _fileSystem,
            historyPath,
            JsonOptions,
            _logger,
            "session history",
            cancellationToken).ConfigureAwait(false);
        if (entries.Count > 0)
        {
            // Phase 3a (#531): preserve every entry; collapse pre-Phase-3a multi-summary
            // state forward via SessionCompaction.ApplyLegacyHistoryProjection. The LLM-visible
            // filter (IsHistory + IsCrashSentinel) is applied downstream by the projector.
            session.AddEntries(SessionCompaction.ApplyLegacyHistoryProjection(entries));
            session.UpdatedAt = meta.UpdatedAt;
        }

        // P9-I (#674): legacy-orphan recovery — pre-Phase-9 sidecars carried `agentId` but
        // no `conversationId`. Stamp the legacy conversation using the sidecar's recorded
        // AgentId (the ONLY legitimate read of meta.AgentId post-P9-I) and rewrite the
        // sidecar so subsequent loads observe the stamp.
        if (!session.ConversationId.IsInitialized())
        {
            if (meta.AgentId is null)
            {
                throw new InvalidOperationException(
                    $"Session sidecar '{metaPath}' has neither ConversationId nor AgentId — " +
                    "unrecoverable orphan. Inspect the sidecar manually or delete it.");
            }

            var legacy = await _legacyResolver.ResolveAsync(meta.AgentId.Value, cancellationToken).ConfigureAwait(false);
            session.ConversationId = legacy.ConversationId;

            if (session.Status == SessionStatus.Active)
            {
                await _legacyResolver.BindActiveSessionIfNoneAsync(legacy, session.SessionId, cancellationToken).ConfigureAwait(false);
            }

            await WriteToFileAsync(session, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "Backfilled orphan sidecar for session {SessionId} (legacy AgentId {AgentId}) with conversation {LegacyConversationId} (sidecar rewritten).",
                session.SessionId, meta.AgentId.Value, legacy.ConversationId);
        }

        // P9-I (#674): hydrate AgentId from Conversation.AgentId after any legacy-orphan
        // recovery. Every load path (GetAsync, GetOrCreateAsync, EnumerateSessionsAsync,
        // ListByConversationAsync) routes through LoadFromFileAsync so callers always
        // observe a hydrated AgentId.
        await HydrateAgentIdAsync(session, cancellationToken).ConfigureAwait(false);

        return session;
    }

    /// <summary>
    /// Defensively stamps a session whose <see cref="Session.ConversationId"/> is unset
    /// (<c>default(ConversationId)</c>) at save time with the agent's
    /// <c>legacy:{agentId}</c> conversation, persisting the stamp before the sidecar is
    /// written so subsequent reads see a consistent view.
    /// </summary>
    private async Task EnsureConversationIdStampedAsync(GatewaySession session, CancellationToken cancellationToken)
    {
        if (session.ConversationId.IsInitialized())
            return;

        var legacy = await _legacyResolver.ResolveAsync(session.AgentId, cancellationToken).ConfigureAwait(false);
        session.ConversationId = legacy.ConversationId;

        // If this is the current active session, also bind it as the conversation's
        // active session pointer so the canonical reset path (DefaultConversationResetService)
        // can find it. Sealed/Suspended/Expired sessions are not bound.
        if (session.Status == SessionStatus.Active)
        {
            await _legacyResolver.BindActiveSessionIfNoneAsync(legacy, session.SessionId, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "Session {SessionId} for agent {AgentId} was saved with no ConversationId; stamped legacy conversation {LegacyConversationId}.",
            session.SessionId, session.AgentId, legacy.ConversationId);
    }

    private async Task WriteToFileAsync(GatewaySession session, CancellationToken cancellationToken)
    {
        var historyPath = GetHistoryPath(session.SessionId);
        await SessionJsonl.WriteAllAsync(
            _fileSystem,
            historyPath,
            session.GetHistorySnapshot(),
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        var metaPath = GetMetaPath(session.SessionId);
        var meta = new SessionMeta(
            // P9-I (#674): never write AgentId on new sidecars. Agent ownership is hydrated
            // from Conversation.AgentId on load. Pre-existing sidecars that still carry it
            // are read by LoadFromFileAsync's legacy-orphan recovery path; subsequent
            // saves (e.g. after backfill rewrites the sidecar) drop the field.
            null,
            session.ChannelType,
            session.CallerId,
            session.SessionType,
            // P9-F (#657): never write Participants on new sidecars — the legacy field is
            // retained on the record for back-compat reads (and the backfill scan) but
            // not populated. Sending `null` preserves the JSON shape without re-emitting
            // stale data on every save.
            null,
            session.CreatedAt,
            session.UpdatedAt,
            session.Status,
            session.ExpiresAt,
            session.StreamReplay.NextSequenceId,
            [.. session.StreamReplay.GetEventSnapshot()],
            // P9-B-2: Session.ConversationId is now non-nullable, but the SessionMeta
            // sidecar still carries it as ConversationId? so back-compat readers see
            // `null` for orphan rows (preserving the no-resolver back-compat path).
            // Without the IsInitialized check, the unset Vogen default would wrap into
            // Nullable<ConversationId>(HasValue=true, Value=default) and the Vogen STJ
            // converter would throw "uninitialized value object" at write time.
            session.ConversationId.IsInitialized() ? session.ConversationId : (ConversationId?)null,
            session.Metadata.Count == 0 ? null : new Dictionary<string, object?>(session.Metadata));
        await SessionMetadataSidecar.WriteAsync(
            _fileSystem,
            metaPath,
            meta,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
    }

    private string GetHistoryPath(SessionId sessionId) => Path.Combine(_storePath, SessionFileNames.HistoryFileName(sessionId.Value));
    private string GetMetaPath(SessionId sessionId) => Path.Combine(_storePath, SessionFileNames.MetadataFileName(sessionId.Value));

    private sealed record SessionMeta(
        // P9-I (#674): AgentId is now NULLABLE. New writes ALWAYS pass null — durable
        // agent ownership lives on Conversation.AgentId post-P9-I and is hydrated on
        // load via Conversation.AgentId. The legacy field is retained for back-compat
        // reads (pre-P9 orphan-sidecar recovery in LoadFromFileAsync) and for the
        // one-shot orphan-migration scan in MigrateOrphanedSessionsAsync only.
        AgentId? AgentId,
        ChannelKey? ChannelType,
        string? CallerId,
        SessionType? SessionType,
        List<SessionParticipant>? Participants,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        SessionStatus Status = SessionStatus.Active,
        DateTimeOffset? ExpiresAt = null,
        // Renamed from NextSequenceId to keep the #575 SessionStreamReplay architecture fence
        // (`SessionStreamReplayArchitectureTests`) maximally tight. The JSON wire shape stays
        // `nextSequenceId` (CamelCase) so existing on-disk sidecars round-trip unchanged.
        [property: JsonPropertyName("nextSequenceId")] long PersistedNextSequenceId = 1,
        List<GatewaySessionStreamEvent>? StreamEvents = null,
        ConversationId? ConversationId = null,
        // PR #549: cross-world federation receiver requires Session.Metadata to round-trip on disk
        // (OwnedByRequester validates RemoteSessionId via sourceWorldId/sourceAgentId stashed
        // here). Historically this record dropped Metadata entirely, silently breaking the receiver
        // after any gateway restart on a FileSessionStore-backed deployment.
        Dictionary<string, object?>? Metadata = null);
}
