namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Browser-local portal preferences. Stored in localStorage. Not synced to server.
/// </summary>
public sealed class PortalPreferences
{
    /// <summary>Auto-expand the chat textarea as the user types.</summary>
    public bool ExpandingInput { get; set; } = true;

    /// <summary>Maximum visible rows before the textarea scrolls internally.</summary>
    public int ExpandingInputMaxLines { get; set; } = 8;
}
