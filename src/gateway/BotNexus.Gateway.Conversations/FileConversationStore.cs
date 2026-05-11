using System.IO.Abstractions;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Conversations;

// Gateway.Conversations is a reusable library — it uses ConfigureAwait(false) on all awaited
// tasks to prevent deadlocks when consumed by callers with a synchronization context.

/// <summary>
/// File-backed conversation store. Persists each conversation as a single JSON file at:
/// <c>{BotNexusHome}/conversations/{agentId}/{conversationId}.json</c>
/// Thread-safe via <see cref="SemaphoreSlim"/>.
/// </summary>
public sealed class FileConversationStore : IConversationStore
{
    private readonly string _rootPath;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<FileConversationStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>
    /// Initialises a new <see cref="FileConversationStore"/> with the given root path.
    /// </summary>
    /// <param name="rootPath">Base directory under which <c>{agentId}/{conversationId}.json</c> files are stored.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="fileSystem">Abstracted file system.</param>
    public FileConversationStore(string rootPath, ILogger<FileConversationStore> logger, IFileSystem fileSystem)
    {
        _rootPath = rootPath;
        _logger = logger;
        _fileSystem = fileSystem;
        _fileSystem.Directory.CreateDirectory(rootPath);
    }

    /// <inheritdoc />
    public async Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { return await LoadFileAsync(conversationId, ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { return await EnumerateAsync(agentId, ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task<Conversation> GetOrCreateDefaultAsync(AgentId agentId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await EnumerateAsync(agentId, ct).ConfigureAwait(false);
            var existing = all.FirstOrDefault(c => c.IsDefault && c.Status == ConversationStatus.Active);
            if (existing is not null)
                return existing;

            var archived = all
                .Where(c => c.IsDefault && c.Status == ConversationStatus.Archived)
                .OrderByDescending(c => c.UpdatedAt)
                .FirstOrDefault();
            if (archived is not null)
            {
                archived.Status = ConversationStatus.Active;
                archived.ActiveSessionId = null;
                archived.UpdatedAt = DateTimeOffset.UtcNow;
                await WriteFileAsync(archived, ct).ConfigureAwait(false);
                return archived;
            }

            var conversation = new Conversation
            {
                ConversationId = ConversationId.Create(),
                AgentId = agentId,
                Title = "Default",
                IsDefault = true,
                Status = ConversationStatus.Active
            };
            await WriteFileAsync(conversation, ct).ConfigureAwait(false);
            return conversation;
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = GetPath(conversation.AgentId, conversation.ConversationId);
            if (_fileSystem.File.Exists(path))
                throw new InvalidOperationException($"A conversation with id '{conversation.ConversationId}' already exists.");
            await WriteFileAsync(conversation, ct).ConfigureAwait(false);
            return conversation;
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task SaveAsync(Conversation conversation, CancellationToken ct = default)
    {
        conversation = conversation with { UpdatedAt = DateTimeOffset.UtcNow };
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { await WriteFileAsync(conversation, ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task ArchiveAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conversation = await FindByConversationIdAsync(conversationId, ct).ConfigureAwait(false);
            if (conversation is null)
                return;
            await WriteFileAsync(
                conversation with { Status = ConversationStatus.Archived, ActiveSessionId = null, UpdatedAt = DateTimeOffset.UtcNow },
                ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task<Conversation?> ResolveByBindingAsync(
        AgentId agentId,
        ChannelKey channelType,
        ChannelAddress channelAddress,
        ThreadId? threadId,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await EnumerateAsync(agentId, ct).ConfigureAwait(false);
            return all.FirstOrDefault(c =>
                c.Status == ConversationStatus.Active &&
                c.ChannelBindings.Any(b =>
                    b.ChannelType == channelType &&
                    b.ChannelAddress == channelAddress &&
                    b.ThreadId == threadId));
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(AgentId? agentId = null, CancellationToken ct = default)
    {
        var all = await ListAsync(agentId, ct).ConfigureAwait(false);
        return [.. all
            .Where(c => c.Status != ConversationStatus.Archived)
            .Select(ToSummary)];
    }

    private async Task<IReadOnlyList<Conversation>> EnumerateAsync(AgentId? agentId, CancellationToken ct)
    {
        var results = new List<Conversation>();

        IEnumerable<string> dirs;
        if (agentId is not null)
        {
            var agentDir = Path.Combine(_rootPath, agentId.Value.Value);
            dirs = _fileSystem.Directory.Exists(agentDir) ? [agentDir] : [];
        }
        else
        {
            dirs = _fileSystem.Directory.Exists(_rootPath)
                ? _fileSystem.Directory.GetDirectories(_rootPath)
                : [];
        }

        foreach (var dir in dirs)
        {
            if (!_fileSystem.Directory.Exists(dir))
                continue;

            foreach (var file in _fileSystem.Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var json = await _fileSystem.File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                    var conversation = JsonSerializer.Deserialize<Conversation>(json, JsonOptions);
                    if (conversation is not null)
                        results.Add(conversation);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read conversation file {File}", file);
                }
            }
        }

        return results;
    }

    private async Task<Conversation?> FindByConversationIdAsync(ConversationId conversationId, CancellationToken ct)
    {
        // Search all agent directories because we don't know the agent from id alone
        if (!_fileSystem.Directory.Exists(_rootPath))
            return null;

        foreach (var dir in _fileSystem.Directory.GetDirectories(_rootPath))
        {
            var loaded = await LoadFileAsync(dir, conversationId, ct).ConfigureAwait(false);
            if (loaded is not null)
                return loaded;
        }
        return null;
    }

    private async Task<Conversation?> LoadFileAsync(ConversationId conversationId, CancellationToken ct)
        => await FindByConversationIdAsync(conversationId, ct).ConfigureAwait(false);

    private async Task<Conversation?> LoadFileAsync(string agentDir, ConversationId conversationId, CancellationToken ct)
    {
        var path = Path.Combine(agentDir, $"{conversationId.Value}.json");
        if (!_fileSystem.File.Exists(path))
            return null;

        try
        {
            var json = await _fileSystem.File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<Conversation>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read conversation file {Path}", path);
            return null;
        }
    }

    private async Task WriteFileAsync(Conversation conversation, CancellationToken ct)
    {
        var dir = Path.Combine(_rootPath, conversation.AgentId.Value);
        _fileSystem.Directory.CreateDirectory(dir);
        var path = GetPath(conversation.AgentId, conversation.ConversationId);
        var json = JsonSerializer.Serialize(conversation, JsonOptions);
        await _fileSystem.File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

    private string GetPath(AgentId agentId, ConversationId conversationId)
        => Path.Combine(_rootPath, agentId.Value, $"{conversationId.Value}.json");

    private static ConversationSummary ToSummary(Conversation c) =>
        new(
            c.ConversationId.Value,
            c.AgentId.Value,
            c.Title,
            c.IsDefault,
            c.Status.ToString(),
            c.ActiveSessionId?.Value,
            c.ChannelBindings.Count,
            c.CreatedAt,
            c.UpdatedAt);
}
