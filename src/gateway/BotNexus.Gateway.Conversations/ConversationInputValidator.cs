namespace BotNexus.Gateway.Conversations;

/// <summary>
/// Validates conversation input fields (title, purpose, instructions) against length constraints.
/// Shared by both the REST controller and the agent tool to enforce consistent limits.
/// </summary>
public static class ConversationInputValidator
{
    /// <summary>Maximum allowed length for conversation title after trimming.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Maximum allowed length for conversation purpose after trimming.</summary>
    public const int MaxPurposeLength = 1_000;

    /// <summary>Maximum allowed length for conversation instructions after trimming.</summary>
    public const int MaxInstructionsLength = 10_000;

    /// <summary>
    /// Validates a title value. Returns null if valid, or an error message if invalid.
    /// </summary>
    /// <param name="title">The title to validate (may be null).</param>
    /// <param name="required">Whether the title is required (non-empty after trim).</param>
    /// <returns>Error message or null if valid.</returns>
    public static string? ValidateTitle(string? title, bool required = false)
    {
        if (title is null)
            return required ? "title is required." : null;

        var trimmed = title.Trim();
        if (required && trimmed.Length == 0)
            return "title must not be empty.";

        if (trimmed.Length > MaxTitleLength)
            return $"title must be {MaxTitleLength} characters or fewer (was {trimmed.Length}).";

        return null;
    }

    /// <summary>
    /// Validates a purpose value. Returns null if valid, or an error message if invalid.
    /// </summary>
    public static string? ValidatePurpose(string? purpose)
    {
        if (purpose is null)
            return null;

        var trimmed = purpose.Trim();
        if (trimmed.Length > MaxPurposeLength)
            return $"purpose must be {MaxPurposeLength} characters or fewer (was {trimmed.Length}).";

        return null;
    }

    /// <summary>
    /// Validates an instructions value. Returns null if valid, or an error message if invalid.
    /// </summary>
    public static string? ValidateInstructions(string? instructions)
    {
        if (instructions is null)
            return null;

        var trimmed = instructions.Trim();
        if (trimmed.Length > MaxInstructionsLength)
            return $"instructions must be {MaxInstructionsLength} characters or fewer (was {trimmed.Length}).";

        return null;
    }
}
