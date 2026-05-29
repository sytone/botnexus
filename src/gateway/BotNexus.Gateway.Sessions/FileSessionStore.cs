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
    private readonly LegacyConversationResolver? _legacyResolver;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FileSessionStore(string storePath, ILogger<FileSessionStore> logger, IFileSystem fileSystem, ISecretRedactor? redactor = null)
        : this(storePath, logger, fileSystem, conversationStore: null, redactor: redactor)
    {
    }

    /// <summary>
    /// Initialises a new <see cref="FileSessionStore"/>.
    /// </summary>
    /// <param name="storePath">Directory used for session JSONL + metadata sidecar files.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="fileSystem">Abstracted file system (real or mocked).</param>
    /// <param name="conversationStore">
    /// When provided, sessions loaded with a null <see cref="Session.ConversationId"/> are
    /// backfilled to the agent's <c>legacy:{agentId}</c> conversation via
    /// <see cref="LegacyConversationResolver"/> and the sidecar is rewritten so the
    /// stamp persists across restarts (Phase 9 / P9-B; issue #615). Save-time stamping
    /// also defends against fresh sessions being persisted with no conversation bound.
    /// </param>
    /// <param name="redactor">When provided, secrets in content are redacted before storage.</param>
    public FileSessionStore(
        string storePath,
        ILogger<FileSessionStore> logger,
        IFileSystem fileSystem,
        IConversationStore? conversationStore,
        ISecretRedactor? redactor = null)
    {
        _storePath = storePath;
        _logger = logger;
        _fileSystem = fileSystem;
        _redactor = redactor;
        _legacyResolver = conversationStore is not null
            ? new LegacyConversationResolver(conversationStore, logger: null)
            : null;
        _fileSystem.Directory.CreateDirectory(storePath);
    }

    /// <inheritdoc />
    public override async Task<GatewaySession?> GetAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.get", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", sessionId);

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
            AgentId = meta.AgentId,
            ChannelType = meta.ChannelType,
            SessionType = meta.SessionType ?? InferSessionType(sessionId, meta.ChannelType),
            Participants = meta.Participants ?? [],
            CreatedAt = meta.CreatedAt,
            UpdatedAt = meta.UpdatedAt,
            Status = meta.Status,
            ExpiresAt = meta.ExpiresAt,
            ConversationId = meta.ConversationId
        }, _redactor)
        {
            CallerId = meta.CallerId
        };
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

        // Phase 9 / P9-B (#615): if this session was persisted before Session.ConversationId
        // was always populated, durably backfill the legacy conversation. The sidecar is
        // rewritten so subsequent loads — and any reader that joins on ConversationId —
        // observe the stamp without another resolution round trip.
        if (session.ConversationId is null && _legacyResolver is not null)
        {
            var legacy = await _legacyResolver.ResolveAsync(session.AgentId, cancellationToken).ConfigureAwait(false);
            session.ConversationId = legacy.ConversationId;

            if (session.Status == SessionStatus.Active)
            {
                await _legacyResolver.BindActiveSessionIfNoneAsync(legacy, session.SessionId, cancellationToken).ConfigureAwait(false);
            }

            await WriteToFileAsync(session, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "Backfilled orphan session {SessionId} for agent {AgentId} with legacy conversation {LegacyConversationId} on load (sidecar rewritten).",
                session.SessionId, session.AgentId, legacy.ConversationId);
        }

        return session;
    }

    /// <summary>
    /// Defensively stamps a session whose <see cref="Session.ConversationId"/> is null at
    /// save time with the agent's <c>legacy:{agentId}</c> conversation, persisting the
    /// stamp before the sidecar is written so subsequent reads see a consistent view.
    /// No-op when no conversation store was registered (back-compat for test composition
    /// roots).
    /// </summary>
    private async Task EnsureConversationIdStampedAsync(GatewaySession session, CancellationToken cancellationToken)
    {
        if (session.ConversationId is not null)
            return;
        if (_legacyResolver is null)
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
            session.AgentId,
            session.ChannelType,
            session.CallerId,
            session.SessionType,
            session.Participants,
            session.CreatedAt,
            session.UpdatedAt,
            session.Status,
            session.ExpiresAt,
            session.StreamReplay.NextSequenceId,
            [.. session.StreamReplay.GetEventSnapshot()],
            session.ConversationId,
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
        AgentId AgentId,
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
