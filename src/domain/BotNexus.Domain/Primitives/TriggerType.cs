using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using BotNexus.Domain.Serialization;

namespace BotNexus.Domain.Primitives;

[JsonConverter(typeof(SmartEnumJsonConverter<TriggerType>))]
/// <summary>
/// Represents trigger type.
/// </summary>
public sealed class TriggerType : IEquatable<TriggerType>
{
    private static readonly ConcurrentDictionary<string, TriggerType> Registry = new(StringComparer.OrdinalIgnoreCase);

    public static readonly TriggerType Cron = Register("cron");
    public static readonly TriggerType Soul = Register("soul");
    public static readonly TriggerType Heartbeat = Register("heartbeat");
    public static readonly TriggerType Memory = Register("memory");

    /// <summary>
    /// Gets the value.
    /// </summary>
    public string Value { get; }

    private TriggerType(string value) => Value = value;

    /// <summary>
    /// Executes from string.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The from string result.</returns>
    public static TriggerType FromString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("TriggerType cannot be empty", nameof(value));

        return Registry.GetOrAdd(value.Trim().ToLowerInvariant(), static v => new TriggerType(v));
    }

    /// <summary>
    /// Performs the declared conversion or operator operation.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The operator trigger type result.</returns>
    public static implicit operator TriggerType(string value) => FromString(value);
    /// <summary>
    /// Performs the declared conversion or operator operation.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The operator string result.</returns>
    public static implicit operator string(TriggerType type) => type.Value;

    /// <summary>
    /// Executes equals.
    /// </summary>
    /// <param name="other">The other.</param>
    /// <returns>The equals result.</returns>
    public bool Equals(TriggerType? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Executes equals.
    /// </summary>
    /// <param name="obj">The obj.</param>
    /// <returns>The equals result.</returns>
    public override bool Equals(object? obj) => obj is TriggerType other && Equals(other);
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

    private static TriggerType Register(string value)
    {
        var type = new TriggerType(value);
        Registry.TryAdd(value, type);
        return type;
    }
}
