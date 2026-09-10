namespace BotNexus.Agent.Providers.Core;

/// <summary>
/// Wire limits for Anthropic-shaped <c>cache_control</c> markers.
/// </summary>
/// <remarks>
/// Shared by the Anthropic provider and the Copilot Messages provider, which speaks the same wire
/// format and is held byte-identical to it by a parity test. The ceiling lives here rather than as
/// a constant in each builder because exceeding it fails the whole request: two copies of the
/// number are two chances for one of them to drift.
/// </remarks>
public static class CacheBreakpoints
{
    /// <summary>
    /// Maximum number of <c>cache_control</c> markers accepted in one request.
    /// </summary>
    public const int Max = 4;
}
