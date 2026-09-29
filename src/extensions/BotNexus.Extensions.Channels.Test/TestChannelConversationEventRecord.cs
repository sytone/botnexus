using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Channels.Test;

/// <summary>
/// One structured agent event routed through the production conversation-event seam and captured
/// by the test channel for assertions.
/// </summary>
/// <param name="Address">Channel-native address selected from the immutable binding snapshot.</param>
/// <param name="AgentId">Agent that owns the conversation.</param>
/// <param name="ConversationId">Stable conversation identity.</param>
/// <param name="SessionId">Session that produced the event.</param>
/// <param name="BindingId">Eligible binding selected for this capture.</param>
/// <param name="ChannelRequestId">Origin request correlation, present only on the originating binding.</param>
/// <param name="StreamEvent">Authoritative typed event supplied by the gateway.</param>
/// <param name="Sequence">Monotonic per-adapter capture sequence.</param>
/// <param name="TimestampUtc">When the adapter captured the event.</param>
public sealed record TestChannelConversationEventRecord(
    string Address,
    AgentId AgentId,
    ConversationId ConversationId,
    SessionId SessionId,
    BindingId? BindingId,
    string? ChannelRequestId,
    AgentStreamEvent StreamEvent,
    long Sequence,
    DateTimeOffset TimestampUtc);
