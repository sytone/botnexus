using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using BotNexus.Domain.Serialization;

namespace BotNexus.Domain.Primitives;

[JsonConverter(typeof(SmartEnumJsonConverter<MessageRole>))]
public sealed class MessageRole : IEquatable<MessageRole>
{
    private static readonly ConcurrentDictionary<string, MessageRole> Registry = new(StringComparer.OrdinalIgnoreCase);

    public static readonly MessageRole User = Register("user");
    public static readonly MessageRole Assistant = Register("assistant");
    public static readonly MessageRole System = Register("system");
    public static readonly MessageRole Tool = Register("tool");

    public string Value { get; }

    private MessageRole(string value) => Value = value;

    public static MessageRole FromString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("MessageRole cannot be empty", nameof(value));

        return Registry.GetOrAdd(value.Trim().ToLowerInvariant(), static v => new MessageRole(v));
    }

    public static implicit operator MessageRole(string value) => FromString(value);
    public static implicit operator string(MessageRole role) => role.Value;

    public bool Equals(MessageRole? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is MessageRole other && Equals(other);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public override string ToString() => Value;

    private static MessageRole Register(string value)
    {
        var role = new MessageRole(value);
        Registry.TryAdd(value, role);
        return role;
    }
}
