using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Default dispatching adapter that reuses existing conversation router/store implementations while
/// exposing a dedicated orchestration surface for future gateway transport rewiring.
/// </summary>
public sealed class DefaultConversationDispatcher : IConversationDispatcher
{
    private readonly IConversationRouter _conversationRouter;
    private readonly IConversationStore _conversationStore;

    /// <summary>
    /// Initializes a dispatcher that maps inbound dispatch contexts to conversation/session resolution.
    /// </summary>
    /// <param name="conversationRouter">Conversation router that owns active session resolution.</param>
    /// <param name="conversationStore">Conversation store used to determine new conversation creation.</param>
    public DefaultConversationDispatcher(
        IConversationRouter conversationRouter,
        IConversationStore conversationStore)
    {
        _conversationRouter = conversationRouter;
        _conversationStore = conversationStore;
    }

    /// <inheritdoc />
    public async Task<DispatchResult> DispatchAsync(InboundMessageContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var existingConversation = await ResolveExistingConversationAsync(context, cancellationToken);
        var routingResult = await _conversationRouter.ResolveInboundAsync(
            context.AgentId,
            context.Source.ChannelType,
            context.Source.ChannelAddress,
            context.RequestedConversationId,
            cancellationToken,
            initiator: context.Message.Sender);

        var resolvedSource = routingResult.OriginatingBinding is null
            ? context.Source
            : context.Source with
            {
                BindingId = routingResult.OriginatingBinding.BindingId,
                DisplayPrefix = routingResult.OriginatingBinding.DisplayPrefix
            };

        var resolution = new ConversationSessionResolution(
            routingResult.Conversation.ConversationId,
            routingResult.SessionId,
            IsNewConversation: existingConversation is null,
            IsNewSession: routingResult.IsNewSession,
            OriginatingBindingId: resolvedSource.BindingId,
            DisplayPrefix: resolvedSource.DisplayPrefix);

        return new DispatchResult(context, resolvedSource, resolution);
    }

    private async Task<Conversation?> ResolveExistingConversationAsync(
        InboundMessageContext context,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(context.RequestedConversationId))
        {
            return await _conversationStore.GetAsync(ConversationId.From(context.RequestedConversationId), cancellationToken);
        }

        return await _conversationStore.ResolveByBindingAsync(
            context.AgentId,
            context.Source.ChannelType,
            context.Source.ChannelAddress,
            cancellationToken);
    }
}
