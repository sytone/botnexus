using System.Text.Json.Nodes;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;

/// <summary>
/// Schema-driven mobile platform-settings page (config-parity PBI 6/6 of #1579, issue #1615 -- the
/// payoff PBI). It consumes the SAME shared <c>SchemaForm</c> renderer the desktop Configuration page
/// uses, fed by <c>GET /api/config/schema</c>: it fetches the UI schema and the effective config once,
/// binds them into the form, and persists the sections the user actually edited. There is no
/// mobile-specific field code, so a config field added to <c>PlatformConfig</c> with annotations
/// surfaces here automatically. Save behaviour matches the desktop -- edits go through the same
/// per-section <c>PUT /api/config/{section}</c> endpoints, so hot-reload-without-restart is unchanged.
/// The logic mirrors the desktop <c>Configuration</c> code-behind so the two surfaces stay in lockstep.
/// </summary>
public partial class Settings : IDisposable
{
    private PlatformConfigFormModel? _form;
    private PlatformConfigFormModel Form => _form ??= new PlatformConfigFormModel(ConfigService);
    private JsonObject? _config => Form.Config;
    private JsonObject? _schema => Form.Schema;
    private bool _loading => Form.IsLoading;
    private bool _saving => Form.IsSaving;
    private bool _dirty => Form.IsDirty;
    private string? _statusMessage;
    private string _statusClass = "";
    private System.Timers.Timer? _statusTimer;

    protected override async Task OnInitializedAsync()
    {
        await LoadConfig();
    }

    private async Task LoadConfig()
    {
        SetStatus("Loading...", "");
        StateHasChanged();
        await Form.LoadAsync();

        if (_config is null || _schema is null)
            SetStatus("Failed to load", "error");
        else
            SetStatus("Loaded", "success", autoHide: true);
        StateHasChanged();
    }

    private void OnConfigChanged(JsonObject updated)
    {
        Form.MarkConfigChanged(updated);
        StateHasChanged();
    }

    private void OnPathChanged(string path) => Form.MarkPathChanged(path);

    private async Task SaveAll()
    {
        if (!_dirty) return;
        SetStatus("Saving...", "");
        StateHasChanged();

        var outcome = await Form.SaveAsync();
        if (outcome.Success)
        {
            SetStatus("Saved successfully", "success", autoHide: true);
            await LoadConfig();
            return;
        }

        if (outcome.IsConflict)
        {
            SetStatus(
                "Configuration changed elsewhere since this page loaded. Reload to see the current values, then re-apply your changes.",
                "error");
        }
        else
        {
            SetStatus($"Failed to save: {outcome.Error}", "error");
        }

        StateHasChanged();
    }

    // Return to the chat surface. The mobile client's root route ("/") is the Chat page, so a plain
    // navigation back there is the natural "close settings" affordance on a single-pane phone UI.
    private void GoBack() => Nav.NavigateTo("/");

    private void SetStatus(string message, string cssClass, bool autoHide = false)
    {
        _statusMessage = message;
        _statusClass = cssClass;
        _statusTimer?.Stop();
        _statusTimer?.Dispose();
        if (autoHide)
        {
            _statusTimer = new System.Timers.Timer(3000);
            _statusTimer.Elapsed += (_, _) =>
            {
                _statusMessage = null;
                _statusClass = "";
                InvokeAsync(StateHasChanged);
            };
            _statusTimer.AutoReset = false;
            _statusTimer.Start();
        }
    }

    public void Dispose()
    {
        _statusTimer?.Stop();
        _statusTimer?.Dispose();
    }
}
