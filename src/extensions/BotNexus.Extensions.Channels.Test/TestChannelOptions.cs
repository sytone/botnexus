namespace BotNexus.Extensions.Channels.Test;

/// <summary>
/// Options for the opt-in test channel adapter.
/// </summary>
/// <remarks>
/// Bound from the <c>channels:test</c> configuration section, mirroring the convention used by the
/// Telegram, Service Bus and Agent 365 channel extensions.
/// </remarks>
public sealed class TestChannelOptions
{
    /// <summary>
    /// The channel key this adapter presents itself as.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>test</c>. Setting it to an existing key (for example <c>telegram</c>) lets an
    /// integration test stand in for that channel without a real transport, which is the point of
    /// the extension: a multi-channel scenario needs a SECOND named channel, not a second portal
    /// client.
    /// </remarks>
    public string ChannelId { get; set; } = "test";

    /// <summary>Human-readable display name reported by the adapter.</summary>
    public string DisplayName { get; set; } = "Test Channel";

    /// <summary>
    /// Ring-buffer bound for captured log entries. The oldest entries are evicted once the bound
    /// is reached, so a long-running gateway cannot grow the buffer without limit.
    /// </summary>
    public int MaxCapturedLogEntries { get; set; } = 2000;
}
