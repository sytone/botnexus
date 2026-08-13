namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Scoped holder for the user-defined conversation section state (issue #2124) that was previously
/// owned by the <c>ConversationSectionsPanel</c> component (issue #2325). Lifting it out of the
/// component means consumers - notably the sidebar "move to section" menu in the layout - resolve
/// sections and assignments from this service instead of a component <c>@ref</c>, so the panel's
/// presence in the render tree no longer determines whether that menu has data.
/// </summary>
public sealed class ConversationSectionsState
{
    private readonly SectionsApiClient _api;
    private List<SectionDto> _sections = [];
    private Dictionary<string, string> _assignments = new(StringComparer.Ordinal);
    private string _loadedAgentId = string.Empty;
    private int _loadGeneration;

    /// <summary>Initialises the state over the sections REST client.</summary>
    public ConversationSectionsState(SectionsApiClient api) => _api = api;

    /// <summary>Raised whenever the section list or assignment map changes.</summary>
    public event Action? Changed;

    /// <summary>The agent whose sections are currently loaded.</summary>
    public string AgentId => _loadedAgentId;

    /// <summary>The loaded sections in display order.</summary>
    public IReadOnlyList<SectionDto> Sections => _sections;

    /// <summary>Map of conversation id to the section id it is assigned to.</summary>
    public IReadOnlyDictionary<string, string> Assignments => _assignments;

    /// <summary>Loads the supplied agent's sections when the agent changed (or was never loaded).</summary>
    public async Task EnsureLoadedAsync(string agentId)
    {
        if (string.IsNullOrEmpty(agentId) || string.Equals(_loadedAgentId, agentId, StringComparison.Ordinal))
            return;
        _loadedAgentId = agentId;
        await ReloadAsync();
    }

    /// <summary>
    /// Reloads sections and assignments for the currently loaded agent from the server.
    /// </summary>
    /// <remarks>
    /// Loads are versioned because several callers can have one in flight at once - the panel's
    /// <c>EnsureLoadedAsync</c> on first render, an assign/unassign from the sidebar menu, and an
    /// agent switch. HTTP responses are not guaranteed to complete in call order, so an unguarded
    /// last-write-wins assignment lets a slower EARLIER response overwrite a newer one and strand
    /// the UI on stale sections. Only the newest generation is allowed to publish.
    /// </remarks>
    public async Task ReloadAsync()
    {
        if (string.IsNullOrEmpty(_loadedAgentId))
            return;
        var generation = ++_loadGeneration;
        var agentId = _loadedAgentId;
        var dto = await _api.ListAsync(agentId);
        // A newer load (or an agent switch) started while this one was awaiting - discard this result.
        if (generation != _loadGeneration || !string.Equals(agentId, _loadedAgentId, StringComparison.Ordinal))
            return;
        _sections = dto.Sections.OrderBy(s => s.Order).ToList();
        _assignments = new Dictionary<string, string>(dto.Assignments, StringComparer.Ordinal);
        Changed?.Invoke();
    }

    /// <summary>Returns the section id a conversation is assigned to, or null when unassigned.</summary>
    public string? GetAssignedSectionId(string conversationId) =>
        _assignments.TryGetValue(conversationId, out var sectionId) ? sectionId : null;

    /// <summary>Conversation ids assigned to the given section.</summary>
    public IReadOnlyList<string> ConversationsFor(string sectionId) =>
        _assignments.Where(a => string.Equals(a.Value, sectionId, StringComparison.Ordinal))
            .Select(a => a.Key)
            .ToList();

    /// <summary>
    /// Flips a section's collapsed preference locally (so the chevron responds immediately) and
    /// persists it server-side.
    /// </summary>
    public async Task ToggleCollapseAsync(SectionDto section)
    {
        section.IsCollapsed = !section.IsCollapsed;
        Changed?.Invoke();
        await _api.UpdateAsync(_loadedAgentId, section.SectionId, name: null, isCollapsed: section.IsCollapsed);
    }

    /// <summary>Moves a section between positions locally and persists the new order server-side.</summary>
    public async Task MoveAsync(int from, int to)
    {
        if (from < 0 || from >= _sections.Count || to < 0 || to >= _sections.Count || from == to)
            return;
        var item = _sections[from];
        _sections.RemoveAt(from);
        _sections.Insert(to, item);
        Changed?.Invoke();
        await _api.ReorderAsync(_loadedAgentId, _sections.Select(s => s.SectionId).ToList());
    }
}
