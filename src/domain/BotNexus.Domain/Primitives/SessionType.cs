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

    /// <summary>
    /// P9-E (issue #645): legacy persisted values that get remapped at the registry
    /// layer so <see cref="FromString"/> returns the canonical replacement uniformly
    /// across every serializer path. <c>"soul"</c> and <c>"heartbeat"</c> were proxy
    /// triggers stamped onto agent-self sessions; <c>"cron"</c> was a proxy trigger
    /// for a user-scheduled job. The trigger kind now lives on
    /// <see cref="BotNexus.Gateway.Abstractions.Models.SessionEntry.Trigger"/>; the
    /// session itself is just the conversation kind.
    /// </summary>
    private static readonly Dictionary<string, SessionType> LegacyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["soul"] = AgentSelf,
        ["heartbeat"] = AgentSelf,
        ["cron"] = UserAgent
    };

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

        var normalized = value.Trim().ToLowerInvariant();

        // P9-E (issue #645): transparent legacy-value migration. JSON deserialization,
        // direct string -> SessionType conversion, and any other read path hit FromString,
        // so applying the alias map here means every store benefits from the same migration
        // without a per-store hook. Pre-collapse values "soul"/"heartbeat"/"cron" land on
        // the canonical replacement (AgentSelf/AgentSelf/UserAgent); the proxy-trigger kind
        // now lives on SessionEntry.Trigger.
        if (LegacyAliases.TryGetValue(normalized, out var migrated))
            return migrated;

        return Registry.GetOrAdd(normalized, static v => new SessionType(v));
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
