using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Conversations;

/// <summary>
/// Minimal active-conversation projection used by retention workers.
/// </summary>
public sealed record ConversationRetentionCandidate(
    ConversationId ConversationId,
    AgentId AgentId,
    DateTimeOffset UpdatedAt,
    bool IsPinned,
    ConversationSource Source,
    string? SourceId);
