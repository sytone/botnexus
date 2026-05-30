using System.IO.Abstractions;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Configuration;
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
    private readonly IWorldContext? _worldContext;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>
    /// Initialises a new <see cref="FileConversationStore"/> with the given root path. No world
    /// stamping — kept for tests and bare wire-ups; production callers should use the
    /// world-aware overload.
    /// </summary>
    /// <param name="rootPath">Base directory under which <c>{agentId}/{conversationId}.json</c> files are stored.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="fileSystem">Abstracted file system.</param>
    public FileConversationStore(string rootPath, ILogger<FileConversationStore> logger, IFileSystem fileSystem)
        : this(rootPath, logger, fileSystem, worldContext: null)
    {
    }

    /// <summary>
    /// Initialises a new <see cref="FileConversationStore"/> that stamps and lazy-backfills the
    /// current world id on <see cref="Conversation.WorldId"/>.
    /// </summary>
    /// <param name="rootPath">Base directory under which <c>{agentId}/{conversationId}.json</c> files are stored.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="fileSystem">Abstracted file system.</param>
    /// <param name="worldContext">Resolves the gateway's current world identity for stamping; <c>null</c> disables stamping.</param>
    public FileConversationStore(
        string rootPath,
        ILogger<FileConversationStore> logger,
        IFileSystem fileSystem,
        IWorldContext? worldContext)
    {
        _rootPath = rootPath;
        _logger = logger;
        _fileSystem = fileSystem;
        _worldContext = worldContext;
        _fileSystem.Directory.CreateDirectory(rootPath);
    }

    /// <inheritdoc />
    public async Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { return BackfillWorldId(await LoadFileAsync(conversationId, ct).ConfigureAwait(false)); }
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
    public async Task<IReadOnlyList<Conversation>> ListForCitizenAsync(CitizenId citizen, CancellationToken ct = default)
    {
        if (!citizen.IsValid)
            throw new ArgumentException("Citizen must be a valid (non-default) CitizenId.", nameof(citizen));

        // P9-F: cannot scope-narrow by agent dir even when citizen is an Agent. Participant
        // matches can land on conversations owned by *another* agent, so the pre-P9-F
        // narrowing (scope = citizen.AsAgent) would silently drop them. The optimisation
        // is gone until/unless an index supplements the scan.
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await EnumerateAsync(agentId: null, ct).ConfigureAwait(false);
            IReadOnlyList<Conversation> filtered = [.. all.Where(c => MatchesCitizen(c, citizen))];
            return filtered;
        }
        finally { _lock.Release(); }
    }

    private static bool MatchesCitizen(Conversation conversation, CitizenId citizen)
    {
        if (conversation.Initiator is { IsValid: true } init && init == citizen)
            return true;

        if (citizen.Kind == CitizenKind.Agent && citizen.AsAgent is { } agent && conversation.AgentId == agent)
            return true;

        // Participant-match (P9-F): the conversation includes this citizen in its
        // participant set persisted in the JSON sidecar.
        if (conversation.Participants.Any(p => p.CitizenId == citizen))
            return true;

        return false;
    }

    public async Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default)
    {
        StampWorldId(conversation);

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
        StampWorldId(conversation);
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
    public async Task AddParticipantsAsync(
        ConversationId conversationId,
        IEnumerable<SessionParticipant> participants,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(participants);
        var snapshot = participants as IReadOnlyCollection<SessionParticipant> ?? participants.ToArray();
        if (snapshot.Count == 0)
            return;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await FindByConversationIdAsync(conversationId, ct).ConfigureAwait(false);
            if (existing is null)
                return;

            var byCitizen = new Dictionary<CitizenId, SessionParticipant>(existing.Participants.Count);
            foreach (var p in existing.Participants)
                byCitizen[p.CitizenId] = p;

            var changed = false;
            foreach (var participant in snapshot)
            {
                if (!participant.CitizenId.IsValid)
                    continue;
                // First-add wins on role.
                if (byCitizen.ContainsKey(participant.CitizenId))
                    continue;
                byCitizen[participant.CitizenId] = new SessionParticipant
                {
                    CitizenId = participant.CitizenId,
                    Role = participant.Role
                };
                changed = true;
            }

            if (!changed)
                return;

            existing.Participants = byCitizen.Values.ToList();
            await WriteFileAsync(existing, ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task<Conversation?> ResolveByBindingAsync(
        AgentId agentId,
        ChannelKey channelType,
        ChannelAddress channelAddress,
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
                    b.ChannelAddress == channelAddress));
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
                    {
                        BackfillWorldId(conversation);
                        results.Add(conversation);
                    }
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

    // Stamps the current world id onto a conversation being persisted (Create/Save). Only
    // fills an empty WorldId — explicit non-empty values are preserved so cross-world relays
    // can hold the source world's id even when this gateway is the receiver. No-op when no
    // world context is wired (e.g. test setups using the parameterless ctor).
    private void StampWorldId(Conversation conversation)
    {
        if (string.IsNullOrEmpty(conversation.WorldId) && _worldContext is not null)
            conversation.WorldId = _worldContext.CurrentWorldId;
    }

    // Read-time projection: legacy JSON sidecars persisted before #613 deserialise with an
    // empty WorldId. This projects them to the current world on the way out. The file on
    // disk is not rewritten — the next SaveAsync round-trip will durably persist via
    // StampWorldId. Treating backfill as projection-only keeps the read path single-pass
    // and avoids touching disk on every Get/List.
    private Conversation? BackfillWorldId(Conversation? conversation)
    {
        if (conversation is not null && string.IsNullOrEmpty(conversation.WorldId) && _worldContext is not null)
            conversation.WorldId = _worldContext.CurrentWorldId;
        return conversation;
    }

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
            c.UpdatedAt,
            c.Purpose,
            c.Kind.ToString());
}
