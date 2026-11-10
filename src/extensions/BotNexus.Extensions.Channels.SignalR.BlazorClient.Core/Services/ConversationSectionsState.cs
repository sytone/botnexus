namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Scoped confirmed section/assignment/order snapshot. Loads are newest-wins; switching agents
/// synchronously invalidates visible data. Mutations are pessimistic and serialized with captured
/// scope identity. A failed or ambiguous save never becomes a browser-only successful edit.
/// </summary>
public sealed class ConversationSectionsState
{
    private readonly SectionsApiClient _api;
    private IReadOnlyList<SectionDto> _sections = [];
    private IReadOnlyDictionary<string, string> _assignments = new Dictionary<string, string>(StringComparer.Ordinal);
    private IReadOnlyList<SectionGroupOrderDto> _groupOrder = [];
    private string _agentId = string.Empty;
    private int _scopeGeneration;
    private int _loadGeneration;
    private int _writer;

    /// <summary>Uses the shared server-backed client; no browser storage participates.</summary>
    public ConversationSectionsState(SectionsApiClient api) => _api = api;
    /// <summary>Signals snapshot, loading and mutation transitions, including immediate invalidation.</summary>
    public event Action? Changed;
    /// <summary>The selected scope, not a claim that its snapshot loaded successfully.</summary>
    public string AgentId => _agentId;
    /// <summary>Last confirmed custom records for this scope, never a previous agent's records.</summary>
    public IReadOnlyList<SectionDto> Sections => _sections;
    /// <summary>Only assignments targeting owned existing records in the confirmed snapshot.</summary>
    public IReadOnlyDictionary<string, string> Assignments => _assignments;
    /// <summary>Full reconciled peer order, never derived from visible row counts.</summary>
    public IReadOnlyList<SectionGroupOrderDto> GroupOrder => _groupOrder;
    /// <summary>Distinguishes successful empty data from unavailable/never-loaded state.</summary>
    public bool HasSnapshot { get; private set; }
    /// <summary>A current-scope read is pending; old-scope completions cannot clear this flag.</summary>
    public bool IsLoading { get; private set; }
    /// <summary>Current read failure; the last confirmed same-agent snapshot is retained.</summary>
    public string? LoadError { get; private set; }
    /// <summary>A scoped service write is pending, including a write finishing for an old agent.</summary>
    public bool IsSaving => Volatile.Read(ref _writer) != 0;
    /// <summary>Contextual mutation error; a confirmed write is explicitly distinguished from rejection.</summary>
    public string? MutationError { get; private set; }
    /// <summary>Independent read constituents disagreed. Refresh explicitly; never persist this repair.</summary>
    public bool RequiresReconciliation { get; private set; }
    /// <summary>Read required before any next mutation, including after an unknown transport outcome.</summary>
    public bool RequiresRefresh { get; private set; }
    /// <summary>A write succeeded, but authoritative refresh/representation could not be established.</summary>
    public bool SavedButRefreshFailed { get; private set; }
    /// <summary>Unified endpoint was unsupported. A successful explicit refresh clears this capability error.</summary>
    public bool GroupOrderUnsupported { get; private set; }
    /// <summary>UI can disable edits until the selected scope is confirmed and the writer is free.</summary>
    public bool CanMutate => HasSnapshot && !IsLoading && !IsSaving && LoadError is null &&
        !RequiresRefresh && !RequiresReconciliation && !GroupOrderUnsupported && !string.IsNullOrEmpty(_agentId);

    /// <summary>Loads a new agent; retries a failed same-agent read without an automatic retry loop.</summary>
    public Task EnsureLoadedAsync(string agentId)
    {
        if (!string.Equals(_agentId, agentId, StringComparison.Ordinal))
        {
            _agentId = agentId;
            ++_scopeGeneration;
            ++_loadGeneration;
            _sections = [];
            _assignments = new Dictionary<string, string>(StringComparer.Ordinal);
            _groupOrder = [];
            HasSnapshot = false;
            IsLoading = false;
            LoadError = null;
            ClearMutationStatus();
            RequiresReconciliation = false;
            Changed?.Invoke();
        }
        if (string.IsNullOrEmpty(agentId) || IsLoading || (HasSnapshot && LoadError is null))
            return Task.CompletedTask;
        return ReloadAsync();
    }

    /// <summary>Explicit same-agent recovery; never replays the preceding mutation.</summary>
    public Task RetryAsync() => ReloadAsync();

