using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Events;

namespace BotNexus.Extensions.Channels.Test;

/// <summary>
/// One non-agent conversation lifecycle event routed through the production event publisher and
/// captured by the test channel for scenario assertions.
/// </summary>
public sealed record TestChannelLifecycleEventRecord(
    string Address,
    BindingId? BindingId,
    ConversationEvent ConversationEvent,
    long Sequence,
    DateTimeOffset TimestampUtc);
