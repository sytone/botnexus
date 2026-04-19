using System.Text.Json.Nodes;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;

public partial class Configuration : IDisposable
{
    private JsonObject? _config;
    private bool _loading = true;
    private bool _saving;
    private bool _dirty;
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
        _loading = true;
        _dirty = false;
        SetStatus("Loading…", "");
        StateHasChanged();

        _config = await ConfigService.LoadAsync();
        _loading = false;

        if (_config is null)
            SetStatus("Failed to load", "error");
        else
            SetStatus("Loaded", "success", autoHide: true);

        StateHasChanged();
    }

    private async Task SaveAll()
    {
        if (_config is null) return;

        _saving = true;
        SetStatus("Saving…", "");
        StateHasChanged();

        var sections = _config
            .Where(kv => kv.Key is not "$schema" and not "version" and not "agents")
            .Select(kv => kv.Key)
            .ToList();

        var allOk = true;
        foreach (var section in sections)
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
            SetStatus("✅ Saved successfully", "success", autoHide: true);
        }

        StateHasChanged();
    }

    private async Task Validate()
    {
        SetStatus("Validating…", "");
        StateHasChanged();

        _validationResult = await ConfigService.ValidateAsync();

        if (_validationResult is null)
            SetStatus("Validation request failed", "error");
        else if (_validationResult.IsValid)
            SetStatus("✅ Valid", "success", autoHide: true);
        else
            SetStatus("❌ Errors found", "error");

        StateHasChanged();
    }

    // ── Status bar helpers ──────────────────────────────────────────

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

    private void MarkDirty()
    {
        _dirty = true;
    }

    // ── JSON accessor helpers ───────────────────────────────────────

    private JsonObject? GetObject(string key)
    {
        return _config?[key] as JsonObject;
    }

    private static JsonObject? GetObject(JsonObject? parent, string key)
    {
        return parent?[key] as JsonObject;
    }

    private static string GetStr(JsonObject? obj, string key)
    {
        var node = obj?[key];
        if (node is JsonValue jv)
        {
            var raw = jv.ToString();
            return raw == "***" ? "" : raw;
        }
        return "";
    }

    private static bool GetBool(JsonObject? obj, string key)
    {
        if (obj?[key] is JsonValue jv && jv.TryGetValue<bool>(out var b))
            return b;
        return false;
    }

    private int? GetInt(string key)
    {
        if (_config?[key] is JsonValue jv && jv.TryGetValue<int>(out var i))
            return i;
        return null;
    }

    private static int? GetNullableInt(JsonObject? obj, string key)
    {
        if (obj?[key] is JsonValue jv && jv.TryGetValue<int>(out var i))
            return i;
        return null;
    }

    private static List<string> GetList(JsonObject? obj, string key)
    {
        if (obj?[key] is JsonArray arr)
            return arr.Select(n => n?.ToString() ?? "").ToList();
        return [];
    }

    private static Dictionary<string, JsonObject?> GetDict(JsonObject? obj, string key)
    {
        if (obj?[key] is JsonObject dict)
            return dict.ToDictionary(kv => kv.Key, kv => kv.Value as JsonObject);
        return new();
    }

    private Dictionary<string, JsonObject?> GetDict(string key)
    {
        return GetDict(_config, key);
    }

    // ── Setters (mutate _config in place, mark dirty) ───────────────

    private void SetValue(string key, ChangeEventArgs e)
    {
        if (_config is null) return;
        var val = e.Value?.ToString();
        if (int.TryParse(val, out var intVal))
            _config[key] = intVal;
        else
            _config[key] = val;
        MarkDirty();
    }

    private void SetValue(string key, string? value)
    {
        if (_config is null) return;
        _config[key] = string.IsNullOrEmpty(value) ? null : value;
        MarkDirty();
    }

    private void SetNested(string section, string key, string? value, bool asBool = false)
    {
        EnsureObject(section);
        var obj = _config![section] as JsonObject;
        if (obj is null) return;

        if (asBool)
            obj[key] = bool.TryParse(value, out var b) && b;
        else
            obj[key] = string.IsNullOrEmpty(value) ? null : value;
        MarkDirty();
    }

    private void SetNestedBool(string section, string key, bool value)
    {
        EnsureObject(section);
        var obj = _config![section] as JsonObject;
        if (obj is null) return;
        obj[key] = value;
        MarkDirty();
    }

    private void SetNestedNum(string section, string key, int? value)
    {
        EnsureObject(section);
        var obj = _config![section] as JsonObject;
        if (obj is null) return;
        obj[key] = value.HasValue ? JsonValue.Create(value.Value) : null;
        MarkDirty();
    }

    private void SetDeep(string section, string sub, string key, string? value)
    {
        EnsureObject(section);
        var parent = _config![section] as JsonObject;
        if (parent is null) return;
        EnsureChildObject(parent, sub);
        var child = parent[sub] as JsonObject;
        if (child is null) return;
        child[key] = string.IsNullOrEmpty(value) ? null : value;
        MarkDirty();
    }

    private void SetDeepNum(string section, string sub, string key, int? value)
    {
        EnsureObject(section);
        var parent = _config![section] as JsonObject;
        if (parent is null) return;
        EnsureChildObject(parent, sub);
        var child = parent[sub] as JsonObject;
        if (child is null) return;
        child[key] = value.HasValue ? JsonValue.Create(value.Value) : null;
        MarkDirty();
    }

    private void SetDeepBool(string section, string sub, string key, bool value)
    {
        EnsureObject(section);
        var parent = _config![section] as JsonObject;
        if (parent is null) return;
        EnsureChildObject(parent, sub);
        var child = parent[sub] as JsonObject;
        if (child is null) return;
        child[key] = value;
        MarkDirty();
    }

    private void SetDeepList(string section, string sub, string key, List<string> value)
    {
        EnsureObject(section);
        var parent = _config![section] as JsonObject;
        if (parent is null) return;
        EnsureChildObject(parent, sub);
        var child = parent[sub] as JsonObject;
        if (child is null) return;
        child[key] = value.Count > 0 ? new JsonArray(value.Select(v => JsonValue.Create(v)).ToArray<JsonNode>()) : null;
        MarkDirty();
    }

    // Dictionary field setters for gateway.apiKeys, etc.
    private void SetDictField(string section, string dict, string entryKey, string field, string? value)
    {
        var parent = _config?[section] as JsonObject;
        var dictObj = parent?[dict] as JsonObject;
        var entry = dictObj?[entryKey] as JsonObject;
        if (entry is null) return;
        entry[field] = string.IsNullOrEmpty(value) ? null : value;
        MarkDirty();
    }

    private void SetDictBoolField(string section, string dict, string entryKey, string field, bool value)
    {
        var parent = _config?[section] as JsonObject;
        var dictObj = parent?[dict] as JsonObject;
        var entry = dictObj?[entryKey] as JsonObject;
        if (entry is null) return;
        entry[field] = value;
        MarkDirty();
    }

    private void SetDictListField(string section, string dict, string entryKey, string field, List<string> value)
    {
        var parent = _config?[section] as JsonObject;
        var dictObj = parent?[dict] as JsonObject;
        var entry = dictObj?[entryKey] as JsonObject;
        if (entry is null) return;
        entry[field] = value.Count > 0 ? new JsonArray(value.Select(v => JsonValue.Create(v)).ToArray<JsonNode>()) : null;
        MarkDirty();
    }

    // Top-level dictionary setters (providers, channels)
    private void SetTopDictField(string section, string entryKey, string field, string? value)
    {
        var dict = _config?[section] as JsonObject;
        var entry = dict?[entryKey] as JsonObject;
        if (entry is null) return;
        entry[field] = string.IsNullOrEmpty(value) ? null : value;
        MarkDirty();
    }

    private void SetTopDictBool(string section, string entryKey, string field, bool value)
    {
        var dict = _config?[section] as JsonObject;
        var entry = dict?[entryKey] as JsonObject;
        if (entry is null) return;
        entry[field] = value;
        MarkDirty();
    }

    private void SetTopDictList(string section, string entryKey, string field, List<string> value)
    {
        var dict = _config?[section] as JsonObject;
        var entry = dict?[entryKey] as JsonObject;
        if (entry is null) return;
        entry[field] = value.Count > 0 ? new JsonArray(value.Select(v => JsonValue.Create(v)).ToArray<JsonNode>()) : null;
        MarkDirty();
    }

    private void SetTopDictDeep(string section, string entryKey, string sub, string field, string? value)
    {
        var dict = _config?[section] as JsonObject;
        var entry = dict?[entryKey] as JsonObject;
        if (entry is null) return;
        EnsureChildObject(entry, sub);
        var child = entry[sub] as JsonObject;
        if (child is null) return;
        child[field] = string.IsNullOrEmpty(value) ? null : value;
        MarkDirty();
    }

    private void SetCronJobField(string jobKey, string field, string? value)
    {
        var cron = _config?["cron"] as JsonObject;
        var jobs = cron?["jobs"] as JsonObject;
        var job = jobs?[jobKey] as JsonObject;
        if (job is null) return;
        job[field] = string.IsNullOrEmpty(value) ? null : value;
        MarkDirty();
    }

    private void SetCronJobBool(string jobKey, string field, bool value)
    {
        var cron = _config?["cron"] as JsonObject;
        var jobs = cron?["jobs"] as JsonObject;
        var job = jobs?[jobKey] as JsonObject;
        if (job is null) return;
        job[field] = value;
        MarkDirty();
    }

    // ── Add / Delete dictionary entries ─────────────────────────────

    private void AddTopDictEntry(string section, string key)
    {
        if (_config is null || string.IsNullOrWhiteSpace(key)) return;
        if (_config[section] is not JsonObject dict)
        {
            dict = new JsonObject();
            _config[section] = dict;
        }
        dict[key] = new JsonObject();
        MarkDirty();
        StateHasChanged();
    }

    private async Task DeleteTopDictEntry(string section, string key)
    {
        var (success, _) = await ConfigService.DeleteSectionEntryAsync(section, key);
        if (success)
            await LoadConfig();
    }

    private void AddNestedDictEntry(string section, string dict, string key)
    {
        if (_config is null || string.IsNullOrWhiteSpace(key)) return;
        EnsureObject(section);
        var parent = _config[section] as JsonObject;
        if (parent is null) return;
        if (parent[dict] is not JsonObject dictObj)
        {
            dictObj = new JsonObject();
            parent[dict] = dictObj;
        }
        dictObj[key] = new JsonObject();
        MarkDirty();
        StateHasChanged();
    }

    private void DeleteNestedDictEntry(string section, string dict, string key)
    {
        var parent = _config?[section] as JsonObject;
        var dictObj = parent?[dict] as JsonObject;
        if (dictObj is null) return;
        dictObj.Remove(key);
        MarkDirty();
        StateHasChanged();
    }

    // ── Ensure helpers ──────────────────────────────────────────────

    private void EnsureObject(string key)
    {
        if (_config is null) return;
        if (_config[key] is not JsonObject)
            _config[key] = new JsonObject();
    }

    private static void EnsureChildObject(JsonObject parent, string key)
    {
        if (parent[key] is not JsonObject)
            parent[key] = new JsonObject();
    }

    public void Dispose()
    {
        _statusTimer?.Stop();
        _statusTimer?.Dispose();
    }
}
