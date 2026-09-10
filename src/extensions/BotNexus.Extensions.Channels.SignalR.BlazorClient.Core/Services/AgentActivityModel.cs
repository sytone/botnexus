namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// What an agent is doing right now, at the granularity the portal can actually observe.
/// </summary>
/// <remarks>
/// Four states, not six. The comparator this came from shows idle / working / waiting / blocked /
/// thinking / done, but BotNexus tracks a connection flag, a streaming flag, in-flight tool calls
/// and an observer classification — and nothing that distinguishes "waiting" from "blocked", or
/// "done" from "idle". Rendering a state the data cannot support would put a confident dot on a
/// guess, which is worse than the text label it replaces.
/// </remarks>
public enum AgentActivity
{
    /// <summary>Connected with nothing in flight.</summary>
    Idle,

    /// <summary>Generating a response.</summary>
    Working,

    /// <summary>Running one or more tools. Distinct from <see cref="Working"/> because it is the
    /// state that most often takes long enough for someone to wonder whether anything is happening.</summary>
    UsingTools,

    /// <summary>The hub connection is down, so what this agent is doing is unknown.</summary>
    Offline,

    /// <summary>A read-only observer view of a sub-agent, which has no activity of its own.</summary>
    ReadOnly,
}

/// <summary>How one agent's activity should be drawn and announced.</summary>
/// <param name="Activity">The state itself.</param>
/// <param name="Label">Human-readable text. Carried for screen readers and for the card's own label.</param>
/// <param name="CssModifier">The class suffix the stylesheet keys on, e.g. <c>working</c>.</param>
/// <param name="ShowsIndicator">
/// False for <see cref="AgentActivity.Idle"/>: a dot on every agent is a dot that means nothing.
/// The point is that the two working agents stand out among sixteen.
/// </param>
public sealed record AgentActivitySpec(
    AgentActivity Activity,
    string Label,
    string CssModifier,
    bool ShowsIndicator);

/// <summary>
/// Derives an agent's activity from the flags the client already holds.
/// </summary>
/// <remarks>
/// State reads as "○ Idle" beside a name today, so finding the working agent among sixteen means
/// reading sixteen labels. Folding it into the avatar puts identity and activity in the same piece
/// of real estate, which is what makes it readable at a glance rather than on inspection.
/// <para>
/// The text label is kept rather than replaced: the indicator is a colour and a shape, and colour
/// alone is not an accessible signal.
/// </para>
/// </remarks>
public static class AgentActivityModel
{
    /// <summary>
    /// Resolve an agent's activity.
    /// </summary>
    /// <remarks>
    /// Order matters and is deliberate. Read-only wins because an observer view has no activity of
    /// its own to report. Offline wins next: while the connection is down every other flag is a
    /// stale memory of what was true when it dropped, and reporting "working" from a dead
    /// connection is the one reading that could send someone looking for output that will never
    /// arrive. Tools beat streaming because a tool call is the part that takes long enough to
    /// wonder about.
    /// </remarks>
    /// <param name="isConnected">Whether the hub connection is live.</param>
    /// <param name="isStreaming">Whether a response is streaming.</param>
    /// <param name="activeToolCalls">How many tool calls are in flight.</param>
    /// <param name="isReadOnly">Whether this is a read-only observer entry.</param>
    /// <returns>How to draw and announce the state.</returns>
    public static AgentActivitySpec For(
        bool isConnected,
        bool isStreaming,
        int activeToolCalls,
        bool isReadOnly = false)
    {
        if (isReadOnly)
            return new AgentActivitySpec(AgentActivity.ReadOnly, "Read-only", "readonly", ShowsIndicator: false);

        if (!isConnected)
            return new AgentActivitySpec(AgentActivity.Offline, "Offline", "offline", ShowsIndicator: true);

        if (activeToolCalls > 0)
            return new AgentActivitySpec(AgentActivity.UsingTools, "Using tools", "tools", ShowsIndicator: true);

        if (isStreaming)
            return new AgentActivitySpec(AgentActivity.Working, "Working", "working", ShowsIndicator: true);

        return new AgentActivitySpec(AgentActivity.Idle, "Idle", "idle", ShowsIndicator: false);
    }
}
