namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.Abstractions;

/// <summary>
/// Manages browser-local portal preferences backed by localStorage.
/// </summary>
public interface IPortalPreferencesService
{
    /// <summary>Current preferences snapshot.</summary>
    PortalPreferences Current { get; }

    /// <summary>Raised when any preference changes.</summary>
    event Action OnChanged;

    /// <summary>Load preferences from localStorage. Falls back to defaults on missing or invalid data.</summary>
    Task LoadAsync();

    /// <summary>Persist current preferences to localStorage.</summary>
    Task SaveAsync();

    /// <summary>Update the expanding-input preference and persist.</summary>
    Task SetExpandingInputAsync(bool enabled);
}
