using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using BotNexus.Domain.Serialization;

namespace BotNexus.Domain.Primitives;

[JsonConverter(typeof(SmartEnumJsonConverter<SessionType>))]
public sealed class SessionType : IEquatable<SessionType>
{
    private static readonly ConcurrentDictionary<string, SessionType> Registry = new(StringComparer.OrdinalIgnoreCase);

    public static readonly SessionType UserAgent = Register("user-agent");
    public static readonly SessionType AgentSelf = Register("agent-self");
    public static readonly SessionType AgentSubAgent = Register("agent-subagent");
    public static readonly SessionType AgentAgent = Register("agent-agent");
    public static readonly SessionType Soul = Register("soul");
    public static readonly SessionType Cron = Register("cron");

    public string Value { get; }

    private SessionType(string value) => Value = value;

    public static SessionType FromString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("SessionType cannot be empty", nameof(value));

        return Registry.GetOrAdd(value.Trim().ToLowerInvariant(), static v => new SessionType(v));
    }

    public static implicit operator SessionType(string value) => FromString(value);
    public static implicit operator string(SessionType type) => type.Value;

    public bool Equals(SessionType? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is SessionType other && Equals(other);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public override string ToString() => Value;

    private static SessionType Register(string value)
    {
        var type = new SessionType(value);
        Registry.TryAdd(value, type);
        return type;
    }
}
