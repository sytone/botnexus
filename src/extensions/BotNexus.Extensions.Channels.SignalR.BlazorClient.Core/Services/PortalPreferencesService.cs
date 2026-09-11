using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.Abstractions;
using Microsoft.JSInterop;
using System.Text.Json;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// localStorage-backed portal preferences service.
/// </summary>
public sealed class PortalPreferencesService : IPortalPreferencesService
{
    private const string StorageKey = "botnexus.portal.prefs";
    private readonly IJSRuntime _js;
    private PortalPreferences _current = new();

    public PortalPreferencesService(IJSRuntime js) => _js = js;

    /// <inheritdoc/>
    public PortalPreferences Current => _current;

    /// <inheritdoc/>
    public event Action OnChanged = delegate { };

    /// <inheritdoc/>
    public async Task LoadAsync()
    {
        try
        {
            var json = await _js.InvokeAsync<string>("portalPrefs.load", StorageKey);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var loaded = JsonSerializer.Deserialize<PortalPreferences>(json);
                if (loaded is not null)
                {
                    // A hand-edited or stale localStorage entry must never leak an unknown density
                    // token into the DOM, so normalise on the way in as well as on the way out.
                    loaded.Density = PortalDensity.Normalize(loaded.Density);
                    loaded.Theme = PortalTheme.Normalize(loaded.Theme);
                    _current = loaded;
                }
            }
        }
        catch
        {
            // Malformed JSON or JS interop failure — silently fall back to defaults
            _current = new PortalPreferences();
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(_current);
        await _js.InvokeAsync<object>("portalPrefs.save", StorageKey, json);
    }

    /// <inheritdoc/>
    public async Task SetExpandingInputAsync(bool enabled)
    {
        _current.ExpandingInput = enabled;
        await SaveAsync();
        OnChanged.Invoke();
    }

    /// <inheritdoc/>
    public async Task SetDebugModeAsync(bool enabled)
    {
        _current.DebugModeEnabled = enabled;
        await SaveAsync();
        OnChanged.Invoke();
    }

    /// <inheritdoc/>
    public async Task SetArchiveConfirmAsync(bool enabled)
    {
        _current.ArchiveConfirmEnabled = enabled;
        await SaveAsync();
        OnChanged.Invoke();
    }

    /// <inheritdoc/>
    public async Task SetDensityAsync(string density)
    {
        _current.Density = PortalDensity.Normalize(density);
        await SaveAsync();
        OnChanged.Invoke();
    }

    /// <inheritdoc/>
    public async Task SetThemeAsync(string theme)
    {
        _current.Theme = PortalTheme.Normalize(theme);
        // Applied to the DOM before the preference is persisted so the swap is instant even if
        // localStorage is unavailable (private browsing, quota) - the theme still changes for the
        // session rather than appearing to do nothing.
        await _js.InvokeVoidAsync("portalPrefs.applyTheme", _current.Theme);
        await SaveAsync();
        OnChanged.Invoke();
    }
}
