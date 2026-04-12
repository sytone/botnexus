namespace BotNexus.Domain.World;

using BotNexus.Domain.Serialization;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;

[JsonConverter(typeof(SmartEnumJsonConverter<LocationType>))]
public sealed class LocationType : IEquatable<LocationType>
{
    private static readonly ConcurrentDictionary<string, LocationType> Registry = new(StringComparer.OrdinalIgnoreCase);

    public static readonly LocationType FileSystem = Register("filesystem");
    public static readonly LocationType Api = Register("api");
    public static readonly LocationType McpServer = Register("mcp-server");
    public static readonly LocationType RemoteNode = Register("remote-node");
    public static readonly LocationType Database = Register("database");

    public string Value { get; }

    private LocationType(string value) => Value = value;

    public static LocationType FromString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("LocationType cannot be empty", nameof(value));

        return Registry.GetOrAdd(value.Trim().ToLowerInvariant(), static v => new LocationType(v));
    }

    public static implicit operator string(LocationType type) => type.Value;

    public bool Equals(LocationType? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is LocationType other && Equals(other);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public override string ToString() => Value;

    private static LocationType Register(string value)
    {
        var type = new LocationType(value);
        Registry.TryAdd(value, type);
        return type;
    }
}
