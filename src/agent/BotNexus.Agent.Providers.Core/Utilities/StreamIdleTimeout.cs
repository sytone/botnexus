namespace BotNexus.Agent.Providers.Core.Utilities;

/// <summary>
/// Resolves the inter-chunk idle deadline used by streaming response-body guards.
/// </summary>
public static class StreamIdleTimeout
{
    /// <summary>
    /// Resolves a stream option to a timeout. Null uses the platform default, zero disables the
    /// deadline, and a positive value overrides the default.
    /// </summary>
    public static TimeSpan Resolve(StreamOptions? options)
        => options?.StreamIdleTimeoutMs switch
        {
            null => BoundedHttpContent.DefaultIdleChunkTimeout,
            0 => Timeout.InfiniteTimeSpan,
            int milliseconds when milliseconds > 0 => TimeSpan.FromMilliseconds(milliseconds),
            _ => throw new ArgumentOutOfRangeException(
                nameof(options),
                options?.StreamIdleTimeoutMs,
                "Stream idle timeout must be zero (disabled) or positive.")
        };
}
