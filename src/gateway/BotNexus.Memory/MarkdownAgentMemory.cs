using System.IO.Abstractions;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Memory.Embeddings;
using BotNexus.Memory.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BotNexus.Domain.Text;

using BotNexus.Memory.Tools;

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
    private readonly ILogger _logger;

    public MarkdownAgentMemory(
        string agentId,
        IAgentWorkspaceManager workspaceManager,
        IMemoryStore memoryStore,
        IFileSystem fileSystem,
        string? memoryPathOverride = null,
        ILogger<MarkdownAgentMemory>? logger = null)
    {
        _agentId = string.IsNullOrWhiteSpace(agentId)
            ? throw new ArgumentException("Agent ID is required.", nameof(agentId))
            : agentId;
        _workspaceManager = workspaceManager ?? throw new ArgumentNullException(nameof(workspaceManager));
        _memoryStore = memoryStore ?? throw new ArgumentNullException(nameof(memoryStore));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _memoryPathOverride = memoryPathOverride;
        _logger = logger ?? NullLogger<MarkdownAgentMemory>.Instance;
    }

    /// <inheritdoc />
    public Task<AgentMemoryContext> GetPromptContextAsync(AgentMemoryPromptRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var workspacePath = ResolveWorkspaceDirectory(_workspaceManager.GetWorkspacePath(request.AgentId));
        return LoadDailyMemoryContextAsync(workspacePath, request.MaxTokenBudget, ct);
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

        await IndexNoteAsync(request.AgentId, filePath: null, ct).ConfigureAwait(false);
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

        await IndexNoteAsync(_agentId, filePath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Mirrors the markdown note that was just appended into the searchable memory store with
    /// <c>SourceType = "note"</c> (issue #2780). Strictly additive and fail-safe: the workspace
    /// file is the source of truth, so any indexing failure is logged and swallowed rather than
    /// propagated back to the caller that already wrote the note successfully.
    /// </summary>
    private async Task IndexNoteAsync(string agentId, string? filePath, CancellationToken ct)
    {
        try
        {
            var workspacePath = ResolveWorkspaceDirectory(_workspaceManager.GetWorkspacePath(agentId));
            var notePath = MarkdownNoteIndexer.ResolveNotePath(_fileSystem, workspacePath, _memoryPathOverride, filePath);
            if (string.IsNullOrWhiteSpace(notePath))
                return;

            await MarkdownNoteIndexer
                .IndexNoteFileAsync(_memoryStore, _fileSystem, agentId, workspacePath, notePath, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to index markdown note for agent {AgentId}; the note file was written and remains authoritative.", agentId);
        }
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

        var entries = await _memoryStore.SearchScoredAsync(request.Query, request.TopK, filter, ct).ConfigureAwait(false);
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
                    // See MemoryIndexer: the pair carries a verbatim user turn, so it is stamped
                    // with the more conservative of the two halves (#2480).
                    Provenance = MemoryProvenance.User,
                    OriginSessionId = sessionEvent.SessionId,
                    OriginConversationId = sessionEvent.ConversationId,
                                        // Strip LLM control / role-injection markup before persisting raw transcript
                    // text to the searchable store - defends against memory-poisoning (#1560).
                    // Then delimit through the single shared encoder so no user text can forge an
                    // extra role record in the stored row (#2954).
                    Content = TranscriptTurnFormat.Encode(
                        UntrustedContentSanitizer.Sanitize(pendingUser.Content),
                        UntrustedContentSanitizer.Sanitize(turn.Content)),
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

    /// <summary>
    /// Assembles the daily-note context and applies the caller's token budget (#2871).
    /// </summary>
    /// <remarks>
    /// Budgeting is applied here, at the single point where assembled content becomes the returned
    /// context, rather than at the caller. Enforcing it at the provider means every consumer of
    /// <see cref="IAgentMemory"/> gets the bound the contract advertises, and a future second
    /// caller cannot silently opt out of it by forgetting to trim. The trimming order and the
    /// disclosure rule are documented on <see cref="MemoryPromptBudget"/>.
    /// </remarks>
    private async Task<AgentMemoryContext> LoadDailyMemoryContextAsync(string workspacePath, int maxTokenBudget, CancellationToken ct)
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
                    // Daily notes under the agent's own memory root are written by the agent
                    // itself through memory_save, so they are first-party `agent` content (#2480)
                    // -- UNLESS the note was quarantined at write time because the run that
                    // produced it consumed foreign content (#2519). Markdown notes have no
                    // provenance column, so the marker embedded in the content IS the provenance
                    // record; deriving it here is what stops a quarantined note from being handed
                    // back as first-party knowledge on a later session.
                    var provenance = MemoryQuarantine.IsQuarantined(trimmed)
                        ? MemoryProvenance.ExternalUntrusted
                        : MemoryProvenance.Agent;
                    dailyNotes.Add(new AgentMemoryDailyNote(date, trimmed, provenance));
                }
            }
        }

        // Trust gate BEFORE budgeting (#3232 AC5). Order matters: budgeting after exclusion means
        // the surviving first-party notes get the whole budget rather than sharing it with content
        // that was about to be discarded, and the exclusion disclosure is itself subject to the cap.
        var (eligible, excludedCount) = MemoryInjectionGate.Apply(dailyNotes);

        if (excludedCount > 0)
        {
            _logger.LogWarning(
                "Withheld {ExcludedCount} non-first-party daily note(s) from always-on context for agent {AgentId}; they remain retrievable via memory_search.",
                excludedCount,
                _agentId);
        }

        // Rough token estimate: ~4 chars per token, computed by the budget helper so the reported
        // count and the enforced cap are expressed in identical units.
        var budgeted = MemoryPromptBudget.Apply(eligible, maxTokenBudget);

        if (budgeted.WasTrimmed)
        {
            _logger.LogInformation(
                "Memory prompt context for agent {AgentId} exceeded the {Budget}-token budget and was trimmed to ~{Tokens} tokens.",
                _agentId,
                maxTokenBudget,
                budgeted.ApproximateTokenCount);
        }

        return new AgentMemoryContext(null, budgeted.Notes, budgeted.ApproximateTokenCount);
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
            CreatedAt: entry.CreatedAt)
        {
            // Always the normalized value, never the raw column: recall must not be able to
            // present a NULL or malformed provenance as anything other than `unknown` (#2480).
            Provenance = entry.NormalizedProvenance,
            // Derived from the same row, not defaulted: a caller that renders the tier must see the
            // tier the ranker actually applied to this row (#3232 AC8).
            TrustTier = MemoryTrust.ToWireValue(entry.TrustTier),
            OriginConversationId = entry.OriginConversationId,
            OriginSessionId = entry.OriginSessionId
        };

    /// <summary>
    /// Maps a ranked row, preserving the fused relevance score so the caller can render a magnitude
    /// and apply a floor instead of inferring relevance from position (#2781).
    /// </summary>
    private static AgentMemorySearchResult MapToSearchResult(ScoredMemoryEntry scored)
        => MapToSearchResult(scored.Entry) with { RelevanceScore = scored.Score };
}
