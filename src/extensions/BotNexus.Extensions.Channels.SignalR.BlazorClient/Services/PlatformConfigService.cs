using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Client-side service for reading and writing the platform configuration via the REST API.
/// </summary>
public sealed class PlatformConfigService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly HttpClient _http;

    public PlatformConfigService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>Load the full platform config (secrets redacted).</summary>
    public async Task<JsonObject?> LoadAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<JsonObject>("/api/config", s_jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Load a single config section.</summary>
    public async Task<JsonNode?> LoadSectionAsync(string section)
    {
        try
        {
            return await _http.GetFromJsonAsync<JsonNode>($"/api/config/{Uri.EscapeDataString(section)}", s_jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Save (replace) an entire config section.</summary>
    public async Task<(bool Success, string? Error)> SaveSectionAsync(string section, JsonNode value)
    {
        try
        {
            var response = await _http.PutAsJsonAsync(
                $"/api/config/{Uri.EscapeDataString(section)}", value, s_jsonOptions);

            if (response.IsSuccessStatusCode)
                return (true, null);

            var body = await response.Content.ReadAsStringAsync();
            return (false, $"HTTP {(int)response.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Save a single entry within a section (e.g. a single provider).</summary>
    public async Task<(bool Success, string? Error)> SaveSectionEntryAsync(string section, string key, JsonNode value)
    {
        try
        {
            var response = await _http.PutAsJsonAsync(
                $"/api/config/{Uri.EscapeDataString(section)}/{Uri.EscapeDataString(key)}", value, s_jsonOptions);

            if (response.IsSuccessStatusCode)
                return (true, null);

            var body = await response.Content.ReadAsStringAsync();
            return (false, $"HTTP {(int)response.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Delete an entry from a section.</summary>
    public async Task<(bool Success, string? Error)> DeleteSectionEntryAsync(string section, string key)
    {
        try
        {
            var response = await _http.DeleteAsync(
                $"/api/config/{Uri.EscapeDataString(section)}/{Uri.EscapeDataString(key)}");

            if (response.IsSuccessStatusCode)
                return (true, null);

            var body = await response.Content.ReadAsStringAsync();
            return (false, $"HTTP {(int)response.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Validate the config file.</summary>
    public async Task<ConfigValidationResult?> ValidateAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<ConfigValidationResult>("/api/config/validate", s_jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public sealed record ConfigValidationResult
    {
        public bool IsValid { get; init; }
        public string? ConfigPath { get; init; }
        public List<string> Warnings { get; init; } = [];
        public List<string> Errors { get; init; } = [];
    }
}
