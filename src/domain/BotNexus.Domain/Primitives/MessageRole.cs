using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using BotNexus.Domain.Serialization;

namespace BotNexus.Domain.Primitives;

[JsonConverter(typeof(SmartEnumJsonConverter<MessageRole>))]
/// <summary>
/// Represents message role.
/// </summary>
public sealed class MessageRole : IEquatable<MessageRole>
{
    private static readonly ConcurrentDictionary<string, MessageRole> Registry = new(StringComparer.OrdinalIgnoreCase);

    public static readonly MessageRole User = Register("user");
    public static readonly MessageRole Assistant = Register("assistant");
    public static readonly MessageRole System = Register("system");
    public static readonly MessageRole Tool = Register("tool");

    /// <summary>Gateway-generated notification messages (e.g. restart interruption notices). Not forwarded to the LLM.</summary>
    public static readonly MessageRole Notification = Register("notification");

    /// <summary>
    /// Gets the value.
    /// </summary>
    public string Value { get; }

    private MessageRole(string value) => Value = value;

    /// <summary>
    /// Executes from string.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The from string result.</returns>
    public static MessageRole FromString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("MessageRole cannot be empty", nameof(value));

        return Registry.GetOrAdd(value.Trim().ToLowerInvariant(), static v => new MessageRole(v));
    }

    /// <summary>
    /// Performs the declared conversion or operator operation.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The operator message role result.</returns>
    public static implicit operator MessageRole(string value) => FromString(value);
    /// <summary>
    /// Performs the declared conversion or operator operation.
    /// </summary>
    /// <param name="role">The role.</param>
    /// <returns>The operator string result.</returns>
    public static implicit operator string(MessageRole role) => role.Value;

    /// <summary>
    /// Executes equals.
    /// </summary>
    /// <param name="other">The other.</param>
    /// <returns>The equals result.</returns>
    public bool Equals(MessageRole? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Executes equals.
    /// </summary>
    /// <param name="obj">The obj.</param>
    /// <returns>The equals result.</returns>
    public override bool Equals(object? obj) => obj is MessageRole other && Equals(other);
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

    private static MessageRole Register(string value)
    {
        var role = new MessageRole(value);
        Registry.TryAdd(value, role);
        return role;
    }
}
