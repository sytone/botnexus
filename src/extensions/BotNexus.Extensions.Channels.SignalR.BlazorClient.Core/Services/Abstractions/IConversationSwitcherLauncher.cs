namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Lets anything in the portal open the conversation switcher, without owning it.
/// </summary>
/// <remarks>
/// The switcher lives inside <c>ChatPanel</c>, anchored to the conversation title it switches away
/// from. That is the right home for the control and the wrong home for its <em>entry points</em>:
/// the sidebar search box sits in <c>MainLayout</c>, and the keyboard shortcut belongs to the
/// document. Both need to open a component neither of them renders, which is what this indirection
/// is for - a request, raised by whoever wants the switcher, answered by the switcher itself.
/// <para>
/// Scoped per circuit, like the rest of the client's state services, so one user's shortcut cannot
/// open another's switcher.
/// </para>
/// </remarks>
public interface IConversationSwitcherLauncher
{
    /// <summary>
    /// Raised when something asks for the switcher. <c>ConversationSwitcher</c> subscribes; the
    /// instance belonging to the active agent opens, the rest ignore it.
    /// </summary>
    event Action? Requested;

    /// <summary>Ask for the switcher to open.</summary>
    void Request();
}
