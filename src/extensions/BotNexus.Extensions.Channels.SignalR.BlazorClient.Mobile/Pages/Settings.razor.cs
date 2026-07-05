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
    private HashSet<string>? _rawSections;
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
        SetStatus("Loading...", "");
        StateHasChanged();

        _schema = await ConfigService.LoadSchemaAsync();
        _config = await ConfigService.LoadAsync();

        // Load raw config to know which sections actually exist on disk, so we only persist sections
        // the user materialised rather than default-injected ones.
        var raw = await ConfigService.LoadRawAsync();
        _rawSections = raw?.Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

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

    private async Task SaveAll()
    {
        if (_config is null || !_dirty) return;
        _saving = true;
        SetStatus("Saving...", "");
        StateHasChanged();

        // Persist every top-level section that exists on disk (raw) and is user-editable. The schema
        // form mutates the whole tree, so without per-section dirty tracking we save the materialised
        // sections; default-only sections are skipped to avoid writing injected defaults to disk.
        var sectionsToSave = _config
            .Select(kv => kv.Key)
            .Where(key => !NonPersistedSections.Contains(key))
            .Where(key => _rawSections is null || _rawSections.Contains(key))
            .ToList();

        var allOk = true;
        foreach (var section in sectionsToSave)
        {
            var node = _config[section];
            if (node is null) continue;
            var (success, error) = await ConfigService.SaveSectionAsync(section, node.DeepClone());
            if (!success)
            {
                SetStatus($"Failed to save {section}: {error}", "error");
                allOk = false;
                break;
            }
        }

        _saving = false;
        if (allOk)
        {
            _dirty = false;
            SetStatus("Saved successfully", "success", autoHide: true);
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
