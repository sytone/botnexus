using System.Globalization;
using System.Text.Json;

namespace BotNexus.Gateway.Contracts.Tools;

/// <summary>Declared timeout units; never infer a unit from an argument's spelling.</summary>
public enum ToolTimeoutUnit
{
    /// <summary>Whole seconds.</summary>
    Seconds,
    /// <summary>Whole milliseconds.</summary>
    Milliseconds
}

/// <summary>Explicit timeout declaration used to widen an executor's configured cancellation budget.</summary>
/// <param name="ArgumentName">Canonical caller timeout argument.</param>
/// <param name="Unit">Unit shared by the canonical argument and legacy alias.</param>
/// <param name="DeprecatedAliasName">Legacy name sharing the canonical unit; precedence is owned by the executor.</param>
/// <remarks>
/// The source declaration specifies absent-canonical fallback, but the current agent executor also
/// falls back when the canonical value is invalid. This record intentionally performs no key
/// selection; future adapters must preserve the executor's behavior rather than infer precedence.
/// </remarks>
public sealed record ToolTimeoutArgument(string ArgumentName, ToolTimeoutUnit Unit, string? DeprecatedAliasName = null)
{
    /// <summary>Converts a positive 32-bit integer number/string; absent, invalid, and nonpositive values return null.</summary>
    /// <remarks>Mirrors legacy integer parsing; no unit inference or schema mutation occurs here.</remarks>
    public TimeSpan? ToTimeSpan(JsonElement? rawValue)
    {
        if (rawValue is not { } raw || raw.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)
            || !int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var value)
            || value <= 0)
            return null;

        return Unit == ToolTimeoutUnit.Seconds
            ? TimeSpan.FromSeconds(value)
            : TimeSpan.FromMilliseconds(value);
    }
}
