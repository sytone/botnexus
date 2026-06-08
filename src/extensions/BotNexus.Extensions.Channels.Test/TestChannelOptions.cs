namespace BotNexus.Extensions.Channels.Test;

/// <summary>
/// Configuration options for the test channel adapter.
/// </summary>
public sealed class TestChannelOptions
{
    /// <summary>
    /// Optional display name override. Defaults to "Test Channel".
    /// </summary>
    public string DisplayName { get; set; } = "Test Channel";
}
