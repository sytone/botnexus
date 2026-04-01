using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Session;

/// <summary>
/// File-backed session manager that persists session history as JSONL files.
/// Each session is stored in its own file: {storePath}/{sessionKey}.jsonl
/// </summary>
public sealed class SessionManager : ISessionManager
{
    private readonly string _storePath;
    private readonly ILogger<SessionManager> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, Core.Models.Session> _cache = [];

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public SessionManager(string storePath, ILogger<SessionManager> logger)
    {
        _storePath = storePath;
        _logger = logger;
        Directory.CreateDirectory(storePath);
    }

    /// <inheritdoc/>
    public async Task<Core.Models.Session> GetOrCreateAsync(string sessionKey, string agentName, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(sessionKey, out var cached))
                return cached;

            var session = await LoadFromFileAsync(sessionKey, agentName, cancellationToken).ConfigureAwait(false);
            _cache[sessionKey] = session;
            return session;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync(Core.Models.Session session, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cache[session.Key] = session;
            await WriteToFileAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task ResetAsync(string sessionKey, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(sessionKey, out var session))
            {
                session.Clear();
                await WriteToFileAsync(session, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var filePath = GetFilePath(sessionKey);
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
            _logger.LogInformation("Session {SessionKey} reset", sessionKey);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string sessionKey, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cache.Remove(sessionKey);
            var filePath = GetFilePath(sessionKey);
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var files = Directory.GetFiles(_storePath, "*.jsonl");
            var keys = files
                .Select(f => Path.GetFileNameWithoutExtension(f)!)
                .Select(DecodeSessionKey)
                .ToList();
            return keys;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Core.Models.Session> LoadFromFileAsync(string sessionKey, string agentName, CancellationToken cancellationToken)
    {
        var session = new Core.Models.Session { Key = sessionKey, AgentName = agentName };
        var filePath = GetFilePath(sessionKey);

        if (!File.Exists(filePath))
            return session;

        try
        {
            var lines = await File.ReadAllLinesAsync(filePath, cancellationToken).ConfigureAwait(false);
            foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                var entry = JsonSerializer.Deserialize<SessionEntry>(line, _jsonOptions);
                if (entry is not null)
                    session.History.Add(entry);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load session {SessionKey} from {FilePath}", sessionKey, filePath);
        }

        return session;
    }

    private async Task WriteToFileAsync(Core.Models.Session session, CancellationToken cancellationToken)
    {
        var filePath = GetFilePath(session.Key);
        var lines = session.History.Select(e => JsonSerializer.Serialize(e, _jsonOptions));
        await File.WriteAllLinesAsync(filePath, lines, cancellationToken).ConfigureAwait(false);
    }

    private string GetFilePath(string sessionKey)
        => Path.Combine(_storePath, $"{EncodeSessionKey(sessionKey)}.jsonl");

    private static string EncodeSessionKey(string key)
        => Uri.EscapeDataString(key).Replace("%", "_");

    private static string DecodeSessionKey(string encoded)
        => Uri.UnescapeDataString(encoded.Replace("_", "%"));
}
