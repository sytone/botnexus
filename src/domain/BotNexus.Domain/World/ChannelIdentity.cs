using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.World;

/// <summary>
/// A citizen's identity on a single channel — pairs the channel key with the address
/// the citizen is addressable at on that channel.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SenderAddress"/> is the <b>sender-side</b> address — i.e. the address
/// that uniquely identifies <i>this citizen</i> on the channel. It is deliberately
/// distinct from <c>InboundMessage.ChannelAddress</c>, which is the <b>conversation</b>
/// address (chat / room / topic) that messages flow through. In a group chat several
/// citizens share a single <c>ChannelAddress</c> on the message but each has their own
/// <see cref="SenderAddress"/> here.
/// </para>
/// <para>
/// Example: a Telegram group chat at <c>ChannelAddress = "-1001234"</c> may have
/// alice with <c>ChannelIdentity { Channel = "telegram", SenderAddress = "555111" }</c>
/// and bob with <c>SenderAddress = "555222"</c>.
/// </para>
/// </remarks>
/// <param name="Channel">The channel this identity is reachable on.</param>
/// <param name="SenderAddress">
/// The address that uniquely identifies the citizen on <paramref name="Channel"/>.
/// </param>
public readonly record struct ChannelIdentity(ChannelKey Channel, ChannelAddress SenderAddress);
