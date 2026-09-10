namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Lets anything in the portal open the agent persona panel, without owning it.
/// </summary>
/// <remarks>
/// The panel is mounted by <c>MainLayout</c>, outside the app shell, because it has to escape the
/// overflow containment the top bar and body impose. That is the right home for it and the wrong
/// home for its <em>entry points</em>: the identity chip is rendered by MainLayout and can be
/// handed a callback directly, but the agent cards live in a page under <c>@Body</c>, which
/// MainLayout does not render and cannot pass parameters to.
/// <para>
/// So anything that is not MainLayout's own child asks through this instead - a request raised by
/// whoever wants the panel, answered by the panel itself. Same shape and same reasoning as
/// <see cref="IConversationSwitcherLauncher"/>, which exists for exactly this problem.
/// </para>
/// <para>
/// Scoped per circuit, like the rest of the client's state services, so one user's click cannot
/// open another's panel.
/// </para>
/// </remarks>
public interface IAgentPersonaLauncher
{
    /// <summary>
    /// Raised when something asks for the persona panel, carrying the agent to open it for.
    /// </summary>
    event Action<string>? Requested;

    /// <summary>Ask for the persona panel to open for one agent.</summary>
    /// <param name="agentId">The agent whose persona should be shown.</param>
    void Request(string agentId);
}
