using System.IO.Abstractions;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Memory.Models;

namespace BotNexus.Memory;

/// <summary>
/// File-based memory provider that delegates to workspace files for saves and prompt context,
/// and to the SQLite memory store for search and retrieval operations.
/// This preserves the exact existing behavior while exposing it through the IAgentMemory abstraction.
/// </summary>
public sealed class MarkdownAgentMemory : IAgentMemory
{
    private readonly string _agentId;
    private readonly IAgentWorkspaceManager _workspaceManager;
    private readonly IMemoryStore _memoryStore;
    private readonly IFileSystem _fileSystem;
    private readonly string? _memoryPathOverride;

    public MarkdownAgentMemory(
        string agentId,
        IAgentWorkspaceManager workspaceManager,
        IMemoryStore memoryStore,
        IFileSystem fileSystem,
        string? memoryPathOverride = null)
    {
        _agentId = string.IsNullOrWhiteSpace(agentId)
            ? throw new ArgumentException("Agent ID is required.", nameof(agentId))
            : agentId;
        _workspaceManager = workspaceManager ?? throw new ArgumentNullException(nameof(workspaceManager));
        _memoryStore = memoryStore ?? throw new ArgumentNullException(nameof(memoryStore));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _memoryPathOverride = memoryPathOverride;
    }

    /// <inheritdoc />
    public Task<AgentMemoryContext> GetPromptContextAsync(AgentMemoryPromptRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var workspacePath = ResolveWorkspaceDirectory(_workspaceManager.GetWorkspacePath(request.AgentId));
        return LoadDailyMemoryContextAsync(workspacePath, ct);
    }

