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

    /// <summary>Show the debug inspector panel entry point in the main layout. Default: false.</summary>
    public bool DebugModeEnabled { get; set; } = false;

    /// <summary>Prompt for confirmation before archiving/closing a conversation. Default: true.</summary>
    public bool ArchiveConfirmEnabled { get; set; } = true;
}
