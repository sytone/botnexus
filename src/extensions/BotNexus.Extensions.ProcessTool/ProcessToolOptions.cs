namespace BotNexus.Extensions.ProcessTool;

/// <summary>
/// Configuration for <see cref="ProcessTool"/>. Currently bounds the <c>output</c> action's
/// <c>tail</c> parameter so an agent (or a poisoned cron prompt) cannot pull the entire captured
/// ring buffer into a single tool result with an absurd line count.
/// </summary>
public sealed class ProcessToolOptions
{
    /// <summary>
    /// Maximum number of trailing lines the <c>output</c> action may return. Caller-supplied
    /// <c>tail</c> values above this ceiling are clamped. Defaults to 10,000 lines.
    /// </summary>
    public int MaxTail { get; set; } = 10_000;

    /// <summary>Shared default instance used when no options are injected.</summary>
    public static ProcessToolOptions Default { get; } = new();
}
