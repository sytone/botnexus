using System.Text.Json.Serialization;
using BotNexus.Domain.Serialization;

namespace BotNexus.Domain.Primitives;

[JsonConverter(typeof(AgentIdJsonConverter))]
public readonly record struct AgentId(string Value) : IComparable<AgentId>
{
    public static AgentId From(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("AgentId cannot be empty", nameof(value))
            : new(value.Trim());

    public static implicit operator string(AgentId id) => id.Value;
    public static implicit operator AgentId(string value) => From(value);

    public override string ToString() => Value;
    public int CompareTo(AgentId other) => string.Compare(Value, other.Value, StringComparison.Ordinal);
}
