using BotNexus.Satellite.Client.SignalR;
namespace BotNexus.Satellite.Client.Windows;

internal sealed class SettingsDialog : Form
{
    private readonly TextBox _endpoint = new() { Dock = DockStyle.Fill };
    private readonly TextBox _identifier = new() { Dock = DockStyle.Fill };
    private readonly TextBox _key = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TextBox _token = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly NumericUpDown _heartbeat = new() { Dock = DockStyle.Fill, Minimum = 1, Maximum = int.MaxValue, Value = 30 };
    private readonly NumericUpDown _stale = new() { Dock = DockStyle.Fill, Minimum = 1, Maximum = int.MaxValue, Value = 120 };
    private readonly CheckBox _launch = new() { Text = "Connect when the client starts", AutoSize = true };
    private readonly CheckBox _loopback = new() { Text = "Allow HTTP for exact loopback hosts only", AutoSize = true };
    private readonly CheckBox _tunnel = new() { Text = "Use devtunnels token authentication", AutoSize = true };
    private readonly CheckBox _delete = new() { Text = "Delete credentials for the selected profile", AutoSize = true };
    public ClientProfile? Profile { get; private set; }

    public SettingsDialog(ClientProfile? current)
    {
        Text = "BotNexus satellite settings";
        Size = new Size(610, 570);
        MinimumSize = Size;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(15), ColumnCount = 2, RowCount = 13 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(layout, 0, "Gateway origin", _endpoint);
        AddRow(layout, 1, "Satellite identifier", _identifier);
        AddRow(layout, 2, "Gateway API key", _key);
        AddRow(layout, 3, "Tunnel access token", _token);
        AddRow(layout, 11, "Heartbeat seconds", _heartbeat);
        AddRow(layout, 12, "Gateway stale seconds", _stale);
        var note = new Label { Text = "Blank secret fields keep existing credentials for this endpoint and identifier. Changing either selects a separate credential profile. The API key—not this local identifier—determines gateway identity.", AutoSize = true, MaximumSize = new Size(540, 0) };
        layout.Controls.Add(note, 0, 4); layout.SetColumnSpan(note, 2);
        foreach (var (control, row) in new[] { (_launch, 5), (_loopback, 6), (_tunnel, 7), (_delete, 8) })
        { layout.Controls.Add(control, 0, row); layout.SetColumnSpan(control, 2); }
        var auth = new Label { Text = "Browser sign-in alone does not authenticate this app. Enter a non-browser devtunnels client access token above. Only one local tray process runs; on other machines the gateway's last connection wins.", AutoSize = true, MaximumSize = new Size(540, 0) };
        layout.Controls.Add(auth, 0, 9); layout.SetColumnSpan(auth, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var save = new Button { Text = "Save" };
        save.Click += (_, _) => Save();
        buttons.Controls.Add(cancel); buttons.Controls.Add(save);
        layout.Controls.Add(buttons, 0, 10); layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = save; CancelButton = cancel;
        _endpoint.Text = current?.GatewayEndpoint ?? "";
        _identifier.Text = current?.SatelliteIdentifier ?? "";
        _launch.Checked = current?.ConnectOnLaunch ?? true;
        _loopback.Checked = current?.AllowLoopbackHttp ?? false;
        _tunnel.Checked = current?.UseTunnelAuthentication ?? false;
        _heartbeat.Value = current?.HeartbeatIntervalSeconds ?? 30;
        _stale.Value = current?.StaleTimeoutSeconds ?? 120;
    }

    private static void AddRow(TableLayoutPanel layout, int row, string label, Control control)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(control, 1, row);
    }
    private void Save()
    {
        try
        {
            var profile = new ClientProfile { GatewayEndpoint = _endpoint.Text.Trim(), SatelliteIdentifier = _identifier.Text.Trim(), ConnectOnLaunch = _launch.Checked, AllowLoopbackHttp = _loopback.Checked, UseTunnelAuthentication = _tunnel.Checked, HeartbeatIntervalSeconds = (int)_heartbeat.Value, StaleTimeoutSeconds = (int)_stale.Value };
            profile.Validate();
            var store = new WindowsCredentialStore(profile);
            if (_delete.Checked) store.Delete();
            else store.Save(_key.Text.Length == 0 ? null : _key.Text, _token.Text.Length == 0 ? null : _token.Text);
            new SettingsStore().Save(profile);
            Profile = profile;
            _key.Clear(); _token.Clear();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (ArgumentException error) { MessageBox.Show(this, error.Message, "Check settings", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        catch (Exception) { MessageBox.Show(this, "Settings or Windows credentials could not be saved. No plaintext credential fallback is used.", "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _key.Clear(); _token.Clear(); }
        base.Dispose(disposing);
    }
}
