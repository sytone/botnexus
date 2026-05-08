namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Full outcome of inbound dispatch orchestration. Provides the normalized inbound context,
/// the effective source metadata, and the resolved conversation/session target.
/// </summary>
/// <param name="Context">Inbound dispatch context provided by the transport layer.</param>
/// <param name="Source">Effective source details after router/binding resolution.</param>
/// <param name="Resolution">Conversation and session resolution metadata.</param>
public sealed record DispatchResult(
    InboundMessageContext Context,
    ChannelSource Source,
    ConversationSessionResolution Resolution);
