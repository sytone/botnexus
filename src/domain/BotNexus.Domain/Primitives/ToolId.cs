using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// Identifies a user-defined portal tool (a named external URL launcher) inside the
/// BotNexus world. Construct via <see cref="From(string)"/>; the value must be non-null,
/// non-empty, non-whitespace and is stored trimmed.
/// </summary>
/// <remarks>
/// Tool ids are persisted as TEXT in the SQLite tools store and carried as strings over
/// the REST wire format - this value object guarantees a single canonical shape (trimmed,
/// non-empty) without changing the storage or network representation. Part of the portal
/// Tools foundation (#2232).
/// </remarks>
[ValueObject<string>(conversions: Conversions.SystemTextJson)]
public readonly partial struct ToolId
{
    private static Validation Validate(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Validation.Invalid("ToolId cannot be null, empty, or whitespace.")
            : Validation.Ok;

    private static string NormalizeInput(string input) =>
        input is null ? input! : input.Trim();
}
