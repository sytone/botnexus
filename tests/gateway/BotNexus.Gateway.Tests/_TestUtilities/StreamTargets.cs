using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Convenience factory for building <see cref="ChannelStreamTarget"/> instances in unit tests
/// where the test does not care about distinguishing the ConversationId, SessionId, and
/// ChannelAddress fields. Production code should always construct
/// <see cref="ChannelStreamTarget"/> explicitly with the correct typed values from the source
/// <c>OutboundMessage</c> / <c>ChannelBinding</c>.
/// </summary>
internal static class StreamTargets
{
    /// <summary>
    /// Builds a <see cref="ChannelStreamTarget"/> where the same string is used for the
    /// ConversationId, SessionId, and ChannelAddress. Use this in tests where the adapter
    /// under test only consumes one of those fields and the test does not need to
    /// distinguish.
    /// </summary>
    public static ChannelStreamTarget For(string value) =>
        new(ConversationId.From(value), SessionId.From(value), ChannelAddress.From(value), null);

    /// <summary>
    /// Builds a <see cref="ChannelStreamTarget"/> with explicit values for each field.
    /// Use this in tests that exercise routing logic where the three values must differ
    /// (e.g. post-compaction tests that pin the same conversation against a new session).
    /// </summary>
    public static ChannelStreamTarget For(string conversationId, string sessionId, string channelAddress, string? bindingId = null) =>
        new(
            ConversationId.From(conversationId),
            SessionId.From(sessionId),
            ChannelAddress.From(channelAddress),
            bindingId is null ? null : BindingId.From(bindingId));
}
