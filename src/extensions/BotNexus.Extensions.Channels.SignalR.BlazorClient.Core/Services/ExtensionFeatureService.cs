namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Tracks whether optional BotNexus extensions are loaded.
/// Populated once the portal is ready via <see cref="LoadAsync"/>.
/// </summary>
public sealed class ExtensionFeatureService
{
    private readonly IGatewayRestClient _restClient;
    private bool _skillsEnabled;

    public ExtensionFeatureService(IGatewayRestClient restClient)
    {
        _restClient = restClient;
    }

    /// <summary>Whether the botnexus-skills extension is loaded and enabled.</summary>
    public bool SkillsEnabled => _skillsEnabled;

    /// <summary>Raised when extension feature state changes.</summary>
    public event Action? OnChanged;

    /// <summary>Fetches the extension list from the gateway and updates feature flags.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var extensions = await _restClient.GetExtensionDetailsAsync(ct);
            _skillsEnabled = extensions.Any(e =>
                string.Equals(e.Id, "botnexus-skills", StringComparison.OrdinalIgnoreCase) && e.Enabled);
            OnChanged?.Invoke();
        }
        catch
        {
            // Non-fatal — leave flags at defaults
        }
    }
}
