using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using BotNexus.Domain.Serialization;

namespace BotNexus.Domain.Primitives;

[JsonConverter(typeof(SmartEnumJsonConverter<SessionType>))]
/// <summary>
/// Represents session type.
/// </summary>
public sealed class SessionType : IEquatable<SessionType>
{
    private static readonly ConcurrentDictionary<string, SessionType> Registry = new(StringComparer.OrdinalIgnoreCase);

    public static readonly SessionType UserAgent = Register("user-agent");
    public static readonly SessionType AgentSelf = Register("agent-self");
    public static readonly SessionType AgentSubAgent = Register("agent-subagent");
    public static readonly SessionType AgentAgent = Register("agent-agent");
    public static readonly SessionType Soul = Register("soul");
    public static readonly SessionType Cron = Register("cron");
    public static readonly SessionType Heartbeat = Register("heartbeat");
    public static readonly SessionType MultiAgent = Register("multi-agent");

    /// <summary>
    /// Gets the value.
    /// </summary>
    public string Value { get; }

    private SessionType(string value) => Value = value;

    /// <summary>
    /// Executes from string.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The from string result.</returns>
    public static SessionType FromString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("SessionType cannot be empty", nameof(value));

        return Registry.GetOrAdd(value.Trim().ToLowerInvariant(), static v => new SessionType(v));
    }

    /// <summary>
    /// Performs the declared conversion or operator operation.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The operator session type result.</returns>
    public static implicit operator SessionType(string value) => FromString(value);
    /// <summary>
    /// Performs the declared conversion or operator operation.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The operator string result.</returns>
    public static implicit operator string(SessionType type) => type.Value;

    /// <summary>
    /// Executes equals.
    /// </summary>
    /// <param name="other">The other.</param>
    /// <returns>The equals result.</returns>
    public bool Equals(SessionType? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Executes equals.
    /// </summary>
    /// <param name="obj">The obj.</param>
    /// <returns>The equals result.</returns>
    public override bool Equals(object? obj) => obj is SessionType other && Equals(other);
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

    private static SessionType Register(string value)
    {
        var type = new SessionType(value);
        Registry.TryAdd(value, type);
        return type;
    }
}
