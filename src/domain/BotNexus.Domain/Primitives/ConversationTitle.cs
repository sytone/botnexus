using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// The human-readable label of a conversation, as shown in the portal sidebar and channel headers.
/// Construct via <see cref="From(string)"/>; the value must be non-empty after trimming and no
/// longer than <see cref="MaxLength"/> characters, and is stored trimmed.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="MaxLength"/> is the single source of the limit.</b> Before #502 the number 200
/// lived only inside <c>ConversationInputValidator</c>, so any other writer of a title - a factory,
/// an auto-titler, an importer - could exceed it without anything noticing. The validator now reads
/// the limit from this type, which keeps the REST error message and the domain invariant provably
/// in step instead of coincidentally equal.
/// </para>
/// <para>
/// <b>Blank is rejected, not defaulted.</b> Callers that want a placeholder for an absent title
/// (<c>"General"</c>, <c>"New conversation"</c>) choose it deliberately in
/// <c>ConversationFactory</c>. Silently substituting one here would let an empty title flow through
/// the system and reappear as a default at an arbitrary later point, which is exactly the class of
/// bug strong typing exists to prevent.
/// </para>
/// <para>Introduced by #502 (primitive obsession phase 3).</para>
/// </remarks>
[ValueObject<string>(conversions: Conversions.SystemTextJson)]
public readonly partial struct ConversationTitle
{
    /// <summary>
    /// Maximum accepted title length, measured after trimming. Matches the limit the REST surface
    /// has always enforced; <c>ConversationInputValidator.MaxTitleLength</c> now derives from it.
    /// </summary>
    public const int MaxLength = 200;

    private static Validation Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Validation.Invalid("ConversationTitle cannot be null, empty, or whitespace.");
        }

        var trimmed = value.Trim();

        return trimmed.Length > MaxLength
            ? Validation.Invalid(
                $"ConversationTitle must be {MaxLength} characters or fewer (was {trimmed.Length}).")
            : Validation.Ok;
    }

    private static string NormalizeInput(string input) =>
        input is null ? input! : input.Trim();
}
