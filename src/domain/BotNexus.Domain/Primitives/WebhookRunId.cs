using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// Identifies a single webhook run (one inbound POST, one agent execution) inside
/// BotNexus. Construct via <see cref="From(string)"/>; value must be non-null,
/// non-empty, non-whitespace and is stored trimmed.
/// </summary>
[ValueObject<string>(conversions: Conversions.SystemTextJson)]
public readonly partial struct WebhookRunId
{
    private static Validation Validate(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Validation.Invalid("WebhookRunId cannot be null, empty, or whitespace.")
            : Validation.Ok;

    private static string NormalizeInput(string input) =>
        input is null ? input! : input.Trim();

    /// <summary>Creates a new, unique <see cref="WebhookRunId"/>.</summary>
    public static WebhookRunId Create() => From("whr_" + Guid.NewGuid().ToString("N"));
}
