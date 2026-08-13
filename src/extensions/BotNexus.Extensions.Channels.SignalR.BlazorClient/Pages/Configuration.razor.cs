using System.Text.Json.Nodes;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;

/// <summary>
/// Schema-driven platform configuration page (config-parity PBI 4/6 of #1579, issue #1612). The
/// eight hand-written config panels were replaced by the generic <c>SchemaForm</c> renderer fed by
/// <c>GET /api/config/schema</c>: the page fetches the UI schema and the effective config once, binds
/// them into the form, and persists the sections the user actually edited. Hot-reload behaviour is
/// unchanged -- saves go through the same per-section PUT endpoints the panels used.
/// </summary>
public partial class Configuration : IDisposable
{
    /// <summary>
    /// Config section from the route (e.g. <c>/configuration/providers</c>). Selects which root
    /// section the sidebar highlights and which subtree <c>SchemaForm</c> renders (#1892). Null or
    /// an unknown key falls back to the first section.
    /// </summary>
    [Parameter] public string? Section { get; set; }

    [Inject] private NavigationManager Nav { get; set; } = default!;

    /// <summary>
    /// Ordered, user-editable top-level sections for the sidebar (key + label). Derived from the
    /// root schema properties minus <see cref="NonPersistedSections"/>; label from <c>x-ui-label</c>,
    /// ordered by <c>x-ui-order</c>.
    /// </summary>
    private IReadOnlyList<(string Key, string Label)> Sections
    {
        get
        {
            var props = _schema?["schema"]?["properties"]?.AsObject();
            if (props is null)
                return [];
            return props
                .Where(kv => kv.Value is JsonObject && !NonPersistedSections.Contains(kv.Key))
                .Select(kv => (kv.Key, Node: kv.Value!.AsObject()))
                .OrderBy(x => x.Node["x-ui-order"]?.GetValue<int>() ?? int.MaxValue)
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => (x.Key, Label: x.Node["x-ui-label"]?.GetValue<string>() ?? x.Key))
                .ToList();
        }
    }

    /// <summary>
    /// The section currently shown: the route <see cref="Section"/> when it matches a known section,
    /// otherwise the first section. Empty when the schema has not loaded yet.
    /// </summary>
    private string ActiveSection
    {
        get
        {
            var sections = Sections;
            if (sections.Count == 0)
                return string.Empty;
            if (!string.IsNullOrEmpty(Section) &&
                sections.Any(s => string.Equals(s.Key, Section, StringComparison.OrdinalIgnoreCase)))
                return sections.First(s => string.Equals(s.Key, Section, StringComparison.OrdinalIgnoreCase)).Key;
            return sections[0].Key;
        }
    }

    private void SelectSection(string key)
    {
        Section = key;
        Nav.NavigateTo($"/configuration/{key}");
    }

    private JsonObject? _config;
    private JsonObject? _schema;
    private string? _revision;
    private readonly ConfigDirtyPathTracker _dirtyPaths = new();
    private bool _loading = true;
    private bool _saving;
    private bool _dirty;
    private string? _statusMessage;
    private string _statusClass = "";
    private PlatformConfigService.ConfigValidationResult? _validationResult;
    private System.Timers.Timer? _statusTimer;

    // Top-level keys that are never persisted from the settings UI: metadata and the agents tree
    // (agents are managed through the dedicated agent editor, not the platform config form).
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

        // Load the raw snapshot for its REVISION, not to decide what may be saved (#2059). The
        // previous code used the raw document's top-level keys as a save filter, which is why a
        // section absent from disk could never be materialised by editing its defaults. Saving is
        // now driven by what the operator edited; the revision is what makes that save safe.
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

        // Patch only the edited paths, quoting the revision the form was rendered from. A section
        // nobody touched is not in the batch and so cannot be reverted to a stale value; the whole
        // batch commits or none of it does; and a save built on a superseded snapshot comes back as
        // a conflict instead of silently winning.
        var operations = _dirtyPaths.BuildOperations(_config);
        var outcome = await ConfigService.PatchAsync(operations, _revision);

        _saving = false;
        if (outcome.Success)
        {
            _dirty = false;
            _dirtyPaths.Reset();
            _revision = outcome.Revision;
            SetStatus("Saved successfully", "success", autoHide: true);
            // Re-read raw and effective config after commit so the form shows what is actually on
            // disk (defaults materialised, secrets re-redacted) rather than the local edit buffer.
            await LoadConfig();
            return;
        }

        if (outcome.IsConflict)
        {
            // Do NOT clear the dirty paths: the operator's edits are still unsaved and must not be
            // presented as committed. Reloading here would discard them silently.
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

    private async Task Validate()
    {
        SetStatus("Validating...", "");
        StateHasChanged();

        _validationResult = await ConfigService.ValidateAsync();
        if (_validationResult is null)
            SetStatus("Validation request failed", "error");
        else if (_validationResult.IsValid)
            SetStatus("Valid", "success", autoHide: true);
        else
            SetStatus("Errors found", "error");
        StateHasChanged();
    }

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
