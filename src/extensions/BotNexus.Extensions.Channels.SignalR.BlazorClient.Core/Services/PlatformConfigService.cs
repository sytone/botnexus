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

    /// <summary>Load the effective platform config with defaults applied (secrets redacted).</summary>
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

    /// <summary>Load the raw platform config from disk (secrets redacted, no defaults applied).</summary>
    public async Task<JsonObject?> LoadRawAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<JsonObject>("/api/config/raw", s_jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Load the read-only UI schema for the whole platform config tree from
    /// <c>GET /api/config/schema</c>. The returned envelope drives the schema-driven settings UI
    /// (the generic SchemaForm renderer) so no hand-written config panels are needed; returns null
    /// when the request fails so the page can show a fallback state.
    /// </summary>
    public async Task<JsonObject?> LoadSchemaAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<JsonObject>("/api/config/schema", s_jsonOptions);
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

    /// <summary>
    /// Load the raw platform config together with the revision token it was read at (#2059).
    /// </summary>
    /// <remarks>
    /// The settings page saves with <see cref="PatchAsync"/> quoting this revision, so a save built
    /// on a snapshot another writer has since superseded is rejected as a conflict instead of
    /// silently overwriting them.
    /// </remarks>
    public async Task<ConfigSnapshot?> LoadSnapshotAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<ConfigSnapshot>("/api/config/snapshot", s_jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Save a batch of addressed config changes as one atomic, optimistically-concurrent write
    /// (#2059).
    /// </summary>
    /// <remarks>
    /// Replaces the per-section save loop the settings pages used to run. Only the edited paths are
    /// sent, so a section the operator never touched is not part of the write and cannot be
    /// clobbered; a stale revision comes back as <see cref="ConfigPatchOutcome.IsConflict"/> so the
    /// page can reload rather than overwrite.
    /// </remarks>
    public async Task<ConfigPatchOutcome> PatchAsync(
        IReadOnlyList<ConfigPatchOperationDto> operations,
        string? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(operations);

        if (operations.Count == 0)
            return new ConfigPatchOutcome(true, false, expectedRevision, null);

        try
        {
            var response = await _http.PatchAsJsonAsync(
                "/api/config",
                new ConfigPatchRequestDto(operations, expectedRevision),
                s_jsonOptions);

            var payload = await ReadPatchResponseAsync(response);

            if (response.IsSuccessStatusCode)
                return new ConfigPatchOutcome(true, false, payload?.Revision, null);

            var message = payload?.Errors is { Count: > 0 }
                ? string.Join("; ", payload.Errors)
                : $"HTTP {(int)response.StatusCode}";

            return new ConfigPatchOutcome(
                false,
                response.StatusCode == System.Net.HttpStatusCode.Conflict,
                payload?.Revision,
                message);
        }
        catch (Exception ex)
        {
            return new ConfigPatchOutcome(false, false, null, ex.Message);
        }
    }

    private static async Task<ConfigPatchResponseDto?> ReadPatchResponseAsync(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ConfigPatchResponseDto>(s_jsonOptions);
        }
        catch
        {
            // A non-JSON error body (proxy page, plain-text 500) must not mask the status code.
            return null;
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

    /// <summary>A raw config document plus the revision it was read at (#2059).</summary>
    /// <param name="Revision">Compare-and-swap token to quote on the next save.</param>
    /// <param name="Config">The raw config document, secrets redacted.</param>
    public sealed record ConfigSnapshot(string Revision, JsonObject Config);

    /// <summary>Result of an attempted config patch (#2059).</summary>
    /// <param name="Success">Whether the batch committed.</param>
    /// <param name="IsConflict">Whether the save was rejected because the revision was stale.</param>
    /// <param name="Revision">The revision now on disk, when the server reported one.</param>
    /// <param name="Error">Presentable failure message; null on success.</param>
    public sealed record ConfigPatchOutcome(bool Success, bool IsConflict, string? Revision, string? Error);

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
        public bool HasConfiguredSecret { get; init; }
    }

    public sealed record UpsertLocationRequest
    {
        public string Name { get; init; } = string.Empty;
        public string Type { get; init; } = "filesystem";
        public string Value { get; init; } = string.Empty;
        public string? Description { get; init; }
    }
}
