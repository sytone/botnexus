using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Sessions;

// Gateway library code uses ConfigureAwait(false) on awaited tasks to avoid deadlocks
// in synchronization-context-bound callers.

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
public sealed class FileSessionStore : ISessionStore
{
    private readonly string _storePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, GatewaySession> _cache = [];
    private readonly ILogger<FileSessionStore> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FileSessionStore(string storePath, ILogger<FileSessionStore> logger)
    {
        _storePath = storePath;
        _logger = logger;
        Directory.CreateDirectory(storePath);
    }

    /// <inheritdoc />
    public async Task<GatewaySession?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
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
    public async Task<GatewaySession> GetOrCreateAsync(string sessionId, string agentId, CancellationToken cancellationToken = default)
    {
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

            var session = new GatewaySession { SessionId = sessionId, AgentId = agentId };
            _cache[sessionId] = session;
            return session;
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cache[session.SessionId] = session;
            await WriteToFileAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cache.Remove(sessionId);
            var historyPath = GetHistoryPath(sessionId);
            var metaPath = GetMetaPath(sessionId);
            if (File.Exists(historyPath)) File.Delete(historyPath);
            if (File.Exists(metaPath)) File.Delete(metaPath);
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GatewaySession>> ListAsync(string? agentId = null, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Load all meta files from disk
            var sessions = new List<GatewaySession>();
            foreach (var metaFile in Directory.GetFiles(_storePath, "*.meta.json"))
            {
                var sessionId = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(metaFile));
                var session = _cache.GetValueOrDefault(sessionId) ?? await LoadFromFileAsync(sessionId, cancellationToken).ConfigureAwait(false);
                if (session is not null && (agentId is null || session.AgentId == agentId))
                    sessions.Add(session);
            }
            return sessions;
        }
        finally { _lock.Release(); }
    }

    private async Task<GatewaySession?> LoadFromFileAsync(string sessionId, CancellationToken cancellationToken)
    {
        var metaPath = GetMetaPath(sessionId);
        if (!File.Exists(metaPath)) return null;

        var metaJson = await File.ReadAllTextAsync(metaPath, cancellationToken).ConfigureAwait(false);
        var meta = JsonSerializer.Deserialize<SessionMeta>(metaJson, JsonOptions);
        if (meta is null) return null;

        var session = new GatewaySession
        {
            SessionId = sessionId,
            AgentId = meta.AgentId,
            ChannelType = meta.ChannelType,
            CallerId = meta.CallerId,
            CreatedAt = meta.CreatedAt,
            UpdatedAt = meta.UpdatedAt
        };

        var historyPath = GetHistoryPath(sessionId);
        if (File.Exists(historyPath))
        {
            var lines = await File.ReadAllLinesAsync(historyPath, cancellationToken).ConfigureAwait(false);
            foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                var entry = JsonSerializer.Deserialize<SessionEntry>(line, JsonOptions);
                if (entry is not null) session.History.Add(entry);
            }
        }

        return session;
    }

    private async Task WriteToFileAsync(GatewaySession session, CancellationToken cancellationToken)
    {
        // Write history as JSONL
        var historyPath = GetHistoryPath(session.SessionId);
        var lines = session.History.Select(e => JsonSerializer.Serialize(e, JsonOptions));
        await File.WriteAllLinesAsync(historyPath, lines, cancellationToken).ConfigureAwait(false);

        // Write metadata sidecar
        var metaPath = GetMetaPath(session.SessionId);
        var meta = new SessionMeta(session.AgentId, session.ChannelType, session.CallerId, session.CreatedAt, session.UpdatedAt);
        var metaJson = JsonSerializer.Serialize(meta, JsonOptions);
        await File.WriteAllTextAsync(metaPath, metaJson, cancellationToken).ConfigureAwait(false);
    }

    private string GetHistoryPath(string sessionId) => Path.Combine(_storePath, $"{SanitizeFileName(sessionId)}.jsonl");
    private string GetMetaPath(string sessionId) => Path.Combine(_storePath, $"{SanitizeFileName(sessionId)}.meta.json");

    private static string SanitizeFileName(string name) => Uri.EscapeDataString(name);

    private sealed record SessionMeta(
        string AgentId,
        string? ChannelType,
        string? CallerId,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
