using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Identifies the originating channel endpoint for an inbound message so transport layers
/// and dispatching layers can share a stable routing model.
/// </summary>
/// <param name="ChannelType">External channel type (for example signalr or telegram).</param>
/// <param name="ChannelAddress">Channel-native address used for reply delivery.</param>
/// <param name="SenderId">Identity of the message sender within the source channel.</param>
/// <param name="ThreadId">Optional native thread/topic identifier for threaded channels.</param>
/// <param name="BindingId">Optional conversation binding identifier resolved during dispatch.</param>
/// <param name="DisplayPrefix">Optional display prefix used by prefix threading modes.</param>
public sealed record ChannelSource(
    ChannelKey ChannelType,
    ChannelAddress ChannelAddress,
    string SenderId,
    ThreadId? ThreadId = null,
    BindingId? BindingId = null,
    string? DisplayPrefix = null);
