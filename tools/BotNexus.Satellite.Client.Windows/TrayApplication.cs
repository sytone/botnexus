using BotNexus.Satellite.Client.Core;
using BotNexus.Satellite.Client.SignalR;

namespace BotNexus.Satellite.Client.Windows;

internal sealed class TrayApplication : ApplicationContext
{
    private readonly Control _dispatcher = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _status = new("Offline") { Enabled = false };
    private readonly ToolStripMenuItem _connect = new("Connect");
    private readonly ToolStripMenuItem _stop = new("Stop");
    private readonly ToolStripMenuItem _settings = new("Settings…");
    private readonly ToolStripMenuItem _signin = new("Sign in to configured tunnel… (token still required)");
    private readonly ToolStripMenuItem _exit = new("Exit");
    private ClientProfile? _profile;
    private SatelliteClient? _client;
    private Task? _runner;
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private bool _exiting;
    private ClientState _state;

    public TrayApplication()
    {
        _ = _dispatcher.Handle;
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([_status, new ToolStripSeparator(), _connect, _stop, _settings, _signin, new ToolStripSeparator(), _exit]);
        _tray = new NotifyIcon { Visible = true, Icon = SystemIcons.Application, Text = "BotNexus satellite: Offline", ContextMenuStrip = menu };
        _connect.Click += async (_, _) => await ActionAsync(ConnectAsync);
        _stop.Click += async (_, _) => await ActionAsync(StopAsync);
        _settings.Click += async (_, _) => await ActionAsync(SettingsAsync);
        _signin.Click += async (_, _) => await ActionAsync(SignInAsync);
        _exit.Click += async (_, _) => await ExitAsync();
        _tray.DoubleClick += async (_, _) => await ActionAsync(SettingsAsync);
        try { _profile = new SettingsStore().Load(); }
        catch { MessageBox.Show("Satellite settings could not be loaded. Open Settings to create a valid profile.", "BotNexus satellite", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        UpdateState(ClientState.Offline);
        _dispatcher.BeginInvoke(async () => await ActionAsync(InitializeAsync));
    }

    private async Task InitializeAsync()
    {
        if (_profile is null) await SettingsAsync();
        if (_profile?.ConnectOnLaunch == true) await ConnectAsync();
    }
    private async Task ActionAsync(Func<Task> action)
    {
        if (_busy || _exiting) return;
        _busy = true; RefreshMenu();
        try { await action(); }
        catch { MessageBox.Show("The satellite action could not be completed. Check Settings and protected credentials.", "BotNexus satellite", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { _busy = false; RefreshMenu(); }
    }
    private async Task ConnectAsync()
    {
        if (_profile is null) { await SettingsAsync(); if (_profile is null) return; }
        if (_runner is { IsCompleted: false }) return;
        await StopAsync();
        var profile = _profile;
        if (profile is null) return;
        _cancellation = new CancellationTokenSource();
        _client = new SatelliteClient(profile.ToOptions(), new WindowsCredentialStore(profile), new SignalRSatelliteTransport(), new BrowserLauncher(profile), TimeProvider.System, new ClientDelay(), OnStateChanged);
        _runner = RunAsync(_client, _cancellation.Token);
    }
    private async Task RunAsync(SatelliteClient client, CancellationToken token)
    {
        try { await client.RunAsync(token); }
        catch { OnStateChanged(ClientState.Offline); }
    }
    private async Task StopAsync()
    {
        _cancellation?.Cancel();
        if (_runner is { } runner) await runner;
        _runner = null;
        _client = null;
        _cancellation?.Dispose();
        _cancellation = null;
        UpdateState(ClientState.Offline);
    }
    private async Task SettingsAsync()
    {
        await StopAsync();
        using var dialog = new SettingsDialog(_profile);
        if (dialog.ShowDialog() == DialogResult.OK) _profile = dialog.Profile;
        RefreshMenu();
    }
    private Task SignInAsync()
    {
        if (_profile is not { UseTunnelAuthentication: true } profile) return Task.CompletedTask;
        MessageBox.Show("The browser opens only your configured tunnel origin. Browser sign-in alone does not authenticate this client. Enter a devtunnels client access token in Settings, then select Connect.", "Tunnel authentication", MessageBoxButtons.OK, MessageBoxIcon.Information);
        var browser = new BrowserLauncher(profile);
        return browser.OpenAsync(new Uri(profile.GatewayEndpoint), CancellationToken.None);
    }
    private void OnStateChanged(ClientState state)
    {
        if (_exiting || _dispatcher.IsDisposed) return;
        if (_dispatcher.InvokeRequired) _dispatcher.BeginInvoke(() => UpdateState(state));
        else UpdateState(state);
    }
    private void UpdateState(ClientState state)
    {
        _state = state;
        var label = state == ClientState.NeedsAuthentication ? "Needs authentication — enter API key / tunnel token in Settings" : state.ToString();
        _status.Text = label;
        _tray.Text = "BotNexus satellite: " + (state == ClientState.NeedsAuthentication ? "Needs authentication" : state.ToString());
        _tray.Icon = state switch
        {
            ClientState.Online => SystemIcons.Information,
            ClientState.Connecting or ClientState.Reconnecting => SystemIcons.Warning,
            ClientState.NeedsAuthentication => SystemIcons.Shield,
            _ => SystemIcons.Application
        };
        RefreshMenu();
    }
    private void RefreshMenu()
    {
        var available = !_busy && !_exiting;
        _connect.Enabled = available && _profile is not null && _state is ClientState.Offline or ClientState.NeedsAuthentication;
        _stop.Enabled = available && _state is ClientState.Connecting or ClientState.Online or ClientState.Reconnecting;
        _settings.Enabled = available;
        _signin.Enabled = available && _profile?.UseTunnelAuthentication == true;
        _exit.Enabled = !_exiting;
    }
    private async Task ExitAsync()
    {
        if (_exiting || _busy) return;
        _exiting = true; RefreshMenu();
        await StopAsync();
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
        _dispatcher.Dispose();
        ExitThread();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tray.Dispose(); _dispatcher.Dispose(); _cancellation?.Dispose(); }
        base.Dispose(disposing);
    }
}
