using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// Identifies a user-defined conversation section - a personal grouping the user creates in the
/// portal sidebar to organise conversations beyond the built-in system sections (Pinned,
/// Conversations, Scheduled, Webhooks). Construct via <see cref="From(string)"/> for existing
/// values or <see cref="Create"/> for a new section. The value must be non-null, non-empty,
/// non-whitespace and is stored trimmed.
/// </summary>
/// <remarks>
/// <see cref="PrimitiveEqualityGeneration.Omit"/> mirrors <see cref="ConversationId"/>: the
/// auto-generated <c>operator ==(SectionId, string?)</c> overload is removed so callers compare
/// section ids against each other directly and never ambiguously against a raw string.
/// </remarks>
[ValueObject<string>(
    conversions: Conversions.SystemTextJson,
    primitiveEqualityGeneration: PrimitiveEqualityGeneration.Omit)]
public readonly partial struct SectionId
{
    /// <summary>
    /// Creates a new unique <see cref="SectionId"/> with the <c>sec_</c> prefix.
    /// </summary>
    public static SectionId Create() => From($"sec_{Guid.NewGuid():N}");

    private static Validation Validate(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Validation.Invalid("SectionId cannot be null, empty, or whitespace.")
            : Validation.Ok;

    private static string NormalizeInput(string input) =>
        input is null ? input! : input.Trim();
}
