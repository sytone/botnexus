using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using BotNexus.Domain.Serialization;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// Orthogonal, typed presentation/delivery kind for a message or transcript entry (issue #2149).
/// It is deliberately independent of <see cref="MessageRole"/>: <see cref="MessageRole"/> stays the
/// LLM/conversation role (<c>user</c>/<c>assistant</c>/...), while <see cref="MessageKind"/> lets a
/// channel adapter distinguish an ordinary direct response from a sub-agent completion notification
/// and from the parent agent's response produced while handling that completion. The kind survives
/// the whole path (InboundMessage -> SessionEntry -> session store -> history/conversation DTOs ->
/// OutboundMessage / conversation event -> channel adapter) so replay and live delivery agree, and
/// no channel has to parse role, sender ids, session ids, or message text to recover the distinction.
/// </summary>
[JsonConverter(typeof(SmartEnumJsonConverter<MessageKind>))]
public sealed class MessageKind : IEquatable<MessageKind>
{
    private static readonly ConcurrentDictionary<string, MessageKind> Registry = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ordinary direct response / default. Legacy rows and messages with no explicit kind resolve here.</summary>
    public static readonly MessageKind Message = Register("message");

    /// <summary>The internal sub-agent completion notification delivered to the parent session.</summary>
    public static readonly MessageKind SubAgentCompletion = Register("subagent-completion");

    /// <summary>The parent agent's response produced while handling a sub-agent completion.</summary>
    public static readonly MessageKind SubAgentResponse = Register("subagent-response");

    /// <summary>Gets the stable wire value of this kind.</summary>
    public string Value { get; }

    private MessageKind(string value) => Value = value;

    /// <summary>
    /// Resolves a <see cref="MessageKind"/> from its wire value, registering previously unseen values
    /// so forward-compatible kinds round-trip through persistence without a schema change. Trims and
    /// lower-cases so the token is canonical.
    /// </summary>
    /// <param name="value">The wire value (e.g. <c>message</c>, <c>subagent-completion</c>).</param>
    /// <returns>The corresponding kind.</returns>
    public static MessageKind FromString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("MessageKind cannot be empty", nameof(value));

        return Registry.GetOrAdd(value.Trim().ToLowerInvariant(), static v => new MessageKind(v));
    }

    /// <summary>
    /// Resolves a kind from an optional wire value, mapping <c>null</c>/blank (a legacy or unstamped
    /// row) to <see cref="Message"/>. This is the single place the "default safely to message" rule
    /// lives so every read path (session store, DTO projection) agrees.
    /// </summary>
    /// <param name="value">The optional wire value; <c>null</c> or blank yields <see cref="Message"/>.</param>
    /// <returns>The resolved kind, never <c>null</c>.</returns>
    public static MessageKind FromNullableString(string? value)
        => string.IsNullOrWhiteSpace(value) ? Message : FromString(value);

    /// <summary>Performs the declared conversion or operator operation.</summary>
    public static implicit operator MessageKind(string value) => FromString(value);

    /// <summary>Performs the declared conversion or operator operation.</summary>
    public static implicit operator string(MessageKind kind) => kind.Value;

    /// <inheritdoc/>
    public bool Equals(MessageKind? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is MessageKind other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    /// <inheritdoc/>
    public override string ToString() => Value;

    private static MessageKind Register(string value)
    {
        var kind = new MessageKind(value);
        Registry.TryAdd(value, kind);
        return kind;
    }
}
