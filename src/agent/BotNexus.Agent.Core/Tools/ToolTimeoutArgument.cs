namespace BotNexus.Agent.Core.Tools;

/// <summary>
/// The unit a tool's timeout argument is expressed in.
/// </summary>
/// <remarks>
/// Exists because the argument <c>timeout</c> historically meant seconds in
/// <c>ShellTool</c>/<c>FileWatcherTool</c> but milliseconds in <c>ProcessTool</c>, and the
/// executor inferred the unit from the bare argument name. That inference silently inflated a
/// millisecond budget by 1000x (see issue #2955). The unit is now declared by the tool that owns
/// the argument, so a disagreement is a registration-time concern instead of a silent error.
/// </remarks>
public enum ToolTimeoutUnit
{
    /// <summary>The argument value is a whole number of seconds.</summary>
    Seconds,

    /// <summary>The argument value is a whole number of milliseconds.</summary>
    Milliseconds
}

/// <summary>
/// A tool's declaration of which invocation argument carries a caller-requested timeout, and in
/// which unit that argument is expressed.
/// </summary>
/// <remarks>
/// <para>
/// <c>ToolExecutor</c> uses this to widen its per-tool cancellation budget when an agent explicitly
/// asks for a longer run. A tool that returns <c>null</c> from
/// <see cref="IAgentTool.TimeoutArgument"/> opts out entirely: the executor will not inspect any
/// argument, so it can never misread a unit it was not told about.
/// </para>
/// <para>
/// This is deliberately a declaration rather than a naming convention. Names are not a safe
/// carrier for units across an open tool surface — extensions ship their own schemas and cannot be
/// forced to agree on a spelling.
/// </para>
/// </remarks>
/// <param name="ArgumentName">
/// The canonical argument name, which should carry its unit (for example <c>timeoutMs</c>).
/// </param>
/// <param name="Unit">The unit <paramref name="ArgumentName"/> is expressed in.</param>
/// <param name="DeprecatedAliasName">
/// An optional legacy argument name that is still accepted with the same <paramref name="Unit"/>
/// semantics. Used only when the canonical argument is absent from the call.
/// </param>
public sealed record ToolTimeoutArgument(
    string ArgumentName,
    ToolTimeoutUnit Unit,
    string? DeprecatedAliasName = null)
{
    /// <summary>
    /// Converts a raw argument value to a <see cref="TimeSpan"/> using the declared unit, or
    /// <c>null</c> when the value is absent, unparseable, or non-positive.
    /// </summary>
    public TimeSpan? ToTimeSpan(object? rawValue)
    {
        if (rawValue is null || !int.TryParse(rawValue.ToString(), out var value) || value <= 0)
        {
            return null;
        }

        return Unit == ToolTimeoutUnit.Seconds
            ? TimeSpan.FromSeconds(value)
            : TimeSpan.FromMilliseconds(value);
    }
}
