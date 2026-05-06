using System.Text.Json.Serialization;
using BotNexus.Domain.Serialization;

namespace BotNexus.Domain.Primitives;

[JsonConverter(typeof(ThreadIdJsonConverter))]
/// <summary>
/// Strongly-typed thread/topic identifier within a channel, preventing accidental confusion
/// with <c>ChannelAddress</c> or other adjacent string parameters.
/// </summary>
public readonly record struct ThreadId : IComparable<ThreadId>
{
    /// <summary>Gets the raw string value of this thread identifier.</summary>
    public string Value { get; }

    private ThreadId(string value) => Value = value;

    /// <summary>
    /// Creates a <see cref="ThreadId"/> from a string value.
    /// </summary>
    /// <param name="value">The raw thread id string. Must not be null or empty.</param>
    public static ThreadId From(string value) =>
        new(value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>
    /// Creates a nullable <see cref="ThreadId"/> — returns <c>null</c> when the input is null or empty,
    /// which models the absence of a thread context on channels that don't support forum topics.
    /// </summary>
    public static ThreadId? FromNullable(string? value) =>
        string.IsNullOrEmpty(value) ? null : From(value);

    // No implicit conversions — use .Value or .From() explicitly to keep the boundary visible.

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <inheritdoc />
    public int CompareTo(ThreadId other) =>
        string.Compare(Value, other.Value, StringComparison.Ordinal);
}
