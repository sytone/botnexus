using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// Identifies a webhook registration inside BotNexus. Construct via
/// <see cref="From(string)"/>; value must be non-null, non-empty, non-whitespace
/// and is stored trimmed.
/// </summary>
[ValueObject<string>(conversions: Conversions.SystemTextJson)]
public readonly partial struct WebhookId
{
    private static Validation Validate(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Validation.Invalid("WebhookId cannot be null, empty, or whitespace.")
            : Validation.Ok;

    private static string NormalizeInput(string input) =>
        input is null ? input! : input.Trim();

    /// <summary>Creates a new, unique <see cref="WebhookId"/> using a short random prefix.</summary>
    public static WebhookId Create() => From("wh_" + Guid.NewGuid().ToString("N")[..16]);
}
