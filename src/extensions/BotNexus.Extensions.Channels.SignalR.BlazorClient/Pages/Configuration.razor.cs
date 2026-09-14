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
    /// root schema properties minus <see cref="PlatformConfigFormModel.NonPersistedSections"/>; label from <c>x-ui-label</c>,
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
                .Where(kv => kv.Value is JsonObject && !PlatformConfigFormModel.NonPersistedSections.Contains(kv.Key))
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

    private PlatformConfigFormModel? _form;
    private PlatformConfigFormModel Form => _form ??= new PlatformConfigFormModel(ConfigService);
    private JsonObject? _config => Form.Config;
    private JsonObject? _schema => Form.Schema;
    private bool _loading => Form.IsLoading;
    private bool _saving => Form.IsSaving;
    private bool _dirty => Form.IsDirty;
    private string? _statusMessage;
    private string _statusClass = "";
    private PlatformConfigService.ConfigValidationResult? _validationResult;
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
