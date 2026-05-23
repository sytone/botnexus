using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// Identifies a conversation -- the durable container the citizen (user or agent) thinks
/// they are in. Construct via <see cref="From(string)"/> for existing values, or
/// <see cref="Create"/> for a new conversation. The value must be non-null, non-empty,
/// non-whitespace and is stored trimmed.
/// </summary>
[ValueObject<string>(conversions: Conversions.SystemTextJson)]
public readonly partial struct ConversationId
{
    /// <summary>
    /// Creates a new unique <see cref="ConversationId"/> with the <c>c_</c> prefix.
    /// </summary>
    public static ConversationId Create() => From($"c_{Guid.NewGuid():N}");

    private static Validation Validate(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Validation.Invalid("ConversationId cannot be null, empty, or whitespace.")
            : Validation.Ok;

    private static string NormalizeInput(string input) =>
        input is null ? input! : input.Trim();
}
