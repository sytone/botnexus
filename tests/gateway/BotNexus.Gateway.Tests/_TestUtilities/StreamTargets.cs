using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Convenience factory for building <see cref="ChannelStreamTarget"/> instances in unit tests
/// where the test does not care about distinguishing the SessionId and ChannelAddress fields.
/// Production code should always construct <see cref="ChannelStreamTarget"/> explicitly with
/// the correct typed values from the source <c>OutboundMessage</c> / <c>ChannelBinding</c>.
/// </summary>
internal static class StreamTargets
{
    /// <summary>
    /// Builds a <see cref="ChannelStreamTarget"/> where the same string is used for both
    /// the SessionId and the ChannelAddress. Use this in tests where the adapter under
    /// test only consumes one of those fields and the test does not need to distinguish.
    /// </summary>
    public static ChannelStreamTarget For(string value) =>
        new(SessionId.From(value), ChannelAddress.From(value), null);
}
