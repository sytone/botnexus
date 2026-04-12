using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using BotNexus.Domain.Serialization;

namespace BotNexus.Domain.Primitives;

[JsonConverter(typeof(SmartEnumJsonConverter<SubAgentArchetype>))]
public sealed class SubAgentArchetype : IEquatable<SubAgentArchetype>
{
    private static readonly ConcurrentDictionary<string, SubAgentArchetype> Registry = new(StringComparer.OrdinalIgnoreCase);

    public static readonly SubAgentArchetype Researcher = Register("researcher");
    public static readonly SubAgentArchetype Coder = Register("coder");
    public static readonly SubAgentArchetype Planner = Register("planner");
    public static readonly SubAgentArchetype Reviewer = Register("reviewer");
    public static readonly SubAgentArchetype Writer = Register("writer");
    public static readonly SubAgentArchetype General = Register("general");

    public string Value { get; }

    private SubAgentArchetype(string value) => Value = value;

    public static SubAgentArchetype FromString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("SubAgentArchetype cannot be empty", nameof(value));

        return Registry.GetOrAdd(value.Trim().ToLowerInvariant(), static v => new SubAgentArchetype(v));
    }

    public static implicit operator SubAgentArchetype(string value) => FromString(value);
    public static implicit operator string(SubAgentArchetype archetype) => archetype.Value;

    public bool Equals(SubAgentArchetype? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is SubAgentArchetype other && Equals(other);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public override string ToString() => Value;

    private static SubAgentArchetype Register(string value)
    {
        var archetype = new SubAgentArchetype(value);
        Registry.TryAdd(value, archetype);
        return archetype;
    }
}
