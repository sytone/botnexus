using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Represents a binding between a conversation and a specific channel address (e.g., a Telegram chat, a Teams thread).
/// </summary>
public sealed record ChannelBinding
{
    /// <summary>Gets or sets the unique binding identifier.</summary>
    public BindingId BindingId { get; set; } = BindingId.Create();

    /// <summary>Gets or sets the channel type for this binding.</summary>
    public ChannelKey ChannelType { get; set; }

    /// <summary>Gets or sets the channel-specific address (e.g. chat id, phone number). Use <see cref="ChannelAddress.Empty"/> for addressless channels.</summary>
    public ChannelAddress ChannelAddress { get; set; } = ChannelAddress.Empty;

    /// <summary>Gets or sets the native thread or topic id within the channel, if applicable. Null for channels without thread support.</summary>
    public ThreadId? ThreadId { get; set; }

    /// <summary>Gets or sets the binding mode controlling message fan-out participation.</summary>
    public BindingMode Mode { get; set; } = BindingMode.Interactive;

    /// <summary>Gets or sets the threading mode controlling how the conversation maps to channel threads.</summary>
    public ThreadingMode ThreadingMode { get; set; } = ThreadingMode.Single;

    /// <summary>Gets or sets an optional display prefix prepended to outbound messages when using <see cref="ThreadingMode.Prefix"/>.</summary>
    public string? DisplayPrefix { get; set; }

    /// <summary>Gets or sets when this binding was created.</summary>
    public DateTimeOffset BoundAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets when the last inbound message arrived on this binding.</summary>
    public DateTimeOffset? LastInboundAt { get; set; }

    /// <summary>Gets or sets when the last outbound message was sent on this binding.</summary>
    public DateTimeOffset? LastOutboundAt { get; set; }
}
