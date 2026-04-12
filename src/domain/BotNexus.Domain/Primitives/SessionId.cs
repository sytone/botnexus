using System.Text.Json.Serialization;
using BotNexus.Domain.Serialization;

namespace BotNexus.Domain.Primitives;

[JsonConverter(typeof(SessionIdJsonConverter))]
public readonly record struct SessionId(string Value) : IComparable<SessionId>
{
    public static SessionId From(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("SessionId cannot be empty", nameof(value))
            : new(value.Trim());

    public static SessionId Create() => new(Guid.NewGuid().ToString("N"));

    public static SessionId ForSubAgent(string parentId, string uniqueId)
    {
        var parentSessionId = From(parentId);
        if (string.IsNullOrWhiteSpace(uniqueId))
            throw new ArgumentException("Sub-agent unique ID cannot be empty", nameof(uniqueId));

        return new($"{parentSessionId.Value}::subagent::{uniqueId.Trim()}");
    }

    public static SessionId ForSubAgent(SessionId parentId, string uniqueId)
        => ForSubAgent(parentId.Value, uniqueId);

    public static SessionId ForCrossAgent(string sourceId, string targetId)
    {
        var sourceSessionId = From(sourceId);
        var targetSessionId = From(targetId);
        return new($"xagent::{sourceSessionId.Value}::{targetSessionId.Value}");
    }

    public bool IsSubAgent => Value.Contains("::subagent::", StringComparison.OrdinalIgnoreCase);

    public static implicit operator string(SessionId id) => id.Value;
    public static implicit operator SessionId(string value) => From(value);

    public override string ToString() => Value;
    public int CompareTo(SessionId other) => string.Compare(Value, other.Value, StringComparison.Ordinal);
}
