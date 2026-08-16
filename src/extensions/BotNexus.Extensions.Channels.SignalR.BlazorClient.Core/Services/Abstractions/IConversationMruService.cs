namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// #3064: a per-agent most-recently-used list of conversations the user has actually navigated to.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the URL can be the single source of rendered identity. An agent-only route
/// (<c>/agent/{AgentId}</c>) carries no conversation segment, so every component below it has had to
/// resolve identity ambiently from <c>AgentState.ActiveConversationId</c>. Consulting this MRU lets
/// the route seam resolve an explicit conversation ONCE and redirect, after which identity flows
/// down by parameter.
/// </para>
/// <para>
/// It is deliberately NOT the same question as <c>ActiveConversationId</c>, which conflates "what am
/// I looking at" with "what is most current" and has no ordering. This answers exactly one question:
/// which conversations has this circuit navigated to, most recent first.
/// </para>
/// <para>
/// In-memory and scoped to the circuit by registration - persisting it across sessions is explicitly
/// out of scope for #3064, and a shared (singleton) instance would leak one user's navigation
/// history into another user's redirect.
/// </para>
/// </remarks>
public interface IConversationMruService
{
    /// <summary>
    /// Records <paramref name="conversationId"/> as the most recently navigated conversation for
    /// <paramref name="agentId"/>, promoting it if already present. Blank ids are ignored so a
    /// partially-populated route can never poison the list.
    /// </summary>
    void Record(string agentId, string conversationId);

    /// <summary>
    /// The most recently navigated conversation for <paramref name="agentId"/>, or <c>null</c> when
    /// this circuit has never navigated to one for that agent (cold start / hard refresh).
    /// </summary>
    string? GetMostRecent(string agentId);

    /// <summary>
    /// The agent's navigated conversations, most recent first. Empty for an unknown agent.
    /// </summary>
    IReadOnlyList<string> GetForAgent(string agentId);

    /// <summary>
    /// Drops a single conversation from the agent's list, leaving the rest intact so the previous
    /// entry becomes the answer (the shape a UI-initiated delete needs).
    /// </summary>
    void Remove(string agentId, string conversationId);
}
