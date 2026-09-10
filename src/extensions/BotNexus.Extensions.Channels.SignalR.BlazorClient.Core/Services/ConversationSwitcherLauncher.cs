namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Per-circuit implementation of <see cref="IConversationSwitcherLauncher"/>.
/// </summary>
/// <remarks>
/// Deliberately holds no state beyond the event. It carries no "is open" flag because the switcher
/// itself owns that: a launcher that tracked open-ness would be a second source of truth for it, and
/// the two would disagree the moment the user dismissed the panel by clicking away.
/// </remarks>
public sealed class ConversationSwitcherLauncher : IConversationSwitcherLauncher
{
    /// <inheritdoc />
    public event Action? Requested;

    /// <inheritdoc />
    public void Request() => Requested?.Invoke();
}
