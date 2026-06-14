using BotNexus.Gateway.Abstractions.Conversations;

namespace BotNexus.Gateway.Conversations;

/// <summary>
/// No-op implementation of <see cref="IConversationChangeNotifier"/> used as a fallback when
/// no channel-specific notifier is registered (e.g. when SignalR extension is not loaded).
/// Prevents DI validation failures on startup with minimal or empty configurations.
/// </summary>
internal sealed class NullConversationChangeNotifier : IConversationChangeNotifier
{
    public Task NotifyConversationChangedAsync(string changeType, string agentId, string conversationId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
