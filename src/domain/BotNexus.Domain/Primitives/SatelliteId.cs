using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// Identifies a satellite node in a BotNexus world. Construct via <see cref="From(string)"/>;
/// the value must be non-null, non-empty, non-whitespace and is stored trimmed.
/// </summary>
[ValueObject<string>(conversions: Conversions.SystemTextJson)]
public readonly partial struct SatelliteId
{
    private static Validation Validate(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Validation.Invalid("SatelliteId cannot be null, empty, or whitespace.")
            : Validation.Ok;

    private static string NormalizeInput(string input) =>
        input is null ? input! : input.Trim();
}
