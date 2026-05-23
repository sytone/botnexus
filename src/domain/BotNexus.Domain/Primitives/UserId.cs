using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// Identifies a user (human citizen) inside a BotNexus world. Construct via
/// <see cref="From(string)"/>; the value must be non-null, non-empty, non-whitespace
/// and is stored trimmed.
/// </summary>
/// <remarks>
/// The validation floor is intentionally permissive (non-empty after trim). Sender
/// strings are channel-specific — Telegram numeric ids, TUI <see cref="Environment.UserName"/>,
/// composite <c>channel:address</c> tokens — and a stricter rule would block legitimate
/// channels before a real user registry exists (Phase 2). Stricter shape rules belong on
/// channel-specific user identity types, not on the canonical <c>UserId</c>.
/// </remarks>
[ValueObject<string>(conversions: Conversions.SystemTextJson)]
public readonly partial struct UserId
{
    private static Validation Validate(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Validation.Invalid("UserId cannot be null, empty, or whitespace.")
            : Validation.Ok;

    private static string NormalizeInput(string input) =>
        input is null ? input! : input.Trim();
}
