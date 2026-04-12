using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using BotNexus.Domain.Serialization;

namespace BotNexus.Domain.Primitives;

[JsonConverter(typeof(SmartEnumJsonConverter<TriggerType>))]
public sealed class TriggerType : IEquatable<TriggerType>
{
    private static readonly ConcurrentDictionary<string, TriggerType> Registry = new(StringComparer.OrdinalIgnoreCase);

    public static readonly TriggerType Cron = Register("cron");
    public static readonly TriggerType Soul = Register("soul");
    public static readonly TriggerType Heartbeat = Register("heartbeat");

    public string Value { get; }

    private TriggerType(string value) => Value = value;

    public static TriggerType FromString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("TriggerType cannot be empty", nameof(value));

        return Registry.GetOrAdd(value.Trim().ToLowerInvariant(), static v => new TriggerType(v));
    }

    public static implicit operator TriggerType(string value) => FromString(value);
    public static implicit operator string(TriggerType type) => type.Value;

    public bool Equals(TriggerType? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is TriggerType other && Equals(other);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public override string ToString() => Value;

    private static TriggerType Register(string value)
    {
        var type = new TriggerType(value);
        Registry.TryAdd(value, type);
        return type;
    }
}
