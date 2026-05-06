using System.Text.Json.Serialization;
using BotNexus.Domain.Serialization;

namespace BotNexus.Domain.Primitives;

[JsonConverter(typeof(ChannelAddressJsonConverter))]
/// <summary>
/// Strongly-typed channel address, preventing accidental confusion with <c>ThreadId</c>,
/// <c>BindingId</c>, or other adjacent string parameters.
/// </summary>
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
