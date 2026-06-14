using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.SignalR;

namespace BotNexus.Gateway.Api;

/// <summary>
/// Applies explicit, intentional limits to the gateway's SignalR hub instead of relying on the
/// framework's implicit defaults.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddSignalR()</c> with no options leaves <see cref="HubOptions.MaximumReceiveMessageSize"/>
/// (32 KB), <c>MaximumParallelInvocationsPerClient</c> (1) and
/// <see cref="HubOptions.StreamBufferCapacity"/> (10) at framework defaults. None of those are
/// documented or guaranteed not to drift between framework versions, and the gateway carries
/// base64-encoded inline media through <c>SendMessageWithMedia</c> which exceeds the 32 KB
/// default. Setting the values here makes the security posture explicit: a generous-but-bounded
/// inbound frame cap and an explicit per-connection concurrency bound, both overridable via the
/// <see cref="SignalRConfig"/> section without ever becoming unbounded.
/// </para>
/// <para>
/// This lives as a standalone static method so the limit logic is unit-testable without spinning
/// up the web host.
/// </para>
/// </remarks>
public static class SignalRHubLimits
{
    /// <summary>
    /// Default maximum inbound hub frame size (10 MB). Comfortably accommodates base64-encoded
    /// inline media (~33% inflation over the raw bytes) while bounding a single frame so it cannot
    /// exhaust server memory.
    /// </summary>
    public const long DefaultMaximumReceiveMessageSizeBytes = 10L * 1024 * 1024;

    /// <summary>
    /// Default maximum parallel hub invocations per connection. Allows modest concurrency for a
    /// responsive portal while bounding how much work a single client can force concurrently.
    /// </summary>
    public const int DefaultMaximumParallelInvocationsPerClient = 10;

    /// <summary>
    /// Default upload-stream buffer capacity. Matches the framework default but is set explicitly
    /// so the intent is recorded.
    /// </summary>
    public const int DefaultStreamBufferCapacity = 10;

    /// <summary>
    /// Applies the configured (or default) hub limits to <paramref name="options"/>.
    /// Non-positive configured values are ignored in favour of the secure default so a misconfig
    /// can never disable the bound.
    /// </summary>
    /// <param name="options">The hub options to mutate.</param>
    /// <param name="config">Optional configured overrides; <see langword="null"/> applies defaults.</param>
    public static void Apply(HubOptions options, SignalRConfig? config)
    {
        ArgumentNullException.ThrowIfNull(options);

        var maxReceive = config?.MaximumReceiveMessageSizeBytes is { } size && size > 0
            ? size
            : DefaultMaximumReceiveMessageSizeBytes;

        var maxParallel = config?.MaximumParallelInvocationsPerClient is { } parallel && parallel > 0
            ? parallel
            : DefaultMaximumParallelInvocationsPerClient;

        var streamBuffer = config?.StreamBufferCapacity is { } buffer && buffer > 0
            ? buffer
            : DefaultStreamBufferCapacity;

        options.MaximumReceiveMessageSize = maxReceive;
        options.MaximumParallelInvocationsPerClient = maxParallel;
        options.StreamBufferCapacity = streamBuffer;
    }
}
