using System.Text.Json.Serialization;
using BotNexus.Domain.Serialization;

namespace BotNexus.Domain.Primitives;

[JsonConverter(typeof(ChannelKeyJsonConverter))]
public readonly record struct ChannelKey : IComparable<ChannelKey>, IEquatable<ChannelKey>
{
    public string Value { get; }

    public ChannelKey(string value)
    {
        Value = Normalize(value);
    }

    public static ChannelKey From(string value) => new(value);

    public static implicit operator string(ChannelKey channelKey) => channelKey.Value;
    public static implicit operator ChannelKey(string value) => From(value);

    public bool Equals(ChannelKey other) =>
        string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public override string ToString() => Value;
    public int CompareTo(ChannelKey other) => string.Compare(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("ChannelKey cannot be empty", nameof(value));

        return value.Trim().ToLowerInvariant();
    }
}
