namespace BotNexus.Extensions.Plugins;

/// <summary>
/// A single reason a plugin document was rejected. <see cref="Field"/> is carried separately
/// from <see cref="Message"/> so callers can group or filter by field without parsing prose;
/// the manifest contract deliberately rejects rather than coerces an unknown shape, so this
/// is the only channel through which a user learns which field to fix.
/// </summary>
/// <param name="Field">JSON path of the offending field, e.g. <c>#/name</c>.</param>
/// <param name="Message">Human readable description naming the offending field.</param>
public sealed record PluginValidationError(string Field, string Message)
{
    /// <summary>Renders the error as a single diagnostic line for logs and CLI output.</summary>
    public override string ToString() => Message;
}

/// <summary>
/// Outcome of parsing a plugin document. Success and failure are represented in one value
/// rather than by exceptions because a caller scanning a plugin directory needs to report
/// every bad manifest it finds, not abort on the first one.
/// </summary>
/// <typeparam name="T">The typed document produced on success.</typeparam>
public sealed class PluginParseResult<T>
    where T : class
{
    private PluginParseResult(T? value, IReadOnlyList<PluginValidationError> errors)
    {
        Value = value;
        Errors = errors;
    }

    /// <summary>The parsed document, or <c>null</c> when <see cref="IsValid"/> is <c>false</c>.</summary>
    public T? Value { get; }

    /// <summary>Every validation failure found; empty on success.</summary>
    public IReadOnlyList<PluginValidationError> Errors { get; }

    /// <summary>True when the document satisfied the schema and a typed value is available.</summary>
    public bool IsValid => Value is not null;

    /// <summary>Creates a successful result carrying the parsed document.</summary>
    public static PluginParseResult<T> Success(T value) => new(value, []);

    /// <summary>Creates a failed result carrying at least one field-naming error.</summary>
    public static PluginParseResult<T> Failure(IReadOnlyList<PluginValidationError> errors) => new(null, errors);

    /// <summary>Creates a failed result from a single field-naming error.</summary>
    public static PluginParseResult<T> Failure(string field, string message) =>
        new(null, [new PluginValidationError(field, message)]);
}