    /// <inheritdoc />
    public async Task SaveAsync(AgentMemorySaveRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Delegate to the workspace manager which handles file-based memory saves
        // (daily notes, specific file paths, memory path overrides).
        await _workspaceManager.SaveMemoryAsync(
            request.AgentId,
            null, // filePath derived from request context — for now matches existing tool behavior
            request.Content,
            _memoryPathOverride,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves memory to a specific file path under the memory root.
    /// </summary>
    public async Task SaveToFileAsync(string content, string? filePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _workspaceManager.SaveMemoryAsync(
            _agentId,
            filePath,
            content,
            _memoryPathOverride,
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentMemorySearchResult>> SearchAsync(AgentMemorySearchRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var filter = request.Filter is not null
            ? new MemorySearchFilter
            {
                SourceType = request.Filter.SourceType,
                SessionId = request.Filter.SessionId,
                AfterDate = request.Filter.AfterDate,
                BeforeDate = request.Filter.BeforeDate,
                Tags = request.Filter.Tags
            }
            : null;

        var entries = await _memoryStore.SearchAsync(request.Query, request.TopK, filter, ct).ConfigureAwait(false);
        return entries.Select(MapToSearchResult).ToList();
    }

    /// <inheritdoc />
    public async Task<AgentMemorySearchResult?> GetAsync(string entryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var entry = await _memoryStore.GetByIdAsync(entryId, ct).ConfigureAwait(false);
        return entry is null ? null : MapToSearchResult(entry);
    }

    /// <inheritdoc />
    public async Task OnSessionCompleteAsync(AgentMemorySessionEvent sessionEvent, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (sessionEvent.History is null || sessionEvent.History.Count == 0)
            return;

        await _memoryStore.InitializeAsync(ct).ConfigureAwait(false);

        var existing = await _memoryStore.GetBySessionAsync(sessionEvent.SessionId, int.MaxValue, ct).ConfigureAwait(false);
        var indexedTurns = existing
            .Where(entry => entry.TurnIndex.HasValue)
            .Select(entry => entry.TurnIndex!.Value)
            .ToHashSet();

        AgentMemorySessionTurn? pendingUser = null;

        foreach (var turn in sessionEvent.History)
        {
            ct.ThrowIfCancellationRequested();

            if (turn.Role.Equals("tool", StringComparison.OrdinalIgnoreCase))
                continue;

            if (turn.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                pendingUser = turn;
                continue;
            }

            if (pendingUser is null || !turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!indexedTurns.Contains(pendingUser.Index))
            {
                var memory = new MemoryEntry
                {
                    Id = string.Empty,
                    AgentId = sessionEvent.AgentId,
                    SessionId = sessionEvent.SessionId,
                    TurnIndex = pendingUser.Index,
                    SourceType = "conversation",
                    Content = $"User: {pendingUser.Content}\nAssistant: {turn.Content}",
                    MetadataJson = null,
                    Embedding = null,
                    CreatedAt = turn.Timestamp,
                    UpdatedAt = null,
                    ExpiresAt = null,
                    IsArchived = false
                };

                await _memoryStore.InsertAsync(memory, ct).ConfigureAwait(false);
                indexedTurns.Add(pendingUser.Index);
            }

            pendingUser = null;
        }
    }

    /// <inheritdoc />
    public Task ConsolidateAsync(AgentMemoryConsolidateRequest request, CancellationToken ct = default)
    {
        // Consolidation is handled by the existing MemoryDreamingCronAction.
        // This provider does not perform its own consolidation.
        return Task.CompletedTask;
    }

    private async Task<AgentMemoryContext> LoadDailyMemoryContextAsync(string workspacePath, CancellationToken ct)
    {
        var memoryRoot = ResolveMemoryRoot(workspacePath);
        if (!_fileSystem.Directory.Exists(memoryRoot))
            return AgentMemoryContext.Empty;

        var today = DateTime.Now.Date;
        var targetNames = new HashSet<string>(StringComparer.Ordinal)
        {
            today.ToString("yyyy-MM-dd"),
            today.AddDays(-1).ToString("yyyy-MM-dd")
        };

        var files = _fileSystem.Directory.GetFiles(memoryRoot, "*.md")
            .Select(path => new
            {
                FullPath = path,
                Name = _fileSystem.Path.GetFileNameWithoutExtension(path)
            })
            .Where(file => targetNames.Contains(file.Name))
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .ToList();

        var dailyNotes = new List<AgentMemoryDailyNote>();
        var totalChars = 0;

        foreach (var file in files)
        {
            string? content = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    content = await _fileSystem.File.ReadAllTextAsync(file.FullPath, ct).ConfigureAwait(false);
                    break;
                }
                catch (IOException) when (attempt < 2)
                {
                    await Task.Delay(50 * (attempt + 1), ct).ConfigureAwait(false);
                }
            }

            if (!string.IsNullOrWhiteSpace(content))
            {
                var trimmed = content.Trim();
                if (DateOnly.TryParse(file.Name, out var date))
                {
                    dailyNotes.Add(new AgentMemoryDailyNote(date, trimmed));
                    totalChars += trimmed.Length;
                }
            }
        }

        // Rough token estimate: ~4 chars per token
        var approxTokens = totalChars / 4;
        return new AgentMemoryContext(null, dailyNotes, approxTokens);
    }

    private string ResolveWorkspaceDirectory(string workspacePath)
    {
        var resolvedPath = _fileSystem.Path.GetFullPath(workspacePath);
        if (_fileSystem.Path.GetFileName(resolvedPath)
            .Equals("workspace", StringComparison.OrdinalIgnoreCase))
            return resolvedPath;

        var nestedWorkspacePath = _fileSystem.Path.Combine(resolvedPath, "workspace");
        return _fileSystem.Directory.Exists(nestedWorkspacePath) ? nestedWorkspacePath : resolvedPath;
    }

    private string ResolveMemoryRoot(string workspacePath)
    {
        var relative = string.IsNullOrWhiteSpace(_memoryPathOverride)
            ? "memory"
            : _memoryPathOverride.Trim().Replace('\\', '/');

        if (relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            relative = _fileSystem.Path.GetDirectoryName(relative) ?? "memory";

        var memoryRoot = _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(workspacePath, relative));
        var workspaceFullPath = _fileSystem.Path.GetFullPath(workspacePath);
        var workspacePrefix = workspaceFullPath.TrimEnd(
            _fileSystem.Path.DirectorySeparatorChar,
            _fileSystem.Path.AltDirectorySeparatorChar) + _fileSystem.Path.DirectorySeparatorChar;

        if (!memoryRoot.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase) &&
            !memoryRoot.Equals(workspaceFullPath, StringComparison.OrdinalIgnoreCase))
            return _fileSystem.Path.Combine(workspacePath, "memory");

        return memoryRoot;
    }

    private static AgentMemorySearchResult MapToSearchResult(MemoryEntry entry)
        => new(
            Id: entry.Id,
            Content: entry.Content,
            SourceType: entry.SourceType,
            SessionId: entry.SessionId,
            CreatedAt: entry.CreatedAt);
}
