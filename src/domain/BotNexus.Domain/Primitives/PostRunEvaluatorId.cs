using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>Stable, case-insensitive identifier for a post-run evaluator.</summary>
[ValueObject<string>(conversions: Conversions.SystemTextJson)]
public readonly partial struct PostRunEvaluatorId
{
    private static Validation Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Validation.Invalid("PostRunEvaluatorId cannot be null, empty, or whitespace.");

        if (value.Length > 64)
            return Validation.Invalid("PostRunEvaluatorId cannot exceed 64 characters.");

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.')
            {
                return Validation.Invalid(
                    "PostRunEvaluatorId can contain only ASCII letters, digits, hyphens, underscores, and periods.");
            }
        }

        return Validation.Ok;
    }

    private static string NormalizeInput(string input) =>
        input is null ? input! : input.Trim().ToLowerInvariant();
}
