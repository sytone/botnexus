using System.Text.Json.Serialization;
using BotNexus.Domain.Serialization;

namespace BotNexus.Domain.Primitives;

[JsonConverter(typeof(ChannelAddressJsonConverter))]
/// <summary>
/// Strongly-typed channel address, preventing accidental confusion with
/// <c>BindingId</c> or other adjacent string parameters.
/// </summary>
/// <remarks>
/// The value is opaque to the core router. Channel adapters are free to fold native
/// sub-addresses (e.g. Telegram forum topics, future Teams/Slack thread ids) into the
/// address itself using a channel-specific encoding — the core only routes by
/// <c>(ChannelType, ChannelAddress)</c>. See <c>TelegramChannelAddress</c> in the
/// Telegram extension for an example encoding (<c>&lt;chatId&gt;/topic:&lt;threadId&gt;</c>).
/// </remarks>
public readonly record struct ChannelAddress : IComparable<ChannelAddress>
{
    /// <summary>Gets the raw string value of this channel address.</summary>
    public string Value { get; }

    private ChannelAddress(string value) => Value = value;

    /// <summary>
    /// Creates a <see cref="ChannelAddress"/> from a string value.
    /// </summary>
    /// <param name="value">The raw channel address string. Must not be null.</param>
    public static ChannelAddress From(string value) =>
        new(value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>
    /// Empty channel address — used for addressless channels (e.g. portal SignalR
    /// where the address is derived from the agent identity at routing time).
    /// </summary>
    public static ChannelAddress Empty => new(string.Empty);

    /// <summary>Gets a value indicating whether this address is empty.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    // No implicit conversions — use .Value or .From() explicitly to keep the boundary visible.

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <inheritdoc />
    public int CompareTo(ChannelAddress other) =>
        string.Compare(Value, other.Value, StringComparison.Ordinal);
}
