using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;

namespace BotNexus.Gateway.Conversations;

public sealed class ConversationCanvasNotifier(IConversationStore store) : IAgentCanvasNotifier
{
    private readonly IConversationStore _store = store;

    public async Task NotifyCanvasUpdatedAsync(string agentId, string conversationId, string html, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return;
        var conversation = await _store.GetAsync(ConversationId.From(conversationId), cancellationToken).ConfigureAwait(false);
        if (conversation is null)
            return;
        conversation.CanvasHtml = string.IsNullOrEmpty(html) ? null : html;
        await _store.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);
    }
}