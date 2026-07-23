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

    // Citizen scoping is shared across all three conversation stores — see ConversationStoreShared (#1383).
    private static bool MatchesCitizen(Conversation conversation, CitizenId citizen)
        => ConversationStoreShared.MatchesCitizen(conversation, citizen);

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
        if (conversation.Status == ConversationStatus.Archived && conversation.ActiveSessionId is not null)
            throw new InvalidOperationException($"Conversation '{conversation.ConversationId}' cannot be archived while an active session is assigned.");
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
    public async Task TouchAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conversation = await FindByConversationIdAsync(conversationId, ct).ConfigureAwait(false);
            if (conversation is null)
                return;
            await WriteFileAsync(conversation with { UpdatedAt = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task PinAsync(ConversationId conversationId, bool pin, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conversation = await FindByConversationIdAsync(conversationId, ct).ConfigureAwait(false);
            if (conversation is null)
                return;
            conversation.IsPinned = pin;
            conversation.PinnedAt = pin ? DateTimeOffset.UtcNow : null;
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteFileAsync(conversation, ct).ConfigureAwait(false);
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
    public async Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(CancellationToken ct = default)
    {
        var all = await ListAsync(null, ct).ConfigureAwait(false);
        return [.. all
            .Where(c => c.Status != ConversationStatus.Archived)
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.PinnedAt)
            .ThenByDescending(c => c.UpdatedAt)
            .ThenBy(c => c.ConversationId.Value, StringComparer.Ordinal)
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

    // World-id stamping/back-fill is shared across all three conversation stores — see
    // ConversationStoreShared (#1383). These forwarders thread this store's world context
    // into the shared logic while keeping the existing call-site signatures unchanged.
    private void StampWorldId(Conversation conversation)
        => ConversationStoreShared.StampWorldId(conversation, _worldContext);

    private Conversation? BackfillWorldId(Conversation? conversation)
        => ConversationStoreShared.BackfillWorldId(conversation, _worldContext);

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
            c.Kind.ToString(),
            c.IsPinned,
            c.PinnedAt,
            // #1427: populate the participant roster (avatar-chip list the portal renders)
            // so File-backed listings match the InMemory reference shape instead of returning
            // a null roster. The conversation already carries its hydrated Participants here.
            c.Participants.Select(p => new ParticipantSummary(
                p.CitizenId.Kind.ToString(),
                p.CitizenId.Value,
                p.Role)).ToList());

    // ── Canvas State ───────────────────────────────────────────────────────
    // The file store persists canvas state in the CanvasState property of the Conversation
    // JSON file. This is simpler than a side-table approach since each conversation already
    // has its own file.

    /// <inheritdoc />
    public async Task<Dictionary<string, JsonElement>?> GetCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        var conversation = await GetAsync(conversationId, ct).ConfigureAwait(false);
        if (conversation is null)
            return null;

        return conversation.CanvasState ?? new Dictionary<string, JsonElement>();
    }

    /// <inheritdoc />
    public async Task<bool> SetCanvasStateKeyAsync(ConversationId conversationId, string key, JsonElement value, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conversation = await GetCoreAsync(conversationId, ct).ConfigureAwait(false);
            if (conversation is null)
                return false;

            conversation.CanvasState ??= new Dictionary<string, JsonElement>();
            conversation.CanvasState[key] = value;
            await SaveAsync(conversation, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteCanvasStateKeyAsync(ConversationId conversationId, string key, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conversation = await GetCoreAsync(conversationId, ct).ConfigureAwait(false);
            if (conversation is null)
                return;

            if (conversation.CanvasState is not null)
            {
                conversation.CanvasState.Remove(key);
                await SaveAsync(conversation, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ClearCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conversation = await GetCoreAsync(conversationId, ct).ConfigureAwait(false);
            if (conversation is null)
                return;

            conversation.CanvasState = null;
            await SaveAsync(conversation, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Conversation?> GetCoreAsync(ConversationId conversationId, CancellationToken ct)
    {
        // Internal get that bypasses the public lock
        return await GetAsync(conversationId, ct).ConfigureAwait(false);
    }
}
