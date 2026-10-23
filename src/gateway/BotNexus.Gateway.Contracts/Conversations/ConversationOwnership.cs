using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Conversations;

/// <summary>Canonical identity and owner only; no aggregate state or read-through cache.</summary>
public sealed record ConversationOwnership(ConversationId ConversationId, AgentId AgentId);
