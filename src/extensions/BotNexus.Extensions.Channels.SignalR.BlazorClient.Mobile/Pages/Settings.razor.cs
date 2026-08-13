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
    private JsonObject? _config;
    private JsonObject? _schema;
    private string? _revision;
    private readonly ConfigDirtyPathTracker _dirtyPaths = new();
    private bool _loading = true;
    private bool _saving;
    private bool _dirty;
    private string? _statusMessage;
    private string _statusClass = "";
    private System.Timers.Timer? _statusTimer;

    // Top-level keys that are never persisted from the settings UI: metadata and the agents tree
    // (agents are managed through the dedicated agent editor, not the platform config form). Kept in
    // sync with the desktop Configuration page so both surfaces persist the same sections.
    private static readonly HashSet<string> NonPersistedSections =
        new(StringComparer.OrdinalIgnoreCase) { "$schema", "version", "agents" };

    protected override async Task OnInitializedAsync()
    {
        await LoadConfig();
    }

    private async Task LoadConfig()
    {
        _loading = true;
        _dirty = false;
        _dirtyPaths.Reset();
        SetStatus("Loading...", "");
        StateHasChanged();

        _schema = await ConfigService.LoadSchemaAsync();
        _config = await ConfigService.LoadAsync();

        // Load the raw snapshot for its REVISION, not to decide what may be saved (#2059). Mirrors
        // the desktop page exactly: the save set is now what the operator edited, and the revision
        // is what makes that save safe against a concurrent writer.
        var snapshot = await ConfigService.LoadSnapshotAsync();
        _revision = snapshot?.Revision;

        _loading = false;
        if (_config is null || _schema is null)
            SetStatus("Failed to load", "error");
        else
            SetStatus("Loaded", "success", autoHide: true);
        StateHasChanged();
    }

    // SchemaForm edits _config in place and raises this on every change; flip the dirty flag so the
    // Save button enables. We re-render because SchemaForm hands back the same instance reference.
    private void OnConfigChanged(JsonObject updated)
    {
        _config = updated;
        _dirty = true;
        StateHasChanged();
    }

    // Records WHICH path changed (#2059) so the save can patch exactly that and nothing else.
    private void OnPathChanged(string path) => _dirtyPaths.Mark(path);

    private async Task SaveAll()
    {
        if (_config is null || !_dirty) return;
        _saving = true;
        SetStatus("Saving...", "");
        StateHasChanged();

        // One atomic patch of exactly the edited paths, guarded by the loaded revision. Shares the
        // ConfigDirtyPathTracker + PatchAsync seam with the desktop page so the two surfaces cannot
        // drift into two different save semantics again.
        var operations = _dirtyPaths.BuildOperations(_config);
        var outcome = await ConfigService.PatchAsync(operations, _revision);

        _saving = false;
        if (outcome.Success)
        {
            _dirty = false;
            _dirtyPaths.Reset();
            _revision = outcome.Revision;
            SetStatus("Saved successfully", "success", autoHide: true);
            await LoadConfig();
            return;
        }

        if (outcome.IsConflict)
        {
            // Keep the dirty paths: the edits are still unsaved and must not look committed.
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