    /// <summary>Refreshes newest-wins; a failure keeps confirmed data and exposes unavailable state.</summary>
    public async Task ReloadAsync()
    {
        if (string.IsNullOrEmpty(_agentId)) return;
        var agent = _agentId;
        var scope = _scopeGeneration;
        var load = ++_loadGeneration;
        IsLoading = true;
        LoadError = null;
        Changed?.Invoke();
        try
        {
            var dto = await _api.ListAsync(agent);
            if (!Current(agent, scope) || load != _loadGeneration) return;
            var reconciled = ConversationSidebarGroups.Reconcile(agent, dto);
            _sections = reconciled.Sections;
            _assignments = reconciled.Assignments;
            _groupOrder = reconciled.GroupOrder;
            HasSnapshot = true;
            RequiresReconciliation = reconciled.RequiresRefresh;
            ClearMutationStatus();
            RequiresRefresh = reconciled.RequiresRefresh;
        }
        catch (SectionsApiException error)
        {
            if (!Current(agent, scope) || load != _loadGeneration) return;
            LoadError = error.Message;
            RequiresRefresh = true;
        }
        finally
        {
            if (Current(agent, scope) && load == _loadGeneration)
            {
                IsLoading = false;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Returns a valid custom override; stale/deleted targets resolve to natural home.</summary>
    public string? GetAssignedSectionId(string conversationId) =>
        _assignments.TryGetValue(conversationId, out var sectionId) ? sectionId : null;
    /// <summary>Legacy assignment lookup; new renderers must use the actual-row shared projection.</summary>
    public IReadOnlyList<string> ConversationsFor(string sectionId) =>
        _assignments.Where(a => a.Value == sectionId).Select(a => a.Key).ToList();

    /// <summary>Persists custom collapse pessimistically; rejected saves leave confirmed data unchanged.</summary>
    public async Task ToggleCollapseAsync(SectionDto section)
    {
        if (!Owns(section.SectionId) || section.AgentId != _agentId) return;
        var id = section.SectionId;
        var collapsed = !section.IsCollapsed;
        await MutateAsync(agent => _api.UpdateAsync(agent, id, null, collapsed));
    }

    /// <summary>Legacy custom-index movement retains the custom-only endpoint until UI migration.</summary>
    public async Task MoveAsync(int from, int to)
    {
        if (!ValidMove(from, to, _sections.Count)) return;
        var ids = _sections.Select(s => s.SectionId).ToList();
        Move(ids, from, to);
        await MutateAsync(agent => _api.ReorderAsync(agent, ids));
    }

    /// <summary>Moves canonical peers, including empty/hidden groups; success uses the server order.</summary>
    public Task<bool> MoveGroupAsync(int from, int to)
    {
        if (!ValidMove(from, to, _groupOrder.Count)) return Task.FromResult(false);
        var order = _groupOrder.ToList();
        Move(order, from, to);
        IReadOnlyList<SectionGroupOrderDto>? canonical = null;
        return MutateAsync(async agent =>
        {
            canonical = await _api.ReorderGroupsAsync(agent, order);
            return true;
        }, () => canonical);
    }

    /// <summary>Creates once; successful write plus failed refresh blocks any blind repost.</summary>
    public Task<bool> CreateAsync(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > 100) return Reject("Section name must contain 1 to 100 characters.");
        return MutateAsync(async agent => await _api.CreateAsync(agent, trimmed) is not null);
    }
    /// <summary>Renames an owned custom record, preserving all confirmed state on rejection.</summary>
    public Task<bool> RenameAsync(string sectionId, string name)
    {
        var trimmed = name.Trim();
        if (!Owns(sectionId) || trimmed.Length is 0 or > 100) return Reject("Select an owned section and a name of 1 to 100 characters.");
        return MutateAsync(agent => _api.UpdateAsync(agent, sectionId, trimmed, null));
    }
    /// <summary>Deletes an owned custom override container, never the conversations themselves.</summary>
    public Task<bool> DeleteAsync(string sectionId) => !Owns(sectionId) ? Reject("Section is not available for this agent.")
        : MutateAsync(agent => _api.DeleteAsync(agent, sectionId));

    /// <summary>
    /// Assigns only an actual writable non-default user row supplied by the active-agent caller.
    /// Server ownership/visibility guards remain authoritative; this cannot grant write permission.
    /// </summary>
    public Task<bool> AssignAsync(ConversationState conversation, string sectionId, SelectionSource selectionSource, bool agentIsReadOnly = false) =>
        !Owns(sectionId) || !Writable(conversation, selectionSource, agentIsReadOnly) ? Reject("Conversation cannot be moved to this section.")
            : MutateAsync(agent => _api.AssignAsync(agent, sectionId, conversation.ConversationId));
    /// <summary>Removes an override only for a currently writable row; does not change pin/source facts.</summary>
    public Task<bool> UnassignAsync(ConversationState conversation, SelectionSource selectionSource, bool agentIsReadOnly = false) =>
        !Writable(conversation, selectionSource, agentIsReadOnly) ? Reject("Conversation cannot be moved.")
            : MutateAsync(agent => _api.UnassignAsync(agent, conversation.ConversationId));

    private async Task<bool> MutateAsync(Func<string, Task<bool>> write, Func<IReadOnlyList<SectionGroupOrderDto>?>? canonicalOrder = null)
    {
        // Fail closed rather than enqueueing stale UI indices/drafts behind another write.
        if (!CanMutate || Interlocked.CompareExchange(ref _writer, 1, 0) != 0) return false;
        var agent = _agentId;
        var scope = _scopeGeneration;
        ++_loadGeneration;
        MutationError = null;
        Changed?.Invoke();
        try
        {
            if (!await write(agent)) return false;
            if (!Current(agent, scope)) return true; // Saved A must never refresh or repaint B (including A→B→A).
            // Any read started before the write completed may predate persistence.
            ++_loadGeneration;
            IsLoading = false;
            if (canonicalOrder?.Invoke() is { } canonical && TryPublishCanonical(agent, canonical))
                return true;
            RequiresRefresh = true;
            await ReloadAsync();
            if (!Current(agent, scope)) return true;
            if (IsLoading || LoadError is not null || RequiresReconciliation || RequiresRefresh)
            {
                SavedButRefreshFailed = true;
                RequiresRefresh = true;
                MutationError = "Saved, but refresh failed or returned inconsistent data. Refresh before continuing; do not save again.";
                return false;
            }
            return true;
        }
        catch (SectionsApiException error)
        {
            if (Current(agent, scope))
            {
                MutationError = error.Message;
                GroupOrderUnsupported = error.UnsupportedGroupOrder;
                RequiresRefresh = error.OutcomeUnknown || error.WriteConfirmed || error.UnsupportedGroupOrder;
                SavedButRefreshFailed = error.WriteConfirmed;
                // Discard any GET that started before this uncertain completion. Recovery is an explicit new GET.
                ++_loadGeneration;
                IsLoading = false;
            }
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _writer, 0);
            Changed?.Invoke();
        }
    }

    private bool TryPublishCanonical(string agent, IReadOnlyList<SectionGroupOrderDto> canonical)
    {
        // Unlike GET, the PUT response is the canonical order at its commit boundary. Recompute the
        // legacy custom subsequence locally only from that confirmed response, never from row counts.
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in canonical)
        {
            if (entry?.SectionId is { } id)
                ranks.TryAdd(id, ranks.Count);
        }
        var sections = _sections.Select(s => new SectionDto
        {
            AgentId = s.AgentId, SectionId = s.SectionId, Name = s.Name, IsCollapsed = s.IsCollapsed,
            Order = ranks.GetValueOrDefault(s.SectionId, int.MaxValue),
        }).ToArray();
        var snapshot = ConversationSidebarGroups.Reconcile(agent, new SectionListDto
        {
            Sections = sections, Assignments = _assignments, GroupOrder = canonical,
        });
        if (snapshot.RequiresRefresh) return false; // Concurrent membership change: explicit GET reconciliation.
        _sections = snapshot.Sections;
        _groupOrder = snapshot.GroupOrder;
        ClearMutationStatus();
        return true;
    }

    private bool Current(string agent, int scope) => agent == _agentId && scope == _scopeGeneration;
    private bool Owns(string id) => _sections.Any(s => s.AgentId == _agentId && s.SectionId == id);
    private static bool Writable(ConversationState row, SelectionSource source, bool readOnlyAgent) =>
        !readOnlyAgent && !row.IsDefault && !row.Project(source).IsReadOnly &&
        PortalListOrdering.IsUserFacingConversation(row) && !PortalListOrdering.IsArchivedConversation(row);
    private Task<bool> Reject(string message)
    {
        MutationError = message;
        Changed?.Invoke();
        return Task.FromResult(false);
    }
    private void ClearMutationStatus()
    {
        MutationError = null;
        RequiresRefresh = false;
        SavedButRefreshFailed = false;
        GroupOrderUnsupported = false;
    }
    private static bool ValidMove(int from, int to, int count) => from >= 0 && from < count && to >= 0 && to < count && from != to;
    private static void Move<T>(List<T> items, int from, int to)
    {
        var item = items[from];
        items.RemoveAt(from);
        items.Insert(to, item);
    }
}
