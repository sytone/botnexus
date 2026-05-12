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

    /// <summary>List all resolved locations.</summary>
    public async Task<List<LocationItem>?> ListLocationsAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<List<LocationItem>>("/api/locations", s_jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Create a location definition.</summary>
    public async Task<(bool Success, string? Error, LocationItem? Location)> CreateLocationAsync(UpsertLocationRequest request)
    {
        return await SendLocationAsync(HttpMethod.Post, "/api/locations", request);
    }

    /// <summary>Update a location definition.</summary>
    public async Task<(bool Success, string? Error, LocationItem? Location)> UpdateLocationAsync(string name, UpsertLocationRequest request)
    {
        return await SendLocationAsync(HttpMethod.Put, $"/api/locations/{Uri.EscapeDataString(name)}", request);
    }

    /// <summary>Delete a location definition.</summary>
    public async Task<(bool Success, string? Error)> DeleteLocationAsync(string name)
    {
        try
        {
            var response = await _http.DeleteAsync($"/api/locations/{Uri.EscapeDataString(name)}");
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

    private async Task<(bool Success, string? Error, LocationItem? Location)> SendLocationAsync(
        HttpMethod method,
        string url,
        UpsertLocationRequest request)
    {
        try
        {
            using var message = new HttpRequestMessage(method, url)
            {
                Content = JsonContent.Create(request, options: s_jsonOptions)
            };

            using var response = await _http.SendAsync(message);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                return (false, $"HTTP {(int)response.StatusCode}: {body}", null);
            }

            var location = await response.Content.ReadFromJsonAsync<LocationItem>(s_jsonOptions);
            return (true, null, location);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
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

    public sealed record LocationItem
    {
        public string Name { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string? PathOrEndpoint { get; init; }
        public string? Description { get; init; }
        public string Status { get; init; } = "unknown";
        public bool IsUserDefined { get; init; }
    }

    public sealed record UpsertLocationRequest
    {
        public string Name { get; init; } = string.Empty;
        public string Type { get; init; } = "filesystem";
        public string Value { get; init; } = string.Empty;
        public string? Description { get; init; }
    }
}
