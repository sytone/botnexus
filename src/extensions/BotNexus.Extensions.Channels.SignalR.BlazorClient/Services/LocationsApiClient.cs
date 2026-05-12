using System.Net.Http.Json;
using System.Text.Json;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Client for the gateway locations REST API (<c>/api/locations</c>).
/// Supports listing, creating, updating, deleting, and health-checking
/// named locations at runtime without requiring a platform restart.
/// </summary>
public sealed class LocationsApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public LocationsApiClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>Lists all resolved locations from the gateway.</summary>
    public async Task<(IReadOnlyList<LocationDto> Locations, string? Error)> ListAsync()
    {
        try
        {
            var result = await _http.GetFromJsonAsync<List<LocationDto>>("/api/locations", JsonOptions);
            return (result as IReadOnlyList<LocationDto> ?? [], null);
        }
        catch (Exception ex)
        {
            return ([], ex.Message);
        }
    }

    /// <summary>Creates a new user-defined location.</summary>
    public async Task<(LocationDto? Location, string? Error)> CreateAsync(UpsertLocationDto request)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/api/locations", request, JsonOptions);
            if (response.IsSuccessStatusCode)
            {
                var dto = await response.Content.ReadFromJsonAsync<LocationDto>(JsonOptions);
                return (dto, null);
            }

            var error = await ReadErrorAsync(response);
            return (null, error);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Updates an existing user-defined location.</summary>
    public async Task<(LocationDto? Location, string? Error)> UpdateAsync(string name, UpsertLocationDto request)
    {
        try
        {
            var response = await _http.PutAsJsonAsync(
                $"/api/locations/{Uri.EscapeDataString(name)}", request, JsonOptions);
            if (response.IsSuccessStatusCode)
            {
                var dto = await response.Content.ReadFromJsonAsync<LocationDto>(JsonOptions);
                return (dto, null);
            }

            var error = await ReadErrorAsync(response);
            return (null, error);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Deletes a user-defined location by name.</summary>
    public async Task<(bool Success, string? Error)> DeleteAsync(string name)
    {
        try
        {
            var response = await _http.DeleteAsync($"/api/locations/{Uri.EscapeDataString(name)}");
            if (response.IsSuccessStatusCode)
                return (true, null);

            var error = await ReadErrorAsync(response);
            return (false, error);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Runs a health check for a single location.</summary>
    public async Task<(LocationHealthDto? Result, string? Error)> CheckHealthAsync(string name)
    {
        try
        {
            var response = await _http.PostAsync(
                $"/api/locations/{Uri.EscapeDataString(name)}/check", null);
            if (response.IsSuccessStatusCode)
            {
                var dto = await response.Content.ReadFromJsonAsync<LocationHealthDto>(JsonOptions);
                return (dto, null);
            }

            var error = await ReadErrorAsync(response);
            return (null, error);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var errorProp))
                    return errorProp.GetString() ?? body;
            }
            return $"HTTP {(int)response.StatusCode}";
        }
        catch
        {
            return $"HTTP {(int)response.StatusCode}";
        }
    }
}

/// <summary>Location data returned by the locations API.</summary>
public sealed class LocationDto
{
    /// <summary>The location name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The location type (filesystem, api, mcp-server, database, remote-node).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The path or endpoint value.</summary>
    public string? PathOrEndpoint { get; set; }

    /// <summary>Optional description.</summary>
    public string? Description { get; set; }

    /// <summary>The current health status.</summary>
    public string Status { get; set; } = "unknown";

    /// <summary>Whether this location is user-defined in config.</summary>
    public bool IsUserDefined { get; set; }
}

/// <summary>Request payload for creating or updating a location.</summary>
public sealed class UpsertLocationDto
{
    /// <summary>The location name (required for create).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The location type.</summary>
    public string Type { get; set; } = "filesystem";

    /// <summary>The path, endpoint, or connection string value.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Optional location description.</summary>
    public string? Description { get; set; }
}

/// <summary>Health check response for a single location.</summary>
public sealed class LocationHealthDto
{
    /// <summary>The location name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The health status result.</summary>
    public string Status { get; set; } = "unknown";

    /// <summary>Additional status details.</summary>
    public string Message { get; set; } = string.Empty;
}
