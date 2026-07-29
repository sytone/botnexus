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

    /// <summary>
    /// UI density preset driving the <c>--density-*</c> CSS custom properties (#2441). The portal
    /// ships a tight <see cref="PortalDensity.Compact"/> default and a roomier
    /// <see cref="PortalDensity.Comfortable"/> alternative; the value is emitted as the
    /// <c>data-density</c> attribute on the app shell so every spacing token switches together.
    /// </summary>
    public string Density { get; set; } = PortalDensity.Compact;
}

/// <summary>
/// Canonical density preset identifiers. These are the only legal values of
/// <see cref="PortalPreferences.Density"/> and map 1:1 to the <c>[data-density="..."]</c> token
/// blocks in <c>app.css</c>.
/// </summary>
public static class PortalDensity
{
    /// <summary>Tight spacing preset. The portal default.</summary>
    public const string Compact = "compact";

    /// <summary>Roomier spacing preset for large displays or accessibility needs.</summary>
    public const string Comfortable = "comfortable";

    /// <summary>
    /// Coerces arbitrary stored input to a legal preset so a corrupted or hand-edited preference
    /// can never emit an unknown <c>data-density</c> value into the DOM.
    /// </summary>
    /// <param name="value">Raw preference value; may be null, blank, or unrecognised.</param>
    /// <returns><see cref="Comfortable"/> when explicitly requested, otherwise <see cref="Compact"/>.</returns>
    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Comfortable, StringComparison.OrdinalIgnoreCase)
            ? Comfortable
            : Compact;
}
