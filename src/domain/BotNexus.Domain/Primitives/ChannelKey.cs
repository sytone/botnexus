using System.Text.Json.Serialization;
using BotNexus.Domain.Serialization;

namespace BotNexus.Domain.Primitives;

[JsonConverter(typeof(ChannelKeyJsonConverter))]
/// <summary>
/// Represents struct.
/// </summary>
public readonly record struct ChannelKey : IComparable<ChannelKey>, IEquatable<ChannelKey>
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["web chat"] = "signalr",
        ["web-chat"] = "signalr",
        ["webchat"] = "signalr"
    };

    /// <summary>
    /// Gets the value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// The portal/web-chat channel, which acts as the platform's channel-agnostic OBSERVER: it is
    /// expected to see every turn regardless of which channel originated it or which channels also
    /// receive it (#2631). Named here on the domain primitive that already owns this string in its
    /// alias table, so gateway orchestration never has to spell a concrete channel key and the
    /// ChannelKnowledgeFence rules (#2086 R2/R3) stay intact.
    /// </summary>
    public static ChannelKey Observer { get; } = From("signalr");

    public ChannelKey(string value)
    {
        Value = Normalize(value);
    }

    /// <summary>
    /// Executes from.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The from result.</returns>
    public static ChannelKey From(string value) => new(value);

    /// <summary>
    /// Performs the declared conversion or operator operation.
    /// </summary>
    /// <param name="channelKey">The channel key.</param>
    /// <returns>The operator string result.</returns>
    public static implicit operator string(ChannelKey channelKey) => channelKey.Value;
    /// <summary>
    /// Performs the declared conversion or operator operation.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The operator channel key result.</returns>
    public static implicit operator ChannelKey(string value) => From(value);

    /// <summary>
    /// Executes equals.
    /// </summary>
    /// <param name="other">The other.</param>
    /// <returns>The equals result.</returns>
    public bool Equals(ChannelKey other) =>
        string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Executes get hash code.
    /// </summary>
    /// <returns>The get hash code result.</returns>
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    /// <summary>
    /// Executes to string.
    /// </summary>
    /// <returns>The to string result.</returns>
    public override string ToString() => Value;
    /// <summary>
    /// Executes compare to.
    /// </summary>
    /// <param name="other">The other.</param>
    /// <returns>The compare to result.</returns>
    public int CompareTo(ChannelKey other) => string.Compare(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("ChannelKey cannot be empty", nameof(value));

        var normalized = value.Trim().ToLowerInvariant();
        return Aliases.TryGetValue(normalized, out var canonical) ? canonical : normalized;
    }
}
