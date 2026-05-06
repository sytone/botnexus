using System.Text.Json.Serialization;
using BotNexus.Domain.Serialization;

namespace BotNexus.Domain.Primitives;

[JsonConverter(typeof(BindingIdJsonConverter))]
/// <summary>
/// Strongly-typed identifier for a <c>ChannelBinding</c>, preventing accidental confusion
/// with <c>ChannelAddress</c>, <c>ThreadId</c>, or other adjacent string parameters.
/// </summary>
public readonly record struct BindingId : IComparable<BindingId>
{
    /// <summary>Gets the raw string value of this binding identifier.</summary>
    public string Value { get; }

    private BindingId(string value) => Value = value;

    /// <summary>
    /// Creates a <see cref="BindingId"/> from an existing string value.
    /// </summary>
    /// <param name="value">The raw identifier string. Must not be null or whitespace.</param>
    public static BindingId From(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("BindingId cannot be empty", nameof(value))
            : new(value.Trim());

    /// <summary>
    /// Creates a new unique <see cref="BindingId"/> using a GUID.
    /// </summary>
    public static BindingId Create() => new(Guid.NewGuid().ToString("N"));

    /// <summary>Explicit conversion from <see cref="BindingId"/> to <see cref="string"/>. Use <see cref="Value"/> or <see cref="ToString"/> instead.</summary>
    public static explicit operator string(BindingId id) => id.Value;

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <inheritdoc />
    public int CompareTo(BindingId other) => string.Compare(Value, other.Value, StringComparison.Ordinal);
}
