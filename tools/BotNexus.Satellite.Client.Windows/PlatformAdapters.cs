using System.Diagnostics;
using System.Text.Json;
using BotNexus.Satellite.Client.Core;
using BotNexus.Satellite.Client.SignalR;

namespace BotNexus.Satellite.Client.Windows;

internal sealed class SettingsStore
{
    // Intentionally independent of BOTNEXUS_HOME and gateway configuration.
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotNexus.Satellite.Client", "settings.json");
    public ClientProfile? Load()
    {
        if (!File.Exists(_path)) return null;
        var profile = JsonSerializer.Deserialize<ClientProfile>(File.ReadAllText(_path));
        profile?.Validate();
        return profile;
    }
    public void Save(ClientProfile profile)
    {
        profile.Validate();
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Settings location is unavailable.");
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class BrowserLauncher(ClientProfile profile) : IBrowserLauncher
{
    public Task OpenAsync(Uri origin, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = profile.ToOptions();
        _ = options.GetHubUri();
        if (options.TunnelBrowserOrigin is not { } configured || new Uri(configured) != origin)
            throw new ArgumentException("Only the configured matching HTTPS tunnel origin can be opened.");
        using var process = Process.Start(new ProcessStartInfo(origin.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}

internal sealed class ClientDelay : IClientDelay
{
    public Task DelayAsync(TimeSpan duration, TimeProvider clock, CancellationToken cancellationToken) => Task.Delay(duration, clock, cancellationToken);
}
